using System;
using Verse;

namespace MomentFrames
{
    /// <summary>Exact map boundaries; no ordinary-photo span/aspect limits.</summary>
    internal static class SettlementLayout
    {
        internal static CellRect[] Quadrants(int width, int height)
        {
            if (width < 2 || height < 2) throw new ArgumentOutOfRangeException();
            int west = width / 2, south = height / 2;
            return new[] {
                new CellRect(0, south, west, height - south),
                new CellRect(west, south, width - west, height - south),
                new CellRect(0, 0, west, south),
                new CellRect(west, 0, width - west, south)
            };
        }

        internal static void ImageSize(int cellsWide, int cellsHigh, int maxSide, out int width, out int height)
        {
            if (cellsWide <= 0 || cellsHigh <= 0) throw new ArgumentOutOfRangeException();
            int cap = Math.Max(64, Math.Min(SettlementResolution.MaximumSide, maxSide));
            double scale = Math.Min((double)cap / cellsWide, (double)cap / cellsHigh);
            width = Math.Max(1, (int)Math.Round(cellsWide * scale));
            height = Math.Max(1, (int)Math.Round(cellsHigh * scale));
        }

        internal static int Stamp(int year, int quarter) => checked(year * 4 + quarter - 1);
        internal static void Period(int stamp, out int year, out int quarter) { year = stamp / 4; quarter = stamp % 4 + 1; }
        internal static bool IsDue(int mode, int previous, int current) => mode > 0 && current > previous && (mode == 1 || current / 4 > previous / 4);
    }
}
