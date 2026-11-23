using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>What the mod itself costs while the game runs (2026-11-05, -momentFramesTickCost): every tick, frame and Harmony hook of the mod gets a timer
    /// on the main thread while a large colony plays. Time inside the mod only: not its GPU cost, worker threads, or what other mods do around it.</summary>
    public static partial class Probe
    {
        private static bool tickCostMode;
        private sealed class CostRow { public long time, calls, worst; }
        private static readonly Dictionary<MethodBase, CostRow> costRows = new Dictionary<MethodBase, CostRow>();
        private static readonly HashSet<MethodBase> costTargets = new HashSet<MethodBase>(), costSeen = new HashSet<MethodBase>();
        private static readonly List<string> costResults = new List<string>(), costFailed = new List<string>(), costIds = new List<string>();
        private static readonly HashSet<string> CostNames = new HashSet<string> {
            "Tick", "TickRare", "TickLong", "TickInterval", "MapComponentTick", "MapComponentUpdate", "MapComponentOnGUI", "GameComponentTick", "GameComponentUpdate", "GameComponentOnGUI",
            "LordToilTick", "LordJobTick", "UpdateAllDuties", "DrawAt", "DynamicDrawPhaseAt", "Print", "DrawExtraSelectionOverlays", "DrawGUIOverlay", "WindowUpdate", "DoWindowContents", "ExtraOnGUI",
            "TryGiveJob", "GetChance", "ExposeData" };
        private static int costMainThread, costDepth, costStartTick, costGoal, costGen0, costRecords;
        private static long costTopTick, costTopFrame, costCallsTick, costCallsFrame, costThisFrame, costWorstFrame, costTickTime, costTickCount, costFrames, costProbeSelf, costStart, costLimit;
        private static bool costInTick;
        private static double costOverheadNs;
        private static string costLabel;
        private static TimeSpeed costSpeed;
        private static Building_PhotoProjector costProjector;
        private static Vector3 costDisplayView;
        private static double CostMs(long time) => time * 1000.0 / Stopwatch.Frequency;
        private static string CostName(MethodBase m) => m.DeclaringType?.Name + "." + m.Name;

        private static void CostPrefix(out long __state)
        {
            __state = 0;
            if (Thread.CurrentThread.ManagedThreadId != costMainThread) return;
            costDepth++;
            __state = Stopwatch.GetTimestamp();
        }
        private static void CostPostfix(MethodBase __originalMethod, long __state)
        {
            if (__state == 0) return;
            long spent = Stopwatch.GetTimestamp() - __state;
            costDepth--;
            if (!costRows.TryGetValue(__originalMethod, out var row)) costRows[__originalMethod] = row = new CostRow();
            row.time += spent; row.calls++; if (spent > row.worst) row.worst = spent;
            if (costInTick) costCallsTick++; else costCallsFrame++;
            if (costDepth != 0) return;
            if (costInTick) costTopTick += spent; else costTopFrame += spent;
            costThisFrame += spent;
        }
        private static void CostTickPrefix(out long __state) { __state = Stopwatch.GetTimestamp(); costInTick = true; costDepth = 0; }
        private static void CostTickPostfix(long __state) { costTickTime += Stopwatch.GetTimestamp() - __state; costTickCount++; costInTick = false; }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void CostNothing() { }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CostSpin() { long end = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000; while (Stopwatch.GetTimestamp() < end) { } }

        private static void CostInstrument()
        {
            costMainThread = Thread.CurrentThread.ManagedThreadId;
            var harmony = new Harmony("momentframes.probe.tickcost");
            var mine = typeof(Probe).Assembly;
            bool IsTest(Type t) => t == typeof(Probe) || t?.DeclaringType == typeof(Probe);
            foreach (var original in Harmony.GetAllPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(original);
                if (info == null) continue;
                foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                    if (patch.PatchMethod.DeclaringType?.Assembly == mine && !IsTest(patch.PatchMethod.DeclaringType)) costTargets.Add(patch.PatchMethod);
            }
            foreach (var type in AccessTools.GetTypesFromAssembly(mine))
            {
                if (IsTest(type) || type.IsGenericTypeDefinition || type.IsInterface) continue;
                foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (CostNames.Contains(m.Name) && !m.IsAbstract && !m.ContainsGenericParameters
                        && (m.Name != "ExposeData" || typeof(GameComponent).IsAssignableFrom(type) || typeof(MapComponent).IsAssignableFrom(type)))
                        costTargets.Add(m);
            }
            costTargets.Add(AccessTools.Method(typeof(ExternalPhotoImport), "ObserveCurrent"));
            costTargets.Add(AccessTools.Method(typeof(ExternalPhotoImport), "Protect"));
            costTargets.Add(AccessTools.Method(typeof(RelicImport), "WriteIndex"));
            // What the per-frame image pipeline does on the game thread (2026-11-23: JPG originals cost more here on 1.7 than PNG ones).
            foreach (var m in new[] {
                AccessTools.Method(typeof(FramePhotoTextures), "Update"), AccessTools.Method(typeof(FramePhotoTextures), "UploadOtherPng"),
                AccessTools.Method(typeof(PhotoThumbnailLoader), "Update"), AccessTools.Method(typeof(PhotoPreviews), "Update"),
                AccessTools.Method(typeof(PhotoPreviews), "DisplayPath"), AccessTools.Method(typeof(PhotoPreviewFallback), "Make"),
                AccessTools.Method(typeof(PhotoTextureCache), "CompleteThumb"), AccessTools.Method(typeof(PhotoTextureCache), "Upload"),
                AccessTools.Method(typeof(PhotoTextureCache), "Decode", new[] { typeof(byte[]), typeof(string) }),
                AccessTools.Method(typeof(PhotoTextureCache), "Thumb", new[] { typeof(string), typeof(string) }),
                AccessTools.Method(typeof(ExternalPhotoFiles), "HashFile", new[] { typeof(string) }) })
                if (m != null) costTargets.Add(m);
            var prefix = new HarmonyMethod(typeof(Probe), nameof(CostPrefix));
            var postfix = new HarmonyMethod(typeof(Probe), nameof(CostPostfix));
            foreach (var target in costTargets)
                try { harmony.Patch(target, prefix, postfix); }
                catch (Exception e) { costFailed.Add(CostName(target) + ": " + e.GetType().Name); }
            harmony.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)),
                new HarmonyMethod(typeof(Probe), nameof(CostTickPrefix)), new HarmonyMethod(typeof(Probe), nameof(CostTickPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Probe), nameof(CostNothing)), prefix, postfix);
            harmony.Patch(AccessTools.Method(typeof(Probe), nameof(CostSpin)), prefix, postfix);
            checks.Add("INFO: timed " + costTargets.Count + " hooks of the mod; could not time " + costFailed.Count + ": " + string.Join(", ", costFailed));
        }

        // The timers themselves: what one timed call adds, and whether a known 1 ms reads as 1 ms.
        private static void CostCalibrate()
        {
            const int Calls = 200000;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < Calls; i++) CostNothing();
            clock.Stop();
            costOverheadNs = clock.Elapsed.TotalMilliseconds * 1000000.0 / Calls;
            var nothing = AccessTools.Method(typeof(Probe), nameof(CostNothing));
            CostControl(costRows.TryGetValue(nothing, out var row) && row.calls == Calls, "control: the timer sees every call of a small method");
            for (int i = 0; i < 20; i++) CostSpin();
            var spin = AccessTools.Method(typeof(Probe), nameof(CostSpin));
            double spun = costRows.TryGetValue(spin, out var spinRow) ? CostMs(spinRow.time) : 0;
            CostControl(spun > 18 && spun < 40, "control: 20 calls of a 1 ms spin read as " + spun.ToString("0.0") + " ms");
            checks.Add("INFO: one timed call adds about " + costOverheadNs.ToString("0") + " ns; that is subtracted below");
        }
        private static LordJob_MFMemoryNight costNight;
        private static void CostNightState(string when) => checks.Add("INFO: night " + when + ": lord alive " + map.lordManager.lords.Contains(costNight.lord) + ", toil " + costNight.lord?.CurLordToil?.GetType().Name
            + ", watching " + costNight.Watching + ", cancelled " + costNight.Cancelled + ", film photos " + costNight.HasFilmPhotos + ", arrived " + costNight.ArrivedCount + "/" + costNight.Expected.Count + ", music " + MemoryNightMusic.State + " (" + MemoryNightMusic.LastReason + "), speed " + Find.TickManager.CurTimeSpeed
            + ", danger " + map.dangerWatcher.DangerRating + ", conditions fine " + GatheringsUtility.AcceptableGameConditionsToContinueGathering(map) + ", organizer fine " + GatheringsUtility.PawnCanStartOrContinueGathering(Traverse.Create(costNight).Field("organizer").GetValue<Pawn>()) + ", in the lord " + (costNight.lord?.ownedPawns.Count ?? 0) + ", called off now " + (bool)AccessTools.Method(typeof(LordJob_MFMemoryNight), "ShouldBeCalledOff").Invoke(costNight, null));
        private static void CostControl(bool ok, string label) => checks.Add((ok ? "PASS: " : "FAIL: ") + label);

        private static void CostTopUp()
        {
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.needs?.food != null) p.needs.food.CurLevel = .95f;
                if (p.needs?.rest != null) p.needs.rest.CurLevel = .95f;
                if (p.Drafted) p.drafter.Drafted = false;
            }
        }

        private static void CostBegin(string label, int ticks, TimeSpeed speed, float realLimit)
        {
            CloseLog();
            costLabel = label; costGoal = ticks; costSpeed = speed;
            costRows.Clear();
            costTopTick = costTopFrame = costCallsTick = costCallsFrame = costThisFrame = costWorstFrame = costTickTime = costTickCount = costFrames = costProbeSelf = 0;
            costGen0 = GC.CollectionCount(0);
            costRecords = PhotoStore.Instance.Records.Count;
            costStartTick = Find.TickManager.TicksGame;
            costLimit = (long)(realLimit * Stopwatch.Frequency);
            costStart = Stopwatch.GetTimestamp();
            Find.TickManager.CurTimeSpeed = speed;
        }
        private static bool CostRunning()
        {
            costFrames++;
            if (costThisFrame > costWorstFrame) costWorstFrame = costThisFrame;
            costThisFrame = 0;
            if ((costGoal <= 0 || Find.TickManager.TicksGame - costStartTick < costGoal) && Stopwatch.GetTimestamp() - costStart < costLimit)
            {
                Find.TickManager.CurTimeSpeed = costSpeed;
                return true;
            }
            CostEnd();
            return false;
        }
        private static long CostCalls(string name) => costRows.Where(r => CostName(r.Key) == name).Sum(r => r.Value.calls);

        private static void CostEnd()
        {
            double real = CostMs(Stopwatch.GetTimestamp() - costStart);
            int ticks = Find.TickManager.TicksGame - costStartTick;
            foreach (var key in costRows.Keys) costSeen.Add(key);
            // The test's own step runs inside the mod's frame update, so it comes off that figure.
            double probeMs = CostMs(costProbeSelf);
            double tickMs = Math.Max(0, CostMs(costTopTick) - costCallsTick * costOverheadNs / 1000000.0);
            double frameMs = Math.Max(0, CostMs(costTopFrame) - probeMs - costCallsFrame * costOverheadNs / 1000000.0);
            double perTickUs = costTickCount > 0 ? tickMs * 1000.0 / costTickCount : 0;
            double perFrameUs = costFrames > 0 ? frameMs * 1000.0 / costFrames : 0;
            double worldTick = costTickCount > 0 ? CostMs(costTickTime) / costTickCount : 0;
            var top = costRows.Select(r =>
            {
                string name = CostName(r.Key);
                double ms = CostMs(r.Value.time) - r.Value.calls * costOverheadNs / 1000000.0;
                if (name == "PhotoStore.GameComponentUpdate") ms -= probeMs;
                return new { name, r.Value.calls, ms = Math.Max(0, ms), worst = CostMs(r.Value.worst) };
            }).OrderByDescending(r => r.ms).Take(10).ToList();
            var text = new StringBuilder();
            text.AppendLine("== " + costLabel);
            text.AppendLine("ticks " + costTickCount + " (" + ticks + ")  frames " + costFrames + "  real " + (real / 1000).ToString("0.0") + " s  tps " + (costTickCount * 1000.0 / real).ToString("0") + "  fps " + (costFrames * 1000.0 / real).ToString("0"));
            text.AppendLine("whole game tick " + worldTick.ToString("0.000") + " ms | mod in a tick " + perTickUs.ToString("0.0") + " us (" + (worldTick > 0 ? perTickUs / 10.0 / worldTick : 0).ToString("0.00") + "% of the tick, "
                + (perTickUs / 166.67).ToString("0.00") + "% of a 60 tps budget) | mod in a frame " + perFrameUs.ToString("0.0") + " us | mod total " + (tickMs + frameMs).ToString("0.0") + " ms = " + ((tickMs + frameMs) * 100 / real).ToString("0.00") + "% of real time");
            text.AppendLine("worst frame in the mod " + CostMs(costWorstFrame).ToString("0.0") + " ms | gen0 collections " + (GC.CollectionCount(0) - costGen0) + " | photos " + costRecords + " -> " + PhotoStore.Instance.Records.Count);
            foreach (var r in top) text.AppendLine("  " + r.ms.ToString("0.00").PadLeft(9) + " ms  " + r.calls.ToString().PadLeft(8) + " calls  worst " + r.worst.ToString("0.00").PadLeft(7) + " ms  " + r.name);
            File.AppendAllText(Path.Combine(dir, "tick-cost.txt"), text.ToString() + "\n");
            File.AppendAllText(Path.Combine(dir, "tick-cost-all.txt"), "== " + costLabel + "\n" + string.Join("\n", costRows.OrderByDescending(r => r.Value.time).Select(r => CostName(r.Key) + "\t" + r.Value.calls + "\t" + CostMs(r.Value.time).ToString("0.000") + "\t" + CostMs(r.Value.worst).ToString("0.000"))) + "\n\n");
            costResults.Add("{\"label\":" + Json(costLabel) + ",\"ticks\":" + costTickCount + ",\"frames\":" + costFrames + ",\"realMs\":" + real.ToString("0.0") + ",\"worldTickMs\":" + worldTick.ToString("0.000")
                + ",\"modUsPerTick\":" + perTickUs.ToString("0.0") + ",\"modUsPerFrame\":" + perFrameUs.ToString("0.0") + ",\"modMs\":" + (tickMs + frameMs).ToString("0.0") + ",\"worstFrameMs\":" + CostMs(costWorstFrame).ToString("0.0")
                + ",\"gen0\":" + (GC.CollectionCount(0) - costGen0) + ",\"top\":[" + string.Join(",", top.Select(r => "{\"name\":" + Json(r.name) + ",\"calls\":" + r.calls + ",\"ms\":" + r.ms.ToString("0.00") + ",\"worstMs\":" + r.worst.ToString("0.00") + "}")) + "]}");
        }

        private static void CostSeedColony()
        {
            while (map.mapPawns.FreeColonistsSpawnedCount < 30)
            {
                var p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(center, map, 14), map);
            }
            var store = PhotoStore.Instance;
            Directory.CreateDirectory(store.FolderPath);
            string big = Path.Combine(dir, "cost-big.png");
            CopyAsPng("family", big);
            // The fixtures are written the way new photos are (the mod's own encoder, the "Photo file format" setting, JPG by default).
            // 1.7's EncodeToPNG always writes RGBA, which the workers leave to the engine on the game thread: real photos never look like that.
            byte[] large = CostFixture(File.ReadAllBytes(big), 0, 0);
            byte[] small = CostFixture(null, 600, 400);
            var people = map.mapPawns.FreeColonistsSpawned.ToList();
            string[] kinds = { "wedding", "party", GroupPhoto.Kind, Kinds.Manual, "birthday", Battles.Kind, "recruit", "journey" };
            for (int n = 1; n <= 500; n++)
            {
                var rec = ArchRecord(store, n <= 70 ? large : small, n, "cost-" + n, kinds[n % kinds.Length]);
                rec.SetPeople(people.InRandomOrder().Take(1 + n % 6));
                costIds.Add(rec.id);
            }
            store.NotifyChanged();
            CostTopUp();
        }

        private static byte[] CostFixture(byte[] image, int width, int height)
        {
            var tex = image != null ? new Texture2D(2, 2, TextureFormat.RGB24, false) : new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                if (image != null) tex.LoadImage(image, false);
                else { tex.SetPixels32(Enumerable.Repeat(new Color32(70, 120, 90, 255), width * height).ToArray()); tex.Apply(false); }
                var px = tex.GetPixels32(); var rgb = new byte[px.Length * 3];
                for (int i = 0, o = 0; i < px.Length; i++, o += 3) { rgb[o] = px[i].r; rgb[o + 1] = px[i].g; rgb[o + 2] = px[i].b; }
                return PhotoImageFormat.Encode(new PhotoPixels(tex.width, tex.height, rgb), PhotoImageFormat.SaveJpeg);
            }
            finally { UnityEngine.Object.Destroy(tex); }
        }

        private static void CostSeedDisplays()
        {
            int shown = 0;
            string Next() => costIds[shown++ % 70];
            var styles = new[] { FrameStyle.Material, FrameStyle.Gilded, FrameStyle.Polaroid, FrameStyle.BlackMat, FrameStyle.Canvas };
            for (int row = 0; row < 3; row++)
            {
                int wz = center.z + 12 + row * 8;
                for (int x = center.x - 22; x <= center.x + 22; x++)
                    foreach (var cell in new[] { new IntVec3(x, 0, wz), new IntVec3(x, 0, wz - 1) })
                    {
                        map.fogGrid.Unfog(cell); map.roofGrid.SetRoof(cell, null);
                        foreach (var t in cell.GetThingList(map).ToList()) if (!(t is Pawn) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                    }
                for (int x = center.x - 22; x <= center.x + 22; x++)
                {
                    var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                    wall.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(wall, new IntVec3(x, 0, wz), map);
                }
                for (int i = 0; i < 20; i++)
                {
                    var frame = ArchSpawn<Building_PhotoFrame>("MF_PhotoFrame", new IntVec3(center.x - 20 + i * 2, 0, wz - 1));
                    frame.SetPhoto(Next()); frame.SetStyle(styles[i % styles.Length]); frame.RefreshBinding();
                }
            }
            for (int c = 0; c < 2; c++)
            {
                var collage = ArchSpawn<Building_PhotoCollage>("MF_PhotoCollage", center + new IntVec3(-12 + c * 8, 0, 6));
                for (int slot = 0; slot < Building_PhotoCollage.Slots; slot++) collage.SetSlot(slot, Next());
            }
            var tree = ArchSpawn<Building_PhotoTree>("MF_MemoryTree", center + new IntVec3(10, 0, 6));
            for (int i = 0; i < MemoryTreeLayout.Count; i++) tree.SetPhoto(i, Next());
            costProjector = ArchSpawn<Building_PhotoProjector>("MF_Projector", center + new IntVec3(0, 0, -6));
            costProjector.SetSelectedFilm(costIds.Take(12).ToArray());
            var second = ArchSpawn<Building_PhotoProjector>("MF_Projector", center + new IntVec3(-14, 0, -6));
            second.SetSelectedFilm(costIds.Skip(12).Take(12).ToArray());
            costDisplayView = (center + new IntVec3(0, 0, 14)).ToVector3Shifted();
            CostTopUp();
        }

        private static void TickCostStep()
        {
            long began = Stopwatch.GetTimestamp();
            costDepth = 1;   // inside the mod's own frame update; a method that threw must not leave the count high
            try { TickCostPhase(); }
            finally { costProbeSelf += Stopwatch.GetTimestamp() - began; }
        }

        private static void TickCostPhase()
        {
            switch (phase)
            {
                case 0:
                    deadline = Time.realtimeSinceStartup + 600f; map = Find.CurrentMap; center = map.Center;
                    ClearHostiles(map); Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear();
                    Prefs.DevMode = false; Find.PlaySettings.showLearningHelper = false;
                    foreach (var cell in map.AllCells) map.fogGrid.Unfog(cell);
                    File.WriteAllText(Path.Combine(dir, "tick-cost.txt"), "Moment Frames tick cost, " + VersionControl.CurrentVersionStringWithRev + ", map " + map.Size.x + "\n\n");
                    CostInstrument();
                    CostCalibrate();
                    Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 24f);
                    CostBegin("small colony (" + map.mapPawns.FreeColonistsSpawnedCount + " colonists, no photos, default settings)", 1500, TimeSpeed.Superfast, 40f);
                    phase = 1; return;
                case 1:
                    if (CostRunning()) return;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    CostSeedColony(); Wait(2f); phase = 2; return;
                case 2:
                    CostBegin("large colony (30 colonists, 500 photos, no displays)", 3000, TimeSpeed.Superfast, 60f);
                    phase = 3; return;
                case 3:
                    if (CostRunning()) return;
                    CostControl(CostCalls("PhotoStore.GameComponentUpdate") >= costFrames * 9 / 10, "control: the frame update was timed every frame");
                    CostControl(CostCalls("MapComponent_MFBuildingMilestones.MapComponentTick") >= costTickCount * 9 / 10, "control: the map tick was timed every tick");
                    CostControl(CostCalls("Patch_PlacesToggle.Postfix") >= costFrames * 9 / 10, "control: a Harmony patch of the mod was timed every frame");
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    CostSeedDisplays(); Find.CameraDriver.SetRootPosAndSize(costDisplayView, 34f); Wait(3f); phase = 4; return;
                case 4:
                    CostBegin("displays in view (60 frames, 2 collages, tree, 2 projectors)", 1500, TimeSpeed.Fast, 30f);
                    phase = 5; return;
                case 5:
                    if (CostRunning()) return;
                    Find.CameraDriver.SetRootPosAndSize((center + new IntVec3(-80, 0, -80)).ToVector3Shifted(), 20f); Wait(.5f); phase = 6; return;
                case 6:
                    CostBegin("displays out of view", 1500, TimeSpeed.Fast, 30f);
                    phase = 7; return;
                case 7:
                    if (CostRunning()) return;
                    CostTopUp();
                    ClearHostiles(map);   // the map's own hostile animals would call a gathering off at once
                    Find.CameraDriver.SetRootPosAndSize(costProjector.Position.ToVector3Shifted(), 24f);
                    CostControl(MemoryNight.StartPrepared(costProjector, map.mapPawns.FreeColonistsSpawned.ToList(), 3600), "a memory night starts with everyone");
                    costNight = (LordJob_MFMemoryNight)map.lordManager.lords.Last(l => l.LordJob is LordJob_MFMemoryNight).LordJob;
                    Traverse.Create(costNight).Field("clock").Method("BeginWatching").GetValue();
                    costNight.EnsureAudience();
                    CostNightState("started");
                    Wait(.5f); phase = 8; return;
                case 8:
                    CostNightState("before measuring");
                    CostBegin("memory night watching", 1800, TimeSpeed.Fast, 30f);
                    phase = 9; return;
                case 9:
                    if (CostRunning()) return;
                    CostNightState("after measuring");
                    checks.Add("INFO: during the night, party tick patch " + CostCalls("Patch_PartyTick.Postfix") + " calls, night toil tick " + CostCalls("LordToil_MFMemoryNight.LordToilTick") + " calls, in " + costTickCount + " ticks");
                    foreach (var lordToEnd in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(lordToEnd);
                    CostTopUp();
                    Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 24f);
                    bool raided = false;
                    foreach (var enemy in Find.FactionManager.AllFactionsListForReading.Where(f => f.HostileTo(Faction.OfPlayer) && !f.defeated && !f.Hidden && f.def.humanlikeFaction).ToList())
                    {
                        var parms = new IncidentParms { target = map, faction = enemy, points = 1200f, forced = true, raidStrategy = RaidStrategyDefOf.ImmediateAttack, raidArrivalMode = PawnsArrivalModeDefOf.CenterDrop };
                        if (IncidentDefOf.RaidEnemy.Worker.TryExecute(parms)) { raided = true; break; }
                    }
                    CostControl(raided, "a raid arrives");
                    Wait(.5f); phase = 10; return;
                case 10:
                    CostBegin("raid (1200 points, in the colony)", 1500, TimeSpeed.Fast, 40f);
                    phase = 11; return;
                case 11:
                    if (CostRunning()) return;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    ClearHostiles(map); AlbumOpen(false, false); phase = 12; return;
                case 12:
                    CostBegin("album open (500 photos, paused)", 0, TimeSpeed.Paused, 8f);
                    phase = 13; return;
                case 13:
                    if (CostRunning()) return;
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    CostBegin("saving the first time (baselines for 500 fixture photos)", 0, TimeSpeed.Paused, 60f);
                    { var clock = Stopwatch.StartNew(); GameDataSaveLoader.SaveGame("mfcost"); clock.Stop(); checks.Add("INFO: whole save took " + clock.ElapsedMilliseconds + " ms"); }
                    CostEnd();
                    CostBegin("saving again (ordinary save, 500 photos)", 0, TimeSpeed.Paused, 60f);
                    { var clock = Stopwatch.StartNew(); GameDataSaveLoader.SaveGame("mfcost2"); clock.Stop(); checks.Add("INFO: second save took " + clock.ElapsedMilliseconds + " ms"); }
                    CostEnd();
                    CostBegin("saving a third time", 0, TimeSpeed.Paused, 60f);
                    { var clock = Stopwatch.StartNew(); GameDataSaveLoader.SaveGame("mfcost3"); clock.Stop(); checks.Add("INFO: third save took " + clock.ElapsedMilliseconds + " ms"); }
                    CostEnd();
                    var never = costTargets.Where(t => !costSeen.Contains(t)).Select(CostName).OrderBy(n => n).ToList();
                    File.AppendAllText(Path.Combine(dir, "tick-cost.txt"), string.Join("\n", checks) + "\n\nnever called (" + never.Count + "): " + string.Join(", ", never) + "\n");
                    bool ok = !checks.Any(c => c.StartsWith("FAIL"));
                    File.WriteAllText(Path.Combine(dir, "tick-cost-result.json"), "{\"ok\":" + (ok ? "true" : "false") + ",\"runtime\":" + Json(VersionControl.CurrentVersionStringWithRev) + ",\"overheadNs\":" + costOverheadNs.ToString("0")
                        + ",\"timed\":" + costTargets.Count + ",\"phases\":[" + string.Join(",", costResults) + "],\"checks\":[" + string.Join(",", checks.Select(Json)) + "]}");
                    active = false; Application.Quit(); return;
            }
        }
    }
}
