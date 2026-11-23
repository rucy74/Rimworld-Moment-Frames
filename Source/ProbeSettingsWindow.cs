using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Verse;
using HarmonyLib;
using RimWorld;
using UnityEngine.TextCore.LowLevel;

namespace MomentFrames
{
    /// <summary>Menu-only native UI receipts, guarded by a fresh disposable settings profile.
    /// This flag does not invoke Probe's quick-test world or open any save.</summary>
    [StaticConstructorOnStartup]
    internal static class ProbeSettingsWindow
    {
        private static string output;
        private static readonly List<string> errors = new List<string>();
        private static readonly List<string> checks = new List<string>();
        private static readonly List<string> receipts = new List<string>();
        private static readonly HashSet<string> reached = new HashSet<string>();
        private static SettingsProbeWindow window;
        private static bool active;
        private static float startDeadline;

        static ProbeSettingsWindow()
        {
            if (GenCommandLine.CommandLineArgPassed("momentFramesSettingsLayoutProbe")) return;
            if (!GenCommandLine.TryGetCommandLineArg("momentFramesSettingsProbe", out output)) return;
            if (GenCommandLine.TryGetCommandLineArg("momentFramesProbe", out _))
            { Log.Error("[MomentFrames settings probe] gameplay Probe must not be combined with this menu-only flag."); return; }
            if (!Path.IsPathRooted(output) || !GenCommandLine.TryGetCommandLineArg("savedatafolder", out var profile)
                || !Path.IsPathRooted(profile) || !File.Exists(Path.Combine(profile, "MOMENTFRAMES_SETTINGS_DISPOSABLE")))
            { Log.Error("[MomentFrames settings probe] an absolute output and marked disposable settings profile are required."); return; }
            Directory.CreateDirectory(output); active = true;
            Application.runInBackground = true; Prefs.RunInBackground = true;
            Application.logMessageReceived += NoteLog;
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                // Def loading finishes before Root_Entry creates its UIRoot/WindowStack.
                // A frame driver waits for that real menu, rather than drawing inside the def-loading callback.
                startDeadline = Time.realtimeSinceStartup + 180f;
                var driver = new GameObject("MomentFrames Settings Menu Probe") { hideFlags = HideFlags.HideAndDontSave };
                driver.AddComponent<SettingsProbeMenuDriver>();
            });
        }
        internal static void MenuTick()
        {
            if (!active || window != null) return;
            try
            {
                if (Time.realtimeSinceStartup > startDeadline) throw new TimeoutException("settings native menu did not initialize");
                if (Current.ProgramState != ProgramState.Entry || Current.Root_Entry == null || Find.UIRoot == null
                    || Find.WindowStack == null || LongEventHandler.ShouldWaitForEvent) return;
                window = new SettingsProbeWindow(); Find.WindowStack.Add(window);
            }
            catch (Exception e) { Complete(e); }
        }
        private static void NoteLog(string condition, string stackTrace, LogType type)
        {
            if (!active || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            errors.Add(type + ": " + condition + "\n" + stackTrace);
        }
        private static string Json(string text)
        {
            if (text == null) return "null";
            return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }
        private static void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException(text);
            string line = "PASS: " + text;
            if (!checks.Contains(line)) checks.Add(line);
        }
        private static void Complete(Exception failure = null)
        {
            if (!active) return;
            if (failure != null) errors.Add("probe assertion: " + failure);
            bool ok = errors.Count == 0;
            string body = "{\"ok\":" + (ok ? "true" : "false") + ",\"language\":" + Json(LanguageDatabase.activeLanguage?.folderName)
                + ",\"programState\":" + Json(Current.ProgramState.ToString()) + ",\"gameCreated\":" + (Current.Game != null ? "true" : "false")
                + ",\"settingsUsed\":\"new MomentFramesSettings fixture, never assigned to the live mod Settings singleton\""
                + ",\"checks\":[" + string.Join(",", checks.Select(Json)) + "],\"errors\":[" + string.Join(",", errors.Select(Json))
                + "],\"drawnSettingAttempts\":[" + string.Join(",", reached.OrderBy(s => s).Select(Json)) + "],\"screens\":["
                + string.Join(",", receipts) + "],\"limits\":\"Menu renderer at native Unity OnGUI. Entry attempts include rows outside the scroll viewport; screenshot visual acceptance is separate. No save, world, full modpack or natural gameplay tested.\"}";
            File.WriteAllText(Path.Combine(output, "settings-native-result.json"), body, new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(output, "settings-native-errors.txt"), errors, new UTF8Encoding(false));
            active = false; Application.logMessageReceived -= NoteLog;
            Application.Quit();
        }
        private sealed class State
        {
            internal string Name, Category, Search = "", Preview;
            internal bool Expanded, Auto = true, Narrow;
            internal float Scroll;
        }
        private sealed class SettingsProbeWindow : Window
        {
            private readonly SettingsWindow renderer = new SettingsWindow();
            private readonly MomentFramesSettings settings = new MomentFramesSettings
                { fade = true, birthdayMode = Birthday.ModeAsk, pxPerCell = 66, eventZoom = 1.06f };
            private readonly State[] states;
            private int index, repaints;
            private float changedAt, capturedAt, deadline;
            private bool captured;
            private string expectedSettings;
            private string originalLiveSettings;
            protected override float Margin => 0f;
            public override Vector2 InitialSize => new Vector2(UI.screenWidth - 32f, UI.screenHeight - 32f);
            internal SettingsProbeWindow()
            {
                doCloseX = false; doCloseButton = false; doWindowBackground = false;
                closeOnCancel = closeOnAccept = false; absorbInputAroundWindow = true; forcePause = false;
                layer = WindowLayer.Super;
                string birthdaySearch = LanguageDatabase.activeLanguage?.folderName?.StartsWith("Korean") == true ? "생일" : "birthday";
                states = new[]
                {
                    new State { Name = "01-capture", Category = "capture" },
                    new State { Name = "02-photo", Category = "photo" },
                    new State { Name = "03-appearance", Category = "appearance", Expanded = true },
                    new State { Name = "04-memories", Category = "memories" },
                    new State { Name = "05-storage", Category = "storage", Expanded = true },
                    new State { Name = "06-capture-expanded", Category = "capture", Expanded = true },
                    new State { Name = "07-capture-expanded-bottom", Category = "capture", Expanded = true, Scroll = 100000f },
                    new State { Name = "08-photo-expanded", Category = "photo", Expanded = true },
                    new State { Name = "09-photo-expanded-bottom", Category = "photo", Expanded = true, Scroll = 100000f },
                    new State { Name = "10-auto-off", Category = "capture", Expanded = true, Auto = false },
                    new State { Name = "11-search-birthday", Category = "capture", Search = birthdaySearch, Expanded = true },
                    new State { Name = "12-search-empty", Category = "storage", Search = "zzzz-no-native-match-7842" },
                    new State { Name = "13-preview-framing", Category = "photo", Expanded = true, Preview = "framing" },
                    new State { Name = "14-narrow", Category = "appearance", Narrow = true, Expanded = true },
                    new State { Name = "15-narrow-capture-expanded", Category = "capture", Narrow = true, Expanded = true },
                    new State { Name = "16-memories-bottom", Category = "memories", Expanded = true, Scroll = 100000f },
                };
                deadline = Time.realtimeSinceStartup + 240f;
            }
            public override void PostOpen()
            {
                base.PostOpen();
                Check(Current.ProgramState == ProgramState.Entry && Current.Game == null, "native settings probe starts at the menu with no game/save");
                originalLiveSettings = SettingsFact(MomentFramesMod.Settings);
                Check(settings.pxPerCell == 66 && Mathf.Abs(settings.eventZoom - 1.06f) < .000001f,
                    "native fixture keeps legacy non-step values before passive range drawing");
                ApplyState();
            }
            private static string SettingsFact(MomentFramesSettings value) => string.Join("|", typeof(MomentFramesSettings).GetFields(BindingFlags.Instance | BindingFlags.Public)
                .OrderBy(f => f.Name).Select(f => f.Name + "=" + (f.GetValue(value)?.ToString() ?? "null")));
            private void ApplyState()
            {
                var state = states[index];
                windowRect = new Rect((UI.screenWidth - (state.Narrow ? 620f : UI.screenWidth - 32f)) / 2f, 16f,
                    state.Narrow ? Mathf.Min(620f, UI.screenWidth - 32f) : UI.screenWidth - 32f, UI.screenHeight - 32f);
                settings.autoCaptureEnabled = state.Auto;
                renderer.SelectCategory(state.Category); renderer.SetSearch(state.Search); renderer.SetExpanded(state.Expanded);
                renderer.SetScroll(state.Scroll);
                if (state.Preview != null) renderer.SetPreview(state.Preview);
                expectedSettings = SettingsFact(settings); repaints = 0; captured = false;
                changedAt = Time.realtimeSinceStartup;
            }
            public override void DoWindowContents(Rect inRect)
            {
                if (!active) return;
                Color beforeColor = GUI.color; bool beforeEnabled = GUI.enabled; Matrix4x4 beforeMatrix = GUI.matrix;
                var beforeFont = Text.Font; var beforeAnchor = Text.Anchor; bool beforeWrap = Text.WordWrap;
                try
                {
                    renderer.Draw(inRect, settings);
                    Check(GUI.color == beforeColor && GUI.enabled == beforeEnabled && GUI.matrix == beforeMatrix
                        && Text.Font == beforeFont && Text.Anchor == beforeAnchor && Text.WordWrap == beforeWrap,
                        "native renderer restores GUI/Text state for " + states[index].Name + " " + Event.current.type);
                    foreach (string setting in renderer.DrawnSettings) reached.Add(setting);
                    if (Event.current.type != EventType.Repaint) return;
                    repaints++;
                    if (captured || repaints < 3 || Time.realtimeSinceStartup - changedAt < .6f) return;
                    Check(SettingsFact(settings) == expectedSettings && settings.pxPerCell == 66 && Mathf.Abs(settings.eventZoom - 1.06f) < .000001f,
                        "passive native drawing preserves all settings and legacy non-step values for " + states[index].Name);
                    Check(SettingsFact(MomentFramesMod.Settings) == originalLiveSettings, "native fixture does not alter the live mod settings singleton");
                    Check(Current.ProgramState == ProgramState.Entry && Current.Game == null, "native " + states[index].Name + " remains menu-only");
                    ScreenCapture.CaptureScreenshot(Path.Combine(output, states[index].Name + ".png"));
                    captured = true; capturedAt = Time.realtimeSinceStartup;
                    File.WriteAllText(Path.Combine(output, "progress.txt"), (index + 1) + "/" + states.Length + " " + states[index].Name);
                }
                catch (Exception e) { Complete(e); }
                finally
                {
                    GUI.color = beforeColor; GUI.enabled = beforeEnabled; GUI.matrix = beforeMatrix;
                    Text.Font = beforeFont; Text.Anchor = beforeAnchor; Text.WordWrap = beforeWrap;
                }
            }
            public override void WindowUpdate()
            {
                base.WindowUpdate();
                if (!active) return;
                try
                {
                    if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("settings native phase " + index);
                    if (!captured || Time.realtimeSinceStartup - capturedAt < .7f) return;
                    string path = Path.Combine(output, states[index].Name + ".png");
                    if (!File.Exists(path)) return;
                    var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    try
                    {
                        Check(texture.LoadImage(File.ReadAllBytes(path), false) && texture.width > 600 && texture.height > 400,
                            "native screenshot decodes at the window resolution for " + states[index].Name);
                        var pixels = texture.GetPixels32();
                        Check(pixels.Any(p => p.r > 150 && p.g > 150 && p.b > 150)
                            && pixels.Any(p => p.r < 100 && p.g < 100 && p.b < 100), "native screenshot contains UI pixels for " + states[index].Name);
                        receipts.Add("{\"name\":" + Json(states[index].Name) + ",\"file\":" + Json(Path.GetFileName(path))
                            + ",\"width\":" + texture.width + ",\"height\":" + texture.height + ",\"repaints\":" + repaints
                            + ",\"category\":" + Json(states[index].Category) + ",\"search\":" + Json(states[index].Search)
                            + ",\"expanded\":" + (states[index].Expanded ? "true" : "false") + ",\"autoCapture\":" + (states[index].Auto ? "true" : "false")
                            + ",\"settingsRect\":" + Json(windowRect.ToString()) + "}");
                    }
                    finally { UnityEngine.Object.Destroy(texture); }
                    index++;
                    if (index == states.Length)
                    {
                        var expected = new HashSet<string>(SettingsCatalog.Entries.Select(e => e.Id));
                        Check(reached.SetEquals(expected), "every catalog entry has an exact reachable native draw attempt across the selected pages/states");
                        Complete(); return;
                    }
                    ApplyState();
                }
                catch (Exception e) { Complete(e); }
            }
        }
    }

    internal sealed class SettingsProbeMenuDriver : MonoBehaviour
    {
        private void Update() { ProbeSettingsWindow.MenuTick(); ProbeSettingsLayout.MenuTick(); }
    }

    /// <summary>The native Dialog_ModSettings caller is part of this regression fixture.
    /// The ordinary full-screen renderer probe above cannot establish this host contract.</summary>
    [StaticConstructorOnStartup]
    internal static class ProbeSettingsLayout
    {
        private const string ActualFontBundle = "G:/SteamLibrary/steamapps/workshop/content/294100/3231727915/rimfonts";
        private static readonly List<string> checks = new List<string>(), errors = new List<string>(), receipts = new List<string>();
        private static readonly HashSet<string> reached = new HashSet<string>();
        private static string output, originalSettingsFact, expectedSettings;
        private static bool active, initialized, captured;
        private static int index, repaints;
        private static float started, changedAt, capturedAt;
        private static MomentFramesMod mod;
        private static MomentFramesSettings originalSettings;
        private static readonly MomentFramesSettings fixture = new MomentFramesSettings
            { fade = true, birthdayMode = Birthday.ModeAsk, pxPerCell = 66, eventZoom = 1.06f };
        private static SettingsWindow renderer;
        private static Dialog_ModSettings host;
        private static Rect actualSettingsRect;
        private static AssetBundle actualFontBundle;
        private static Font actualFont;
        private static readonly List<string> fontFacts = new List<string>();
        private static string actualFontFact;
        private static Rect lastPhotoRect, clickedUv;
        private static Color clickedTint;
        private static Texture2D clickedTexture;
        private static int clickPhase;
        private static Window lightbox;
        private static bool lightboxCaptured;
        private static readonly List<Rect> previewUvs = new List<Rect>();
        private static readonly List<Color> previewTints = new List<Color>();
        private static readonly State[] states = {
            new State { Name="01-native-900x700", Original=true },
            new State { Name="02-native-900x650", Original=true, Short=true },
            new State { Name="03-large-capture" },
            new State { Name="04-large-photo", Category="photo" },
            new State { Name="05-large-appearance", Category="appearance", Expanded=true },
            new State { Name="06-large-memories", Category="memories", Expanded=true },
            new State { Name="07-large-storage", Category="storage", Expanded=true },
            new State { Name="08-capture-expanded", Expanded=true },
            new State { Name="09-capture-expanded-bottom", Expanded=true, Scroll=100000 },
            new State { Name="10-photo-reactions", Category="photo", Expanded=true, Preview="reactions", Scroll=100000 },
            new State { Name="11-photo-framing", Category="photo", Expanded=true, Preview="framing", SearchPreview=true, Scroll=100000 },
            new State { Name="12-narrow-appearance", Category="appearance", Expanded=true, Narrow=true },
            new State { Name="13-contaminated-capture", Contaminated=true, ActualFont=true },
            new State { Name="14-actual-font-appearance", Category="appearance", Expanded=true, Contaminated=true, ActualFont=true },
            new State { Name="15-actual-font-reactions", Category="photo", Expanded=true, Preview="reactions", Scroll=100000, ActualFont=true },
            new State { Name="16-actual-font-small-host", Original=true, Short=true, Contaminated=true, ActualFont=true },
            new State { Name="17-zoom-preview", Category="photo", Expanded=true, Preview="zoom", SearchPreview=true, Scroll=100000 },
            new State { Name="18-night-preview", Category="photo", Expanded=true, Preview="night", SearchPreview=true, Scroll=100000 },
            new State { Name="19-appearance-lightbox", Category="appearance", Expanded=true, Click=true },
            new State { Name="20-reactions-lightbox", Category="photo", Expanded=true, Preview="reactions", SearchPreview=true, Scroll=100000, Click=true },
        };
        private sealed class State
        {
            internal string Name, Category="capture", Preview;
            internal bool Original, Short, Narrow, Expanded, Contaminated, ActualFont, Click, SearchPreview;
            internal float Scroll;
        }
        private sealed class DrawState
        {
            internal GUIStyle[][] Arrays, Originals, Injected;
            internal Color Color;
            internal bool Enabled, Wrap;
            internal Matrix4x4 Matrix;
            internal GameFont Font;
            internal TextAnchor Anchor;
            internal bool Restored;
        }
        static ProbeSettingsLayout()
        {
            if (!GenCommandLine.CommandLineArgPassed("momentFramesSettingsLayoutProbe")) return;
            if (!GenCommandLine.TryGetCommandLineArg("momentFramesSettingsProbe",out output)
                || !Path.IsPathRooted(output) || !GenCommandLine.TryGetCommandLineArg("savedatafolder",out string profile)
                || !Path.IsPathRooted(profile) || !File.Exists(Path.Combine(profile,"MOMENTFRAMES_SETTINGS_DISPOSABLE"))
                || GenCommandLine.TryGetCommandLineArg("momentFramesProbe",out _))
            { Log.Error("[MomentFrames settings layout probe] a disposable menu-only profile is required"); return; }
            Directory.CreateDirectory(output); active=true; Application.runInBackground=true; Prefs.RunInBackground=true;
            Application.logMessageReceived+=NoteLog;
            var harmony=new Harmony("momentframes.settings.layout.disposable.probe");
            harmony.Patch(AccessTools.Method(typeof(SettingsWindow),"Draw"),
                prefix:new HarmonyMethod(typeof(ProbeSettingsLayout),nameof(BeforeDraw)),
                postfix:new HarmonyMethod(typeof(ProbeSettingsLayout),nameof(AfterDraw)),
                finalizer:new HarmonyMethod(typeof(ProbeSettingsLayout),nameof(DrawFinalizer)));
            harmony.Patch(AccessTools.Method(typeof(SettingsWindow),"DrawPhoto"),prefix:new HarmonyMethod(typeof(ProbeSettingsLayout),nameof(ObservePhoto)));
            harmony.Patch(AccessTools.Method(typeof(Widgets),nameof(Widgets.ButtonInvisible)),
                prefix:new HarmonyMethod(typeof(ProbeSettingsLayout),nameof(BeforePhotoButton)));
            LongEventHandler.ExecuteWhenFinished(()=> {
                started=Time.realtimeSinceStartup;
                var driver=new GameObject("MomentFrames Native Settings Host Probe") {hideFlags=HideFlags.HideAndDontSave};
                driver.AddComponent<SettingsProbeMenuDriver>();
            });
        }
        private static string Json(string value) => value==null?"null":"\""+value.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n").Replace("\t","\\t")+"\"";
        private static string SettingsFact(MomentFramesSettings value) => string.Join("|",typeof(MomentFramesSettings)
            .GetFields(BindingFlags.Instance|BindingFlags.Public).OrderBy(f=>f.Name).Select(f=>f.Name+"="+(f.GetValue(value)?.ToString()??"null")));
        private static void Check(bool value,string text)
        {
            if (!value) throw new InvalidOperationException(text);
            string line="PASS: "+text; if (!checks.Contains(line)) checks.Add(line);
        }
        private static void NoteLog(string condition,string stack,LogType type)
        {
            if(active && (type==LogType.Error||type==LogType.Exception||type==LogType.Assert)) errors.Add(type+": "+condition+"\n"+stack);
        }
        internal static void MenuTick()
        {
            if(!active) return;
            try
            {
                if(Time.realtimeSinceStartup-started>240) throw new TimeoutException("native settings host phase "+index);
                if(Current.ProgramState!=ProgramState.Entry||Current.Root_Entry==null||Find.UIRoot==null||Find.WindowStack==null||LongEventHandler.ShouldWaitForEvent) return;
                if(!initialized)
                {
                    Check(Current.Game==null,"native settings layout starts at menu with no game/save");
                    mod=LoadedModManager.GetMod<MomentFramesMod>();
                    Check(mod!=null,"actual MomentFrames mod is loaded in the disposable profile");
                    originalSettings=MomentFramesMod.Settings; originalSettingsFact=SettingsFact(originalSettings);
                    var other=LoadedModManager.ModHandles.FirstOrDefault(m=>!(m is MomentFramesMod));
                    Check(other!=null,"minimal profile supplies another native mod settings caller");
                    var otherHost=new Dialog_ModSettings(other);
                    Check(otherHost.InitialSize==new Vector2(900,700)&&!otherHost.resizeable,"another mod keeps the game's original 900x700 nonresizable settings host");
                    MomentFramesMod.Settings=fixture;
                    renderer=(SettingsWindow)AccessTools.Field(typeof(MomentFramesMod),"settingsWindow").GetValue(mod);
                    initialized=true; ApplyState(); return;
                }
                if(states[index].Click && clickPhase==2 && !lightboxCaptured)
                {
                    lightbox=Find.WindowStack.Windows.FirstOrDefault(w=>w.GetType().DeclaringType==typeof(SettingsWindow)&&w.GetType().Name=="PhotoExampleWindow");
                    Check(lightbox!=null,"handler-directed photo button dispatch opens production lightbox for "+states[index].Name);
                    Check(ReferenceEquals(AccessTools.Field(lightbox.GetType(),"texture").GetValue(lightbox),clickedTexture)
                        &&(Rect)AccessTools.Field(lightbox.GetType(),"uv").GetValue(lightbox)==clickedUv
                        &&(Color)AccessTools.Field(lightbox.GetType(),"tint").GetValue(lightbox)==clickedTint,
                        "production lightbox retains the actually clicked image/crop/tint for "+states[index].Name);
                    if(Time.realtimeSinceStartup-changedAt<1.2f) return;
                    ScreenCapture.CaptureScreenshot(Path.Combine(output,states[index].Name+".png"));
                    lightboxCaptured=captured=true;capturedAt=Time.realtimeSinceStartup;
                    File.WriteAllText(Path.Combine(output,"progress.txt"),(index+1)+"/"+states.Length+" "+states[index].Name);
                }
                if(!captured||Time.realtimeSinceStartup-capturedAt<.7f) return;
                string path=Path.Combine(output,states[index].Name+".png");
                if(!File.Exists(path)) return;
                var texture=new Texture2D(2,2,TextureFormat.RGB24,false);
                try
                {
                    Check(texture.LoadImage(File.ReadAllBytes(path),false)&&texture.width>600&&texture.height>400,"native screenshot decodes for "+states[index].Name);
                    var pixels=texture.GetPixels32();
                    Check(pixels.Any(p=>p.r>150&&p.g>150&&p.b>150)&&pixels.Any(p=>p.r<100&&p.g<100&&p.b<100),"native screenshot contains UI pixels for "+states[index].Name);
                    receipts.Add("{\"name\":"+Json(states[index].Name)+",\"file\":"+Json(Path.GetFileName(path))+",\"width\":"+texture.width+",\"height\":"+texture.height
                        +",\"hostType\":"+Json(host.GetType().FullName)+",\"hostRect\":"+Json(host.windowRect.ToString())+",\"settingsRect\":"+Json(actualSettingsRect.ToString())
                        +",\"repaints\":"+repaints+",\"category\":"+Json(states[index].Category)+",\"contaminated\":"+(states[index].Contaminated?"true":"false")
                        +",\"actualPlayerFont\":"+(states[index].ActualFont?"true":"false")+",\"searchPreview\":"+(states[index].SearchPreview?"true":"false")
                        +",\"handlerDirectedLightbox\":"+(states[index].Click?"true":"false")+",\"lightboxType\":"+Json(lightbox?.GetType().FullName)+"}");
                }
                finally {UnityEngine.Object.Destroy(texture);}
                if(lightbox!=null) {Find.WindowStack.TryRemove(lightbox,true);lightbox=null;}
                if(index==states.Length-1)
                {
                    renderer.SetSearch("native-cancel-test"); renderer.SetPreview("reactions");
                    host.OnCancelKeyPressed();
                    Check(Find.WindowStack.Windows.Contains(host)&&AccessTools.Field(typeof(SettingsWindow),"preview").GetValue(renderer)==null
                        &&(string)AccessTools.Field(typeof(SettingsWindow),"search").GetValue(renderer)=="native-cancel-test","native first Cancel closes example before search and keeps settings host open");
                    host.OnCancelKeyPressed();
                    Check(Find.WindowStack.Windows.Contains(host)&&(string)AccessTools.Field(typeof(SettingsWindow),"search").GetValue(renderer)=="","native second Cancel clears search and keeps settings host open");
                    host.OnCancelKeyPressed();
                    Check(!Find.WindowStack.Windows.Contains(host),"native third Cancel falls through to game's ordinary close behavior");
                }
                Find.WindowStack.TryRemove(host,true); host=null;
                index++;
                if(index==states.Length)
                {
                    var expected=new HashSet<string>(SettingsCatalog.Entries.Select(e=>e.Id));
                    Check(reached.SetEquals(expected),"every catalog entry reached through actual Dialog_ModSettings mod callback");
                    Complete(); return;
                }
                ApplyState();
            }
            catch(Exception e) {Complete(e);}
        }
        private static void ApplyState()
        {
            var state=states[index];
            SettingsWindowHost.ProbeUseOriginalSize=state.Original;
            renderer.SelectCategory(state.Category); renderer.SetSearch(""); renderer.SetExpanded(state.Expanded); renderer.SetScroll(state.Scroll);
            if(state.SearchPreview) renderer.SetSearch(SettingsCatalog.Entries.First(e=>e.Category==state.Category&&e.Preview==state.Preview).Label);
            if(state.Preview!=null) renderer.SetPreview(state.Preview);
            host=new Dialog_ModSettings(mod); Find.WindowStack.Add(host);
            if(state.Short) host.windowRect=new Rect((UI.screenWidth-900)/2f,(UI.screenHeight-650)/2f,900,650);
            if(state.Narrow) host.windowRect=new Rect((UI.screenWidth-620)/2f,24,620,Mathf.Min(850,UI.screenHeight-48));
            Check(host.GetType()==typeof(Dialog_ModSettings),"phase "+state.Name+" uses the game's real mod settings dialog");
            Check(host.windowRect.x>=0&&host.windowRect.y>=0&&host.windowRect.xMax<=UI.screenWidth+.1f&&host.windowRect.yMax<=UI.screenHeight+.1f,
                "native settings host fits logical display for "+state.Name);
            if(state.Original) Check(Mathf.Abs(host.windowRect.width-900)<.1f&&Mathf.Abs(host.windowRect.height-(state.Short?650:700))<.1f,
                "original native host size exercised for "+state.Name);
            else if(!state.Narrow) Check(host.windowRect.width>900 && host.windowRect.height>700,
                "MomentFrames native host expands beyond game's original size and clamps to display for "+state.Name);
            expectedSettings=SettingsFact(fixture); repaints=0;captured=false;changedAt=Time.realtimeSinceStartup;
            clickPhase=0;lightboxCaptured=false;clickedTexture=null;lastPhotoRect=Rect.zero;previewUvs.Clear();previewTints.Clear();
        }
        private static void LoadActualFont()
        {
            if(actualFont!=null) return;
            Check(File.Exists(ActualFontBundle),"actual player Custom Fonts bundle exists");
            actualFontBundle=AssetBundle.LoadFromFile(ActualFontBundle);
            Check(actualFontBundle!=null,"actual player Custom Fonts bundle loads in the three-package profile");
            foreach(string asset in actualFontBundle.GetAllAssetNames()) fontFacts.Add("asset: "+asset);
            actualFont=actualFontBundle.LoadAsset<Font>("assets/fonts/sources/goyangdeogyang b.ttf");
            Check(actualFont!=null,"exact player font asset path loads without the separate EB font");
            FontEngine.InitializeFontEngine();
            try
            {
                Check(FontEngine.LoadFontFace(actualFont)==FontEngineError.Success,"actual player font face loads through native FontEngine");
                var face=FontEngine.GetFaceInfo();
                fontFacts.Add(actualFont.name+" | family="+face.familyName+" | style="+face.styleName);
                Check(face.familyName=="GoyangDeogyang B"&&face.styleName=="Regular","exact GoyangDeogyang B Regular player font is selected by native FontEngine face info");
            }
            finally {FontEngine.UnloadFontFace();}
            actualFont.RequestCharactersInTexture("한W가나다ABC",20,FontStyle.Normal);
            Check(actualFont.HasCharacter('한')&&actualFont.HasCharacter('W'),"actual player font supplies Korean and Latin glyphs");
            var fontNames = typeof(Font).GetProperty("fontNames")?.GetValue(actualFont, null) as string[];
            actualFontFact=actualFont.name+" | families="+string.Join("/",fontNames ?? new[] {actualFont.name})+" | dynamic="+actualFont.dynamic+" | fontSize="+actualFont.fontSize+" | ascent="+actualFont.ascent+" | lineHeight="+actualFont.lineHeight;
        }
        private static void BeforeDraw(Rect rect,SettingsWindow __instance,out DrawState __state)
        {
            __state=null;
            if(!active||!initialized||!ReferenceEquals(__instance,renderer)) return;
            var state=states[index];
            __state=new DrawState {Color=GUI.color,Enabled=GUI.enabled,Matrix=GUI.matrix,Font=Text.Font,Anchor=Text.Anchor,Wrap=Text.WordWrap,
                Arrays=new[]{Text.fontStyles,Text.textFieldStyles,Text.textAreaStyles,Text.textAreaReadOnlyStyles}};
            __state.Originals=__state.Arrays.Select(a=>(GUIStyle[])a.Clone()).ToArray();
            try
            {
                if(Event.current.type==EventType.Repaint) {previewUvs.Clear();previewTints.Clear();lastPhotoRect=Rect.zero;}
                if(state.ActualFont) LoadActualFont();
                if(state.Contaminated||state.ActualFont)
                    for(int a=0;a<__state.Arrays.Length;a++) for(int i=0;i<__state.Arrays[a].Length;i++)
                    {
                        var style=new GUIStyle(__state.Arrays[a][i]);
                        if(state.ActualFont) style.font=actualFont;
                        if(state.Contaminated) {style.fontStyle=FontStyle.Bold;style.contentOffset=new Vector2(-11,-13);style.padding=new RectOffset(9,17,19,11);style.clipping=TextClipping.Clip;}
                        __state.Arrays[a][i]=style;
                    }
                __state.Injected=__state.Arrays.Select(a=>(GUIStyle[])a.Clone()).ToArray();
                if(state.Contaminated)
                {
                    var makeStyle=AccessTools.Method(typeof(SettingsWindow),"Style");
                    var body=(GUIStyle)makeStyle.Invoke(null,new object[]{false,TextAnchor.UpperLeft});
                    var help=(GUIStyle)makeStyle.Invoke(null,new object[]{true,TextAnchor.UpperLeft});
                    if(state.ActualFont) Check(body.font==actualFont&&help.font==actualFont,"settings body/help preserve the player's actual font while neutralizing style for "+state.Name);
                    Check(body.fontSize>=20&&help.fontSize>=17,"settings body/help remain readable under inherited font fixture for "+state.Name);
                    foreach(var style in new[]{body,help}) Check(style.fontStyle==FontStyle.Normal&&style.contentOffset==Vector2.zero
                        &&style.padding.left==0&&style.padding.right==0&&style.padding.top==0&&style.padding.bottom==0&&style.clipping==TextClipping.Overflow,
                        "settings text neutralizes inherited bold/offset/padding/clipping for "+state.Name);
                    string metrics=state.Name+" | bodyFont="+body.font?.name+" | bodySize="+body.fontSize+" | bodyLineHeight="+body.lineHeight
                        +" | KoreanCalcHeight="+body.CalcHeight(new GUIContent("한W"),600)+" | offset="+body.contentOffset+" | padding="+body.padding+" | style="+body.fontStyle+" | clipping="+body.clipping;
                    if(!fontFacts.Contains(metrics)) fontFacts.Add(metrics);
                }
                actualSettingsRect=rect;
            }
            catch(Exception e) {Restore(__state);Complete(e);}
        }
        private static void AfterDraw(DrawState __state)
        {
            if(__state==null||!active) {Restore(__state);return;}
            try
            {
                var state=states[index];
                Check(GUI.color==__state.Color&&GUI.enabled==__state.Enabled&&GUI.matrix==__state.Matrix&&Text.Font==__state.Font&&Text.Anchor==__state.Anchor&&Text.WordWrap==__state.Wrap,
                    "native renderer restores GUI/Text state for "+state.Name+" "+Event.current.type);
                for(int a=0;a<__state.Arrays.Length;a++) for(int i=0;i<__state.Arrays[a].Length;i++) Check(ReferenceEquals(__state.Arrays[a][i],__state.Injected[a][i]),
                    "native renderer restores caller font style references for "+state.Name);
                foreach(string id in renderer.DrawnSettings) reached.Add(id);
                if(Event.current.type!=EventType.Repaint) return;
                repaints++;
                if(captured||repaints<3||Time.realtimeSinceStartup-changedAt<.6f) return;
                Check(actualSettingsRect.y==40 && actualSettingsRect.width<host.windowRect.width
                    &&actualSettingsRect.height<host.windowRect.height-40,"actual mod settings body subtracts native title/margins/close area for "+state.Name);
                Check(SettingsFact(fixture)==expectedSettings&&fixture.pxPerCell==66&&Mathf.Abs(fixture.eventZoom-1.06f)<.000001f,
                    "passive native host drawing preserves all fixture values for "+state.Name);
                Check(SettingsFact(originalSettings)==originalSettingsFact,"original settings singleton object stays unmodified in disposable profile");
                Check(Current.Game==null&&Current.ProgramState==ProgramState.Entry,"native host phase "+state.Name+" remains menu-only");
                if(state.Name=="03-large-capture") ExerciseNativeResizeMinimum();
                if(state.Preview=="zoom") Check(previewUvs.Count==2&&previewUvs[0].width<previewUvs[1].width&&previewUvs[0].height<previewUvs[1].height,
                    "actual zoom preview renders distinct closer and wider real-image crops");
                if(state.Preview=="night") Check(previewTints.Count==2&&previewTints[0].grayscale<previewTints[1].grayscale,
                    "actual night preview renders the dimmer and brighter photo comparison");
                if(state.Click) return;
                ScreenCapture.CaptureScreenshot(Path.Combine(output,state.Name+".png")); captured=true;capturedAt=Time.realtimeSinceStartup;
                File.WriteAllText(Path.Combine(output,"progress.txt"),(index+1)+"/"+states.Length+" "+state.Name);
            }
            catch(Exception e) {Complete(e);}
            finally {Restore(__state);}
        }
        private static Exception DrawFinalizer(Exception __exception,DrawState __state)
        {
            Restore(__state);
            if(__exception!=null&&active) Complete(__exception);
            return __exception;
        }
        private static void ExerciseNativeResizeMinimum()
        {
            string label="actual native resize drag prevents a settings host below safe logical dimensions";
            if(checks.Contains("PASS: "+label)) return;
            var resizer=AccessTools.Field(typeof(Window),"resizer").GetValue(host) as WindowResizer;
            Check(resizer!=null,"native settings host has the real WindowResizer");
            Event saved=new Event(Event.current);
            try
            {
                Rect before=host.windowRect;
                Event.current.type=EventType.MouseDown;Event.current.button=0;Event.current.mousePosition=new Vector2(before.width-12,before.height-12);
                resizer.DoResizeControl(before);
                Event.current.type=EventType.MouseDrag;Event.current.mousePosition=new Vector2(-2000,-2000);
                Rect small=resizer.DoResizeControl(before);
                Check(small.width>=Mathf.Min(760,UI.screenWidth-48)&&small.height>=Mathf.Min(680,UI.screenHeight-48),label);
                Event.current.type=EventType.MouseUp;resizer.DoResizeControl(before);
            }
            finally {Event.current=saved;}
        }
        private static void ObservePhoto(Rect r,Texture2D tex,Rect uv,Color tint)
        {
            if(!active||!initialized||Event.current.type!=EventType.Repaint) return;
            previewUvs.Add(uv);previewTints.Add(tint);lastPhotoRect=r;
            if(states[index].Click&&clickPhase==0) {clickedTexture=tex;clickedUv=uv;clickedTint=tint;}
        }
        private static bool BeforePhotoButton(Rect butRect,ref bool __result)
        {
            if(!active||!initialized||!states[index].Click||clickPhase>=2||Event.current.type!=EventType.Repaint
                ||repaints<3||Time.realtimeSinceStartup-changedAt<.6f||butRect!=lastPhotoRect||clickedTexture==null) return true;
            // Input acquisition is the narrow disposable test seam. The production conditional,
            // popup construction, window stack and clicked image state all continue normally.
            __result=true;clickPhase=2;changedAt=Time.realtimeSinceStartup;
            checks.Add("INFO: handler-directed ButtonInvisible result dispatches the selected rendered photo for "+states[index].Name);
            return false;
        }
        private static void Restore(DrawState state)
        {
            if(state==null||state.Restored) return;
            state.Restored=true;
            if(state.Arrays!=null&&state.Originals!=null) for(int a=0;a<state.Arrays.Length;a++) Array.Copy(state.Originals[a],state.Arrays[a],state.Arrays[a].Length);
            GUI.color=state.Color;GUI.enabled=state.Enabled;GUI.matrix=state.Matrix;Text.Font=state.Font;Text.Anchor=state.Anchor;Text.WordWrap=state.Wrap;
        }
        private static void Complete(Exception error=null)
        {
            if(!active) return;
            if(error!=null) errors.Add("probe assertion: "+error);
            if(originalSettings!=null)
            {
                if(SettingsFact(originalSettings)!=originalSettingsFact) errors.Add("original settings changed");
                MomentFramesMod.Settings=originalSettings;
            }
            SettingsWindowHost.ProbeUseOriginalSize=false;
            string body="{\"ok\":"+(errors.Count==0?"true":"false")+",\"language\":"+Json(LanguageDatabase.activeLanguage?.folderName)
                +",\"programState\":"+Json(Current.ProgramState.ToString())+",\"gameCreated\":"+(Current.Game!=null?"true":"false")
                +",\"settingsUsed\":\"Disposable fixture temporarily supplied to actual mod callback; original singleton restored before quit\""
                +",\"actualPlayerFont\":"+Json(actualFontFact)+",\"checks\":["+string.Join(",",checks.Select(Json))+"],\"errors\":["+string.Join(",",errors.Select(Json))
                +"],\"drawnSettingAttempts\":["+string.Join(",",reached.OrderBy(x=>x).Select(Json))+"],\"screens\":["+string.Join(",",receipts)
                +"],\"limits\":\"Actual native Dialog_ModSettings in minimal three-package menu profile. Baseline is the new renderer inside the game's original host size, not a replay of the previous DLL. Framing/zoom/night examples use native search to isolate the selected setting into the viewport. Lightbox dispatch uses a targeted ButtonInvisible return-value test seam and does not establish physical mouse input. The prior r2 MouseDown/MouseUp injection failure is retained. Actual user font asset and inherited-style fixtures are exercised; full modpack/live-save compatibility and user visual acceptance remain separate. Draw attempts include offscreen rows.\"}";
            File.WriteAllText(Path.Combine(output,"settings-native-result.json"),body,new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(output,"settings-native-errors.txt"),errors,new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(output,"font-metrics.txt"),fontFacts,new UTF8Encoding(false));
            active=false;Application.logMessageReceived-=NoteLog;Application.Quit();
        }
    }
}
