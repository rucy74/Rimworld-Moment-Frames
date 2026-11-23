using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // Focused release check on a COPY of the existing disposable settlement fixture, never a player save.
    public static partial class Probe
    {
        private static bool releaseFollowupMode;
        private static PhotoRecord followupOriginal;
        private static Dialog_MFPhotoZoom followupViewer;
        private static readonly Dictionary<string, string> followupHashes = new Dictionary<string, string>();
        private static string followupUrl;
        private static int followupOpenCount;
        private static Harmony followupPatches;

        private static string FollowupHash(string file)
        {
            using (var stream = File.OpenRead(file))
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }
        private static void FollowupRequire(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("release followup: " + label);
            checks.Add("PASS: " + label);
        }
        private static bool FollowupOpenUrl(string __0)
        {
            if (!releaseFollowupMode) return true;
            followupUrl = __0; followupOpenCount++;
            File.WriteAllText(Path.Combine(dir, "export-url.txt"), __0);
            return false; // Do not launch a system browser over the user's other work. Verify the URL offline after exit.
        }
        private static void ReleaseFollowupStep()
        {
            var store = PhotoStore.Instance;
            File.WriteAllText(Path.Combine(dir, "phase.txt"), phase.ToString());
            switch (phase)
            {
                case 0:
                    deadline = Time.realtimeSinceStartup + 240f;
                    map = Find.CurrentMap; center = map.Center;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused; Prefs.DevMode = false; CloseLog();
                    var settings = MomentFramesMod.Settings;
                    settings.folderOverride = ""; settings.autoCaptureEnabled = false; settings.buildingPhotos = false;
                    settings.settlementPeriod = 0; settings.memoryNight = false; settings.yearLetter = false;
                    settings.albumOpacity = 1f;
                    FollowupRequire(store.Settlement.Snapshots.Any(s => s.complete && s.photoIds.Count == 5), "copied native fixture has a complete five-photo settlement survey");
                    string surveyInput = Path.Combine(dir, "survey-input.xml");
                    if (File.Exists(surveyInput))
                    {
                        // Workshop demonstration: existing author photos, with their original dates, inside the disposable fixture only.
                        var input = XDocument.Load(surveyInput).Root;
                        var snapshot = store.Settlement.Snapshots.First(s => s.complete && s.photoIds.Count == 5);
                        var inputs = input.Elements("photo").ToList();
                        FollowupRequire(inputs.Count == 5, "Workshop input supplies the overview and its four real regions");
                        int abs = int.Parse(input.Attribute("ticks").Value);
                        Find.TickManager.DebugSetTicksGame(abs - (Find.TickManager.TicksAbs - Find.TickManager.TicksGame));
                        snapshot.stamp = SettlementLayout.Stamp(int.Parse(input.Attribute("year").Value), int.Parse(input.Attribute("quarter").Value));
                        snapshot.requestedAbs = snapshot.startedAbs = snapshot.finishedAbs = abs;
                        for (int i = 0; i < 5; i++)
                        {
                            var record = store.Get(snapshot.photoIds[i]);
                            string destination = Path.GetFullPath(store.FilePath(record));
                            FollowupRequire(destination.StartsWith(Path.GetFullPath(GenFilePaths.SaveDataFolderPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Workshop replacement is confined to the copied profile");
                            File.Copy(inputs[i].Attribute("source").Value, destination, true);
                            string thumb = store.ThumbPath(record);
                            if (File.Exists(thumb)) File.Delete(thumb); // Regenerate this derived copy from the selected real photo.
                            record.title = inputs[i].Attribute("label").Value; record.desc = "";
                            record.generatedDesc = false; record.userDescription = true;
                            record.ticksAbs = record.capturedTicksAbs = abs;
                            record.dateSlug = input.Attribute("date").Value;
                            record.dateLabel = input.Attribute("year").Value + ", quarter " + input.Attribute("quarter").Value;
                            record.year = int.Parse(input.Attribute("year").Value); record.locationName = "Example colony";
                        }
                        store.NotifyChanged();
                        if (map.Parent is RimWorld.Planet.Settlement example) example.Name = "Example colony";
                    }
                    Find.PlaySettings.showLearningHelper = false; Prefs.AdaptiveTrainingEnabled = false;
                    foreach (var record in store.Records.Where(r => r.HasFile))
                    {
                        string path = store.FilePath(record);
                        FollowupRequire(File.Exists(path) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(GenFilePaths.SaveDataFolderPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "input is inside this disposable profile: " + record.id);
                        followupHashes[path] = FollowupHash(path);
                    }
                    FollowupRequire(followupHashes.Count >= 5, "at least five real image inputs are present");
                    checks.Add("INFO: runtime " + VersionControl.CurrentVersionString + "; engine=" + (RuntimeAudio.Waylith ? "1.7 Waylith" : "1.6 Unity"));
                    SettlementOpenTab(); Wait(2f); phase = 1; return;
                case 1:
                    CloseLog(); Shot("settlement-overview.png"); Wait(1f); phase = 2; return;
                case 2:
                    Require("settlement-overview.png");
                    Traverse.Create(typeof(SettlementRecordsTab)).Field("scroll").SetValue(new Vector2(0f, 600f));
                    Wait(1f); phase = 3; return;
                case 3:
                    Shot("settlement-regions.png"); Wait(1f); phase = 4; return;
                case 4:
                    Require("settlement-regions.png");
                    Find.MainTabsRoot.EscapeCurrentTab(false); PhotoTextureCache.Clear();
                    followupOriginal = store.Records.First(r => r.kind == SettlementChronicle.OverviewKind && r.HasFile);
                    FollowupRequire(!PhotoTextureCache.IsCached(store.FilePath(followupOriginal)), "original viewer starts with an uncached source");
                    followupViewer = new Dialog_MFPhotoZoom(followupOriginal); Find.WindowStack.Add(followupViewer);
                    Wait(.1f); phase = 5; return;
                case 5:
                    var image = Traverse.Create(followupViewer).Field("texture").GetValue<Texture2D>();
                    if (image == null)
                    {
                        FollowupRequire(!Traverse.Create(followupViewer).Field("loadFailed").GetValue<bool>(), "original worker has not failed");
                        Wait(.1f); return;
                    }
                    var pixels = PhotoImageFormat.DecodeOnWorker(File.ReadAllBytes(store.FilePath(followupOriginal)))
                        ?? throw new InvalidOperationException("followup original could not be decoded");
                    FollowupRequire(image.width == pixels.Width && image.height == pixels.Height && image.width >= 2048,
                        "original worker uploaded full-resolution texture " + image.width + "x" + image.height);
                    Shot("original-fit.png"); Wait(1f); phase = 6; return;
                case 6:
                    Require("original-fit.png");
                    Traverse.Create(followupViewer).Field("zoom").SetValue(2f);
                    Traverse.Create(followupViewer).Field("pan").SetValue(new Vector2(90f, -60f));
                    Wait(.5f); phase = 7; return;
                case 7:
                    Shot("original-zoom.png"); Wait(1f); phase = 8; return;
                case 8:
                    Require("original-zoom.png"); followupViewer.Close();
                    SettlementOpenTab();
                    followupPatches = new Harmony("rucy74.momentframes.probe.release-followup");
                    followupPatches.Patch(AccessTools.Method(typeof(Application), "OpenURL", new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(Probe), nameof(FollowupOpenUrl)));
                    Traverse.Create((MainTabWindow_Album)AlbumDef.TabWindow).Method("ToolsMenu").GetValue();
                    var menu = Find.WindowStack.WindowOfType<FloatMenu>();
                    FollowupRequire(menu != null, "actual album tools menu opened");
                    var option = Traverse.Create(menu).Field("options").GetValue<List<FloatMenuOption>>()
                        .Single(o => o.Label == "MF_Export".Translate().ToString());
                    menu.Close(); option.action(); // Invoke the very action bound to the product menu, not a second exporter.
                    string album = Path.Combine(store.FolderPath, "album.html");
                    checks.Add("INFO: export URL count=" + followupOpenCount + "; album=" + album + "; URL=" + followupUrl);
                    FollowupRequire(followupOpenCount == 1 && followupUrl != null && string.Equals(Path.GetFullPath(new Uri(followupUrl).LocalPath), Path.GetFullPath(album), StringComparison.OrdinalIgnoreCase) && File.Exists(album),
                        "the actual export menu action wrote album.html in the photo folder and asked to open it exactly once (browser launch intercepted)");
                    string page = File.ReadAllText(album);
                    var wanted = store.Records.Where(r => r.HasFile && !Kinds.PersonalOnly(r.kind)).ToList();
                    int listed = wanted.Count(r => AlbumExport.Href(store, r) is string href && page.Contains("\"f\":\"" + AlbumExport.Json(href) + "\""));
                    FollowupRequire(listed == wanted.Count && listed >= 5, "album.html lists every album photo by its path from the photo folder (" + listed + " of " + wanted.Count + ")");
                    FollowupRequire(page.Contains("/*MF_DATA*/{\"colony\"") && AlbumExport.Template() != null, "album.html was filled from the mod's Html/album.html");
                    foreach (var pair in followupHashes) FollowupRequire(FollowupHash(pair.Key) == pair.Value, "original PNG unchanged: " + Path.GetFileName(pair.Key));
                    File.WriteAllText(Path.Combine(dir, "followup-result.json"), "{\"ok\":true,\"engine17\":" + RuntimeAudio.Waylith.ToString().ToLowerInvariant()
                        + ",\"album\":" + Json(album) + ",\"checks\":[" + string.Join(",", checks.Select(Json))
                        + "],\"limits\":\"Copied controlled native fixture; menu action invoked directly, not a physical mouse click. URL launch intercepted. Zoom/pan set via probe; native image visually inspected separately. No player save, DEV install, full modpack or natural gameplay claim.\"}");
                    followupPatches.UnpatchAll(followupPatches.Id); active = false; Application.Quit(); return;
            }
        }
    }
}
