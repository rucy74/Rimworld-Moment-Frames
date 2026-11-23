using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Re-submit the game's portrait body/clothes contract above scenery for posed photos only.
    /// No equipment or mod names are involved. Ordinary map rendering never enters this scope.</summary>
    internal static class PhotoForeground
    {
        [ThreadStatic] internal static bool Queuing;
        internal static bool ProbeDisabled;
        internal static int LastPeople;
        internal static PhotoRecord LastRecord;
        private static readonly MethodInfo drawParms = AccessTools.Method(typeof(PawnRenderer), "GetDrawParms");
        private static readonly MethodInfo defaultFlags = AccessTools.PropertyGetter(typeof(PawnRenderer), "DefaultRenderFlagsNow");

        internal static void Submit(Map map, PhotoRecord record)
        {
            LastPeople = 0; LastRecord = record;
            if (ProbeDisabled || record == null || (record.kind != "group" && record.kind != "birthday" && record.kind != PetPhotos.Kind)) return;
            bool previous = Queuing;
            try
            {
                Queuing = true;
                foreach (var pawn in PhotoCapture.VisiblePawns(map))
                {
                    if (pawn.Dead || !record.Has(pawn) || pawn.RaceProps?.Humanlike != true
                        || !CapturedPhotoRoster.Visible(record, pawn, map, record.center, record.spanCells, record.Aspect)
                        || !PhotoCapture.VisualPosition(pawn, map, out var position)) continue;
                    // Preserve each pawn's world X/Z and relative drawing altitude, including seat adjustments.
                    position.y += 2f;
                    var renderer = pawn.Drawer.renderer;
                    var nativeFlags = (PawnRenderFlags)defaultFlags.Invoke(renderer, null);
                    var flags = nativeFlags | PawnRenderFlags.Cache | PawnRenderFlags.Portrait | PawnRenderFlags.Clothes | PawnRenderFlags.Headgear;
                    var parms = (PawnDrawParms)drawParms.Invoke(renderer, new object[] { position, 0f, pawn.Rotation, RotDrawMode.Fresh, flags });
                    renderer.renderTree.EnsureInitialized(nativeFlags);
                    renderer.renderTree.ParallelPreDraw(parms);
                    renderer.renderTree.Draw(parms);
                    LastPeople++;
                }
            }
            finally { Queuing = previous; }
        }
    }

}
