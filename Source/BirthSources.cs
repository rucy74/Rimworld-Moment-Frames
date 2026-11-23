using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace MomentFrames
{
    internal static class BirthSources
    {
        internal static Pawn Adopting;
        internal static int BirthDepth;
        internal static string Kind(Pawn pawn) => pawn == Adopting ? "adoption" : BirthDepth > 0 ? "baby" : "babyarrival";
        internal static void Remember(Pawn pawn, string kind)
        {
            var store = PhotoStore.Instance;
            if (store == null || pawn == null || pawn.Faction != Faction.OfPlayer || !pawn.RaceProps.Humanlike || !Hooks.Playing) return;
            string key = "life-origin:" + pawn.GetUniqueLoadID();
            if (!store.AddMark(key)) return;
            store.KnownColonists?.Add(pawn.GetUniqueLoadID());
            if (MomentFramesMod.Settings.lifeEvents)
                LifeEvents.Record(kind == "baby" ? LifeEvents.Born : kind == "adoption" ? "adopted" : LifeEvents.Joined,
                    new[] { pawn }, null, null, pawn.MapHeld);
        }
    }

    [HarmonyPatch(typeof(Designator_Adopt), nameof(Designator_Adopt.DesignateThing))]
    internal static class BirthSourceAdopt
    {
        private static void Prefix(Thing t, out Pawn __state) { __state = BirthSources.Adopting; BirthSources.Adopting = t as Pawn; }
        private static Exception Finalizer(Exception __exception, Pawn __state) { BirthSources.Adopting = __state; return __exception; }
    }

    // PregnancyUtility's static initializer indexes DefOf relations. Patching it in
    // Mod's constructor runs that initializer before DefOf resolution; wait for defs.
    [StaticConstructorOnStartup]
    internal static class BirthSourceDelivery
    {
        static BirthSourceDelivery()
        {
            if (!ModsConfig.BiotechActive) return;
            new Harmony("rucy74.momentframes.birth-source").Patch(
                AccessTools.Method(typeof(PregnancyUtility), nameof(PregnancyUtility.ApplyBirthOutcome)),
                prefix: new HarmonyMethod(typeof(BirthSourceDelivery), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(BirthSourceDelivery), nameof(Postfix)),
                finalizer: new HarmonyMethod(typeof(BirthSourceDelivery), nameof(Finalizer)));
        }
        private static void Prefix() { BirthSources.BirthDepth++; }
        private static void Postfix(Thing __result)
        {
            // Delivery has already finished. Only our observation/recording belongs
            // in this boundary; the finalizer still returns the game's exception.
            try
            {
                if (!(__result is Pawn born) || born.Dead || born.Faction != Faction.OfPlayer || !Hooks.Playing) return;
                BirthSources.Remember(born, "baby");
                string key = "baby:" + born.GetUniqueLoadID();
                if (born.Spawned && MomentFramesMod.Settings.msBaby && PhotoStore.Instance?.HasKey(key) == false)
                    Hooks.Request(born.Map, born.Position, "baby", key, "MF_Baby".Translate(born.LabelShort), null, new[] { born });
            }
            catch (Exception e)
            {
                // Diagnostics are optional too; they must not fail a successful birth.
                try { Hooks.Fail("birth delivery", e); }
                catch { }
            }
        }
        private static Exception Finalizer(Exception __exception) { BirthSources.BirthDepth--; return __exception; }
    }
}
