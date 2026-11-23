using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Per-def frame geometry, set in XML via modExtensions.</summary>
    public class MFFrameProps : DefModExtension
    {
        public float photoWidth = 1.4f;
        public float photoHeight = 0.933f;
        public bool wallHung = true;
        public float wallOffset = 1f;         // wall frames are drawn centred on the wall cell they face
        public float sideScale = 0.72f;      // 1x1 wall frames on east/west walls: the wall column is one cell wide
        public bool northSouthOnly = false;   // wide wall frames only hang on horizontal walls
    }

    /// <summary>Until 2026-09-27 the plain frames were constructed without quality and only separate "fine" ones were crafted with it; now every
    /// frame is crafted at the sculptor's table with quality (user: the furniture tab should not keep growing). CompQuality loads a missing
    /// quality as Awful, so a frame from an older save gets Normal here. Call from ExposeData after base.ExposeData(); true for an old fine frame.</summary>
    public static class FrameQuality
    {
        public static bool FromOldSave(ThingWithComps t)
        {
            bool current = true;
            Scribe_Values.Look(ref current, "mfQuality", false);
            if (Scribe.mode != LoadSaveMode.LoadingVars || current) return false;
            var q = t.GetComp<CompQuality>();
            if (q == null) return false;
            if (Scribe.loader.curXmlParent?["quality"] != null) return true;    // it came with a quality: an old fine frame
            q.SetQuality(QualityCategory.Normal, null);
            return false;
        }
    }

    /// <summary>Old saves name the merged fine frames (things, install blueprints, bills "Make_MF_Fine...", storage filters): each resolves to the
    /// plain def of the same shape. Translations are left alone so an old "fine" label never lands on a plain frame.</summary>
    [HarmonyPatch(typeof(BackCompatibility), nameof(BackCompatibility.BackCompatibleDefName))]
    public static class Patch_FineFramesMerged
    {
        public static void Postfix(Type defType, bool forDefInjections, ref string __result)
        {
            if (forDefInjections || __result == null || !__result.Contains("MF_Fine")) return;
            string plain = __result.Replace("MF_Fine", "MF_");
            if (GenDefDatabase.GetDefSilentFail(defType, plain, false) != null) __result = plain;
        }
    }

    /// <summary>Wall attachment check for every occupied cell (vanilla Placeworker_AttachedToWall only checks the root cell).</summary>
    public class PlaceWorker_MFWallFrame : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            var def = checkingDef as ThingDef;
            if (def == null) return true;
            var props = def.GetModExtension<MFFrameProps>();
            if (props != null && props.northSouthOnly && rot.IsHorizontal) return "MF_NorthSouthOnly".Translate();
            IntVec3 dir = GenAdj.CardinalDirections[rot.AsInt];
            foreach (IntVec3 c in GenAdj.OccupiedRect(loc, rot, def.Size))
            {
                if (!c.InBounds(map)) return false;
                foreach (Thing t in c.GetThingList(map))
                {
                    if (t == thingToIgnore) continue;
                    if (!(GenConstruct.BuiltDefOf(t.def) is ThingDef td) || td.building == null) continue;
                    if (td.Fillage == FillCategory.Full) return false;
                    if (td.building.isAttachment && t.Rotation == rot) return "SomethingPlacedOnThisWall".Translate();
                }
                IntVec3 s = c + dir;
                if (!s.InBounds(map)) return false;
                bool wall = false, blocked = false;
                foreach (Thing t in s.GetThingList(map))
                {
                    if (t == thingToIgnore) continue;
                    if (!(GenConstruct.BuiltDefOf(t.def) is ThingDef td) || td.building == null) continue;
                    if (!td.building.supportsWallAttachments) blocked = true;
                    else if (td.Fillage == FillCategory.Full) wall = true;
                }
                if (!wall) return blocked ? "CannotSupportAttachment".Translate() : "MustPlaceOnWall".Translate();
            }
            return true;
        }
    }

    /// <summary>Shows where the projector's picture will land while it is being placed — the throw is fixed, so without this you only find out
    /// after building it (user, 2026-09-20: "어디로 쏘아지는지 설치해봐야 알아서").</summary>
    public class PlaceWorker_MFProjector : PlaceWorker
    {
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            var p = thing as Building_PhotoProjector;
            if (thing is MinifiedThing minified) p = minified.InnerThing as Building_PhotoProjector;
            GenDraw.DrawFieldEdges(Building_PhotoProjector.BeamCells(center, rot, Find.CurrentMap, p?.ProjectionSize ?? 0,
                p?.LayoutAspect ?? PhotoCapture.DefaultAspect, p?.CustomProjectionWidth ?? 0f, p?.CustomProjectionHeight ?? 0f).ToList(), Color.white);
        }
    }
}
