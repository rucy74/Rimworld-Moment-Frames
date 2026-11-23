using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // Runs only behind Probe's marked disposable-profile gate. Construction is a controlled
    // real Frame.CompleteConstruction call, not a claim about natural builder AI or a player save.
    public static partial class Probe
    {
        private static bool settlementMode;
        private static bool settlementPreview;
        private static int settlementTarget, settlementCount;
        private static string settlementSnapshot, settlementCandidate;
        private static int settlementTracePhase = -1;
        private static float settlementFarClip, settlementNearClip;
        private static IntVec3 settlementBuild = new IntVec3(108, 0, 107);
        private static IntVec3 settlementGap;
        private static Blueprint settlementUnfinished;
        private static Building settlementBench;
        private static readonly string[] settlementNames = { "overview", "north-west", "north-east", "south-west", "south-east" };
        private const string SettlementSave = "mf-settlement-fixture";

        private static void SettlementAssert(bool value, string label)
        { if (!value) throw new InvalidOperationException(label); checks.Add("PASS: " + label); }

        private static void SettlementStep()
        {
            if (settlementPreview) { SettlementPreviewStep(); return; }
            if (settlementTracePhase != phase)
            { settlementTracePhase = phase; File.WriteAllText(Path.Combine(dir, "phase.txt"), phase + " at tick " + Find.TickManager.TicksGame); }
            var store = PhotoStore.Instance;
            switch (phase)
            {
                case 0:
                    SettlementSetup();
                    SettlementAssert(map.Size.x == 250 && map.Size.z == 250, "actual native map is 250 x 250 cells");
                    SettlementAssert(store.Settlement.Request(store, map, SettlementChronicle.Stamp(map, Find.TickManager.TicksAbs), false), "manual full-map request works with automatic capture disabled");
                    settlementSnapshot = store.Settlement.Snapshots.Last().id;
                    phase = 1; Wait(.1f); return;
                case 1:
                    var snapshot = store.Settlement.Snapshots.First(s => s.id == settlementSnapshot);
                    if (snapshot.failed) throw new InvalidOperationException("full-map capture failed after " + snapshot.attempts + " attempts");
                    if (!snapshot.complete) { Wait(.1f); return; }
                    SettlementAssert(snapshot.photoIds.Count == 5, "one survey commits exactly five image records");
                    SettlementAssert(Mathf.Abs(Find.Camera.farClipPlane - settlementFarClip) < .001f && Mathf.Abs(Find.Camera.nearClipPlane - settlementNearClip) < .001f,
                        "five area captures restore the native camera far and near clipping planes");
                    SettlementExport(store, snapshot);
                    SettlementOpenTab(); Wait(2f); phase = 2; return;
                case 2:
                    CloseLog(); Shot("native-settlement-overview.png"); Wait(2f); phase = 3; return;
                case 3:
                    SettlementAssert(File.Exists(Path.Combine(dir, "native-settlement-overview.png")), "native settlement overview screenshot exists");
                    Traverse.Create(typeof(SettlementRecordsTab)).Field("scroll").SetValue(new Vector2(0f, 525f));
                    Wait(2f); phase = 4; return;
                case 4:
                    CloseLog(); Shot("native-settlement-quadrants.png"); Wait(2f); phase = 5; return;
                case 5:
                    SettlementAssert(File.Exists(Path.Combine(dir, "native-settlement-quadrants.png")), "native quadrant cards screenshot exists");
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    MomentFramesMod.Settings.autoCaptureEnabled = true;
                    MomentFramesMod.Settings.buildingPhotos = true;
                    SettlementRoom(settlementBuild.x, settlementBuild.z, 20, 14, true, true);
                    settlementGap = new IntVec3(settlementBuild.x + 8, 0, settlementBuild.z + 8);
                    map.roofGrid.SetRoof(settlementGap, null);
                    settlementUnfinished = (Blueprint)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Bed.blueprintDef, ThingDefOf.WoodLog),
                        new IntVec3(settlementBuild.x + 15, 0, settlementBuild.z + 4), map);
                    settlementUnfinished.SetFaction(Faction.OfPlayer);
                    settlementBench = map.listerThings.ThingsOfDef(ThingDefOf.SimpleResearchBench).OfType<Building>()
                        .First(b => b.Position.x > settlementBuild.x && b.Position.x < settlementBuild.x + 20);
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                    var candidates = Traverse.Create(map.GetComponent<MapComponent_MFBuildingMilestones>()).Field("candidates").GetValue<List<MFBuildingCandidate>>();
                    SettlementAssert(candidates.Count > 0, "real Frame completion generated a persisted building candidate");
                    settlementCandidate = candidates.Last().eventKey;
                    settlementTarget = Find.TickManager.TicksGame + 6800;
                    phase = 6; return;
                case 6:
                    if (!SettlementTicks()) return;
                    SettlementAssert(!store.Records.Any(r => r.kind == "building"), "roof gap and unfinished blueprint prevent an early completion photo");
                    GameDataSaveLoader.SaveGame(SettlementSave); phase = 7; Wait(2f); return;
                case 7:
                    GameDataSaveLoader.LoadGame(SettlementSave); phase = 8; Wait(3f); return;
                case 8:
                    map = Find.CurrentMap; p1 = map.mapPawns.FreeColonistsSpawned.First(); CloseLog(); Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    var restored = Traverse.Create(map.GetComponent<MapComponent_MFBuildingMilestones>()).Field("candidates").GetValue<List<MFBuildingCandidate>>();
                    SettlementAssert(restored.Any(c => c.eventKey == settlementCandidate), "unfinished construction candidate survives native save/load");
                    SettlementAssert(store.Settlement.Snapshots.Any(s => s.id == settlementSnapshot && s.complete && s.photoIds.Count == 5), "completed five-image survey survives native save/load");
                    map.roofGrid.SetRoof(settlementGap, RoofDefOf.RoofConstructed);
                    foreach (var unfinished in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Where(t => t.Position.x >= settlementBuild.x && t.Position.x < settlementBuild.x + 20).ToList()) unfinished.Destroy();
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                    settlementTarget = Find.TickManager.TicksGame + 7300; phase = 9; return;
                case 9:
                    if (!SettlementTicks()) return;
                    phase = 10; Wait(.1f); return;
                case 10:
                    var building = store.Records.FirstOrDefault(r => r.kind == "building" && r.HasFile);
                    if (building == null) { Wait(.2f); return; }
                    SettlementAssert(store.Records.Count(r => r.kind == "building" && r.HasFile) == 1, "stable roofed usable new room creates exactly one saved building photo");
                    File.Copy(store.FilePath(building), Path.Combine(dir, "building.png"), true);
                    SettlementOpenTab();
                    Traverse.Create(typeof(SettlementRecordsTab)).Field("scroll").SetValue(new Vector2(0f, 2000f));
                    phase = 11; Wait(2f); return;
                case 11:
                    CloseLog(); Shot("native-settlement-building.png"); phase = 12; Wait(2f); return;
                case 12:
                    SettlementAssert(File.Exists(Path.Combine(dir, "native-settlement-building.png")), "native dated building photograph screenshot exists");
                    Find.MainTabsRoot.EscapeCurrentTab(false); settlementCount = store.Records.Count;
                    GameDataSaveLoader.SaveGame(SettlementSave); phase = 13; Wait(2f); return;
                case 13:
                    GameDataSaveLoader.LoadGame(SettlementSave); phase = 14; Wait(3f); return;
                case 14:
                    map = Find.CurrentMap; p1 = map.mapPawns.FreeColonistsSpawned.First(); CloseLog(); Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    SettlementAssert(store.Records.Count == settlementCount && store.Records.Count(r => r.kind == "building" && r.HasFile) == 1,
                        "completed building and survey records reload without duplicates");
                    settlementBench = map.listerThings.ThingsOfDef(ThingDefOf.SimpleResearchBench).OfType<Building>()
                        .First(b => b.Position.x > settlementBuild.x && b.Position.x < settlementBuild.x + 20);
                    IntVec3 benchPosition = settlementBench.Position; Rot4 benchRotation = settlementBench.Rotation;
                    settlementBench.Destroy(); SettlementComplete(ThingDefOf.SimpleResearchBench, benchPosition, benchRotation);
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                    settlementTarget = Find.TickManager.TicksGame + 13000; phase = 15; return;
                case 15:
                    if (!SettlementTicks()) return;
                    SettlementAssert(store.Records.Count(r => r.kind == "building" && r.HasFile) == 1, "replacing furniture in a recorded room does not make a new-building photo");
                    SettlementCrossMap();
                    SettlementFinish(store); return;
            }
        }
        private static void SettlementPreviewStep()
        {
            switch (phase)
            {
                case 0:
                    map = Find.CurrentMap; Prefs.DevMode = false; CloseLog(); Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    var s = MomentFramesMod.Settings;
                    s.autoCaptureEnabled = false; s.buildingPhotos = false; s.settlementPeriod = 0;
                    s.albumTheme = "graphite"; s.albumFont = "pencil"; s.albumTextSize = 115; s.albumOpacity = 1f;
                    SettlementAssert(PhotoStore.Instance.Records.Count(r => r.HasFile && SettlementChronicle.IsSurvey(r)) == 5
                        && PhotoStore.Instance.Records.Count(r => r.HasFile && r.kind == "building") == 1,
                        "preview loads the five survey images and one building image from copied disposable save/photo data");
                    SettlementOpenTab(); Wait(2f); phase = 1; return;
                case 1:
                    CloseLog(); Shot("native-settlement-overview.png"); Wait(2f); phase = 2; return;
                case 2:
                    SettlementAssert(File.Exists(Path.Combine(dir, "native-settlement-overview.png")), "enlarged overview native screen exists");
                    Traverse.Create(typeof(SettlementRecordsTab)).Field("scroll").SetValue(new Vector2(0f, 850f));
                    Wait(2f); phase = 3; return;
                case 3:
                    CloseLog(); Shot("native-settlement-quadrants.png"); Wait(2f); phase = 4; return;
                case 4:
                    SettlementAssert(File.Exists(Path.Combine(dir, "native-settlement-quadrants.png")), "quadrant native screen exists after layout enlargement");
                    Traverse.Create(typeof(SettlementRecordsTab)).Field("scroll").SetValue(new Vector2(0f, 2000f));
                    Wait(2f); phase = 5; return;
                case 5:
                    CloseLog(); Shot("native-settlement-building.png"); Wait(2f); phase = 6; return;
                case 6:
                    SettlementAssert(File.Exists(Path.Combine(dir, "native-settlement-building.png")), "dated building native screen exists after layout enlargement");
                    File.WriteAllText(Path.Combine(dir, "preview-result.json"), "{\"ok\":true,\"fixture\":true,\"scope\":\"Only actual enlarged native UI using copied disposable fixture save and photos; no new lifecycle claims.\",\"checks\":[" + string.Join(",", checks.Select(Json)) + "]}");
                    active = false; Application.Quit(); return;
            }
        }

        private static bool SettlementTicks()
        {
            if (PhotoCapture.Busy) { Wait(.05f); return false; }
            int count = Math.Min(250, settlementTarget - Find.TickManager.TicksGame);
            for (int i = 0; i < count; i++) Find.TickManager.DoSingleTick();
            SettleDraw(); CloseLog(); Wait(.01f);
            return Find.TickManager.TicksGame >= settlementTarget;
        }
        private static void SettlementSetup()
        {
            map = Find.CurrentMap; center = map.Center; Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear(); ClearHostiles(map); CloseLog();
            var s = MomentFramesMod.Settings; s.autoCaptureEnabled = false; s.settlementPeriod = 0; s.buildingPhotos = false;
            Prefs.DevMode = false;
            s.memoryNight = false; s.yearLetter = false; s.lifeEvents = false; s.msAnniversaryLetter = false;
            s.fxFreeze = s.fxCamera = s.fxFlash = s.fxPopup = s.fxShutter = false; s.brightPhotos = true;
            s.albumTheme = "graphite"; s.albumFont = "pencil"; s.albumTextSize = 115; s.albumOpacity = 1f;
            foreach (var c in map.AllCells) map.fogGrid.Unfog(c);
            p1 = map.mapPawns.FreeColonistsSpawned.First(); p2 = map.mapPawns.FreeColonistsSpawned.Skip(1).FirstOrDefault();
            foreach (var p in map.mapPawns.FreeColonistsSpawned.ToList()) { FixReady(p); p.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced); }
            // Clear only the designed scene and leave the rest of the generated native landscape visible.
            var town = new CellRect(82, 76, 86, 94);
            foreach (var t in map.listerThings.AllThings.Where(t => !(t is Pawn) && t.def.destroyable && town.Contains(t.Position)).ToList()) t.Destroy();
            foreach (var c in town) { map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil); map.roofGrid.SetRoof(c, null); }
            SettlementRoom(90, 132, 14, 12, false, false);
            SettlementRoom(109, 139, 19, 14, false, false);
            SettlementRoom(139, 129, 17, 17, false, false);
            SettlementRoom(143, 100, 17, 18, false, false);
            SettlementRoom(94, 81, 17, 15, false, false);
            // Native map-edge landmarks prove the five pictures include all four corners, not a central crop.
            string[] floors = { "WoodPlankFloor", "Concrete", "MetalTile", "PavedTile" };
            IntVec3[] corners = { new IntVec3(4, 0, 230), new IntVec3(230, 0, 230), new IntVec3(4, 0, 4), new IntVec3(230, 0, 4) };
            for (int i = 0; i < 4; i++)
                foreach (var c in new CellRect(corners[i].x, corners[i].z, 16, 16))
                { c.GetEdifice(map)?.Destroy(); map.roofGrid.SetRoof(c, null); map.terrainGrid.SetTerrain(c, DefDatabase<TerrainDef>.GetNamed(floors[i])); }
            int n = 0; foreach (var p in map.mapPawns.FreeColonistsSpawned.ToList()) p.Position = new IntVec3(119 + n++ * 2, 0, 127);
            SettleDraw(); map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            var component = map.GetComponent<MapComponent_MFBuildingMilestones>();
            Traverse.Create(component).Field("initialized").SetValue(false);
            Traverse.Create(component).Field("known").SetValue(new List<MFBuildingFootprint>());
            Traverse.Create(component).Field("candidates").SetValue(new List<MFBuildingCandidate>());
            component.EnsureBaseline();
            SettlementAssert(Traverse.Create(component).Field("known").GetValue<List<MFBuildingFootprint>>().Count >= 5,
                "native baseline remembers five pre-existing roofed rooms by their cell footprints");
            Find.CameraDriver.JumpToCurrentMapLoc(center.ToVector3Shifted()); Find.CameraDriver.SetRootSize(44f);
            map.mapDrawer.RegenerateEverythingNow();
            settlementFarClip = Find.Camera.farClipPlane; settlementNearClip = Find.Camera.nearClipPlane;
            checks.Add("INFO: native camera clipping before survey near=" + settlementNearClip + "; far=" + settlementFarClip + "; current altitude=" + Find.Camera.transform.position.y
                + "; area span125 requires driver root62.5 and a larger altitude than default far65.5");
        }
        private static void SettlementRoom(int x, int z, int width, int height, bool completedFrame, bool research)
        {
            var area = new CellRect(x, z, width, height);
            foreach (var c in area)
            {
                c.GetEdifice(map)?.Destroy(); map.terrainGrid.SetTerrain(c, DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                if (c.x == x || c.x == x + width - 1 || c.z == z || c.z == z + height - 1)
                {
                    if (c.x == x + width / 2 && c.z == z) continue;
                    var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog); wall.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(wall, c, map);
                }
            }
            var doorway = new IntVec3(x + width / 2, 0, z);
            if (completedFrame) SettlementComplete(ThingDefOf.Door, doorway, Rot4.North);
            else { var door = ThingMaker.MakeThing(ThingDefOf.Door, ThingDefOf.WoodLog); door.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(door, doorway, map); }
            var furniture = ThingMaker.MakeThing(research ? ThingDefOf.SimpleResearchBench : ThingDefOf.Bed, ThingDefOf.WoodLog);
            furniture.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(furniture, new IntVec3(x + 4, 0, z + 5), map, Rot4.North);
        }
        private static void SettlementComplete(ThingDef built, IntVec3 cell, Rot4 rotation)
        {
            var work = (RimWorld.Frame)ThingMaker.MakeThing(built.frameDef, built.MadeFromStuff ? ThingDefOf.WoodLog : null);
            work.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(work, cell, map, rotation); work.CompleteConstruction(p1);
            SettlementAssert(work.Destroyed && cell.GetEdifice(map)?.def == built, "original Frame.CompleteConstruction completed " + built.defName);
        }
        private static void SettlementOpenTab()
        {
            Find.MainTabsRoot.SetCurrentTab(AlbumDef, false);
            var album = (MainTabWindow_Album)AlbumDef.TabWindow;
            var type = typeof(MainTabWindow_Album).GetNestedType("Tab", System.Reflection.BindingFlags.NonPublic);
            Traverse.Create(album).Method("SetTab", Enum.Parse(type, "Settlement")).GetValue();
            album.windowRect = new Rect(18f, 18f, UI.screenWidth - 36f, UI.screenHeight - 62f);
        }
        private static void SettlementCrossMap()
        {
            var layer = Find.WorldGrid.Surface;
            PlanetTile tile = PlanetTile.Invalid;
            for (int i = 0; i < layer.TilesCount; i++)
            {
                var candidate = layer.PlanetTileForID(i);
                if (candidate == map.Tile || Find.World.Impassable(candidate) || Find.WorldObjects.AnyWorldObjectAt(candidate)
                    || Find.WorldGrid[candidate].PrimaryBiome?.canBuildBase != true) continue;
                tile = candidate; break;
            }
            SettlementAssert(tile.Valid, "second native map has a valid independent world tile");
            var second = GetOrGenerateMapUtility.GetOrGenerateMap(tile, new IntVec3(100, 1, 100), WorldObjectDefOf.Camp ?? WorldObjectDefOf.AbandonedSettlement);
            Current.Game.CurrentMap = map;
            var camera = Find.CameraDriver; var mainCamera = camera.GetComponent<Camera>();
            Vector3 root = Traverse.Create(camera).Field("rootPos").GetValue<Vector3>();
            float size = Traverse.Create(camera).Field("rootSize").GetValue<float>();
            bool callback = false;
            SettlementAssert(!PhotoCapture.Capture(second, second.Center, 19f, _ => callback = true), "ordinary capture rejects a non-current native map");
            SettlementAssert(!PhotoCapture.CaptureArea(second, new CellRect(0, 0, 100, 100), _ => callback = true), "full-area capture rejects a non-current native map");
            SettlementAssert(ReferenceEquals(Find.CurrentMap, map) && ReferenceEquals(Find.CameraDriver, camera)
                && ReferenceEquals(camera.GetComponent<Camera>(), mainCamera) && !callback && !PhotoCapture.Busy
                && Traverse.Create(camera).Field("rootPos").GetValue<Vector3>() == root
                && Mathf.Abs(Traverse.Create(camera).Field("rootSize").GetValue<float>() - size) < .001f,
                "rejected cross-map requests leave viewed map, camera identity, root position, size, callback and capture state intact");
            var away = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(away, CellFinder.RandomClosewalkCellNear(second.Center, second, 5), second);
            var store = PhotoStore.Instance; string note = "Fixture explicit off-map event note";
            var deferred = store.Request(second, away.Position.ToVector3Shifted(), "manual", "fixture-offmap:" + Guid.NewGuid().ToString("N"), "Fixture off-map event", note, new[] { away });
            SettlementAssert(deferred != null && deferred.deferredCapture && deferred.eventPeopleNames.Count == 1 && deferred.eventPeopleIds.Count == 1
                && deferred.eventPeopleNames[0] == away.LabelShort && deferred.eventPeopleIds[0] == away.GetUniqueLoadID()
                && deferred.eventPeopleNames.SequenceEqual(deferred.people) && deferred.eventPeopleIds.SequenceEqual(deferred.peopleIds) && deferred.captureDeadlineTick > Find.TickManager.TicksGame,
                "off-map request immediately preserves facts, event people and expiry deadline without switching the viewed map");
            store.SetCaptureDeadline(deferred, Find.TickManager.TicksGame - 1);
            // GameComponentUpdate itself calls Probe.Update; disable only this nested probe callback.
            active = false; try { store.GameComponentUpdate(); } finally { active = true; }
            SettlementAssert(!deferred.HasFile && deferred.eventOnly && deferred.desc.StartsWith(note, StringComparison.Ordinal)
                && deferred.eventPeopleNames.Count == 1 && deferred.eventPeopleIds[0] == away.GetUniqueLoadID() && deferred.people.Count == 0
                && deferred.peopleIds.Count == 0 && ReferenceEquals(Find.CurrentMap, map),
                "expired off-map request leaves an event-only record with its explicit note and does not move the map");
            var edgePawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            var edge = new CellRect(1, 10, 7, 80).First(c => c.Standable(second));
            GenSpawn.Spawn(edgePawn, edge, second);
            var area = new CellRect(0, 0, 100, 100);
            var areaCenter = new Vector3(50f, 0f, 50f);
            SettlementAssert(!PhotoCapture.InFrame(edgePawn, second, areaCenter, 81f, 1f),
                "100x100 non-current area fixture has a real colonist outside the ordinary 81-cell frame");
            string areaNote = "Fixture explicit large off-map area event note";
            var areaDeferred = store.RequestAutomaticArea(second, area, "building", "fixture-offmap-area:" + Guid.NewGuid().ToString("N"),
                "Fixture full off-map area event", areaNote, new[] { edgePawn });
            SettlementAssert(areaDeferred != null && areaDeferred.deferredCapture && areaDeferred.eventSubtype == "mf-survey-area"
                && Mathf.Abs(areaDeferred.spanCells - 100f) < .001f && Mathf.Abs(areaDeferred.Aspect - 1f) < .001f
                && areaDeferred.center == areaCenter && areaDeferred.peopleIds.Count == 1 && areaDeferred.eventPeopleIds.Count == 1
                && areaDeferred.peopleIds[0] == edgePawn.GetUniqueLoadID() && areaDeferred.eventPeopleIds[0] == edgePawn.GetUniqueLoadID()
                && areaDeferred.eventPeopleNames[0] == edgePawn.LabelShort,
                "explicit 100x100 area freezes its edge colonist event roster despite the ordinary 81-cell bound");
            store.SetCaptureDeadline(areaDeferred, Find.TickManager.TicksGame - 1);
            active = false; try { store.GameComponentUpdate(); } finally { active = true; }
            SettlementAssert(!areaDeferred.HasFile && areaDeferred.eventOnly && areaDeferred.desc.StartsWith(areaNote, StringComparison.Ordinal)
                && areaDeferred.people.Count == 0 && areaDeferred.peopleIds.Count == 0 && areaDeferred.eventPeopleIds.Count == 1
                && areaDeferred.eventPeopleIds[0] == edgePawn.GetUniqueLoadID() && areaDeferred.eventPeopleNames[0] == edgePawn.LabelShort
                && ReferenceEquals(Find.CurrentMap, map) && ReferenceEquals(Find.CameraDriver, camera),
                "expired explicit-area request preserves the actual edge-person event snapshot and note without a photograph roster or map switch");
        }
        private static void SettlementExport(PhotoStore store, SettlementSnapshot snapshot)
        {
            var result = new StringBuilder("{\n  \"fixture\": true, \"mapCells\": [250,250], \"photos\": [\n");
            var areas = new[] { new CellRect(0, 0, 250, 250) }.Concat(SettlementLayout.Quadrants(250, 250)).ToArray();
            for (int i = 0; i < 5; i++)
            {
                var rec = store.Records.First(r => r.id == snapshot.photoIds[i]);
                SettlementAssert(rec.HasFile && rec.mapId == map.uniqueID, settlementNames[i] + " file committed for the right map");
                var texture = new Texture2D(2, 2);
                try
                {
                    texture.LoadImage(File.ReadAllBytes(store.FilePath(rec)));
                    SettlementAssert(texture.width == (i == 0 ? 4096 : 2048) && texture.height == texture.width,
                        settlementNames[i] + " actual PNG dimensions " + texture.width + " x " + texture.height);
                    SettlementAssert(Mathf.Abs(rec.spanCells - areas[i].Height) < .01f && Mathf.Abs(rec.Aspect - 1f) < .001f,
                        settlementNames[i] + " metadata covers its complete cell area");
                    var pixels = texture.GetRawTextureData<byte>(); int lit = 0, sampled = 0;
                    int step = Math.Max(1, texture.width * texture.height / 10000);
                    for (int p = 0; p < texture.width * texture.height; p += step)
                    { int offset = p * 3; sampled++; if (pixels[offset] > 12 || pixels[offset + 1] > 12 || pixels[offset + 2] > 12) lit++; }
                    SettlementAssert(lit > sampled * .4f, settlementNames[i] + " unfogged native terrain is visible: " + lit + "/" + sampled + " lit samples");
                    int markerCount = i == 0 ? 4 : 1;
                    for (int marker = 0; marker < markerCount; marker++)
                    {
                        int q = i == 0 ? marker : i - 1;
                        float mx = q % 2 == 0 ? (i == 0 ? .048f : .096f) : (i == 0 ? .952f : .904f);
                        float my = q / 2 == 0 ? (i == 0 ? .952f : .904f) : (i == 0 ? .048f : .096f);
                        Color pixel = texture.GetPixel(Mathf.FloorToInt(texture.width * mx), Mathf.FloorToInt(texture.height * my));
                        SettlementAssert(pixel.r > .04f || pixel.g > .04f || pixel.b > .04f,
                            settlementNames[i] + " map-corner terrain marker " + q + " has visible RGB " + pixel.r.ToString("F3") + "," + pixel.g.ToString("F3") + "," + pixel.b.ToString("F3"));
                    }
                    SettlementAssert(rec.capturedTicksAbs == rec.ticksAbs && rec.capturedTicksAbs >= 0,
                        settlementNames[i] + " records its actual shutter timestamp");
                }
                finally { UnityEngine.Object.Destroy(texture); }
                string file = settlementNames[i] + ".png"; File.Copy(store.FilePath(rec), Path.Combine(dir, file), true);
                result.Append("    {\"name\": ").Append(Json(settlementNames[i])).Append(", \"file\": ").Append(Json(file))
                    .Append(", \"id\": ").Append(Json(rec.id)).Append(", \"title\": ").Append(Json(rec.DisplayTitle))
                    .Append(", \"date\": ").Append(Json(rec.dateLabel)).Append(", \"bounds\": ").Append(Json(areas[i].ToString())).Append("}").Append(i < 4 ? ",\n" : "\n");
            }
            File.WriteAllText(Path.Combine(dir, "survey.json"), result.Append("  ]\n}\n").ToString());
        }
        private static void SettlementFinish(PhotoStore store)
        {
            var sb = new StringBuilder("{\n  \"ok\": true, \"fixture\": true, \"mapCells\": [250,250], \"checks\": [\n");
            for (int i = 0; i < checks.Count; i++) sb.Append("    ").Append(Json(checks[i])).Append(i < checks.Count - 1 ? ",\n" : "\n");
            var building = store.Records.First(r => r.kind == "building" && r.HasFile);
            sb.Append("  ],\n  \"building\": {\"id\": ").Append(Json(building.id)).Append(", \"title\": ").Append(Json(building.DisplayTitle))
                .Append(", \"date\": ").Append(Json(building.dateLabel)).Append(", \"description\": ").Append(Json(building.DisplayDesc))
                .Append("},\n  \"limitations\": \"Harmony + Core + run-owned MomentFrames; controlled 250-cell native fixture; construction uses real Frame completion with no natural builder job or material-consumption claim; isolated saves only; no user save or full modpack compatibility claim\"\n}\n");
            File.WriteAllText(Path.Combine(dir, "result.json"), sb.ToString());
            Log.Message("[MomentFrames settlement probe] PASS " + checks.Count); active = false; Application.Quit();
        }
    }
}
