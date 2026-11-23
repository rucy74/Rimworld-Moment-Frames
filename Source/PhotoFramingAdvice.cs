using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal static class PhotoFramingAdvice
    {
        // Match the drawn body margins used by PhotoRecord.MeasurePeople, at planned gathering cells.
        internal static bool Contains(Vector3 center, float span, float aspect, Vector3 person) =>
            person.x - .45f >= center.x - span * aspect / 2f && person.x + .45f <= center.x + span * aspect / 2f
            && person.z - .45f >= center.z - span / 2f && person.z + .75f <= center.z + span / 2f;

        internal static string ForPlan(Building_PhotoSpot spot, IList<Pawn> moving, IList<IntVec3> cells, Pawn stationary = null)
        {
            if (spot == null || !spot.TryGetFrame(out Vector3 center, out float span, out float aspect)
                || moving == null || cells == null || cells.Count < moving.Count) return null;
            var names = new List<string>();
            for (int i = 0; i < moving.Count; i++)
                if (moving[i] != null && !Contains(center, span, aspect, cells[i].ToVector3Shifted())) names.Add(moving[i].LabelShort);
            if (stationary != null && !moving.Contains(stationary) && !Contains(center, span, aspect, stationary.PositionHeld.ToVector3Shifted())) names.Insert(0, stationary.LabelShort);
            if (names.Count == 0) return null;
            return "MF_FavoriteFrameWarning".Translate(string.Join(", ", names.Take(3)), names.Count);
        }
    }
}
