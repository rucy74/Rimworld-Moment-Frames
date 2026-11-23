using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // Keep the game's title, close/save behavior and window stack. Only our settings need more room.
    [HarmonyPatch(typeof(Dialog_ModSettings), "get_InitialSize")]
    internal static class SettingsWindowHost
    {
        internal static bool ProbeUseOriginalSize;

        private static void Postfix(Mod ___mod, ref Vector2 __result)
        {
            if (!(___mod is MomentFramesMod) || ProbeUseOriginalSize) return;
            __result = new Vector2(Mathf.Min(1280f, UI.screenWidth - 48f), Mathf.Min(900f, UI.screenHeight - 48f));
        }
    }

    [HarmonyPatch(typeof(Dialog_ModSettings), MethodType.Constructor, new[] { typeof(Mod) })]
    internal static class SettingsWindowResize
    {
        private static void Postfix(Dialog_ModSettings __instance, Mod mod)
        {
            if (mod is MomentFramesMod) __instance.resizeable = true;
        }
    }

    [HarmonyPatch(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.DoWindowContents))]
    internal static class SettingsWindowMinimum
    {
        private static readonly System.Reflection.FieldInfo Resizer = AccessTools.Field(typeof(Window), "resizer");
        private static void Prefix(Dialog_ModSettings __instance, Mod ___mod)
        {
            if (!(___mod is MomentFramesMod)) return;
            var resizer = Resizer?.GetValue(__instance) as WindowResizer;
            if (resizer != null) resizer.minWindowSize = new Vector2(Mathf.Min(760,UI.screenWidth-48),Mathf.Min(680,UI.screenHeight-48));
        }
    }

    [HarmonyPatch(typeof(Window), nameof(Window.OnCancelKeyPressed))]
    internal static class SettingsWindowCancel
    {
        private static readonly AccessTools.FieldRef<Dialog_ModSettings,Mod> ModField = AccessTools.FieldRefAccess<Dialog_ModSettings,Mod>("mod");
        private static bool Prefix(Window __instance)
        {
            if (__instance is Dialog_ModSettings host && ModField(host) is MomentFramesMod mod && mod.TryCancelSettings()) return false;
            return true;
        }
    }

    [HarmonyPatch(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.PreClose))]
    internal static class SettingsWindowRelease
    {
        private static void Postfix(Mod ___mod)
        {
            if (___mod is MomentFramesMod mod) mod.ReleaseSettingsPreview();
        }
    }
}
