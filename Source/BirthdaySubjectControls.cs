using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace MomentFrames
{
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class BirthdaySubjectControls
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (var gizmo in __result) yield return gizmo;
            var job = Birthday.Active(__instance);
            if (job == null || !job.FixedBirthdaySubject) yield break;
            foreach (var gizmo in job.GetPawnGizmos(__instance)) yield return gizmo;
        }
    }
}
