using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    internal static class Hooks
    {
        private static MomentFramesSettings S => MomentFramesMod.Settings;

        internal static bool Playing => Current.ProgramState == ProgramState.Playing && Current.Game != null;
        internal static bool AutomaticCapture => Playing && SettingsCapturePolicy.Automatic(MomentFramesMod.Settings);

        internal static PhotoRecord Request(Map map, IntVec3 center, string kind, string key, string title, string desc, IEnumerable<Pawn> people, float? span = null, float aspect = PhotoCapture.DefaultAspect, int delayTicks = 0)
        {
            try
            {
                var store = PhotoStore.Instance;
                if (store == null || map == null) return null;
                return store.RequestAutomatic(map, center, kind, key, title, desc, people, span, aspect, null, delayTicks);
            }
            catch (Exception e)
            {
                Log.Warning("[MomentFrames] hook failed (" + kind + "): " + e);
                return null;
            }
        }

        internal static PhotoRecord Request(Map map, Vector3 center, string kind, string key, string title, string desc, IEnumerable<Pawn> people, float span, float aspect, int delayTicks = 0)
        {
            try
            {
                var store = PhotoStore.Instance;
                if (store == null || map == null) return null;
                return store.RequestAutomatic(map, center, kind, key, title, desc, people, span, aspect, null, delayTicks);
            }
            catch (Exception e)
            {
                Log.Warning("[MomentFrames] hook failed (" + kind + "): " + e);
                return null;
            }
        }

        /// <summary>A moment hook failed: logged once per hook, and the game's own method carries on (every hook body runs inside a try, so a
        /// modded ritual or an odd lord state can cost a photo but never the ceremony, death or letter it is attached to).</summary>
        internal static void Fail(string what, Exception e) => Log.WarningOnce("[MomentFrames] " + what + " hook failed: " + e, ("MomentFrames." + what).GetHashCode());

        /// <summary>One-time milestone: returns false if already marked.</summary>
        internal static bool Once(string mark)
        {
            var store = PhotoStore.Instance;
            return store != null && store.AddMark(mark);
        }

        internal static IEnumerable<Pawn> Owned(LordJob job) => job?.lord?.ownedPawns ?? Enumerable.Empty<Pawn>();

        internal static string Key(string prefix, LordJob job) => prefix + ":" + (job?.lord != null ? job.lord.GetUniqueLoadID() : Find.TickManager.TicksGame.ToString());

        public const float GatherMinSpan = 13f, GatherMaxSpan = 31f, GatherPadding = 2f, GatherReach = 20f;
        public const float MinZoom = 0.5f, MaxZoom = 2f;

        /// <summary>The player's zoom for automatic framing: the worked-out span divided by it, so a larger number means a closer picture
        /// (1.1 = a tenth nearer, 0.9 = wider). Photos the player frames by hand have their own span setting and are left alone.</summary>
        public static float ZoomedSpan(float span)
        {
            float zoom = MomentFramesMod.Settings?.eventZoom ?? 1f;
            if (zoom < MinZoom || zoom > MaxZoom) zoom = 1f;
            return Mathf.Clamp(span / zoom, PhotoCapture.MinSpan, PhotoCapture.MaxSpan);
        }

        /// <summary>Framing for a gathering: the venue cell plus everyone taking part who is near it (within GatherReach — a straggler far away must
        /// not stretch the picture), padded, at the rectangle's own proportions, never narrower than minSpan nor wider than maxSpan.</summary>
        internal static void AutoFrame(Map map, IntVec3 focus, IEnumerable<Pawn> people, float minSpan, float maxSpan, out Vector3 center, out float span, out float aspect, IEnumerable<Vector3> extra = null)
        {
            if (!focus.IsValid || !focus.InBounds(map)) focus = map.Center;
            int minX = focus.x, maxX = focus.x, minZ = focus.z, maxZ = focus.z;
            if (people != null)
                foreach (var p in people)
                {
                    if (p == null || !p.Spawned || p.Map != map || !p.Position.InHorDistOf(focus, GatherReach)) continue;
                    var c = p.Position;
                    if (c.x < minX) minX = c.x; if (c.x > maxX) maxX = c.x; if (c.z < minZ) minZ = c.z; if (c.z > maxZ) maxZ = c.z;
                }
            // Things that are part of the moment but not people (sky lanterns in the air): drawn positions, same reach.
            if (extra != null)
                foreach (var v in extra)
                {
                    var c = v.ToIntVec3();
                    if (!c.InBounds(map) || !c.InHorDistOf(focus, GatherReach)) continue;
                    if (c.x < minX) minX = c.x; if (c.x > maxX) maxX = c.x; if (c.z < minZ) minZ = c.z; if (c.z > maxZ) maxZ = c.z;
                }
            var rect = CellRect.FromLimits(minX, minZ, maxX, maxZ).ExpandedBy((int)GatherPadding).ClipInsideMap(map);
            Designator_TakePhoto.FitRect(rect, out center, out span, out aspect);
            span = Mathf.Clamp(span, minSpan, maxSpan);
            // Never a portrait for a gathering: a tall, narrow group (r44: the spot and one guest) gets side margins instead (widening never cuts anything).
            if (aspect < PhotoCapture.DefaultAspect) aspect = PhotoCapture.DefaultAspect;
            span = ZoomedSpan(span);
            center = KeepInside(map, center, span, aspect);
        }

        /// <summary>Moves a capture's centre so the picture stays on the map where it fits: near an edge (colonists who just walked in) the
        /// minimum span reached past the edge and the photo showed a black band (r100). The people's rectangle is inside the map and inside
        /// the picture, so sliding the picture inwards never cuts them. A map smaller than the picture keeps it centred.</summary>
        internal static Vector3 KeepInside(Map map, Vector3 center, float span, float aspect)
        {
            float halfW = span * aspect / 2f, halfH = span / 2f;
            if (map.Size.x >= halfW * 2f) center.x = Mathf.Clamp(center.x, halfW, map.Size.x - halfW);
            if (map.Size.z >= halfH * 2f) center.z = Mathf.Clamp(center.z, halfH, map.Size.z - halfH);
            return center;
        }
    }

    /// <summary>Photographs gatherings part-way through instead of at their end (when everyone is already breaking up): rituals at their climax
    /// (RitualMoment) or else at 60% progress, parties at 60% of their duration, bestowing ceremonies once the speech has begun. The photo goes into
    /// the album at once (card and all) and stays there whatever happens to the event afterwards. Runs every CheckEvery ticks of an active event only.</summary>
    internal static class MidEvent
    {
        public const int CheckEvery = 10;                        // an execution's climax (the executioner at the altar) lasts 35 ticks
        public const float ShootAt = 0.6f, ShootUntil = 0.95f;   // window of progress in which the shot is attempted (the map may not be viewed at first)
        public const float TreeShootAt = 0.3f, TreeShootUntil = 0.55f;   // tree celebration: its first stage (0-60%) gathers everyone around the tree
        public const float LanternRise = 3f;                     // cells the released sky lanterns have drifted up (median) before the shot
        public const int DuelShootAfter = 120;                   // ticks into the fight
        public const int SpeechShootAfter = 180;                 // ticks after the speaker takes the floor (the crowd has turned to listen)
        public const int BestowSpeechTicks = 300;                // ticks into the ceremony before the shot
        private static readonly HashSet<string> done = new HashSet<string>();   // keys already shot or skipped this session (a new game resets)
        private static readonly Dictionary<string, int> momentSince = new Dictionary<string, int>();   // ritual key -> tick the fight / speech was first seen

        internal static void Reset() { done.Clear(); momentSince.Clear(); }

        private static bool InWindow(float progress, float from, float until) => !float.IsNaN(progress) && progress >= from && progress <= until;

        /// <summary>Is this pawn at the climax of a ritual job: an executioner / sacrificer standing at the altar, a scarifier or blinder touching
        /// the target, waiting out the 35 ticks before the cut? victim = who it is done to.</summary>
        internal static bool AtClimax(Pawn p, RitualMoment moment, out Pawn victim)
        {
            victim = null;
            var job = p?.CurJob;
            if (job == null || job.def == null || !p.Spawned || p.pather.Moving) return false;
            var target = job.targetA.Pawn;
            if (target == null || !target.Spawned) return false;
            if (moment == RitualMoment.Kill && job.def == JobDefOf.Sacrifice)
            {
                if (!job.targetB.IsValid || p.Position != job.targetB.Cell) return false;   // the job walks to the altar cell, waits 35 ticks, then cuts
            }
            else if (moment == RitualMoment.Mutilation && (job.def == JobDefOf.Scarify || job.def == JobDefOf.Blind))
            {
                if (!p.Position.AdjacentTo8WayOrInside(target.Position)) return false;
            }
            else return false;
            victim = target;
            return true;
        }

        /// <summary>Sky lanterns near spot that have risen at least LanternRise cells (median), with their drawn positions for the framing.</summary>
        internal static bool LanternsAloft(Map map, IntVec3 spot, out List<Vector3> drawn)
        {
            drawn = null;
            if (ThingDefOf.SkyLantern == null || map == null) return false;
            var lanterns = map.listerThings.ThingsOfDef(ThingDefOf.SkyLantern);
            if (lanterns.Count == 0) return false;
            var rises = new List<float>();
            drawn = new List<Vector3>();
            foreach (var t in lanterns)
            {
                if (!t.Spawned || !t.Position.InHorDistOf(spot, Hooks.GatherReach)) continue;
                Vector3 d = t.DrawPos, root = t.Position.ToVector3Shifted();
                rises.Add(new Vector2(d.x - root.x, d.z - root.z).magnitude);
                drawn.Add(d);
            }
            if (rises.Count == 0) return false;
            rises.Sort();
            return rises[rises.Count / 2] >= LanternRise;
        }

        /// <summary>True once the moment has been in view for this many ticks (the first sighting is only remembered, not shot).</summary>
        private static bool Settled(string key, int ticks)
        {
            int now = Find.TickManager.TicksGame;
            if (!momentSince.TryGetValue(key, out int since)) { momentSince[key] = now; return false; }
            return now - since >= ticks;
        }

        /// <summary>Whether this ritual is at its moment now, and what to centre on.</summary>
        private static bool MomentNow(LordJob_Ritual job, string key, RitualMoment moment, out IntVec3 focus, out List<Vector3> extra)
        {
            focus = job.selectedTarget.IsValid ? job.selectedTarget.Cell : job.Spot;
            extra = null;
            switch (moment)
            {
                case RitualMoment.TreeCelebration:
                    return InWindow(job.Progress, TreeShootAt, TreeShootUntil);
                case RitualMoment.Kill:
                case RitualMoment.Mutilation:
                    foreach (var p in job.lord.ownedPawns)
                        if (AtClimax(p, moment, out Pawn victim)) { focus = victim.Position; return true; }
                    return false;
                case RitualMoment.Duel:
                    if (!job.lord.ownedPawns.Any(p => p.mindState?.duty?.def?.defName == "Duel")) return false;
                    return Settled(key, DuelShootAfter);
                case RitualMoment.Speech:
                {
                    Pawn speaker = job.lord.ownedPawns.FirstOrDefault(p => RitualGroups.IsSpeechDuty(p?.mindState?.duty?.def?.defName));
                    if (speaker == null || !speaker.Spawned) return false;
                    if (!Settled(key, SpeechShootAfter)) return false;
                    focus = speaker.Position;
                    return true;
                }
                case RitualMoment.SkyLanterns:
                    return LanternsAloft(job.Map, job.Spot, out extra);
                default:
                    return InWindow(job.Progress, ShootAt, ShootUntil);
            }
        }

        internal static void RitualTick(LordJob_Ritual job)
        {
            if (!Hooks.AutomaticCapture || job?.lord == null || job.Map == null) return;
            if (job is LordJob_BestowingCeremony b) { BestowTick(b); return; }
            if (job.DurationTicks <= 0) return;
            string key = Hooks.Key("ritual", job);
            if (done.Contains(key)) return;
            var moment = RitualGroups.MomentOf(job.Ritual);
            if (!MomentNow(job, key, moment, out IntVec3 focus, out List<Vector3> extra)) return;
            var store = PhotoStore.Instance;
            if (store == null || store.HasKey(key)) { done.Add(key); return; }
            if (!RitualGroups.Describe(job, out string kind, out string title, out string desc)) { done.Add(key); return; }
            var people = Hooks.Owned(job).ToList();
            Hooks.AutoFrame(job.Map, focus, people, Hooks.GatherMinSpan, Hooks.GatherMaxSpan, out Vector3 center, out float span, out float aspect, extra);
            if (store.RequestAutomaticNow(job.Map, center, kind, key, title, desc, people, span, aspect, null) != null) done.Add(key);
        }

        internal static void PartyTick(LordJob_Joinable_Party job)
        {
            if (job is LordJob_MFMemoryNight) return;   // a party underneath, photographed as itself (MemoryNight.cs)
            if (!Hooks.AutomaticCapture || job?.lord == null || job.Map == null || !MomentFramesMod.Settings.captureParties || job.DurationTicks <= 0) return;
            float progress = job.lord.ticksInToil / (float)job.DurationTicks;
            if (progress < ShootAt || progress > ShootUntil) return;
            string key = Hooks.Key("party", job);
            if (done.Contains(key)) return;
            var store = PhotoStore.Instance;
            if (store == null || store.HasKey(key)) { done.Add(key); return; }
            var people = Hooks.Owned(job).ToList();
            Hooks.AutoFrame(job.Map, job.Spot, people, Hooks.GatherMinSpan, Hooks.GatherMaxSpan, out Vector3 center, out float span, out float aspect);
            EventDescriptions.Party(job, out string title, out string prefix, out string instrument);
            string desc = PhotoDesc.PickSpecific(prefix, "party", job.Organizer != null ? new[] { job.Organizer } : null, job.Map,
                prefix == "concert" ? title : null, instrument);
            var record = store.RequestAutomaticNow(job.Map, center, "party", key, title, desc, people, span, aspect, null);
            if (record != null) { record.eventSubtype = prefix; record.generatedDesc = true; done.Add(key); }
        }

        private static void BestowTick(LordJob_BestowingCeremony job)
        {
            if (!job.ceremonyStarted || !MomentFramesMod.Settings.msBestow || job.lord.ticksInToil < BestowSpeechTicks || job.target == null) return;
            string key = Hooks.Key("bestow", job);
            if (done.Contains(key)) return;
            var store = PhotoStore.Instance;
            if (store == null || store.HasKey(key)) { done.Add(key); return; }
            IntVec3 spot = job.spotCell.IsValid ? job.spotCell : (job.targetSpot.IsValid ? job.targetSpot.Cell : job.target.PositionHeld);
            var people = Patch_Bestowing.People(job);
            Hooks.AutoFrame(job.Map, spot, people, Hooks.GatherMinSpan, 25f, out Vector3 center, out float span, out float aspect);
            string title = "MF_BestowCeremony".Translate(job.target.LabelShort);
            if (store.RequestAutomaticNow(job.Map, center, "bestow", key, title, "", people, span, aspect, null) != null) done.Add(key);
        }
    }

    [HarmonyPatch(typeof(LordJob_Ritual), nameof(LordJob_Ritual.LordJobTick))]
    internal static class Patch_RitualTick
    {
        private static void Postfix(LordJob_Ritual __instance)
        {
            if (Find.TickManager.TicksGame % MidEvent.CheckEvery != 0) return;
            try { MidEvent.RitualTick(__instance); }
            catch (Exception e) { Log.WarningOnce("[MomentFrames] mid-ritual shot failed: " + e, 0x4D465232); }
        }
    }

    [HarmonyPatch(typeof(LordToil_Party), nameof(LordToil_Party.LordToilTick))]
    internal static class Patch_PartyTick
    {
        private static void Postfix(LordToil_Party __instance)
        {
            if (Find.TickManager.TicksGame % MidEvent.CheckEvery != 0) return;
            if (!(__instance.lord?.LordJob is LordJob_Joinable_Party job)) return;   // the marriage ceremony's after-party uses this toil too: not that one
            try { MidEvent.PartyTick(job); }
            catch (Exception e) { Log.WarningOnce("[MomentFrames] mid-party shot failed: " + e, 0x4D465233); }
        }
    }

    [HarmonyPatch(typeof(LordJob_Joinable_MarriageCeremony), "WeddingSucceeded")]
    internal static class Patch_Wedding
    {
        private static void Postfix(LordJob_Joinable_MarriageCeremony __instance, IntVec3 ___spot)
        {
            try
            {
                if (!Hooks.AutomaticCapture || !MomentFramesMod.Settings.captureWeddings) return;
                Pawn pa = __instance.firstPawn, pb = __instance.secondPawn;
                string a = pa?.LabelShort ?? "?";
                string b = pb?.LabelShort ?? "?";
                // The couple, not the venue: centred between the two, 7 cells tall (about 10 wide). Falls back to the spot if either is not here.
                IntVec3 center = ___spot;
                float? span = null;
                if (pa != null && pb != null && pa.Spawned && pb.Spawned && pa.Map == __instance.Map && pb.Map == __instance.Map)
                {
                    center = new IntVec3((pa.Position.x + pb.Position.x) / 2, 0, (pa.Position.z + pb.Position.z) / 2);
                    span = CoupleSpan;
                }
                var record = Hooks.Request(__instance.Map, center, "wedding", Hooks.Key("wedding", __instance),
                    "MF_Wedding".Translate(a, b), null, Hooks.Owned(__instance), span);
                if (record != null)
                    record.eventSubjectIds = new[] { pa, pb }.Where(p => p != null).Select(p => p.GetUniqueLoadID()).Distinct().ToList();
            }
            catch (Exception e) { Hooks.Fail("wedding", e); }
        }

        public const float CoupleSpan = 7f;
    }

    /// <summary>A colonist brought back (resurrector serum and the like): one photo per pawn, where they stand up.</summary>
    [HarmonyPatch(typeof(ResurrectionUtility), nameof(ResurrectionUtility.TryResurrect))]
    internal static class Patch_Resurrect
    {
        private static void Postfix(Pawn pawn, bool __result)
        {
            try
            {
                if (!__result || !Hooks.AutomaticCapture || !MomentFramesMod.Settings.msResurrect) return;
                if (pawn == null || !pawn.Spawned || pawn.Faction != Faction.OfPlayer || !pawn.RaceProps.Humanlike) return;
                Hooks.Request(pawn.Map, pawn.Position, "resurrect", "resurrect:" + pawn.GetUniqueLoadID(), "MF_Resurrect".Translate(pawn.LabelShort), "", new[] { pawn });
            }
            catch (Exception e) { Hooks.Fail("resurrection", e); }
        }
    }

    /// <summary>Ideology: a relic placed into a reliquary. ThingOwner.NotifyAdded runs for every container add in the game; everything but
    /// one type check happens only when the owner is a reliquary's container. Once per relic (moving it again is not a new moment).</summary>
    [HarmonyPatch(typeof(ThingOwner), "NotifyAdded")]
    internal static class Patch_RelicInstalled
    {
        private static void Postfix(ThingOwner __instance, Thing item)
        {
            if (!(__instance.Owner is CompRelicContainer c)) return;
            try
            {
                if (!Hooks.AutomaticCapture || !MomentFramesMod.Settings.msRelic || item == null) return;
                var holder = c.parent;
                if (holder == null || !holder.Spawned || holder.Faction != Faction.OfPlayer || !CompRelicContainer.IsRelic(item)) return;
                var map = holder.Map;
                var near = map.mapPawns.FreeColonistsSpawned.Where(p => p.Position.DistanceTo(holder.Position) <= 6f).ToList();
                Hooks.Request(map, holder.Position, "reliquary", "reliquary:" + item.GetUniqueLoadID(), "MF_RelicInstalled".Translate(item.LabelCap), "", near);
            }
            catch (Exception e)
            {
                Log.WarningOnce("[MomentFrames] relic hook failed: " + e, 0x4D465231);
            }
        }
    }

    /// <summary>Anomaly: the void monolith advanced a level (any activation path: pawn, auto). One photo per level, wide enough for the whole structure.</summary>
    [HarmonyPatch(typeof(Building_VoidMonolith), nameof(Building_VoidMonolith.Activate))]
    internal static class Patch_Monolith
    {
        private static void Postfix(Building_VoidMonolith __instance, Pawn pawn)
        {
            try
            {
                if (!Hooks.AutomaticCapture || !MomentFramesMod.Settings.msMonolith || __instance == null || !__instance.Spawned) return;
                int level = Find.Anomaly?.Level ?? 0;
                string label = Find.Anomaly?.LevelDef?.monolithLabel;
                if (label.NullOrEmpty()) label = level.ToString();
                Hooks.Request(__instance.Map, __instance.Position, "monolith", "monolith:" + level, "MF_Monolith".Translate(label.CapitalizeFirst()), "", pawn != null ? new[] { pawn } : null, 15f);
            }
            catch (Exception e) { Hooks.Fail("monolith", e); }
        }
    }

    /// <summary>A party ends (its time ran out, or it was called off). One that never got its mid-party shot (the map was not viewed) is
    /// photographed now, framed on the people still there — if it really happened.</summary>
    [HarmonyPatch(typeof(LordJob_Joinable_Party), "ApplyOutcome")]
    internal static class Patch_Party
    {
        private static void Postfix(LordJob_Joinable_Party __instance)
        {
            try
            {
                if (!Hooks.AutomaticCapture || __instance.lord == null || __instance is LordJob_MFMemoryNight) return;
                string key = Hooks.Key("party", __instance);
                if (PhotoStore.Instance?.HasKey(key) != false) return;   // photographed mid-way (or no store)
                bool happened = __instance.lord.ticksInToil >= 1500;   // ApplyOutcome also runs when a party is called off early
                if (!MomentFramesMod.Settings.captureParties || !happened) return;
                var people = Hooks.Owned(__instance).ToList();
                Hooks.AutoFrame(__instance.Map, __instance.Spot, people, Hooks.GatherMinSpan, Hooks.GatherMaxSpan, out Vector3 center, out float span, out float aspect);
                EventDescriptions.Party(__instance, out string title, out string prefix, out string instrument);
                string desc = PhotoDesc.PickSpecific(prefix, "party", __instance.Organizer != null ? new[] { __instance.Organizer } : null, __instance.Map,
                    prefix == "concert" ? title : null, instrument);
                if (prefix == "concert") desc = EventDescriptions.Afterward(title, instrument, desc);
                var record = Hooks.Request(__instance.Map, center, "party", key, title, desc, people, span, aspect);
                if (record != null)
                {
                    record.eventSubtype = prefix;
                    record.generatedDesc = true; record.eventEnded = true;
                }
            }
            catch (Exception e) { Hooks.Fail("party", e); }
        }
    }

    internal enum RitualGroup { Skip, Funeral, Party, Ship, Ritual }

    /// <summary>When a ritual is photographed: most at 60% progress; those with a visible climax at that climax.</summary>
    internal enum RitualMoment { Progress, TreeCelebration, SkyLanterns, Kill, Mutilation, Duel, Speech }

    /// <summary>Sorts ideoligion rituals (vanilla LordJob_Ritual, all DLCs) into the settings groups. Frequent rituals are never photographed.</summary>
    internal static class RitualGroups
    {
        // Pattern or behavior defNames. Ships = photographed by the pilot-console hook instead (the ritual ends by opening the destination map).
        private static readonly HashSet<string> Excluded = new HashSet<string> { "LeaderSpeech", "ThroneSpeech", "SmokeCircle", "BurnCircle", "AnimaTreeLinking", "RoleChange", "ChildBirth" };
        private static readonly HashSet<string> Funerals = new HashSet<string> { "FuneralStandard", "FuneralNoCorpse", "Funeral" };
        private static readonly HashSet<string> Parties = new HashSet<string> { "CelebrationPartyChat", "CelebrationPartyDanceDrum", "CelebrationPartyDanceTech", "CelebrationSkyLanterns", "CelebrationTree", "FeastCannibal", "DateRitual", "DancePartyTech", "PartyDanceDrums" };
        private static readonly HashSet<string> Ships = new HashSet<string> { "GravshipLaunch" };
        private static string userSource;
        private static HashSet<string> userExcluded = new HashSet<string>();

        private static HashSet<string> UserExcluded()
        {
            string src = MomentFramesMod.Settings?.ritualExclude ?? "";
            if (!ReferenceEquals(src, userSource))
            {
                userSource = src;
                userExcluded = new HashSet<string>(src.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
            }
            return userExcluded;
        }

        private static readonly Dictionary<RitualBehaviorDef, RitualMoment> moments = new Dictionary<RitualBehaviorDef, RitualMoment>();

        public static RitualMoment MomentOf(Precept_Ritual ritual)
        {
            if (ritual == null) return RitualMoment.Progress;
            if (ritual.sourcePattern?.defName == "CelebrationTree") return RitualMoment.TreeCelebration;
            return MomentOf(ritual.behavior?.def);
        }

        /// <summary>Read from the behavior's stages, not its name, so modded rituals built from the same pieces follow: a stage whose role duty is
        /// Sacrifice (execution, prisoner and animal sacrifice) / Scarify or Blind / Duel, or a stage that releases sky lanterns.</summary>
        public static RitualMoment MomentOf(RitualBehaviorDef def)
        {
            if (def == null) return RitualMoment.Progress;
            if (moments.TryGetValue(def, out var m)) return m;
            m = def.defName == "CelebrationTree" ? RitualMoment.TreeCelebration : RitualMoment.Progress;
            if (def.stages != null)
                foreach (var stage in def.stages)
                {
                    if (stage.tickActionMaker is RitualStageAction_ReleaseLanterns) m = RitualMoment.SkyLanterns;
                    if (stage.roleBehaviors == null) continue;
                    foreach (var rb in stage.roleBehaviors)
                    {
                        string duty = rb.dutyDef?.defName;
                        if (duty == "Sacrifice") m = RitualMoment.Kill;
                        else if (duty == "Scarify" || duty == "Blind") m = RitualMoment.Mutilation;
                        else if (duty == "Duel") m = RitualMoment.Duel;
                        else if (m == RitualMoment.Progress && IsSpeechDuty(duty)) m = RitualMoment.Speech;   // the speech only wins if nothing louder happens in this ritual
                    }
                }
            moments[def] = m;
            return m;
        }

        /// <summary>Vanilla duties for someone addressing the gathering (Ideology Duties_Rituals, Core Duties_Gatherings).</summary>
        public static bool IsSpeechDuty(string duty) => duty != null && (duty.StartsWith("SpeakOnCell") || duty == "GiveSpeech");

        /// <summary>Who the photo credits. Rituals started by an obligation have no organizer at all (LordJob_Ritual's organizer defaults to null),
        /// so fall back to whoever is speaking, then to the first assigned role holder. Null = credit nobody.</summary>
        public static Pawn HostOf(LordJob_Ritual job)
        {
            if (job == null) return null;
            if (job.Organizer != null && job.Organizer.Spawned) return job.Organizer;
            var owned = job.lord?.ownedPawns;
            if (owned != null)
                foreach (var p in owned)
                    if (IsSpeechDuty(p?.mindState?.duty?.def?.defName)) return p;
            var roles = job.Ritual?.behavior?.def?.roles;
            if (roles != null && job.assignments != null)
                foreach (var r in roles)
                {
                    if (r?.id == null) continue;
                    var p = job.assignments.FirstAssignedPawn(r.id);
                    if (p != null) return p;
                }
            return null;
        }

        public static RitualGroup Classify(Precept_Ritual ritual)
        {
            if (ritual == null) return RitualGroup.Ritual;
            string pattern = ritual.sourcePattern?.defName ?? "";
            string behavior = ritual.behavior?.def?.defName ?? "";
            string worker = ritual.behavior?.GetType().Name ?? "";
            var user = UserExcluded();
            if (user.Contains(pattern) || user.Contains(behavior) || user.Contains(ritual.def.defName)) return RitualGroup.Skip;
            if (Ships.Contains(pattern) || Ships.Contains(behavior) || worker == "RitualBehaviorWorker_GravshipLaunch") return RitualGroup.Ship;
            if (Excluded.Contains(pattern) || Excluded.Contains(behavior) || worker.Contains("Speech") || worker.Contains("AnimaLinking") || worker.Contains("ChildBirth")) return RitualGroup.Skip;
            if (Funerals.Contains(pattern) || Funerals.Contains(behavior) || worker.Contains("Funeral")) return RitualGroup.Funeral;
            if (Parties.Contains(pattern) || Parties.Contains(behavior) || worker.Contains("Party") || pattern.Contains("Celebration") || behavior.Contains("Celebration")) return RitualGroup.Party;
            return RitualGroup.Ritual;
        }

        /// <summary>The role-change ceremony (a colonist takes a new ideoligion role): photographed as the appointment, not as a ritual.</summary>
        public static bool IsRoleChange(Precept_Ritual ritual) => ritual != null && (ritual.sourcePattern?.defName == "RoleChange" || ritual.behavior?.def?.defName == "RoleChange");

        private static string DeceasedName(LordJob_Ritual job)
        {
            Thing t = job.selectedTarget.IsValid ? job.selectedTarget.Thing : null;
            if (t is Corpse c) return c.InnerPawn?.LabelShort;
            if (t is Building_Grave g) return g.Corpse?.InnerPawn?.LabelShort;
            return null;
        }

        /// <summary>Kind, title and sentence for a photo of this ritual, by its settings group. False = not photographed (excluded, switched off,
        /// or covered by another hook). The role-change ceremony is titled as a ceremony for the role changer; the appointment hook re-titles it.</summary>
        public static bool Describe(LordJob_Ritual job, out string kind, out string title, out string desc)
        {
            kind = title = desc = null;
            var s = MomentFramesMod.Settings;
            string label = job.RitualLabel;
            if (IsRoleChange(job.Ritual))
            {
                if (!s.msRole) return false;
                Pawn changer = job.assignments?.FirstAssignedPawn("role_changer");
                kind = "role";
                title = "MF_RoleRitual".Translate(changer?.LabelShort ?? label);
                desc = "";
                return true;
            }
            switch (Classify(job.Ritual))
            {
                case RitualGroup.Skip:
                case RitualGroup.Ship:
                    return false;
                case RitualGroup.Funeral:
                    if (!s.captureFunerals) return false;
                    string who = DeceasedName(job);
                    kind = "funeral";
                    title = "MF_Funeral".Translate(who ?? label);
                    desc = PhotoDesc.Pick("funeral", Hooks.Owned(job).ToList(), job.Map, who ?? label);
                    return true;
                case RitualGroup.Party:
                    if (!s.captureParties) return false;
                    kind = "party";
                    Pawn host = HostOf(job);
                    title = host != null ? "MF_Celebration".Translate(label, host.LabelShort) : "MF_CelebrationPlain".Translate(label);
                    desc = PhotoDesc.PickSpecific(EventDescriptions.RitualPrefix(job.Ritual), "celebration", host != null ? new[] { host } : null, job.Map, label);
                    return true;
                default:
                    if (!s.captureRituals) return false;
                    kind = "ritual";
                    title = "MF_Ritual".Translate(label);
                    desc = PhotoDesc.PickSpecific(EventDescriptions.RitualPrefix(job.Ritual), "ritual", Hooks.Owned(job).ToList(), job.Map, label);
                    return true;
            }
        }
    }

    /// <summary>A ritual ends. One that never got its mid-ritual shot (the map was not viewed in time) is photographed now if it completed,
    /// framed on the people still at the venue.</summary>
    [HarmonyPatch(typeof(LordJob_Ritual), "RitualFinished")]
    internal static class Patch_Ritual
    {
        private static void Postfix(LordJob_Ritual __instance, float progress, bool cancelled, bool __result)
        {
            try
            {
                if (!Hooks.AutomaticCapture) return;
                if (__instance is LordJob_BestowingCeremony) return;   // has its own hooks
                string key = Hooks.Key("ritual", __instance);
                if (PhotoStore.Instance?.HasKey(key) != false) return;   // photographed mid-way (or no store)
                if (!__result || cancelled || __instance.Map == null) return;
                if (!RitualGroups.Describe(__instance, out string kind, out string title, out string desc)) return;
                if (kind == "role") return;   // the appointment hook photographs a role-change ceremony without a mid-ritual shot
                if (EventDescriptions.RitualPrefix(__instance.Ritual) != null) desc = EventDescriptions.Afterward(title, null, desc);
                IntVec3 focus = __instance.selectedTarget.IsValid ? __instance.selectedTarget.Cell : __instance.Spot;
                var people = Hooks.Owned(__instance).ToList();
                Hooks.AutoFrame(__instance.Map, focus, people, Hooks.GatherMinSpan, Hooks.GatherMaxSpan, out Vector3 center, out float span, out float aspect);
                Hooks.Request(__instance.Map, center, kind, key, title, desc, people, span, aspect);
            }
            catch (Exception e) { Hooks.Fail("ritual", e); }
        }
    }

    /// <summary>A colonist takes an ideoligion role. Through the role-change ceremony: the mid-ritual shot of that ceremony (if any) becomes the
    /// appointment photo, else the ceremony is photographed now, wide. Assigned straight from the ideoligion tab (no ceremony): a close portrait
    /// of the person where they are — the moment has no venue.</summary>
    [HarmonyPatch(typeof(Precept_Role), nameof(Precept_Role.Notify_PawnAssigned))]
    internal static class Patch_RoleAssigned
    {
        public const float PortraitSpan = 7f;

        private static void Postfix(Precept_Role __instance, Pawn newPawn)
        {
            try
            {
                if (!Hooks.AutomaticCapture || !MomentFramesMod.Settings.msRole) return;
                if (newPawn == null || !newPawn.Spawned || newPawn.Map == null || newPawn.Faction != Faction.OfPlayer || !newPawn.RaceProps.Humanlike) return;
                if (Find.TickManager.TicksGame < 60) return;   // roles handed out while the game is being set up
                string role = __instance.LabelForPawn(newPawn);
                string title = "MF_Role".Translate(newPawn.LabelShort, role);
                string desc = PhotoDesc.Pick("role", new[] { newPawn }, newPawn.Map, role);
                var store = PhotoStore.Instance;
                if (newPawn.GetLord()?.LordJob is LordJob_Ritual ritual && !(ritual is LordJob_BestowingCeremony) && ritual.Map == newPawn.Map && RitualGroups.IsRoleChange(ritual.Ritual))
                {
                    string rkey = Hooks.Key("ritual", ritual);
                    var people = new List<Pawn>(Hooks.Owned(ritual));
                    if (!people.Contains(newPawn)) people.Insert(0, newPawn);
                    if (store != null && store.Retitle(rkey, title, "role", people, desc)) return;   // the ceremony's mid-way photo becomes the appointment photo
                    IntVec3 focus = ritual.selectedTarget.IsValid ? ritual.selectedTarget.Cell : ritual.Spot;
                    Hooks.AutoFrame(newPawn.Map, focus, people, Hooks.GatherMinSpan, Hooks.GatherMaxSpan, out Vector3 c, out float sp, out float asp);
                    Hooks.Request(newPawn.Map, c, "role", rkey, title, desc, people, sp, asp);
                    return;
                }
                Hooks.Request(newPawn.Map, newPawn.Position, "role", "role:" + __instance.def.defName + ":" + newPawn.GetUniqueLoadID() + ":" + Find.TickManager.TicksGame,
                    title, desc, new[] { newPawn }, PortraitSpan);
            }
            catch (Exception e) { Hooks.Fail("role", e); }
        }
    }

    /// <summary>The bestowing ceremony ends: the photo taken during the speech is re-titled with the title actually granted; a ceremony that
    /// never got one is photographed now.</summary>
    [HarmonyPatch(typeof(LordJob_BestowingCeremony), nameof(LordJob_BestowingCeremony.FinishCeremony))]
    internal static class Patch_Bestowing
    {
        internal static List<Pawn> People(LordJob_BestowingCeremony job)
        {
            var people = new List<Pawn>(Hooks.Owned(job));
            if (job.target != null && !people.Contains(job.target)) people.Insert(0, job.target);
            if (job.bestower != null && !people.Contains(job.bestower)) people.Add(job.bestower);
            return people;
        }

        private static void Postfix(LordJob_BestowingCeremony __instance)
        {
            try
            {
                if (!Hooks.Playing) return;
                Pawn target = __instance.target;
                if (target == null) return;
                Map map = __instance.Map ?? target.MapHeld;
                if (map == null) return;
                string key = Hooks.Key("bestow", __instance);
                string titleLabel = target.royalty?.MostSeniorTitle?.def?.GetLabelFor(target).CapitalizeFirst() ?? "";
                string title = "MF_Bestow".Translate(target.LabelShort, titleLabel);
                var people = People(__instance);
                string desc = PhotoDesc.Pick("bestow", new[] { target }, map, titleLabel);
                var store = PhotoStore.Instance;
                if (store != null && store.Retitle(key, title, "bestow", people, desc)) return;
                if (!Hooks.AutomaticCapture || !MomentFramesMod.Settings.msBestow) return;
                IntVec3 spot = __instance.spotCell.IsValid ? __instance.spotCell : (__instance.targetSpot.IsValid ? __instance.targetSpot.Cell : target.PositionHeld);
                Hooks.AutoFrame(map, spot, people, Hooks.GatherMinSpan, 25f, out Vector3 center, out float span, out float aspect);
                Hooks.Request(map, center, "bestow", key, title, desc, people, span, aspect);
            }
            catch (Exception e) { Hooks.Fail("bestowing", e); }
        }
    }

    internal static class Gravships
    {
        /// <summary>Bounding cells of the ship (all connected substructure, or the engine itself), padded by two cells.</summary>
        public static CellRect Footprint(Building_GravEngine engine, Map map)
        {
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (IntVec3 c in engine.AllConnectedSubstructure)
            {
                if (c.x < minX) minX = c.x; if (c.z < minZ) minZ = c.z; if (c.x > maxX) maxX = c.x; if (c.z > maxZ) maxZ = c.z;
            }
            CellRect rect = minX <= maxX ? CellRect.FromLimits(minX, minZ, maxX, maxZ) : engine.OccupiedRect();
            return rect.ExpandedBy(2).ClipInsideMap(map);
        }
    }

    [HarmonyPatch(typeof(LetterStack), "ReceiveLetter", new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    internal static class Patch_BabyLetter
    {
        private static void Postfix(Letter let)
        {
            try
            {
                if (!Hooks.Playing) return;
                if (!(let is ChoiceLetter_BabyBirth)) return;
                GlobalTargetInfo t = let.lookTargets != null ? let.lookTargets.TryGetPrimaryTarget() : GlobalTargetInfo.Invalid;
                if (!t.IsValid || t.Map == null) return;
                string who = t.Thing is Pawn p ? p.LabelShort : let.Label.ToString();
                // Key by the baby, not the tick: twins arrive in the same tick.
                string key = "baby:" + (t.Thing is Pawn bp ? bp.GetUniqueLoadID() : Find.TickManager.TicksGame.ToString());
                var child = t.Thing as Pawn;
                string kind = BirthSources.Kind(child);
                BirthSources.Remember(child, kind);
                if (!Hooks.AutomaticCapture || !MomentFramesMod.Settings.msBaby) return;
                Hooks.Request(t.Map, t.Cell, kind, key,
                    (kind == "baby" ? "MF_Baby" : kind == "adoption" ? "MF_Adoption" : "MF_BabyArrival").Translate(who),
                    null, child != null ? new[] { child } : null);
            }
            catch (Exception e) { Hooks.Fail("birth", e); }
        }
    }

    [HarmonyPatch(typeof(Pawn), "Kill")]
    internal static class Patch_PawnKill
    {
        private class State
        {
            public Map map;
            public IntVec3 pos;
            public bool colonist;
            public string boss;
        }

        private static readonly HashSet<string> Bosses = new HashSet<string> { "Mech_Diabolus", "Mech_UltraDiabolus", "Mech_Warqueen", "Mech_Apocriton" };
        private const int BossBlastTicks = 120;   // the death explosion and its flash are gone by then (2 seconds)

        private static void Prefix(Pawn __instance, out State __state)
        {
            __state = null;
            try
            {
                if (!Hooks.AutomaticCapture || __instance == null || !__instance.Spawned) return;
                var s = new State { map = __instance.Map, pos = __instance.Position };
                s.colonist = __instance.IsColonist && __instance.RaceProps.Humanlike;
                string kind = __instance.kindDef?.defName;
                if (kind != null && Bosses.Contains(kind) && __instance.HostileTo(Faction.OfPlayer)) s.boss = __instance.LabelShort;
                if (s.colonist || s.boss != null) __state = s;
            }
            catch { __state = null; }
        }

        private static void Postfix(Pawn __instance, State __state)
        {
            try
            {
                if (__state == null || !Hooks.AutomaticCapture) return;
                var settings = MomentFramesMod.Settings;
                if (__state.colonist && settings.msDeath)
                    Hooks.Request(__state.map, __state.pos, "death", "death:" + __instance.GetUniqueLoadID(), "MF_Death".Translate(__instance.LabelShort), "", new[] { __instance });
                if (__state.boss != null && settings.msMechBoss)
                {
                    // The boss dies in an explosion that whites out the frame, and the picture is about who brought it down: wait for the blast to
                    // clear, and frame the colonists who were standing around it rather than the wreck alone.
                    var fighters = __state.map?.mapPawns?.FreeColonistsSpawned?
                        .Where(p => p != null && p.Spawned && !p.Dead && p.Position.InHorDistOf(__state.pos, Hooks.GatherReach)).ToList();
                    Hooks.AutoFrame(__state.map, __state.pos, fighters, Hooks.GatherMinSpan, Hooks.GatherMaxSpan, out Vector3 bc, out float bspan, out float baspect);
                    Hooks.Request(__state.map, bc, "mechboss", "mechboss:" + __instance.GetUniqueLoadID(), "MF_MechBoss".Translate(__state.boss), "",
                        fighters, bspan, baspect, BossBlastTicks);
                }
            }
            catch (Exception e) { Hooks.Fail("death", e); }
        }
    }

    /// <summary>A botched operation: the big-damage failure outcomes only (a lesser slip is not a story, and a patient who dies on the table is
    /// already covered by the death photo).</summary>
    [HarmonyPatch(typeof(SurgeryOutcome_Failure), "Apply")]
    internal static class Patch_SurgeryFail
    {
        public const int BadEnough = 30;   // totalDamage of the outcome; vanilla's catastrophic failures carry 65

        private static void Postfix(SurgeryOutcome_Failure __instance, Pawn surgeon, Pawn patient, bool __result)
        {
            try
            {
                if (!__result || !Hooks.AutomaticCapture || !MomentFramesMod.Settings.msPersonal) return;
                if (patient == null || patient.Dead || !patient.Spawned || !patient.IsColonist) return;
                if (__instance.totalDamage < BadEnough) return;
                var store = PhotoStore.Instance;
                if (store == null) return;
                string key = "surgeryfail:" + patient.ThingID + ":" + Find.TickManager.TicksGame;
                var people = new List<Pawn> { patient };
                if (surgeon != null && surgeon != patient && surgeon.Spawned) people.Add(surgeon);
                Hooks.AutoFrame(patient.Map, patient.Position, people, 9f, 15f, out Vector3 c, out float span, out float aspect);
                store.RequestAutomatic(patient.Map, c, Historic.KindSurgeryFail, key, "MF_SurgeryFail".Translate(patient.LabelShort), "", people, span, aspect);
            }
            catch (Exception e) { Log.Warning("[MomentFrames] surgery hook failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(RecruitUtility), "Recruit")]
    internal static class Patch_Recruit
    {
        private static void Postfix(Pawn pawn, Faction faction, Pawn recruiter)
        {
            try
            {
                if (faction == Faction.OfPlayer) JoinCapture.Completed(pawn, recruiter);
            }
            catch (Exception e) { Hooks.Fail("recruit", e); }
        }
    }

    [HarmonyPatch(typeof(QualityUtility), "SendCraftNotification")]
    internal static class Patch_Legendary
    {
        private static void Postfix(Thing thing, Pawn worker)
        {
            try
            {
                if (!Hooks.AutomaticCapture || !MomentFramesMod.Settings.msLegendary) return;
                if (thing == null || worker == null || worker.Faction != Faction.OfPlayer || !worker.Spawned) return;
                var q = thing.TryGetComp<CompQuality>();
                if (q == null || q.Quality != QualityCategory.Legendary) return;
                Hooks.Request(worker.Map, worker.Position, "legendary", "legendary:" + thing.GetUniqueLoadID(), "MF_Legendary".Translate(thing.LabelCap), "", new[] { worker });
            }
            catch (Exception e) { Hooks.Fail("legendary", e); }
        }
    }

    [HarmonyPatch(typeof(ShipCountdown), "InitiateCountdown", new[] { typeof(Building) })]
    internal static class Patch_ShipLaunch
    {
        private static void Postfix(Building launchingShipRoot)
        {
            try
            {
                if (!Hooks.AutomaticCapture || !MomentFramesMod.Settings.msShip || launchingShipRoot == null || !launchingShipRoot.Spawned) return;
                if (!Hooks.Once("ship:launch")) return;
                Hooks.Request(launchingShipRoot.Map, launchingShipRoot.Position, "ship", "ship:launch", "MF_ShipLaunch".Translate(), "", launchingShipRoot.Map.mapPawns.FreeColonistsSpawned);
            }
            catch (Exception e) { Hooks.Fail("ship launch", e); }
        }
    }
}
