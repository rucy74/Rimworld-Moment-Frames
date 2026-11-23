using System;
using Verse;
namespace MomentFrames
{
    internal static class JourneyFramePolicy
    {
        /// <summary>A quarter-map view starts in the arriving party's quadrant. Keep that size while moving it to include the party;
        /// enlarge only an axis whose participant bounds are wider than its original quadrant.</summary>
        internal static CellRect Quadrant(int width, int height, int focusX, int focusZ, CellRect participants)
        {
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException();
            int west = width / 2, south = height / 2;
            int x = focusX < west ? 0 : west, z = focusZ < south ? 0 : south;
            int w = focusX < west ? west : width - west, h = focusZ < south ? south : height - south;
            if (!participants.IsEmpty)
            {
                FitAxis(width, participants.minX, participants.maxX, ref x, ref w);
                FitAxis(height, participants.minZ, participants.maxZ, ref z, ref h);
            }
            return new CellRect(x, z, w, h);
        }

        private static void FitAxis(int mapSide, int first, int last, ref int start, ref int length)
        {
            first = Math.Max(0, Math.Min(mapSide - 1, first));
            last = Math.Max(first, Math.Min(mapSide - 1, last));
            length = Math.Max(length, last - first + 1);
            start = Math.Max(last - length + 1, Math.Min(first, start));
            start = Math.Max(0, Math.Min(mapSide - length, start));
        }

        // A place name is never evidence for a lake, mountain, or other visible feature.
        internal static float Score(int revealed, int water, int rock, int trees, int people, int total)
        {
            if (total <= 0 || people < 1 || revealed < total / 3f) return -1f;
            float wet = water / (float)total, stone = rock / (float)total;
            if (water < 20 && rock < 25) return -1f;
            // Prefer a shoreline or terrain edge over a frame that contains only water/rock.
            float edge = Math.Min(wet, 1f - wet) * 4f + Math.Min(stone, 1f - stone) * 2f;
            return edge + Math.Min(trees, 60) / 120f + Math.Min(people, 6) * .08f;
        }
    }
}
