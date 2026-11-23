using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // Only reached by the explicitly marked disposable review-fixes probe.
    public static partial class Probe
    {
        private static int reviewWorldStage, reviewWorldHomeId, reviewWorldAwayId;
        private static Map reviewWorldHome, reviewWorldAway;
        private static PhotoRecord reviewWorldShip, reviewWorldAscension, reviewWorldGone, reviewWorldReplaced,
            reviewWorldNight, reviewWorldEndFrame, reviewWorldExpired, reviewWorldConcurrentA, reviewWorldConcurrentB,
            reviewWorldLateCooldown, reviewWorldLegacy;
        private static GameCondition reviewWorldCondition, reviewWorldReplacement, reviewWorldConcurrentConditionA,
            reviewWorldConcurrentConditionB;
        private static string reviewWorldNightId;
        private static int reviewWorldEventTick, reviewWorldDeadline, reviewWorldConcurrentSavedTick,
            reviewWorldLegacyTick, reviewWorldLegacyDeadline, reviewWorldLegacyCooldown;
        private static string reviewWorldDate, reviewWorldLegacyDate;

        private static PhotoRecord ReviewWorldRoundTrip(PhotoRecord original, string name)
        {
            string path = Path.Combine(dir, name + ".xml"); PhotoRecord loaded = original;
            Scribe.saver.InitSaving(path, "WorldReview"); Scribe_Deep.Look(ref loaded, "record"); Scribe.saver.FinalizeSaving();
            loaded = null; Scribe.loader.InitLoading(path); Scribe_Deep.Look(ref loaded, "record"); Scribe.loader.FinalizeLoading();
            ReviewFixCheck(loaded.requiredConditionDef == original.requiredConditionDef &&
                loaded.requiredConditionStartTick == original.requiredConditionStartTick && loaded.captureDeadlineTick == original.captureDeadlineTick &&
                loaded.ticksAbs == original.ticksAbs && loaded.dateLabel == original.dateLabel &&
                loaded.requiredSubjects.SequenceEqual(original.requiredSubjects),
                "native Scribe preserves condition identity, original event date, deadline, and pet subjects: " + name);
            return loaded;
        }

        private static GameCondition ReviewWorldRegister(string defName, Map target, int age)
        {
            var c = GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed(defName), GenDate.TicksPerDay * 3);
            (target == null ? Find.World.gameConditionManager : target.gameConditionManager).RegisterCondition(c);
            c.startTick = Find.TickManager.TicksGame - age;
            return c;
        }

        private static int ReviewWorldPendingCount(PhotoRecord rec)
        {
            var queued = (System.Collections.IEnumerable)AccessTools.Field(typeof(PhotoStore), "pending").GetValue(PhotoStore.Instance);
            return queued.Cast<object>().Count(p => ReferenceEquals(AccessTools.Field(p.GetType(), "record").GetValue(p), rec));
        }

        private static PhotoRecord ReviewWorldPoll(GameCondition c, string kind, int hour)
        {
            var s = MomentFramesMod.Settings; bool sky = s.msSky, disaster = s.msDisaster;
            try
            {
                s.msSky = kind == Conditions.KindSky; s.msDisaster = kind == Conditions.KindDisaster;
                Conditions.ProbeHour = hour; Conditions.Reset(); Conditions.Poll(PhotoStore.Instance, Find.TickManager.TicksGame);
                string key = kind + ":" + c.def.defName + ":" + c.startTick;
                var rec = PhotoStore.Instance.Records.SingleOrDefault(r => r.eventKey == key);
                ReviewFixCheck(rec != null && rec.requiredConditionDef == c.def.defName && rec.requiredConditionStartTick == c.startTick,
                    "production condition poll queues an exact guarded activation: " + c.def.defName);
                return rec;
            }
            finally { s.msSky = sky; s.msDisaster = disaster; }
        }

        private static void ReviewWorldLifeDates()
        {
            var store = PhotoStore.Instance;
            Pawn actor = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            var caravan = CaravanMaker.MakeCaravan(new[] { actor }, Faction.OfPlayer, new PlanetTile(0), true);
            ReviewFixCheck(actor.IsCaravanMember() && actor.MapHeld == null && actor.Tile == caravan.Tile,
                "life date regression uses an actual native caravan actor and its world tile");
            int now = Find.TickManager.TicksAbs;
            LifeEvents.Record(LifeEvents.Joined, new[] { actor }, null, null, null);
            var withHome = store.LifeEvents.Last();
            ReviewFixCheck(withHome.dateLabel == GenDate.DateFullStringAt(now, Find.WorldGrid.LongLatOf(caravan.Tile)) &&
                withHome.year == GenDate.Year(now, Find.WorldGrid.LongLatOf(caravan.Tile).x),
                "caravan life date uses the actor's tile even with a home map available");
            var maps = Find.Maps.ToList(); Map current = Find.CurrentMap;
            LifeEvent noMaps;
            try
            {
                Current.Game.CurrentMap = null; Find.Maps.Clear();
                LifeEvents.Record(LifeEvents.Lover, new[] { actor }, "Review partner", null, null);
                noMaps = store.LifeEvents.Last();
                ReviewFixCheck(noMaps.year > 0 && !noMaps.dateLabel.NullOrEmpty() && noMaps.dateLabel == withHome.dateLabel,
                    "zero-map caravan recording retains a nonzero year and its real tile date");
            }
            finally { Find.Maps.Clear(); Find.Maps.AddRange(maps); Current.Game.CurrentMap = current; }
            string lifePath = Path.Combine(dir, "review-world-life.xml"); LifeEvent loaded = noMaps;
            Scribe.saver.InitSaving(lifePath, "LifeReview"); Scribe_Deep.Look(ref loaded, "event"); Scribe.saver.FinalizeSaving();
            loaded = null; Scribe.loader.InitLoading(lifePath); Scribe_Deep.Look(ref loaded, "event"); Scribe.loader.FinalizeLoading();
            ReviewFixCheck(loaded.year == noMaps.year && loaded.dateLabel == noMaps.dateLabel && loaded.ticksAbs == now,
                "native life-event Scribe round-trip retains the corrected world-only date");
            ReviewFixCheck(PhotoStore.Year(PlanetTile.Invalid, now) == GenDate.Year(now, 0f) &&
                PhotoStore.DateLabel(PlanetTile.Invalid, now) == GenDate.DateFullStringAt(now, Vector2.zero),
                "an unavailable world tile falls back to a real zero-longitude calendar, never year zero");
            int boundary = GenDate.TickGameToAbs(GenDate.TicksPerYear);
            ReviewFixCheck(PhotoStore.Year(caravan.Tile, boundary) == GenDate.Year(boundary, Find.WorldGrid.LongLatOf(caravan.Tile).x),
                "world tile year calculation also follows the native calendar at a year boundary");
        }

        private static PhotoRecord ReviewWorldCountdown(Type type, string rootName, string key, Building origin)
        {
            var root = AccessTools.Field(type, rootName); var time = AccessTools.Field(type, "timeLeft");
            object oldRoot = root.GetValue(null), oldTime = time.GetValue(null);
            var store = PhotoStore.Instance;
            try
            {
                root.SetValue(null, null); time.SetValue(null, 3f);
                AccessTools.Method(typeof(Historic), "CheckEndings").Invoke(null, new object[] { store });
                ReviewFixCheck(!store.HasMark(key) && !store.HasKey(key), "rootless countdown does not invent a map or consume its mark: " + key);
                root.SetValue(null, origin);
                AccessTools.Method(typeof(Historic), "CheckEndings").Invoke(null, new object[] { store });
                var rec = store.Records.Single(r => r.eventKey == key);
                ReviewFixCheck(rec.mapId == origin.Map.uniqueID && rec.mapId != Find.CurrentMap.uniqueID && !rec.HasFile,
                    "countdown is queued for its real origin while another map is viewed: " + key);
                AccessTools.Method(typeof(Historic), "CheckEndings").Invoke(null, new object[] { store });
                ReviewFixCheck(store.Records.Count(r => r.eventKey == key) == 1, "repeated countdown poll stays once-only: " + key);
                return rec;
            }
            finally { root.SetValue(null, oldRoot); time.SetValue(null, oldTime); }
        }

        private static void ReviewWorldScope()
        {
            var c = ReviewWorldRegister("ColdSnap", null, 7500);
            var oldBlacklist = c.def.layerBlacklist;
            try
            {
                ReviewFixCheck(Conditions.ActiveOn(reviewWorldHome).Contains(c), "a real world condition is visible on an eligible loaded home map");
                c.def.layerBlacklist = new List<PlanetLayerDef>(oldBlacklist ?? new List<PlanetLayerDef>()) { reviewWorldHome.Tile.LayerDef };
                ReviewFixCheck(!c.CanApplyOnMap(reviewWorldHome) && !Conditions.ActiveOn(reviewWorldHome).Contains(c),
                    "world condition enumeration respects the installed native planet-layer filter");
            }
            finally { c.def.layerBlacklist = oldBlacklist; c.End(); }
            ReviewFixCheck(!Conditions.ActiveOn(reviewWorldHome).Contains(c) && !Conditions.ActiveOn(null).Any(),
                "ended world conditions and absent maps cannot satisfy a photograph guard");
        }

        private static bool ReviewWorldFixesStep()
        {
            var store = PhotoStore.Instance;
            switch (reviewWorldStage)
            {
                case 0:
                    reviewWorldHome = map = Find.CurrentMap; reviewWorldHomeId = map.uniqueID; center = map.Center;
                    Find.TickManager.Pause(); ReviewWorldLifeDates(); ReviewWorldScope();
                    var neighbors = new List<PlanetTile>(); Find.WorldGrid.GetTileNeighbors(map.Tile, neighbors);
                    var tile = neighbors.Where(t => t.Valid && !Find.World.Impassable(t) && !Find.WorldObjects.AnyWorldObjectAt(t)
                        && Find.WorldGrid[t].PrimaryBiome?.canBuildBase == true).DefaultIfEmpty(PlanetTile.Invalid).First();
                    ReviewFixCheck(tile.Valid, "away-map regression has a valid neighboring destination tile");
                    reviewWorldAway = GetOrGenerateMapUtility.GetOrGenerateMap(tile, new IntVec3(75, 1, 75), WorldObjectDefOf.Camp);
                    reviewWorldAwayId = reviewWorldAway.uniqueID; reviewWorldAway.fogGrid.ClearAllFog(); ClearHostiles(reviewWorldAway);
                    reviewWorldAway.mapDrawer.RegenerateEverythingNow(); Current.Game.CurrentMap = reviewWorldAway;
                    CameraJumper.TryJump(reviewWorldAway.Center, reviewWorldAway, CameraJumper.MovementMode.Cut);
                    reviewWorldStage = 1; Wait(2f); return false;
                case 1:
                    ReviewFixCheck(Find.CurrentMap == reviewWorldAway && WorldRendererUtility.DrawingMap,
                        "second native map settles through real rendering frames before the ending regression");
                    var computer = (Building)ThingMaker.MakeThing(ThingDef.Named("Ship_ComputerCore")); computer.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(computer, CellFinder.RandomClosewalkCellNear(reviewWorldHome.Center, reviewWorldHome, 8), reviewWorldHome);
                    reviewWorldShip = ReviewWorldCountdown(typeof(ShipCountdown), "shipRoot", "ending:shipcountdown", computer);
                    if (ModsConfig.IdeologyActive)
                    {
                        var core = (Building)ThingMaker.MakeThing(ThingDef.Named("ArchonexusCore"));
                        GenSpawn.Spawn(core, CellFinder.RandomClosewalkCellNear(reviewWorldHome.Center, reviewWorldHome, 12), reviewWorldHome);
                        reviewWorldAscension = ReviewWorldCountdown(typeof(ArchonexusCountdown), "archonexusCoreRoot", "ending:ascension", core);
                    }
                    else checks.Add("INFO: native ascension regression unavailable without Ideology; installed root API inspected separately");
                    Current.Game.CurrentMap = reviewWorldHome;
                    CameraJumper.TryJump(reviewWorldHome.Center, reviewWorldHome, CameraJumper.MovementMode.Cut);
                    reviewWorldStage = 2; Wait(2f); return false;
                case 2:
                    if (!reviewWorldShip.HasFile || reviewWorldAscension != null && !reviewWorldAscension.HasFile) { Wait(.1f); return false; }
                    File.Copy(store.OriginalPath(reviewWorldShip), Path.Combine(dir, "review-world-ship-origin.png"), true);
                    if (reviewWorldAscension != null) File.Copy(store.OriginalPath(reviewWorldAscension), Path.Combine(dir, "review-world-ascension-origin.png"), true);
                    ReviewFixCheck(reviewWorldShip.HasFile && reviewWorldShip.mapId == reviewWorldHomeId,
                        "origin-map ship request completes an actual native PNG after returning to that map");
                    if (reviewWorldAscension != null) ReviewFixCheck(reviewWorldAscension.HasFile && reviewWorldAscension.mapId == reviewWorldHomeId,
                        "origin-map ascension request completes an actual native PNG after returning to that map");
                    var blocker = store.RequestNow(reviewWorldHome, center.ToVector3Shifted(), Kinds.Test, "review:worldbusy", "Busy boundary", "", null, 12f, 1.5f, null);
                    ReviewFixCheck(blocker != null && PhotoCapture.Busy, "condition cancellation regression starts with a real native capture busy");
                    reviewWorldCondition = ReviewWorldRegister("HeatWave", reviewWorldHome, 7500);
                    reviewWorldGone = ReviewWorldPoll(reviewWorldCondition, Conditions.KindDisaster, 12);
                    ReviewFixCheck(!store.HasMark("disaster:first:HeatWave"), "queued disaster does not consume its first photograph before image success");
                    reviewWorldCondition.End(); reviewWorldStage = 3; Wait(1f); return false;
                case 3:
                    ReviewFixCheck(store.Get(reviewWorldGone.id) == null && !reviewWorldGone.HasFile && !store.HasMark("disaster:first:HeatWave"),
                        "a disaster ending while the renderer is busy leaves no stale image or first-photograph cooldown");
                    reviewWorldCondition = ReviewWorldRegister("HeatWave", reviewWorldHome, 7000);
                    Current.Game.CurrentMap = reviewWorldAway;
                    reviewWorldReplaced = ReviewWorldPoll(reviewWorldCondition, Conditions.KindDisaster, 12);
                    reviewWorldCondition.End(); reviewWorldReplacement = ReviewWorldRegister("HeatWave", reviewWorldHome, 6500);
                    ReviewFixCheck(Conditions.CaptureStatus(reviewWorldReplaced, reviewWorldHome) == Conditions.CaptureState.Ended,
                        "a different activation of the same condition cannot satisfy the old request");
                    reviewWorldStage = 4; Wait(.5f); return false;
                case 4:
                    ReviewFixCheck(store.Get(reviewWorldReplaced.id) == null && !reviewWorldReplaced.HasFile,
                        "returning later cannot photograph a replacement condition as the original event");
                    reviewWorldReplacement.End();
                    reviewWorldCondition = ReviewWorldRegister("Aurora", reviewWorldHome, 7500);
                    reviewWorldNight = ReviewWorldPoll(reviewWorldCondition, Conditions.KindSky, 22);
                    reviewWorldNightId = reviewWorldNight.id; reviewWorldEventTick = reviewWorldNight.ticksAbs;
                    reviewWorldDate = reviewWorldNight.dateLabel; reviewWorldDeadline = reviewWorldNight.captureDeadlineTick;
                    Conditions.ProbeHour = 12; Current.Game.CurrentMap = reviewWorldHome;
                    CameraJumper.TryJump(center, reviewWorldHome, CameraJumper.MovementMode.Cut);
                    reviewWorldStage = 5; Wait(1f); return false;
                case 5:
                    ReviewFixCheck(!reviewWorldNight.HasFile && store.Get(reviewWorldNightId) != null &&
                        Conditions.CaptureStatus(reviewWorldNight, reviewWorldHome) == Conditions.CaptureState.Wait,
                        "a viewed aurora request waits through daylight instead of saving an ordinary daytime photograph");
                    ReviewWorldRoundTrip(reviewWorldNight, "review-world-night-pending");
                    var endCondition = ReviewWorldRegister("Aurora", reviewWorldHome, 6500);
                    Conditions.ProbeHour = 22;
                    reviewWorldEndFrame = store.RequestNow(reviewWorldHome, center.ToVector3Shifted(), Conditions.KindSky,
                        "sky:Aurora:" + endCondition.startTick, "End-frame guard", "", null, 12f, 1.5f, null);
                    ReviewFixCheck(reviewWorldEndFrame != null && PhotoCapture.Busy, "end-frame validity fixture enters the actual native renderer");
                    reviewWorldEndFrame.requiredConditionDef = "Aurora"; reviewWorldEndFrame.requiredConditionStartTick = endCondition.startTick;
                    store.SetCaptureDeadline(reviewWorldEndFrame, Find.TickManager.TicksGame + GenDate.TicksPerDay);
                    endCondition.End(); Conditions.ProbeHour = 12;
                    reviewWorldStage = 6; Wait(1f); return false;
                case 6:
                    ReviewFixCheck(!reviewWorldEndFrame.HasFile && store.Get(reviewWorldEndFrame.id) == null,
                        "the final native capture callback rejects a condition that ended after capture started");
                    GameDataSaveLoader.SaveGame("MomentFramesReviewWorldPending");
                    ReviewFixCheck(File.Exists(GenFilePaths.FilePathForSavedGame("MomentFramesReviewWorldPending")),
                        "actual disposable game save contains the guarded pending aurora");
                    reviewWorldStage = 7;
                    GameDataSaveLoader.LoadGame("MomentFramesReviewWorldPending"); Wait(1f); return false;
                case 7:
                    reviewWorldHome = map = Find.Maps.Single(m => m.uniqueID == reviewWorldHomeId);
                    reviewWorldAway = Find.Maps.Single(m => m.uniqueID == reviewWorldAwayId); center = map.Center;
                    reviewWorldNight = store.Get(reviewWorldNightId); Find.TickManager.Pause();
                    ReviewFixCheck(reviewWorldNight != null && !reviewWorldNight.HasFile && reviewWorldNight.ticksAbs == reviewWorldEventTick &&
                        reviewWorldNight.dateLabel == reviewWorldDate && reviewWorldNight.captureDeadlineTick == reviewWorldDeadline &&
                        reviewWorldNight.requiredConditionDef == "Aurora" && Conditions.CaptureStatus(reviewWorldNight, map) == Conditions.CaptureState.Wait,
                        "actual whole-game load restores the exact condition, original date, deadline, and daylight waiting state");
                    var legacy = ReviewWorldRoundTrip(reviewWorldNight, "review-world-night-legacy");
                    legacy.requiredConditionDef = null; legacy.requiredConditionStartTick = -1; legacy.captureDeadlineTick = -1;
                    ReviewFixCheck(Conditions.CaptureStatus(legacy, map) == Conditions.CaptureState.Wait && legacy.requiredConditionDef == null,
                        "old pending event keys are checked without rewriting legacy identity or dates");
                    Current.Game.CurrentMap = map; CameraJumper.TryJump(center, map, CameraJumper.MovementMode.Cut);
                    reviewWorldStage = 8; Wait(2f); return false;
                case 8:
                    Conditions.ProbeHour = 22;
                    ReviewFixCheck(!store.HasMark("sky:first:Aurora") && Conditions.CaptureStatus(reviewWorldNight, map) == Conditions.CaptureState.Ready,
                        "the original loaded aurora becomes eligible when its visible nighttime window returns");
                    reviewWorldStage = 9; Wait(.1f); return false;
                case 9:
                    if (!reviewWorldNight.HasFile) { Wait(.1f); return false; }
                    File.Copy(store.OriginalPath(reviewWorldNight), Path.Combine(dir, "review-world-aurora-after-load.png"), true);
                    ReviewFixCheck(reviewWorldNight.ticksAbs == reviewWorldEventTick && reviewWorldNight.dateLabel == reviewWorldDate &&
                        store.HasMark("sky:first:Aurora") && store.LastSkyTick >= 0,
                        "nighttime native PNG consumes its first/cooldown only after success and keeps the original event date");
                    foreach (var c in Conditions.ActiveOn(map).ToList()) if (c.def.defName == "Aurora") c.End();
                    reviewWorldCondition = ReviewWorldRegister("ColdSnap", map, 7500);
                    Current.Game.CurrentMap = reviewWorldAway;
                    reviewWorldExpired = ReviewWorldPoll(reviewWorldCondition, Conditions.KindDisaster, 12);
                    store.SetCaptureDeadline(reviewWorldExpired, Find.TickManager.TicksGame - 1);
                    var expiredLoaded = ReviewWorldRoundTrip(reviewWorldExpired, "review-world-expired-pending");
                    store.Remove(reviewWorldExpired); ((List<PhotoRecord>)store.Records).Add(expiredLoaded); reviewWorldExpired = expiredLoaded;
                    AccessTools.Method(typeof(PhotoStore), "RestorePendingAfterLoad").Invoke(store, null);
                    reviewWorldStage = 10; Wait(.5f); return false;
                case 10:
                    ReviewFixCheck(store.Get(reviewWorldExpired.id) == null && !reviewWorldExpired.HasFile && !store.HasMark("disaster:first:ColdSnap"),
                        "restoring an already expired request never extends its original one-day capture deadline or spends the cooldown");
                    reviewWorldCondition.End();
                    // Both kinds have been photographed before. Booking both while away must not
                    // allow two repeat successes inside the same seasonal cooldown.
                    store.AddMark("disaster:first:ToxicFallout"); store.AddMark("disaster:first:HeatWave");
                    store.LastDisasterTick = Find.TickManager.TicksGame - Battles.SeasonTicks;
                    Current.Game.CurrentMap = reviewWorldAway;
                    reviewWorldConcurrentConditionA = ReviewWorldRegister("ToxicFallout", reviewWorldHome, 8000);
                    reviewWorldConcurrentConditionB = ReviewWorldRegister("HeatWave", reviewWorldHome, 7900);
                    reviewWorldConcurrentA = ReviewWorldPoll(reviewWorldConcurrentConditionA, Conditions.KindDisaster, 12);
                    reviewWorldConcurrentB = store.Records.Single(r => r.eventKey == "disaster:HeatWave:" + reviewWorldConcurrentConditionB.startTick);
                    ReviewFixCheck(Conditions.CaptureStatus(reviewWorldConcurrentA, reviewWorldHome) == Conditions.CaptureState.Ready &&
                        Conditions.CaptureStatus(reviewWorldConcurrentB, reviewWorldHome) == Conditions.CaptureState.Ready &&
                        ReviewWorldPendingCount(reviewWorldConcurrentA) == 1 && ReviewWorldPendingCount(reviewWorldConcurrentB) == 1,
                        "two already-known compatible disasters enter the real pending queue together before either image succeeds");
                    Current.Game.CurrentMap = map = reviewWorldHome; center = map.Center;
                    CameraJumper.TryJump(center, map, CameraJumper.MovementMode.Cut);
                    reviewWorldStage = 11; Wait(1f); return false;
                case 11:
                    if (PhotoCapture.Busy ||
                        !reviewWorldConcurrentA.HasFile && store.Get(reviewWorldConcurrentA.id) != null ||
                        !reviewWorldConcurrentB.HasFile && store.Get(reviewWorldConcurrentB.id) != null) { Wait(.1f); return false; }
                    var successful = reviewWorldConcurrentA.HasFile ? reviewWorldConcurrentA : reviewWorldConcurrentB;
                    var rejected = ReferenceEquals(successful, reviewWorldConcurrentA) ? reviewWorldConcurrentB : reviewWorldConcurrentA;
                    ReviewFixCheck(reviewWorldConcurrentA.HasFile != reviewWorldConcurrentB.HasFile && store.Get(rejected.id) == null &&
                        Conditions.ActiveOn(map).Contains(reviewWorldConcurrentConditionA) && Conditions.ActiveOn(map).Contains(reviewWorldConcurrentConditionB),
                        "only one actual native PNG survives two simultaneous repeat requests while both disasters remain active");
                    reviewWorldConcurrentSavedTick = store.LastDisasterTick;
                    int successGameTick = GenDate.TickAbsToGame(successful.capturedTicksAbs), successObservedTick = Find.TickManager.TicksGame;
                    checks.Add("INFO: concurrent cooldown last=" + reviewWorldConcurrentSavedTick + ", now=" + successObservedTick +
                        ", capturedAbs=" + successful.capturedTicksAbs + ", capturedGame=" + successGameTick);
                    File.WriteAllText(Path.Combine(dir, "review-world-cooldown-timing.json"), "{\"last\":" + reviewWorldConcurrentSavedTick +
                        ",\"now\":" + successObservedTick + ",\"capturedAbs\":" + successful.capturedTicksAbs + ",\"capturedGame\":" + successGameTick +
                        ",\"toxicFirst\":" + store.HasMark("disaster:first:ToxicFallout").ToString().ToLowerInvariant() +
                        ",\"heatFirst\":" + store.HasMark("disaster:first:HeatWave").ToString().ToLowerInvariant() + "}");
                    ReviewFixCheck(successful.capturedTicksAbs >= 0 && reviewWorldConcurrentSavedTick == successGameTick &&
                        successObservedTick >= successGameTick && successObservedTick - successGameTick < Battles.SeasonTicks &&
                        store.HasMark("disaster:first:ToxicFallout") && store.HasMark("disaster:first:HeatWave"),
                        "the successful repeat consumes the shared cooldown without changing either existing first mark");
                    File.Copy(store.OriginalPath(successful), Path.Combine(dir, "review-world-concurrent-one-success.png"), true);
                    var rejectedCondition = ReferenceEquals(rejected, reviewWorldConcurrentA) ? reviewWorldConcurrentConditionA : reviewWorldConcurrentConditionB;
                    reviewWorldLateCooldown = store.RequestNow(map, center.ToVector3Shifted(), Conditions.KindDisaster,
                        rejected.eventKey, "Final cooldown boundary", "", null, 12f, 1.5f, null);
                    ReviewFixCheck(reviewWorldLateCooldown != null && PhotoCapture.Busy, "the cooldown final-callback fixture enters the actual native renderer");
                    reviewWorldLateCooldown.requiredConditionDef = rejectedCondition.def.defName;
                    reviewWorldLateCooldown.requiredConditionStartTick = rejectedCondition.startTick;
                    store.SetCaptureDeadline(reviewWorldLateCooldown, Find.TickManager.TicksGame + GenDate.TicksPerDay);
                    reviewWorldStage = 12; Wait(.5f); return false;
                case 12:
                    if (PhotoCapture.Busy) { Wait(.1f); return false; }
                    ReviewFixCheck(!PhotoCapture.Busy && !reviewWorldLateCooldown.HasFile && store.Get(reviewWorldLateCooldown.id) == null &&
                        store.LastDisasterTick == reviewWorldConcurrentSavedTick,
                        "a direct native renderer callback also rejects a repeat after another photo consumed the cooldown");
                    reviewWorldConcurrentConditionA.End(); reviewWorldConcurrentConditionB.End();
                    reviewWorldCondition = ReviewWorldRegister("Aurora", map, 6100);
                    Conditions.ProbeHour = 12; Current.Game.CurrentMap = reviewWorldAway;
                    string legacyKey = "sky:Aurora:" + reviewWorldCondition.startTick;
                    var oldPending = store.Request(map, center, Conditions.KindSky, legacyKey, "Legacy waiting boundary", "", null, 12f);
                    store.AddMark(legacyKey); store.AddMark("sky:first:Aurora");
                    reviewWorldLegacyCooldown = store.LastSkyTick;
                    ReviewFixCheck(oldPending != null && oldPending.requiredConditionDef == null && oldPending.requiredConditionStartTick == -1 &&
                        oldPending.captureDeadlineTick == -1 && !Conditions.Takes(store, Conditions.KindSky, reviewWorldCondition.def, Find.TickManager.TicksGame),
                        "old-format pending starts with default minus-one deadline and its already-spent first and cooldown");
                    reviewWorldLegacy = ReviewWorldRoundTrip(oldPending, "review-world-legacy-default-deadline");
                    reviewWorldLegacyTick = reviewWorldLegacy.ticksAbs; reviewWorldLegacyDate = reviewWorldLegacy.dateLabel;
                    store.Remove(oldPending); ((List<PhotoRecord>)store.Records).Add(reviewWorldLegacy); store.NotifyChanged();
                    AccessTools.Method(typeof(PhotoStore), "RestorePendingAfterLoad").Invoke(store, null);
                    reviewWorldLegacyDeadline = Find.TickManager.TicksGame + GenDate.TicksPerDay;
                    ReviewFixCheck(reviewWorldLegacy.captureDeadlineTick == reviewWorldLegacyDeadline && ReviewWorldPendingCount(reviewWorldLegacy) == 1 &&
                        reviewWorldLegacy.requiredConditionDef == null && reviewWorldLegacy.requiredConditionStartTick == -1 &&
                        reviewWorldLegacy.ticksAbs == reviewWorldLegacyTick && reviewWorldLegacy.dateLabel == reviewWorldLegacyDate &&
                        store.HasMark(legacyKey) && store.HasMark("sky:first:Aurora") && store.LastSkyTick == reviewWorldLegacyCooldown,
                        "restoring a legacy reservation initializes one deadline while preserving its date, identity, marks, and spent cooldown");
                    Current.Game.CurrentMap = map; CameraJumper.TryJump(center, map, CameraJumper.MovementMode.Cut);
                    reviewWorldStage = 13; Wait(2f); return false;
                case 13:
                    ReviewFixCheck(!PhotoCapture.Busy && !reviewWorldLegacy.HasFile && ReviewWorldPendingCount(reviewWorldLegacy) == 1,
                        "legacy aurora remains queued through the unavailable daylight window");
                    Conditions.ProbeHour = 22;
                    ReviewFixCheck(Conditions.CaptureStatus(reviewWorldLegacy, map) == Conditions.CaptureState.Ready,
                        "a legacy booked reservation remains eligible despite its already-consumed cooldown");
                    // GameComponentUpdate calls Probe.Update too; defer the probe before re-entering
                    // the production queue so this boundary fixture cannot recursively run itself.
                    reviewWorldStage = 14; Wait(.5f);
                    store.GameComponentUpdate();
                    ReviewFixCheck(PhotoCapture.Busy && ReviewWorldPendingCount(reviewWorldLegacy) == 0,
                        "legacy reservation starts through the production queue and actual native renderer");
                    Conditions.ProbeHour = 12; return false;
                case 14:
                    if (PhotoCapture.Busy) { Wait(.1f); return false; }
                    ReviewFixCheck(!PhotoCapture.Busy && !reviewWorldLegacy.HasFile && store.Get(reviewWorldLegacy.id) == reviewWorldLegacy &&
                        ReviewWorldPendingCount(reviewWorldLegacy) == 1 && reviewWorldLegacy.captureDeadlineTick == reviewWorldLegacyDeadline &&
                        reviewWorldLegacy.ticksAbs == reviewWorldLegacyTick && reviewWorldLegacy.dateLabel == reviewWorldLegacyDate &&
                        store.HasMark(reviewWorldLegacy.eventKey) && store.HasMark("sky:first:Aurora") && store.LastSkyTick == reviewWorldLegacyCooldown,
                        "a final native callback changing from ready to wait requeues legacy exactly once without extending its original deadline");
                    ReviewWorldRoundTrip(reviewWorldLegacy, "review-world-legacy-after-final-wait");
                    AccessTools.Method(typeof(PhotoStore), "RestorePendingAfterLoad").Invoke(store, null);
                    ReviewFixCheck(ReviewWorldPendingCount(reviewWorldLegacy) == 1 && reviewWorldLegacy.captureDeadlineTick == reviewWorldLegacyDeadline,
                        "another restore does not duplicate the waiting reservation or replace its established deadline");
                    Conditions.ProbeHour = 22; reviewWorldStage = 15; Wait(.1f); return false;
                case 15:
                    if (!reviewWorldLegacy.HasFile && store.Get(reviewWorldLegacy.id) != null) { Wait(.1f); return false; }
                    int legacySavedTick = GenDate.TickAbsToGame(reviewWorldLegacy.capturedTicksAbs), legacyObservedTick = Find.TickManager.TicksGame;
                    checks.Add("INFO: legacy cooldown last=" + store.LastSkyTick + ", now=" + legacyObservedTick +
                        ", capturedAbs=" + reviewWorldLegacy.capturedTicksAbs + ", capturedGame=" + legacySavedTick);
                    File.WriteAllText(Path.Combine(dir, "review-world-legacy-timing.json"), "{\"last\":" + store.LastSkyTick +
                        ",\"now\":" + legacyObservedTick + ",\"capturedAbs\":" + reviewWorldLegacy.capturedTicksAbs +
                        ",\"capturedGame\":" + legacySavedTick + ",\"oldCooldown\":" + reviewWorldLegacyCooldown + "}");
                    ReviewFixCheck(reviewWorldLegacy.HasFile && File.Exists(store.OriginalPath(reviewWorldLegacy)) && reviewWorldLegacy.requiredConditionDef == null &&
                        reviewWorldLegacy.requiredConditionStartTick == -1 && reviewWorldLegacy.captureDeadlineTick == reviewWorldLegacyDeadline &&
                        reviewWorldLegacy.ticksAbs == reviewWorldLegacyTick && reviewWorldLegacy.dateLabel == reviewWorldLegacyDate &&
                        store.HasMark(reviewWorldLegacy.eventKey) && store.HasMark("sky:first:Aurora") &&
                        store.LastSkyTick == legacySavedTick && legacyObservedTick >= legacySavedTick,
                        "the original legacy reservation finally saves an actual nighttime PNG with unchanged event date, identity, and deadline");
                    File.Copy(store.OriginalPath(reviewWorldLegacy), Path.Combine(dir, "review-world-legacy-after-wait.png"), true);
                    reviewWorldCondition.End(); Conditions.ProbeHour = -1;
                    File.WriteAllText(Path.Combine(dir, "review-world-fixes-scope.json"), "{\"wholeGameLoad\":true,\"shipNativePng\":true,\"ascensionNativePng\":" +
                        (reviewWorldAscension != null ? "true" : "false") + ",\"conditionNativePng\":true,\"simultaneousRepeatNativePng\":true,\"legacyFinalWaitNativePng\":true,\"controlledFixture\":true,\"naturalVictoryGameplay\":false}");
                    return true;
            }
            return false;
        }
    }
}
