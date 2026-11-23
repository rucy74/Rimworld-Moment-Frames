using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Weather and sky moments (user, 2026-10-02: "재해랑 하늘 현상 두 개 넣어줘"). A disaster (toxic fallout, volcanic winter, a cold
    /// snap or heat wave, a flashstorm, a drought ...) is photographed while it lasts, by day, wide over the colony (user: "재해 끝날 때가
    /// 아니라 재해 중에 찍어야 그 재해가 나오는 거 아니야?" — after it ends, its sky and haze are gone from the picture). A sky event (an
    /// aurora, an eclipse, bioluminescent spores) is photographed at the hour it shows, with the colonists out under the open sky. Like
    /// fights: the first of each kind always, after that at most one a season per group.</summary>
    public static class Conditions
    {
        public const string KindDisaster = "disaster", KindSky = "sky";
        private const int CheckEvery = 250;
        private const int Settle = 2 * GenDate.TicksPerHour;   // a disaster or sky event is shot once it has been on for a while

        private static readonly HashSet<string> Disasters = new HashSet<string>
        {
            "ToxicFallout", "VolcanicWinter", "ColdSnap", "HeatWave", "Flashstorm",
            "NoxiousHaze",                                                    // Biotech
            "BloodRain", "DeathPall", "GrayPall", "UnnaturalDarkness",       // Anomaly
            "Drought", "DeepFreeze", "LavaFlow", "VolcanicAsh"               // Odyssey
        };

        /// <summary>Sky events and the hours they show: an aurora and glowing spores at night, an eclipse by day.</summary>
        private static readonly Dictionary<string, bool> Sky = new Dictionary<string, bool>
        {
            { "Aurora", true }, { "BioluminescentSpores", true }, { "Eclipse", false }
        };

        private static int lastCheck = -99999;

        /// <summary>Probe only: the hour to use instead of the map's (-1 = the map's), so the hours a sky event shows can be checked.</summary>
        internal static int ProbeHour = -1;

        public static void Reset() => lastCheck = -99999;

        internal static bool IsDisaster(GameConditionDef def) => def != null && Disasters.Contains(def.defName);

        /// <summary>The name used in the title: our own for the few whose game label reads oddly there, else the game's.</summary>
        internal static string NameOf(GameConditionDef def)
        {
            string key = "MF_Cond_" + def.defName;
            return key.CanTranslate() ? key.Translate().ToString() : def.label;
        }

        /// <summary>The first of each kind always; after that one a season for the whole group (disasters, or the sky).</summary>
        internal static bool Takes(PhotoStore store, string kind, GameConditionDef def, int tick)
        {
            if (!store.HasMark(kind + ":first:" + def.defName)) return true;
            int last = kind == KindDisaster ? store.LastDisasterTick : store.LastSkyTick;
            return last < 0 || tick - last >= Battles.SeasonTicks;
        }

        private static void Taken(PhotoStore store, string kind, GameConditionDef def, int tick)
        {
            store.AddMark(kind + ":first:" + def.defName);
            if (kind == KindDisaster) store.LastDisasterTick = tick;
            else store.LastSkyTick = tick;
        }

        internal enum CaptureState { Ready, Wait, Ended }

        private static bool Identity(PhotoRecord rec, out string defName, out int startTick)
        {
            defName = rec.requiredConditionDef;
            startTick = rec.requiredConditionStartTick;
            if (!defName.NullOrEmpty() && startTick >= 0) return true;
            // Pending records from older saves already carry the identity in their event key.
            // Read it without rewriting the original record or any saved once-only marks.
            var parts = rec.eventKey?.Split(':');
            if (parts == null || parts.Length != 3 || parts[0] != rec.kind || !int.TryParse(parts[2], out startTick)) return false;
            defName = parts[1];
            return !defName.NullOrEmpty();
        }

        private static bool HourAllows(string kind, string defName, int hour)
        {
            if (kind == KindDisaster) return hour >= 7 && hour <= 18;
            return Sky.TryGetValue(defName, out bool night) && (night ? hour <= 4 || hour >= 20 : hour >= 8 && hour <= 17);
        }

        /// <summary>Recheck the exact activation and its visible hour at capture time, including after a load.</summary>
        internal static CaptureState CaptureStatus(PhotoRecord rec, Map map)
        {
            if (rec.kind != KindSky && rec.kind != KindDisaster) return CaptureState.Ready;
            if (map == null || !Identity(rec, out string defName, out int startTick) ||
                !ActiveOn(map).Any(c => c.def.defName == defName && c.startTick == startTick)) return CaptureState.Ended;
            // Another queued image may have succeeded since this one was requested.
            // Older requests already spent their first/cooldown at booking time: honour that reservation.
            if (!rec.requiredConditionDef.NullOrEmpty())
            {
                var def = DefDatabase<GameConditionDef>.GetNamedSilentFail(defName);
                var store = PhotoStore.Instance;
                if (def == null || store != null && !Takes(store, rec.kind, def, Find.TickManager.TicksGame)) return CaptureState.Ended;
            }
            int hour = ProbeHour >= 0 ? ProbeHour : GenLocalDate.HourOfDay(map);
            return HourAllows(rec.kind, defName, hour) ? CaptureState.Ready : CaptureState.Wait;
        }

        private static void Guard(PhotoStore store, PhotoRecord rec, GameCondition condition)
        {
            if (rec == null) return;
            rec.requiredConditionDef = condition.def.defName;
            rec.requiredConditionStartTick = condition.startTick;
            store.SetCaptureDeadline(rec, Find.TickManager.TicksGame + GenDate.TicksPerDay);
        }

        /// <summary>A pending image does not consume the first photograph or the seasonal cooldown.</summary>
        internal static void NotifySaved(PhotoStore store, PhotoRecord rec)
        {
            if (rec.kind != KindSky && rec.kind != KindDisaster || !Identity(rec, out string defName, out int ignored)) return;
            var def = DefDatabase<GameConditionDef>.GetNamedSilentFail(defName);
            if (def != null) Taken(store, rec.kind, def, Find.TickManager.TicksGame);
        }

        /// <summary>The home map a condition was on (the one in view when it covered several).</summary>
        private static Map HomeOf(GameCondition c)
        {
            var maps = c?.AffectedMaps;
            if (maps == null || maps.Count == 0) return null;
            var current = Find.CurrentMap;
            if (current != null && current.IsPlayerHome && maps.Contains(current)) return current;
            return maps.FirstOrDefault(m => m != null && m.IsPlayerHome);
        }

        /// <summary>On the store's short poll: a disaster or sky event that has been on a while, at an hour it shows. A disaster by day, wide
        /// over the colony with everyone there; a sky event with the colonists out under the open sky.</summary>
        internal static void Poll(PhotoStore store, int tick)
        {
            var s = MomentFramesMod.Settings;
            if (store == null || !SettingsCapturePolicy.Automatic(s) || (!s.msSky && !s.msDisaster) || tick - lastCheck < CheckEvery && tick >= lastCheck) return;
            lastCheck = tick;
            foreach (var map in Find.Maps)
            {
                if (map == null || !map.IsPlayerHome) continue;
                foreach (var c in ActiveOn(map).ToList())
                {
                    int hour = ProbeHour >= 0 ? ProbeHour : GenLocalDate.HourOfDay(map);
                    if (s.msDisaster && IsDisaster(c.def))
                    {
                        string dkey = "disaster:" + c.def.defName + ":" + c.startTick;
                        if (store.HasMark(dkey) || c.TicksPassed < Settle || hour < 7 || hour > 18) continue;   // by day: its colours show
                        store.AddMark(dkey);   // looked at once: taken now, or skipped for the season
                        if (!Takes(store, KindDisaster, c.def, tick)) continue;
                        var shot = store.RequestAutomatic(map, Historic.ColonyCenter(map), KindDisaster, dkey, "MF_DisasterDuring".Translate(NameOf(c.def)), "",
                            map.mapPawns.FreeColonistsSpawned.ToList(), Historic.WideSpan);
                        Guard(store, shot, c);
                        continue;
                    }
                    if (!s.msSky || !Sky.TryGetValue(c.def.defName, out bool night)) continue;
                    string key = "sky:" + c.def.defName + ":" + c.startTick;
                    if (store.HasMark(key) || c.TicksPassed < Settle) continue;
                    if (night ? hour > 4 && hour < 20 : hour < 8 || hour > 17) continue;
                    var outside = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Position.Roofed(map)).ToList();
                    if (outside.Count == 0) continue;
                    store.AddMark(key);   // looked at once: taken now, or skipped for the season
                    if (!Takes(store, KindSky, c.def, tick)) continue;
                    // Around the biggest group outside: the colonist with the most others near, and those near them.
                    Pawn hub = outside.OrderByDescending(p => outside.Count(o => o.Position.InHorDistOf(p.Position, 12f))).First();
                    var people = outside.Where(p => p.Position.InHorDistOf(hub.Position, 12f)).ToList();
                    Hooks.AutoFrame(map, hub.Position, people, Hooks.GatherMinSpan, Hooks.GatherMaxSpan, out Vector3 center, out float span, out float aspect);
                    var rec = store.RequestAutomatic(map, center, KindSky, key, "MF_SkyUnder".Translate(NameOf(c.def)), "", people, span, aspect);
                    Guard(store, rec, c);
                }
            }
        }

        /// <summary>The conditions on this map and the world's (an aurora can be either).</summary>
        internal static IEnumerable<GameCondition> ActiveOn(Map map)
        {
            if (map == null) yield break;
            if (map.gameConditionManager != null)
                foreach (var c in map.gameConditionManager.ActiveConditions)
                    if (c?.def != null && !c.Expired && c.CanApplyOnMap(map)) yield return c;
            var world = Find.World?.gameConditionManager;
            if (world != null)
                foreach (var c in world.ActiveConditions)
                    if (c?.def != null && !c.Expired && c.CanApplyOnMap(map)) yield return c;
        }
    }
}
