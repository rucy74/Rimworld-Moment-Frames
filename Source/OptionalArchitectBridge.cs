using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace MomentFrames
{
    /// <summary>Better Architect separates ordinary tools into its right-hand orders panel.
    /// Keep our existing photo designator in Misc's main grid, without a dependency on that mod.</summary>
    [HarmonyPatch]
    internal static class Patch_MFBetterArchitectPhotoMain
    {
        private const string CategoryDrawerType = "BetterArchitect.ArchitectCategoryTab_DesignationTabOnGUI_Patch";
        private static MethodInfo target;

        private static bool Prepare()
        {
            Type drawer = AccessTools.TypeByName(CategoryDrawerType);
            target = drawer == null ? null : AccessTools.Method(drawer, "SeparateDesignatorsByType",
                new[] { typeof(IEnumerable<Designator>), typeof(DesignationCategoryDef) });
            // A different adapter signature must leave the original menu untouched.
            return target != null && target.IsStatic
                && target.ReturnType == typeof(ValueTuple<List<Designator>, List<Designator>>);
        }

        private static MethodBase TargetMethod() => target;

        private static void Postfix(DesignationCategoryDef category,
            ValueTuple<List<Designator>, List<Designator>> __result)
        {
            // Postfix only: exceptions from the original menu method remain visible.
            try
            {
                if (category?.defName != "Misc" || __result.Item1 == null || __result.Item2 == null) return;
                var main = __result.Item1;
                var orders = __result.Item2;
                if (ReferenceEquals(main, orders)) return;
                for (int i = orders.Count - 1; i >= 0; i--)
                {
                    if (!(orders[i] is Designator_TakePhoto photo)) continue;
                    // Add before removal so our tool stays available if insertion fails.
                    if (!main.Exists(d => d is Designator_TakePhoto)) main.Insert(0, photo);
                    orders.RemoveAt(i);
                }
            }
            catch (Exception error)
            {
                try { Log.WarningOnce("[MomentFrames] photo menu placement unavailable: " + error, 0x4D464152); }
                catch { }
            }
        }
    }
}
