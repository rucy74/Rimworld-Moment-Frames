using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace MomentFrames
{
    /// <summary>Ending steps that exist only as quest state, not as a thing on a map (the same hooks as Memory album uses for them): the royal
    /// ascent appearing, its shuttle arriving, each move of the colony toward the archonexus, and the mech hive's core switched off. One photo
    /// each, under the "ending steps" switch. The ship countdown, the ascension countdown, reaching the mech hive and standing before the
    /// archonexus core or the void node are polled in Historic.CheckEndings. None of these can be staged by the probe (code review only).</summary>
    internal static class Endings
    {
        internal static bool On => Hooks.AutomaticCapture && MomentFramesMod.Settings != null && MomentFramesMod.Settings.msEnding;

        /// <summary>A wide shot of the colony on the given map (or the player's home map), with everyone there.</summary>
        internal static void Colony(Map map, string key, string title)
        {
            var store = PhotoStore.Instance;
            map = map ?? Find.AnyPlayerHomeMap;
            if (store == null || map == null || !store.AddMark(key)) return;
            store.RequestAutomatic(map, Historic.ColonyCenter(map), Historic.KindEnding, key, title, "", map.mapPawns.FreeColonistsSpawned.ToList(), Historic.WideSpan);
        }
    }

    /// <summary>The royal ascent (Royalty ending) becomes available: the quest is added.</summary>
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Add))]
    internal static class Patch_EndingRoyalAscent
    {
        private static void Postfix(Quest quest)
        {
            try
            {
                if (!Endings.On || quest?.root?.defName != "EndGame_RoyalAscent") return;
                Endings.Colony(null, "ending:royalascent", "MF_EndingRoyalAscent".Translate());
            }
            catch (Exception e) { Hooks.Fail("royal ascent", e); }
        }
    }

    /// <summary>The royal ascent's shuttle has come: a shuttle delay of that quest finishing.</summary>
    [HarmonyPatch(typeof(QuestPart_Delay), "DelayFinished")]
    internal static class Patch_EndingRoyalShuttle
    {
        private static void Postfix(QuestPart_Delay __instance)
        {
            try
            {
                if (!(__instance is QuestPart_ShuttleDelay) || !Endings.On || __instance.quest?.root?.defName != "EndGame_RoyalAscent") return;
                Endings.Colony(Find.CurrentMap != null && Find.CurrentMap.IsPlayerHome ? Find.CurrentMap : null, "ending:royalshuttle", "MF_EndingRoyalShuttle".Translate());
            }
            catch (Exception e) { Hooks.Fail("royal shuttle", e); }
        }
    }

    /// <summary>The colony sets out for the next archonexus cycle (Ideology ending): photographed on the colony it is leaving, as it leaves.
    /// The first move and the second each once.</summary>
    [HarmonyPatch(typeof(QuestPart_NewColony), "InitMoveColony")]
    internal static class Patch_EndingArchonexusMove
    {
        private static void Postfix(QuestPart_NewColony __instance)
        {
            try
            {
                string root = __instance?.quest?.root?.defName;
                if (!Endings.On || root == null || !root.StartsWith("EndGame_ArchonexusVictory", StringComparison.Ordinal)) return;
                var store = PhotoStore.Instance;
                if (store == null) return;
                int n = store.HasMark("ending:archonexusmove1") ? 2 : 1;
                if (n == 2 && store.HasMark("ending:archonexusmove2")) return;
                Map map = Find.CurrentMap != null && Find.CurrentMap.IsPlayerHome ? Find.CurrentMap : null;
                Endings.Colony(map, "ending:archonexusmove" + n, "MF_EndingArchonexusMove".Translate(n));
            }
            catch (Exception e) { Hooks.Fail("archonexus move", e); }
        }
    }

    /// <summary>The mech hive's cerebrex core is switched off (Biotech ending): the core and whoever stands by it.</summary>
    [HarmonyPatch(typeof(CompCerebrexCore), "DeactivateCore")]
    internal static class Patch_EndingCerebrex
    {
        private static void Prefix(CompCerebrexCore __instance)
        {
            try
            {
                var core = __instance?.parent;
                var store = PhotoStore.Instance;
                if (!Endings.On || store == null || core == null || !core.Spawned || !store.AddMark("ending:mechhivedone")) return;
                Historic.Sighting(store, core.Map, core.Position, Historic.KindEnding, "ending:mechhivedone", "MF_EndingMechhiveDone".Translate());
            }
            catch (Exception e) { Hooks.Fail("mech hive core", e); }
        }
    }
}
