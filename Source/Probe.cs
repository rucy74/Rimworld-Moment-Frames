using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>
    /// Automated in-game test. Only runs when launched with -momentFramesProbe=&lt;dir&gt; AND an isolated
    /// -savedatafolder containing the marker file MOMENTFRAMES_DISPOSABLE. Never touches user saves.
    /// </summary>
    [StaticConstructorOnStartup]
    public static partial class Probe
    {
        private static bool active;
        private static string dir;
        private static int phase;
        private static float nextAt;
        private static float deadline;
        private static Map map;
        private static IntVec3 center;
        private static Building_PhotoFrame frame;
        private static Building_PhotoFrame easel;
        private static PhotoRecord testRec;
        private static Pawn p1, p2;
        private static int weddingStartTick;
        private static readonly List<string> checks = new List<string>();

        static Probe()
        {
            if (!GenCommandLine.TryGetCommandLineArg("momentFramesProbe", out dir)) return;
            if (!GenCommandLine.TryGetCommandLineArg("savedatafolder", out var profile) || !Path.IsPathRooted(profile) ||
                !File.Exists(Path.Combine(profile, "MOMENTFRAMES_DISPOSABLE")))
            {
                // Not thrown: a type initializer that throws fails again on every later touch (the store calls Probe.Update every frame).
                Log.Error("[MomentFrames] the probe needs an explicitly marked disposable profile; it stays off.");
                return;
            }
            Directory.CreateDirectory(dir);
            active = true;
            releaseFixesMode = GenCommandLine.TryGetCommandLineArg("momentFramesReleaseFixes", out _);
            uiFixesMode = GenCommandLine.TryGetCommandLineArg("momentFramesUiFixes", out _);
            archiveMode = GenCommandLine.TryGetCommandLineArg("momentFramesArchive", out _);
            tickCostMode = GenCommandLine.TryGetCommandLineArg("momentFramesTickCost", out _);
            framePhotoQualityMode = GenCommandLine.TryGetCommandLineArg("momentFramesFrameQuality", out _);
            creditsPhotosMode = GenCommandLine.TryGetCommandLineArg("momentFramesCreditsPhotos", out _);
            baselineParityMode = GenCommandLine.TryGetCommandLineArg("momentFramesBaselineParity", out _);
            dcSaveMode = GenCommandLine.TryGetCommandLineArg("momentFramesDcSave", out _);
            renderEffectsMode = GenCommandLine.TryGetCommandLineArg("momentFramesRenderEffects", out _);
            renderOrderMode = GenCommandLine.TryGetCommandLineArg("momentFramesRenderOrder", out _);
            renderEffectsMode |= renderOrderMode;
            faultIsolationMode=GenCommandLine.TryGetCommandLineArg("momentFramesFaultIsolation",out _);
            playtestOctoberMode=GenCommandLine.TryGetCommandLineArg("momentFramesPlaytestOctober",out _);
            sceneQualityMode=GenCommandLine.TryGetCommandLineArg("momentFramesSceneQuality",out _);
            storageReviewMode=GenCommandLine.TryGetCommandLineArg("momentFramesStorageReview",out _);
            importedPhotosMode=GenCommandLine.TryGetCommandLineArg("momentFramesImportedPhotos",out _);
            photoBridgeMode=GenCommandLine.TryGetCommandLineArg("momentFramesPhotoBridge",out _);
            photoFormatMode=GenCommandLine.TryGetCommandLineArg("momentFramesPhotoFormat",out _);
            if (GenCommandLine.TryGetCommandLineArg("momentFramesUserTest", out var userTest) && !userTest.NullOrEmpty()) userTestSource = userTest;   // user-test-jpg.cmd (ProbeUserTest.cs)
            faultBaseline=GenCommandLine.TryGetCommandLineArg("momentFramesFaultBaseline",out _);
            releaseFollowupMode = GenCommandLine.TryGetCommandLineArg("momentFramesReleaseFollowup", out _);
            projectorControlsMode = GenCommandLine.TryGetCommandLineArg("momentFramesProjectorControls", out _);
            photoStudioMode = GenCommandLine.TryGetCommandLineArg("momentFramesPhotoStudio", out _);
            settlementMode = GenCommandLine.TryGetCommandLineArg("momentFramesSettlement", out _);
            settlementPreview = GenCommandLine.TryGetCommandLineArg("momentFramesSettlementPreview", out _);
            settlementMode |= settlementPreview;
            captionContextMode = GenCommandLine.TryGetCommandLineArg("momentFramesCaptionContext", out _);
            playtestFeedbackMode = GenCommandLine.TryGetCommandLineArg("momentFramesPlaytestFeedback", out _);
            birthdayPreparationMode = GenCommandLine.TryGetCommandLineArg("momentFramesBirthdayPreparation", out _);
            sharedPhotoUiMode = GenCommandLine.TryGetCommandLineArg("momentFramesSharedPhotoUI",out _);
            photoDoodlesMode = GenCommandLine.TryGetCommandLineArg("momentFramesPhotoDoodles",out _);
            petPhotosMode = GenCommandLine.TryGetCommandLineArg("momentFramesPets",out _);
            reviewFixesMode = GenCommandLine.TryGetCommandLineArg("momentFramesReviewFixes",out _);
            playtestFixesMode = GenCommandLine.TryGetCommandLineArg("momentFramesPlaytestFixes", out _);
            appearanceMode = GenCommandLine.TryGetCommandLineArg("momentFramesAppearance", out _);
            varietyMode = GenCommandLine.TryGetCommandLineArg("momentFramesVariety", out _);
            treeMode = GenCommandLine.TryGetCommandLineArg("momentFramesTree", out _);
            eventTextMode = GenCommandLine.TryGetCommandLineArg("momentFramesEventText", out _);
            memoryMusicMode = GenCommandLine.TryGetCommandLineArg("momentFramesMemoryMusic", out _);
            memorySfxMode = GenCommandLine.TryGetCommandLineArg("momentFramesMemorySfx", out _);
            if (GenCommandLine.TryGetCommandLineArg("momentFramesShots", out var shots) && Directory.Exists(shots)) shotsSource = shots;   // Workshop screenshots, no checks (ProbeShots.cs)
            steamMediaMode = GenCommandLine.TryGetCommandLineArg("momentFramesSteamMedia", out _);                                         // with -momentFramesShots: GIF frames and stills (ProbeSteamMedia.cs)
            if (GenCommandLine.TryGetCommandLineArg("momentFramesSteamExtras", out var extras)) steamExtras = extras;
            if (GenCommandLine.TryGetCommandLineArg("momentFramesLoad", out var save) && !save.NullOrEmpty()) loadSave = save;              // load that save and report (LoadDiagStep)
            // Probe-only render log: which pawns RenderPawnAt handled in which frame (diagnosing photos that show shadows but no bodies).
            new HarmonyLib.Harmony("rucy74.momentframes.probe").Patch(HarmonyLib.AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt)),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(NoteRender)));
            // The probe window is hidden/unfocused; keep Unity's update loop running regardless of focus.
            Application.runInBackground = true;
            Prefs.RunInBackground = true;
            Log.Message("[MomentFrames probe] starting; output " + dir);
            LongEventHandler.ExecuteWhenFinished(Begin);
        }

        private static void Begin()
        {
            if (loadSave != null)
            {
                GameDataSaveLoader.LoadGame(loadSave);
                deadline = Time.realtimeSinceStartup + 600f;
                return;
            }
            LongEventHandler.QueueLongEvent(() =>
            {
                Root_Play.SetupForQuickTestPlay();
                bool timeFilm = GenCommandLine.TryGetCommandLineArg("momentFramesSteamOnly", out var steamScenes) && steamScenes.Contains("time");
                Find.GameInitData.mapSize = settlementMode || timeFilm || tickCostMode ? 250 : 120;
                if (timeFilm)
                {
                    var surface = Find.WorldGrid.Surface;
                    for (int i = 0; i < surface.TilesCount; i++)
                    {
                        var tile = surface.PlanetTileForID(i);
                        if (Find.WorldGrid[tile].PrimaryBiome?.defName == "TemperateForest" && Find.WorldGrid[tile].hilliness == RimWorld.Planet.Hilliness.Flat
                            && RimWorld.Planet.TileFinder.IsValidTileForNewSettlement(tile)) { Find.GameInitData.startingTile = tile; break; }
                    }
                }
                Find.GameInitData.PrepForMapGen();
                Find.Scenario.PreMapGenerate();
            }, "Play", "MomentFrames probe map", true, null);
            deadline = Time.realtimeSinceStartup + 900f;   // the suite has grown (camera, rare beasts, moments); 600 s was being grazed
        }

        public static void Update()
        {
            if (!active) return;
            try
            {
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("probe timeout in phase " + phase);
                if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || LongEventHandler.ShouldWaitForEvent) return;
                if (memoryMusicMode) SampleMemoryMusic();
                if (memorySfxMode) SampleMemorySfx();
                if (Time.realtimeSinceStartup < nextAt) return;
                if (userTestSource != null) { UserTestStep(); return; }
                if (tickCostMode) { TickCostStep(); return; }
                if (framePhotoQualityMode) { FramePhotoQualityStep(); return; }
                if (creditsPhotosMode) { CreditsPhotosStep(); return; }
                if (baselineParityMode) { BaselineParityStep(); return; }
                if (uiFixesMode) { UiFixesStep(); return; }
                if (archiveMode) { ArchiveStep(); return; }
                if (releaseFixesMode) { ReleaseFixesStep(); return; }
                if (renderEffectsMode) { RenderEffectsStep(); return; }
                if (faultIsolationMode) { FaultIsolationStep(); return; }
                if (playtestOctoberMode) { PlaytestOctoberStep(); return; }
                if (sceneQualityMode) { SceneQualityStep(); return; }
                if (storageReviewMode) { StorageReviewStep(); return; }
                if (importedPhotosMode) { ImportedPhotosStep(); return; }
                if (photoBridgeMode) { PhotoBridgeStep(); return; }
                if (photoFormatMode) { PhotoFormatStep(); return; }
                if (releaseFollowupMode) { ReleaseFollowupStep(); return; }
                if (projectorControlsMode) { ProjectorControlsStep(); return; }
                if (photoStudioMode) { PhotoStudioStep(); return; }
                if (settlementMode) { SettlementStep(); return; }
                if (dcSaveMode) { DcSaveStep(); return; }
                if (loadSave != null) { LoadDiagStep(); return; }
                if (captionContextMode) { CaptionContextStep(); return; }
                if (playtestFeedbackMode) { PlaytestFeedbackStep(); return; }
                if (birthdayPreparationMode) { BirthdayPreparationStep(); return; }
                if (sharedPhotoUiMode) { SharedPhotoUiStep(); return; }
                if (photoDoodlesMode) { PhotoDoodlesStep(); return; }
                if (petPhotosMode) { PetPhotosStep(); return; }
                if (reviewFixesMode) { ReviewFixesStep(); return; }
                if (playtestFixesMode) { PlaytestFixesStep(); return; }
                if (memoryMusicMode) { MemoryMusicStep(); return; }
                if (memorySfxMode) { MemorySfxStep(); return; }
                if (eventTextMode) { EventTextStep(); return; }
                if (varietyMode) { VarietyStep(); return; }
                if (treeMode) { TreeStep(); return; }
                Step();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static void Wait(float seconds) => nextAt = Time.realtimeSinceStartup + seconds;

        // ---------- load a save and report (-momentFramesLoad=<save>): where the photos are looked for, whether they load ----------

        private static string loadSave;

        private static void LoadDiagStep()
        {
            var store = PhotoStore.Instance;
            var m = Find.CurrentMap;
            switch (phase)
            {
                case 0:
                    CloseLog();
                    Wait(5f);
                    phase = 1;
                    return;
                case 1:
                {
                    checks.Add("INFO: loaded '" + loadSave + "' — gameId " + store.GameId + ", override '" + MomentFramesMod.Settings?.folderOverride + "', save data " + GenFilePaths.SaveDataFolderPath);
                    checks.Add("INFO: photo folder " + store.FolderPath + " — exists " + Directory.Exists(store.FolderPath));
                    var first = store.Records.FirstOrDefault(r => r.HasFile);
                    if (first != null)
                    {
                        string fp = store.FilePath(first), tp = store.ThumbPath(first);
                        var t = PhotoTextureCache.Thumb(tp, fp);
                        checks.Add("INFO: first photo " + fp + " exists " + File.Exists(fp) + "; thumb " + tp + " exists " + File.Exists(tp) + "; thumbnail loads " + (t == null ? "no" : t.width + "x" + t.height));
                    }
                    var frames = m.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoFrame>().ToList();
                    checks.Add("INFO: " + frames.Count + " frames, " + frames.Count(f => f.HasPhotoTexture) + " with a photo texture; " + store.Records.Count(r => r.HasFile) + " photos; places " + Places.Show + ", " + Places.On(m).Count + " pinnable here");
                    var shown = m.listerThings.AllThings.Where(t => t is Building_PhotoFrame || t is Building_PhotoCollage || t is Building_PhotoProjector).ToList();
                    Func<Thing, string> q = t => t.TryGetComp<CompQuality>()?.Quality.ToString() ?? "no quality";
                    checks.Add("INFO: displays after load — " + string.Join("; ", shown.Select(t => t.def.defName + " " + q(t) + (t is Building_PhotoFrame pf ? " " + pf.Style : ""))));
                    int awful = shown.Count(t => t.TryGetComp<CompQuality>()?.Quality == QualityCategory.Awful), noQ = shown.Count(t => t.TryGetComp<CompQuality>() == null);
                    checks.Add((awful == 0 && noQ == 0 ? "PASS" : "FAIL") + ": displays of this save all carry a quality and none became awful (" + shown.Count + " displays, " + awful + " awful, " + noQ + " without)");
                    Shot("load.png");
                    Wait(2f);
                    phase = 2;
                    return;
                }
                default:
                    Require("load.png");
                    Finish();
                    return;
            }
        }

        private static readonly Dictionary<int, List<string>> renderLog = new Dictionary<int, List<string>>();

        /// <summary>Harmony prefix on PawnRenderer.RenderPawnAt (probe only): frame -> "name(draw/cached/valid)@drawLoc".</summary>
        public static void NoteRender(PawnRenderer __instance, Pawn ___pawn, Vector3 drawLoc)
        {
            try
            {
                var r = HarmonyLib.Traverse.Create(__instance).Field("results");
                string s = ___pawn.LabelShort + "(" + r.Field("draw").GetValue<bool>() + "/" + r.Field("useCached").GetValue<bool>() + "/" + r.Field("valid").GetValue<bool>() + ")@" + drawLoc.ToString("F1");
                int f = Time.frameCount;
                if (!renderLog.TryGetValue(f, out var l)) renderLog[f] = l = new List<string>();
                if (l.Count < 30) l.Add(s);
                if (renderLog.Count > 300) renderLog.Remove(renderLog.Keys.Min());
            }
            catch { }
        }

        private static string RenderedIn(int frame) => renderLog.TryGetValue(frame, out var l) ? l.Count + ": " + string.Join("; ", l) : "(none)";

        /// <summary>One line about the last capture: render/expand frames, the union rect, where the given pawns are and who was rendered in that frame.</summary>
        private static string CaptureDiagLine(string what, IEnumerable<Pawn> pawns)
        {
            int rf = PhotoCapture.LastRenderFrame, ef = PhotoCapture.LastExpandFrame;
            var union = PhotoCapture.LastUnion;
            string where = string.Join(", ", pawns.Select(p => p.LabelShort + "@" + p.Position + "/draw" + p.DrawPos.ToString("F1") + (union.Contains(p.Position) ? "" : " OUTSIDE")));
            return "INFO: " + what + " render frame " + rf + " expand " + ef + " union " + union + " now " + Time.frameCount + "; " + where + "; rendered in " + rf + " -> " + RenderedIn(rf);
        }

        /// <summary>A photo of people must show people: fails when the PNG has no pawn-coloured pixels.</summary>
        private static void RequirePeople(string file)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) throw new InvalidOperationException(file + " missing");
            if (!PawnsVisible(path, out int lit)) throw new InvalidOperationException(file + " shows no pawns (" + lit + " pawn-coloured pixels)");
            checks.Add("PASS: " + file + " shows people (" + lit + " pawn-coloured pixels)");
        }

        private static void Step()
        {
            if (appearanceMode) { AppearanceStep(); return; }
            if (shotsSource != null) { ShotsStep(); return; }
            if (phase == 1000) { DesignStep(); return; }
            var store = PhotoStore.Instance;
            switch (phase)
            {
                case 0:
                    FxSet(false);   // effects stay off so they cannot leak into the other screenshots; phase 18-22 tests them explicitly
                    Setup();
                    Wait(2f);
                    phase = 1;
                    return;
                case 1:
                    testRec = store.Request(map, center, "test", "probe:test", "Probe test capture", "north wall on top", map.mapPawns.FreeColonistsSpawned);
                    if (testRec == null) throw new InvalidOperationException("test request rejected");
                    checks.Add("PASS: request accepted");
                    phase = 2;
                    return;
                case 2:
                    if (!testRec.HasFile)
                    {
                        if (!store.Records.Contains(testRec)) throw new InvalidOperationException("test record dropped (capture failed)");
                        return;
                    }
                    {
                        string path = store.FilePath(testRec);
                        long size = new FileInfo(path).Length;
                        if (size < 20000) throw new InvalidOperationException("capture file too small: " + size);
                        File.Copy(path, Path.Combine(dir, "capture.png"), true);
                        if (File.Exists(store.ThumbPath(testRec))) File.Copy(store.ThumbPath(testRec), Path.Combine(dir, "capture.thumb.png"), true);
                        checks.Add("PASS: capture file " + size + " bytes at " + path);
                        frame.SetPhoto(testRec.id);
                        Find.CameraDriver.SetRootPosAndSize(frame.DrawPos + new Vector3(0f, 0f, 0.7f), 11f);
                        Wait(1.5f);
                        phase = 3;
                    }
                    return;
                case 3:
                    CloseLog();
                    Shot("frame-on-map.png");
                    // Close-up render of the frame through the same capture pipeline (frame ~100 px wide).
                    if (!PhotoCapture.Capture(map, frame.Position, 7, r =>
                        {
                            if (r?.png != null) File.WriteAllBytes(Path.Combine(dir, "frame-closeup.png"), r.png);
                        }))
                        throw new InvalidOperationException("close-up capture could not start");
                    Wait(2f);
                    phase = 4;
                    return;
                case 4:
                    Require("frame-on-map.png");
                    Require("frame-closeup.png");
                    // "View" opens the album's gallery on that photo (the old viewer dialog is gone).
                    MainTabWindow_Album.Open(testRec);
                    Wait(1.5f);
                    phase = 5;
                    return;
                case 5:
                    CloseLog();
                    Shot("gallery.png");
                    Wait(2f);
                    phase = 6;
                    return;
                case 6:
                    Require("gallery.png");
                    if (Find.MainTabsRoot.OpenTab != AlbumDef || !(AlbumDef.TabWindow is MainTabWindow_Album ga) || ga.GalleryCurrent != testRec)
                        throw new InvalidOperationException("gallery did not open on the test photo (tab " + Find.MainTabsRoot.OpenTab?.defName + ")");
                    if (ga.windowRect.width >= UI.screenWidth - 10f) throw new InvalidOperationException("gallery window is full-screen (" + ga.windowRect.width + " of " + UI.screenWidth + ")");
                    checks.Add("PASS: 'view' opened the album gallery on the photo (book-sized window " + ga.windowRect.width + "x" + ga.windowRect.height + ")");
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    Find.WindowStack.Add(new Dialog_ChoosePhoto(frame));
                    Wait(1.5f);
                    phase = 7;
                    return;
                case 7:
                    CloseLog();
                    Shot("chooser.png");
                    Wait(2f);
                    phase = 8;
                    return;
                case 8:
                    Require("chooser.png");
                    Find.WindowStack.TryRemove(typeof(Dialog_ChoosePhoto));
                    // Mod settings window (Options → Mod settings → Moment Frames), as the player sees it. The page measures its own height while
                    // drawing; start that too small so a listing that wraps into a second, off-screen column shows up here (v2.4.0: the English
                    // labels ran past the first guess and the page collapsed to its first line).
                    Find.WindowStack.Add(new Dialog_ModSettings(LoadedModManager.GetMod<MomentFramesMod>()));
                    Wait(1.5f);
                    phase = 14;
                    return;
                case 14:
                    {
                    }
                    CloseLog();
                    Shot("settings.png");
                    Wait(2f);
                    phase = 15;
                    return;
                case 15:
                    Require("settings.png");
                    Find.WindowStack.TryRemove(typeof(Dialog_ModSettings));
                    PlaceVariants();
                    Wait(1.5f);
                    phase = 16;
                    return;
                case 16:
                    DragCapture();
                    phase = 17;
                    return;
                case 17:
                    if (!CheckDragCapture()) return;
                    Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 11f);
                    if (!PhotoCapture.Capture(map, center.ToVector3Shifted(), 15f, r =>
                        {
                            if (r?.png != null) File.WriteAllBytes(Path.Combine(dir, "variants.png"), r.png);
                        }))
                        throw new InvalidOperationException("variants capture could not start");
                    Wait(2f);
                    phase = 18;
                    return;
                case 18:
                    if (PhotoCapture.Busy) { Wait(0.2f); return; }
                    Require("variants.png");
                    StylesCapture();
                    Wait(2f);
                    phase = 76;
                    return;
                case 76:
                    Require("frame-styles.png");
                    FxStart();
                    phase = 20;
                    return;
                case 20:
                    if (fxAt <= 0f) return;   // the effect shot has not been saved yet
                    if (!fxFlashShot && Time.realtimeSinceStartup - fxAt > 0.04f)
                    {
                        fxFlashShot = true;
                        CloseLog();
                        Shot("fx-flash.png");   // ~first frame after the shot: white overlay near full strength, photo still large in the centre
                    }
                    if (Time.realtimeSinceStartup - fxAt < 0.35f) return;
                    Require("fx-flash.png");
                    if (!Find.TickManager.Paused) throw new InvalidOperationException("freeze: game not held 0.35 s after the shot");
                    checks.Add("PASS: freeze holds the game right after the shot (forcePause card)");
                    CloseLog();
                    Shot("fx-shrink.png");
                    phase = 21;
                    return;
                case 21:
                    if (Time.realtimeSinceStartup - fxAt < 1.3f) return;
                    if (!Find.WindowStack.IsOpen(typeof(Window_PhotoPop))) throw new InvalidOperationException("photo card not open 1.3 s after the shot");
                    if (Find.WindowStack.WindowsForcePause) throw new InvalidOperationException("freeze still holding the game 1.3 s after the shot");
                    checks.Add("PASS: photo card open, freeze released after " + PhotoFx.FreezeSeconds + " s");
                    Shot("fx-card.png");
                    phase = 22;
                    return;
                case 22:
                    if (Time.realtimeSinceStartup - fxAt < 4.2f) return;
                    Require("fx-shrink.png");
                    Require("fx-card.png");
                    if (Find.WindowStack.IsOpen(typeof(Window_PhotoPop))) throw new InvalidOperationException("photo card still open 4.2 s after the shot");
                    checks.Add("PASS: photo card closed itself");
                    FxBusyChecks();
                    LetterPauseChecks();
                    FxSet(false);
                    Find.TickManager.Pause();
                    if (ModsConfig.IdeologyActive)
                    {
                        IdeoChecks();
                        phase = 19;
                        return;
                    }
                    checks.Add("INFO: Ideology inactive in this run — ritual grouping and role-photo checks skipped (launch with -Dlc)");
                    StartWedding();
                    phase = 9;
                    deadline = Time.realtimeSinceStartup + 300f;
                    return;
                case 19:
                    if (!CheckRolePhoto()) return;
                    FloorUnderFrameChecks();
                    CameraPark();
                    Wait(0.5f);
                    phase = 61;
                    return;
                case 61:
                    CameraShotPlayed();
                    Wait(0.5f);
                    phase = 62;
                    return;
                case 62:
                    CameraFollowed();
                    Wait(0.5f);
                    phase = 63;
                    return;
                case 63:
                    CameraLeftAlone();
                    RareAnimalSpawn();
                    Wait(1.5f);
                    phase = 64;
                    return;
                case 64:
                    RareAnimalPhotographed();
                    EditChecksA();
                    Wait(0.6f);
                    phase = 65;
                    return;
                case 65:
                    EditFrameFollows();
                    ReviewFixes();
                    EndingHooks();
                    LifeEventsCheck();
                    DescriptionCandidatesCheck();
                    BattlesCheck();
                    ConditionsCheck();
                    Find.WindowStack.Add(new Dialog_PhotoEdit(testRec));
                    Wait(1f);
                    phase = 66;
                    return;
                case 66:
                    Shot("editor.png");
                    Wait(1f);
                    phase = 67;
                    return;
                case 67:
                    Require("editor.png");
                    Find.WindowStack.TryRemove(typeof(Dialog_PhotoEdit), false);
                    MainTabWindow_Album.ShowVersion(testRec, null);
                    Wait(1f);
                    phase = 68;
                    return;
                case 68:
                    Shot("edit-gallery.png");
                    Wait(1f);
                    phase = 86;
                    return;
                // An edited version's own title and note; the album as a window of its own (moved, resized, see-through).
                case 86:
                    Require("edit-gallery.png");
                    VersionTextChecks();
                    Wait(1f);
                    phase = 87;
                    return;
                case 87:
                    Shot("edit-title.png");
                    Wait(1f);
                    phase = 88;
                    return;
                case 88:
                    Require("edit-title.png");
                    AlbumWindowStart();
                    Wait(1.2f);
                    phase = 89;
                    return;
                case 89:
                    Shot("album-narrow.png");
                    Wait(1f);
                    phase = 90;
                    return;
                case 90:
                    Require("album-narrow.png");
                    AlbumWindowKept();
                    phase = 69;
                    return;
                case 69:
                    Require("edit-gallery.png");
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    EditChecksB();
                    JourneyStart();
                    Wait(0.5f);
                    phase = 70;
                    return;
                case 70:
                    if (!JourneyWait()) return;
                    // The route on the world map: the world tab open, looking at the new place.
                    CameraJumper.TryJump(journeyTile);   // shows the world, looking at the new place
                    Wait(1.5f);
                    phase = 71;
                    return;
                case 71:
                    CloseLog();
                    Shot("journey-world.png");
                    Wait(1f);
                    phase = 72;
                    return;
                case 72:
                    Require("journey-world.png");
                    JourneyEnd();
                    BirthdayStart();
                    Wait(1.5f);
                    phase = 73;
                    return;
                case 73:
                    CloseLog();
                    Shot("birthday-letter.png");
                    Wait(1f);
                    phase = 74;
                    return;
                case 74:
                    Require("birthday-letter.png");
                    BirthdayChooseHere();
                    phase = 75;
                    return;
                case 75:
                    if (!BirthdayPhotoReady()) return;
                    BirthdayPickPlace();
                    MomentChecks();
                    Wait(2f);
                    phase = 60;
                    return;
                case 60:
                    Require("lanterns.png");
                    foreach (var t in probeLanterns) if (t.Spawned) t.Destroy();
                    probeLanterns.Clear();
                    foreach (var tp in probeTemp) if (!tp.Destroyed) tp.Destroy(DestroyMode.Vanish);
                    probeTemp.Clear();
                    if (probeVictim?.Corpse != null && probeVictim.Corpse.Spawned) probeVictim.Corpse.Destroy();
                    probeVictim = null;
                    StartWedding();
                    phase = 9;
                    deadline = Time.realtimeSinceStartup + 300f;
                    return;
                case 9:
                    RunWedding();
                    return;
                // Album (after the wedding, so the chronicle has real milestones). Screenshots are written at the end of the frame,
                // so every tab change happens in the phase after the shot.
                case 23:
                    AlbumOpen(false, false);
                    Wait(1.5f);
                    phase = 26;
                    return;
                case 26:
                    CloseLog();
                    Shot("album.png");
                    Wait(1f);
                    phase = 27;
                    return;
                case 27:
                    AlbumOpen(true, true);
                    Wait(1.5f);
                    phase = 28;
                    return;
                case 28:
                    CloseLog();
                    Shot("chronicle.png");
                    Wait(1f);
                    phase = 29;
                    return;
                case 29:
                    AlbumChecks();
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    // The chronicle tab in p1's inspect pane, as the player opens it.
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(p1, false);
                    if (!(InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Chronicle)) is ITab_Pawn_Chronicle)) throw new InvalidOperationException("chronicle tab could not be opened for " + p1.LabelShort);
                    Find.CameraDriver.SetRootPosAndSize(p1.DrawPos, 14f);
                    Wait(1.5f);
                    phase = 24;
                    return;
                case 24:
                    CloseLog();
                    Shot("pawn-tab.png");
                    Wait(1f);
                    phase = 25;
                    return;
                case 25:
                    Require("pawn-tab.png");
                    {
                        var inspect = (MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow;
                        if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect || inspect.OpenTabType != typeof(ITab_Pawn_Chronicle))
                            throw new InvalidOperationException("chronicle tab not open at the screenshot (open " + inspect.OpenTabType?.Name + ")");
                        // A row opens the gallery on it, still filtered to this person — also for a non-milestone photo (the chronicle order would drop it otherwise).
                        var mine = PhotoStore.Instance.For(p1).OrderBy(r => r.ticksAbs).ToList();
                        var pick = mine.FirstOrDefault(r => !Kinds.IsMilestone(r.kind)) ?? mine.First();
                        MainTabWindow_Album.OpenFor(p1, pick, true, true);
                        if (Find.MainTabsRoot.OpenTab != AlbumDef || !(AlbumDef.TabWindow is MainTabWindow_Album tabAlbum) || tabAlbum.GalleryCurrent != pick)
                            throw new InvalidOperationException("a row in the chronicle tab did not open the gallery on it");
                        if (tabAlbum.PersonFilter != p1.GetUniqueLoadID()) throw new InvalidOperationException("the gallery lost the person filter (" + tabAlbum.PersonFilter + ")");
                        checks.Add("PASS: chronicle tab screenshot; a row opened the gallery on '" + pick.title + "' (milestone " + Kinds.IsMilestone(pick.kind) + "), filtered to " + p1.LabelShort);
                    }
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    Find.Selector.ClearSelection();
                    Wait(1f);
                    phase = 30;
                    return;
                case 30:
                    Require("album.png");
                    Require("chronicle.png");
                    Phase2Start();
                    Wait(0.5f);
                    phase = 31;
                    return;
                case 31:
                    if (!Phase2FilesReady()) return;
                    Phase2Historic();
                    Wait(0.5f);
                    phase = 81;
                    return;
                // Places of memory: pins on the map, their menu, then and now.
                case 81:
                    PlacesChecks();
                    Places.Show = true;
                    CloseLog();
                    Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 22f);
                    Wait(1.5f);
                    phase = 82;
                    return;
                case 82:
                    Shot("places.png");
                    Wait(1.5f);
                    phase = 83;
                    return;
                case 83:
                    Require("places.png");
                    if (Places.PinsOnScreen(map).Count == 0) throw new InvalidOperationException("no pins on screen with the places switched on (" + Places.On(map).Count + " photos on this map)");
                    checks.Add("PASS: places of memory drawn — " + Places.PinsOnScreen(map).Count + " pins on screen for " + Places.On(map).Count + " photos on this map (places.png)");
                    Find.WindowStack.Add(new Dialog_ThenNow(testRec, reshootRec));   // r109: opened in the same step as the pins shot, it covered them
                    Wait(1.5f);
                    phase = 85;
                    return;
                case 85:
                    Shot("then-now.png");
                    Wait(1f);
                    phase = 84;
                    return;
                case 84:
                    Require("then-now.png");
                    foreach (var w in Find.WindowStack.Windows.OfType<Dialog_ThenNow>().ToList()) w.Close(false);
                    Places.Show = false;   // later screenshots without pins; switched on again for the save/load check
                    checks.Add("PASS: then and now opens side by side for '" + testRec.title + "' and its re-shoot (then-now.png)");
                    phase = 140;
                    return;
                case 140:
                    // p1's chronicle again, scrolled to the end: the lines without a photo (LifeEventsCheck) between the photos.
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(p1, false);
                    if (!(InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Chronicle)) is ITab_Pawn_Chronicle lifeTab)) throw new InvalidOperationException("chronicle tab could not be opened again for " + p1.LabelShort);
                    HarmonyLib.Traverse.Create(lifeTab).Field("scroll").SetValue(new Vector2(0f, 100000f));
                    Find.CameraDriver.SetRootPosAndSize(p1.DrawPos, 14f);
                    CloseLog();
                    Wait(1.5f);
                    phase = 141;
                    return;
                case 141:
                    Shot("pawn-tab-life.png");
                    Wait(1f);
                    phase = 142;
                    return;
                case 142:
                    Require("pawn-tab-life.png");
                    checks.Add("PASS: " + p1.LabelShort + "'s chronicle with " + PhotoStore.Instance.LifeOf(p1).Count() + " lines without a photo, scrolled to the end (pawn-tab-life.png)");
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    Find.Selector.ClearSelection();
                    phase = 143;
                    return;
                case 143:
                    // A disaster photo kept for a look: toxic fallout on the map by day, photographed while it lasts (its colour must be in the picture).
                    falloutCondition = GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed("ToxicFallout"), GenDate.TicksPerDay);
                    map.gameConditionManager.RegisterCondition(falloutCondition);
                    falloutCondition.startTick = Find.TickManager.TicksGame - 3 * GenDate.TicksPerHour;
                    Conditions.ProbeHour = 12;
                    HarmonyLib.Traverse.Create(typeof(Conditions)).Field("lastCheck").SetValue(-99999);
                    Conditions.Poll(PhotoStore.Instance, Find.TickManager.TicksGame);
                    Conditions.ProbeHour = -1;
                    falloutRec = PhotoStore.Instance.Records.LastOrDefault(r => r.kind == Conditions.KindDisaster && r.eventKey.Contains("ToxicFallout"));
                    falloutUntil = Time.realtimeSinceStartup + 15f;
                    Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 30f);
                    Wait(1f);
                    phase = 144;
                    return;
                case 144:
                    if (falloutRec != null && !falloutRec.HasFile && Time.realtimeSinceStartup < falloutUntil) return;
                    if (falloutRec != null && falloutRec.HasFile)
                    {
                        File.Copy(PhotoStore.Instance.FilePath(falloutRec), Path.Combine(dir, "disaster-photo.png"), true);
                        checks.Add("PASS: a toxic fallout photographed while it lasts: '" + falloutRec.title + "' (disaster-photo.png)");
                        // The same place, same moment, as an ordinary photo (bright-photos light) to compare colours with.
                        falloutBase = PhotoStore.Instance.Request(map, falloutRec.center, Kinds.Test, "probe:fallout-baseline", "Probe fallout baseline", "",
                            map.mapPawns.FreeColonistsSpawned, falloutRec.spanCells, falloutRec.Aspect);
                    }
                    else checks.Add("FAIL: no toxic fallout photo while it lasted (" + (falloutRec == null ? "not requested" : "not captured in 15 s") + ")");
                    falloutUntil = Time.realtimeSinceStartup + 15f;
                    phase = 145;
                    return;
                case 145:
                    if (falloutBase != null && !falloutBase.HasFile && Time.realtimeSinceStartup < falloutUntil) return;
                    if (falloutBase != null && falloutBase.HasFile) File.Copy(PhotoStore.Instance.FilePath(falloutBase), Path.Combine(dir, "disaster-baseline.png"), true);
                    falloutCondition?.End();
                    phase = 32;
                    return;
                case 32:
                    if (!Phase2HistoricFilesReady()) return;
                    Phase2Letter();
                    Wait(1.5f);
                    phase = 33;
                    return;
                case 33:
                    CloseLog();
                    Shot("letter.png");
                    Wait(1f);
                    phase = 34;
                    return;
                case 34:
                    Require("letter.png");
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    Find.WindowStack.TryRemove(typeof(Dialog_NodeTreeWithFactionInfo), false);
                    Find.WindowStack.TryRemove(typeof(Dialog_NodeTree), false);
                    phase = 35;
                    return;
                // Phase 3: reminiscing (hung-photo index, "remember when?" talk, recreation job), before the save so the memories are saved too.
                case 35:
                    Phase3Start();
                    Wait(0.2f);
                    phase = 36;
                    return;
                case 36:
                    if (!Phase3ViewProgress()) return;
                    Wait(1.2f);
                    phase = 37;
                    return;
                case 37:
                    CloseLog();
                    Shot("reminisce.png");
                    Wait(1f);
                    phase = 38;
                    return;
                case 38:
                    Require("reminisce.png");
                    Phase3Finish();
                    Wait(0.2f);
                    phase = 55;
                    return;
                case 55:
                    if (!Phase3FastJoyProgress()) return;
                    Phase4Start();
                    Wait(1.5f);
                    phase = 39;
                    return;
                // Phase 4: collage board, projector, memorial stand.
                case 39:
                    CloseLog();
                    Shot("collage.png");
                    if (!PhotoCapture.Capture(map, collage.Position, 7, r =>
                        {
                            if (r?.png != null) File.WriteAllBytes(Path.Combine(dir, "collage-closeup.png"), r.png);
                        }))
                        throw new InvalidOperationException("collage close-up capture could not start");
                    Wait(2f);
                    phase = 78;
                    return;
                case 78:
                    Find.WindowStack.Add(new Dialog_MFSlotCrop(collage, 2));
                    Wait(1f);
                    phase = 79;
                    return;
                case 79:
                    Shot("collage-crop.png");
                    Wait(1f);
                    phase = 80;
                    return;
                case 80:
                    Require("collage-crop.png");
                    foreach (var w in Find.WindowStack.Windows.OfType<Dialog_MFSlotCrop>().ToList()) w.Close(false);
                    if (collage.CropAt(2) != FrameLook.CropStart) throw new InvalidOperationException("opening and closing the picker changed the chosen part: " + collage.CropAt(2));
                    checks.Add("PASS: the visible-part picker opens for the portrait place (collage-crop.png) and closing it keeps the choice");
                    phase = 40;
                    return;
                case 40:
                    Require("collage.png");
                    Require("collage-closeup.png");
                    Find.CameraDriver.SetRootPosAndSize(projector.DrawPos + new Vector3(0f, 0f, 1.5f), 8f);
                    Wait(1.5f);
                    phase = 41;
                    return;
                case 41:
                    CloseLog();
                    Shot("projector.png");
                    Wait(1f);
                    phase = 42;
                    return;
                case 42:
                    Require("projector.png");
                    if (!Phase4Memorial()) return;
                    Wait(1.5f);
                    phase = 43;
                    return;
                case 43:
                    CloseLog();
                    Shot("memorial.png");
                    Wait(1f);
                    phase = 44;
                    return;
                case 44:
                    Require("memorial.png");
                    phase = 45;
                    return;
                case 45:
                    if (!Phase4Wait()) return;
                    phase = 150;
                    return;
                // Memory night at the phase-4 projector, and the year's letter.
                case 150:
                    MemoryNightStart();
                    Wait(1f);
                    phase = 151;
                    return;
                case 151:
                    CloseLog();
                    Shot("memory-night.png");
                    Wait(1f);
                    phase = 152;
                    return;
                case 152:
                    Require("memory-night.png");
                    MemoryNightToPhoto();
                    nightUntil = Time.realtimeSinceStartup + 15f;
                    phase = 153;
                    return;
                case 153:
                    // The store starts a queued capture from its per-frame update, so the photo is taken here, between frames, with the
                    // audience still seated — not after the night has been ticked to its end (r134).
                    if (nightRec != null && !nightRec.HasFile && Time.realtimeSinceStartup < nightUntil) return;
                    MemoryNightFinish();
                    MemoryNightPhotoAndYear();
                    phase = 46;
                    return;
                // Phase 5: fading (pure function + a measurable close-up shift) and relic photos from other games' folders.
                case 46:
                    Phase5Start();
                    Wait(1.5f);
                    phase = 47;
                    return;
                case 47:
                    CloseLog();
                    Shot("relics.png");
                    Wait(1f);
                    phase = 48;
                    return;
                case 48:
                    Require("relics.png");
                    Find.WindowStack.TryRemove(typeof(Dialog_ImportRelics));
                    Phase5Import();
                    // Far variant: the camera stays at the memorial stand; only the expanded view rect can get the frame drawn.
                    CaptureDiag("before far capture");
                    FrameCloseup("fade-far-before.png");
                    Wait(2f);
                    phase = 49;
                    return;
                case 49:
                    Require("fade-far-before.png");
                    CaptureDiag("far-before");
                    OffscreenDrawn("fade-far-before.png");
                    Fading.PreviewYears = 10;
                    FrameCloseup("fade-far-after.png");
                    Wait(2f);
                    phase = 50;
                    return;
                case 50:
                    Require("fade-far-after.png");
                    CaptureDiag("far-after");
                    OffscreenDrawn("fade-far-after.png");
                    Fading.PreviewYears = 0;
                    frame.Layout(out _, out _, out Vector3 off50);
                    Find.CameraDriver.SetRootPosAndSize(frame.DrawPos + off50, 11f);
                    Wait(1.5f);
                    phase = 51;
                    return;
                case 51:
                    // Near variant: the camera on the frame (the framing every earlier close-up used).
                    CaptureDiag("before near capture");
                    FrameCloseup("fade-before.png");
                    Wait(2f);
                    phase = 52;
                    return;
                case 52:
                    Require("fade-before.png");
                    CaptureDiag("near-before");
                    Fading.PreviewYears = 10;
                    FrameCloseup("fade-after.png");
                    Wait(2f);
                    phase = 53;
                    return;
                case 53:
                    Require("fade-after.png");
                    CaptureDiag("near-after");
                    CloseLog();
                    Shot("fade.png");
                    Wait(1f);
                    phase = 54;
                    return;
                case 54:
                    Require("fade.png");
                    Phase5Compare();
                    Phase6Start();
                    Wait(0.5f);
                    phase = 56;
                    return;
                // Phase 6: couple-framed wedding, resurrection, relic installed (Ideology), monolith advanced (Anomaly); their captures must finish before the save.
                case 56:
                    if (!Phase6Wait()) return;
                    partyStep = 0;
                    phase = 58;
                    return;
                // A party photographed part-way through (in the album at once) and one whose lord vanishes (the photo stays).
                case 58:
                    if (!RunParty()) return;
                    Phase7Start();
                    Wait(0.2f);
                    phase = 57;
                    return;
                // Phase 7: group photo — the spot, the line-up behind it, a mid-way drop-out, the shot, then a second gathering cancelled from the spot.
                case 57:
                    if (!Phase7Run()) return;
                    group2Step = 0;
                    phase = 59;
                    return;
                case 59:
                    if (!Phase7Second()) return;
                    phase = 10;
                    return;
                case 10:
                    SaveGame();
                    Wait(2f);
                    phase = 11;
                    return;
                case 11:
                    LoadGame();
                    Wait(3f);
                    phase = 12;
                    deadline = Time.realtimeSinceStartup + 180f;
                    return;
                case 12:
                    VerifyAfterLoad();
                    postLoadBusy = 0;
                    phase = 77;
                    return;
                case 77:
                    // r101: another capture was still running right after the load (Busy) and the close-up was refused — wait for it.
                    if (PhotoCapture.Busy)
                    {
                        if (++postLoadBusy > 600) throw new TimeoutException("a capture stayed busy after the load; records " + PhotoStore.Instance.Records.Count + " vs saved " + savedRecordCount + ", pending " + PhotoStore.Instance.HasPending);
                        return;
                    }
                    if (postLoadBusy > 0)
                    {
                        var extra = PhotoStore.Instance.Records.Skip(savedRecordCount).Select(r => r.kind + " '" + r.title + "'").ToList();
                        checks.Add("INFO: after the load another capture ran for " + postLoadBusy + " updates before the close-up" + (extra.Count > 0 ? " — new since the save: " + string.Join(", ", extra) : ""));
                    }
                    PostLoadCloseup();
                    Wait(2f);
                    phase = 13;
                    return;
                case 13:
                    Require("frame-closeup-after-load.png");
                    phase = 1000;
                    return;
            }
        }

        private static string savedGameId;
        private static int postLoadBusy;
        private static int savedRecordCount;
        private static string savedFramePhotoId;
        private static IntVec3 savedFrameCell;
        private static string savedWeddingFile;

        private static void SaveGame()
        {
            var store = PhotoStore.Instance;
            savedGameId = store.GameId;
            savedRecordCount = store.Records.Count;
            savedFramePhotoId = frame.photoId;
            Places.Show = true;   // the switch is saved with the colony
            savedFrameCell = frame.Position;
            savedWeddingFile = store.Records.First(r => r.kind == "wedding").file;
            savedP1Id = p1.GetUniqueLoadID();
            if (photoSpot != null) savedSpotCell = photoSpot.Position;
            SaveDescriptionFacts();
            GameDataSaveLoader.SaveGame("mfprobe");
            string path = GenFilePaths.FilePathForSavedGame("mfprobe");
            if (!File.Exists(path)) throw new InvalidOperationException("save file missing: " + path);
            checks.Add("PASS: game saved (" + new FileInfo(path).Length + " bytes)");
        }

        private static void LoadGame()
        {
            // Real load path: new Game, new maps, new PhotoStore, frames restored through Scribe.
            frame = null;
            map = null;
            GameDataSaveLoader.LoadGame("mfprobe");
        }

        private static void VerifyAfterLoad()
        {
            var store = PhotoStore.Instance;
            map = Find.CurrentMap;
            Find.Storyteller.storytellerComps.Clear();   // the load rebuilt them from the def
            Find.Storyteller.incidentQueue.Clear();
            if (store.GameId != savedGameId) throw new InvalidOperationException("gameId changed after load: " + store.GameId + " vs " + savedGameId);
            if (store.Records.Count != savedRecordCount) throw new InvalidOperationException("record count after load: " + store.Records.Count + " vs " + savedRecordCount);
            CheckDescriptionFacts();
            var wed = store.Records.FirstOrDefault(r => r.kind == "wedding");
            if (wed == null || wed.file != savedWeddingFile || !File.Exists(store.FilePath(wed))) throw new InvalidOperationException("wedding record/file lost after load");
            frame = savedFrameCell.GetThingList(map).OfType<Building_PhotoFrame>().FirstOrDefault();
            if (frame == null) throw new InvalidOperationException("frame not found at " + savedFrameCell + " after load");
            if (frame.photoId != savedFramePhotoId) throw new InvalidOperationException("frame photoId after load: " + frame.photoId + " vs " + savedFramePhotoId);
            frame.RefreshBinding();
            if (!frame.HasPhotoTexture) throw new InvalidOperationException("frame photo texture not bound after load");
            checks.Add("PASS: after real save/load — gameId, " + store.Records.Count + " records, wedding file, frame photoId + texture all restored");
            if (!Places.Show) throw new InvalidOperationException("the places-of-memory switch was not kept by the save");
            Places.Show = false;
            checks.Add("PASS: after load — the places-of-memory switch kept");
            if (polaroidCell.IsValid)
            {
                var pol = polaroidCell.GetThingList(map).OfType<Building_PhotoFrame>().FirstOrDefault();
                if (pol == null || pol.Style != FrameStyle.Polaroid) throw new InvalidOperationException("the chosen frame style after load: " + (pol == null ? "no frame" : pol.Style.ToString()));
                checks.Add("PASS: after load — the chosen frame style survived (polaroid)");
            }
            if (reshootRec != null)
            {
                var rs = store.Get(reshootRec.id);
                if (rs == null || rs.reshootOf != testRec.id) throw new InvalidOperationException("reshoot link lost after load");
                if (Find.LetterStack.LettersListForReading.OfType<Letter_MFPhoto>().All(l => l.photoId != synthRec.id)) throw new InvalidOperationException("photo letter lost after load");
                checks.Add("PASS: after load — reshoot link and the photo letter survived");
            }
            if (savedP1Id != null)
            {
                var q = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == savedP1Id);
                var t = q?.needs.mood.thoughts.memories.GetFirstMemoryOfDef(DefDatabase<ThoughtDef>.GetNamed(Reminisce.ThoughtTogether)) as Thought_MFPhoto;
                if (t == null || t.title != savedTogetherTitle) throw new InvalidOperationException("remember-when memory lost after load (pawn " + (q != null) + ", memory " + (t != null) + ")");
                int hung = Reminisce.DisplayedFrames(map).Count;
                if (hung == 0) throw new InvalidOperationException("no hung photos indexed after load");
                checks.Add("PASS: after load — 'remember when' memory kept its caption, " + hung + " hung photos re-indexed");
            }
            if (savedDiverId != null)
            {
                var col = savedCollageCell.GetThingList(map).OfType<Building_PhotoCollage>().FirstOrDefault();
                if (col == null) throw new InvalidOperationException("collage not found after load");
                col.RefreshBinding();
                if (col.FilledCount != 4 || col.BoundCount != 4) throw new InvalidOperationException("collage after load: filled " + col.FilledCount + ", bound " + col.BoundCount);
                if (col.CropAt(2) != FrameLook.CropStart) throw new InvalidOperationException("collage after load: place 3 shows part " + col.CropAt(2) + " instead of the chosen top");
                var mem = savedMemorialCell.GetThingList(map).OfType<Building_MemorialFrame>().FirstOrDefault();
                if (mem == null) throw new InvalidOperationException("memorial not found after load");
                var r = mem.Record;
                if (r == null || !mem.Derived || !r.peopleIds.Contains(savedDiverId)) throw new InvalidOperationException("memorial after load: " + (r == null ? "no photo" : r.title));
                checks.Add("PASS: after load — collage 4 photos bound (place 3 still shows the top part chosen), memorial stand re-derived '" + r.title + "' from the grave");
            }
            ZoomChecks();
            ReunionChecks();
            if (journeyTile.Valid)
            {
                if (!store.Visited(journeyTile) || !store.Visited(map.Tile)) throw new InvalidOperationException("journey places after load: " + store.Visits.Count);
                var jv = store.Visits.First(v => v.tile == journeyTile);
                if (jv.photoId.NullOrEmpty() || store.Get(jv.photoId) == null) throw new InvalidOperationException("the place lost its photo after load");
                checks.Add("PASS: after load — the journey kept its " + store.Visits.Count + " places and the new place's photo");
            }
            {
                var t = store.Get(testRec.id);
                var kept = t?.edits.FirstOrDefault();
                if (t == null || t.edits.Count != 1 || kept == null || !File.Exists(store.EditPath(kept)) || t.Shown != null || kept.filter != PhotoFilters.Sepia)
                    throw new InvalidOperationException("edits after load: " + (t == null ? "record missing" : t.edits.Count + " edits, shown " + t.shownEdit));
                checks.Add("PASS: after load — the photo keeps its edited version (sepia, file on disk) and is still shown as the original");
            }
            if (relicRec != null)
            {
                var rr = store.Get(relicRec.id);
                if (rr == null || rr.kind != Kinds.Relic || rr.peopleIds.Count != 0 || !File.Exists(store.FilePath(rr))) throw new InvalidOperationException("relic record after load: " + (rr == null ? "missing" : rr.kind + " people " + rr.peopleIds.Count));
                string index = Path.Combine(store.FolderPath, RelicImport.IndexFile);
                if (!File.Exists(index)) throw new InvalidOperationException("index.tsv not written on save");
                var self = RelicImport.Read(store.FolderPath, store.GameId);
                int withFile = store.Records.Count(x => x.HasFile);
                if (self == null || !self.fromIndex || self.photos.Count != withFile) throw new InvalidOperationException("index.tsv lists " + self?.photos.Count + " of " + withFile + " photos");
                if (self.photos.All(p => p.title != rr.title)) throw new InvalidOperationException("index.tsv lacks the relic's title");
                File.Copy(index, Path.Combine(dir, "index.tsv"), true);
                checks.Add("PASS: after load — relic record kept (kind relic, no people); this game's index.tsv lists all " + self.photos.Count + " photos with titles for a future colony");
            }
            if (groupRec != null)
            {
                var gr = store.Get(groupRec.id);
                if (gr == null || gr.kind != GroupPhoto.Kind || !File.Exists(store.FilePath(gr))) throw new InvalidOperationException("group record after load: " + (gr == null ? "missing" : gr.kind));
                var spotAfter = savedSpotCell.GetFirstThing<Building_PhotoSpot>(map);
                if (spotAfter == null) throw new InvalidOperationException("photo spot lost after load");
                if (!spotAfter.TryGetFrame(out Vector3 lc, out float ls, out float la) || (lc - savedFrameCenter).magnitude > 0.01f || Mathf.Abs(ls - savedFrameSpan) > 0.01f || Mathf.Abs(la - savedFrameAspect) > 0.01f)
                    throw new InvalidOperationException("the spot's framing after load: framed " + spotAfter.Framed + ", " + lc + " / " + ls + " / " + la + " vs " + savedFrameCenter + " / " + savedFrameSpan + " / " + savedFrameAspect);
                checks.Add("PASS: after load — the photo spot kept the framing the player dragged (span " + ls.ToString("0.#") + ", aspect " + la.ToString("0.00") + ")");
                spotAfter.ClearFrame();
                if (spotAfter.Framed || spotAfter.FrameCells != null) throw new InvalidOperationException("'automatic framing' did not clear the framing");
                if (map.lordManager.lords.Any(l => l.LordJob is LordJob_MFGroupPhoto)) throw new InvalidOperationException("a finished group-photo lord survived the load");
                checks.Add("PASS: after load — group photo record + file kept, photo spot still on the map, no stray lord");
            }
            Find.TickManager.Pause();
        }

        private static void PostLoadCloseup()
        {
            Find.CameraDriver.SetRootPosAndSize(frame.DrawPos + new Vector3(0f, 0f, 0.7f), 11f);
            if (!PhotoCapture.Capture(map, frame.Position, 7, r =>
                {
                    if (r?.png != null) File.WriteAllBytes(Path.Combine(dir, "frame-closeup-after-load.png"), r.png);
                }))
                throw new InvalidOperationException("post-load close-up capture could not start (busy " + PhotoCapture.Busy + ", current map " + (Find.CurrentMap == map) + ")");
        }

        /// <summary>Every language file of the mod parses as XML (dev, 2026-09-23: an unescaped '&amp;' in the English file made RimWorld drop the whole
        /// English translation — every label showed its key — and the Korean runs could not see it).</summary>
        private static void LanguageFilesParse()
        {
            string root = LoadedModManager.GetMod<MomentFramesMod>().Content.RootDir;
            int n = 0;
            foreach (var path in Directory.GetFiles(Path.Combine(root, "Languages"), "*.xml", SearchOption.AllDirectories))
            {
                try { new System.Xml.XmlDocument().Load(path); n++; }
                catch (Exception e) { throw new InvalidOperationException("language file does not parse: " + path + " — " + e.Message); }
            }
            if (n == 0) throw new InvalidOperationException("no language files found under " + root);
            checks.Add("PASS: all " + n + " language files parse as XML");
        }

        /// <summary>No hostile creatures on the probe map, and the danger rating recomputed at once (r90: a quick-test map came with an insect hive —
        /// spelopedes and megascarabs — and the High danger rating calls a party off on its first tick; the cause of the party failures of
        /// r65, r66 and r78 as well).</summary>
        private static void ClearHostiles(Map m)
        {
            int n = 0;
            foreach (var p in m.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)).ToList()) { p.Destroy(DestroyMode.Vanish); n++; }
            foreach (var h in m.listerThings.ThingsOfDef(ThingDefOf.Hive).ToList()) { h.Destroy(DestroyMode.Vanish); n++; }
            var w = HarmonyLib.Traverse.Create(m.dangerWatcher);
            w.Field("lastUpdateTick").SetValue(-10000);
            w.Field("lastColonistHarmedTick").SetValue(-10000);
            if (n > 0) checks.Add("INFO: cleared " + n + " hostile creatures/hives from the probe map; danger now " + m.dangerWatcher.DangerRating);
        }

        private static void Setup()
        {
            map = Find.CurrentMap;
            LanguageFilesParse();
            center = map.Center;
            Find.TickManager.Pause();
            CloseLog();
            // No storyteller: the probe runs tens of thousands of ticks, and a random raid or manhunter pack raises the danger rating, which
            // calls a party off on its first tick (r65, r66, r78) and stops pawns from chatting (r77). Checks that need a threat make their own.
            Find.Storyteller.storytellerComps.Clear();
            Find.Storyteller.incidentQueue.Clear();
            ClearHostiles(map);
            checks.Add("INFO: storyteller incidents off for the probe (random raids and manhunters made the party and chat phases flaky)");
            int foggedBefore = 0;
            for (int x = center.x - 10; x <= center.x + 10; x++)
                for (int z = center.z - 7; z <= center.z + 7; z++)
                    if (map.fogGrid.IsFogged(new IntVec3(x, 0, z))) foggedBefore++;
            map.fogGrid.ClearAllFog();
            checks.Add("INFO: fogged cells in capture rect before ClearAllFog = " + foggedBefore);
            // Clear a 40x24 area (keep pawns), plain soil under it, unfog it. The soil: every run is a new random map, and fixed placements met
            // the sea (r100), then ground too weak for a reliquary (r103).
            for (int x = center.x - 20; x <= center.x + 20; x++)
                for (int z = center.z - 12; z <= center.z + 12; z++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (!c.InBounds(map)) continue;
                    foreach (var t in c.GetThingList(map).ToList())
                        if (!(t is Pawn) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                    map.roofGrid.SetRoof(c, null);
                    map.fogGrid.Unfog(c);
                }
            // Wood wall line along the north edge of the photo area (z = center.z + 6), 21 cells wide.
            for (int x = center.x - 10; x <= center.x + 10; x++)
            {
                var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                wall.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(wall, new IntVec3(x, 0, center.z + 6), map);
            }
            // Marriage spot at the center.
            var spot = ThingMaker.MakeThing(ThingDef.Named("MarriageSpot"));
            spot.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(spot, center, map);
            // Frame hanging on the wall (cell just south of the wall, facing north).
            frame = (Building_PhotoFrame)ThingMaker.MakeThing(ThingDef.Named("MF_PhotoFrame"), ThingDefOf.WoodLog);
            frame.SetFaction(Faction.OfPlayer);
            var frameCell = new IntVec3(center.x + 4, 0, center.z + 5);
            var report = ThingDef.Named("MF_PhotoFrame").PlaceWorkers[0].AllowsPlacing(ThingDef.Named("MF_PhotoFrame"), frameCell, Rot4.North, map);
            if (!report.Accepted) throw new InvalidOperationException("frame placement rejected: " + report.Reason);
            GenSpawn.Spawn(frame, frameCell, map, Rot4.North);
            checks.Add("PASS: frame placed against wall via PlaceWorker_MFWallFrame");
            var bad = ThingDef.Named("MF_PhotoFrame").PlaceWorkers[0].AllowsPlacing(ThingDef.Named("MF_PhotoFrame"), new IntVec3(center.x - 4, 0, center.z - 3), Rot4.North, map);
            if (bad.Accepted) throw new InvalidOperationException("frame placement away from wall was accepted");
            checks.Add("PASS: frame placement away from wall rejected");
            CraftedFrames();
            // Colonists near the spot; first two become fiances.
            var colonists = map.mapPawns.FreeColonistsSpawned.ToList();
            if (colonists.Count < 2) throw new InvalidOperationException("need 2 colonists, have " + colonists.Count);
            p1 = colonists[0];
            p2 = colonists[1];
            for (int i = 0; i < colonists.Count; i++)
            {
                var c = center + new IntVec3(-2 + i * 2, 0, -2);
                colonists[i].Position = c;
                colonists[i].Notify_Teleported(true, true);
            }
            Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 14f);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            Find.TickManager.Pause();
            map.mapDrawer.RegenerateEverythingNow();
        }

        // ---------- frame looks: hung on the wall cell, five styles, a click on the wall picks the frame ----------

        private static IntVec3 polaroidCell = IntVec3.Invalid;

        /// <summary>An edited version's own title and note (user, 2026-09-26). The dialogs on an edit change the edit's, never the photo's; the
        /// version shown everywhere lends its title and note to the album, frames and the HTML; emptied, the version falls back to the photo's.
        /// Leaves the gallery on the edit with its title, for the screenshot.</summary>
        private static void VersionTextChecks()
        {
            var store = PhotoStore.Instance;
            var e = testRec.edits.FirstOrDefault(x => x.HasFile);
            if (e == null) throw new InvalidOperationException("no edited version of '" + testRec.title + "' to name");
            string photoTitle = testRec.title, photoDesc = testRec.desc, shownBefore = testRec.shownEdit;
            var rename = new Dialog_RenamePhoto(testRec, e);
            HarmonyLib.Traverse.Create(rename).Field("text").SetValue("Edited version title");
            HarmonyLib.Traverse.Create(rename).Method("Apply").GetValue();
            e.note = "Edited version note";
            if (testRec.title != photoTitle || testRec.desc != photoDesc || e.title != "Edited version title")
                throw new InvalidOperationException("renaming the edit: photo '" + testRec.title + "' (was '" + photoTitle + "'), edit '" + e.title + "'");
            if (testRec.TitleOf(e.id) != e.title || testRec.TitleOf("") != photoTitle || testRec.DescOf(e.id) != e.note || testRec.DescOf(null) != photoDesc)
                throw new InvalidOperationException("TitleOf/DescOf: " + testRec.TitleOf(e.id) + " / " + testRec.TitleOf("") + " / " + testRec.DescOf(e.id));
            store.SetShown(testRec, e.id);
            if (testRec.DisplayTitle != e.title || testRec.DisplayDesc != e.note) throw new InvalidOperationException("the shown edit's title does not lead: " + testRec.DisplayTitle);
            store.SetShown(testRec, shownBefore.NullOrEmpty() ? null : shownBefore);
            var clear = new Dialog_RenamePhoto(testRec, e);
            HarmonyLib.Traverse.Create(clear).Field("text").SetValue("");
            HarmonyLib.Traverse.Create(clear).Method("Apply").GetValue();
            if (e.title != null || testRec.TitleOf(e.id) != photoTitle || testRec.title != photoTitle) throw new InvalidOperationException("an emptied edit title did not fall back: '" + e.title + "'");
            checks.Add("PASS: an edited version's own title and note — the dialog on the edit changes only the edit's; the shown edit's title and note lead in the album/frames/HTML; emptied, it falls back to the photo's ('" + photoTitle + "')");
            e.title = "Edited version title";
            MainTabWindow_Album.ShowVersion(testRec, e.id);
        }

        private static void AlbumWindowStart()
        {
            var w = (MainTabWindow_Album)AlbumDef.TabWindow;
            if (!w.draggable || !w.resizeable || w.doWindowBackground) throw new InvalidOperationException("the album window: draggable " + w.draggable + ", resizeable " + w.resizeable + ", own background " + !w.doWindowBackground);
            MomentFramesMod.Settings.albumOpacity = 0.45f;
            w.windowRect = new Rect(60f, 60f, MainTabWindow_Album.MinSize.x, MainTabWindow_Album.MinSize.y);   // the narrowest it can be, see-through
        }

        /// <summary>Closed and opened again, the album comes back where and as big as it was left; the reset puts it back. Clears the probe's edit title.</summary>
        private static void AlbumWindowKept()
        {
            var s = MomentFramesMod.Settings;
            var w = (MainTabWindow_Album)AlbumDef.TabWindow;
            Find.MainTabsRoot.EscapeCurrentTab(false);
            if (Mathf.Abs(s.albumX - 60f) > 0.5f || Mathf.Abs(s.albumW - MainTabWindow_Album.MinSize.x) > 0.5f) throw new InvalidOperationException("closing did not keep the place: " + s.albumX + "," + s.albumY + " " + s.albumW + "x" + s.albumH);
            Find.MainTabsRoot.SetCurrentTab(AlbumDef, false);
            if (Mathf.Abs(w.windowRect.x - 60f) > 0.5f || Mathf.Abs(w.windowRect.width - MainTabWindow_Album.MinSize.x) > 0.5f) throw new InvalidOperationException("reopened at " + w.windowRect);
            HarmonyLib.Traverse.Create(w).Method("ResetPlacement").GetValue();
            if (s.albumW >= 0f || Mathf.Abs(w.windowRect.width - w.RequestedTabSize.x) > 0.5f) throw new InvalidOperationException("reset: settings " + s.albumW + ", window " + w.windowRect);
            s.albumOpacity = 1f;
            checks.Add("PASS: the album is a window of its own — draggable, resizable, see-through (album-narrow.png at its minimum " + MainTabWindow_Album.MinSize + ", 45%); closed and reopened it comes back at the same place and size; the reset puts it back");
            var e = testRec.edits.FirstOrDefault(x => x.HasFile);
            if (e != null) { e.title = null; e.note = null; }
        }

        /// <summary>The RimTalk bridge. Always: a pictured colonist's line names the photo, how long ago and who else is in it; someone in no photo
        /// gets none. With RimTalk active (-ExtraMods cj.rimtalk): the section is registered, and RimTalk's own registry hands back our provider
        /// after its Thoughts section, giving the same line. Without it: not registered, quietly.</summary>
        private static void RimTalkCheck(PhotoRecord wedding)
        {
            var groom = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => wedding.Has(p));
            if (groom == null) throw new InvalidOperationException("nobody of '" + wedding.title + "' on the map");
            string line = RimTalkBridge.Section(groom);
            if (line.NullOrEmpty() || !line.Contains(groom.LabelShort)) throw new InvalidOperationException("RimTalk line for " + groom.LabelShort + ": '" + line + "'");
            var latest = PhotoStore.Instance.Records.Where(r => r.HasFile && r.kind != Kinds.Test && r.Has(groom)).OrderByDescending(r => r.ticksAbs).FirstOrDefault();
            if (latest == null || !line.Contains(latest.title)) throw new InvalidOperationException("RimTalk line misses the latest photo '" + latest?.title + "': " + line);
            var nobody = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            if (!RimTalkBridge.Section(nobody).NullOrEmpty()) throw new InvalidOperationException("a colonist in no photo got a RimTalk line");
            if (!ModsConfig.IsActive(RimTalkBridge.PackageId))
            {
                if (RimTalkBridge.Registered) throw new InvalidOperationException("registered with RimTalk although it is not active");
                checks.Add("PASS: RimTalk line — " + line + " | RimTalk not active: nothing registered (" + RimTalkBridge.Why + ")");
                return;
            }
            if (!RimTalkBridge.Registered) throw new InvalidOperationException("RimTalk is active but the bridge is not registered: " + RimTalkBridge.Why);
            Type registry = HarmonyLib.AccessTools.TypeByName("RimTalk.API.ContextHookRegistry");
            Type pawnCats = HarmonyLib.AccessTools.TypeByName("RimTalk.API.ContextCategories+Pawn");
            object thoughts = HarmonyLib.AccessTools.Field(pawnCats, "Thoughts").GetValue(null);
            var sections = (System.Collections.IEnumerable)HarmonyLib.AccessTools.Method(registry, "GetInjectedSectionsAt").Invoke(null, new[] { thoughts });
            Func<Pawn, string> ours = null;
            foreach (var s in sections)
            {
                var t = HarmonyLib.Traverse.Create(s);
                if (t.Field("Item1").GetValue<string>() == RimTalkBridge.SectionName) ours = t.Field("Item4").GetValue<Delegate>() as Func<Pawn, string>;
            }
            if (ours == null) throw new InvalidOperationException("RimTalk's registry has no MomentFrames section after Thoughts");
            if (ours(groom) != line) throw new InvalidOperationException("RimTalk's registry gives '" + ours(groom) + "'");
            checks.Add("PASS: RimTalk bridge — registered with the active RimTalk after its Thoughts section, and RimTalk's registry gives the line: " + line);
        }

        /// <summary>Places of memory. Pure: pins stack by screen distance (two pairs within reach and a loner -> 3 pins of 2, 2, 1). This map's
        /// photos: the test photo and its re-shoot are there, every listed photo belongs to this map, has a file and a centre, and none is a
        /// relic. The pin of a photo and its re-shoot: "then" is the original, and its menu offers then-and-now and shooting it again.</summary>
        private static void PlacesChecks()
        {
            var pins = Places.Stack(new[] { (new Vector2(0f, 0f), testRec), (new Vector2(10f, 0f), reshootRec), (new Vector2(100f, 0f), testRec), (new Vector2(105f, 5f), testRec), (new Vector2(300f, 0f), testRec) });
            if (pins.Count != 3 || pins[0].photos.Count != 2 || pins[1].photos.Count != 2 || pins[2].photos.Count != 1)
                throw new InvalidOperationException("stacking: " + pins.Count + " pins of " + string.Join("/", pins.Select(p => p.photos.Count)));
            var here = Places.On(map);
            if (!here.Contains(testRec) || !here.Contains(reshootRec)) throw new InvalidOperationException("this map's places miss the test photo " + here.Contains(testRec) + " / its re-shoot " + here.Contains(reshootRec));
            var stray = here.FirstOrDefault(r => r.mapId != map.uniqueID || !r.HasFile || !r.HasCenter || r.kind == Kinds.Relic);
            if (stray != null) throw new InvalidOperationException("a photo that cannot be pinned is listed: " + stray.title + " (map " + stray.mapId + ", kind " + stray.kind + ")");
            var pin = Places.Stack(new[] { (Vector2.zero, testRec), (Vector2.zero, reshootRec) })[0];
            if (Places.Oldest(pin) != testRec) throw new InvalidOperationException("'then' of the pin is " + Places.Oldest(pin)?.title);
            var labels = Places.Options(pin).Select(o => o.Label).ToList();
            string thenNow = "MF_PlacesThenNow".Translate(testRec.title), again = "MF_PlacesReshoot".Translate(testRec.title);
            if (!labels.Contains(thenNow) || !labels.Contains(again)) throw new InvalidOperationException("pin menu: " + string.Join(" | ", labels));
            checks.Add("PASS: places of memory — pins stack by screen distance (3 pins of 2/2/1); " + here.Count + " photos pinnable on this map, all of it; the pin of '" + testRec.title
                + "' and its re-shoot takes the original as 'then' and offers then-and-now and shooting again (" + labels.Count + " menu lines)");
        }

        /// <summary>Which part of a trimmed photo a collage window shows. Pure: the three positions of a tall and a wide photo, and the suggestion
        /// following where the people stood (top, bottom, middle, unknown, right). Measured: a photo taken in this run knows where its people
        /// stood. On the board: the portrait place asks and got the suggestion when it went in, the 3:2 place does not ask, a chosen part sticks.</summary>
        private static void CollageVisiblePart(PhotoRecord withPeople)
        {
            float wa = collage.WindowAspect, tall = 2f / 3f;
            Rect tTop = FrameLook.CoverUV(tall, wa, FrameLook.CropStart), tBot = FrameLook.CoverUV(tall, wa, FrameLook.CropEnd), tMid = FrameLook.CoverUV(tall, wa);
            Rect wLeft = FrameLook.CoverUV(3f, wa, FrameLook.CropStart), wRight = FrameLook.CoverUV(3f, wa, FrameLook.CropEnd);
            if (Mathf.Abs(tTop.yMax - 1f) > 1e-4f || Mathf.Abs(tBot.yMin) > 1e-4f || Mathf.Abs(tMid.center.y - 0.5f) > 1e-4f || Mathf.Abs(wLeft.xMin) > 1e-4f || Mathf.Abs(wRight.xMax - 1f) > 1e-4f)
                throw new InvalidOperationException("visible parts: tall top " + tTop + ", bottom " + tBot + ", middle " + tMid + "; wide left " + wLeft + ", right " + wRight);
            int sTop = Building_PhotoCollage.Recommend(tall, wa, Rect.MinMaxRect(0.3f, 0.05f, 0.7f, 0.25f));
            int sBot = Building_PhotoCollage.Recommend(tall, wa, Rect.MinMaxRect(0.3f, 0.75f, 0.7f, 0.95f));
            int sMid = Building_PhotoCollage.Recommend(tall, wa, Rect.MinMaxRect(0.3f, 0.45f, 0.7f, 0.55f));
            int sNone = Building_PhotoCollage.Recommend(tall, wa, null);
            int sRight = Building_PhotoCollage.Recommend(3f, wa, Rect.MinMaxRect(0.8f, 0.3f, 0.95f, 0.7f));
            if (sTop != FrameLook.CropStart || sBot != FrameLook.CropEnd || sMid != FrameLook.CropMiddle || sNone != FrameLook.CropMiddle || sRight != FrameLook.CropEnd)
                throw new InvalidOperationException("suggestions: people at the top " + sTop + ", bottom " + sBot + ", middle " + sMid + ", unknown " + sNone + ", right of a panorama " + sRight);
            if (!withPeople.HasPeopleBox) throw new InvalidOperationException("'" + withPeople.title + "' (" + withPeople.people.Count + " people) was taken without a people box");
            var box = withPeople.ShownPeopleBox.Value;
            if (box.xMin < 0f || box.yMin < 0f || box.xMax > 1f || box.yMax > 1f || box.width <= 0f || box.height <= 0f) throw new InvalidOperationException("people box " + box);
            if (!collage.NeedsChoice(2) || collage.NeedsChoice(0)) throw new InvalidOperationException("asks: portrait place " + collage.NeedsChoice(2) + ", 3:2 place " + collage.NeedsChoice(0));
            if (collage.CropAt(2) != collage.RecommendedCrop(2)) throw new InvalidOperationException("the portrait place shows " + collage.CropAt(2) + ", suggested " + collage.RecommendedCrop(2));
            collage.SetCrop(2, FrameLook.CropStart);
            if (collage.SlotCrop(2).yMax < 0.999f) throw new InvalidOperationException("the chosen top part is not shown: " + collage.SlotCrop(2));
            checks.Add("PASS: visible part of trimmed collage photos — top/bottom/middle of a tall photo and left/right of a panorama crop right; the suggestion follows the people (top, bottom, middle, unknown -> middle, right of a panorama); '"
                + withPeople.title + "' knows where its people stood (x " + box.xMin.ToString("0.00") + "-" + box.xMax.ToString("0.00") + ", y " + box.yMin.ToString("0.00") + "-" + box.yMax.ToString("0.00")
                + "); the portrait place asks and got its suggestion, the 3:2 place does not ask; the top part chosen for place 3 is shown");
        }

        /// <summary>Is a box of height oh centred at world z inside the wall cell north of the frame's cell (a hair of slack)?</summary>
        private static bool InsideWall(Building_PhotoFrame f, float cz, float oh) => cz - oh / 2f >= f.Position.z + 1 - 0.02f && cz + oh / 2f <= f.Position.z + 2 + 0.02f;

        /// <summary>Three more wall frames, one per remaining style. Defaults: below excellent = material, excellent+ = gilded. Every north frame's outer box lies
        /// inside the wall cell it faces (the old drawing, centred 0.72 cells up, lay half on the floor — the negative control below is that
        /// position). A click on the wall behind a frame selects the frame first and still reaches the wall; a bare wall selects the wall.</summary>
        private static void FrameStyles(Building_PhotoFrame wide, Building_PhotoFrame fine)
        {
            // A journey photo is centred on the colonist nearest the group's middle, never on empty ground between two far apart.
            var two = map.mapPawns.FreeColonistsSpawned.Take(2).ToList();
            if (two.Count == 2)
            {
                IntVec3 f = Journey.Focus(two, map);
                if (f != two[0].Position && f != two[1].Position) throw new InvalidOperationException("journey focus " + f + " is not on a colonist (" + two[0].Position + ", " + two[1].Position + ")");
                checks.Add("PASS: a journey photo centres on a colonist (" + f + "), not on the ground between them");
            }
            // Auto-framed pictures stay on the map: a picture centred in a corner slides inwards; one in the middle does not move.
            Vector3 corner = Hooks.KeepInside(map, new Vector3(3f, 0f, 3f), 25f, 1.5f), mid = Hooks.KeepInside(map, map.Center.ToVector3Shifted(), 25f, 1.5f);
            if (Mathf.Abs(corner.x - 18.75f) > 0.01f || Mathf.Abs(corner.z - 12.5f) > 0.01f) throw new InvalidOperationException("corner picture centre " + corner + " (expected 18.75, 12.5)");
            if ((mid - map.Center.ToVector3Shifted()).sqrMagnitude > 1e-4f) throw new InvalidOperationException("a picture in the middle moved to " + mid);
            checks.Add("PASS: auto-framed pictures near the map edge slide inwards (corner centre 3,3 -> " + corner.x.ToString("0.00") + "," + corner.z.ToString("0.00") + " for 25 cells at 3:2); the middle stays");
            if (frame.Style != FrameStyle.Material || fine.Style != FrameStyle.Gilded || wide.Style != FrameStyle.Material)
                throw new InvalidOperationException("default styles: normal " + frame.Style + ", legendary " + fine.Style + ", wide " + wide.Style);
            var made = new List<Building_PhotoFrame>();
            var styles = new[] { FrameStyle.BlackMat, FrameStyle.Polaroid, FrameStyle.Canvas };
            var xs = new[] { -5, -1, 1 };
            for (int i = 0; i < styles.Length; i++)
            {
                var f = Spawn("MF_PhotoFrame", ThingDefOf.WoodLog, new IntVec3(center.x + xs[i], 0, center.z + 5), Rot4.North);
                f.SetPhoto(testRec.id);
                f.SetStyle(styles[i]);
                f.RefreshBinding();
                if (!f.HasPhotoTexture) throw new InvalidOperationException(styles[i] + " frame did not bind the photo");
                made.Add(f);
            }
            polaroidCell = made[1].Position;
            var all = new List<Building_PhotoFrame> { frame, wide, fine };
            all.AddRange(made);
            var dims = new List<string>();
            foreach (var f in all)
            {
                f.Outer(out float ow, out float oh, out Vector3 off);
                float cz = f.DrawPos.z + off.z;
                if (!InsideWall(f, cz, oh)) throw new InvalidOperationException(f.def.defName + " (" + f.Style + ") drawn z " + (cz - oh / 2f).ToString("0.00") + ".." + (cz + oh / 2f).ToString("0.00") + ", its wall cell " + (f.Position.z + 1) + ".." + (f.Position.z + 2));
                if (InsideWall(f, f.DrawPos.z + 0.72f, oh)) throw new InvalidOperationException("negative control: the old position (0.72 up) passes the wall check");
                dims.Add(f.Style + " " + ow.ToString("0.00") + "x" + oh.ToString("0.00"));
            }
            checks.Add("PASS: wall frames drawn inside the wall cell they face — " + string.Join(", ", dims) + " (the old 0.72-up position fails the same check); defaults normal = material, legendary = gilded");
            var parms = new TargetingParameters { mustBeSelectable = true, canTargetPawns = true, canTargetBuildings = true, canTargetItems = true, mapObjectTargetsMustBeAutoAttackable = false };
            foreach (var f in new[] { made[0], wide })
            {
                var under = GenUI.ThingsUnderMouse((f.Position + IntVec3.North).ToVector3Shifted(), 1f, parms);
                if (under.Count == 0 || under[0] != f) throw new InvalidOperationException("a click on the wall behind " + f.def.defName + " picks " + string.Join(", ", under.Select(t => t.def.defName)));
                if (!under.Any(t => t.def == ThingDefOf.Wall)) throw new InvalidOperationException("the wall behind " + f.def.defName + " can no longer be selected");
            }
            var bare = GenUI.ThingsUnderMouse(new IntVec3(center.x - 10, 0, center.z + 6).ToVector3Shifted(), 1f, parms);
            if (bare.Count == 0 || bare[0].def != ThingDefOf.Wall) throw new InvalidOperationException("a bare wall click picks " + string.Join(", ", bare.Select(t => t.def.defName)));
            checks.Add("PASS: a click on the wall behind a frame selects the frame first, the wall next; a bare wall selects the wall");
        }

        /// <summary>Every frame is crafted at the sculptor's table and installed (user, 2026-09-27): none is in the architect's menu, each has a
        /// sculptor's-table recipe, quality and an install blueprint; only the photo spot stays in Misc. The fine defs are gone, and the names old
        /// saves use for them (things, install blueprints, bills) resolve to the plain frame of the same shape; translations are not redirected.</summary>
        private static void CraftedFrames()
        {
            var table = ThingDef.Named("TableSculpting");
            var made = new List<string>();
            foreach (var d in DefDatabase<ThingDef>.AllDefs.Where(x => x.defName.StartsWith("MF_") && x.building != null && !x.IsBlueprint && !x.IsFrame))
            {
                if (d.designationCategory?.defName == "Misc" && d.defName != "MF_PhotoSpot") continue;
                if (d.defName == "MF_PhotoSpot")
                {
                    if (d.designationCategory?.defName != "Misc") throw new InvalidOperationException("the photo spot left the Misc tab: " + d.designationCategory?.defName);
                    continue;
                }
                if (d.designationCategory != null) throw new InvalidOperationException(d.defName + " is still in the architect's " + d.designationCategory.defName + " tab");
                var r = DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(x => x.ProducedThingDef == d);
                if (r == null || !table.AllRecipes.Contains(r)) throw new InvalidOperationException(d.defName + " has no recipe at the sculptor's table");
                if (!d.HasComp(typeof(CompQuality)) || d.installBlueprintDef == null || d.minifiedDef == null)
                    throw new InvalidOperationException(d.defName + ": quality " + d.HasComp(typeof(CompQuality)) + ", install blueprint " + d.installBlueprintDef + ", minified " + d.minifiedDef);
                made.Add(d.defName);
            }
            if (made.Count != 7 || !made.Contains("MF_MemoryTree")) throw new InvalidOperationException("expected 7 crafted things including memory tree, found " + made.Count + ": " + string.Join(", ", made));
            if (DefDatabase<ThingDef>.AllDefs.Any(x => x.defName.Contains("MF_Fine"))) throw new InvalidOperationException("a fine def is still loaded");
            Func<Type, string, bool, string> resolve = (t, n, inj) => BackCompatibility.BackCompatibleDefName(t, n, inj);
            string easelBp = ThingDef.Named("MF_PhotoEasel").installBlueprintDef.defName;
            var got = new[]
            {
                resolve(typeof(ThingDef), "MF_FinePhotoFrame", false), resolve(typeof(ThingDef), "MF_FinePhotoFrameWide", false), resolve(typeof(ThingDef), "MF_FinePhotoEasel", false),
                resolve(typeof(ThingDef), "MF_FinePhotoBoard", false), resolve(typeof(RecipeDef), "Make_MF_FinePhotoBoard", false), resolve(typeof(ThingDef), easelBp.Replace("MF_Photo", "MF_FinePhoto"), false),
            };
            string[] want = { "MF_PhotoFrame", "MF_PhotoFrameWide", "MF_PhotoEasel", "MF_PhotoBoard", "Make_MF_PhotoBoard", easelBp };
            for (int i = 0; i < want.Length; i++)
                if (got[i] != want[i]) throw new InvalidOperationException("old name resolves to " + got[i] + ", expected " + want[i]);
            if (resolve(typeof(ThingDef), "MF_FinePhotoEasel", true) != "MF_FinePhotoEasel") throw new InvalidOperationException("an old translation was redirected onto a plain frame");
            checks.Add("PASS: every display is crafted at the sculptor's table and installed — 8 things (" + string.Join(", ", made) + ") with recipe, quality and install blueprint, none in the architect's menu; the photo spot stays in Misc; old fine names resolve (" + string.Join(", ", got) + "); translations untouched");
        }

        private static void StylesCapture()
        {
            var at = new Vector3(center.x - 3.5f, 0f, center.z + 5.5f);
            Find.CameraDriver.SetRootPosAndSize(at, 9f);
            if (!PhotoCapture.Capture(map, at, 13f, r =>
                {
                    if (r?.png != null) File.WriteAllBytes(Path.Combine(dir, "frame-styles.png"), r.png);
                }))
                throw new InvalidOperationException("frame-styles capture could not start");
        }

        private static Building_PhotoFrame Spawn(string defName, ThingDef stuff, IntVec3 cell, Rot4 rot, QualityCategory? quality = null)
        {
            var def = ThingDef.Named(defName);
            var b = (Building_PhotoFrame)ThingMaker.MakeThing(def, stuff);
            b.SetFaction(Faction.OfPlayer);
            if (quality.HasValue) b.TryGetComp<CompQuality>()?.SetQuality(quality.Value, ArtGenerationContext.Colony);
            var report = GenConstruct.CanPlaceBlueprintAt(def, cell, rot, map);
            if (!report.Accepted) throw new InvalidOperationException(defName + " placement at " + cell + " rejected: " + report.Reason);
            GenSpawn.Spawn(b, cell, map, rot);
            return b;
        }

        private static void PlaceVariants()
        {
            // Wide 2x1: north wall OK, east rotation rejected by the placement rule.
            var wideDef = ThingDef.Named("MF_PhotoFrameWide");
            var wideCell = new IntVec3(center.x - 8, 0, center.z + 5);
            var east = wideDef.PlaceWorkers[0].AllowsPlacing(wideDef, wideCell, Rot4.East, map);
            if (east.Accepted) throw new InvalidOperationException("wide frame accepted on east rotation");
            checks.Add("PASS: wide frame rejects east/west rotation (" + east.Reason + ")");
            var wide = Spawn("MF_PhotoFrameWide", ThingDefOf.Steel, wideCell, Rot4.North);
            wide.SetPhoto(testRec.id);
            // Free-standing easel 2x2 and board 3x2 on open ground.
            easel = Spawn("MF_PhotoEasel", ThingDefOf.WoodLog, new IntVec3(center.x + 7, 0, center.z), Rot4.North);
            easel.SetPhoto(testRec.id);
            var board = Spawn("MF_PhotoBoard", ThingDefOf.WoodLog, new IntVec3(center.x - 7, 0, center.z - 3), Rot4.North);
            board.SetPhoto(testRec.id);
            // Quality: a legendary steel frame on the wall beside the normal wood one.
            var fine = Spawn("MF_PhotoFrame", ThingDefOf.Steel, new IntVec3(center.x - 3, 0, center.z + 5), Rot4.North, QualityCategory.Legendary);
            fineFrame = fine;
            fine.SetPhoto(testRec.id);
            float fineBeauty = fine.GetStatValue(StatDefOf.Beauty), basicBeauty = frame.GetStatValue(StatDefOf.Beauty);
            if (fineBeauty <= basicBeauty) throw new InvalidOperationException("legendary beauty " + fineBeauty + " not above normal " + basicBeauty);
            checks.Add("PASS: legendary steel frame beauty " + fineBeauty + " > normal wood frame " + basicBeauty);
            var fq = fine.TryGetComp<CompQuality>();
            fq.SetQuality(QualityCategory.Good, null);
            bool goodFine = fine.IsFine;
            fq.SetQuality(QualityCategory.Excellent, null);
            bool excellentFine = fine.IsFine;
            fq.SetQuality(QualityCategory.Legendary, null);
            if (goodFine || !excellentFine || frame.IsFine || frame.TryGetComp<CompQuality>()?.Quality != QualityCategory.Normal)
                throw new InvalidOperationException("the fine finish: good " + goodFine + ", excellent " + excellentFine + ", normal frame " + frame.IsFine);
            checks.Add("PASS: quality decides the finish — excellent and better are gilded and fade at half speed, good and below are not; a frame made without a quality is normal");
            if (fine.DrawColor == frame.DrawColor) throw new InvalidOperationException("steel and wood frames have the same border colour");
            checks.Add("PASS: border colour follows stuff (wood " + frame.DrawColor + " vs steel " + fine.DrawColor + ")");
            foreach (var b in new[] { wide, easel, board, fine })
            {
                b.RefreshBinding();
                if (!b.HasPhotoTexture) throw new InvalidOperationException(b.def.defName + " did not bind the photo texture");
            }
            checks.Add("PASS: wide/easel/board/legendary variants spawned and show the photo");
            FrameStyles(wide, fine);
            // Uninstall -> reinstall keeps the photo without choosing again.
            string id = frame.photoId;
            var oldCell = frame.Position;
            var mini = frame.MakeMinified();
            if (frame.Spawned || mini.InnerThing != frame) throw new InvalidOperationException("uninstall did not wrap the same frame instance");
            if (((Building_PhotoFrame)mini.InnerThing).photoId != id) throw new InvalidOperationException("photoId lost on uninstall");
            var inner = (Building_PhotoFrame)mini.InnerThing;
            mini.InnerThing = null;
            mini.Destroy();
            var newCell = new IntVec3(center.x + 8, 0, center.z + 5);
            GenSpawn.Spawn(inner, newCell, map, Rot4.North);
            frame = inner;
            frame.RefreshBinding();
            if (frame.photoId != id || !frame.HasPhotoTexture) throw new InvalidOperationException("photo not restored after reinstall");
            checks.Add("PASS: uninstall (" + oldCell + ") -> reinstall (" + newCell + ") kept photo " + id.Substring(0, 8) + " and texture");
        }

        private static PhotoRecord dragRec, tallRec, roleRec;
        private static float fxAt;
        private static bool fxFlashShot;

        // ---------- phase 2: re-shoot, historic moments, anniversary letter, thumbnail eviction ----------

        private static PhotoRecord reshootRec, synthRec, annivRec, popRec;
        private static string evictThumbPath;
        private static int fakeNow;

        private static void Phase2Start()
        {
            var store = PhotoStore.Instance;
            // Pure predicates with synthetic values (the game cannot be advanced a year inside the probe).
            int start = 1000000;
            Near(Historic.AnniversaryYears(start + GenDate.TicksPerYear + 100, start), 1, "anniversary +1y");
            Near(Historic.AnniversaryYears(start + GenDate.TicksPerYear + GenDate.TicksPerDay + 1, start), 0, "anniversary +1y+1d");
            Near(Historic.AnniversaryYears(start + 2 * GenDate.TicksPerYear, start), 2, "anniversary +2y");
            Near(Historic.AnniversaryYears(start + 100, start), 0, "anniversary +100t");
            Near(Historic.PopulationMilestone(9), 0, "pop 9"); Near(Historic.PopulationMilestone(10), 10, "pop 10");
            Near(Historic.PopulationMilestone(25), 20, "pop 25"); Near(Historic.PopulationMilestone(100), 100, "pop 100");
            float lon = Find.WorldGrid.LongLatOf(map.Tile).x;
            int now = Find.TickManager.TicksAbs;
            Near(Historic.YearsAgoToday(now, now - GenDate.TicksPerYear, lon), 1, "years-ago 1");
            Near(Historic.YearsAgoToday(now, now - GenDate.TicksPerYear - 2 * GenDate.TicksPerDay, lon), 0, "years-ago off-day");
            Near(Historic.YearsAgoToday(now, now - 3 * GenDate.TicksPerYear, lon), 3, "years-ago 3");
            checks.Add("PASS: historic predicates (anniversary / population / years-ago-today) on synthetic ticks");
            // Map frames draw the thumbnail, not the full image.
            var bound = frame.BoundTexture;
            if (bound == null || bound.height > PhotoCapture.ThumbHeight) throw new InvalidOperationException("frame texture " + (bound == null ? "null" : bound.width + "x" + bound.height));
            checks.Add("PASS: wall frame binds the thumbnail (" + bound.width + "x" + bound.height + ")");
            // Thumbnail eviction: cap 2, touch every thumbnail now, re-check a few frames later.
            PhotoTextureCache.ThumbCap = 2;
            int touched = 0;
            foreach (var r in store.Records.Where(x => x.HasFile))
            {
                if (PhotoTextureCache.Thumb(store.ThumbPath(r), store.FilePath(r)) != null) { touched++; evictThumbPath = store.ThumbPath(r); }
            }
            if (touched < 4) throw new InvalidOperationException("only " + touched + " thumbnails to test eviction with");
            // Same framing again.
            reshootRec = store.Reshoot(testRec);
            if (reshootRec == null || reshootRec.reshootOf != testRec.id) throw new InvalidOperationException("reshoot record missing/unlinked");
            if ((reshootRec.center - testRec.center).magnitude > 0.01f || Math.Abs(reshootRec.spanCells - testRec.spanCells) > 0.01f || Math.Abs(reshootRec.aspect - testRec.aspect) > 0.001f)
                throw new InvalidOperationException("reshoot framing differs");
            checks.Add("PASS: re-shoot queued with the original framing (span " + reshootRec.spanCells + ", aspect " + reshootRec.aspect.ToString("0.00") + ")");
            // A milestone "taken a year ago today" (synthetic date) for the anniversary letter.
            int startAbs = Find.TickManager.gameStartAbsTick;
            fakeNow = startAbs + GenDate.TicksPerYear + 10;
            synthRec = store.Request(map, center, "wedding", "probe:synth", "Synthetic wedding a year ago", "", map.mapPawns.FreeColonistsSpawned);
            if (synthRec == null) throw new InvalidOperationException("synthetic record rejected");
            synthRec.ticksAbs = startAbs + 10;
            // A milestone taken exactly two years ago today must NOT produce a letter: only the first anniversary does.
            synth2Rec = store.Request(map, center, "party", "probe:synth2", "Synthetic party two years ago", "", map.mapPawns.FreeColonistsSpawned);
            if (synth2Rec == null) throw new InvalidOperationException("second synthetic record rejected");
            synth2Rec.ticksAbs = startAbs - GenDate.TicksPerYear + 10;
        }

        private static bool Phase2FilesReady()
        {
            foreach (var r in new[] { reshootRec, synthRec, synth2Rec })
            {
                if (r.HasFile) continue;
                if (!PhotoStore.Instance.Records.Contains(r)) throw new InvalidOperationException("phase-2 capture failed: " + r.title);
                return false;
            }
            return true;
        }

        private static void Phase2Historic()
        {
            var store = PhotoStore.Instance;
            // Eviction ran? Touch one thumbnail now (the others were last used many frames ago).
            PhotoTextureCache.Thumb(evictThumbPath, null);
            if (PhotoTextureCache.ThumbCount > 2) throw new InvalidOperationException("thumb cache " + PhotoTextureCache.ThumbCount + " after eviction (cap 2)");
            checks.Add("PASS: thumbnail eviction leaves " + PhotoTextureCache.ThumbCount + " of the touched set (cap 2)");
            PhotoTextureCache.ThumbCap = 240;
            // Re-shoot file has the original's dimensions.
            var a = new Texture2D(2, 2); a.LoadImage(File.ReadAllBytes(store.FilePath(testRec)));
            var b = new Texture2D(2, 2); b.LoadImage(File.ReadAllBytes(store.FilePath(reshootRec)));
            bool same = a.width == b.width && a.height == b.height;
            string dims = a.width + "x" + a.height + " vs " + b.width + "x" + b.height;
            UnityEngine.Object.Destroy(a); UnityEngine.Object.Destroy(b);
            if (!same) throw new InvalidOperationException("reshoot dimensions differ: " + dims);
            File.Copy(store.FilePath(reshootRec), Path.Combine(dir, "reshoot.png"), true);
            checks.Add("PASS: re-shoot file written with the original's dimensions (" + dims + ")");
            // Population: bring the colony to 10 and run the daily check "a year later".
            int need = 10 - PawnsFinder.AllMaps_FreeColonists.Count;
            for (int i = 0; i < need; i++)
            {
                var p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, center + new IntVec3(-6 + i, 0, -6), map);
            }
            int before = store.Records.Count;
            Historic.DailyCheck(store, fakeNow);
            annivRec = store.Records.Skip(before).FirstOrDefault(r => r.kind == Historic.KindAnniversary);
            popRec = store.Records.Skip(before).FirstOrDefault(r => r.kind == Historic.KindPopulation);
            if (annivRec == null) throw new InvalidOperationException("anniversary photo not requested");
            if (popRec == null) throw new InvalidOperationException("population photo not requested (colonists " + PawnsFinder.AllMaps_FreeColonists.Count + ")");
            if (!store.HasMark("anniv:1") || !store.HasMark("pop:10")) throw new InvalidOperationException("historic marks missing");
            int again = store.Records.Count;
            Historic.DailyCheck(store, fakeNow);
            if (store.Records.Count != again) throw new InvalidOperationException("daily check fired twice for the same milestones");
            checks.Add("PASS: daily check (fake +1y) -> '" + annivRec.title + "' + '" + popRec.title + "', idempotent on re-run");
            var letters = Find.LetterStack.LettersListForReading.OfType<Letter_MFPhoto>().ToList();
            if (letters.Count != 1 || letters[0].photoId != synthRec.id)
                throw new InvalidOperationException("expected exactly one letter, for the 1-year-old photo; got " + letters.Count + (letters.Count > 0 ? " (first: " + store.Get(letters[0].photoId)?.title + ")" : ""));
            checks.Add("PASS: 'a year ago today' letter for the 1-year-old wedding only, none for the 2-year-old party: " + letters[0].Label);
        }

        private static bool Phase2HistoricFilesReady()
        {
            foreach (var r in new[] { annivRec, popRec })
            {
                if (r.HasFile) continue;
                if (!PhotoStore.Instance.Records.Contains(r)) throw new InvalidOperationException("historic capture failed: " + r.title);
                return false;
            }
            return true;
        }

        private static void Phase2Letter()
        {
            var store = PhotoStore.Instance;
            var tex = new Texture2D(2, 2); tex.LoadImage(File.ReadAllBytes(store.FilePath(annivRec)));
            string dims = tex.width + "x" + tex.height;
            UnityEngine.Object.Destroy(tex);
            PhotoCapture.ImageSize(Historic.WideSpan, PhotoCapture.DefaultAspect, out int aw, out int ah);
            if (dims != aw + "x" + ah) throw new InvalidOperationException("colony-wide anniversary shot is " + dims + ", expected " + aw + "x" + ah + " (" + PhotoCapture.PixelsPerCell + " px/cell capped at " + PhotoCapture.MaxSide + ")");
            File.Copy(store.FilePath(annivRec), Path.Combine(dir, "anniversary.png"), true);
            checks.Add("PASS: anniversary shot written at " + dims + " (61 cells, capped at " + PhotoCapture.MaxSide + " px), population shot " + popRec.file);
            var letter = Find.LetterStack.LettersListForReading.OfType<Letter_MFPhoto>().First();
            letter.OpenLetter();
            var view = letter.Choices.First();
            view.action();
            if (Find.MainTabsRoot.OpenTab != AlbumDef || !(AlbumDef.TabWindow is MainTabWindow_Album ga) || ga.GalleryCurrent != synthRec)
                throw new InvalidOperationException("letter's view option did not open the gallery on the photo");
            checks.Add("PASS: letter opens and its first choice shows the photo in the gallery");
        }

        // ---------- phase 3: reminiscing ----------

        private static Pawn p3;
        private static Building_PhotoFrame viewTarget;
        private static int viewTicks;
        private static PhotoRecord synth2Rec;
        private static string savedP1Id, savedTogetherTitle;

        /// <summary>Random chit-chat during the ticks can put a pawn on the 120-tick interaction cooldown; retry a few times.</summary>
        private static bool ForceTalk(Pawn a, Pawn b, InteractionDef def)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (a.interactions.TryInteractWith(b, def)) return true;
                for (int i = 0; i < 130; i++) Find.TickManager.DoSingleTick();
            }
            return false;
        }

        private static void Phase3Start()
        {
            var s = MomentFramesMod.Settings;
            s.reminisce = true;
            if (frame.Record != testRec) frame.SetPhoto(testRec.id);   // the wall frame shows the test photo, which p1 and p2 are in
            p3 = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p != p1 && p != p2 && !testRec.Has(p));
            if (p3 == null) throw new InvalidOperationException("no colonist outside the test photo");
            // Both near the frame (cleared ground), p2 next to p1 for the talk.
            p1.Position = frame.Position + new IntVec3(0, 0, -2);
            p1.Notify_Teleported(true, false);
            p2.Position = p1.Position + IntVec3.East;
            p2.Notify_Teleported(true, false);
            var displayed = Reminisce.DisplayedFrames(map);
            if (!displayed.Contains(frame)) throw new InvalidOperationException("wall frame not in the hung-photo index (" + displayed.Count + " frames)");
            if (Reminisce.SharedFrame(p1, p2) == null) throw new InvalidOperationException(p1.LabelShort + "/" + p2.LabelShort + " are both in the wall photo but no shared frame was found");
            if (Reminisce.SharedFrame(p1, p3) != null) throw new InvalidOperationException(p3.LabelShort + " is in no photo but a shared frame was found");
            int rebuilds = Reminisce.Rebuilds;
            var intDef = DefDatabase<InteractionDef>.GetNamed("MF_RememberWhen");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float w12 = 0f, w13 = 0f;
            for (int i = 0; i < 10000; i++)
            {
                w12 = intDef.Worker.RandomSelectionWeight(p1, p2);
                w13 = intDef.Worker.RandomSelectionWeight(p1, p3);
            }
            sw.Stop();
            if (w12 <= 0f || w13 != 0f) throw new InvalidOperationException("remember-when weight " + w12 + " (pair in the photo) / " + w13 + " (outsider)");
            if (Reminisce.Rebuilds != rebuilds) throw new InvalidOperationException("index rebuilt " + (Reminisce.Rebuilds - rebuilds) + " times during 20000 queries");
            if (sw.ElapsedMilliseconds > 250) throw new InvalidOperationException("20000 weight queries took " + sw.ElapsedMilliseconds + " ms");
            checks.Add("PASS: hung-photo index — " + displayed.Count + " frames; weight " + w12 + " for the pair in the photo, 0 for the outsider; 20000 queries in " + sw.ElapsedMilliseconds + " ms with 0 rebuilds");
            // Changing a frame's photo invalidates the index exactly once.
            easel.SetPhoto(null);
            Reminisce.DisplayedFrames(map);
            if (Reminisce.Rebuilds != rebuilds + 1) throw new InvalidOperationException("clearing a frame rebuilt the index " + (Reminisce.Rebuilds - rebuilds) + " times");
            easel.SetPhoto(tallRec.id);
            checks.Add("PASS: clearing a frame's photo rebuilds the index once");
            // One switch turns both the talk and the recreation off.
            var jg = DefDatabase<JoyGiverDef>.GetNamed("MF_ViewPhoto").Worker;
            s.reminisce = false;
            if (intDef.Worker.RandomSelectionWeight(p1, p2) != 0f || jg.TryGiveJob(p1) != null) throw new InvalidOperationException("reminisce setting off but the talk or the job still fires");
            s.reminisce = true;
            checks.Add("PASS: the single reminisce setting disables both the talk and the recreation job");
            // Forced talk: both get the memory with the photo's caption; a repeat renews instead of stacking.
            if (!ForceTalk(p1, p2, intDef)) throw new InvalidOperationException("forced remember-when interaction refused 5 times");
            var together = DefDatabase<ThoughtDef>.GetNamed(Reminisce.ThoughtTogether);
            var m1 = p1.needs.mood.thoughts.memories.GetFirstMemoryOfDef(together) as Thought_MFPhoto;
            var m2 = p2.needs.mood.thoughts.memories.GetFirstMemoryOfDef(together) as Thought_MFPhoto;
            if (m1 == null || m2 == null) throw new InvalidOperationException("remember-when memory missing (p1 " + (m1 != null) + ", p2 " + (m2 != null) + ")");
            // SharedPhoto picks at random among every displayed photo with both of them in it (r35: a drag shot had caught the pair too), so any of those captions is right.
            var sharedTitles = Reminisce.DisplayedFrames(map).OfType<IPhotoDisplay>().SelectMany(d => d.Displayed).Where(r => r.Has(p1) && r.Has(p2)).Select(r => r.title).Distinct().ToList();
            if (!sharedTitles.Contains(m1.title) || m1.otherPawn != p2 || m2.otherPawn != p1) throw new InvalidOperationException("remember-when memory wrong: title '" + m1.title + "' not among the shared captions [" + string.Join(" | ", sharedTitles) + "], other " + m1.otherPawn?.LabelShort);
            for (int i = 0; i < 130; i++) Find.TickManager.DoSingleTick();   // past the 120-tick interaction cooldown
            if (!ForceTalk(p1, p2, intDef)) throw new InvalidOperationException("second remember-when interaction refused");
            int n = p1.needs.mood.thoughts.memories.NumMemoriesOfDef(together);
            if (n != 1) throw new InvalidOperationException("remember-when memory stacked to " + n);
            savedTogetherTitle = m1.title;
            checks.Add("PASS: 'remember when' -> both got '" + m1.LabelCap + "' (mood " + m1.MoodOffset() + "); a repeat renews instead of stacking");
            // Recreation: only a viewer who is in the photo gets the memory.
            if (!Reminisce.GrantsMemory(p1, testRec) || Reminisce.GrantsMemory(p3, testRec)) throw new InvalidOperationException("GrantsMemory: p1 " + Reminisce.GrantsMemory(p1, testRec) + ", p3 " + Reminisce.GrantsMemory(p3, testRec));
            // Deterministic target: only the wall frame keeps its photo while p1 goes to look. The giver picks at random among every frame showing p1
            // (r26 chose the easel's drag shot, r27 the legendary frame and the job ended before the memory); the others are restored in Phase3Finish.
            hungBackup.Clear();
            foreach (var b in Reminisce.DisplayedFrames(map).OfType<Building_PhotoFrame>().ToList())
                if (b != frame) { hungBackup[b] = b.photoId; b.SetPhoto(null); }
            p1.needs.joy.CurLevel = 0.3f;
            // Nobody else may hold the frame (a party guest could still be looking at it), and the frames must count as indoors: the giver skips
            // unroofed frames when it is raining or the outdoor temperature is unsafe (r46 failed here after the party phases pushed the clock on).
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
                if (p != p1 && p.CurJobDef?.defName == "MF_ViewPhoto") p.jobs.EndCurrentJob(JobCondition.InterruptForced);
            map.reservationManager.ReleaseAllForTarget(frame);
            RoofFrameRow(true);
            var job = jg.TryGiveJob(p1);
            if (job == null || job.def.defName != "MF_ViewPhoto" || !(job.targetA.Thing is Building_PhotoFrame target))
                throw new InvalidOperationException("joy giver gave " + (job == null ? "nothing" : job.def.defName) + " — outsideOk " + JoyUtility.EnjoyableOutsideNow(p1) + ", frame roofed " + frame.Position.Roofed(map)
                    + ", reach " + p1.CanReserveAndReach(frame, PathEndMode.Touch, Danger.None) + ", reserver " + map.reservationManager.FirstRespectedReserver(frame, p1)?.LabelShort
                    + ", outdoor " + map.mapTemperature.OutdoorTemp.ToString("0") + "C, hour " + GenLocalDate.HourOfDay(map) + ", weather " + map.weatherManager.curWeather?.defName + ", displayed " + Reminisce.DisplayedFrames(map).Count);
            viewTarget = target;
            viewTicks = 0;
            p1.jobs.StartJob(job, JobCondition.InterruptForced);
            checks.Add("PASS: recreation job given to " + p1.LabelShort + " -> " + target.def.defName + " at " + target.Position);
        }

        /// <summary>Ticks the game until p1 has looked long enough to gain the memory (or fails after 3000 ticks).</summary>
        private static bool Phase3ViewProgress()
        {
            var viewedDef = DefDatabase<ThoughtDef>.GetNamed(Reminisce.ThoughtViewed);
            var mem = p1.needs.mood.thoughts.memories;
            for (int i = 0; i < 200 && mem.GetFirstMemoryOfDef(viewedDef) == null; i++)
            {
                Find.TickManager.DoSingleTick();
                viewTicks++;
            }
            var m = mem.GetFirstMemoryOfDef(viewedDef) as Thought_MFPhoto;
            if (m == null)
            {
                if (viewTicks > 3000) throw new InvalidOperationException("no 'looked at a photo of myself' memory after " + viewTicks + " ticks; job " + p1.CurJobDef?.defName + ", pawn at " + p1.Position + ", frame at " + viewTarget.Position);
                return false;
            }
            // The joy giver picks among every frame showing a photo with p1 in it (self photos weighted 4x), so the memory carries that frame's caption.
            var shown = viewTarget.Record;
            if (shown == null || m.title != shown.title || !shown.Has(p1)) throw new InvalidOperationException("viewed memory title '" + m.title + "' vs the frame's photo '" + shown?.title + "' (p1 in it: " + (shown != null && shown.Has(p1)) + ")");
            checks.Add("PASS: " + p1.LabelShort + " walked to the frame and after " + viewTicks + " ticks gained '" + m.LabelCap + "' (mood " + m.MoodOffset() + "); job now " + p1.CurJobDef?.defName);
            Find.CameraDriver.SetRootPosAndSize(viewTarget.DrawPos + new Vector3(0f, 0f, -0.5f), 9f);
            return true;
        }

        private static void Phase3Finish()
        {
            // p1 releases the wall frame and the other photos come back before the outsider looks for a frame (r28: with one frame, reserved by p1, none).
            if (p1.CurJobDef?.defName == "MF_ViewPhoto") p1.jobs.EndCurrentJob(JobCondition.InterruptForced);
            RestoreHung();
            // The outsider gets the job too (recreation is for everyone) but never the memory.
            var jg = DefDatabase<JoyGiverDef>.GetNamed("MF_ViewPhoto").Worker;
            p3.needs.joy.CurLevel = 0.3f;
            var job = jg.TryGiveJob(p3);
            if (job == null) throw new InvalidOperationException("joy giver gave the outsider nothing");
            checks.Add("PASS: outsider " + p3.LabelShort + " also gets the recreation job (" + job.targetA.Thing.def.defName + "), memory gate false");
            // Fast recreation: on the legendary frame (beauty x8 = joy x8) joy fills from 0.9 in ~90 ticks, so the job ends long before the
            // 600-tick memory — the look must still count once recreation is about to be full (r27 lost the memory this way).
            foreach (var b in Reminisce.DisplayedFrames(map).OfType<Building_PhotoFrame>().ToList())
                if (b != fineFrame) { hungBackup[b] = b.photoId; b.SetPhoto(null); }
            p1.needs.mood.thoughts.memories.RemoveMemoriesOfDef(DefDatabase<ThoughtDef>.GetNamed(Reminisce.ThoughtViewed));
            p1.needs.joy.CurLevel = 0.9f;
            var job2 = jg.TryGiveJob(p1);
            if (job2 == null || job2.targetA.Thing != fineFrame) throw new InvalidOperationException("fast-recreation setup: job " + (job2 == null ? "none" : job2.targetA.Thing.def.defName));
            p1.jobs.StartJob(job2, JobCondition.InterruptForced);
            viewTarget = fineFrame;
            viewTicks = 0;
        }

        /// <summary>Ticks until p1 has the memory from the legendary frame; fails if the job ended on full recreation without it.</summary>
        private static bool Phase3FastJoyProgress()
        {
            var viewedDef = DefDatabase<ThoughtDef>.GetNamed(Reminisce.ThoughtViewed);
            var mem = p1.needs.mood.thoughts.memories;
            for (int i = 0; i < 200 && mem.GetFirstMemoryOfDef(viewedDef) == null; i++)
            {
                Find.TickManager.DoSingleTick();
                viewTicks++;
            }
            var m = mem.GetFirstMemoryOfDef(viewedDef) as Thought_MFPhoto;
            if (m == null)
            {
                if (p1.CurJobDef?.defName != "MF_ViewPhoto" || viewTicks > 3000)
                    throw new InvalidOperationException("fast recreation: job " + p1.CurJobDef?.defName + " after " + viewTicks + " ticks, joy " + p1.needs.joy.CurLevel.ToString("0.00") + ", no memory");
                return false;
            }
            if (m.title != testRec.title) throw new InvalidOperationException("fast recreation memory title '" + m.title + "'");
            checks.Add("PASS: legendary frame — " + p1.LabelShort + " got '" + m.LabelCap + "' after " + viewTicks + " ticks although recreation filled up (joy " + p1.needs.joy.CurLevel.ToString("0.00") + ", job now " + p1.CurJobDef?.defName + ")");
            if (p1.CurJobDef?.defName == "MF_ViewPhoto") p1.jobs.EndCurrentJob(JobCondition.InterruptForced);
            RestoreHung();
            RoofFrameRow(false);
            return true;
        }

        /// <summary>Roof over the wall-frame row (the cells just south of the wall) for phase 3 only, so the recreation giver treats the frames as
        /// indoors whatever the weather or the hour; removed afterwards so the close-up photos see the map as before.</summary>
        private static void RoofFrameRow(bool on)
        {
            for (int x = center.x - 10; x <= center.x + 10; x++)
            {
                var c = new IntVec3(x, 0, center.z + 5);
                if (c.InBounds(map)) map.roofGrid.SetRoof(c, on ? RoofDefOf.RoofConstructed : null);
            }
        }

        private static void RestoreHung()
        {
            foreach (var kv in hungBackup) kv.Key.SetPhoto(kv.Value);
            hungBackup.Clear();
        }

        private static Building_PhotoFrame fineFrame;
        private static readonly Dictionary<Building_PhotoFrame, string> hungBackup = new Dictionary<Building_PhotoFrame, string>();

        // ---------- phase 4: collage board, projector, memorial stand ----------

        private static Building_PhotoCollage collage;
        private static Building_PhotoProjector projector;
        private static Building_MemorialFrame memorial;
        private static Building_Grave grave;
        private static Pawn memorialDiver;
        private static float memorialPhotoUntil;
        private static string savedDiverId;
        private static IntVec3 savedCollageCell, savedMemorialCell;

        private static void Phase4Start()
        {
            var store = PhotoStore.Instance;
            // Four photos on one board (place 3 holds the portrait photo).
            var cdef = ThingDef.Named("MF_PhotoCollage");
            collage = (Building_PhotoCollage)ThingMaker.MakeThing(cdef, ThingDefOf.WoodLog);
            collage.SetFaction(Faction.OfPlayer);
            var ccell = new IntVec3(center.x + 14, 0, center.z - 4);
            var rep = GenConstruct.CanPlaceBlueprintAt(cdef, ccell, Rot4.North, map);
            if (!rep.Accepted) throw new InvalidOperationException("collage placement rejected: " + rep.Reason);
            GenSpawn.Spawn(collage, ccell, map, Rot4.North);
            var wed = store.Records.First(r => r.kind == "wedding" && r.eventKey != "probe:synth");
            string[] ids = { testRec.id, dragRec.id, tallRec.id, wed.id };
            for (int i = 0; i < ids.Length; i++) collage.SetSlot(i, ids[i]);
            collage.RefreshBinding();
            if (collage.FilledCount != 4 || collage.BoundCount != 4) throw new InvalidOperationException("collage filled " + collage.FilledCount + ", bound " + collage.BoundCount);
            // Four equal windows; the portrait photo (place 3) is cropped to its middle rows to fill a landscape window.
            collage.SlotLayout(0, out _, out float sw, out float sh);
            for (int i = 1; i < Building_PhotoCollage.Slots; i++)
            {
                collage.SlotLayout(i, out _, out float wi, out float hi);
                if (Mathf.Abs(wi - sw) > 1e-4f || Mathf.Abs(hi - sh) > 1e-4f) throw new InvalidOperationException("collage window " + (i + 1) + " is " + wi + " x " + hi + ", window 1 " + sw + " x " + sh);
            }
            Rect tallCrop = collage.SlotCrop(2);
            if (tallCrop.width < 0.999f || tallCrop.height > 0.9f) throw new InvalidOperationException("portrait photo in a landscape window shows " + tallCrop + " (expected full width, middle rows)");
            if (!Reminisce.DisplayedFrames(map).Contains(collage)) throw new InvalidOperationException("collage not indexed as a display");
            if (!collage.Displayed.Any(r => r.Has(p1))) throw new InvalidOperationException("collage shows no photo with " + p1.LabelShort);
            checks.Add("PASS: collage board shows 4 photos in 4 equal windows (" + sw.ToString("0.00") + " x " + sh.ToString("0.00") + "), the portrait one cropped to rows "
                + tallCrop.yMin.ToString("0.00") + "-" + tallCrop.yMax.ToString("0.00") + " to fill its window; indexed for reminiscing");
            CollageVisiblePart(wed);
            RimTalkCheck(wed);
            // Projector: the photo shown is a pure function of the tick and the record list.
            projector = (Building_PhotoProjector)ThingMaker.MakeThing(ThingDef.Named("MF_Projector"), ThingDefOf.Steel);
            projector.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(projector, new IntVec3(center.x - 14, 0, center.z - 8), map, Rot4.North);
            var reel = projector.Reel;
            int n = reel.Count;
            if (n < 5) throw new InvalidOperationException("reel has only " + n + " photos");
            float per = Building_PhotoProjector.SecondsPerPhoto;
            Near(Building_PhotoProjector.IndexFor(0f, n), 0, "reel index t=0");
            Near(Building_PhotoProjector.IndexFor(per * (n + 1) + 0.5f, n), 1, "reel index wraps");
            Near(Building_PhotoProjector.IndexFor(12.3f, 0), -1, "reel index empty");
            Near(Building_PhotoProjector.IndexFor(0f, n, 1), 1, "reel index skip");
            for (int i = 1; i < n; i++) if (reel[i - 1].ticksAbs > reel[i].ticksAbs) throw new InvalidOperationException("reel not chronological at " + i);
            var cur = projector.Current;
            if (cur == null || cur != reel[projector.CurrentIndex]) throw new InvalidOperationException("projector current photo mismatch");
            if (Building_PhotoProjector.Clock <= 0f) throw new InvalidOperationException("projector clock never advanced (" + Building_PhotoProjector.Clock + ")");
            checks.Add("PASS: projector clock " + Building_PhotoProjector.Clock.ToString("0.0") + " real s of unpaused play, " + per + " s per photo (game-speed independent)");
            // Film choice: milestones only, one person, and "next photo".
            projector.SetFilm(Building_PhotoProjector.FilmMilestones, "", "");
            var film = projector.Reel;
            if (film.Count == 0 || film.Count >= n || film.Any(r => !Kinds.IsMilestone(r.kind))) throw new InvalidOperationException("milestone film has " + film.Count + " of " + n);
            int milestones = film.Count;
            projector.SetFilm("", p3.GetUniqueLoadID(), p3.LabelShort);
            film = projector.Reel;
            if (film.Count == 0 || film.Any(r => !r.Has(p3))) throw new InvalidOperationException("person film wrong (" + film.Count + ")");
            int personCount = film.Count;
            var before = projector.Current;
            projector.Next();
            if (film.Count > 1 && projector.Current == before) throw new InvalidOperationException("next photo did not advance");
            projector.SetFilm("", "", "");
            if (projector.Reel.Count != n) throw new InvalidOperationException("film reset lost photos");
            checks.Add("PASS: projector film — milestones " + milestones + "/" + n + ", person " + p3.LabelShort + " " + personCount + "/" + n + ", 'next photo' advances, reset restores all");
            if (!Reminisce.MovingDisplays(map).Contains(projector) || Reminisce.DisplayedFrames(map).Contains(projector)) throw new InvalidOperationException("projector must be a moving display, not in the talk index");
            projector.ImageLayout(out Vector3 poff, out float pw, out float ph);
            if (poff.z <= 0f || pw <= 0f || ph <= 0f) throw new InvalidOperationException("projector image layout " + poff + " " + pw + "x" + ph);
            checks.Add("PASS: projector shows reel " + (projector.CurrentIndex + 1) + "/" + n + " ('" + cur.title + "') as a pure function of the clock; recreation target, not in the talk index");
            Find.CameraDriver.SetRootPosAndSize(collage.DrawPos, 9f);
        }

        private static bool Phase4Memorial()
        {
            // Grave + memorial stand beside it; bury a colonist who is in the photos.
            if (memorialDiver == null)
            {
                Pawn candidate = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p != p1 && p != p2 && testRec.Has(p));
                if (candidate == null) throw new InvalidOperationException("no third colonist in the test photo");
                grave = (Building_Grave)ThingMaker.MakeThing(ThingDefOf.Grave);
                grave.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(grave, new IntVec3(center.x + 2, 0, center.z - 9), map, Rot4.North);
                memorial = (Building_MemorialFrame)ThingMaker.MakeThing(ThingDef.Named("MF_MemorialFrame"), ThingDefOf.WoodLog);
                memorial.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(memorial, new IntVec3(center.x + 3, 0, center.z - 9), map, Rot4.North);
                if (memorial.BuriedNeighbour != null || memorial.Record != null) throw new InvalidOperationException("memorial shows something before any burial");
                savedDiverId = candidate.GetUniqueLoadID();
                candidate.Kill(null);
                memorialDiver = candidate;
                memorialPhotoUntil = Time.realtimeSinceStartup + 15f;
                return false;
            }
            var photographedDeath = PhotoStore.Instance.Records.FirstOrDefault(r => r.eventKey == "death:" + savedDiverId);
            if (PhotoStore.Instance.HasPending)
            {
                if (Time.realtimeSinceStartup > memorialPhotoUntil) throw new TimeoutException("memorial death photo did not finish before burial");
                Wait(.1f); return false;
            }
            if (photographedDeath == null || !photographedDeath.HasFile || !photographedDeath.Has(memorialDiver))
                throw new InvalidOperationException("native death photograph must contain the still-visible corpse before burial");
            Pawn diver = memorialDiver;
            string name = diver.LabelShort;
            var corpse = diver.Corpse;
            if (corpse == null) throw new InvalidOperationException("no corpse after Kill");
            if (corpse.Spawned) corpse.DeSpawn();
            if (!grave.TryAcceptThing(corpse, false)) throw new InvalidOperationException("grave refused the corpse");
            if (memorial.BuriedNeighbour != diver) throw new InvalidOperationException("memorial does not see the buried " + name);
            var rec = memorial.Record;
            if (rec == null || !rec.Has(diver) || !memorial.Derived) throw new InvalidOperationException("memorial derived nothing for " + name);
            memorial.RefreshBinding();
            if (!memorial.HasPhotoTexture) throw new InvalidOperationException("memorial did not bind the derived photo");
            // A hand-picked photo wins; clearing it returns to the grave's.
            memorial.SetPhoto(dragRec.id);
            if (memorial.Record != dragRec || memorial.Derived) throw new InvalidOperationException("hand-picked photo did not win");
            memorial.SetPhoto(null);
            if (memorial.Record != rec) throw new InvalidOperationException("clearing did not return to the derived photo");
            checks.Add("PASS: memorial stand beside " + name + "'s grave derived '" + rec.title + "' by itself; a hand-picked photo wins, clearing returns to the grave's");
            savedCollageCell = collage.Position;
            savedMemorialCell = memorial.Position;
            Find.CameraDriver.SetRootPosAndSize(memorial.DrawPos + new Vector3(0f, 0f, 0.5f), 8f);
            checks.Add("PASS: native death photograph finishes with the corpse visible before the memorial fixture buries it");
            return true;
        }

        /// <summary>The death of the buried colonist queued a death photo; let it finish before the save.</summary>
        private static bool Phase4Wait()
        {
            var store = PhotoStore.Instance;
            if (store.HasPending) return false;
            var death = store.Records.FirstOrDefault(r => r.eventKey == "death:" + savedDiverId);
            if (death != null && death.HasFile && memorial.Record != death) throw new InvalidOperationException("memorial did not switch to the newer death photo");
            checks.Add("PASS: death photo " + (death == null ? "not requested (setting off)" : "captured and the memorial switched to it: '" + death.title + "'"));
            return true;
        }

        // ---------- phase 5: fading, relic photos ----------

        private static RelicSource relicA, relicB;
        private static PhotoRecord relicRec, relicRec2;

        private static void Phase5Start()
        {
            var store = PhotoStore.Instance;
            var s = MomentFramesMod.Settings;
            int y = GenDate.TicksPerYear;
            Near(Fading.Fade01(0, false, false, 10), 0f, "fade fresh");
            Near(Fading.Fade01(5 * y, false, false, 10), 0.5f, "fade 5 of 10 years");
            Near(Fading.Fade01(20 * y, false, false, 10), 1f, "fade clamps at 1");
            Near(Fading.Fade01(10 * y, true, false, 10), 0.5f, "excellent+ frame half speed");
            Near(Fading.Fade01(0, false, true, 10), 0.5f, "relic starts half faded");
            Near(Fading.Fade01(-5 * y, false, false, 10), 0f, "future-dated photo not faded");
            s.fade = false;
            Fading.PreviewYears = 10;
            if (Fading.TintFor(testRec, false) != Color.white) throw new InvalidOperationException("tint applied with the fading setting off");
            s.fade = true;
            s.fadeYears = 10;
            Fading.PreviewYears = 0;
            var fresh = Fading.TintFor(testRec, false);
            if (Math.Abs(fresh.r - 1f) > 0.02f || Math.Abs(fresh.b - 1f) > 0.02f) throw new InvalidOperationException("a photo taken today is already tinted " + fresh);
            Fading.PreviewYears = 10;
            var old = Fading.TintFor(testRec, false);
            if (old.b >= old.r || old.b > 0.65f) throw new InvalidOperationException("+10y tint " + old);
            Fading.PreviewYears = 0;
            checks.Add("PASS: fading — pure function (5/10 y = 0.5, excellent+ frame half, relic starts at 0.5, future = 0); setting off = white; today = white; +10 y preview = " + old);
            // Two "old colony" folders beside this game's: one with an index.tsv (and one photo without a thumbnail), one with bare files only.
            string root = Path.GetDirectoryName(store.FolderPath);
            var wed = store.Records.First(r => r.kind == "wedding" && r.HasFile && r.eventKey != "probe:synth");
            string dirA = Path.Combine(root, "probe-old-a"), dirB = Path.Combine(root, "probe-old-b");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            File.Copy(store.FilePath(wed), Path.Combine(dirA, wed.file), true);
            File.Copy(store.OriginalThumbPath(wed), Path.Combine(dirA, Path.GetFileNameWithoutExtension(wed.file) + ".thumb.png"), true);
            File.Copy(store.FilePath(testRec), Path.Combine(dirA, testRec.file), true);   // no thumbnail and not in the index: file-name fallback + generated thumbnail
            File.WriteAllText(Path.Combine(dirA, RelicImport.IndexFile),
                "#MomentFrames\tOld Colony A\tprobe-old-a\n" + wed.file + "\twedding\t" + wed.dateSlug + "\t" + wed.dateLabel + "\tWedding at the old colony\tthey were happy\t" + wed.year + "\t1.5\n",
                new UTF8Encoding(false));
            File.Copy(store.FilePath(dragRec), Path.Combine(dirB, dragRec.file), true);
            var sources = RelicImport.Sources(store);
            if (sources.Any(x => x.gameId == store.GameId)) throw new InvalidOperationException("the current game is listed as a relic source");
            relicA = sources.FirstOrDefault(x => x.gameId == "probe-old-a");
            relicB = sources.FirstOrDefault(x => x.gameId == "probe-old-b");
            if (relicA == null || relicB == null) throw new InvalidOperationException("relic sources: " + string.Join(", ", sources.Select(x => x.gameId + "=" + x.photos.Count)));
            if (relicA.name != "Old Colony A" || !relicA.fromIndex || relicA.photos.Count != 2) throw new InvalidOperationException("source A: '" + relicA.name + "' " + relicA.photos.Count + " photos, index " + relicA.fromIndex);
            var a1 = relicA.photos.First(p => p.file == wed.file);
            if (a1.title != "Wedding at the old colony" || a1.thumbFile == null || a1.desc != "they were happy") throw new InvalidOperationException("index entry: " + a1.title + " / " + a1.desc + " / thumb " + a1.thumbFile);
            var a2 = relicA.photos.First(p => p.file == testRec.file);
            if (a2.thumbFile != null || a2.dateSlug != testRec.dateSlug || a2.kind != "test" || a2.title != testRec.title) throw new InvalidOperationException("file-name fallback: " + a2.kind + " " + a2.dateSlug + " '" + a2.title + "' thumb " + a2.thumbFile);
            if (relicB.fromIndex || relicB.photos.Count != 1 || relicB.photos[0].kind != "manual") throw new InvalidOperationException("source B: index " + relicB.fromIndex + ", " + relicB.photos.Count + " photos");
            checks.Add("PASS: relic sources — 'Old Colony A' from index.tsv (2 photos: indexed wedding with thumbnail + unindexed test shot by file name) and a bare folder (1 manual); current game excluded");
            Find.WindowStack.Add(new Dialog_ImportRelics());
        }

        private static void Phase5Import()
        {
            var store = PhotoStore.Instance;
            int before = store.Records.Count;
            var a2 = relicA.photos.First(p => p.thumbFile == null);
            relicRec = store.ImportRelic(relicA, a2);
            if (relicRec == null) throw new InvalidOperationException("relic import returned null");
            if (relicRec.kind != Kinds.Relic || relicRec.people.Count != 0 || relicRec.peopleIds.Count != 0 || Kinds.IsMilestone(relicRec.kind) || relicRec.Delayed)
                throw new InvalidOperationException("relic record: kind " + relicRec.kind + ", people " + relicRec.people.Count + "/" + relicRec.peopleIds.Count);
            if (!File.Exists(store.FilePath(relicRec)) || !File.Exists(store.ThumbPath(relicRec))) throw new InvalidOperationException("relic file or thumbnail missing");
            var t = new Texture2D(2, 2);
            t.LoadImage(File.ReadAllBytes(store.ThumbPath(relicRec)));
            string tdims = t.width + "x" + t.height;
            UnityEngine.Object.Destroy(t);
            if (!tdims.EndsWith("x" + PhotoCapture.ThumbHeight)) throw new InvalidOperationException("generated thumbnail is " + tdims);
            File.Copy(store.ThumbPath(relicRec), Path.Combine(dir, "relic-thumb.png"), true);
            if (store.ImportRelic(relicA, a2) != null) throw new InvalidOperationException("the same relic was imported twice");
            relicRec2 = store.ImportRelic(relicB, relicB.photos[0]);
            if (relicRec2 == null || store.Records.Count != before + 2) throw new InvalidOperationException("second relic: " + (relicRec2 == null) + ", records " + store.Records.Count + " vs " + (before + 2));
            if (Reminisce.GrantsMemory(p1, relicRec)) throw new InvalidOperationException("a relic grants a memory to " + p1.LabelShort);
            if (MainTabWindow_Album.KindLabel(Kinds.Relic) == Kinds.Relic) throw new InvalidOperationException("no label for the relic kind");
            if (!relicRec.desc.Contains("Old Colony A") || !relicRec.dateLabel.Contains(a2.dateSlug)) throw new InvalidOperationException("relic caption: " + relicRec.desc + " / " + relicRec.dateLabel);
            checks.Add("PASS: relic import — file copied, thumbnail generated (" + tdims + "), kind relic, no people, not a milestone, no memory, not delayed; re-import refused; 2 relics from 2 old colonies: '" + relicRec.title + "', '" + relicRec2.title + "'");
            // On the easel: drawn half faded although it arrived today.
            easel.SetPhoto(relicRec.id);
            Near(Fading.TintFor(relicRec, false).b, Fading.Tint(0.5f).b, "relic tint on a frame");
            Fading.PreviewYears = 0;
        }

        /// <summary>Close-up of the wall frame's photo (span 7, centred on the drawn photo) through the capture pipeline.</summary>
        private static void FrameCloseup(string name)
        {
            frame.Layout(out _, out _, out Vector3 off);
            if (!PhotoCapture.Capture(map, frame.DrawPos + off, 7f, r =>
                {
                    if (r?.png != null) File.WriteAllBytes(Path.Combine(dir, name), r.png);
                }))
                throw new InvalidOperationException(name + " capture could not start");
        }

        /// <summary>Was the wall frame drawn in the frame the last capture rendered? Recorded as INFO for diagnosis.</summary>
        private static void CaptureDiag(string what)
        {
            checks.Add("INFO: " + what + " — view rect " + Find.CameraDriver.CurrentViewRect + ", frame " + frame.Position + " spawned " + frame.Spawned +
                ", registered " + map.dynamicDrawManager.DrawThings.Contains(frame) + ", in view " + Find.CameraDriver.InViewOf(frame) +
                "; last capture: expand frame " + PhotoCapture.LastExpandFrame + " union " + PhotoCapture.LastUnion + ", render frame " + PhotoCapture.LastRenderFrame +
                ", frame drawn in that frame " + (frame.lastDrawFrame == PhotoCapture.LastRenderFrame) + " (last drawn " + frame.lastDrawFrame + ", now " + Time.frameCount + ")");
        }

        /// <summary>Mean colour of the central 8% of an image (inside the framed photo in the close-up: the photo is ~13% of the width).</summary>
        private static Color CentreMean(string name)
        {
            var tex = new Texture2D(2, 2);
            tex.LoadImage(File.ReadAllBytes(Path.Combine(dir, name)));
            int w = tex.width, h = tex.height, x0 = (int)(w * 0.46f), x1 = (int)(w * 0.54f), z0 = (int)(h * 0.46f), z1 = (int)(h * 0.54f);
            var px = tex.GetPixels32();
            UnityEngine.Object.Destroy(tex);
            float r = 0f, g = 0f, b = 0f;
            int n = 0;
            for (int y = z0; y < z1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var c = px[y * w + x];
                    r += c.r; g += c.g; b += c.b; n++;
                }
            return new Color(r / n / 255f, g / n / 255f, b / n / 255f);
        }

        /// <summary>The wall frame is outside the camera view here, so it can only be in the photo if the culling rect was widened in the render frame;
        /// the same goes for the map sections above it (an undrawn section renders black).</summary>
        private static void OffscreenDrawn(string name)
        {
            if (Find.CameraDriver.InViewOf(frame)) throw new InvalidOperationException("off-screen test invalid: the frame is in the camera view " + Find.CameraDriver.CurrentViewRect);
            if (frame.lastDrawFrame != PhotoCapture.LastRenderFrame)
                throw new InvalidOperationException("off-screen capture " + name + ": the frame was not drawn in the render frame (drawn " + frame.lastDrawFrame + ", render " + PhotoCapture.LastRenderFrame + ", expand " + PhotoCapture.LastExpandFrame + ")");
            var tex = new Texture2D(2, 2);
            tex.LoadImage(File.ReadAllBytes(Path.Combine(dir, name)));
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            float sum = 0f;
            int n = 0;
            for (int y = h - h / 10; y < h; y++)   // top rows of the image (Texture2D rows run bottom-up)
                for (int x = 0; x < w; x++) { var c = px[y * w + x]; sum += (c.r + c.g + c.b) / 765f; n++; }
            UnityEngine.Object.Destroy(tex);
            float top = sum / n;
            if (top < 0.05f) throw new InvalidOperationException("off-screen capture " + name + ": the top band is black (mean " + top.ToString("0.000") + ") — map sections outside the camera view were not drawn");
            checks.Add("PASS: off-screen capture " + name + " (camera view " + Find.CameraDriver.CurrentViewRect + ", frame at " + frame.Position + ") drew the frame and the far map sections (top-band brightness " + top.ToString("0.00") + ")");
        }

        private static void ComparePair(string beforeName, string afterName, string what)
        {
            Color before = CentreMean(beforeName), after = CentreMean(afterName);
            float warmBefore = before.r - before.b, warmAfter = after.r - after.b;
            if (warmAfter - warmBefore < 0.05f || after.b >= before.b) { checks.Add("FAIL: fade not visible in the " + what + " close-up: before " + before + " after " + after); return; }   // reported, and the rest of the suite still runs
            checks.Add("PASS: +10 y fade preview shifts the " + what + " close-up warmer and darker (r-b " + warmBefore.ToString("0.000") + " -> " + warmAfter.ToString("0.000") + ", blue " + before.b.ToString("0.00") + " -> " + after.b.ToString("0.00") + ")");
        }

        private static void Phase5Compare()
        {
            Fading.PreviewYears = 0;
            MomentFramesMod.Settings.fade = false;   // back to the default before the save/load round trip
            ComparePair("fade-far-before.png", "fade-far-after.png", "off-screen");
            ComparePair("fade-before.png", "fade-after.png", "on-screen");
        }

        // ---------- phase 6: more moments ----------

        private static readonly List<PhotoRecord> phase6Recs = new List<PhotoRecord>();

        private static void Phase6Start()
        {
            var store = PhotoStore.Instance;
            phase6Recs.Clear();
            // The wedding photo is framed on the couple, not the venue.
            var wed = store.Records.First(r => r.kind == "wedding" && r.eventKey != "probe:synth");
            Near(wed.spanCells, Patch_Wedding.CoupleSpan, "wedding couple span");
            if (!wed.Has(p1) || !wed.Has(p2)) throw new InvalidOperationException("wedding photo lacks the couple");
            checks.Add("PASS: wedding photo framed on the couple (span " + wed.spanCells + ", centre " + wed.center + ")");
            // Resurrection: kill a colonist who is not used elsewhere, bring them back; one photo of them, once.
            var victim = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p != p1 && p != p2 && p != p3 && !p.Dead);
            if (victim == null) throw new InvalidOperationException("no colonist to resurrect");
            string vname = victim.LabelShort;
            int before = store.Records.Count;
            victim.Kill(null);
            if (victim.Corpse == null) throw new InvalidOperationException("no corpse for " + vname);
            if (!ResurrectionUtility.TryResurrect(victim)) throw new InvalidOperationException("resurrection of " + vname + " failed");
            var res = store.Records.Skip(before).FirstOrDefault(r => r.kind == "resurrect");
            if (res == null || !res.Has(victim) || res.eventKey != "resurrect:" + victim.GetUniqueLoadID()) throw new InvalidOperationException("no resurrection photo for " + vname);
            if (!victim.Spawned || victim.Dead) throw new InvalidOperationException(vname + " not back after resurrection");
            phase6Recs.Add(res);
            checks.Add("PASS: " + vname + " killed and resurrected -> '" + res.title + "' (plus the death photo), keyed per pawn");
            // Ideology: generate the ideo's relic and put it into a reliquary.
            if (ModsConfig.IdeologyActive)
            {
                var precept = Faction.OfPlayer.ideos?.PrimaryIdeo?.PreceptsListForReading.OfType<Precept_Relic>().FirstOrDefault();
                var rdef = DefDatabase<ThingDef>.GetNamedSilentFail("Reliquary");
                if (precept == null || rdef == null) checks.Add("INFO: no relic precept / Reliquary def in this game — relic-photo check skipped");
                else
                {
                    Thing relic = precept.GeneratedRelic ?? precept.GenerateRelic();
                    var reliquary = ThingMaker.MakeThing(rdef, rdef.MadeFromStuff ? GenStuff.DefaultStuffFor(rdef) : null);
                    reliquary.SetFaction(Faction.OfPlayer);
                    var cell = new IntVec3(center.x - 12, 0, center.z - 1);
                    var rep = GenConstruct.CanPlaceBlueprintAt(rdef, cell, Rot4.North, map);
                    if (!rep.Accepted) throw new InvalidOperationException("reliquary placement rejected: " + rep.Reason);
                    GenSpawn.Spawn(reliquary, cell, map, Rot4.North);
                    var comp = reliquary.TryGetComp<CompRelicContainer>();
                    if (comp == null) throw new InvalidOperationException("reliquary has no CompRelicContainer");
                    before = store.Records.Count;
                    if (relic.Spawned) relic.DeSpawn();
                    if (!comp.innerContainer.TryAdd(relic)) throw new InvalidOperationException("reliquary refused the relic " + relic.LabelCap);
                    var rr = store.Records.Skip(before).FirstOrDefault(r => r.kind == "reliquary");
                    if (rr == null) throw new InvalidOperationException("no relic-installed photo");
                    phase6Recs.Add(rr);
                    checks.Add("PASS: relic '" + relic.LabelCap + "' placed in a reliquary -> '" + rr.title + "'");
                }
            }
            else checks.Add("INFO: Ideology inactive — relic-photo check skipped");
            // Anomaly: advance the monolith if this map has one.
            if (ModsConfig.AnomalyActive)
            {
                var mono = Find.Anomaly?.monolith;
                if (mono == null || !mono.Spawned || mono.Map != map) checks.Add("INFO: no void monolith on the probe map — monolith-photo check skipped");
                else
                {
                    before = store.Records.Count;
                    try { mono.Activate(p1); }
                    catch (Exception e) { checks.Add("INFO: monolith activation threw (" + e.GetType().Name + ") — monolith-photo check skipped"); before = -1; }
                    if (before >= 0)
                    {
                        var mr = store.Records.Skip(before).FirstOrDefault(r => r.kind == "monolith");
                        if (mr == null) throw new InvalidOperationException("no monolith photo after activation (level " + Find.Anomaly.Level + ")");
                        Near(mr.spanCells, 15f, "monolith span");
                        phase6Recs.Add(mr);
                        checks.Add("PASS: monolith activated to level " + Find.Anomaly.Level + " -> '" + mr.title + "' (span " + mr.spanCells + ")");
                    }
                }
            }
            else checks.Add("INFO: Anomaly inactive — monolith-photo check skipped");
            Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 14f);
        }

        /// <summary>Lets the phase-6 captures (and the extra death photo) finish, then checks their files.</summary>
        private static bool Phase6Wait()
        {
            var store = PhotoStore.Instance;
            if (store.HasPending) return false;
            foreach (var r in phase6Recs)
            {
                if (!r.HasFile || !File.Exists(store.FilePath(r))) throw new InvalidOperationException("phase-6 photo not written: " + r.title);
                File.Copy(store.FilePath(r), Path.Combine(dir, r.kind + ".png"), true);
            }
            checks.Add("PASS: phase-6 photos written: " + string.Join(", ", phase6Recs.Select(r => r.kind)));
            return true;
        }

        // ---------- phase 7: group photo ----------

        private static Building_PhotoSpot photoSpot;
        private static LordJob_MFGroupPhoto groupJob;
        private static Pawn groupDrafted, groupMidDrafted, groupLead;
        private static PhotoRecord groupRec;
        private static int groupStartTick, groupBefore;
        private static bool groupDraftTested, groupShotChecked;
        private static int groupStep, groupRetakeBefore;
        private static IntVec3 savedSpotCell;
        private static Vector3 savedFrameCenter;
        private static float savedFrameSpan, savedFrameAspect;

        private static Lord GroupLord => map.lordManager.lords.FirstOrDefault(l => l.LordJob == groupJob);

        /// <summary>Which CanJoin conditions a pawn fails (diagnostics for a failed line-up).</summary>
        private static string GroupWhyNot(Pawn p)
        {
            var why = new List<string>();
            if (!p.Spawned) why.Add("unspawned");
            if (p.Dead) why.Add("dead");
            if (!p.IsFreeColonist) why.Add("not free colonist");
            if (p.DevelopmentalStage.Baby()) why.Add("baby");
            if (!p.Awake()) why.Add("asleep (" + p.CurJobDef?.defName + ")");
            if (p.Downed) why.Add("downed");
            if (p.Drafted) why.Add("drafted");
            if (p.InMentalState) why.Add("mental " + p.MentalStateDef?.defName);
            if (p.GetLord() != null) why.Add("lord " + p.GetLord().LordJob?.GetType().Name);
            if (!GatheringsUtility.PawnCanStartOrContinueGathering(p)) why.Add("vanilla gathering start rule");
            if (!GatheringsUtility.ShouldGuestKeepAttendingGathering(p)) why.Add("vanilla guest keep-attending rule");
            if (GroupPhoto.Busy(p)) why.Add("busy (" + p.CurJobDef?.defName + (p.CurJob != null && p.CurJob.playerForced ? ", player-forced" : "") + ")");
            return p.LabelShort + ": " + (why.Count == 0 ? "ok" : string.Join(", ", why));
        }

        private static void Phase7Start()
        {
            var store = PhotoStore.Instance;
            var def = ThingDef.Named("MF_PhotoSpot");
            if (def.GetStatValueAbstract(StatDefOf.WorkToBuild) != 0f || !def.costList.NullOrEmpty() || def.MadeFromStuff) throw new InvalidOperationException("photo spot is not free/instant like a marriage spot");
            var cell = new IntVec3(center.x, 0, center.z - 5);
            var rep = GenConstruct.CanPlaceBlueprintAt(def, cell, Rot4.North, map);
            if (!rep.Accepted) throw new InvalidOperationException("photo spot placement rejected: " + rep.Reason);
            photoSpot = (Building_PhotoSpot)ThingMaker.MakeThing(def);
            photoSpot.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(photoSpot, cell, map);
            var all = map.mapPawns.FreeColonistsSpawned.ToList();
            var preview = GroupFormation.Cells(cell, all.Count, map);
            if (preview.Count != all.Count || preview.Distinct().Count() != all.Count) throw new InvalidOperationException("formation gave " + preview.Count + " cells for " + all.Count + " colonists");
            if (preview.Any(c => c.z <= cell.z || !c.Standable(map))) throw new InvalidOperationException("a formation cell is not behind the spot or not standable");
            checks.Add("PASS: photo spot placed (free, instant); formation for " + all.Count + " = " + preview.Count + " distinct standable cells north of it, rows " + string.Join("/", preview.GroupBy(c => c.z).OrderBy(g => g.Key).Select(g => g.Count())));
            // A big group gets wider rows (ten abreast) so the picture stays near 3:2.
            var big = GroupFormation.Cells(cell, 30, map);
            int bigRows = big.GroupBy(c => c.z).Count();
            if (GroupFormation.PerRow(30) != 10 || big.Count != 30 || bigRows != 3) throw new InvalidOperationException("formation for 30: " + big.Count + " cells in " + bigRows + " rows (per row " + GroupFormation.PerRow(30) + ")");
            checks.Add("PASS: formation for 30 people = 3 rows of 10 (six abreast up to 12, eight up to 24, ten beyond)");
            // A clean slate (r33: the wedding's after-party lord still held everyone, and at night a woken pawn went straight back to bed):
            // no lords, rest and food full, everyone awake. One colonist drafted: the drafted one is left out of the line-up.
            foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
            foreach (var p in all)
            {
                if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.needs?.food != null) p.needs.food.CurLevel = 1f;
                if (!p.Awake()) RestUtility.WakeUp(p);
                if (p.InMentalState) p.MentalState.RecoverFromState();   // r106: the host was wandering confused and the party was called off at once
            }
            groupDrafted = all.LastOrDefault(p => p != p1 && p != p2);
            if (groupDrafted == null) throw new InvalidOperationException("need a third colonist to draft");
            groupDrafted.drafter.Drafted = true;
            // A colonist on a job the player ordered by hand is left out too (review r39: "what about people doing other things").
            var forced = all.FirstOrDefault(p => p != p1 && p != p2 && p != groupDrafted);
            if (forced == null) throw new InvalidOperationException("need a fourth colonist for the forced-job check");
            var forcedJob = JobMaker.MakeJob(JobDefOf.Wait, 900);
            forcedJob.playerForced = true;
            forced.jobs.StartJob(forcedJob, JobCondition.InterruptForced);
            if (!GroupPhoto.Busy(forced)) throw new InvalidOperationException(forced.LabelShort + " on a player-forced job is not Busy");
            if (MFDefs.PoseDuty.hook != ThinkTreeDutyHook.MediumPriority) throw new InvalidOperationException("pose duty hook is " + MFDefs.PoseDuty.hook + ", expected MediumPriority (below emergency work)");
            GroupPhoto.ForgetEligible();
            var elig = GroupPhoto.Eligible(map, cell);
            var expected = all.Where(p => p != groupDrafted && p != forced && GroupPhoto.CanJoin(p)).ToList();
            if (elig.Contains(groupDrafted) || elig.Contains(forced) || elig.Count != expected.Count || elig.Count < 2)
                throw new InvalidOperationException("eligible " + elig.Count + " (expected " + expected.Count + " of " + all.Count + "; drafted excluded: " + !elig.Contains(groupDrafted) + ", forced-job excluded: " + !elig.Contains(forced) + ")\n" + string.Join("\n", all.Select(GroupWhyNot)));
            var report = GroupPhoto.CanStart(map, cell, out var pawns);
            if (!report.Accepted) throw new InvalidOperationException("group photo cannot start: " + report.Reason);
            groupBefore = store.Records.Count;
            var lord = GroupPhoto.Start(map, cell, pawns);
            if (lord == null || !(lord.LordJob is LordJob_MFGroupPhoto job)) throw new InvalidOperationException("group photo lord not created");
            groupJob = job;
            groupStartTick = Find.TickManager.TicksGame;
            groupDraftTested = groupShotChecked = groupRejoinTested = groupLongJobSeen = false;
            groupStep = 0;
            var leftOut = all.Where(p => !lord.ownedPawns.Contains(p)).Select(GroupWhyNot).ToList();
            // Whoever passed the admission rules is a member (r42: p1 himself was unfit that run — heatstroke or the like), so no named pawn is required.
            if (lord.ownedPawns.Count != pawns.Count || lord.ownedPawns.Count < 2) throw new InvalidOperationException("lord owns " + lord.ownedPawns.Count + " of " + pawns.Count + "; left out: " + string.Join(" | ", leftOut));
            groupLead = lord.ownedPawns[0];
            // The line-up for N people is its own layout (r41: six make one row wider than the nine-person preview's rows), so compare with that.
            var layout = GroupFormation.Cells(cell, lord.ownedPawns.Count, map);
            var taken = new HashSet<IntVec3>();
            foreach (var p in lord.ownedPawns)
            {
                if (p.mindState.duty?.def != MFDefs.PoseDuty) throw new InvalidOperationException(p.LabelShort + " has duty " + p.mindState.duty);
                var c = job.CellFor(p);
                if (c == cell || !layout.Contains(c) || !taken.Add(c)) throw new InvalidOperationException(p.LabelShort + " has no distinct formation cell (" + c + "; layout " + string.Join(" ", layout) + ")");
            }
            checks.Add("PASS: group photo started with " + pawns.Count + " colonists (drafted " + groupDrafted.LabelShort + " and player-ordered " + forced.LabelShort + " left out; duty hook MediumPriority), each with the pose duty on a distinct formation cell");
            checks.Add("INFO: left out of the line-up: " + string.Join(" | ", leftOut));
            groupDrafted.drafter.Drafted = false;   // stays out: the line-up was fixed at the start
            forced.jobs.EndCurrentJob(JobCondition.InterruptForced);
            Find.CameraDriver.SetRootPosAndSize(cell.ToVector3Shifted() + new Vector3(0f, 0f, 2f), 14f);
        }

        private static bool groupRejoinTested;
        private static bool groupLongJobSeen;
        private static int groupUndraftTick, groupMembers;

        private static string PawnState(Pawn p) =>
            p.LabelShort + "@" + p.Position + " job " + p.CurJobDef?.defName + (p.CurJob != null && p.CurJob.playerForced ? "(forced)" : "") + ", duty " + p.mindState.duty + ", lord " + p.GetLord()?.LordJob?.GetType().Name
            + ", drafted " + p.Drafted + ", canJoin " + GroupPhoto.CanJoin(p) + ", priority " + (groupJob != null ? groupJob.VoluntaryJoinPriorityFor(p) : -1f);

        /// <summary>Drives the gathering in 100-tick batches: a mid-way draft drops only that person (announced), the others reach their cells
        /// and hold — and nothing is taken while one of the called is missing; undrafted, the drop-out rejoins by themselves; at the shutter
        /// everyone called stands on their cell facing south; afterwards the record, its framing, the disbanded lord and the cleared duties are checked.</summary>
        private static bool Phase7Run()
        {
            var store = PhotoStore.Instance;
            var job = groupJob;
            var lord = GroupLord;
            int elapsed = Find.TickManager.TicksGame - groupStartTick;
            if (elapsed > 9000) throw new TimeoutException("group photo not finished within 9000 ticks: outcome " + job.Result + ", shooting " + job.Shooting + ", lord alive " + (lord != null) + ", in place " + job.InPlace + " of " + lord?.ownedPawns.Count + ", roster " + job.Roster);
            if (!groupDraftTested && elapsed >= 100)
            {
                if (lord == null) throw new InvalidOperationException("lord gone before the drop-out test: " + job.Result);
                var mid = lord.ownedPawns.FirstOrDefault(p => p != groupLead);
                if (mid == null) throw new InvalidOperationException("need a second member for the drop-out test");
                int n = lord.ownedPawns.Count;
                groupMembers = n;
                mid.drafter.Drafted = true;
                if (lord.ownedPawns.Contains(mid) || lord.ownedPawns.Count != n - 1 || GroupLord == null) throw new InvalidOperationException("drafting " + mid.LabelShort + " mid-way: still a member " + lord.ownedPawns.Contains(mid) + ", count " + lord.ownedPawns.Count + ", lord alive " + (GroupLord != null));
                if (mid.mindState.duty != null) throw new InvalidOperationException("the drop-out kept a duty: " + mid.mindState.duty);
                if (!job.Dropped.Contains(mid) || job.DropReason(mid).NullOrEmpty() || job.Complete) throw new InvalidOperationException("drop-out not recorded: dropped " + job.Dropped.Contains(mid) + ", reason '" + job.DropReason(mid) + "', complete " + job.Complete);
                groupMidDrafted = mid;
                mid.drafter.Drafted = false;
                // Straight back to a long job (r102: studying an entity kept them away past 2500 ticks). The vanilla join node only runs when
                // a job ends — 4000 ticks here — so rejoining within 2500 needs the line to call them back.
                var longJob = JobMaker.MakeJob(JobDefOf.Wait);
                longJob.expiryInterval = 4000;
                mid.jobs.StartJob(longJob, JobCondition.InterruptForced);
                groupUndraftTick = Find.TickManager.TicksGame;
                groupDraftTested = true;
                checks.Add("PASS: drafting " + mid.LabelShort + " mid-way dropped only them (" + lord.ownedPawns.Count + " remain, lord alive, reason '" + job.DropReason(mid) + "', line-up incomplete)");
            }
            if (groupDraftTested && !groupRejoinTested)
            {
                if (job.Shooting || job.Finished || job.Holding) throw new InvalidOperationException("photo taken while " + groupMidDrafted.LabelShort + " was out of the line (" + job.Result + ", in place " + job.InPlace + ")");
                int sinceUndraft = Find.TickManager.TicksGame - groupUndraftTick;
                if (!groupLongJobSeen && sinceUndraft < GroupPhoto.RejoinCooldownTicks)
                {
                    // Negative control: during the cooldown nothing pulls them, and the long job holds them (vanilla alone would until it ends).
                    if (lord != null && lord.ownedPawns.Contains(groupMidDrafted)) throw new InvalidOperationException("rejoined during the cooldown (" + sinceUndraft + " ticks)");
                    if (groupMidDrafted.CurJobDef != JobDefOf.Wait) throw new InvalidOperationException("the long job did not hold the drop-out: " + PawnState(groupMidDrafted));
                    groupLongJobSeen = true;
                }
                if (lord != null && lord.ownedPawns.Contains(groupMidDrafted))
                {
                    if (groupMidDrafted.mindState.duty?.def != MFDefs.PoseDuty) throw new InvalidOperationException("rejoined without the pose duty: " + PawnState(groupMidDrafted));
                    if (job.Dropped.Contains(groupMidDrafted)) throw new InvalidOperationException("rejoined but still listed as dropped");
                    groupRejoinTested = true;
                    if (!groupLongJobSeen) throw new InvalidOperationException("the long job was never seen holding the drop-out (batches too coarse?)");
                    checks.Add("PASS: undrafted and sent to a 4000-tick job, " + groupMidDrafted.LabelShort + " was held by it through the cooldown, then called back to the line after " + sinceUndraft + " ticks (nothing was taken meanwhile; " + job.InPlace + " of " + lord.ownedPawns.Count + " in place)");
                }
                else if (Find.TickManager.TicksGame - groupUndraftTick > 2500)
                    throw new InvalidOperationException("drop-out did not rejoin within 2500 ticks: " + PawnState(groupMidDrafted) + "; in place " + job.InPlace + " of " + lord?.ownedPawns.Count);
            }
            if (job.Shooting && !groupShotChecked)
            {
                if (lord == null) throw new InvalidOperationException("shooting without a lord");
                if (!groupRejoinTested) throw new InvalidOperationException("shooting before the drop-out rejoined");
                if (lord.ownedPawns.Count != groupMembers) throw new InvalidOperationException("at the shutter " + lord.ownedPawns.Count + " members, expected " + groupMembers);
                foreach (var p in lord.ownedPawns)
                    if (p.Position != job.CellFor(p) || p.pather.Moving || p.Rotation != Rot4.South)
                        throw new InvalidOperationException("at the shutter " + p.LabelShort + " is at " + p.Position + " (cell " + job.CellFor(p) + "), moving " + p.pather.Moving + ", facing " + p.Rotation
                            + ", job " + p.CurJobDef?.defName + (p.CurJob != null && p.CurJob.playerForced ? "(forced)" : "") + ", duty " + p.mindState.duty + ", hediffs " + string.Join("/", p.health.hediffSet.hediffs.Select(h => h.def.defName)) + ", " + elapsed + " ticks in");
                groupShotChecked = true;
                checks.Add("PASS: at the shutter all " + lord.ownedPawns.Count + " stood on their cells facing south, " + elapsed + " ticks after the start");
                return false;   // let the frame render the capture
            }
            if (job.Finished) throw new InvalidOperationException("group photo ended (" + job.Result + ", " + job.Takes + " taken) instead of holding the line after the picture");
            if (groupStep == 1) return Phase7Retake(job);
            if (job.Holding)
            {
                if (!groupShotChecked) throw new InvalidOperationException("group photo taken without the probe seeing the pose");
                var rec = store.Records.Skip(groupBefore).FirstOrDefault(r => r.kind == GroupPhoto.Kind);
                if (rec == null) throw new InvalidOperationException("no group record; " + job.Takes + " taken");
                if (!rec.HasFile)
                {
                    if (!store.Records.Contains(rec)) throw new InvalidOperationException("group capture failed (record dropped)");
                    return false;
                }
                if (rec.people.Count != job.ShotCount || rec.people.Count != groupMembers || !rec.Has(groupMidDrafted) || rec.Has(groupDrafted) || !rec.Has(groupLead)) throw new InvalidOperationException("people " + string.Join(",", rec.people) + " vs shot count " + job.ShotCount + " / members " + groupMembers);
                if (rec.desc.NullOrEmpty()) throw new InvalidOperationException("group photo has no sentence");
                var covered = PhotoCapture.CoveredRect(rec.center, rec.spanCells, map, rec.Aspect);
                foreach (var p in job.Members) if (!covered.Contains(job.CellFor(p))) throw new InvalidOperationException("photo does not cover " + p.LabelShort + "'s cell " + job.CellFor(p) + " (rect " + covered + ")");
                File.Copy(store.FilePath(rec), Path.Combine(dir, "group.png"), true);
                groupRec = rec;
                checks.Add(CaptureDiagLine("group", job.Members));
                RequirePeople("group.png");   // the picture must actually contain people (r36: empty ground although everyone stood in frame)
                checks.Add("PASS: group photo '" + rec.title + "' with all " + rec.people.Count + " called (span " + rec.spanCells + ", aspect " + rec.Aspect.ToString("0.00") + ") covers every cell; sentence: " + rec.desc);
                // The line holds after the picture until the player dismisses it (user 09-21: "해산 버튼 누를 때까지 림들 가만히 있게").
                int count = store.Records.Count;
                for (int i = 0; i < 300; i++) Find.TickManager.DoSingleTick();
                var held = GroupLord;
                if (held == null || !job.Holding) throw new InvalidOperationException("the line-up did not hold after the picture: lord alive " + (held != null) + ", " + job.Result);
                if (held.ownedPawns.Count != groupMembers || job.InPlace != held.ownedPawns.Count) throw new InvalidOperationException("holding: " + job.InPlace + " of " + held.ownedPawns.Count + " in place (called " + groupMembers + "); " + string.Join(" | ", held.ownedPawns.Select(PawnState)));
                if (store.Records.Count != count) throw new InvalidOperationException("another picture was taken by itself while holding");
                var labels = photoSpot.GetGizmos().OfType<Command_Action>().Select(g => g.defaultLabel).ToList();
                string retake = "MF_GroupRetake".Translate(held.ownedPawns.Count), dismiss = "MF_GroupDismiss".Translate();
                if (!labels.Contains(retake) || !labels.Contains(dismiss)) throw new InvalidOperationException("holding spot buttons: " + string.Join(" / ", labels));
                checks.Add("PASS: after the picture the line holds — all " + held.ownedPawns.Count + " still on their cells 300 ticks later, nothing taken by itself; the spot offers '" + retake + "' and '" + dismiss + "'");
                groupRetakeBefore = store.Records.Count;
                job.ShootNow();   // "take another"
                groupStep = 1;
                return false;
            }
            for (int i = 0; i < 100; i++)
            {
                Find.TickManager.DoSingleTick();
                if (job.Shooting || job.Finished || job.Holding) break;
                if (groupDraftTested && !groupRejoinTested && GroupLord != null && GroupLord.ownedPawns.Contains(groupMidDrafted)) break;
            }
            SettleDraw();   // the shot renders next frame: draw positions must be on the cells (see SettleDraw)
            return false;
        }

        /// <summary>"Take another" on the holding line: a second picture of its own (the first stays), the line still holds; then "dismiss" lets
        /// everyone go with the outcome Dismissed and no pose duty left.</summary>
        private static bool Phase7Retake(LordJob_MFGroupPhoto job)
        {
            var store = PhotoStore.Instance;
            if (job.Holding && job.Takes >= 2)
            {
                var rec2 = store.Records.Skip(groupRetakeBefore).FirstOrDefault(r => r.kind == GroupPhoto.Kind);
                if (rec2 == null) throw new InvalidOperationException("'take another' made no record");
                if (!rec2.HasFile) return false;
                if (rec2.id == groupRec.id || rec2.eventKey == groupRec.eventKey || !store.Records.Contains(groupRec) || rec2.people.Count != groupMembers)
                    throw new InvalidOperationException("second picture: id " + rec2.id + " / " + groupRec.id + ", key " + rec2.eventKey + " / " + groupRec.eventKey + ", people " + rec2.people.Count + " of " + groupMembers);
                checks.Add("PASS: 'take another' added a second picture of its own (" + rec2.people.Count + " people, key " + rec2.eventKey + "); the first is still in the album and the line still holds");
                job.Cancel();   // "dismiss"
                for (int i = 0; i < 20 && GroupLord != null; i++) Find.TickManager.DoSingleTick();
                if (GroupLord != null) throw new InvalidOperationException("lord still alive after 'dismiss'");
                if (job.Result != LordJob_MFGroupPhoto.Outcome.Dismissed) throw new InvalidOperationException("dismiss outcome " + job.Result);
                foreach (var p in job.Members) if (p.mindState.duty?.def == MFDefs.PoseDuty) throw new InvalidOperationException(p.LabelShort + " kept the pose duty after 'dismiss'");
                checks.Add("PASS: 'dismiss' let everyone go (outcome Dismissed, pose duties cleared)");
                groupStep = 2;
                return true;
            }
            if (Find.TickManager.TicksGame - groupStartTick > 12000) throw new TimeoutException("'take another' gave no second picture: shooting " + job.Shooting + ", holding " + job.Holding + ", taken " + job.Takes);
            for (int i = 0; i < 30; i++)
            {
                if (job.Shooting || job.Takes >= 2) break;   // the capture renders next frame
                Find.TickManager.DoSingleTick();
            }
            SettleDraw();
            return false;
        }

        private static LordJob_MFGroupPhoto group2Job;
        private static Pawn group2Busy;
        private static int group2Step, group2Before, group2Start;

        /// <summary>A second gathering: one member is given a job by hand mid-way and drops out (reason: a job ordered by hand); the rest hold
        /// their cells and nothing is taken; "take it now" on the spot photographs those in place. Then a third gathering is cancelled from the
        /// spot's button: lord gone, no record, nobody bound.</summary>
        private static bool Phase7Second()
        {
            var store = PhotoStore.Instance;
            switch (group2Step)
            {
                case 0:
                    {
                        // The player's own framing for this spot (user 09-21: "유저가 직접 범위 정하게끔 (드래그로?)"), set through the spot's tool.
                        var sp = photoSpot.Position;
                        var drag = CellRect.FromLimits(sp.x - 9, sp.z - 1, sp.x + 9, sp.z + 9);
                        new Designator_MFSpotFrame(photoSpot).DesignateMultiCell(drag.Cells);
                        if (!photoSpot.TryGetFrame(out Vector3 fc, out float fs, out float fa)) throw new InvalidOperationException("the framing tool set no framing");
                        Designator_TakePhoto.FitRect(drag, out Vector3 wc, out float ws, out float wa);
                        if ((fc - wc).magnitude > 0.01f || Mathf.Abs(fs - ws) > 0.01f || Mathf.Abs(fa - wa) > 0.01f)
                            throw new InvalidOperationException("framing " + fc + " span " + fs + " aspect " + fa + ", the drag gives " + wc + " / " + ws + " / " + wa);
                        var fr = photoSpot.FrameRect;
                        string insp = photoSpot.GetInspectString();
                        if (!insp.Contains("MF_SpotFramed".Translate(fr.Width, fr.Height).ToString())) throw new InvalidOperationException("the spot does not show its framing: " + insp);
                        if (!photoSpot.GetGizmos().OfType<Command_Action>().Any(g => g.defaultLabel == "MF_SpotFrameClear".Translate().ToString())) throw new InvalidOperationException("no 'automatic framing' button once framed");
                        checks.Add("PASS: framing tool — a " + drag.Width + "x" + drag.Height + " drag set the spot's framing (span " + fs.ToString("0.#") + ", aspect " + fa.ToString("0.00") + "); the spot shows it and offers 'automatic framing'");
                        GroupPhoto.ForgetEligible();
                        var report = GroupPhoto.CanStart(map, photoSpot.Position, out var pawns);
                        if (!report.Accepted) throw new InvalidOperationException("second group photo cannot start: " + report.Reason);
                        group2Before = store.Records.Count;
                        var lord = GroupPhoto.Start(map, photoSpot.Position, pawns);
                        if (lord == null || lord.ownedPawns.Count < 3) throw new InvalidOperationException("second group photo lord not created with 3+ members");
                        group2Job = (LordJob_MFGroupPhoto)lord.LordJob;
                        group2Start = Find.TickManager.TicksGame;
                        for (int i = 0; i < 30; i++) Find.TickManager.DoSingleTick();
                        group2Busy = lord.ownedPawns.Last();
                        var forcedJob = JobMaker.MakeJob(JobDefOf.Wait, 1500);
                        forcedJob.playerForced = true;
                        group2Busy.jobs.StartJob(forcedJob, JobCondition.InterruptForced);
                        group2Step = 1;
                        return false;
                    }
                case 1:
                    {
                        var lord = map.lordManager.lords.FirstOrDefault(l => l.LordJob == group2Job);
                        if (lord == null) throw new InvalidOperationException("second lord ended early: " + group2Job.Result);
                        int el = Find.TickManager.TicksGame - group2Start;
                        if (!lord.ownedPawns.Contains(group2Busy))
                        {
                            if (group2Job.DropReason(group2Busy) != "MF_Why_Busy".Translate().ToString()) throw new InvalidOperationException("drop reason '" + group2Job.DropReason(group2Busy) + "'");
                            if (group2Job.Complete) throw new InvalidOperationException("line-up complete although " + group2Busy.LabelShort + " is out");
                            checks.Add("PASS: a job ordered by hand mid-way dropped " + group2Busy.LabelShort + " (reason '" + group2Job.DropReason(group2Busy) + "'), " + lord.ownedPawns.Count + " remain");
                            group2Step = 2;
                            return false;
                        }
                        if (el > 400) throw new InvalidOperationException(group2Busy.LabelShort + " on a forced job not dropped after " + el + " ticks: " + PawnState(group2Busy));
                        for (int i = 0; i < 50; i++) Find.TickManager.DoSingleTick();
                        SettleDraw();
                        return false;
                    }
                case 2:
                    {
                        var lord = map.lordManager.lords.FirstOrDefault(l => l.LordJob == group2Job);
                        if (lord == null) throw new InvalidOperationException("second lord ended while the rest were gathering: " + group2Job.Result);
                        if (group2Job.Shooting) throw new InvalidOperationException("photo taken by itself although " + group2Busy.LabelShort + " is out");
                        int el = Find.TickManager.TicksGame - group2Start;
                        if (group2Job.InPlace == lord.ownedPawns.Count && el >= 700)
                        {
                            // Everyone left stood in place for hundreds of ticks and nothing was taken: the player decides.
                            checks.Add("PASS: with one member out, the other " + lord.ownedPawns.Count + " held their cells and nothing was taken by itself (" + el + " ticks); 'take it now' pressed");
                            group2Job.ShootNow();
                            group2Step = 3;
                            return false;
                        }
                        if (el > 3000) throw new InvalidOperationException("the rest never all stood in place: " + group2Job.InPlace + " of " + lord.ownedPawns.Count + "; " + string.Join(" | ", lord.ownedPawns.Select(PawnState)));
                        for (int i = 0; i < 50; i++) Find.TickManager.DoSingleTick();
                        SettleDraw();
                        return false;
                    }
                case 3:
                    {
                        if (group2Job.Finished) throw new InvalidOperationException("'take it now' ended the line-up (" + group2Job.Result + ") instead of holding it");
                        if (!group2Job.Holding)
                        {
                            if (Find.TickManager.TicksGame - group2Start > 4500) throw new InvalidOperationException("'take it now' did not end in a photo: shooting " + group2Job.Shooting + ", outcome " + group2Job.Result);
                            for (int i = 0; i < 50; i++) { if (group2Job.Shooting || group2Job.Holding) break; Find.TickManager.DoSingleTick(); }
                            SettleDraw();
                            return false;
                        }
                        var rec = store.Records.Skip(group2Before).FirstOrDefault(r => r.kind == GroupPhoto.Kind);
                        if (rec == null) throw new InvalidOperationException("'take it now': no record, " + group2Job.Takes + " taken");
                        if (!rec.HasFile) return false;
                        if (rec.Has(group2Busy) || rec.people.Count != group2Job.ShotCount) throw new InvalidOperationException("'take it now' people " + string.Join(",", rec.people) + " (busy " + group2Busy.LabelShort + ", shot " + group2Job.ShotCount + ")");
                        photoSpot.TryGetFrame(out Vector3 fc, out float fs, out float fa);
                        if ((rec.center - fc).magnitude > 0.01f || Mathf.Abs(rec.spanCells - fs) > 0.01f || Mathf.Abs(rec.Aspect - fa) > 0.01f)
                            throw new InvalidOperationException("the picture ignored the spot's framing: " + rec.center + " / " + rec.spanCells + " / " + rec.Aspect + " vs " + fc + " / " + fs + " / " + fa);
                        if (group2Busy.CurJobDef == JobDefOf.Wait) group2Busy.jobs.EndCurrentJob(JobCondition.InterruptForced);
                        File.Copy(store.FilePath(rec), Path.Combine(dir, "group-framed.png"), true);
                        checks.Add("PASS: 'take it now' photographed the " + rec.people.Count + " in place without " + group2Busy.LabelShort + ", framed exactly as the spot's framing (span " + rec.spanCells.ToString("0.#") + ", aspect " + rec.Aspect.ToString("0.00") + "): '" + rec.title + "'");
                        // What the player sees: the spot selected, its line-up (white) and its framing (amber). A letter left open by an
                        // earlier phase (r79: the monolith's) would cover the map.
                        foreach (var w in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) w.Close(false);
                        Find.Selector.ClearSelection();
                        Find.Selector.Select(photoSpot, false);
                        Find.CameraDriver.SetRootPosAndSize(photoSpot.FrameRect.CenterVector3, 22f);
                        Wait(0.6f);
                        group2Step = 5;
                        return false;
                    }
                case 5:
                    Shot("spot-frame.png");
                    Wait(0.8f);
                    group2Step = 6;
                    return false;
                case 6:
                    Require("spot-frame.png");
                    Find.Selector.ClearSelection();
                    group2Job.Cancel();   // the line held; "dismiss"
                    for (int i = 0; i < 20 && map.lordManager.lords.Any(l => l.LordJob == group2Job); i++) Find.TickManager.DoSingleTick();
                    if (map.lordManager.lords.Any(l => l.LordJob == group2Job) || group2Job.Result != LordJob_MFGroupPhoto.Outcome.Dismissed) throw new InvalidOperationException("second line-up after 'dismiss': " + group2Job.Result);
                    photoSpot.TryGetFrame(out savedFrameCenter, out savedFrameSpan, out savedFrameAspect);   // kept for the save/load check
                    group2Step = 4;
                    return false;
                case 4:
                    Phase7Cancel();
                    return true;
            }
            return false;
        }

        /// <summary>A third gathering, cancelled from the spot's button after 30 ticks: lord gone, no record, nobody bound.</summary>
        private static void Phase7Cancel()
        {
            var store = PhotoStore.Instance;
            for (int i = 0; i < 20 && map.lordManager.lords.Any(l => l.LordJob is LordJob_MFGroupPhoto); i++) Find.TickManager.DoSingleTick();
            GroupPhoto.ForgetEligible();
            var report = GroupPhoto.CanStart(map, photoSpot.Position, out var pawns);
            if (!report.Accepted) throw new InvalidOperationException("third group photo cannot start: " + report.Reason);
            int before = store.Records.Count;
            var lord = GroupPhoto.Start(map, photoSpot.Position, pawns);
            if (lord == null) throw new InvalidOperationException("third group photo lord not created");
            var job = (LordJob_MFGroupPhoto)lord.LordJob;
            for (int i = 0; i < 30; i++) Find.TickManager.DoSingleTick();
            if (!map.lordManager.lords.Contains(lord)) throw new InvalidOperationException("third lord ended early: " + job.Result);
            job.Cancel();
            for (int i = 0; i < 5 && map.lordManager.lords.Contains(lord); i++) Find.TickManager.DoSingleTick();
            if (map.lordManager.lords.Contains(lord)) throw new InvalidOperationException("cancel did not disband the lord");
            if (store.Records.Count != before) throw new InvalidOperationException("cancel produced a record");
            foreach (var p in pawns) if (p.GetLord() == lord || p.mindState.duty?.def == MFDefs.PoseDuty) throw new InvalidOperationException(p.LabelShort + " still bound after the cancel");
            checks.Add("PASS: third gathering of " + pawns.Count + " cancelled from the spot's button: lord gone, no record, duties cleared");
            Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 14f);
        }

        private static MainButtonDef AlbumDef => DefDatabase<MainButtonDef>.GetNamed("MF_Album");

        private static void AlbumOpen(bool chronicle, bool withManual)
        {
            var def = AlbumDef;
            if (!(def.TabWindow is MainTabWindow_Album album)) throw new InvalidOperationException("album tab window is " + def.TabWindow?.GetType().Name);
            album.ShowChronicle(chronicle, withManual);
            if (Find.MainTabsRoot.OpenTab != def) Find.MainTabsRoot.SetCurrentTab(def, false);
            if (Find.MainTabsRoot.OpenTab != def) throw new InvalidOperationException("album tab did not open");
        }

        /// <summary>Record ids/years/delay flag, the pawn photo tab, the HTML export, and the thumbnail cap.</summary>
        private static void AlbumChecks()
        {
            var store = PhotoStore.Instance;
            if (testRec.peopleIds.Count != testRec.people.Count || testRec.peopleIds.Count < 2) throw new InvalidOperationException("peopleIds " + testRec.peopleIds.Count + " vs people " + testRec.people.Count);
            if (!testRec.Has(p1)) throw new InvalidOperationException("test record does not list " + p1.LabelShort + " by id");
            if (testRec.year <= 0) throw new InvalidOperationException("record year " + testRec.year);
            if (testRec.capturedTicksAbs < testRec.ticksAbs || testRec.Delayed) throw new InvalidOperationException("immediate capture flagged delayed: " + testRec.ticksAbs + " -> " + testRec.capturedTicksAbs);
            checks.Add("PASS: record stores pawn ids (" + testRec.peopleIds.Count + "), year " + testRec.year + ", capture tick; not flagged delayed");
            int expected = store.Records.Count(r => r.HasFile && r.Has(p1));
            int counted = store.CountFor(p1);
            if (counted != expected || expected == 0) throw new InvalidOperationException("CountFor " + counted + " != " + expected);
            // The chronicle tab sits just before Health in the list (= drawn to its right) and lists the same photos, oldest first.
            var tabs = p1.def.inspectorTabsResolved;
            int chronTab = tabs.FindIndex(t => t is ITab_Pawn_Chronicle), healthTab = tabs.FindIndex(t => t is ITab_Pawn_Health);
            if (chronTab < 0 || healthTab != chronTab + 1) throw new InvalidOperationException("chronicle tab at " + chronTab + ", health at " + healthTab + " (" + string.Join(",", tabs.Select(t => t.GetType().Name)) + ")");
            if (p1.GetGizmos().OfType<Command_Action>().Any(g => g.defaultLabel == "MF_PawnPhotos".Translate(expected).ToString())) throw new InvalidOperationException("the old photo gizmo is still there");
            var tab = (ITab_Pawn_Chronicle)tabs[chronTab];
            var listed = tab.EntriesOf(p1, false).ToList();
            // Events they took part in are listed too; their own photos must all be there.
            if (listed.Count(r => r.HasFile && r.Has(p1)) != expected) throw new InvalidOperationException("chronicle tab lists " + listed.Count(r => r.HasFile && r.Has(p1)) + " of their photos != " + expected);
            for (int i = 1; i < listed.Count; i++) if (listed[i].ticksAbs < listed[i - 1].ticksAbs) throw new InvalidOperationException("chronicle tab not oldest first at " + i);
            int milestones = store.Records.Count(r => r.HasFile && r.Has(p1) && (Kinds.IsMilestone(r.kind) || Kinds.PersonalOnly(r.kind)));   // a person's own photos count in their chronicle
            int listedMilestones = tab.EntriesOf(p1, true).Count;
            if (listedMilestones != milestones) throw new InvalidOperationException("milestones only lists " + listedMilestones + " != " + milestones + ": "
                + string.Join(" | ", tab.EntriesOf(p1, true).Select(r => r.kind + " file=" + r.HasFile + " has=" + r.Has(p1) + " eventOnly=" + r.eventOnly + " tick=" + r.ticksAbs + " '" + r.title + "'"))
                + " (now " + Find.TickManager.TicksAbs + ")");
            checks.Add("PASS: " + p1.LabelShort + " has the chronicle tab right of Health: " + expected + " photos oldest first, " + milestones + " with 'milestones only' (no gizmo)");
            string html = AlbumExport.Write(store);
            if (!File.Exists(html) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(html)), Path.GetFullPath(store.FolderPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("album.html not written in the photo folder: " + html);
            string text = File.ReadAllText(html);
            int inAlbum = 0, birthdays = 0;
            foreach (var rec in store.Records.Where(r => r.HasFile))
            {
                string href = AlbumExport.Href(store, rec);
                if (href == null) throw new InvalidOperationException("no file for " + rec.id);
                bool inPage = text.Contains("\"f\":\"" + AlbumExport.Json(href) + "\"");
                if (Kinds.PersonalOnly(rec.kind)) { birthdays++; if (inPage) throw new InvalidOperationException("album.html lists the birthday photo " + rec.id); }
                else { inAlbum++; if (!inPage) throw new InvalidOperationException("album.html lacks photo " + rec.id); }
            }
            if (AlbumExport.Template() == null || !text.Contains("/*MF_DATA*/{\"colony\"")) throw new InvalidOperationException("album.html was not made from the mod's Html/album.html template");
            string evil = AlbumExport.Json("a\"b</script>\n\\c");
            if (evil.Contains("</") || evil.Contains("\n") || evil != "a\\\"b\\u003c/script>\\n\\\\c") throw new InvalidOperationException("JSON escaping: " + evil);
            File.WriteAllText(Path.Combine(dir, "album-location.txt"), html);
            checks.Add("PASS: album.html in the photo folder lists all " + inAlbum + " album photos and none of " + birthdays + " birthday photos (" + new FileInfo(html).Length + " HTML bytes): " + html);
            if (PhotoTextureCache.ThumbCount > 240) throw new InvalidOperationException("thumb cache " + PhotoTextureCache.ThumbCount);
            checks.Add("PASS: thumbnail cache " + PhotoTextureCache.ThumbCount + " <= 240");
        }

        private static void FxSet(bool on)
        {
            var s = MomentFramesMod.Settings;
            s.fxFlash = s.fxShutter = s.fxPopup = s.fxFreeze = on;
            s.fxCamera = false;   // every other phase frames its own screenshots; CameraChecks() turns it on by itself
        }

        /// <summary>"The same people again": who the photo's people are now (alive here / dead / away), and that calling them starts a line-up
        /// that will frame its picture exactly like the old one.</summary>
        private static void ReunionChecks()
        {
            var store = PhotoStore.Instance;
            var group = store.Records.FirstOrDefault(r => r.kind == GroupPhoto.Kind && r.peopleIds.Count > 1);
            if (group == null) throw new InvalidOperationException("no group photo to re-shoot with its people");
            if (!Reunion.Possible(group)) throw new InvalidOperationException("the group photo cannot be re-shot with its people");

            var before = Reunion.Candidates(group);
            if (before.Count != group.peopleIds.Count) throw new InvalidOperationException("candidates " + before.Count + " for " + group.peopleIds.Count + " people");
            var ready = before.Where(c => c.CanCome).Select(c => c.pawn).ToList();
            if (ready.Count == 0) throw new InvalidOperationException("nobody in the group photo can come: " + string.Join(", ", before.Select(c => c.name + "=" + c.state)));

            // someone who died is offered as a name, never as a body
            var victim = ready[ready.Count - 1];
            string victimName = victim.LabelShort;
            victim.Kill(null);
            var after = Reunion.Candidates(group);
            var dead = after.FirstOrDefault(c => c.name == victimName);
            if (dead.CanCome || dead.state != Reunion.State.Dead)
                throw new InvalidOperationException("a dead colonist is still offered: " + victimName + " = " + dead.state);
            ResurrectionUtility.TryResurrect(victim);
            checks.Add("PASS: reunion roster — " + before.Count + " in the photo, " + ready.Count + " can come; a dead one (" + victimName + ") is listed but not callable");

            var callable = Reunion.Candidates(group).Where(c => c.CanCome).Select(c => c.pawn).ToList();
            if (callable.Count == 0) { checks.Add("INFO: nobody free right after the resurrection — reunion start not exercised"); return; }
            if (!Reunion.Start(group, callable)) throw new InvalidOperationException("the reunion did not start");
            var lord = map.lordManager.lords.FirstOrDefault(l => (l.LordJob as LordJob_MFGroupPhoto)?.ReunionOf == group.id);
            if (lord == null) throw new InvalidOperationException("the reunion lord was not created");
            var job = (LordJob_MFGroupPhoto)lord.LordJob;
            if (job.Members.Count == 0 || job.Cells.Count != job.Members.Count) throw new InvalidOperationException("reunion line-up " + job.Members.Count + " people, " + job.Cells.Count + " cells");
            checks.Add("PASS: the reunion called " + job.Members.Count + " of them back to " + group.center.ToIntVec3() + " to re-make '" + group.title + "'");
            // The line-up must survive its own state machine: it stands where the old photo was taken, where no photo spot stands (v2.4.0 called it
            // off within 15 ticks as "the spot is gone").
            for (int i = 0; i < 120 && map.lordManager.lords.Contains(lord); i++) Find.TickManager.DoSingleTick();
            if (!map.lordManager.lords.Contains(lord) || job.Finished) throw new InvalidOperationException("the reunion line-up ended by itself within 120 ticks: " + job.Result);
            checks.Add("PASS: the reunion line-up is still gathering after 120 ticks (no photo spot needed where the old photo was taken)");
            var member = lord.ownedPawns.FirstOrDefault();
            if (member != null && !member.GetGizmos().OfType<Command_Action>().Any(g => g.defaultDesc == "MF_GroupCancelDesc".Translate().ToString()))
                throw new InvalidOperationException("the reunion's members have no cancel button (it has no photo spot)");
            map.lordManager.RemoveLord(lord);
            foreach (var p in job.Members) p.jobs?.EndCurrentJob(JobCondition.InterruptForced, false);
        }

        /// <summary>The event-photo zoom: the same scene framed at 1.0, 1.1 (closer) and 0.9 (wider), read off the actual span the framing
        /// produces. A bigger number must give a smaller span.</summary>
        private static void ZoomChecks()
        {
            var s = MomentFramesMod.Settings;
            float prev = s.eventZoom;
            var people = map.mapPawns.FreeColonistsSpawned.Take(3).ToList();
            float Span(float zoom)
            {
                s.eventZoom = zoom;
                Hooks.AutoFrame(map, center, people, Hooks.GatherMinSpan, Hooks.GatherMaxSpan, out _, out float span, out _);
                return span;
            }
            try
            {
                float one = Span(1f), closer = Span(1.1f), wider = Span(0.9f);
                if (closer >= one || wider <= one)
                    throw new InvalidOperationException("zoom does nothing: 1.0 = " + one + ", 1.1 = " + closer + ", 0.9 = " + wider);
                if (Mathf.Abs(closer - one / 1.1f) > 0.01f || Mathf.Abs(wider - one / 0.9f) > 0.01f)
                    throw new InvalidOperationException("zoom is not a straight division: 1.0 = " + one + ", 1.1 = " + closer + ", 0.9 = " + wider);
                checks.Add("PASS: event zoom — span " + one.ToString("0.#") + " at 1.0, " + closer.ToString("0.#") + " at 1.1 (closer), " + wider.ToString("0.#") + " at 0.9 (wider)");
            }
            finally { s.eventZoom = prev; }
        }

        private static string editOriginalHash;
        private static PhotoEdit editMono, editSepia;

        private static string Sha(string path) => BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(File.ReadAllBytes(path))).Replace("-", "").Substring(0, 16);

        /// <summary>Photo editing, part 1. The filter arithmetic against hand-worked values; a crop+black-and-white edit baked to its own file (half
        /// the size, grey everywhere) that becomes what the photo is shown as; a second edit; the original file byte-identical throughout.</summary>
        private static void EditChecksA()
        {
            // Hand-worked: (100,150,200) -> mono 0.299*100+0.587*150+0.114*200 = 140.75 -> 141; sepia r 192.45->192, g 171.4->171, b 133.5->134.
            var mono = PhotoFilters.One(new Color32(100, 150, 200, 255), PhotoFilters.Mono);
            var sepia = PhotoFilters.One(new Color32(100, 150, 200, 255), PhotoFilters.Sepia);
            if (mono.r != 141 || mono.g != 141 || mono.b != 141) throw new InvalidOperationException("mono filter gave " + mono);
            if (sepia.r != 192 || sepia.g != 171 || sepia.b != 134) throw new InvalidOperationException("sepia filter gave " + sepia);
            checks.Add("PASS: filters match hand-worked values (mono 141,141,141; sepia 192,171,134)");

            var store = PhotoStore.Instance;
            string original = store.OriginalPath(testRec);
            if (original == null || !File.Exists(original)) throw new InvalidOperationException("no original file to edit");
            editOriginalHash = Sha(original);
            int w0, h0;
            {
                var t = new Texture2D(2, 2);
                t.LoadImage(File.ReadAllBytes(original), false);
                w0 = t.width; h0 = t.height;
                UnityEngine.Object.Destroy(t);
            }

            editMono = store.SaveEdit(testRec, new Rect(0.25f, 0.25f, 0.5f, 0.5f), PhotoFilters.Mono);
            if (editMono == null) throw new InvalidOperationException("the crop + mono edit was not saved");
            string ep = store.EditPath(editMono);
            if (!File.Exists(ep) || !File.Exists(store.EditThumbPath(editMono))) throw new InvalidOperationException("edit file or its thumbnail missing");
            var tex = new Texture2D(2, 2);
            try
            {
                tex.LoadImage(File.ReadAllBytes(ep), false);
                if (Math.Abs(tex.width - w0 / 2) > 1 || Math.Abs(tex.height - h0 / 2) > 1)
                    throw new InvalidOperationException("crop size " + tex.width + "x" + tex.height + ", expected half of " + w0 + "x" + h0);
                var px = tex.GetPixels32();
                int grey = 0, step = Math.Max(1, px.Length / 4000), seen = 0;
                for (int i = 0; i < px.Length; i += step) { seen++; if (px[i].r == px[i].g && px[i].g == px[i].b) grey++; }
                if (grey != seen) throw new InvalidOperationException("black-and-white edit has colour: " + (seen - grey) + " of " + seen + " samples");
                checks.Add("PASS: crop + black-and-white edit baked to its own file: " + tex.width + "x" + tex.height + " (half of " + w0 + "x" + h0 + "), " + seen + " samples all grey");
            }
            finally { UnityEngine.Object.Destroy(tex); }

            if (store.FilePath(testRec) != ep || store.ThumbPath(testRec) != store.EditThumbPath(editMono))
                throw new InvalidOperationException("the new edit is not what the photo is shown as: " + store.FilePath(testRec));
            if (Math.Abs(testRec.DisplayAspect - editMono.aspect) > 0.001f || Math.Abs(testRec.Aspect - (float)w0 / h0) > 0.05f)
                throw new InvalidOperationException("aspects: display " + testRec.DisplayAspect + " / edit " + editMono.aspect + " / capture " + testRec.Aspect);

            editSepia = store.SaveEdit(testRec, new Rect(0f, 0f, 1f, 1f), PhotoFilters.Sepia);
            if (editSepia == null || testRec.edits.Count != 2 || testRec.shownEdit != editSepia.id) throw new InvalidOperationException("second edit: " + testRec.edits.Count + " edits, shown " + testRec.shownEdit);
            if (editSepia.file == editMono.file) throw new InvalidOperationException("two edits share a file name");
            if (Sha(original) != editOriginalHash) throw new InvalidOperationException("the original file changed while editing");
            checks.Add("PASS: second edit (sepia) listed and shown; the original file is byte-identical after two edits");
            store.SetShown(testRec, editMono.id);
        }

        /// <summary>The whole-mod review of 2026-09-30, one check per fix that a run can observe. Listed, not thrown, so one miss does not hide
        /// the others.</summary>
        private static void ReviewFixes()
        {
            var store = PhotoStore.Instance;
            void C(bool ok, string what) => checks.Add((ok ? "PASS" : "FAIL") + ": " + what);

            // Patches are applied one class at a time: none failed, and every patch class was applied.
            int patchClasses = typeof(MomentFramesMod).Assembly.GetTypes().Count(t => t.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false).Length > 0);
            C(MomentFramesMod.FailedPatches.Count == 0 && MomentFramesMod.AppliedPatches == patchClasses,
                "every patch class applied on its own (" + MomentFramesMod.AppliedPatches + "/" + patchClasses + " applied, failed: [" + string.Join(",", MomentFramesMod.FailedPatches) + "])");

            // The photo folder is worked out once (the same string comes back) and again when the setting changes.
            string f1 = store.FolderPath, f2 = store.FolderPath, saved = MomentFramesMod.Settings.folderOverride;
            MomentFramesMod.Settings.folderOverride = Path.Combine(Path.GetTempPath(), "mf-probe-folder");
            string f3 = store.FolderPath;
            MomentFramesMod.Settings.folderOverride = saved;
            string f4 = store.FolderPath;
            C(ReferenceEquals(f1, f2) && f3 != f1 && f4 == f1, "the photo folder is cached and follows a change of the setting");

            // A map display goes to the disk only when its photo or its shown version changes, not on every draw.
            frame.RefreshBinding();
            int n0 = FramePhotoBinding.Lookups;
            frame.RefreshBinding(); frame.RefreshBinding(); frame.RefreshBinding();
            int n1 = FramePhotoBinding.Lookups;
            store.SetShown(testRec, null);
            frame.RefreshBinding(); frame.RefreshBinding();
            int n2 = FramePhotoBinding.Lookups;
            store.SetShown(testRec, editMono.id);
            frame.RefreshBinding();
            C(n1 == n0 && n2 == n1 + 1 && (frame.BoundPath == store.EditThumbPath(editMono) || frame.BoundPath == store.EditPath(editMono)),
                "a frame looks its file up only when the shown version changes (" + (n1 - n0) + " lookups for 3 redraws, " + (n2 - n1) + " for a version change)");

            // A missing thumbnail is made again from the photo, instead of the full image being cached in its place.
            string thumb = store.OriginalThumbPath(testRec), full = store.OriginalPath(testRec);
            File.Delete(thumb);
            PhotoTextureCache.Forget(thumb);
            int made = PhotoTextureCache.ThumbsMade;
            var tex = PhotoTextureCache.Thumb(thumb, full);
            C(tex != null && tex.height <= PhotoCapture.ThumbHeight && File.Exists(thumb) && PhotoTextureCache.ThumbsMade == made + 1,
                "a missing thumbnail is remade from the photo (" + (tex != null ? tex.width + "x" + tex.height : "none") + ", file back: " + File.Exists(thumb) + ")");

            // The polaroid tilt stays in its range for the thing ids of a long game.
            bool tiltOk = true;
            foreach (int id in new[] { 1, 7, 271183, 300000, 5000000, int.MaxValue })
            {
                float t = FrameLook.Tilt(id);
                if (t < -3.5f || t > 3.5f || Mathf.Abs(t) < 1f) tiltOk = false;
            }
            C(tiltOk, "polaroid tilt stays within -3.5..3.5 degrees for large thing ids");

            // A one-time moment whose photo was dropped gets one more try, and only one.
            var drop = HarmonyLib.AccessTools.Method(typeof(PhotoStore), "Drop");
            string mark = "probe:once:" + Find.TickManager.TicksGame;
            store.AddMark(mark);
            drop.Invoke(store, new object[] { new PhotoRecord { id = "probe-drop-1", eventKey = mark } });
            bool released = !store.HasMark(mark);
            store.AddMark(mark);
            drop.Invoke(store, new object[] { new PhotoRecord { id = "probe-drop-2", eventKey = mark } });
            C(released && store.HasMark(mark), "a dropped one-time photo frees its mark once, and keeps it after a second failure");

            // The fallback album page writes its tilts with a dot under a comma locale, and its language follows the game's.
            var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
            string html = null, why = null;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                html = (string)HarmonyLib.AccessTools.Method(typeof(AlbumExport), "Plain").Invoke(null, new object[] { store, store.Records.Where(r => r.HasFile).ToList() });
            }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = culture; }
            if (html == null) checks.Add("INFO: fallback album page not checked (" + why + ")");
            else
            {
                bool dot = System.Text.RegularExpressions.Regex.IsMatch(html, @"--t:-?\d+\.\d+deg") && !System.Text.RegularExpressions.Regex.IsMatch(html, @"--t:-?\d+,\d+deg");
                string lang = LanguageDatabase.activeLanguage.folderName.StartsWith("Korean") ? "ko" : "en";
                C(dot && html.Contains("<html lang=\"" + lang + "\">"), "fallback album page: tilts written with a dot under a German locale, lang=\"" + lang + "\"");
            }
        }

        /// <summary>The ending steps that live in quest state (Endings.cs) cannot be staged here, so their hooks are called with stand-in
        /// quests: each is photographed once, the archonexus moves count 1 then 2, and another quest's move is ignored. The records made are
        /// removed again so later checks see the album unchanged.</summary>
        private static void EndingHooks()
        {
            var store = PhotoStore.Instance;
            var royal = DefDatabase<QuestScriptDef>.GetNamedSilentFail("EndGame_RoyalAscent");
            var arch = DefDatabase<QuestScriptDef>.GetNamedSilentFail("EndGame_ArchonexusVictory_FirstCycle");
            if (royal == null || arch == null) { checks.Add("INFO: ending quest hooks not checked (Royalty/Ideology quests not loaded)"); return; }
            int before = store.Records.Count;
            var ascent = HarmonyLib.AccessTools.Method(typeof(Patch_EndingRoyalAscent), "Postfix");
            var shuttle = HarmonyLib.AccessTools.Method(typeof(Patch_EndingRoyalShuttle), "Postfix");
            var move = HarmonyLib.AccessTools.Method(typeof(Patch_EndingArchonexusMove), "Postfix");
            var royalQuest = new Quest { root = royal };
            ascent.Invoke(null, new object[] { royalQuest });
            ascent.Invoke(null, new object[] { royalQuest });   // the same quest again: no second photo
            shuttle.Invoke(null, new object[] { new QuestPart_Delay { quest = royalQuest } });   // a plain delay of that quest: not the shuttle
            shuttle.Invoke(null, new object[] { new QuestPart_ShuttleDelay { quest = royalQuest } });
            var archQuest = new Quest { root = arch };
            for (int i = 0; i < 3; i++) move.Invoke(null, new object[] { new QuestPart_NewColony { quest = archQuest } });
            move.Invoke(null, new object[] { new QuestPart_NewColony { quest = royalQuest } });   // not an archonexus quest
            var made = store.Records.Skip(before).Select(r => r.eventKey).ToList();
            string got = string.Join(",", made);
            checks.Add((got == "ending:royalascent,ending:royalshuttle,ending:archonexusmove1,ending:archonexusmove2" ? "PASS" : "FAIL")
                + ": ending quest hooks photograph the royal ascent, its shuttle and two archonexus moves, each once (" + got + ")");
            foreach (var r in store.Records.Skip(before).ToList()) store.Remove(r);
        }

        /// <summary>Moments without a photo (LifeEvents.cs), through the game's own calls: lovers then a break-up between two colonists, a hand
        /// lost to a gunshot (and one cut off in surgery, which must not count), a newcomer found by the poll (once), a bond with an animal,
        /// and a kidnapping (its hook called directly). The colonists there from the start must not be "joined". Relations, the hands and the
        /// newcomer are put back afterwards; the lines stay, so the chronicle tab screenshot later shows them.</summary>
        private static void LifeEventsCheck()
        {
            var store = PhotoStore.Instance;
            void C(bool ok, string what) => checks.Add((ok ? "PASS" : "FAIL") + ": " + what);
            C(!store.LifeEvents.Any(e => e.kind == LifeEvents.Joined || e.kind == LifeEvents.Born), "life events: the colonists there from the start are not recorded as joining (" + store.LifeEvents.Count + " lines so far)");
            var others = map.mapPawns.FreeColonistsSpawned.Where(x => x != p1 && x != p2 && x.ageTracker.AgeBiologicalYears >= 16).ToList();
            if (others.Count < 1) { checks.Add("INFO: life events not checked (no third adult colonist)"); return; }
            Pawn a = p1, b = others[0];
            string ida = a.GetUniqueLoadID(), idb = b.GetUniqueLoadID();
            foreach (var def in new[] { PawnRelationDefOf.Lover, PawnRelationDefOf.ExLover }) a.relations.TryRemoveDirectRelation(def, b);

            int n0 = store.LifeEvents.Count;
            a.relations.AddDirectRelation(PawnRelationDefOf.Lover, b);
            a.relations.RemoveDirectRelation(PawnRelationDefOf.Lover, b);
            a.relations.AddDirectRelation(PawnRelationDefOf.ExLover, b);
            a.relations.RemoveDirectRelation(PawnRelationDefOf.ExLover, b);
            var rel = store.LifeEvents.Skip(n0).ToList();
            string lineA = rel.Count > 0 ? rel[0].LineFor(ida) : "", lineB = rel.Count > 0 ? rel[0].LineFor(idb) : "";
            C(string.Join(",", rel.Select(e => e.kind)) == "lover,brokeup" && lineA.Contains(b.LabelShort) && lineB.Contains(a.LabelShort),
                "life events: lovers then a break-up, one line each for both (" + string.Join(",", rel.Select(e => e.kind)) + "; " + lineA + " / " + lineB + ")");

            int n1 = store.LifeEvents.Count;
            var hands = a.RaceProps.body.AllParts.Where(x => x.def.defName == "Hand" && !a.health.hediffSet.PartIsMissing(x)).ToList();
            if (hands.Count >= 2)
            {
                var shot = (Hediff_MissingPart)HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, a, hands[0]);
                shot.lastInjury = DefDatabase<HediffDef>.GetNamed("Gunshot");
                a.health.AddHediff(shot, hands[0]);
                var cut = (Hediff_MissingPart)HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, a, hands[1]);
                cut.lastInjury = HediffDefOf.SurgicalCut;
                a.health.AddHediff(cut, hands[1]);
                var limb = store.LifeEvents.Skip(n1).ToList();
                string line = limb.Count > 0 ? limb[0].LineFor(ida) : "";
                a.health.RestorePart(hands[0]);
                a.health.RestorePart(hands[1]);
                C(limb.Count == 1 && limb[0].kind == LifeEvents.Limb && line.Contains(hands[0].Label),
                    "life events: a hand lost to a gunshot is a line, one cut off in surgery is not (" + limb.Count + " line: " + line + ")");
            }
            else checks.Add("INFO: lost limb not checked (" + a.LabelShort + " has " + hands.Count + " hands)");

            int n2 = store.LifeEvents.Count;
            var newcomer = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(newcomer, CellFinder.RandomClosewalkCellNear(center, map, 6), map);
            LifeEvents.Poll(store);
            LifeEvents.Poll(store);
            var joined = store.LifeEvents.Skip(n2).ToList();
            string newcomerName = newcomer.LabelShort;
            newcomer.Destroy(DestroyMode.Vanish);
            C(joined.Count == 1 && joined[0].kind == LifeEvents.Joined && joined[0].names.FirstOrDefault() == newcomerName,
                "life events: a new colonist is found by the poll once (" + string.Join(",", joined.Select(e => e.kind + ":" + e.names.FirstOrDefault())) + ")");

            int n3 = store.LifeEvents.Count;
            var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Husky") ?? DefDatabase<PawnKindDef>.GetNamedSilentFail("Muffalo");
            if (kind != null)
            {
                var dog = PawnGenerator.GeneratePawn(kind, Faction.OfPlayer);
                GenSpawn.Spawn(dog, CellFinder.RandomClosewalkCellNear(center, map, 6), map);
                b.relations.AddDirectRelation(PawnRelationDefOf.Bond, dog);
                var bond = store.LifeEvents.Skip(n3).ToList();
                string line = bond.Count > 0 ? bond[0].LineFor(idb) : "";
                b.relations.TryRemoveDirectRelation(PawnRelationDefOf.Bond, dog);
                dog.Destroy(DestroyMode.Vanish);
                C(bond.Count == 1 && bond[0].kind == LifeEvents.Bond && bond[0].pawnIds.SequenceEqual(new[] { idb }), "life events: a bond with an animal, on the person only (" + line + ")");
            }

            int n4 = store.LifeEvents.Count;
            var enemy = Find.FactionManager.RandomEnemyFaction(false, false, true);
            if (enemy != null)
            {
                HarmonyLib.AccessTools.Method(typeof(Patch_LifeCaptured), "Prefix").Invoke(null, new object[] { b, enemy });
                var cap = store.LifeEvents.Skip(n4).ToList();
                C(cap.Count == 1 && cap[0].kind == LifeEvents.Captured && cap[0].LineFor(idb).Contains(enemy.Name), "life events: carried off by raiders (" + (cap.Count > 0 ? cap[0].LineFor(idb) : "none") + ")");
            }
        }

        /// <summary>Fights the colony won (Battles.cs). Through the hook: a big threat letter for a raid opens a fight, an enemy falls, the map is
        /// calm, the poll takes the photo framed where the enemy fell. Then the rules with stand-in fights: a later easy fight is skipped, a hard
        /// one waits for the season, and the four settings. Photos made here are removed again; the settings are put back.</summary>
        private static void BattlesCheck()
        {
            var store = PhotoStore.Instance;
            var settings = MomentFramesMod.Settings;
            void C(bool ok, string what) => checks.Add((ok ? "PASS" : "FAIL") + ": " + what);
            var enemy = Find.FactionManager.RandomEnemyFaction(false, false, true);
            var raidDef = DefDatabase<IncidentDef>.GetNamedSilentFail("RaidEnemy");
            if (enemy == null || raidDef == null) { checks.Add("INFO: fights not checked (no enemy faction or raid incident)"); return; }
            int savedMode = settings.battleMode, savedCalm = Battles.CalmTicks, savedLast = store.LastBattleTick;
            settings.battleMode = Battles.ModeHard;
            Battles.CalmTicks = 0;
            Battles.Reset();
            int before = store.Records.Count;
            try
            {
                // The hook path: the letter of a raid on this map.
                // Match the real incident: spawned enemies exist when the raid letter opens the fight.
                IntVec3 fell = CellFinder.RandomClosewalkCellNear(center + new IntVec3(12, 0, 0), map, 3);
                var raider = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, enemy);
                GenSpawn.Spawn(raider, fell, map);
                var parms = new IncidentParms { target = map, faction = enemy };
                HarmonyLib.AccessTools.Method(typeof(Patch_BattleLetter), "Postfix").Invoke(null, new object[] { LetterDefOf.ThreatBig, parms, raidDef });
                var fight = Battles.Active.FirstOrDefault(f => f.map == map);
                raider.Kill(null);
                if (fight != null) fight.sawDanger = true;
                HarmonyLib.Traverse.Create(typeof(Battles)).Field("lastCheck").SetValue(-99999);
                Battles.Poll(store, Find.TickManager.TicksGame);
                var made = store.Records.Skip(before).Where(r => r.kind == Battles.Kind).ToList();
                bool near = made.Count == 1 && !made[0].eventOnly && fight.enemies.Contains(raider)
                    && new Vector3(made[0].center.x, 0f, made[0].center.z).ToIntVec3().InHorDistOf(fell, 12f);
                C(fight != null && fight.type == "raid" && made.Count == 1 && near && Battles.Active.Count == 0,
                    "fights: a raid letter opens a fight, and once calm the first raid is photographed where the enemy fell (" + (made.Count > 0 ? made[0].title : "no photo") + ")");

                // The rules, with stand-in fights after that first raid.
                int now = Find.TickManager.TicksGame;
                var cols = map.mapPawns.FreeColonistsSpawned.ToList();
                Battles.Fight Make(string type, int enemies) => new Battles.Fight { map = map, type = type, startTick = now, colonists = cols, mostEnemies = enemies, sawDanger = true };
                var easy = Make("raid", 1);
                var hard = Make("raid", cols.Count);
                bool skipEasy = !Battles.Takes(store, easy, now);
                bool waitSeason = !Battles.Takes(store, hard, now);
                store.LastBattleTick = now - Battles.SeasonTicks;
                bool hardLater = Battles.Takes(store, hard, now);
                bool firstSiege = Battles.Takes(store, Make("siege", 0), now);
                settings.battleMode = Battles.ModeFirsts;
                bool firstsOnly = !Battles.Takes(store, hard, now) && Battles.Takes(store, Make("siege", 0), now);
                settings.battleMode = Battles.ModeEvery;
                bool every = Battles.Takes(store, easy, now);
                settings.battleMode = Battles.ModeOff;
                bool off = !Battles.Takes(store, Make("siege", 0), now);
                C(skipEasy && waitSeason && hardLater && firstSiege && firstsOnly && every && off,
                    "fights: a later easy raid is skipped, a hard one waits a season, a first siege is taken; first-only / every / off settings ("
                    + skipEasy + "," + waitSeason + "," + hardLater + "," + firstSiege + "," + firstsOnly + "," + every + "," + off + ")");
            }
            finally
            {
                foreach (var r in store.Records.Skip(before).ToList()) store.Remove(r);
                settings.battleMode = savedMode;
                Battles.CalmTicks = savedCalm;
                store.LastBattleTick = savedLast;
                Battles.Reset();
            }
        }

        /// <summary>Condition polling queues only visible hours, deduplicates each activation, and leaves successful-photo cooldowns untouched.
        /// The focused review-fixes mode separately waits for real PNGs and checks expiry, hour changes and game save/load.</summary>
        private static GameCondition falloutCondition;
        private static PhotoRecord falloutRec;
        private static float falloutUntil;
        private static PhotoRecord falloutBase;

        private static void ConditionsCheck()
        {
            var store = PhotoStore.Instance;
            void C(bool ok, string what) => checks.Add((ok ? "PASS" : "FAIL") + ": " + what);
            int before = store.Records.Count, savedD = store.LastDisasterTick, savedS = store.LastSkyTick;
            int activation = 0;
            try
            {
                var poll = HarmonyLib.Traverse.Create(typeof(Conditions)).Field("lastCheck");
                // One condition on the map for a moment at a given hour, polled twice (the same one must not make a second photo); how many photos of that kind it made.
                int Shots(string defName, int hour, string kind)
                {
                    int n0 = store.Records.Count(r => r.kind == kind);
                    var c = GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed(defName), GenDate.TicksPerDay);
                    map.gameConditionManager.RegisterCondition(c);
                    c.startTick = Find.TickManager.TicksGame - 3 * GenDate.TicksPerHour - ++activation;
                    Conditions.ProbeHour = hour;
                    for (int i = 0; i < 2; i++) { poll.SetValue(-99999); Conditions.Poll(store, Find.TickManager.TicksGame); }
                    Conditions.ProbeHour = -1;
                    c.End();
                    return store.Records.Count(r => r.kind == kind) - n0;
                }
                int coldDay = Shots("ColdSnap", 12, Conditions.KindDisaster);
                int heatNight = Shots("HeatWave", 22, Conditions.KindDisaster);
                int heatDay = Shots("HeatWave", 12, Conditions.KindDisaster);
                int coldAgain = Shots("ColdSnap", 12, Conditions.KindDisaster);
                var dis = store.Records.Skip(before).Where(r => r.kind == Conditions.KindDisaster).Select(r => r.title).ToList();
                C(coldDay == 1 && heatNight == 0 && heatDay == 1 && coldAgain == 1 && store.LastDisasterTick == savedD
                    && !store.HasMark("disaster:first:ColdSnap") && !store.HasMark("disaster:first:HeatWave"),
                    "disaster polling queues distinct activations by day, deduplicates each one, and consumes no successful-photo cooldown before PNG save ("
                    + coldDay + "/" + heatNight + "/" + heatDay + "/" + coldAgain + "; " + string.Join(" / ", dis) + ")");

                int outside = map.mapPawns.FreeColonistsSpawned.Count(p => !p.Downed && !p.Position.Roofed(map));
                if (outside == 0) { checks.Add("INFO: sky not checked (no colonist outside)"); return; }
                // One sky event at a given hour: how many sky photos it made (polled twice — the same event must not make a second).
                int SkyShots(string defName, int hour)
                {
                    int n0 = store.Records.Count(r => r.kind == Conditions.KindSky);
                    var c = GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed(defName), GenDate.TicksPerDay);
                    map.gameConditionManager.RegisterCondition(c);
                    c.startTick = Find.TickManager.TicksGame - 3 * GenDate.TicksPerHour - ++activation;
                    Conditions.ProbeHour = hour;
                    for (int i = 0; i < 2; i++) { poll.SetValue(-99999); Conditions.Poll(store, Find.TickManager.TicksGame); }
                    Conditions.ProbeHour = -1;
                    c.End();
                    return store.Records.Count(r => r.kind == Conditions.KindSky) - n0;
                }
                int auroraNight = SkyShots("Aurora", 22);
                int eclipseNight = SkyShots("Eclipse", 22);
                int eclipseDay = SkyShots("Eclipse", 12);
                var sk = store.Records.Skip(before).Where(r => r.kind == Conditions.KindSky).ToList();
                C(auroraNight == 1 && eclipseNight == 0 && eclipseDay == 1 && sk.All(r => r.people.Count > 0 && !r.HasFile)
                    && store.LastSkyTick == savedS && !store.HasMark("sky:first:Aurora") && !store.HasMark("sky:first:Eclipse"),
                    "sky polling queues the visible hour with people outside and consumes no successful-photo cooldown before PNG save (" + auroraNight + "/" + eclipseNight + "/" + eclipseDay + "; "
                    + string.Join(" / ", sk.Select(r => r.title + ": " + string.Join(", ", r.people))) + ")");
            }
            finally
            {
                foreach (var r in store.Records.Skip(before).ToList()) store.Remove(r);
                store.LastDisasterTick = savedD;
                store.LastSkyTick = savedS;
                Conditions.ProbeHour = -1;
            }
        }

        private static Lord nightLord;
        private static PhotoRecord nightRec;
        private static float nightUntil;
        private static List<Pawn> nightPresent = new List<Pawn>();

        /// <summary>Memory night from the projector's button: the lord is made, people come over and face the picture.</summary>
        private static void MemoryNightStart()
        {
            string why = MemoryNight.CannotStart(projector);
            if (why != null) throw new InvalidOperationException("memory night cannot start: " + why);
            IntVec3 gather = MemoryNight.GatherCell(projector);
            string Hosts() => string.Join(", ", map.mapPawns.FreeColonistsSpawned.Select(x => x.LabelShort + ":" + (MemoryNight.HostProblem(x, gather) ?? "free")));
            checks.Add("INFO: memory night hosts as phase 4 left them: " + Hosts());
            // The same clean slate as the party and the group photo (the phases before move the clock on and leave lords and sleepers about).
            foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
            foreach (var x in map.mapPawns.FreeColonistsSpawned)
            {
                if (x.needs?.rest != null) x.needs.rest.CurLevel = 1f;
                if (x.needs?.food != null) x.needs.food.CurLevel = 1f;
                if (!x.Awake()) RestUtility.WakeUp(x);
                if (x.InMentalState) x.MentalState.RecoverFromState();
                if (x.Drafted) x.drafter.Drafted = false;
            }
            ClearHostiles(map);
            if (!MemoryNight.Start(projector)) throw new InvalidOperationException("memory night did not start (no host found): " + Hosts());
            nightLord = map.lordManager.lords.LastOrDefault(l => l.LordJob is LordJob_MFMemoryNight);
            if (nightLord == null) throw new InvalidOperationException("memory night started but no lord");
            for (int i = 0; i < 1500; i++) Find.TickManager.DoSingleTick();   // people walk over and take their places
            // Watching = a spectate job on THIS picture (its focus is the screen's centre) from a cell within the game's 14.5-cell spectating
            // reach. r132 counted any spectate job and passed while everyone still stood in the removed wedding's spectator row.
            CellRect screen = MemoryNight.ScreenRect(projector);
            bool Watches(Pawn x) => x.CurJobDef == JobDefOf.SpectateCeremony && x.CurJob.targetB.Cell == screen.CenterCell
                && screen.ClosestCellTo(x.CurJob.targetA.Cell).DistanceTo(x.CurJob.targetA.Cell) <= 14.5f;
            string Seats() => string.Join(", ", nightLord.ownedPawns.Select(x => x.LabelShort + ":" + x.CurJobDef?.defName + (x.CurJob == null ? "" :
                "@" + (x.CurJob.targetA.Cell - screen.CenterCell).x + "," + (x.CurJob.targetA.Cell - screen.CenterCell).z + (Watches(x) ? "" : " (not this picture)"))));
            var stray = nightLord.ownedPawns.Where(x => !Watches(x)).ToList();
            if (stray.Count > 0)
            {
                // Removing a live lord leaves its jobs running (the wedding's spectators kept standing in their row); in play a pawn joins from
                // its own think tree, so its old job is over. End those jobs and let the night's duty seat them.
                checks.Add("INFO: memory night before ending jobs left over from removed lords: " + Seats());
                foreach (var x in stray) x.jobs.EndCurrentJob(JobCondition.InterruptForced);
                for (int i = 0; i < 600; i++) Find.TickManager.DoSingleTick();
            }
            Find.CameraDriver.SetRootPosAndSize(MemoryNight.ScreenCenter(projector), 11f);
            SettleDraw();   // paused, the drawn positions never catch up with the ticks (r132/r134 showed the wedding's row)
            int watching = nightLord.ownedPawns.Count(Watches);
            checks.Add((nightLord.ownedPawns.Count >= 2 && watching >= nightLord.ownedPawns.Count - 1 ? "PASS" : "FAIL") + ": memory night opened from the projector: "
                + nightLord.ownedPawns.Count + " came, " + watching + " watching this picture (seat relative to the screen: " + Seats() + ")");
        }

        /// <summary>Tick the night up to its photo (two fifths in): how many of those there stand inside its frame at that moment (r132's photo
        /// held the projector and two people at the edge). Stops there so the capture renders them in their seats.</summary>
        private static void MemoryNightToPhoto()
        {
            var job = (LordJob_MFMemoryNight)nightLord.LordJob;
            nightPresent = nightLord.ownedPawns.ToList();
            nightRec = null;
            for (int i = 0; i < job.DurationTicks && nightRec == null && map.lordManager.lords.Contains(nightLord); i++)
            {
                Find.TickManager.DoSingleTick();
                nightRec = PhotoStore.Instance.Records.LastOrDefault(x => x.kind == MemoryNight.Kind);
            }
            SettleDraw();
            if (nightRec == null || !nightRec.HasCenter) { checks.Add("FAIL: memory night photo never asked for while the night was on"); return; }
            float halfW = nightRec.spanCells * nightRec.Aspect / 2f, halfH = nightRec.spanCells / 2f;
            var owned = nightLord.ownedPawns;
            int inFrame = owned.Count(x => Mathf.Abs(x.Position.x + 0.5f - nightRec.center.x) <= halfW && Mathf.Abs(x.Position.z + 0.5f - nightRec.center.z) <= halfH);
            checks.Add((inFrame >= 2 && inFrame >= owned.Count - 1 ? "PASS" : "FAIL") + ": memory night photo frames the audience: " + inFrame + " of " + owned.Count
                + " inside the frame when it was asked for (" + Find.TickManager.TicksGame + ", span " + nightRec.spanCells.ToString("0.#") + ")");
        }

        /// <summary>Run the night to its end: the memory for those who were there, and no party photo for it.</summary>
        private static void MemoryNightFinish()
        {
            var job = (LordJob_MFMemoryNight)nightLord.LordJob;
            string partyKey = Hooks.Key("party", job);
            int guard = job.DurationTicks + 3000;
            for (int i = 0; i < guard && map.lordManager.lords.Contains(nightLord); i++) Find.TickManager.DoSingleTick();
            bool ended = !map.lordManager.lords.Contains(nightLord);
            int remembered = nightPresent.Count(x => x.needs?.mood?.thoughts?.memories?.GetFirstMemoryOfDef(MemoryNight.Thought) != null);
            bool partyShot = PhotoStore.Instance.HasKey(partyKey);
            checks.Add((ended && remembered >= 1 && nightRec != null && !partyShot ? "PASS" : "FAIL") + ": memory night ran its course (" + job.DurationTicks + " ticks): ended "
                + ended + ", " + remembered + "/" + nightPresent.Count + " got '" + MemoryNight.Thought.stages[0].label + "', its photo '" + (nightRec?.title ?? "none") + "', party photo " + partyShot);
        }

        /// <summary>The night's photo on disk, and the year's letter: once for a year, with its count, opening the album on that year.</summary>
        private static void MemoryNightPhotoAndYear()
        {
            var store = PhotoStore.Instance;
            if (nightRec != null && nightRec.HasFile)
            {
                File.Copy(store.FilePath(nightRec), Path.Combine(dir, "memory-night-photo.png"), true);
                checks.Add("PASS: memory night photo written with " + nightRec.people.Count + " people (memory-night-photo.png)");
            }
            else checks.Add("FAIL: memory night photo not captured (" + (nightRec == null ? "not requested" : "no file after 15 s") + ")");
            int year = store.Records.Where(r => r.HasFile && r.year != 0).GroupBy(r => r.year).OrderByDescending(g => g.Count()).First().Key;
            int count = store.Records.Count(r => r.HasFile && r.year == year && !Kinds.PersonalOnly(r.kind) && r.kind != Kinds.Relic);
            var letter = YearInPhotos.Check(store, year + 1);
            bool once = YearInPhotos.Check(store, year + 1) == null;
            bool album = false;
            if (letter != null)
            {
                letter.Choices.First().action();
                album = AlbumDef.TabWindow is MainTabWindow_Album w && w.YearFilter == year;
                Find.MainTabsRoot.EscapeCurrentTab(false);
                Find.LetterStack.RemoveLetter(letter);
            }
            checks.Add((letter != null && letter.year == year && letter.Text.ToString().Contains(count.ToString()) && once && album ? "PASS" : "FAIL")
                + ": the year's letter for " + year + " (" + count + " photos) came once and opened the album on that year (" + (letter != null ? letter.Label.ToString() : "no letter") + ")");
        }

        /// <summary>Photo editing: a frame hung with the photo follows the version it is shown as (it rebinds on its own).</summary>
        private static void EditFrameFollows()
        {
            var store = PhotoStore.Instance;
            frame.RefreshBinding();
            string want = store.EditThumbPath(editMono);
            if (frame.BoundPath != want && frame.BoundPath != store.EditPath(editMono)) throw new InvalidOperationException("frame shows " + frame.BoundPath + ", expected the mono edit " + want);
            store.SetShown(testRec, null);
            frame.RefreshBinding();
            if (frame.BoundPath != store.OriginalThumbPath(testRec) && frame.BoundPath != store.OriginalPath(testRec)) throw new InvalidOperationException("frame did not go back to the original: " + frame.BoundPath);
            store.SetShown(testRec, editMono.id);
            checks.Add("PASS: the hung frame follows the shown version (edit, then original, then edit again)");
        }

        /// <summary>Photo editing, part 2: deleting the shown edit brings the original back and removes its files; one edit is kept (not shown)
        /// so the save/load round trip later proves edits persist.</summary>
        private static void EditChecksB()
        {
            var store = PhotoStore.Instance;
            string monoFile = store.EditPath(editMono), monoThumb = store.EditThumbPath(editMono);
            store.DeleteEdit(testRec, editMono.id);
            if (File.Exists(monoFile) || File.Exists(monoThumb)) throw new InvalidOperationException("deleted edit left its files behind");
            if (testRec.Shown != null || store.FilePath(testRec) != store.OriginalPath(testRec)) throw new InvalidOperationException("after deleting the shown edit the photo is shown as " + store.FilePath(testRec));
            if (testRec.edits.Count != 1 || testRec.edits[0].id != editSepia.id) throw new InvalidOperationException("edits left: " + testRec.edits.Count);
            if (Sha(store.OriginalPath(testRec)) != editOriginalHash) throw new InvalidOperationException("the original file changed");
            checks.Add("PASS: deleting the shown edit removes its files and brings the original back; the sepia edit stays (not shown) for the save/load check");
        }

        // ---------- the journey: a new place in another biome (a landmark if the world has one), reached by two colonists ----------

        private static RimWorld.Planet.PlanetTile journeyTile = RimWorld.Planet.PlanetTile.Invalid;
        private static Map journeyMap, journeyHome;
        private static readonly List<Pawn> journeyPawns = new List<Pawn>();
        private static int journeyStartTick, journeyBefore;

        /// <summary>The home tile is the journey's start; then a map is made on the nearest tile of another biome (preferring one with a landmark)
        /// and two colonists are moved there, with that map on screen so its arrival photo can be taken.</summary>
        private static void JourneyStart()
        {
            var store = PhotoStore.Instance;
            journeyHome = map;
            if (!store.Visited(map.Tile)) throw new InvalidOperationException("the home tile is not the journey's first place (" + store.Visits.Count + " places)");
            checks.Add("PASS: journey — the home tile " + map.Tile + " (" + map.Biome.defName + ") is the first place");
            var grid = Find.WorldGrid;
            string homeBiome = map.Biome.defName;
            bool Ok(RimWorld.Planet.PlanetTile t) => t.Valid && t.Layer == map.Tile.Layer && !Find.World.Impassable(t) && !Find.WorldObjects.AnyWorldObjectAt(t)
                && grid[t].PrimaryBiome != null && grid[t].PrimaryBiome.defName != homeBiome && grid[t].PrimaryBiome.canBuildBase && !store.Visited(t);
            RimWorld.Planet.PlanetTile best = RimWorld.Planet.PlanetTile.Invalid;
            if (ModsConfig.OdysseyActive && Find.World.landmarks != null)
                best = Find.World.landmarks.landmarks.Keys.Where(Ok).OrderBy(t => grid.ApproxDistanceInTiles(map.Tile, t)).FirstOrDefault();
            if (!best.Valid)
            {
                var surface = grid.Surface;
                float bestD = float.MaxValue;
                for (int i = 0; i < surface.TilesCount; i++)
                {
                    var t = surface.PlanetTileForID(i);
                    if (!Ok(t)) continue;
                    float d = grid.ApproxDistanceInTiles(map.Tile, t);
                    if (d < bestD) { bestD = d; best = t; }
                }
            }
            if (!best.Valid) throw new InvalidOperationException("no tile of another biome to travel to");
            journeyTile = best;
            var parentDef = WorldObjectDefOf.Camp ?? WorldObjectDefOf.AbandonedSettlement;
            journeyMap = GetOrGenerateMapUtility.GetOrGenerateMap(best, new IntVec3(75, 1, 75), parentDef);
            if (journeyMap == null) throw new InvalidOperationException("the map at " + best + " was not made");
            // Keep the fixture's cyan-cloth pixel detector deterministic; rain tint can hide its exact palette.
            journeyMap.weatherManager.TransitionTo(WeatherDefOf.Clear);
            journeyMap.weatherManager.lastWeather = WeatherDefOf.Clear;
            journeyMap.weatherManager.ResetSkyTargetLerpCache();
            journeyMap.weatherDecider.DisableRainFor(GenDate.TicksPerDay);
            journeyPawns.Clear();
            foreach (var p in map.mapPawns.FreeColonistsSpawned.Where(p => p.GetLord() == null && !p.Downed).Take(2).ToList())
            {
                p.jobs?.StopAll();
                p.DeSpawn();
                // Outdoors, as a caravan or a shuttle arrives (r96: the map's centre was inside a dark roofed ruin and the pair was too dim to see).
                // On dry land (r100: an archipelago put them in the sea, swimming and too hidden to count as people in the picture).
                // Together, as a caravan arrives (r104: two independent random cells far apart; the photo frames the group, not strays).
                IntVec3 near = journeyPawns.Count > 0 ? journeyPawns[0].Position : journeyMap.Center;
                int radius = journeyPawns.Count > 0 ? 3 : 60;
                if (!CellFinder.TryFindRandomCellNear(near, journeyMap, radius, c => c.Standable(journeyMap) && !c.Roofed(journeyMap) && !c.Fogged(journeyMap) && !c.GetTerrain(journeyMap).IsWater, out IntVec3 at))
                    at = CellFinder.RandomClosewalkCellNear(near, journeyMap, 4);
                GenSpawn.Spawn(p, at, journeyMap);
                journeyPawns.Add(p);
            }
            if (journeyPawns.Count == 0) throw new InvalidOperationException("nobody free to travel");
            Current.Game.CurrentMap = journeyMap;
            journeyBefore = store.Records.Count;
            journeyStartTick = Find.TickManager.TicksGame;
            var lm = Journey.LandmarkAt(best);
            checks.Add("INFO: journey — travelling to " + best + " (" + grid[best].PrimaryBiome.defName + (lm != null ? ", landmark " + lm.def.defName + " '" + Journey.LandmarkLabel(lm) + "'" : ", no landmark") + ", "
                + grid.ApproxDistanceInTiles(map.Tile, best).ToString("0") + " tiles away) with " + string.Join(", ", journeyPawns.Select(p => p.LabelShort)));
        }

        /// <summary>Ticks until the place is photographed: a record of kind journey whose title names the biome (and the landmark), people in the
        /// picture, and the place remembered with that photo.</summary>
        private static bool JourneyWait()
        {
            var store = PhotoStore.Instance;
            var rec = store.Records.Skip(journeyBefore).FirstOrDefault(r => r.kind == Journey.Kind);
            if (rec != null && rec.HasFile)
            {
                var visit = store.Visits.FirstOrDefault(v => v.tile == journeyTile);
                if (visit == null || visit.photoId != rec.id) throw new InvalidOperationException("the place is not remembered with its photo: visit " + (visit != null) + ", photo " + visit?.photoId + " vs " + rec.id);
                string biome = journeyMap.Biome.LabelCap.ToString();
                var lm = Journey.LandmarkAt(journeyTile);
                if (!rec.title.Contains(biome) && (lm == null || !rec.title.Contains(Journey.LandmarkLabel(lm)))) throw new InvalidOperationException("title '" + rec.title + "' names neither " + biome + " nor the landmark");
                if (lm != null && (visit.landmark != lm.def.defName || !rec.title.Contains(Journey.LandmarkLabel(lm)))) throw new InvalidOperationException("landmark not in the visit/title: " + visit.landmark + " / " + rec.title);
                if (journeyPawns.Any(p => !rec.Has(p))) throw new InvalidOperationException("arrival photo people: " + string.Join(",", rec.people));
                int waited = Find.TickManager.TicksGame - journeyStartTick;
                if (waited < Journey.SettleTicks) throw new InvalidOperationException("photographed before the colonists settled: " + waited + " ticks");
                File.Copy(store.FilePath(rec), Path.Combine(dir, "journey.png"), true);
                RequirePeople("journey.png");
                checks.Add("PASS: journey — arrival at a new place photographed after " + waited + " ticks: '" + rec.title + "' (" + rec.desc + ")");
                // Nothing more for the same place: a second check finds it visited.
                int count = store.Records.Count;
                for (int i = 0; i < Journey.CheckEvery * 2; i++) Find.TickManager.DoSingleTick();
                if (store.Records.Count != count) throw new InvalidOperationException("the same place was photographed again");
                checks.Add("PASS: journey — the place is photographed once; " + store.Visits.Count + " places remembered");
                return true;
            }
            if (Find.TickManager.TicksGame - journeyStartTick > 30000) throw new TimeoutException("no arrival photo after 30000 ticks (visited " + store.Visited(journeyTile) + ", pending " + store.HasPending + ")");
            for (int i = 0; i < 100; i++) Find.TickManager.DoSingleTick();
            SettleDraw();
            return false;
        }

        /// <summary>The route was drawn (arcs between the places) and the pin clicked into a screenshot; then everyone goes home and the map is removed.</summary>
        private static void JourneyEnd()
        {
            if (JourneyMap.Segments == 0) throw new InvalidOperationException("no route drawn on the world map");
            checks.Add("PASS: journey — the route is drawn on the world map (" + JourneyMap.Segments + " arc segments; journey-world.png)");
            CameraJumper.TryHideWorld();
            foreach (var p in journeyPawns)
            {
                if (!p.Spawned) continue;
                p.jobs?.StopAll();
                p.DeSpawn();
                GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(journeyHome.Center, journeyHome, 6), journeyHome);
            }
            Current.Game.CurrentMap = journeyHome;
            var parent = journeyMap.Parent;
            Current.Game.DeinitAndRemoveMap(journeyMap, false);
            if (parent != null && !parent.Destroyed) parent.Destroy();
            journeyMap = null;
            map = journeyHome;
        }

        // ---------- birthdays: the letter, "where they are", the album keeping them out, a place picked on the map ----------

        private static Pawn birthdayPawn;
        private static int birthdayAge, birthdayStart;
        private static ChoiceLetter_MFBirthday birthdayLetter;

        private static Pawn FreeColonist(Pawn not = null) => map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p != not && p.GetLord() == null && !p.Downed && GroupPhoto.CanJoin(p));

        /// <summary>The interval setting, then the game's own birthday method (the patch point) for a free colonist: a letter with four choices
        /// arrives and is opened for the screenshot.</summary>
        private static void BirthdayStart()
        {
            var s = MomentFramesMod.Settings;
            birthdayPawn = FreeColonist();
            if (birthdayPawn == null) throw new InvalidOperationException("no free colonist for the birthday");
            int adult = Mathf.RoundToInt(birthdayPawn.ageTracker.AdultMinAge);
            s.birthdayEvery = 5;
            bool c7 = Birthday.Counts(birthdayPawn, 7), c10 = Birthday.Counts(birthdayPawn, 10), cAdult = Birthday.Counts(birthdayPawn, adult);
            s.birthdayEvery = 1;
            if (c7 || !c10 || !cAdult) throw new InvalidOperationException("every 5 years: 7 " + c7 + ", 10 " + c10 + ", adult " + adult + " " + cAdult);
            checks.Add("PASS: birthday interval — every 5 years takes 10 and the adult birthday (" + adult + "), not 7");
            s.birthdayMode = Birthday.ModeAsk;
            birthdayAge = birthdayPawn.ageTracker.AgeBiologicalYears;
            HarmonyLib.Traverse.Create(birthdayPawn.ageTracker).Method("BirthdayBiological", birthdayAge).GetValue();
            birthdayLetter = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_MFBirthday>().FirstOrDefault(l => l.pawn == birthdayPawn && l.age == birthdayAge);
            if (birthdayLetter == null) throw new InvalidOperationException("no birthday letter for " + birthdayPawn.LabelShort + " turning " + birthdayAge);
            var opts = birthdayLetter.Choices.ToList();
            string[] want = { "MF_BirthdayHere".Translate(), "MF_BirthdaySpot".Translate(), "MF_BirthdayPick".Translate(), "MF_BirthdaySkip".Translate() };
            var texts = opts.Select(o => HarmonyLib.Traverse.Create(o).Field("text").GetValue<string>()).ToList();
            if (!want.All(texts.Contains)) throw new InvalidOperationException("letter choices: " + string.Join(" / ", texts));
            // "The photo spot" is open only when the map has one (this phase runs before the group-photo phases build it); the rest always.
            bool hasSpot = Birthday.SpotFor(birthdayPawn) != null;
            var spotOpt = opts[texts.IndexOf("MF_BirthdaySpot".Translate())];
            if (spotOpt.disabled == hasSpot || opts.Where(o => o != spotOpt).Any(o => o.disabled))
                throw new InvalidOperationException("letter choices enabled wrongly (spot on the map " + hasSpot + "): " + string.Join(" / ", opts.Select(o => o.disabled ? "off: " + o.disabledReason : "on")));
            checks.Add("PASS: birthday — the game's birthday for " + birthdayPawn.LabelShort + " (" + birthdayAge + ") sent the letter with " + opts.Count + " choices: " + string.Join(" / ", texts)
                + (hasSpot ? "" : " ('the photo spot' greyed out: " + spotOpt.disabledReason + ")"));
            foreach (var w in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) w.Close(false);
            birthdayLetter.OpenLetter();
        }

        /// <summary>"Take it where they are".</summary>
        private static void BirthdayChooseHere()
        {
            foreach (var w in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) w.Close(false);
            var here = birthdayLetter.Choices.First(o => HarmonyLib.Traverse.Create(o).Field("text").GetValue<string>() == "MF_BirthdayHere".Translate().ToString());
            Current.Game.CurrentMap = birthdayPawn.Map;
            here.action();
            birthdayStart = Find.TickManager.TicksGame;
            if (!Find.LetterStack.LettersListForReading.Contains(birthdayLetter)) throw new InvalidOperationException("the birthday letter was consumed before a successful photograph");
            var setup = Find.WindowStack.Windows.OfType<Dialog_BirthdaySetup>().LastOrDefault();
            if (setup == null) throw new InvalidOperationException("the birthday letter did not open the preparation window");
            var selectedPlace = HarmonyLib.Traverse.Create(setup).Field("place").GetValue<IntVec3>();
            var selectedDirection = HarmonyLib.Traverse.Create(setup).Field("direction").GetValue<Rot4>();
            if (!Birthday.StartPrepared(birthdayPawn, birthdayAge, selectedPlace, setup.Chosen, selectedDirection, null, setup.Guardian))
                throw new InvalidOperationException("the birthday preparation could not gather its selected roster");
            setup.Close(false);
            checks.Add("PASS: birthday letter opens preparation and remains available until a photo is saved");
        }

        /// <summary>The picture is theirs: kind birthday, their name, in their chronicle, out of the album unless filtered to them or to birthdays.</summary>
        private static bool BirthdayPhotoReady()
        {
            var store = PhotoStore.Instance;
            var rec = store.Records.FirstOrDefault(r => r.eventKey == Birthday.Key(birthdayPawn, birthdayAge));
            if (rec == null || !rec.HasFile)
            {
                if (Find.TickManager.TicksGame - birthdayStart > 3000) throw new TimeoutException("birthday photo not taken");
                for (int i = 0; i < 30; i++) Find.TickManager.DoSingleTick();
                var gathering = Birthday.Active(birthdayPawn);
                if (gathering != null && gathering.Complete && gathering.InPlace == gathering.lord.ownedPawns.Count)
                {
                    SettleDraw();
                    gathering.ShootNow();
                }
                return false;
            }
            if (Find.LetterStack.LettersListForReading.Contains(birthdayLetter)) throw new InvalidOperationException("a successfully photographed birthday did not complete its letter");
            if (rec.kind != Kinds.Birthday || !rec.Has(birthdayPawn) || rec.title != Birthday.Title(birthdayPawn, birthdayAge) || Kinds.IsMilestone(rec.kind))
                throw new InvalidOperationException("birthday record: kind " + rec.kind + ", has " + rec.Has(birthdayPawn) + ", title " + rec.title);
            if (!store.For(birthdayPawn).Contains(rec)) throw new InvalidOperationException("not in the colonist's own chronicle");
            if (Birthday.Possible(birthdayPawn, birthdayAge, out _)) throw new InvalidOperationException("the birthday can be photographed twice");
            var album = (MainTabWindow_Album)AlbumDef.TabWindow;
            var t = HarmonyLib.Traverse.Create(album);
            List<PhotoRecord> Shown(string person, string kind)
            {
                t.Field("filterPerson").SetValue(person);
                t.Field("filterKind").SetValue(kind);
                t.Field("filterYear").SetValue(0);
                t.Field("cacheValid").SetValue(false);
                return t.Method("Filtered").GetValue<List<PhotoRecord>>();
            }
            bool plain = Shown(null, null).Contains(rec), byPerson = Shown(birthdayPawn.GetUniqueLoadID(), null).Contains(rec), byKind = Shown(null, Kinds.Birthday).Contains(rec);
            Shown(null, null);
            t.Field("filterPersonName").SetValue(null);
            if (plain || !byPerson || !byKind) throw new InvalidOperationException("album shows the birthday: plain " + plain + ", by person " + byPerson + ", by kind " + byKind);
            File.Copy(store.FilePath(rec), Path.Combine(dir, "birthday.png"), true);
            RequirePeople("birthday.png");
            checks.Add("PASS: birthday — '" + rec.title + "' taken where they were (" + rec.people.Count + " people): in their own chronicle, out of the album unless filtered to them or to birthdays; not a colony milestone");
            return true;
        }

        /// <summary>"Pick a place": a line-up behind a cell with no photo spot holds (it used to need a spot) and its members carry the buttons.</summary>
        private static void BirthdayPickPlace()
        {
            var p = FreeColonist(birthdayPawn);
            if (p == null) { checks.Add("INFO: no second free colonist for the picked-place birthday"); return; }
            int age = p.ageTracker.AgeBiologicalYears;
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(p.Position, map, 6, c => c.Standable(map) && c.GetFirstThing<Building_PhotoSpot>(map) == null);
            var lord = Birthday.StartLineUp(p, age, cell);
            if (lord == null) throw new InvalidOperationException("the picked-place birthday line-up did not start for " + p.LabelShort);
            var job = (LordJob_MFGroupPhoto)lord.LordJob;
            if (job.BirthdayOf != p.GetUniqueLoadID() || !job.Spotless) throw new InvalidOperationException("line-up: birthday of " + job.BirthdayOf + ", spotless " + job.Spotless);
            for (int i = 0; i < 120 && map.lordManager.lords.Contains(lord); i++) Find.TickManager.DoSingleTick();
            if (!map.lordManager.lords.Contains(lord) || job.Finished) throw new InvalidOperationException("the picked-place line-up ended by itself: " + job.Result);
            var descs = p.GetGizmos().OfType<Command_Action>().Select(g => g.defaultDesc).ToList();
            if (!descs.Contains("MF_BirthdayShootDesc".Translate().ToString()) || !descs.Contains("MF_GroupCancelDesc".Translate().ToString()))
                throw new InvalidOperationException("the members of a line-up without a spot have no take-it-now / cancel buttons");
            checks.Add("PASS: birthday — a place picked on the map: " + lord.ownedPawns.Count + " line up (with the people close to " + p.LabelShort + "), it holds without a photo spot, and the members carry take-it-now / cancel");
            job.Cancel();
            for (int i = 0; i < 20 && map.lordManager.lords.Contains(lord); i++) Find.TickManager.DoSingleTick();
        }

        private static Pawn probeRare;
        private static int rareRecordsBefore;

        /// <summary>A rare beast walks in: the poll should notice it within a second and put one photo in the album (and only one).</summary>
        private static void RareAnimalSpawn()
        {
            var store = PhotoStore.Instance;
            rareRecordsBefore = store.Records.Count(r => r.kind == Historic.KindRareSeen);
            PawnKindDef kind = null;
            foreach (var name in Historic.RareAnimals)
            {
                kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(name);
                if (kind != null) break;
            }
            if (kind == null) { checks.Add("INFO: no rare-animal kind in this run's DLCs — the rare beast check is skipped"); return; }
            probeRare = PawnGenerator.GeneratePawn(kind, null);
            GenSpawn.Spawn(probeRare, CellFinder.StandableCellNear(center + new IntVec3(6, 0, 6), map, 6f), map);
            // The poll runs on game ticks, and the probe keeps the game paused, so wind the clock on: past the colony's first seconds
            // (drop pods) and far enough for one more poll to come due.
            int until = Find.TickManager.TicksGame + Historic.PollTicks * 2;
            while (Find.TickManager.TicksGame < until || Find.TickManager.TicksGame <= Historic.StartEarliestTick) Find.TickManager.DoSingleTick();
        }

        private static void RareAnimalPhotographed()
        {
            if (probeRare == null) return;
            var store = PhotoStore.Instance;
            var recs = store.Records.Where(r => r.kind == Historic.KindRareSeen).ToList();
            if (recs.Count != rareRecordsBefore + 1)
                throw new InvalidOperationException("rare beast " + probeRare.kindDef.defName + ": " + (recs.Count - rareRecordsBefore) + " photos (expected 1)");
            checks.Add("PASS: a rare beast (" + probeRare.kindDef.defName + ") was photographed once on sight: '" + recs[recs.Count - 1].title + "'");
            if (probeRare.Spawned) probeRare.Destroy();
            probeRare = null;
        }

        private static readonly IntVec3 cameraCorner = new IntVec3(8, 0, 8);
        private static float cameraSpan;

        /// <summary>Camera check, step 1: park the view in a far corner with the jump switched on.</summary>
        private static void CameraPark()
        {
            MomentFramesMod.Settings.fxCamera = true;
            Find.CameraDriver.SetRootPosAndSize(cameraCorner.ToVector3Shifted(), 11f);
        }

        /// <summary>Step 2: the corner must really be away from the photographed spot, then an event photo is played through the effect.</summary>
        private static void CameraShotPlayed()
        {
            var driver = Find.CameraDriver;
            if (driver.CurrentViewRect.Contains(center))
                throw new InvalidOperationException("camera test invalid: the corner " + driver.CurrentViewRect + " already shows the shot at " + center);
            cameraSpan = 21f;
            PhotoFx.Play(new PhotoRecord { id = "probe-cam", kind = "recruit", center = center.ToVector3Shifted(), spanCells = cameraSpan, mapId = map.uniqueID, aspect = PhotoCapture.DefaultAspect });
        }

        /// <summary>Step 3: the view has followed the photo and zoomed to its span; then park again and play a hand-taken one.</summary>
        private static void CameraFollowed()
        {
            var driver = Find.CameraDriver;
            float want = Mathf.Clamp(cameraSpan * 0.6f, PhotoFx.MinCameraSize, PhotoFx.MaxCameraSize);
            if (!driver.CurrentViewRect.Contains(center)) throw new InvalidOperationException("the view did not move to the event photo: " + driver.CurrentViewRect);
            if (Mathf.Abs(driver.RootSize - want) > 0.01f) throw new InvalidOperationException("zoom " + driver.RootSize + ", expected " + want + " for span " + cameraSpan);
            checks.Add("PASS: an event photo pulls the view to it (" + driver.CurrentViewRect + ") and zooms to the framed span (RootSize " + driver.RootSize.ToString("0.#") + " for " + cameraSpan + " cells)");
            driver.SetRootPosAndSize(cameraCorner.ToVector3Shifted(), 11f);
            PhotoFx.Play(new PhotoRecord { id = "probe-cam2", kind = Kinds.Manual, center = center.ToVector3Shifted(), spanCells = cameraSpan, mapId = map.uniqueID, aspect = PhotoCapture.DefaultAspect });
        }

        /// <summary>Step 4: a photo the player set up by hand must not move anything. The jump goes back off for the later screenshots.</summary>
        private static void CameraLeftAlone()
        {
            var driver = Find.CameraDriver;
            if (driver.CurrentViewRect.Contains(center) || Mathf.Abs(driver.RootSize - 11f) > 0.01f)
                throw new InvalidOperationException("a hand-taken photo moved the view: " + driver.CurrentViewRect + " size " + driver.RootSize);
            checks.Add("PASS: a photo taken by hand leaves the view where it was");
            MomentFramesMod.Settings.fxCamera = false;
            Find.WindowStack.TryRemove(typeof(Window_PhotoPop), false);
        }

        /// <summary>All four effects on, game running, one immediate shot; fxAt = moment the file was saved (= effect start).</summary>
        private static void FxStart()
        {
            FxSet(true);
            fxAt = 0f;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            var rec = PhotoStore.Instance.RequestNow(map, center.ToVector3Shifted(), "test", "probe:fx", "Probe effect shot", "", map.mapPawns.FreeColonistsSpawned, 9f, PhotoCapture.DefaultAspect,
                () => fxAt = Time.realtimeSinceStartup);
            if (rec == null) throw new InvalidOperationException("effect shot could not start");
        }

        /// <summary>The freeze frame is skipped while a raid or a fight is on (2026-11-17, PhotoFx.Busy). A calm control first (it must
        /// freeze, or the two busy cases below prove nothing), then the game's combat slowdown, then an enemy standing on the map, then calm
        /// again once the enemy is gone. The card opens every time; only its freeze changes. The danger rating is cached for 101 ticks, so
        /// its stamp is cleared before each read.</summary>
        private static void FxBusyChecks()
        {
            var rec = PhotoStore.Instance.Records.LastOrDefault();
            if (rec == null) throw new InvalidOperationException("no photo for the busy-freeze checks");
            var slower = Find.TickManager.slower;
            var until = HarmonyLib.AccessTools.Field(typeof(TimeSlower), "forceNormalSpeedUntil");
            var stamp = HarmonyLib.AccessTools.Field(typeof(DangerWatcher), "lastUpdateTick");
            void Fresh() => stamp.SetValue(map.dangerWatcher, -10000);
            string Pop()   // "freeze" / "no freeze" for the card this shot opened, "no card" if none
            {
                Find.WindowStack.TryRemove(typeof(Window_PhotoPop), false);
                PhotoFx.Play(rec);
                var w = Find.WindowStack.WindowOfType<Window_PhotoPop>();
                Find.WindowStack.TryRemove(typeof(Window_PhotoPop), false);
                return w == null ? "no card" : w.forcePause ? "freeze" : "no freeze";
            }
            int savedUntil = (int)until.GetValue(slower);
            Pawn raider = null;
            try
            {
                until.SetValue(slower, 0);
                Fresh();
                string calm = Pop();
                if (calm != "freeze")
                    throw new InvalidOperationException("calm control: " + calm + " (combat slowdown " + slower.ForcedNormalSpeed + ", danger " + map.dangerWatcher.DangerRating + ")");
                slower.SignalForceNormalSpeed();
                string fight = Pop();
                if (!slower.ForcedNormalSpeed || fight != "no freeze")
                    throw new InvalidOperationException("combat slowdown " + slower.ForcedNormalSpeed + ": " + fight);
                until.SetValue(slower, 0);
                var enemy = Find.FactionManager.RandomEnemyFaction(false, false, true);
                if (enemy == null) throw new InvalidOperationException("no enemy faction for the danger case");
                raider = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, enemy);
                GenSpawn.Spawn(raider, CellFinder.RandomClosewalkCellNear(center + new IntVec3(12, 0, 0), map, 3), map);
                Fresh();
                var danger = map.dangerWatcher.DangerRating;
                string raid = Pop();
                if (danger == StoryDanger.None || raid != "no freeze")
                    throw new InvalidOperationException("enemy " + enemy.Name + " at " + raider.Position + ": danger " + danger + ", " + raid);
                raider.Destroy(DestroyMode.Vanish);
                raider = null;
                Fresh();
                string after = Pop();
                if (after != "freeze") throw new InvalidOperationException("enemy gone, danger " + map.dangerWatcher.DangerRating + ": " + after);
                checks.Add("PASS: freeze frame — calm: freeze; combat slowdown: card, no freeze; enemy on the map (danger " + danger
                    + "): card, no freeze; enemy gone: freeze again");
            }
            finally
            {
                if (raider != null && !raider.Destroyed) raider.Destroy(DestroyMode.Vanish);
                until.SetValue(slower, savedUntil);
                Fresh();
                Find.WindowStack.TryRemove(typeof(Window_PhotoPop), false);
            }
        }

        /// <summary>Our letters and the game's pause (2026-11-17). (1) The three LetterDefs load as AnyLetter. (2) Through the game's own
        /// ReceiveLetter: with "Pause on letter" off a birthday letter leaves the game running, with "Any letter" it pauses (2.5.2 used
        /// Never, which paused for everyone). (3) The birthday letter's end-of-day dialog: opens when calm (control), during the combat
        /// slowdown moves its deadline on at each last tick and keeps the letter, also waits with an enemy on the map, and opens on its
        /// last tick once the danger is over. No game ticks are run, so nothing else comes due for the later phases.</summary>
        private static void LetterPauseChecks()
        {
            foreach (var name in new[] { "MF_PhotoLetter", "MF_BirthdayLetter", "MF_YearLetter" })
            {
                var d = DefDatabase<LetterDef>.GetNamed(name);
                if (d.pauseMode != AutomaticPauseMode.AnyLetter) throw new InvalidOperationException(name + " loaded with pauseMode " + d.pauseMode);
            }
            var p = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(c => !c.Dead);
            if (p == null) throw new InvalidOperationException("no colonist for the letter checks");
            int age = p.ageTracker.AgeBiologicalYears;
            var stack = Find.LetterStack;
            var tm = Find.TickManager;
            var slower = tm.slower;
            var until = HarmonyLib.AccessTools.Field(typeof(TimeSlower), "forceNormalSpeedUntil");
            var stamp = HarmonyLib.AccessTools.Field(typeof(DangerWatcher), "lastUpdateTick");
            void Fresh() => stamp.SetValue(map.dangerWatcher, -10000);
            int Dialogs() => Find.WindowStack.Windows.Count(w => w is Dialog_NodeTree);
            void CloseDialogs() { foreach (var w in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) Find.WindowStack.TryRemove(w, false); }
            var made = new List<Letter>();
            ChoiceLetter_MFBirthday Send()
            {
                int n = stack.LettersListForReading.Count;
                Birthday.SendLetter(p, age);
                var l = stack.LettersListForReading.Skip(n).OfType<ChoiceLetter_MFBirthday>().LastOrDefault();
                if (l == null) throw new InvalidOperationException("the birthday letter for " + p.LabelShort + " did not arrive");
                made.Add(l);
                return l;
            }
            void Drop(Letter l) { if (stack.LettersListForReading.Contains(l)) stack.RemoveLetter(l); }
            var savedMode = Prefs.AutomaticPauseMode;
            var savedSpeed = tm.CurTimeSpeed;
            int savedUntil = (int)until.GetValue(slower);
            Pawn raider = null;
            try
            {
                CloseDialogs();
                if (Find.WindowStack.WindowsForcePause)
                    throw new InvalidOperationException("a window holds the game before the letter checks: "
                        + string.Join(", ", Find.WindowStack.Windows.Where(w => w.forcePause).Select(w => w.GetType().Name)));

                // (2) the player's option, through the real receive path
                Prefs.AutomaticPauseMode = AutomaticPauseMode.Never;
                tm.CurTimeSpeed = TimeSpeed.Normal;
                Drop(Send());
                bool pausedOff = tm.Paused;
                Prefs.AutomaticPauseMode = AutomaticPauseMode.AnyLetter;
                tm.CurTimeSpeed = TimeSpeed.Normal;
                Drop(Send());
                bool pausedAny = tm.Paused;
                Prefs.AutomaticPauseMode = savedMode;
                tm.CurTimeSpeed = TimeSpeed.Normal;
                if (pausedOff || !pausedAny)
                    throw new InvalidOperationException("birthday letter pause: option off paused " + pausedOff + ", option 'any letter' paused " + pausedAny);
                checks.Add("PASS: letters follow 'Pause on letter' — 3 LetterDefs load as AnyLetter; a birthday letter with the option off: game runs; with 'any letter': paused");

                // (3a) calm control: on its last tick the letter opens by itself
                until.SetValue(slower, 0);
                Fresh();
                if (PhotoFx.Busy()) throw new InvalidOperationException("calm control is busy (danger " + map.dangerWatcher.DangerRating + ")");
                var calm = Send();
                calm.disappearAtTick = tm.TicksGame + 1;
                int d0 = Dialogs();
                stack.OpenAutomaticLetters();
                bool calmOpened = Dialogs() > d0;
                CloseDialogs();
                Drop(calm);
                if (!calmOpened) throw new InvalidOperationException("calm: the birthday letter did not open on its last tick");

                // (3b) combat slowdown: on each last tick it moves its deadline on instead of opening, then opens once the slowdown ends.
                // No game ticks here: a first version ran 500 real ticks, which let other event photos come due and moved the camera and
                // the album for the later phases. LetterStackTick calls OpenAutomaticLetters first thing (1.6, 1.7), so this is its path;
                // "the last tick again" is the deadline put back to the next tick.
                var wait = Send();
                slower.SignalForceNormalSpeed();
                var steps = new List<int>();
                for (int i = 0; i < 3; i++)
                {
                    wait.disappearAtTick = tm.TicksGame + 1;
                    stack.OpenAutomaticLetters();
                    if (Dialogs() > 0) throw new InvalidOperationException("the birthday dialog opened during the combat slowdown (round " + (i + 1) + ")");
                    if (!wait.CanShowInLetterStack) throw new InvalidOperationException("the birthday letter timed out during the combat slowdown (round " + (i + 1) + ")");
                    steps.Add(wait.disappearAtTick - tm.TicksGame);
                }
                if (steps.Any(s => s != ChoiceLetter_MFBirthday.WaitStepTicks)) throw new InvalidOperationException("deadline steps " + string.Join(",", steps) + ", expected " + ChoiceLetter_MFBirthday.WaitStepTicks);
                until.SetValue(slower, 0);
                wait.disappearAtTick = tm.TicksGame + 1;
                stack.OpenAutomaticLetters();
                bool released = Dialogs() > 0;
                CloseDialogs();
                Drop(wait);
                if (!released) throw new InvalidOperationException("the slowdown ended but the birthday dialog did not open on its last tick");

                // (3c) an enemy on the map: no dialog, the deadline moved on
                var enemy = Find.FactionManager.RandomEnemyFaction(false, false, true);
                if (enemy == null) throw new InvalidOperationException("no enemy faction for the danger case");
                var raid = Send();
                raid.disappearAtTick = tm.TicksGame + 1;
                raider = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, enemy);
                GenSpawn.Spawn(raider, CellFinder.RandomClosewalkCellNear(center + new IntVec3(12, 0, 0), map, 3), map);
                Fresh();
                var danger = map.dangerWatcher.DangerRating;
                d0 = Dialogs();
                stack.OpenAutomaticLetters();
                bool raidOpened = Dialogs() > d0;
                int moved = raid.disappearAtTick - tm.TicksGame;
                CloseDialogs();
                raider.Destroy(DestroyMode.Vanish);
                raider = null;
                Fresh();
                raid.disappearAtTick = tm.TicksGame + 1;
                d0 = Dialogs();
                stack.OpenAutomaticLetters();
                bool afterRaid = Dialogs() > d0;
                CloseDialogs();
                Drop(raid);
                if (danger == StoryDanger.None || raidOpened || moved <= 1 || !afterRaid)
                    throw new InvalidOperationException("enemy on the map: danger " + danger + ", opened " + raidOpened + ", deadline +" + moved + " ticks, opened once gone " + afterRaid);
                checks.Add("PASS: birthday dialog — calm: opens on the last tick; combat slowdown: 3 last ticks, each moved the deadline +"
                    + ChoiceLetter_MFBirthday.WaitStepTicks + " with the letter kept, opens once it ended; enemy on the map (danger " + danger + "): not opened, deadline +" + moved + " ticks; enemy gone: opens");
            }
            finally
            {
                if (raider != null && !raider.Destroyed) raider.Destroy(DestroyMode.Vanish);
                foreach (var l in made) Drop(l);
                CloseDialogs();
                Prefs.AutomaticPauseMode = savedMode;
                until.SetValue(slower, savedUntil);
                Fresh();
                tm.CurTimeSpeed = savedSpeed;
            }
        }

        /// <summary>Ideology-only: the ritual classifier on the quick-test ideo's own rituals, and a leader appointment producing a role photo.</summary>
        private static void IdeoChecks()
        {
            roleRec = null;
            var ideo = Faction.OfPlayer.ideos?.PrimaryIdeo;
            if (ideo == null)
            {
                checks.Add("INFO: quick-test game has no primary ideo — ritual/role checks skipped");
                return;
            }
            var groups = new Dictionary<string, string>();
            foreach (var r in ideo.PreceptsListForReading.OfType<Precept_Ritual>())
                groups[r.sourcePattern?.defName ?? r.def.defName] = RitualGroups.Classify(r).ToString();
            checks.Add("INFO: ritual groups of ideo '" + ideo.name + "': " + string.Join(", ", groups.Select(kv => kv.Key + "=" + kv.Value)));
            void Expect(string pattern, string group)
            {
                if (!groups.TryGetValue(pattern, out var g)) { checks.Add("INFO: ritual " + pattern + " not in this ideo"); return; }
                if (g != group) throw new InvalidOperationException("ritual " + pattern + " classified " + g + ", expected " + group);
                checks.Add("PASS: ritual " + pattern + " -> " + group);
            }
            Expect("FuneralStandard", "Funeral");
            Expect("LeaderSpeech", "Skip");
            Expect("Trial", "Ritual");
            foreach (var kv in groups.Where(kv => kv.Key.StartsWith("Celebration")).ToList()) Expect(kv.Key, "Party");
            var role = ideo.RolesListForReading.FirstOrDefault(x => x.def.leaderRole);
            if (role == null)
            {
                checks.Add("INFO: ideo has no leader role — role-photo check skipped");
                return;
            }
            var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => role.GetFirstUnmetRequirement(p) == null);
            if (pawn == null)
            {
                checks.Add("INFO: no colonist meets the leader role requirements — role-photo check skipped");
                return;
            }
            int before = PhotoStore.Instance.Records.Count;
            role.Assign(pawn, false);
            roleRec = PhotoStore.Instance.Records.Skip(before).FirstOrDefault(r => r.kind == "role");
            if (roleRec == null) throw new InvalidOperationException("leader appointment created no role photo");
            // Assigned straight from the tab (no ceremony): a close portrait of the person, with a sentence naming them.
            Near(roleRec.spanCells, Patch_RoleAssigned.PortraitSpan, "role portrait span");
            if (roleRec.desc.NullOrEmpty() || !roleRec.desc.Contains(pawn.LabelShort)) throw new InvalidOperationException("role sentence: '" + roleRec.desc + "'");
            checks.Add("PASS: leader role assigned to " + pawn.LabelShort + " -> role photo '" + roleRec.title + "' (portrait, span " + roleRec.spanCells + "; sentence: " + roleRec.desc + ")");
        }

        private static readonly List<Thing> probeLanterns = new List<Thing>();
        private static readonly List<Pawn> probeTemp = new List<Pawn>();
        private static Pawn probeVictim;

        /// <summary>Ideology rituals photographed at their climax. A whole execution ritual cannot be staged here, so the three pieces the hook
        /// relies on are exercised directly: which vanilla behaviours get which moment (read from their stages), the climax window of a real
        /// sacrifice / scarification job (the doer in place and the victim still unharmed), and sky lanterns counted as aloft — photographed.</summary>
        private static void MomentChecks()
        {
            var expect = new Dictionary<string, RitualMoment>
            {
                { "Execution", RitualMoment.Kill }, { "SacrificePrisoner", RitualMoment.Kill }, { "SacrificeAnimal", RitualMoment.Kill },
                { "ScarificationCeremony", RitualMoment.Mutilation }, { "BlindingCeremony", RitualMoment.Mutilation },
                { "GladiatorDuel", RitualMoment.Duel }, { "CelebrationSkyLanterns", RitualMoment.SkyLanterns }, { "CelebrationTree", RitualMoment.TreeCelebration },
                { "Funeral", RitualMoment.Speech }, { "LeaderSpeech", RitualMoment.Speech }, { "Trial", RitualMoment.Speech },
                { "Conversion", RitualMoment.Speech }, { "RoleChange", RitualMoment.Progress }, { "DancePartyTech", RitualMoment.Progress }
            };
            int matched = 0;
            var others = new List<string>();
            foreach (var def in DefDatabase<RitualBehaviorDef>.AllDefsListForReading)
            {
                var m = RitualGroups.MomentOf(def);
                if (expect.TryGetValue(def.defName, out var want))
                {
                    if (m != want) throw new InvalidOperationException("ritual behaviour " + def.defName + " -> " + m + ", expected " + want);
                    matched++;
                }
                else if (m != RitualMoment.Progress) others.Add(def.defName + "=" + m);
            }
            if (matched != expect.Count) throw new InvalidOperationException("only " + matched + " of " + expect.Count + " expected ritual behaviours exist");
            checks.Add("PASS: ritual moments read from behaviour stages: all " + matched + " checked vanilla behaviours as expected" + (others.Count > 0 ? " (also: " + string.Join(", ", others) + ")" : ""));

            // Three temporary colonists do the deeds (the quick-test colony may have only the wedding couple free); removed after the lantern shot.
            probeTemp.Clear();
            for (int i = 0; i < 3; i++)
            {
                var tp = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(tp, CellFinder.StandableCellNear(center + new IntVec3(2 + 2 * i, 0, -3), map, 3f), map);
                probeTemp.Add(tp);
            }
            var free = probeTemp;

            // Sacrifice: walk to the altar cell, wait 35 ticks, cut. The climax is the wait.
            var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Chicken") ?? DefDatabase<PawnKindDef>.AllDefsListForReading.First(k => k.RaceProps != null && k.RaceProps.Animal && k.RaceProps.baseBodySize < 1f);
            var animal = PawnGenerator.GeneratePawn(kind, null);
            IntVec3 altar = CellFinder.StandableCellNear(center + new IntVec3(5, 0, -4), map, 4f);
            GenSpawn.Spawn(animal, CellFinder.StandableCellNear(altar + IntVec3.East, map, 2f), map);
            probeVictim = animal;
            var doer = free[0];
            var sac = JobMaker.MakeJob(JobDefOf.Sacrifice, animal, altar);
            sac.playerForced = true;
            doer.jobs.StartJob(sac, JobCondition.InterruptForced);
            ClimaxRun(doer, sac, RitualMoment.Kill, () => animal.Dead, "sacrifice (" + kind.defName + ")");

            // Scarification: touch the target, wait 35 ticks, cut.
            var target = free[2];
            var hold = JobMaker.MakeJob(JobDefOf.Wait, 4000);
            hold.playerForced = true;
            target.jobs.StartJob(hold, JobCondition.InterruptForced);
            int scarsBefore = target.health.hediffSet.hediffs.Count(h => h.def == HediffDefOf.Scarification);
            var scar = JobMaker.MakeJob(JobDefOf.Scarify, target, target.Position);
            scar.playerForced = true;
            free[1].jobs.StartJob(scar, JobCondition.InterruptForced);
            ClimaxRun(free[1], scar, RitualMoment.Mutilation, () => target.health.hediffSet.hediffs.Count(h => h.def == HediffDefOf.Scarification) > scarsBefore, "scarification");
            target.jobs.EndCurrentJob(JobCondition.InterruptForced);
            SettleDraw();

            // Sky lanterns released beside the colonists near the spot: aloft once they have risen, and the photo frames them.
            probeLanterns.Clear();
            var launchers = map.mapPawns.FreeColonistsSpawned.Where(x => x.Position.InHorDistOf(center, 12f)).Take(5).ToList();
            foreach (var l in launchers)
            {
                IntVec3 cell = l.Position + new IntVec3(-1, 0, 1);   // where ActionOnTick_ReleaseLantern puts it
                if (!cell.InBounds(map)) continue;
                probeLanterns.Add(GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.SkyLantern), cell, map));
            }
            if (probeLanterns.Count == 0) throw new InvalidOperationException("no colonist near the spot to release a lantern");
            int ticks = 0;
            List<Vector3> drawn;
            while (!MidEvent.LanternsAloft(map, center, out drawn))
            {
                if (++ticks > 2000) throw new InvalidOperationException("lanterns never counted as aloft (" + probeLanterns.Count + " spawned)");
                Find.TickManager.DoSingleTick();
            }
            // Not at the moment they appear. "Risen" is the drawn offset from the cell, so wind drifting them sideways counts too (r92: 43 ticks) —
            // the guard is only against a lantern counted as aloft where it stands.
            checks.Add("INFO: lanterns aloft after " + ticks + " ticks, wind " + map.windManager.WindSpeed.ToString("0.00"));
            if (ticks < 20) throw new InvalidOperationException("lanterns counted as aloft after only " + ticks + " ticks (wind " + map.windManager.WindSpeed.ToString("0.00") + ")");
            SettleDraw();
            Hooks.AutoFrame(map, center, launchers, Hooks.GatherMinSpan, Hooks.GatherMaxSpan, out Vector3 lc, out float lspan, out float laspect, drawn);
            var rect = PhotoCapture.CoveredRect(lc, lspan, map, laspect);
            int inFrame = drawn.Count(v => rect.Contains(v.ToIntVec3()));
            if (inFrame < drawn.Count) throw new InvalidOperationException("framing left out " + (drawn.Count - inFrame) + " of " + drawn.Count + " lanterns");
            if (!PhotoCapture.Capture(map, lc, lspan, r => { if (r?.png != null) File.WriteAllBytes(Path.Combine(dir, "lanterns.png"), r.png); }, laspect))
                throw new InvalidOperationException("lantern capture could not start (busy " + PhotoCapture.Busy + ", viewed map " + (Find.CurrentMap == map)
                    + ", last photo asked for: " + PhotoStore.Instance.Records.LastOrDefault()?.kind + " '" + PhotoStore.Instance.Records.LastOrDefault()?.title + "')");
            checks.Add("PASS: " + probeLanterns.Count + " sky lanterns counted as aloft after " + ticks + " ticks (median rise >= " + MidEvent.LanternRise + " cells); framing (span " + lspan.ToString("0.#") + ") holds all " + drawn.Count + " drawn lanterns");
        }

        /// <summary>Ticks one ritual job to its end: the climax must be seen for most of the 35-tick wait while the deed is not yet done, and the
        /// deed must follow.</summary>
        private static void ClimaxRun(Pawn doer, Job job, RitualMoment moment, Func<bool> deedDone, string what)
        {
            int first = -1, count = 0;
            bool doneAtClimax = false;
            for (int t = 0; t < 3000; t++)
            {
                if (deedDone()) break;
                if (doer.CurJob != job) throw new InvalidOperationException(what + ": job ended before the deed (now " + doer.CurJobDef?.defName + ", tick " + t + ", climax ticks " + count + ")");
                if (MidEvent.AtClimax(doer, moment, out Pawn victim))
                {
                    if (first < 0) first = t;
                    count++;
                    if (deedDone()) doneAtClimax = true;
                    if (MidEvent.AtClimax(doer, moment == RitualMoment.Kill ? RitualMoment.Mutilation : RitualMoment.Kill, out _)) throw new InvalidOperationException(what + ": climax also matched the other kind");
                }
                Find.TickManager.DoSingleTick();
            }
            if (!deedDone()) throw new InvalidOperationException(what + ": the deed did not happen in 3000 ticks (climax ticks " + count + ")");
            if (first < 0) throw new InvalidOperationException(what + ": the climax was never detected");
            if (doneAtClimax || count < 20) throw new InvalidOperationException(what + ": climax window " + count + " ticks (need ~35 before the deed)");
            checks.Add("PASS: " + what + " climax detected " + count + " ticks before the deed (from tick " + first + ", the doer in place, the victim untouched)");
            SettleDraw();
        }

        /// <summary>Does the wall frame stop a floor being laid on the cell it stands on? Asked as a comparison, because the ground itself may
        /// refuse a floor (a marsh, a rock face) and that has nothing to do with us: the frame's cell is compared with a free cell of the same
        /// terrain. The wall behind it is asked too, since that is the cell a player aiming at the picture actually clicks.</summary>
        private static void FloorUnderFrameChecks()
        {
            if (frame == null || !frame.Spawned) throw new InvalidOperationException("no frame on the map for the floor check");
            var floor = DefDatabase<TerrainDef>.GetNamedSilentFail("WoodPlankFloor") ?? DefDatabase<TerrainDef>.GetNamedSilentFail("ConcreteFloor");
            if (floor == null) throw new InvalidOperationException("no buildable floor def found");
            IntVec3 frameCell = frame.Position;
            IntVec3 wallCell = frameCell + frame.Rotation.FacingCell;
            TerrainDef ground = map.terrainGrid.TerrainAt(frameCell);

            // a free cell with the same ground, as the control
            IntVec3 control = IntVec3.Invalid;
            foreach (var c in GenRadial.RadialCellsAround(frameCell, 8f, false))
            {
                if (!c.InBounds(map) || c == wallCell || map.terrainGrid.TerrainAt(c) != ground) continue;
                if (c.GetThingList(map).Any(t => t.def.category == ThingCategory.Building)) continue;
                control = c;
                break;
            }
            var onFrame = GenConstruct.CanPlaceBlueprintAt(floor, frameCell, Rot4.North, map);
            var onWall = GenConstruct.CanPlaceBlueprintAt(floor, wallCell, Rot4.North, map);
            string groundLabel = ground != null ? ground.defName : "?";
            if (!control.IsValid)
            {
                checks.Add("INFO: no free cell of the same ground (" + groundLabel + ") near the frame — floor comparison skipped");
            }
            else
            {
                var onControl = GenConstruct.CanPlaceBlueprintAt(floor, control, Rot4.North, map);
                if (onFrame.Accepted != onControl.Accepted)
                    throw new InvalidOperationException("the frame changes the verdict: its cell " + frameCell + " -> " +
                        (onFrame.Accepted ? "allowed" : "refused (" + onFrame.Reason + ")") + ", bare ground " + control + " -> " +
                        (onControl.Accepted ? "allowed" : "refused (" + onControl.Reason + ")"));
                checks.Add("PASS: the wall frame does not stop a floor — its cell " + frameCell + " and bare " + groundLabel + " at " + control +
                           " give the same verdict (" + (onFrame.Accepted ? "allowed" : "refused by the ground: " + onFrame.Reason) + ")");
            }
            checks.Add("INFO: the wall cell the picture is drawn on " + wallCell + " (" +
                       string.Join("/", wallCell.GetThingList(map).Select(t => t.def.defName)) + ") -> " +
                       (onWall.Accepted ? "allowed" : "refused (" + onWall.Reason + ")"));
        }

        private static bool CheckRolePhoto()
        {
            if (roleRec == null) return true;
            if (!roleRec.HasFile)
            {
                if (!PhotoStore.Instance.Records.Contains(roleRec)) throw new InvalidOperationException("role photo capture failed");
                return false;
            }
            File.Copy(PhotoStore.Instance.FilePath(roleRec), Path.Combine(dir, "role.png"), true);
            checks.Add("PASS: role photo file written");
            return true;
        }

        private static PhotoRecord Drag(CellRect rect)
        {
            int before = PhotoStore.Instance.Records.Count;
            new Designator_TakePhoto().DesignateMultiCell(rect.Cells);
            var rec = PhotoStore.Instance.Records.Skip(before).FirstOrDefault(r => r.kind == "manual");
            if (rec == null) throw new InvalidOperationException("drag " + rect + " created no record");
            return rec;
        }

        private static void Near(float actual, float expected, string what)
        {
            if (Math.Abs(actual - expected) > 0.01f) throw new InvalidOperationException(what + " " + actual + " != " + expected);
        }

        private static void DragCapture()
        {
            // 9 x 4 cells: the shot keeps the 2.25 proportion; the 4-cell height is raised to the 5-cell minimum.
            dragRec = Drag(CellRect.FromLimits(center.x - 4, center.z - 1, center.x + 4, center.z + 2));
            Near(dragRec.spanCells, 5f, "drag span");
            Near(dragRec.aspect, 2.25f, "drag aspect");
            if (Math.Abs(dragRec.center.x - (center.x + 0.5f)) > 0.01f || Math.Abs(dragRec.center.z - (center.z + 1f)) > 0.01f)
                throw new InvalidOperationException("drag centre " + dragRec.center + " unexpected");
            checks.Add("PASS: drag 9x4 cells -> aspect 2.25 kept, span raised to the 5-cell minimum, centred " + dragRec.center);
            // 3 x 12 cells: narrower than the 2:3 portrait limit, so the width grows to 8 cells around it.
            tallRec = Drag(CellRect.FromLimits(center.x - 1, center.z - 6, center.x + 1, center.z + 5));
            Near(tallRec.spanCells, 12f, "tall span");
            Near(tallRec.aspect, 2f / 3f, "tall aspect");
            checks.Add("PASS: drag 3x12 cells -> clamped to the 2:3 portrait limit, span 12");
        }

        private static bool CheckDragCapture()
        {
            foreach (var rec in new[] { dragRec, tallRec })
            {
                if (rec.HasFile) continue;
                if (!PhotoStore.Instance.Records.Contains(rec)) throw new InvalidOperationException("drag capture failed: " + rec.title);
                return false;
            }
            File.Copy(PhotoStore.Instance.FilePath(dragRec), Path.Combine(dir, "drag-capture.png"), true);
            File.Copy(PhotoStore.Instance.FilePath(tallRec), Path.Combine(dir, "drag-tall.png"), true);
            // The file itself must have the requested proportions at the set pixels per cell.
            var tex = new Texture2D(2, 2);
            if (!tex.LoadImage(File.ReadAllBytes(PhotoStore.Instance.FilePath(tallRec)))) throw new InvalidOperationException("tall photo png unreadable");
            int w = tex.width, h = tex.height;
            UnityEngine.Object.Destroy(tex);
            PhotoCapture.ImageSize(tallRec.spanCells, tallRec.Aspect, out int ew, out int eh);
            if (h != eh || w != ew) throw new InvalidOperationException("tall photo is " + w + "x" + h + ", expected " + ew + "x" + eh);
            checks.Add("PASS: drag capture files written; portrait file is " + w + "x" + h + " (" + PhotoCapture.PixelsPerCell + " px per cell)");
            // A portrait photo on the 2x2 easel: the drawn frame follows the photo (0.8 x 1.2 instead of 1.8 x 1.2).
            easel.SetPhoto(tallRec.id);
            easel.RefreshBinding();
            if (!easel.HasPhotoTexture) throw new InvalidOperationException("easel did not bind the portrait photo");
            easel.Layout(out float fw, out float fh, out _);
            Near(fh, 1.2f, "easel photo height");
            Near(fw, 0.8f, "easel photo width");
            checks.Add("PASS: portrait photo on the easel -> frame drawn " + fw.ToString("0.00") + " x " + fh.ToString("0.00") + " (border follows the photo)");
            return true;
        }

        private static string WeddingState(Pawn p) => p.LabelShort + ": awake " + p.Awake() + ", downed " + p.Downed + ", drafted " + p.Drafted + ", mental " + p.MentalStateDef?.defName
            + ", rest " + (p.needs?.rest?.CurLevel ?? -1f).ToString("0.00") + ", food " + (p.needs?.food?.CurLevel ?? -1f).ToString("0.00") + ", job " + p.CurJobDef?.defName
            + ", hediffs " + string.Join("/", p.health.hediffSet.hediffs.Select(h => h.def.defName));

        private static void StartWedding()
        {
            // The couple rested, fed and awake, nothing hostile about: the phases before (the journey, birthdays) move the clock on, and a
            // wedding that starts at night or with a threat around is called off (r95).
            foreach (var p in new[] { p1, p2 })
            {
                if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.needs?.food != null) p.needs.food.CurLevel = 1f;
                if (!p.Awake()) RestUtility.WakeUp(p);
                if (p.InMentalState) p.MentalState.RecoverFromState();   // r106: the host was wandering confused and the party was called off at once
                if (p.Drafted) p.drafter.Drafted = false;
            }
            ClearHostiles(map);
            // Same code path as the dev-mode debug action "Start wedding".
            if (MFDebug.StartWedding(p1, p2, center) == null) throw new InvalidOperationException("marriage ceremony lord not created");
            weddingStartTick = Find.TickManager.TicksGame;
            checks.Add("PASS: marriage ceremony lord created (via MFDebug.StartWedding)");
        }

        private static void RunWedding()
        {
            var store = PhotoStore.Instance;
            var wed = store.Records.FirstOrDefault(r => r.kind == "wedding");
            if (wed != null && wed.HasFile)
            {
                File.Copy(store.FilePath(wed), Path.Combine(dir, "wedding.png"), true);
                checks.Add(CaptureDiagLine("wedding", new[] { p1, p2 }));
                RequirePeople("wedding.png");   // r36–r38: shadows but no bodies at span 7 (camera below the pawn altitude)
                bool married = p1.relations.DirectRelationExists(PawnRelationDefOf.Spouse, p2);
                checks.Add((married ? "PASS" : "FAIL") + ": spouse relation after ceremony");
                checks.Add("PASS: wedding hook produced photo '" + wed.title + "' people=" + string.Join(",", wed.people) + " after " + (Find.TickManager.TicksGame - weddingStartTick) + " ticks");
                if (!married) throw new InvalidOperationException("ceremony ended without marriage");
                if (wed.desc.NullOrEmpty() || !(wed.desc.Contains(p1.LabelShort) || wed.desc.Contains(p2.LabelShort))) throw new InvalidOperationException("wedding sentence: '" + wed.desc + "'");
                checks.Add("PASS: wedding sentence picked from the templates: " + wed.desc);
                phase = 23;   // album checks (need the wedding photo for the chronicle); the party runs later, right before the group photo (r46: its 6000 ticks pushed phase 3 into the night)
                return;
            }
            if (wed != null || store.HasPending) return; // capture in flight; let frames render
            if (p1.GetLord() == null && p2.GetLord() == null && !p1.relations.DirectRelationExists(PawnRelationDefOf.Spouse, p2))
                throw new InvalidOperationException("the wedding was called off after " + (Find.TickManager.TicksGame - weddingStartTick) + " ticks (hour " + GenLocalDate.HourOfDay(map) + ", danger "
                    + map.dangerWatcher.DangerRating + "): " + WeddingState(p1) + " | " + WeddingState(p2));
            if (Find.TickManager.TicksGame - weddingStartTick > 60000) throw new TimeoutException("wedding did not complete within 60000 ticks; lord alive=" + (p1.GetLord() != null));
            for (int i = 0; i < 200; i++)
            {
                Find.TickManager.DoSingleTick();
                if (store.HasPending || store.Records.Any(r => r.kind == "wedding")) break;
            }
            SettleDraw();
        }

        // ---------- party: mid-event shot ----------

        private static Lord partyLord;
        private static LordJob_Joinable_Party partyJob;
        private static Pawn partyHost;
        private static string partyKey, partyFile;
        private static int partyStep, partyShotAt;
        private const int PartyTicks = 3000;

        private static void StartProbeParty()
        {
            var store = PhotoStore.Instance;
            foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);   // the wedding's after-party still holds everyone
            ClearHostiles(map);
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.needs?.food != null) p.needs.food.CurLevel = 1f;
                if (!p.Awake()) RestUtility.WakeUp(p);
                if (p.InMentalState) p.MentalState.RecoverFromState();   // r106: the host was wandering confused and the party was called off at once
            }
            partyHost = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p != p1 && p != p2 && p.GetLord() == null);
            if (partyHost == null) throw new InvalidOperationException("no free colonist to host a party");
            partyJob = new LordJob_Joinable_Party(center, partyHost, GatheringDefOf.Party);
            // 5000–15000 by default; shorter keeps the probe quick. Must be set BEFORE MakeNewLord: CreateGraph bakes the value into its timeout trigger (r44).
            HarmonyLib.Traverse.Create(partyJob).Field("durationTicks").SetValue(PartyTicks);
            if (partyJob.DurationTicks != PartyTicks) throw new InvalidOperationException("party duration " + partyJob.DurationTicks);
            partyLord = LordMaker.MakeNewLord(Faction.OfPlayer, partyJob, map, new[] { partyHost });
            partyKey = Hooks.Key("party", partyJob);
            partyShotAt = -1;
            partyFile = null;
            Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 14f);
        }

        /// <summary>Ticks the party; true once the mid-party photo is in the album with its file (taken in the 60–95 % window, the party still on).</summary>
        private static bool PartyShotReady()
        {
            var store = PhotoStore.Instance;
            int el = partyLord.ticksInToil;
            var shot = store.Records.FirstOrDefault(r => r.eventKey == partyKey);
            if (shot != null && shot.HasFile && File.Exists(store.FilePath(shot)))
            {
                if (partyShotAt < 0) partyShotAt = el;
                if (partyShotAt < PartyTicks * 0.55f) throw new InvalidOperationException("mid-party shot taken too early: " + partyShotAt + " of " + PartyTicks);
                if (!map.lordManager.lords.Contains(partyLord)) throw new InvalidOperationException("the party was already over when its photo appeared");
                partyFile = store.FilePath(shot);
                return true;
            }
            if (!map.lordManager.lords.Contains(partyLord))
                throw new InvalidOperationException("the party lord ended at " + el + " of " + PartyTicks + " ticks before any photo (record " + (shot != null) +
                    ", file " + (shot != null && shot.HasFile) + ", pending " + store.HasPending + ", owned " + partyLord.ownedPawns.Count + "; danger " + map.dangerWatcher.DangerRating
                    + ", hostiles " + string.Join(",", map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer) && !p.Downed).Select(p => p.def.defName).Take(6))
                    + "; host " + partyHost.LabelShort + " drafted " + partyHost.Drafted + ", mental " + partyHost.MentalStateDef?.defName + ", downed " + partyHost.Downed
                    + ", bleed " + partyHost.health.hediffSet.BleedRateTotal.ToString("0.00") + ", spawned " + partyHost.Spawned + ")");
            if (PhotoCapture.Busy) return false;
            if (el > PartyTicks * 0.97f) throw new InvalidOperationException("no mid-party shot by " + el + " of " + PartyTicks + " ticks (record " + (shot != null) + ", lord alive true)");
            for (int i = 0; i < 100; i++) Find.TickManager.DoSingleTick();
            SettleDraw();
            return false;
        }

        private static bool RunParty()
        {
            var store = PhotoStore.Instance;
            switch (partyStep)
            {
                case 0:
                    StartProbeParty();
                    checks.Add("PASS: party lord created (host " + partyHost.LabelShort + ", " + PartyTicks + " ticks) for the mid-way shot");
                    partyStep = 1;
                    return false;
                case 1:
                    if (!PartyShotReady()) return false;
                    File.Copy(partyFile, Path.Combine(dir, "party.png"), true);
                    checks.Add(CaptureDiagLine("party", new[] { partyHost }));
                    RequirePeople("party.png");
                    checks.Add("PASS: party photographed part-way (" + partyShotAt + " of " + PartyTicks + " ticks) and in the album at once, while the party goes on");
                    partyStep = 2;
                    return false;
                case 2:
                    if (map.lordManager.lords.Contains(partyLord))
                    {
                        if (partyLord.ticksInToil > PartyTicks + 1200) throw new InvalidOperationException("party did not end by " + partyLord.ticksInToil + " ticks");
                        for (int i = 0; i < 100; i++) Find.TickManager.DoSingleTick();
                        return false;
                    }
                    {
                        var partyRecs = store.Records.Where(r => r.eventKey == partyKey).ToList();
                        if (partyRecs.Count != 1 || !partyRecs[0].HasFile) throw new InvalidOperationException("party photos for the key after the party ended: " + partyRecs.Count);
                        var rec = partyRecs[0];
                        if (rec.desc.NullOrEmpty() || !rec.desc.Contains(partyHost.LabelShort)) throw new InvalidOperationException("party sentence: '" + rec.desc + "'");
                        if (Math.Abs(rec.spanCells - Hooks.GatherMinSpan) > 0.01f && rec.spanCells < Hooks.GatherMinSpan) throw new InvalidOperationException("party span " + rec.spanCells);
                        checks.Add("PASS: party ended -> still one photo, the mid-way one: '" + rec.title + "' (span " + rec.spanCells + ", " + rec.people.Count + " people; sentence: " + rec.desc + ")");
                    }
                    StartProbeParty();
                    partyStep = 3;
                    return false;
                case 3:
                    if (!PartyShotReady()) return false;
                    // The event vanishes without ending (the lord is removed): the photo stays.
                    map.lordManager.RemoveLord(partyLord);
                    partyStep = 4;
                    return false;
                case 4:
                    for (int i = 0; i < 300; i++) Find.TickManager.DoSingleTick();
                    partyStep = 5;
                    return false;
                case 5:
                    {
                        var kept = store.Records.Where(r => r.eventKey == partyKey).ToList();
                        if (kept.Count != 1 || !File.Exists(partyFile)) throw new InvalidOperationException("photo of the vanished party: " + kept.Count + " records, file " + File.Exists(partyFile));
                        checks.Add("PASS: a party whose lord vanished part-way keeps its photo (1 record, file on disk)");
                    }
                    Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 14f);
                    return true;
            }
            return false;
        }

        /// <summary>After a batch of manual ticks, put every pawn's draw position on its real position. The game moves the draw position
        /// (PawnTweener) 9% x speed per *frame* toward the cell, snapping only at 5x or when the pawn was not drawn last frame; a hundred ticks
        /// inside one frame leave it tens of cells behind, so a photo taken in the next frame shows nobody (r20~r36 wedding and group photos —
        /// a probe artifact, not something the real game does, where ticks and frames interleave).</summary>
        private static void SettleDraw()
        {
            foreach (var p in map.mapPawns.AllPawnsSpawned) p.Drawer.tweener.ResetTweenedPosToRoot();
        }

        private static void Shot(string name)
        {
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, name));
        }

        /// <summary>Counts pixels in the colonists' pale-blue starting clothes (quick-test outfits) — terrain, walls and frames never have them.
        /// Cheap way to assert that a photo of people actually shows people (the r36 group and wedding photos were empty).</summary>
        private static bool PawnsVisible(string pngPath, out int lit)
        {
            lit = 0;
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(pngPath), false)) return false;
                var px = tex.GetPixels32();
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i];
                    if (c.b > 150 && c.g > 150 && c.b > c.r + 40 && c.g > c.r + 20) lit++;   // pale blue/cyan cloth
                }
                return lit > 150;
            }
            finally { UnityEngine.Object.Destroy(tex); }
        }

        private static void CloseLog()
        {
            try { Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), false); }
            catch { }
        }

        private static void Require(string name)
        {
            if (!File.Exists(Path.Combine(dir, name))) throw new InvalidOperationException("screenshot missing: " + name);
            checks.Add("PASS: screenshot " + name);
        }

        private static void Finish()
        {
            var store = PhotoStore.Instance;
            var sb = new StringBuilder();
            sb.Append("{\n  \"ok\": true,\n  \"folder\": ").Append(Json(store.FolderPath)).Append(",\n  \"records\": [\n");
            for (int i = 0; i < store.Records.Count; i++)
            {
                var r = store.Records[i];
                sb.Append("    {\"kind\": ").Append(Json(r.kind)).Append(", \"title\": ").Append(Json(r.title)).Append(", \"file\": ").Append(Json(r.file ?? "")).Append(", \"date\": ").Append(Json(r.dateLabel)).Append(", \"people\": ").Append(Json(string.Join(", ", r.people))).Append("}").Append(i + 1 < store.Records.Count ? ",\n" : "\n");
            }
            sb.Append("  ],\n  \"checks\": [\n");
            for (int i = 0; i < checks.Count; i++) sb.Append("    ").Append(Json(checks[i])).Append(i + 1 < checks.Count ? ",\n" : "\n");
            sb.Append("  ],\n  \"limitations\": \"disposable quick-test map, Harmony + " + (ModsConfig.IdeologyActive ? "installed expansions" : "Core only") + "; wedding and party driven by DoSingleTick batches; gravship launch, bestowing ceremony and ideoligion rituals (mid-ritual shot, role-change ceremony) not reproducible here (code review only)\"\n}\n");
            File.WriteAllText(Path.Combine(dir, "result.json"), sb.ToString());
            Log.Message("[MomentFrames probe] PASS\n" + string.Join("\n", checks));
            active = false;
            Application.Quit();
        }

        private static void Fail(Exception e)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "error.txt"), "phase " + phase + "\n" + e + "\n\nchecks so far:\n" + string.Join("\n", checks));
            }
            catch { }
            Log.Error("[MomentFrames probe] FAIL in phase " + phase + ": " + e);
            active = false;
            Application.Quit();
        }

        private static string Json(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
