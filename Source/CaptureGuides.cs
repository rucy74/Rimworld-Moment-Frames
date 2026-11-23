using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // MapUpdate submits silhouettes before the photo camera changes zoom. Suppress
    // them in the prepared frame so zoomed-out highlights cannot cover a close-up.
    // Preferences and ordinary map rendering retain their original behavior.
    [HarmonyPatch(typeof(SilhouetteUtility), nameof(SilhouetteUtility.ShouldDrawSilhouette))]
    internal static class CaptureSilhouetteGuides
    {
        private static bool Prefix(ref bool __result)
        {
            if (!PhotoCapture.HideGuides) return true;
            __result = false;
            return false;
        }
    }

    // World meshes are submitted before Camera.Render. Suppress only their capture frame;
    // thought bubbles (including the user's allowed hand icon) keep the normal renderer.
    [HarmonyPatch(typeof(PlaceWorker_SpectatorPreview), "DrawSpectatorPreview")]
    internal static class CaptureSpectatorGuides
    {
        internal static int Suppressed;
        private static bool Prefix() { if (!PhotoCapture.HideGuides) return true; Suppressed++; return false; }
    }

    [HarmonyPatch(typeof(SelectionDrawer), nameof(SelectionDrawer.DrawSelectionOverlays))]
    internal static class CaptureSelectionGuides
    {
        internal static int Suppressed;
        private static bool Prefix() { if (!PhotoCapture.HideGuides) return true; Suppressed++; return false; }
    }

    // The map UI uses this white arrow for pointing at a target. Suppress the map overload only;
    // world-map arrows and thought/reaction bubbles keep their own renderers.
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawArrowPointingAt), new[] { typeof(Vector3), typeof(bool) })]
    internal static class CaptureMapTargetArrow
    {
        internal static int Suppressed;
        private static bool Prefix() { if (!PhotoCapture.HideGuides) return true; Suppressed++; return false; }
    }

    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawArrowRotated), new[] { typeof(Vector3), typeof(float), typeof(bool) })]
    internal static class CaptureMapRotatedArrow
    {
        internal static int Suppressed;
        private static bool Prefix() { if (!PhotoCapture.HideGuides) return true; Suppressed++; return false; }
    }
}
