using System;
using System.Collections.Generic;
using System.Linq;

namespace MomentFrames
{
    /// <summary>Only an explicitly recorded ordinary group session can collapse into one album print.</summary>
    internal sealed class PhotoBursts
    {
        private readonly Dictionary<string, List<PhotoRecord>> groups = new Dictionary<string, List<PhotoRecord>>();
        private readonly Dictionary<string, PhotoRecord> representatives = new Dictionary<string, PhotoRecord>();
        internal readonly List<PhotoRecord> Prints = new List<PhotoRecord>();

        internal static string Key(PhotoRecord photo)
        {
            if (photo == null) return null;
            if (!string.IsNullOrEmpty(photo.groupSession)) return photo.groupSession;
            // Older ordinary group records already contain the exact Lord id and shot ordinal.
            // Do not infer a group from dates, actors, titles, or an original/reshoot relationship.
            if (photo.kind != GroupPhoto.Kind || photo.mapId < 0 || photo.requiredSubjects?.Count > 0
                || string.IsNullOrEmpty(photo.eventKey)) return null;
            var parts = photo.eventKey.Split(':');
            if (parts.Length < 2 || parts.Length > 3 || parts[0] != "group" || !parts[1].StartsWith("Lord_", StringComparison.Ordinal)
                || !int.TryParse(parts[1].Substring(5), out int lord) || lord < 0
                || (parts.Length == 3 && (!int.TryParse(parts[2], out int shot) || shot < 2))) return null;
            return "group:" + photo.mapId + ":" + parts[1];
        }

        internal PhotoBursts(IList<PhotoRecord> records)
        {
            foreach (var photo in records)
            {
                string key = Key(photo);
                if (key == null) continue;
                if (!groups.TryGetValue(key, out var shots)) groups[key] = shots = new List<PhotoRecord>();
                shots.Add(photo);
            }
            foreach (var pair in groups)
            {
                pair.Value.Sort((a, b) => a.ticksAbs != b.ticksAbs ? a.ticksAbs.CompareTo(b.ticksAbs) : string.CompareOrdinal(a.id, b.id));
                representatives[pair.Key] = pair.Value.FirstOrDefault(p => p.groupRepresentative) ?? pair.Value[0];
            }
            var seen = new HashSet<string>();
            foreach (var photo in records)
            {
                string key = Key(photo);
                if (key == null) Prints.Add(photo);
                else if (seen.Add(key)) Prints.Add(representatives[key]);
            }
        }

        internal List<PhotoRecord> Shots(PhotoRecord photo) => Key(photo) is string key && groups.TryGetValue(key, out var shots) ? shots : null;
        internal PhotoRecord Representative(PhotoRecord photo) => Shots(photo) != null ? representatives[Key(photo)] : photo;
        internal int PrintIndex(string photoId)
        {
            for (int i = 0; i < Prints.Count; i++)
                if (Prints[i].id == photoId || Shots(Prints[i])?.Any(p => p.id == photoId) == true) return i;
            return -1;
        }
        internal static void SetRepresentative(PhotoStore store, PhotoRecord selected)
        {
            string key = Key(selected);
            if (store == null || key == null) return;
            foreach (var shot in store.AllRecords.Where(p => Key(p) == key)) shot.groupRepresentative = shot.id == selected.id;
            store.NotifyChanged();
        }
    }
}
