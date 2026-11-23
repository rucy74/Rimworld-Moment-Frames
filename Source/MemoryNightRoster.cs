using System.Collections.Generic;
using HarmonyLib;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>Only the viewed map's memory-night lords are indexed. Lord changes invalidate immediately, without delaying audio.</summary>
    internal static class MemoryNightRoster
    {
        private static Game game;
        private static Map map;
        private static bool dirty = true;
        private static readonly List<Lord> nights = new List<Lord>();

        internal static void Invalidate() => dirty = true;
        internal static void Reset() { game = null; map = null; dirty = true; nights.Clear(); }
        internal static List<Lord> OnMap(Map current)
        {
            if (game != Current.Game || map != current || dirty)
            {
                game = Current.Game; map = current; dirty = false; nights.Clear();
                if (current != null)
                    foreach (var lord in current.lordManager.lords)
                        if (lord?.LordJob is LordJob_MFMemoryNight) nights.Add(lord);
            }
            return nights;
        }
    }

    [HarmonyPatch(typeof(LordManager), nameof(LordManager.AddLord))]
    internal static class Patch_MFNightRoster_Add
    {
        private static void Postfix() => MemoryNightRoster.Invalidate();
    }

    [HarmonyPatch(typeof(LordManager), nameof(LordManager.RemoveLord))]
    internal static class Patch_MFNightRoster_Remove
    {
        private static void Postfix() => MemoryNightRoster.Invalidate();
    }
}
