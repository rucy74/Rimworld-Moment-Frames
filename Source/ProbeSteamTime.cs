using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // Film-only, behind the same disposable-profile gate as the rest of the Steam media scenes. A colony is put up stage by stage in the test world and each
    // stage is surveyed by the real settlement record, so the album's own "view changes" has something to show. The buildings are placed, not built by colonists.
    public static partial class Probe
    {
        private const int TimeStages = 5;
        private const string TimePastColony = "past-colony";
        private static int timeStage, timeRegion, timeFrame, timeLastStamp;
        private static IntVec3 timeHome;
        private static string timeSnapshot;
        private static Dialog_SettlementSequence timeSequence;
        private static int timeShown;
        private static readonly HashSet<Thing> timeKept = new HashSet<Thing>();

        private static IntVec3 TimeAt(int x, int z) => timeHome + new IntVec3(x, 0, z);
        private static string TimePath(string name) => Path.Combine("steam", "time", name);

        private static void TimeClear(int x, int z, int w, int h)
        {
            foreach (var c in new CellRect(timeHome.x + x, timeHome.z + z, w, h).ClipInsideMap(map))
            {
                foreach (var t in c.GetThingList(map).ToList())
                    if (!(t is Pawn) && t.Faction != Faction.OfPlayer && !timeKept.Contains(t) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                map.roofGrid.SetRoof(c, null);
                if (c.GetTerrain(map).IsWater || !c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy))
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
            }
        }

        private static Thing TimePut(string def, int x, int z, Rot4 rot, string stuff = null)
        {
            var d = DefDatabase<ThingDef>.GetNamedSilentFail(def);
            if (d == null) { checks.Add("INFO: steam time — no " + def); return null; }
            var made = ThingMaker.MakeThing(d, d.MadeFromStuff ? ThingDef.Named(stuff ?? "WoodLog") : null);
            made.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(made, TimeAt(x, z), map, rot);
            made.TryGetComp<CompRefuelable>()?.Refuel(20f);
            return made;
        }

        private static void TimeFloor(int x, int z, int w, int h, string floor)
        {
            var terrain = DefDatabase<TerrainDef>.GetNamed(floor);
            foreach (var c in new CellRect(timeHome.x + x, timeHome.z + z, w, h).ClipInsideMap(map)) map.terrainGrid.SetTerrain(c, terrain);
        }

        private static void TimeRoom(int x, int z, int w, int h, string stuff, string floor, int doorX, int doorZ)
        {
            TimeClear(x, z, w, h);
            TimeFloor(x, z, w, h, floor);
            for (int i = 0; i < w; i++)
                for (int j = 0; j < h; j++)
                {
                    if (i != 0 && j != 0 && i != w - 1 && j != h - 1) continue;
                    if (x + i == doorX && z + j == doorZ) TimePut("Door", doorX, doorZ, Rot4.North, stuff);
                    else TimePut("Wall", x + i, z + j, Rot4.North, stuff);
                }
        }

        private static void TimePlant(string def, int x, int z, float growth)
        {
            var d = DefDatabase<ThingDef>.GetNamedSilentFail(def);
            var c = TimeAt(x, z);
            if (d == null || !c.InBounds(map)) return;
            foreach (var old in c.GetThingList(map).Where(t => t is Plant).ToList()) { timeKept.Remove(old); old.Destroy(DestroyMode.Vanish); }
            var plant = (Plant)ThingMaker.MakeThing(d);
            plant.Growth = growth;
            plant.sown = true;
            GenSpawn.Spawn(plant, c, map);
            timeKept.Add(plant);
        }

        private static void TimeField(string def, int x, int z, int w, int h, float growth)
        {
            TimeClear(x, z, w, h);
            for (int i = 0; i < w; i++)
                for (int j = 0; j < h; j++) TimePlant(def, x + i, z + j, growth);
        }

        private static void TimeGrow(string def, float growth)
        {
            foreach (var plant in timeKept.OfType<Plant>().Where(p => p.def.defName == def && p.Spawned)) plant.Growth = growth;
        }

        private static void TimeCast(params (int x, int z)[] places)
        {
            var people = map.mapPawns.FreeColonistsSpawned.ToList();
            for (int i = 0; i < people.Count; i++)
            {
                var p = people[i];
                p.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
                if (i < places.Length) Teleport(p, TimeAt(places[i].x, places[i].z));
                else Teleport(p, TimeAt(-40 - i, -30));
            }
            SettleDraw();
        }

        /// <summary>Five real quarters of one colony: a camp, a first house, a kitchen and paths, a workshop hall, and finally a wall around all of it.</summary>
        private static void TimeBuild(int stage)
        {
            switch (stage)
            {
                case 0:
                    TimeClear(-14, -9, 28, 18);
                    TimePut("Campfire", 0, 0, Rot4.North);
                    TimePut("Bedroll", -4, 3, Rot4.South, "Cloth");
                    TimePut("Bedroll", -1, 4, Rot4.South, "Cloth");
                    TimePut("Bedroll", 2, 4, Rot4.South, "Cloth");
                    TimePut("Bedroll", 5, 3, Rot4.South, "Cloth");
                    TimeField("Plant_Rice", -13, -9, 9, 5, .25f);
                    TimeCast((-2, 1), (2, 1), (-2, -2), (2, -2), (-9, -3), (-6, -3));
                    break;
                case 1:
                    foreach (var roll in map.listerThings.ThingsOfDef(ThingDef.Named("Bedroll")).ToList()) roll.Destroy(DestroyMode.Vanish);
                    TimeRoom(-16, 6, 15, 9, "WoodLog", "WoodPlankFloor", -9, 6);
                    for (int i = 0; i < 4; i++) TimePut("Bed", -14 + i * 3, 12, Rot4.South);
                    TimePut("Table1x2c", -6, 8, Rot4.East);
                    TimePut("DiningChair", -6, 7, Rot4.North);
                    TimePut("TorchLamp", -14, 8, Rot4.North);
                    TimeGrow("Plant_Rice", 1f);
                    TimeField("Plant_Corn", 5, -9, 11, 7, .5f);
                    TimeCast((-9, 4), (-7, 4), (0, 2), (6, -1), (-4, -7), (10, -1));
                    break;
                case 2:
                    TimeRoom(2, 6, 16, 10, "BlocksGranite", "PavedTile", 9, 6);
                    TimePut("Table2x4c", 8, 10, Rot4.North);
                    for (int i = 0; i < 4; i++) TimePut("DiningChair", 7 + i, 9, Rot4.North);
                    for (int i = 0; i < 4; i++) TimePut("DiningChair", 7 + i, 12, Rot4.South);
                    TimePut("TorchLamp", 4, 8, Rot4.North);
                    TimePut("TorchLamp", 15, 8, Rot4.North);
                    TimeClear(-16, 5, 35, 1);
                    TimeFloor(-16, 5, 35, 1, "PavedTile");
                    TimeClear(2, -12, 1, 17);
                    TimeFloor(2, -12, 1, 17, "PavedTile");
                    TimeGrow("Plant_Corn", 1f);
                    TimeField("Plant_Potato", -13, -17, 9, 6, .6f);
                    for (int i = 0; i < 4; i++) { TimeClear(5 + i * 4, -14, 1, 1); TimePlant("Plant_TreeOak", 5 + i * 4, -14, .5f); }
                    TimeCast((0, 3), (5, 4), (9, 4), (8, 8), (-9, -14), (12, -1));
                    break;
                case 3:
                    TimeRoom(-16, 17, 34, 11, "BlocksGranite", "WoodPlankFloor", 0, 17);
                    TimePut("TableSculpting", -12, 25, Rot4.South);
                    TimePut("HandTailoringBench", -7, 25, Rot4.South);
                    TimePut("TableButcher", 5, 25, Rot4.South);
                    TimePut("Table2x4c", 9, 21, Rot4.North);
                    TimePut("TorchLamp", -14, 19, Rot4.North);
                    TimePut("TorchLamp", 15, 19, Rot4.North);
                    TimeClear(0, 16, 1, 1);
                    TimeFloor(0, 16, 1, 1, "PavedTile");
                    TimeGrow("Plant_Potato", 1f);
                    TimeClear(-23, -7, 8, 7);
                    TimePut("MF_StudioArch", -19, -4, Rot4.North);
                    for (int i = 0; i < 12; i++) { TimeClear(-16 + i * 3, 4, 1, 1); TimePlant(i % 2 == 0 ? "Plant_Rose" : "Plant_Daylily", -16 + i * 3, 4, 1f); }
                    TimeCast((0, 15), (-10, 21), (-3, 21), (8, 8), (-19, -6), (12, -1));
                    break;
                default:
                    int x0 = -24, z0 = -22, w = 51, h = 55;
                    TimeClear(x0, z0, w, h);
                    foreach (var c in new CellRect(timeHome.x + x0, timeHome.z + z0, w, h).EdgeCells)
                    {
                        int x = c.x - timeHome.x, z = c.z - timeHome.z;
                        if (z == z0 && x == 2) TimePut("Door", x, z, Rot4.North, "BlocksGranite");
                        else TimePut("Wall", x, z, Rot4.North, "BlocksGranite");
                    }
                    TimeRoom(8, -20, 14, 8, "WoodLog", "WoodPlankFloor", 14, -13);
                    for (int i = 0; i < 4; i++) TimePut("Bed", 10 + i * 3, -19, Rot4.North);
                    TimePut("MF_MemoryTree", 22, 10, Rot4.North);
                    for (int i = 0; i < 8; i++) { TimePlant("Plant_TreeOak", -22, -18 + i * 6, .9f); }
                    TimeCast((2, 2), (5, 4), (0, 15), (8, 8), (-19, -6), (20, 8));
                    break;
            }
            PreparationRefreshRegions();
        }

        private static int TimePickRegion()
        {
            checks.Add("INFO: steam time — map " + map.Size.x + " x " + map.Size.z);
            if (map.Size.x < 170 || map.Size.z < 170)
            {
                timeHome = map.Center;
                return 0;   // a small map: the colony fills the overview itself, a quadrant could not hold it
            }
            var quadrants = SettlementLayout.Quadrants(map.Size.x, map.Size.z);
            int best = 0, bestBad = int.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                var middle = CellRect.CenteredOn(quadrants[i].CenterCell, 34).ClipInsideMap(map);
                int bad = middle.Cells.Count(c => c.GetTerrain(map).IsWater || c.Impassable(map) || c.Roofed(map));
                if (bad < bestBad) { bestBad = bad; best = i; }
            }
            timeHome = quadrants[best].CenterCell;
            checks.Add("INFO: steam time — colony in quadrant " + best + " at " + timeHome + ", rough cells " + bestBad);
            return best + 1;
        }

        private static void TimeWorldVisits()
        {
            var store = PhotoStore.Instance;
            var grid = Find.WorldGrid;
            var surface = grid.Surface;
            var photos = store.Records.Where(r => r.HasFile && r.eventKey != null && r.eventKey.StartsWith("shots:"))
                .OrderBy(r => r.kind == "journey" ? 0 : r.kind == "wedding" ? 1 : r.kind == "group" ? 2 : r.kind == "petphoto" ? 3 : r.kind == "birthday" ? 4 : r.kind == "party" ? 5 : 6).Take(7).ToList();
            var chain = new List<PlanetTile> { map.Tile };
            var biomes = new HashSet<string> { grid[map.Tile].PrimaryBiome.defName };
            for (int k = 1; k < photos.Count; k++)
            {
                PlanetTile best = PlanetTile.Invalid; float score = float.MinValue;
                for (int i = 0; i < surface.TilesCount; i++)
                {
                    var t = surface.PlanetTileForID(i);
                    var biome = grid[t].PrimaryBiome;
                    if (biome == null || !biome.canBuildBase) continue;
                    float step = grid.ApproxDistanceInTiles(chain.Last(), t);
                    if (step < 5f || step > 9f || chain.Any(o => grid.ApproxDistanceInTiles(o, t) < 5f)) continue;
                    float value = grid.ApproxDistanceInTiles(map.Tile, t) * (k < 4 ? 1f : -1f) + (biomes.Contains(biome.defName) ? 0f : 6f);
                    if (value > score) { score = value; best = t; }
                }
                if (!best.Valid) break;
                chain.Add(best);
                biomes.Add(grid[best].PrimaryBiome.defName);
            }
            var visits = (List<Visit>)AccessTools.Field(typeof(PhotoStore), "visits").GetValue(store);
            visits.Clear();
            int when = Find.TickManager.TicksAbs;
            for (int i = 0; i < chain.Count && i < photos.Count; i++)
                visits.Add(new Visit { tile = chain[i], biome = grid[chain[i]].PrimaryBiome.defName, ticksAbs = when + i * GenDate.TicksPerDay, photoId = photos[i].id, locationName = grid[chain[i]].PrimaryBiome.LabelCap });
            store.NotifyChanged();
            MomentFramesMod.Settings.journeyPhotos = MomentFramesMod.Settings.journeyLines = true;
            CameraJumper.TryShowWorld();
            var middle = chain.OrderBy(t => chain.Sum(o => grid.ApproxDistanceInTiles(t, o))).First();
            Find.WorldCameraDriver.JumpTo(middle);
            var camera = Traverse.Create(Find.WorldCameraDriver);
            camera.Field("altitude").SetValue(260f);
            camera.Field("desiredAltitude").SetValue(260f);
            checks.Add("INFO: steam time — world pins " + chain.Count + ", biomes " + biomes.Count);
        }

        private static void TimePastPhotos()
        {
            bool ko = LanguageDatabase.activeLanguage.folderName.StartsWith("Korean");
            string name = ko ? "산들 마을" : "Windmere";
            var store = PhotoStore.Instance;
            var folders = steamExtras.Split(';').Select(f => f.Trim()).ToArray();
            string from = folders[1];
            string to = Path.Combine(Path.GetDirectoryName(store.FolderPath), TimePastColony);
            Directory.CreateDirectory(to);
            File.WriteAllText(Path.Combine(to, RelicImport.IndexFile), "#MomentFrames\t" + name + "\t" + TimePastColony + "\n");
            var source = new RelicSource { gameId = TimePastColony, name = name, folder = to };
            var picks = new[]
            {
                ("0003_", ko ? "에이든, 세실: 함께 남긴 한 장" : "Aiden and Cecil, together"),
                ("0006_", ko ? "단체 사진: 3명" : "Group photo: 3 people"),
                ("0013_", ko ? "취임 의식: 솔로몬" : "Appointment ceremony: Solomon"),
                ("0015_", ko ? "처음 와 본 눈알 숲" : "First time in the eye forest"),
                ("0022_", ko ? "코코박의 습격을 막아낸 날" : "The cocobak attack, held off"),
                ("0025_", ko ? "마샤의 7번째 생일" : "Masha's 7th birthday"),
                ("0026_", ko ? "에이든, 민뭉: 함께 남긴 한 장" : "Aiden and Minmung, together"),
                ("0027_", ko ? "단체 사진: 7명" : "Group photo: 7 people"),
            };
            PhotoRecord first = null;
            foreach (var (prefix, title) in picks)
            {
                string file = Directory.GetFiles(from, prefix + "*.png").Select(Path.GetFileName).First(f => !f.EndsWith(".thumb.png"));
                string local = prefix + "photo.png";
                File.Copy(Path.Combine(from, file), Path.Combine(to, local), true);
                string slug = file.Split('_')[1];
                var date = slug.Split('-').Select(int.Parse).ToArray();
                string label = ko ? date[0] + "년 " + date[1] + "분기 " + date[2] + "일" : date[2] + " " + ((Quadrum)(date[1] - 1)).Label() + ", " + date[0];
                var made = store.ImportRelic(source, new RelicPhoto { file = local, title = title, kind = file.Split('_')[2], dateSlug = slug, dateLabel = label, year = date[0] });
                if (made == null) throw new InvalidOperationException("past colony photo not imported: " + prefix);
                first = first ?? made;
            }
            store.NotifyChanged();
            MainTabWindow_Album.Open(first);
            importAlbum = (MainTabWindow_Album)DefDatabase<MainButtonDef>.GetNamed("MF_Album").TabWindow;
            importAlbum.OpenImported(PhotoSource.Legacy);
        }

        private static bool SteamTime()
        {
            var store = PhotoStore.Instance;
            switch (steamStep)
            {
                case 0:
                    SteamTidy();
                    foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
                    ClearHostiles(map);
                    var s = MomentFramesMod.Settings;
                    s.autoCaptureEnabled = false; s.settlementPeriod = 0; s.buildingPhotos = false; s.brightPhotos = true;
                    s.fxFreeze = s.fxCamera = s.fxFlash = s.fxPopup = s.fxShutter = false;
                    map.fogGrid.ClearAllFog();
                    foreach (var left in map.listerThings.AllThings.Where(t => !(t is Pawn) && t.def.destroyable && (t.Faction == Faction.OfPlayer || t.def.category == ThingCategory.Item)).ToList())
                        left.Destroy(DestroyMode.Vanish);
                    foreach (var c in map.AllCells) if (c.GetTerrain(map).IsFloor) map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                    JumpToHour(12f);
                    Directory.CreateDirectory(Path.Combine(dir, "steam", "time"));
                    timeRegion = TimePickRegion();
                    TimeFloor(-24, -22, 51, 55, "Soil");
                    timeStage = 0; timeSnapshot = null; timeKept.Clear();
                    steamStep = 1;
                    return false;
                case 1:
                    if (timeSnapshot == null)
                    {
                        TimeBuild(timeStage);
                        if (timeStage > 0) Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + GenDate.TicksPerQuadrum);
                        timeLastStamp = SettlementChronicle.Stamp(map, Find.TickManager.TicksAbs);
                        if (!store.Settlement.Request(store, map, timeLastStamp, false)) throw new InvalidOperationException("survey refused at stage " + timeStage);
                        timeSnapshot = store.Settlement.Snapshots.Last().id;
                        Wait(.2f);
                        return false;
                    }
                    var snapshot = store.Settlement.Snapshots.First(x => x.id == timeSnapshot);
                    if (snapshot.failed) throw new InvalidOperationException("survey failed at stage " + timeStage);
                    if (!snapshot.complete) { Wait(.2f); return false; }
                    File.Copy(store.FilePath(store.Get(snapshot.photoIds[0])), Path.Combine(dir, TimePath("stage-" + timeStage + "-overview.png")), true);
                    File.Copy(store.FilePath(store.Get(snapshot.photoIds[timeRegion])), Path.Combine(dir, TimePath("stage-" + timeStage + "-region.png")), true);
                    timeSnapshot = null;
                    if (++timeStage >= TimeStages)
                    {
                        File.WriteAllText(Path.Combine(dir, TimePath("layout.txt")), "region " + timeRegion + "\nhome " + timeHome.x + " " + timeHome.z + "\nmap " + map.Size.x + " " + map.Size.z + "\n");
                        steamStep = 2;
                    }
                    return false;
                case 2:
                    SteamCleanScreen();
                    FeedbackRecordsOpen(map.uniqueID, timeLastStamp, false, false);
                    CloseLog(); Wait(2.5f); steamStep = 3; return false;
                case 3:
                    Shot(TimePath("records-period.png")); Wait(1f); steamStep = 4; return false;
                case 4:
                    FeedbackRecordsOpen(map.uniqueID, timeLastStamp, false, true, timeRegion);
                    Wait(2.5f); steamStep = 5; return false;
                case 5:
                    Shot(TimePath("records-history.png")); Wait(1f); steamStep = 6; return false;
                case 6:
                    var history = Traverse.Create(typeof(SettlementRecordsTab)).Field("history").GetValue<List<SettlementHistory.Entry>>();
                    if (history.Count != TimeStages) throw new InvalidOperationException("history shows " + history.Count + " periods");
                    timeSequence = new Dialog_SettlementSequence(store, history, map.Parent.LabelCap);
                    Find.WindowStack.Add(timeSequence);
                    timeFrame = 0; timeSequence.Select(0); Wait(2f); steamStep = 7; return false;
                case 7:
                    Shot(TimePath("sequence-" + timeFrame + ".png")); Wait(1f); steamStep = 8; return false;
                case 8:
                    if (++timeFrame < TimeStages) { timeSequence.Select(timeFrame); Wait(2f); steamStep = 7; return false; }
                    // The Steam page's "flip through" picture: the viewer's own Play, from the first quarter to the last, one shot per photo.
                    timeSequence.Select(0); timeFrame = 0; Wait(1.5f); steamStep = 13; return false;
                case 13:
                    if (timeFrame == 0) { timeSequence.TogglePlaying(Time.realtimeSinceStartup); Wait(.5f); timeFrame = 1; return false; }
                    Shot(TimePath("play-" + timeSequence.Index + (timeSequence.Playing ? "" : "-stopped") + ".png"));
                    timeShown = timeSequence.Index; Wait(.6f); steamStep = 14; return false;
                case 14:
                    if (timeSequence.Playing && timeSequence.Index == timeShown) { Wait(.1f); return false; }
                    if (timeSequence.Index != timeShown) { Wait(.6f); steamStep = 13; return false; }
                    Shot(TimePath("play-" + timeSequence.Index + "-stopped.png")); Wait(1f); steamStep = 15; return false;
                case 15:
                    timeSequence.Close();
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    TimeWorldVisits();
                    SteamCleanScreen(); Wait(5f); steamStep = 9; return false;
                case 9:
                    Shot(TimePath("world.png")); Wait(1f); steamStep = 10; return false;
                case 10:
                    CameraJumper.TryHideWorld();
                    TimePastPhotos();
                    SteamCleanScreen(); Wait(3f); steamStep = 11; return false;
                case 11:
                    Shot(TimePath("imports.png")); Wait(1f); steamStep = 12; return false;
                default:
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    return true;
            }
        }
    }
}
