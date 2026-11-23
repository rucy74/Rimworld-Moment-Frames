using System.Collections.Generic;
using System.Linq;

namespace MomentFrames
{
    internal static class ChronicleGroups
    {
        internal sealed class Row
        {
            internal PhotoRecord Record;
            internal string Key;
            internal int Stamp;
            internal List<PhotoRecord> Photos;
        }
        internal static List<Row> Build(IList<PhotoRecord> records, IEnumerable<SettlementSnapshot> snapshots, ISet<string> expanded)
        {
            var membership = new Dictionary<string, string>();
            var stamps = new Dictionary<string, int>();
            foreach (var s in snapshots)
            {
                string key = s.mapId + ":" + s.id;
                stamps[key] = s.stamp;
                foreach (string id in s.photoIds ?? new List<string>()) membership[id] = key;
            }
            var groups = new Dictionary<string, Row>();
            var result = new List<Row>();
            foreach (var rec in records)
            {
                if (!Kinds.IsSurvey(rec.kind) || !membership.TryGetValue(rec.id, out string key))
                { result.Add(new Row { Record = rec }); continue; }
                if (!groups.TryGetValue(key, out var row))
                {
                    row = new Row { Record = rec, Key = key, Stamp = stamps[key], Photos = new List<PhotoRecord>() };
                    groups.Add(key, row); result.Add(row);
                }
                row.Photos.Add(rec);
            }
            var visible = new List<Row>();
            foreach (var row in result)
            {
                visible.Add(row);
                if (row.Key != null && expanded.Contains(row.Key))
                    visible.AddRange(row.Photos.Select(p => new Row { Record = p }));
            }
            return visible;
        }
    }
}
