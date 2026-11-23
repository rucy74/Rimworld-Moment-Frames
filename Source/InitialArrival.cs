using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace MomentFrames
{
    [HarmonyPatch(typeof(ScenPart_PlayerPawnsArriveMethod), nameof(ScenPart_PlayerPawnsArriveMethod.GenerateIntoMap))]
    internal static class InitialArrival
    {
        private static void Prefix(Map map)
        {
            var init = Find.GameInitData; var store = PhotoStore.Instance;
            if (init == null || store == null || init.startingAndOptionalPawns == null) return;
            int count = init.startingPawnCount > 0 ? init.startingPawnCount : init.startingAndOptionalPawns.Count;
            store.InitialColonists = init.startingAndOptionalPawns.Take(count).Where(p => p != null).ToList();
        }
    }
}
