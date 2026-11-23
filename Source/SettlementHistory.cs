using System;
using System.Collections.Generic;
using System.Linq;

namespace MomentFrames
{
    /// <summary>Calendar navigation and actual survey ordering, without creating missing historical entries.</summary>
    internal static class SettlementHistory
    {
        internal sealed class Entry
        {
            internal int MapId, Stamp, CapturedAbs;
            internal bool Complete, Available;
            internal string PhotoId;
        }

        internal static List<Entry> ForMap(IEnumerable<Entry> entries, int mapId, int throughStamp)
            => (entries ?? Enumerable.Empty<Entry>())
                .Where(e => e != null && e.MapId == mapId && e.Complete && e.Available && !string.IsNullOrEmpty(e.PhotoId) && e.Stamp <= throughStamp)
                .OrderBy(e => e.Stamp).ThenBy(e => e.CapturedAbs).ThenBy(e => e.PhotoId, StringComparer.Ordinal).ToList();

        internal static List<int> Years(IEnumerable<int> recordedPeriods, int currentStamp)
        {
            int currentYear = currentStamp / 4;
            int firstYear = Math.Min(5500, currentYear);
            foreach (int period in recordedPeriods ?? Enumerable.Empty<int>())
                if (period >= 0 && period <= currentStamp) firstYear = Math.Min(firstYear, period / 4);
            return Enumerable.Range(firstYear, currentYear - firstYear + 1).ToList();
        }

        internal static int SelectYear(int year, int previousStamp, int currentStamp)
        {
            int quarter = previousStamp % 4 + 1;
            if (year == currentStamp / 4) quarter = Math.Min(quarter, currentStamp % 4 + 1);
            return checked(year * 4 + quarter - 1);
        }
    }
}
