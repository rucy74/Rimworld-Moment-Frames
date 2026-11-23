using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>Workshop showcase, after the screenshots (ProbeShots, same -momentFramesShots run): a gallery wall built in the quick-test colony
    /// with the author's photos, then stills of the newer features (the wall, places of memory, then and now, a birthday letter, the journey route,
    /// the album as a see-through window) and frame sequences for animated GIFs (the five frame looks, the projector's film, a group photo from the call to the card). Sequence frames go to gif-{name}/NNN.png with their real times in times.txt;
    /// the GIFs are put together outside the game. Staged data (photo places, journey stops) exists only in this throwaway profile.</summary>
    public static partial class Probe
    {
        private static int showPhase, burstIndex, showK;
        private static string burstName;
        private static float burstUntil;
        private static StringBuilder burstTimes;
        private static readonly List<Building_PhotoFrame> showFrames = new List<Building_PhotoFrame>();
        private static Building_PhotoFrame showStyleFrame;
        private static Building_PhotoProjector showProjector;
        private static int showRecordsBefore;
        private static PhotoRecord showCaptured;
        private static Letter showLetter;
        private static readonly FrameStyle[] ShowStyles = { FrameStyle.Material, FrameStyle.BlackMat, FrameStyle.Gilded, FrameStyle.Polaroid, FrameStyle.Canvas };
        private static readonly string[] ShowStills = { "show-wall.png", "show-album-window.png", "show-places.png", "show-thennow.png", "show-birthday.png", "show-journey.png",
            "show-memorynight.png", "show-yearletter.png", "show-life.png", "show-fallout.png", "show-aurora.png" };
        private static Lord showNight;
        private static GameCondition showCondition;

        private static PhotoRecord ShotRec(string prefix) => PhotoStore.Instance.Records.First(r => r.eventKey == "shots:" + prefix);

        private static void BurstStart(string name)
        {
            burstName = name;
            burstIndex = 0;
            burstTimes = new StringBuilder();
            Directory.CreateDirectory(Path.Combine(dir, "gif-" + name));
        }

        private static void BurstFrame()
        {
            Shot(Path.Combine("gif-" + burstName, burstIndex.ToString("000") + ".png"));
            burstTimes.Append(burstIndex).Append('\t').Append(Time.realtimeSinceStartup.ToString("0.000", CultureInfo.InvariantCulture)).Append('\n');
            burstIndex++;
        }

        private static void BurstEnd()
        {
            File.WriteAllText(Path.Combine(Path.Combine(dir, "gif-" + burstName), "times.txt"), burstTimes.ToString());
            checks.Add("INFO: showcase sequence gif-" + burstName + " — " + burstIndex + " frames");
        }

        private static void Teleport(Pawn p, IntVec3 c)
        {
            p.jobs?.StopAll();
            p.Position = c;
            p.Notify_Teleported(true, true);
            p.Drawer.tweener.ResetTweenedPosToRoot();
        }

        /// <summary>One step per call; true when the showcase is done.</summary>
        private static bool ShowcaseStep()
        {
            var store = PhotoStore.Instance;
            var s = MomentFramesMod.Settings;
            int wz = center.z + 8;   // the gallery wall's row
            switch (showPhase)
            {
                case 0:
                    {
                        Find.WindowStack.TryRemove(typeof(Dialog_PhotoEdit), false);
                        Find.MainTabsRoot.EscapeCurrentTab(false);
                        ShotsTidy();
                        // Everyone out of the way to the south, then a planked room front: a wooden wall with six frames, an easel and a collage board.
                        foreach (var p in map.mapPawns.FreeColonistsSpawned.ToList())
                            if (p.Position.z > center.z - 3) Teleport(p, CellFinder.RandomClosewalkCellNear(new IntVec3(center.x, 0, center.z - 9), map, 4));
                        var planks = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");
                        for (int x = center.x - 10; x <= center.x + 10; x++)
                        {
                            for (int z = center.z + 1; z < wz; z++) map.terrainGrid.SetTerrain(new IntVec3(x, 0, z), planks);
                            var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                            wall.SetFaction(Faction.OfPlayer);
                            GenSpawn.Spawn(wall, new IntVec3(x, 0, wz), map);
                        }
                        var plan = new[]
                        {
                            ("MF_PhotoFrame", -8, FrameStyle.Material, "0004_", ThingDefOf.WoodLog),
                            ("MF_PhotoFrameWide", -5, FrameStyle.Gilded, "0003_", ThingDefOf.Steel),
                            ("MF_PhotoFrame", -1, FrameStyle.Polaroid, "0007_", ThingDefOf.WoodLog),
                            ("MF_PhotoFrame", 1, FrameStyle.BlackMat, "0001_", ThingDefOf.WoodLog),
                            ("MF_PhotoFrameWide", 4, FrameStyle.Canvas, "0006_", ThingDefOf.WoodLog),
                            ("MF_PhotoFrame", 8, FrameStyle.Material, "0008_", ThingDefOf.Steel),
                        };
                        showFrames.Clear();
                        foreach (var (d, dx, st, ph, stuff) in plan)
                        {
                            var f = Spawn(d, stuff, new IntVec3(center.x + dx, 0, wz - 1), Rot4.North);
                            f.SetPhoto(ShotRec(ph).id);
                            f.SetStyle(st);
                            f.RefreshBinding();
                            showFrames.Add(f);
                        }
                        showStyleFrame = showFrames[3];
                        var easel = Spawn("MF_PhotoEasel", ThingDefOf.WoodLog, new IntVec3(center.x - 4, 0, center.z + 5), Rot4.North);
                        easel.SetPhoto(ShotRec("0009_5500-4-02_group").id);
                        easel.RefreshBinding();
                        var cdef = ThingDef.Named("MF_PhotoCollage");
                        var collage = (Building_PhotoCollage)ThingMaker.MakeThing(cdef, ThingDefOf.WoodLog);
                        collage.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(collage, new IntVec3(center.x + 3, 0, center.z + 5), map, Rot4.North);
                        string[] ids = { ShotRec("0002_").id, ShotRec("0004_").id, ShotRec("0009_5500-4-02_relicfound").id, ShotRec("0008_").id };
                        for (int i = 0; i < ids.Length; i++) collage.SetSlot(i, ids[i]);
                        collage.RefreshBinding();
                        // The projector off to the east, throwing its film onto a concrete pad.
                        var concrete = DefDatabase<TerrainDef>.GetNamed("Concrete");
                        for (int x = center.x + 13; x <= center.x + 17; x++)
                            for (int z = center.z - 7; z <= center.z - 2; z++) map.terrainGrid.SetTerrain(new IntVec3(x, 0, z), concrete);
                        showProjector = (Building_PhotoProjector)ThingMaker.MakeThing(ThingDef.Named("MF_Projector"), ThingDefOf.Steel);
                        showProjector.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(showProjector, new IntVec3(center.x + 15, 0, center.z - 6), map, Rot4.North);
                        Wait(1f);
                        showPhase = 1;
                        return false;
                    }
                case 1:
                    // The wall, rendered by the mod's own camera (the game's camera will not come closer than about 11 cells).
                    ShotsTidy();
                    Find.Selector.ClearSelection();
                    foreach (var a in map.mapPawns.AllPawnsSpawned.Where(x => !x.IsColonist && Mathf.Abs(x.Position.x - center.x) < 14 && x.Position.z > center.z - 2).ToList())
                        Teleport(a, CellFinder.RandomClosewalkCellNear(new IntVec3(center.x - 16, 0, center.z - 10), map, 4));   // a pet in the middle of the room
                    PhotoCapture.ProbePixelsPerCell = 150;
                    ShowCapture("show-wall.png", new Vector3(center.x + 0.5f, 0f, center.z + 7f), 6.4f, 16f / 9f);
                    showPhase = 2;
                    return false;
                case 2:
                    if (!ShowCaptured()) return false;
                    showK = 0;
                    Directory.CreateDirectory(Path.Combine(dir, "gif-styles"));
                    showPhase = 3;
                    return false;
                case 3:
                    // The five looks on one frame, one render each.
                    showStyleFrame.SetStyle(ShowStyles[showK]);
                    Wait(0.3f);
                    showPhase = 4;
                    return false;
                case 4:
                    PhotoCapture.ProbePixelsPerCell = 260;
                    ShowCapture(Path.Combine("gif-styles", showK.ToString("000") + ".png"), new Vector3(showStyleFrame.Position.x + 0.5f, 0f, wz + 0.45f), 1.9f, 1.5f);
                    showPhase = 5;
                    return false;
                case 5:
                    if (!ShowCaptured()) return false;
                    if (++showK < ShowStyles.Length) { showPhase = 3; return false; }
                    showStyleFrame.SetStyle(FrameStyle.BlackMat);
                    showK = 0;
                    Directory.CreateDirectory(Path.Combine(dir, "gif-projector"));
                    showPhase = 9;
                    return false;
                case 9:
                    // The projector's film, one slide after another (its clock only runs unpaused, so it is set by hand here).
                    Building_PhotoProjector.ResetClock();
                    Building_PhotoProjector.Advance(showK * Building_PhotoProjector.SecondsPerPhoto + 0.5f);
                    Wait(0.3f);
                    showPhase = 10;
                    return false;
                case 10:
                    PhotoCapture.ProbePixelsPerCell = 150;
                    ShowCapture(Path.Combine("gif-projector", showK.ToString("000") + ".png"), new Vector3(showProjector.Position.x + 0.5f, 0f, showProjector.Position.z + 2.1f), 4.4f, 1.5f);
                    showPhase = 11;
                    return false;
                case 11:
                    if (!ShowCaptured()) return false;
                    if (++showK < 8) { showPhase = 9; return false; }
                    PhotoCapture.ProbePixelsPerCell = 0;
                    // The album as a window of its own over the colony, see-through.
                    Find.CameraDriver.SetRootPosAndSize(new Vector3(center.x + 0.5f, 0f, center.z + 3f), 13f);
                    AlbumOpen(false, false);
                    s.albumOpacity = 0.55f;
                    ((MainTabWindow_Album)AlbumDef.TabWindow).windowRect = new Rect(250f, 60f, 1000f, 610f);
                    Wait(1.5f);
                    showPhase = 12;
                    return false;
                case 12:
                    CloseLog();
                    Shot("show-album-window.png");
                    Wait(1f);
                    showPhase = 13;
                    return false;
                case 13:
                    {
                        var w = (MainTabWindow_Album)AlbumDef.TabWindow;
                        Find.MainTabsRoot.EscapeCurrentTab(false);
                        HarmonyLib.Traverse.Create(w).Method("ResetPlacement").GetValue();
                        s.albumOpacity = 1f;
                        // Places of memory: each photo pinned where it was taken (staged spots on this map; three photos share the photo spot).
                        var spots = new Dictionary<string, IntVec2>
                        {
                            { "0001_", new IntVec2(-14, 3) }, { "0010_", new IntVec2(-14, 3) }, { "0002_", new IntVec2(-15, -8) },
                            { "0003_", new IntVec2(0, -4) }, { "0011_", new IntVec2(0, -4) }, { "0009_5500-4-02_group", new IntVec2(0, -4) },
                            { "0004_", new IntVec2(13, 9) }, { "0006_", new IntVec2(3, 4) }, { "0007_", new IntVec2(17, -9) },
                            { "0008_", new IntVec2(-6, 11) }, { "0009_5500-4-02_relicfound", new IntVec2(9, -1) },
                        };
                        foreach (var kv in spots) ShotRec(kv.Key).center = new Vector3(center.x + kv.Value.x + 0.5f, 0f, center.z + kv.Value.z + 0.5f);
                        store.NotifyChanged();
                        Places.Show = true;
                        ShotsTidy();
                        Find.CameraDriver.SetRootPosAndSize(new Vector3(center.x + 1f, 0f, center.z + 1f), 30f);
                        Wait(1.5f);
                        showPhase = 14;
                        return false;
                    }
                case 14:
                    CloseLog();
                    Shot("show-places.png");
                    Wait(1f);
                    showPhase = 15;
                    return false;
                case 15:
                    Find.WindowStack.Add(new Dialog_ThenNow(ShotRec("0001_"), ShotRec("0010_")));
                    Wait(1.5f);
                    showPhase = 16;
                    return false;
                case 16:
                    CloseLog();
                    Shot("show-thennow.png");
                    Wait(1f);
                    showPhase = 17;
                    return false;
                case 17:
                    {
                        Find.WindowStack.TryRemove(typeof(Dialog_ThenNow), false);
                        Places.Show = false;
                        ShotsTidy();
                        var who = map.mapPawns.FreeColonistsSpawned.Where(p => p.ageTracker.AgeBiologicalYears >= 20)
                            .OrderBy(p => p.gender == Gender.Female ? 0 : 1).ThenBy(p => p.ageTracker.AgeBiologicalYears).FirstOrDefault() ?? map.mapPawns.FreeColonistsSpawned.First();
                        int before = Find.LetterStack.LettersListForReading.Count;
                        Birthday.SendLetter(who, who.ageTracker.AgeBiologicalYears);
                        if (Find.LetterStack.LettersListForReading.Count == before) throw new InvalidOperationException("showcase: no birthday letter");
                        showLetter = Find.LetterStack.LettersListForReading.Last();
                        showLetter.OpenLetter();
                        Find.CameraDriver.SetRootPosAndSize(who.DrawPos, 12f);
                        Wait(1.5f);
                        showPhase = 18;
                        return false;
                    }
                case 18:
                    CloseLog();
                    Shot("show-birthday.png");
                    Wait(1f);
                    showPhase = 19;
                    return false;
                case 19:
                    {
                        foreach (var w in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) Find.WindowStack.TryRemove(w, false);
                        if (showLetter != null && Find.LetterStack.LettersListForReading.Contains(showLetter)) Find.LetterStack.RemoveLetter(showLetter);
                        ShotsTidy();
                        // The journey: home and three places a few days' travel away, each with a photo (staged for the picture).
                        var grid = Find.WorldGrid;
                        PlanetTile home = map.Tile;
                        if (!store.Visited(home)) store.AddVisit(new Visit { tile = home, biome = map.Biome?.defName, ticksAbs = Find.TickManager.TicksAbs - 3 * GenDate.TicksPerDay, photoId = ShotRec("0003_").id });
                        var surface = grid.Surface;
                        var stops = new List<PlanetTile>();
                        string[] photos = { "0007_", "0006_", "0004_" };
                        float[] want = { 5f, 10f, 15f };
                        foreach (float d in want)
                        {
                            PlanetTile best = PlanetTile.Invalid;
                            float bestErr = float.MaxValue;
                            for (int i = 0; i < surface.TilesCount; i++)
                            {
                                var t = surface.PlanetTileForID(i);
                                if (Find.World.Impassable(t) || grid[t].PrimaryBiome == null || !grid[t].PrimaryBiome.canBuildBase) continue;
                                float dist = grid.ApproxDistanceInTiles(stops.Count == 0 ? home : stops[stops.Count - 1], t);
                                float fromHome = grid.ApproxDistanceInTiles(home, t);
                                float err = Mathf.Abs(dist - 5f) + Mathf.Abs(fromHome - d);
                                if (err < bestErr && !stops.Contains(t) && t != home) { bestErr = err; best = t; }
                            }
                            if (best.Valid) stops.Add(best);
                        }
                        for (int i = 0; i < stops.Count; i++)
                            store.AddVisit(new Visit { tile = stops[i], biome = grid[stops[i]].PrimaryBiome?.defName, ticksAbs = Find.TickManager.TicksAbs - (2 - i) * GenDate.TicksPerDay, photoId = ShotRec(photos[i]).id });
                        store.NotifyChanged();
                        CameraJumper.TryJump(stops.Count > 1 ? stops[1] : home);
                        Wait(2f);
                        showPhase = 20;
                        return false;
                    }
                case 20:
                    CloseLog();
                    Messages.Clear();
                    Shot("show-journey.png");
                    Wait(1f);
                    showPhase = 21;
                    return false;
                case 21:
                    {
                        CameraJumper.TryHideWorld();
                        // A group photo from the call to the card: time-lapse of the line-up, then the flash and the card in real time.
                        foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
                        ShotsTidy();
                        s.fxFlash = s.fxShutter = s.fxPopup = true;
                        s.fxFreeze = s.fxCamera = false;
                        var spot = photoSpot.Position;
                        int k = 0;
                        foreach (var p in map.mapPawns.FreeColonistsSpawned.ToList())
                        {
                            if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                            if (p.needs?.food != null) p.needs.food.CurLevel = 1f;
                            if (!p.Awake()) RestUtility.WakeUp(p);
                            if (p.Drafted) p.drafter.Drafted = false;
                            float a = k++ * 0.7f;
                            var c = new IntVec3(spot.x + Mathf.RoundToInt(Mathf.Cos(a) * 8f), 0, spot.z - 2 + Mathf.RoundToInt(Mathf.Sin(a) * 4f));
                            Teleport(p, CellFinder.RandomClosewalkCellNear(c, map, 2));
                        }
                        GroupPhoto.ForgetEligible();
                        var report = GroupPhoto.CanStart(map, spot, out var pawns);
                        if (!report.Accepted) throw new InvalidOperationException("showcase: group photo cannot start: " + report.Reason);
                        if (GroupPhoto.Start(map, spot, pawns) == null) throw new InvalidOperationException("showcase: group photo did not start");
                        Find.Selector.ClearSelection();
                        Find.CameraDriver.SetRootPosAndSize(spot.ToVector3Shifted() + new Vector3(0f, 0f, 1.5f), 11f);
                        showRecordsBefore = store.Records.Count;
                        showCaptured = null;
                        BurstStart("group");
                        Wait(1f);
                        showPhase = 22;
                        return false;
                    }
                case 22:
                    CloseLog();
                    Messages.Clear();
                    if (showCaptured == null && store.Records.Count > showRecordsBefore)
                    {
                        showCaptured = store.Records[store.Records.Count - 1];
                        burstUntil = Time.realtimeSinceStartup + 3.4f;
                    }
                    if (showCaptured == null)
                    {
                        if (burstIndex > 260) throw new InvalidOperationException("showcase: no group photo after " + burstIndex + " frames");
                        for (int i = 0; i < 6; i++) Find.TickManager.DoSingleTick();
                        SettleDraw();
                        BurstFrame();
                        return false;
                    }
                    if (Time.realtimeSinceStartup < burstUntil)
                    {
                        BurstFrame();
                        return false;
                    }
                    BurstEnd();
                    checks.Add("INFO: showcase group photo '" + showCaptured.title + "' (" + showCaptured.people.Count + " people)");
                    GroupPhoto.ActiveJob(map, photoSpot.Position)?.Cancel();
                    showPhase = 30;
                    Wait(1f);
                    return false;
                case 30:
                    {
                        // Memory night (2026-10-02): everyone gathers at the projector and watches the film. A clean slate first: the group
                        // photo's lord let go, everyone awake and fed, old jobs ended so the night's duty seats them.
                        foreach (var l in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(l);
                        ShotsTidy();
                        PrepareNightScene(showProjector);
                        JumpToHour(21.5f);
                        foreach (var p in map.mapPawns.FreeColonistsSpawned.ToList())
                        {
                            if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                            if (p.needs?.food != null) p.needs.food.CurLevel = 1f;
                            if (!p.Awake()) RestUtility.WakeUp(p);
                            if (p.Drafted) p.drafter.Drafted = false;
                            if (p.InMentalState) p.MentalState.RecoverFromState();
                            p.jobs?.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
                        }
                        showProjector.SetFilm("", "", "");
                        string why = MemoryNight.CannotStart(showProjector);
                        if (why != null) throw new InvalidOperationException("showcase: memory night cannot start: " + why);
                        if (!MemoryNight.Start(showProjector)) throw new InvalidOperationException("showcase: memory night did not start");
                        showNight = map.lordManager.lords.LastOrDefault(l => l.LordJob is LordJob_MFMemoryNight);
                        for (int i = 0; i < 1800; i++) Find.TickManager.DoSingleTick();
                        SettleDraw();
                        Building_PhotoProjector.ResetClock();
                        Building_PhotoProjector.Advance(2 * Building_PhotoProjector.SecondsPerPhoto + 0.5f);
                        ShotsTidy();
                        Find.Selector.ClearSelection();
                        Wait(0.5f);
                        showPhase = 31;
                        return false;
                    }
                case 31:
                    {
                        PhotoCapture.ProbePixelsPerCell = 150;
                        Vector3 screen = MemoryNight.ScreenCenter(showProjector);
                        ShowCapture("show-memorynight.png", screen + new Vector3(0f, 0f, -2.6f), 7.6f, 16f / 9f, true);
                        showPhase = 32;
                        return false;
                    }
                case 32:
                    {
                        if (!ShowCaptured()) return false;
                        PhotoCapture.ProbePixelsPerCell = 0;
                        checks.Add("INFO: showcase memory night: " + (showNight?.ownedPawns.Count ?? 0) + " watching");
                        if (showNight != null && map.lordManager.lords.Contains(showNight)) map.lordManager.RemoveLord(showNight);
                        ShotsTidy();
                        // The year in photos: the letter for the year most of the photos are from.
                        int year = store.Records.Where(r => r.year != 0).GroupBy(r => r.year).OrderByDescending(g => g.Count()).First().Key;
                        s.yearLetter = true;
                        var letter = YearInPhotos.Check(store, year + 1);
                        if (letter == null) throw new InvalidOperationException("showcase: no year letter for " + year);
                        showLetter = letter;
                        letter.OpenLetter();
                        Find.CameraDriver.SetRootPosAndSize(showProjector.DrawPos, 12f);
                        Wait(1.5f);
                        showPhase = 33;
                        return false;
                    }
                case 33:
                    CloseLog();
                    Messages.Clear();
                    Shot("show-yearletter.png");
                    Wait(1f);
                    showPhase = 34;
                    return false;
                case 34:
                    {
                        foreach (var w in Find.WindowStack.Windows.OfType<Dialog_MFYearPhotos>().ToList()) w.Close(false);
                        foreach (var w in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) Find.WindowStack.TryRemove(w, false);
                        if (showLetter != null && Find.LetterStack.LettersListForReading.Contains(showLetter)) Find.LetterStack.RemoveLetter(showLetter);
                        ShotsTidy();
                        // A life in the chronicle tab: moments that make no photo (joining, falling in love, a wedding, a bond), between
                        // her photos (staged dates, the lines are the mod's own).
                        var celeste = ShotPawn("Celeste") ?? map.mapPawns.FreeColonistsSpawned.First();
                        var julian = ShotPawn("Julian") ?? map.mapPawns.FreeColonistsSpawned.First(x => x != celeste);
                        string cid = celeste.GetUniqueLoadID();
                        var times = store.Records.Where(r => r.HasFile && r.Has(celeste)).Select(r => r.ticksAbs).OrderBy(t => t).ToList();
                        if (times.Count < 4) throw new InvalidOperationException("showcase: Celeste is in " + times.Count + " photos");
                        var pet = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.RaceProps.Animal && x.Faction == Faction.OfPlayer);
                        var plan = new (string kind, int at, bool withJulian, string other)[]
                        {
                            (LifeEvents.Joined, times[0] - 3 * GenDate.TicksPerDay, false, null),
                            (LifeEvents.Lover, (times[0] + times[1]) / 2, true, null),
                            (LifeEvents.Bond, (times[1] + times[2]) / 2, false, pet?.LabelShort ?? "Biscuit"),
                            (LifeEvents.Fiance, (times[2] + times[3]) / 2, true, null),
                            (LifeEvents.Spouse, times[3] + GenDate.TicksPerDay, true, null),
                        };
                        foreach (var (kind, at, withJulian, other) in plan)
                        {
                            var e = new LifeEvent { kind = kind, other = other, ticksAbs = at, dateLabel = PhotoStore.DateLabel(map, at), year = PhotoStore.Year(map, at) };
                            e.pawnIds.Add(cid);
                            e.names.Add(celeste.LabelShort);
                            if (withJulian)
                            {
                                e.pawnIds.Add(julian.GetUniqueLoadID());
                                e.names.Add(julian.LabelShort);
                            }
                            store.AddLife(e);
                        }
                        Find.Selector.ClearSelection();
                        Find.Selector.Select(celeste, false);
                        InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Chronicle));
                        Find.CameraDriver.SetRootPosAndSize(celeste.DrawPos, 14f);
                        Wait(1.5f);
                        showPhase = 35;
                        return false;
                    }
                case 35:
                    CloseLog();
                    Messages.Clear();
                    Shot("show-life.png");
                    Wait(1f);
                    showPhase = 36;
                    return false;
                case 36:
                    {
                        Find.Selector.ClearSelection();
                        ShotsTidy();
                        // Disasters and the sky are photographed in their own light: toxic fallout at midday, then an aurora at night, over
                        // the gallery wall with everyone standing about in front of it (shots9: the photo spot's ground alone was empty).
                        var cells = new List<IntVec3>();
                        for (int x = -7; x <= 7; x += 2)
                            for (int z = 1; z <= 5; z += 2) cells.Add(new IntVec3(center.x + x + (z / 2 % 2), 0, center.z + z));
                        var people = map.mapPawns.FreeColonistsSpawned.ToList();
                        for (int i = 0; i < people.Count; i++)
                        {
                            var c = cells[(i * 7) % cells.Count];
                            Teleport(people[i], c.Standable(map) ? c : CellFinder.RandomClosewalkCellNear(c, map, 2));
                            people[i].jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 20000), Verse.AI.JobCondition.InterruptForced);
                        }
                        showCondition = GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed("ToxicFallout"), GenDate.TicksPerDay * 3);
                        map.gameConditionManager.RegisterCondition(showCondition);
                        JumpToHour(13f);
                        for (int i = 0; i < 300; i++) Find.TickManager.DoSingleTick();   // the condition's own tick sets its colours
                        SettleDraw();
                        Find.CameraDriver.SetRootPosAndSize(new Vector3(center.x + 0.5f, 0f, center.z + 4.5f), 16f);
                        Wait(1.5f);
                        showPhase = 37;
                        return false;
                    }
                case 37:
                    PhotoCapture.ProbePixelsPerCell = 90;
                    ShowCapture("show-fallout.png", new Vector3(center.x + 0.5f, 0f, center.z + 4.5f), 11f, 16f / 9f, true);
                    showPhase = 38;
                    return false;
                case 38:
                    {
                        if (!ShowCaptured()) return false;
                        showCondition.End();
                        showCondition = GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed("Aurora"), GenDate.TicksPerDay * 3);
                        map.gameConditionManager.RegisterCondition(showCondition);
                        JumpToHour(22.5f);
                        for (int i = 0; i < 600; i++) Find.TickManager.DoSingleTick();   // an aurora picks its colours as it ticks (shots9: none, a plain night)
                        SettleDraw();
                        Wait(1.5f);
                        showPhase = 39;
                        return false;
                    }
                case 39:
                    ShowCapture("show-aurora.png", new Vector3(center.x + 0.5f, 0f, center.z + 4.5f), 11f, 16f / 9f, true);
                    showPhase = 40;
                    return false;
                case 40:
                    if (!ShowCaptured()) return false;
                    PhotoCapture.ProbePixelsPerCell = 0;
                    checks.Add("INFO: showcase sky: fallout then aurora at " + GenLocalDate.HourFloat(map).ToString("0.0") + "h, glow " + map.skyManager.CurSkyGlow.ToString("0.00"));
                    showCondition.End();
                    showPhase = 41;
                    Wait(1f);
                    return false;
                default:
                    {
                        string html = AlbumExport.Write(store);
                        checks.Add("INFO: showcase album.html " + html);
                        foreach (var n in ShowStills) Require(n);
                        foreach (var g in new[] { "gif-styles", "gif-projector" })
                            checks.Add("INFO: showcase " + g + " — " + Directory.GetFiles(Path.Combine(dir, g), "*.png").Length + " renders");
                        return true;
                    }
            }
        }

        /// <summary>Jump the clock forward (no ticks run) to the given local hour, at least six hours on so a condition just registered has
        /// fully faded in.</summary>
        private static void JumpToHour(float hour)
        {
            float now = GenLocalDate.HourFloat(map);
            float ahead = (hour - now + 24f) % 24f;
            if (ahead < 6f) ahead += 24f;
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + Mathf.RoundToInt(ahead * GenDate.TicksPerHour));
        }

        private static bool showCaptureDone;
        private static float showCaptureDeadline;
        private static Action showCaptureStart;   // a render waiting for the mod's own photo in flight (memory night takes one as it begins)

        /// <summary>A render by the mod's own camera (any span, no UI), written to dir/rel when it arrives; ShowCaptured() says when. If the
        /// mod is still taking a photo of its own, the render starts once that one is done (1.6 offscreen run shots22, 2026-10-20: the memory
        /// night's own photo was still pending and Capture refused).</summary>
        private static void ShowCapture(string rel, Vector3 at, float span, float aspect, bool realLight = false)
        {
            showCaptureDone = false;
            showCaptureDeadline = Time.realtimeSinceStartup + 20f;
            string path = Path.Combine(dir, rel);
            showCaptureStart = () =>
            {
                if (!PhotoCapture.Capture(map, at, span, r =>
                    {
                        if (r?.png != null) File.WriteAllBytes(path, r.png);
                        showCaptureDone = true;
                    }, aspect, realLight))
                    throw new InvalidOperationException("showcase: capture could not start for " + rel + " (busy " + PhotoCapture.Busy
                        + ", current map " + (map == Find.CurrentMap) + ", camera " + (Find.Camera != null) + ")");
            };
            if (!PhotoCapture.Busy) StartShowCapture();
        }

        private static void StartShowCapture()
        {
            var start = showCaptureStart;
            showCaptureStart = null;
            start();
        }

        private static bool ShowCaptured()
        {
            if (showCaptureDone) return true;
            if (Time.realtimeSinceStartup > showCaptureDeadline) throw new InvalidOperationException("showcase: a capture never came back");
            if (showCaptureStart != null && !PhotoCapture.Busy) StartShowCapture();
            return false;
        }
    }
}
