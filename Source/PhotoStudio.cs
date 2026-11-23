using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>A real, built backdrop sharing the spot's gathering commands. No idle tick work.</summary>
    public partial class Building_PhotoStudio : Building_PhotoSpot
    {
        internal static Building_PhotoStudio At(Map map, IntVec3 cell) => map != null && cell.InBounds(map)
            ? cell.GetThingList(map).OfType<Building_PhotoStudio>().FirstOrDefault(s => s.Position == cell && s.Spawned) : null;
        internal const int Columns = 5, Rows = 3;
        internal static IntVec3 Rotate(IntVec3 offset, Rot4 direction) => direction == Rot4.East ? new IntVec3(offset.z,0,-offset.x)
            : direction == Rot4.South ? new IntVec3(-offset.x,0,-offset.z) : direction == Rot4.West ? new IntVec3(-offset.z,0,offset.x) : offset;
        internal static List<IntVec3> Formation(Map map, IntVec3 origin, Rot4 rotation, int count)
        {
            var result = new List<IntVec3>();
            if (count <= 0) return result;
            var studio = At(map, origin);
            if (studio != null && rotation == studio.Rotation)
            {
                var seats = studio.SeatCells().Where(c => GroupFormation.Usable(c, origin.GetRoom(map), map)).ToList();
                if (seats.Count > 0)
                {
                    result.AddRange(seats.Take(Math.Min(count, Columns * Rows)));
                    foreach (int row in new[] { 1, 2, 0 })
                        foreach (int x in Enumerable.Range(-2, Columns).OrderBy(x => Math.Abs(x)).ThenBy(x => x))
                        {
                            if (result.Count >= Math.Min(count, Columns * Rows)) break;
                            var c = origin + Rotate(new IntVec3(x, 0, -1 + row), rotation);
                            if (!result.Contains(c) && GroupFormation.Usable(c, origin.GetRoom(map), map)) result.Add(c);
                            if (result.Count >= Math.Min(count, Columns * Rows)) break;
                        }
                    return result;
                }
            }
            int perRow = Math.Min(Columns, count), rows = (count + perRow - 1) / perRow;
            // The foreground is nearest the camera. Alternate rows so faces do not sit directly behind one another.
            for (int row = 0; row < Math.Min(Rows, rows); row++)
            {
                int n = Math.Min(perRow, count - row * perRow);
                int left = -(n / 2) + (n % 2 == 0 && row % 2 == 1 ? 1 : 0);
                foreach (int x in Enumerable.Range(left, n).OrderBy(x => Math.Abs(x)).ThenBy(x => x))
                {
                    IntVec3 cell = origin + Rotate(new IntVec3(x,0,-1 + row),rotation);
                    if (GroupFormation.Usable(cell,origin.GetRoom(map),map)) result.Add(cell);
                }
            }
            return result;
        }
        internal static void Arrange(List<Pawn> people, List<IntVec3> cells, Pawn subject = null)
        {
            if (people.Count != cells.Count) return;
            var ordered = people.OrderBy(p => p == subject ? 0 : p.DevelopmentalStage.Child() ? 1 : 2)
                .ThenBy(p => p.ageTracker?.AgeBiologicalYearsFloat ?? 99f).ThenBy(p => p.thingIDNumber).ToList();
            // Preserve the caller's pawn order (baby/guardian assignments depend on it), only reorder their destinations.
            var destinations = new Dictionary<Pawn,IntVec3>();
            for (int i=0;i<ordered.Count;i++) destinations[ordered[i]]=cells[i];
            for (int i=0;i<people.Count;i++) cells[i]=destinations[people[i]];
        }
        internal CellRect ArtworkBounds
        {
            get
            {
                float w=def.graphicData.drawSize.x,h=def.graphicData.drawSize.y;
                if(Rotation.IsHorizontal) {float swap=w;w=h;h=swap;}
                Vector3 center=DrawPos;
                // Cover the whole sprite in world space, including both edge cells for even widths.
                // CellRect.CenteredOn's two dimensions are full widths, not half extents.
                return CellRect.FromLimits(Mathf.FloorToInt(center.x-w/2f),Mathf.FloorToInt(center.z-h/2f),
                    Mathf.CeilToInt(center.x+w/2f)-1,Mathf.CeilToInt(center.z+h/2f)-1);
            }
        }
        internal void FitPeopleAndBackdrop(CellRect people, out Vector3 center, out float span, out float aspect)
        {
            var art=ArtworkBounds;
            var all=CellRect.FromLimits(Math.Min(people.minX,art.minX),Math.Min(people.minZ,art.minZ),
                Math.Max(people.maxX,art.maxX),Math.Max(people.maxZ,art.maxZ)).ExpandedBy(1).ClipInsideMap(Map);
            Designator_TakePhoto.FitRect(all,out center,out span,out aspect);
        }
        internal static bool ImmediateFamily(Pawn subject, Pawn other)
        {
            if(subject==other)return true;
            if(subject?.relations==null || other==null)return false;
            return subject.relations.DirectRelationExists(PawnRelationDefOf.Spouse,other)
                || subject.relations.DirectRelationExists(PawnRelationDefOf.Lover,other)
                || subject.relations.DirectRelationExists(PawnRelationDefOf.Fiance,other)
                || subject.relations.DirectRelationExists(PawnRelationDefOf.Parent,other)
                || other.relations?.DirectRelationExists(PawnRelationDefOf.Parent,subject)==true
                || subject.relations.DirectRelations.Any(r=>r.def==PawnRelationDefOf.Parent && other.relations?.DirectRelationExists(PawnRelationDefOf.Parent,r.otherPawn)==true);
        }
        public override string GetInspectString() => base.GetInspectString()+"\n"+"MF_StudioAutoHelp".Translate();
    }
}
