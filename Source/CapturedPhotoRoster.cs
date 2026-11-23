using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>The event roster remains provenance; a shutter records everyone actually visible in its frame.</summary>
    internal static class CapturedPhotoRoster
    {
        internal static bool Includes(PhotoRecord record, Pawn pawn)
        {
            if (pawn == null) return false;
            if (record == null) return pawn.IsColonist; // map surveys retain their colony roster
            return pawn.RaceProps?.Humanlike == true || record.Has(pawn);
        }

        internal static List<Pawn> Observe(PhotoRecord record, Map map)
        {
            return PhotoCapture.VisiblePawns(map).Where(p => Visible(record, p, map,
                record.center, record.spanCells, record.Aspect)).Distinct().ToList();
        }

        internal static bool Visible(PhotoRecord record, Pawn pawn, Map map, Vector3 center, float span, float aspect)
        {
            if (map == null || !Includes(record, pawn) || !PhotoCapture.InFrame(pawn, map, center, span, aspect)
                || !PhotoCapture.VisualPosition(pawn, map, out var position)) return false;
            var cell = position.ToIntVec3();
            return cell.InBounds(map) && !map.fogGrid.IsFogged(cell);
        }

        internal static void FreezeEvent(PhotoRecord record, IEnumerable<Pawn> people)
        {
            var actors = people?.Where(p => p != null).Distinct().ToList() ?? new List<Pawn>();
            record.eventPeopleNames = actors.Select(p => p.LabelShort).ToList();
            record.eventPeopleIds = actors.Select(p => p.GetUniqueLoadID()).ToList();
        }
    }
}
