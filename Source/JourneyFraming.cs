using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal static class JourneyFraming
    {
        /// <summary>New-place exploration records the land on a quarter-map scale, including every currently visible arriving colonist.
        /// A two-cell border keeps bodies and their immediate surroundings away from the crop edge where the map allows it.</summary>
        internal static CellRect ExplorationArea(Map map, List<Pawn> people)
        {
            IntVec3 focus = Journey.Focus(people, map);
            CellRect participants = CellRect.Empty;
            bool found = false;
            foreach (Pawn pawn in people)
            {
                if (!PhotoCapture.VisualPosition(pawn, map, out Vector3 position)) continue;
                int x = Mathf.FloorToInt(position.x), z = Mathf.FloorToInt(position.z);
                if (!found) { participants = new CellRect(x, z, 1, 1); found = true; }
                else participants = CellRect.FromLimits(Mathf.Min(participants.minX, x), Mathf.Min(participants.minZ, z),
                    Mathf.Max(participants.maxX, x), Mathf.Max(participants.maxZ, z));
            }
            if (found) participants = participants.ExpandedBy(2).ClipInsideMap(map);
            return JourneyFramePolicy.Quadrant(map.Size.x, map.Size.z, focus.x, focus.z, participants);
        }

        internal static void Frame(Map map, List<Pawn> people, out Vector3 center, out float span, out float aspect)
        {
            IntVec3 focus = Journey.Focus(people, map);
            Hooks.AutoFrame(map, focus, people, Journey.MinSpan, Journey.MaxSpan, out center, out span, out aspect);
            if (map?.terrainGrid == null || map.fogGrid == null || people.Count == 0) return;
            float best = -1f; Vector3 chosen = center;
            float scenicSpan = Mathf.Max(35f, Mathf.Min(span, 45f)), scenicAspect = 1.5f;
            foreach (int dx in new[] { -12, 0, 12 })
                foreach (int dz in new[] { -12, 0, 12 })
                {
                    Vector3 candidate = focus.ToVector3Shifted() + new Vector3(dx, 0f, dz);
                    float size = scenicSpan, ratio = scenicAspect;
                    PhotoCapture.KeepInside(map, ref candidate, ref size, ref ratio);
                    CellRect area = PhotoCapture.CoveredRect(candidate, size, map, ratio);
                    int seen=0, water=0, rock=0, trees=0;
                    foreach (IntVec3 cell in area.Cells)
                    {
                        if (map.fogGrid.IsFogged(cell)) continue;
                        seen++;
                        if (map.terrainGrid.TerrainAt(cell).IsWater) water++;
                        var edifice = cell.GetEdifice(map);
                        if (edifice?.def?.building?.isNaturalRock == true) rock++;
                        if (cell.GetPlant(map)?.def?.plant?.IsTree == true) trees++;
                    }
                    int inFrame = people.Count(p => PhotoCapture.InFrame(p, map, candidate, size, ratio));
                    float score = JourneyFramePolicy.Score(seen, water, rock, trees, inFrame, area.Area)
                        - (Mathf.Abs(dx) + Mathf.Abs(dz)) * .002f;
                    if (score > best) { best=score; chosen=candidate; scenicSpan=size; scenicAspect=ratio; }
                }
            if (best < 0f) return; // Ordinary ground still retains the actual arriving people.
            center=chosen; span=scenicSpan; aspect=scenicAspect;
        }
    }
}
