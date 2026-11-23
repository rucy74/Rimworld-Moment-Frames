using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace MomentFrames
{
    /// <summary>A fight the colony won (user, 2026-10-01: "B로 해 봐. 계절에 한 번 너 말대로 하고"). A big threat letter on a home map (raid,
    /// siege, mech cluster, infestation, manhunter pack ...) opens a fight; once that map's danger has been back to none for an hour, it is
    /// over and its first actual exchange photograph is published. Which fights: the first of each type always; after that only
    /// hard ones (a new injury/downing/death; for non-animal threats also as many enemies as colonists), at most one a season. Setting: off / first of each type /
    /// hard fights (default) / every fight. Observed fights and their original injury baselines survive save/load.</summary>
    public static class Battles
    {
        public const string Kind = "battle";
        public const int ModeOff = 0, ModeFirsts = 1, ModeHard = 2, ModeEvery = 3;
        public const int SeasonTicks = GenDate.TicksPerQuadrum;
        private const int GiveUpTicks = 3 * GenDate.TicksPerDay;   // a threat that never turned into a fight is dropped
        private const int CheckEvery = 250;

        /// <summary>How long the map must stay calm before the fight counts as over. Settable so the probe need not wait an hour.</summary>
        public static int CalmTicks { get; set; } = GenDate.TicksPerHour;

        public class Fight : IExposable
        {
            public Map map;
            public string type, faction;
            public int startTick, calmSince = -1, mostEnemies;
            public bool sawDanger, colonistDowned, colonistInjured;
            internal bool shotAttempted, candidateSaved;
            internal PhotoRecord candidate;
            public List<Pawn> colonists = new List<Pawn>();
            public HashSet<Pawn> enemies = new HashSet<Pawn>();
            internal Dictionary<Pawn, InjuryBaseline> injuries = new Dictionary<Pawn, InjuryBaseline>();
            private List<Pawn> injuryKeys;
            private List<InjuryBaseline> injuryValues;
            public void ExposeData()
            {
                Scribe_References.Look(ref map, "map");
                Scribe_Values.Look(ref type, "type"); Scribe_Values.Look(ref faction, "faction");
                Scribe_Values.Look(ref startTick, "startTick"); Scribe_Values.Look(ref calmSince, "calmSince", -1);
                Scribe_Values.Look(ref mostEnemies, "mostEnemies"); Scribe_Values.Look(ref sawDanger, "sawDanger");
                Scribe_Values.Look(ref colonistDowned, "colonistDowned"); Scribe_Values.Look(ref colonistInjured, "colonistInjured");
                if (Scribe.mode == LoadSaveMode.Saving && candidate?.HasFile == true) candidateSaved = true;
                Scribe_Values.Look(ref shotAttempted, "shotAttempted");
                Scribe_Values.Look(ref candidateSaved, "candidateSaved");
                Scribe_Deep.Look(ref candidate, "firstExchangePhoto");
                Scribe_Collections.Look(ref colonists, "colonists", LookMode.Reference);
                Scribe_Collections.Look(ref enemies, "enemies", LookMode.Reference);
                Scribe_Collections.Look(ref injuries, "injuries", LookMode.Reference, LookMode.Deep, ref injuryKeys, ref injuryValues);
                if (Scribe.mode == LoadSaveMode.PostLoadInit)
                {
                    colonists ??= new List<Pawn>(); enemies ??= new HashSet<Pawn>(); injuries ??= new Dictionary<Pawn, InjuryBaseline>();
                    colonists.RemoveAll(p => p == null); enemies.RemoveWhere(p => p == null);
                    injuries = injuries.Where(pair => pair.Key != null && pair.Value != null).ToDictionary(pair => pair.Key, pair => pair.Value);
                }
            }
        }
        internal class InjuryBaseline : IExposable
        {
            internal float Severity, Bleeding; internal bool Downed, Dead;
            public void ExposeData()
            {
                Scribe_Values.Look(ref Severity, "severity"); Scribe_Values.Look(ref Bleeding, "bleeding");
                Scribe_Values.Look(ref Downed, "downed"); Scribe_Values.Look(ref Dead, "dead");
            }
        }
        private static InjuryBaseline Injury(Pawn pawn)
        {
            float severity = 0f;
            var hediffs = pawn?.health?.hediffSet?.hediffs;
            if (hediffs != null) for (int i = 0; i < hediffs.Count; i++)
                if (hediffs[i] is Hediff_Injury wound) severity += wound.Severity;
            return new InjuryBaseline { Severity = severity, Bleeding = pawn?.health?.hediffSet?.BleedRateTotal ?? 0f,
                Downed = pawn?.Downed == true, Dead = pawn?.Dead == true };
        }

        private static List<Fight> fights = new List<Fight>();
        private static readonly Dictionary<Map, Fight> byMap = new Dictionary<Map, Fight>();
        private static int lastCheck = -99999;

        internal static IReadOnlyList<Fight> Active => fights;

        internal static void ExposeData()
        {
            Scribe_Collections.Look(ref fights, "activeBattles", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                fights ??= new List<Fight>();
                fights.RemoveAll(f => f == null || f.map == null || f.type.NullOrEmpty());
                byMap.Clear(); foreach (var fight in fights) byMap[fight.map] = fight;
                lastCheck = -99999;
            }
        }

        public static void Reset()
        {
            foreach (var fight in fights) if (fight.candidate != null) fight.candidate.captureCancelled = true;
            fights.Clear();
            byMap.Clear();
            lastCheck = -99999;
        }

        internal static string TypeOf(IncidentDef def, IncidentParms parms)
        {
            string n = def?.defName ?? "";
            if (parms?.raidStrategy != null && parms.raidStrategy.defName.IndexOf("Siege", StringComparison.OrdinalIgnoreCase) >= 0) return "siege";
            if (n.IndexOf("MechCluster", StringComparison.OrdinalIgnoreCase) >= 0) return "mechcluster";
            if (n.IndexOf("Infestation", StringComparison.OrdinalIgnoreCase) >= 0) return "infestation";
            if (n.IndexOf("Manhunter", StringComparison.OrdinalIgnoreCase) >= 0) return "animals";
            if (n.IndexOf("Raid", StringComparison.OrdinalIgnoreCase) >= 0) return "raid";
            return "threat";
        }

        /// <summary>A big threat arrived on a home map. A second one while the first is on joins the same fight.</summary>
        internal static void Begin(Map map, string type, string faction)
        {
            var s = MomentFramesMod.Settings;
            if (map == null || !map.IsPlayerHome || !SettingsCapturePolicy.Automatic(s) || s.battleMode == ModeOff || fights.Any(f => f.map == map)) return;
            var fight = new Fight
            {
                map = map,
                type = type,
                faction = faction,
                startTick = Find.TickManager.TicksGame,
                enemies = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer))),
                colonists = map.mapPawns.FreeColonistsSpawned.ToList()
            };
            foreach (var pawn in fight.colonists) fight.injuries[pawn] = Injury(pawn);
            fights.Add(fight);
            byMap[map] = fight;
        }

        /// <summary>Called only after an actual attack, not an order or warmup. O(1) gates; no new tick polling.</summary>
        internal static bool Watching(Map map) => map != null && byMap.TryGetValue(map, out var fight) && !fight.shotAttempted;

        internal static void Attacked(Thing attacker, LocalTargetInfo target, bool targetWasStanding = false, IntVec3? targetCell = null)
        {
            if (byMap.Count == 0 || attacker?.Map == null || !byMap.TryGetValue(attacker.Map, out var fight)
                || fight.shotAttempted || !SettingsCapturePolicy.Automatic(MomentFramesMod.Settings)
                || MomentFramesMod.Settings.battleMode == ModeOff) return;
            Thing other = target.Thing;
            if (other == null || !targetWasStanding && (other.Spawned != true || other.Map != fight.map
                || other is Pawn b && (b.Dead || b.Downed)) || attacker is Pawn a && (a.Dead || a.Downed)) return;
            bool enemyA = attacker is Pawn pa && (fight.enemies.Contains(pa) || pa.HostileTo(Faction.OfPlayer));
            bool enemyB = other is Pawn pb && (fight.enemies.Contains(pb) || pb.HostileTo(Faction.OfPlayer));
            if (!(enemyA && other.Faction == Faction.OfPlayer || enemyB && attacker.Faction == Faction.OfPlayer)) return;
            fight.shotAttempted = true; fight.sawDanger = true;
            if (attacker is Pawn ea && enemyA) fight.enemies.Add(ea);
            if (other is Pawn eb && enemyB) fight.enemies.Add(eb);
            var store = PhotoStore.Instance;
            // An already recorded type in first-only mode cannot qualify later; don't render a disposable image.
            if (store == null || store.HasMark("battle:first:" + fight.type)
                && (MomentFramesMod.Settings.battleMode == ModeFirsts || MomentFramesMod.Settings.battleMode == ModeHard
                    && store.LastBattleTick >= 0 && Find.TickManager.TicksGame - store.LastBattleTick < SeasonTicks)) return;
            BattlePhoto.Capture(store, fight, attacker, other, targetCell);
        }

        private static void Remove(PhotoStore store, int index)
        {
            var fight = fights[index]; if (fight.map != null) byMap.Remove(fight.map); fights.RemoveAt(index);
            BattlePhoto.Discard(store, fight);
        }

        /// <summary>On the store's short poll; the work runs every few seconds and only while a fight is on.</summary>
        internal static void Poll(PhotoStore store, int tick)
        {
            if (!SettingsCapturePolicy.Automatic(MomentFramesMod.Settings) || MomentFramesMod.Settings.battleMode == ModeOff)
            { for (int i = fights.Count - 1; i >= 0; i--) Remove(store, i); return; }
            if (fights.Count == 0 || tick - lastCheck < CheckEvery && tick >= lastCheck) return;
            lastCheck = tick;
            for (int i = fights.Count - 1; i >= 0; i--)
            {
                var f = fights[i];
                if (f.map == null || !Find.Maps.Contains(f.map) || !f.map.IsPlayerHome || tick - f.startTick > GiveUpTicks)
                {
                    Remove(store, i);
                    continue;
                }
                int enemies = 0;
                foreach (var p in f.map.mapPawns.AllPawnsSpawned)
                    if (p.HostileTo(Faction.OfPlayer)) { f.enemies.Add(p); if (!p.Dead && !p.Downed) enemies++; }
                if (enemies > f.mostEnemies) f.mostEnemies = enemies;
                foreach (var pawn in f.colonists)
                {
                    if (pawn == null || !f.injuries.TryGetValue(pawn, out var baseline)) continue;
                    var state = Injury(pawn);
                    if ((!baseline.Downed && state.Downed) || (!baseline.Dead && state.Dead)) f.colonistDowned = true;
                    if (RecordEventPolicy.NewInjury(baseline.Severity, state.Severity, baseline.Bleeding, state.Bleeding)) f.colonistInjured = true;
                }
                if (f.map.dangerWatcher.DangerRating != StoryDanger.None)
                {
                    f.sawDanger = true;
                    f.calmSince = -1;
                    continue;
                }
                if (f.calmSince < 0) f.calmSince = tick;
                if (tick - f.calmSince < CalmTicks) continue;
                if (f.sawDanger) Finish(store, f, tick);
                Remove(store, i);
            }
        }

        /// <summary>After the first animal fight, headcount alone is insufficient. Pre-existing injuries/downing are baselined.</summary>
        internal static bool Hard(Fight f) => RecordEventPolicy.HardFight(f.type, f.colonistDowned || f.colonistInjured
            || f.colonists.Any(p => p != null && p.Dead && (!f.injuries.TryGetValue(p, out var before) || !before.Dead)), f.mostEnemies, f.colonists.Count);

        /// <summary>Whether this fight gets its photo (the rules above); exposed for the probe.</summary>
        internal static bool Takes(PhotoStore store, Fight f, int tick)
        {
            int mode = MomentFramesMod.Settings?.battleMode ?? ModeHard;
            if (!SettingsCapturePolicy.Automatic(MomentFramesMod.Settings) || mode == ModeOff) return false;
            if (!store.HasMark("battle:first:" + f.type)) return true;
            if (mode == ModeEvery) return true;
            return mode == ModeHard && Hard(f) && (store.LastBattleTick < 0 || tick - store.LastBattleTick >= SeasonTicks);
        }

        internal static PhotoRecord Finish(PhotoStore store, Fight f, int tick)
        {
            if (store == null || !Takes(store, f, tick)) return null;
            Map map = f.map;
            string title = CaptionText.Clean(("MF_Battle_" + f.type).Translate(f.faction.NullOrEmpty() ? "MF_Battle_Enemy".Translate().ToString() : f.faction).ToString());
            string key = "battle:" + f.startTick + ":" + map.uniqueID;
            PhotoRecord rec = BattlePhoto.Publish(store, f, title)
                ?? store.RecordEvent(map, Kind, key, title, "MF_BattleNoExchange".Translate());
            if (rec == null) return null;
            store.AddMark("battle:first:" + f.type);
            store.LastBattleTick = tick;
            return rec;
        }
    }

    [HarmonyPatch(typeof(Pawn_MindState), "Notify_AttackedTarget")]
    internal static class Patch_BattleAttack
    {
        private static void Postfix(Pawn_MindState __instance, LocalTargetInfo target)
        {
            try { Battles.Attacked(__instance.pawn, target); }
            catch (Exception e) { Hooks.Fail("battle exchange", e); }
        }
    }

    // Melee reports a miss as false and may kill/down its target before Notify_AttackedTarget.
    // Snapshot eligibility before the swing, then handle either result after execution.
    [HarmonyPatch(typeof(Verb_MeleeAttack), "TryCastShot")]
    internal static class Patch_BattleMelee
    {
        private static void Prefix(Verb_MeleeAttack __instance, out IntVec3? __state)
        {
            __state = null;
            var pawn = __instance.CasterPawn;
            var target = __instance.CurrentTarget.Thing;
            if (pawn?.Spawned == true && Battles.Watching(pawn.Map) && !pawn.stances.FullBodyBusy
                && target?.Spawned == true && target.Map == pawn.Map && !(target is Pawn p && (p.Dead || p.Downed)))
                __state = target.Position;
        }
        private static void Postfix(Verb_MeleeAttack __instance, IntVec3? __state)
        {
            if (!__state.HasValue) return;
            try { Battles.Attacked(__instance.CasterPawn, __instance.CurrentTarget, true, __state); }
            catch (Exception e) { Hooks.Fail("battle melee", e); }
        }
    }

    // Also covers turret fire. Intended target is used even when the projectile misses.
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch), new[] { typeof(Thing), typeof(Vector3),
        typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef) })]
    internal static class Patch_BattleProjectile
    {
        private static void Postfix(Thing launcher, LocalTargetInfo intendedTarget)
        {
            try { Battles.Attacked(launcher, intendedTarget); }
            catch (Exception e) { Hooks.Fail("battle projectile", e); }
        }
    }

    /// <summary>Every incident letter goes through here; a big threat on a home map opens a fight.</summary>
    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.SendIncidentLetter))]
    internal static class Patch_BattleLetter
    {
        private static void Postfix(LetterDef baseLetterDef, IncidentParms parms, IncidentDef def)
        {
            try
            {
                if (baseLetterDef != LetterDefOf.ThreatBig || !Hooks.Playing || !(parms?.target is Map map)) return;
                Battles.Begin(map, Battles.TypeOf(def, parms), parms.faction?.Name);
            }
            catch (Exception e) { Hooks.Fail("battle letter", e); }
        }
    }
}
