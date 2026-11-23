using System.Linq;
using RimWorld.Planet;
using Verse;

namespace MomentFrames
{
    /// <summary>Names are observed from a real map or a visit linked to a real photograph. No numbered colony name is invented.</summary>
    internal static class PhotoLocations
    {
        internal static void Capture(PhotoRecord record, Map map)
        {
            if (record == null || map == null) return;
            record.locationName = map.Parent?.Label;
            if (record.locationName.NullOrEmpty()) record.locationName = Journey.LandmarkLabel(Journey.LandmarkAt(map.Tile));
            if (record.locationName.NullOrEmpty()) record.locationName = map.Biome?.LabelCap.ToString();
            record.locationTile = map.Tile.Valid ? map.Tile.tileId : -1;
        }
        internal static string Resolve(PhotoRecord record, PhotoStore store)
        {
            if (record == null) return "";
            if (!record.locationName.NullOrEmpty()) return record.locationName;
            string current = Find.Maps.FirstOrDefault(m => m.uniqueID == record.mapId)?.Parent?.Label;
            if (!current.NullOrEmpty()) return current;
            if (store == null) return "";
            // A journey photo ID proves which visit belongs to this map. Calendar proximity alone is not evidence.
            var ids = new System.Collections.Generic.HashSet<string>(store.AllRecords.Where(r => r.mapId == record.mapId).Select(r => r.id));
            var visit = store.Visits.Where(v => !v.photoId.NullOrEmpty() && ids.Contains(v.photoId)
                && (v.photoId == record.id || v.ticksAbs <= record.ticksAbs))
                .OrderByDescending(v => v.ticksAbs).FirstOrDefault();
            if (visit == null) return "";
            if (!visit.locationName.NullOrEmpty()) return visit.locationName;
            var parent = Find.WorldObjects?.AllWorldObjects.OfType<MapParent>().FirstOrDefault(p => p.Tile == visit.tile);
            if (parent != null && !parent.Label.NullOrEmpty()) return parent.Label;
            return visit.landmarkName ?? "";
        }
        internal static string MapLabel(int mapId, PhotoStore store)
        {
            var record = store?.Records.Where(r => r.mapId == mapId).OrderByDescending(r => r.ticksAbs)
                .FirstOrDefault(r => !Resolve(r, store).NullOrEmpty());
            string name = record == null ? Find.Maps.FirstOrDefault(m => m.uniqueID == mapId)?.Parent?.Label : Resolve(record, store);
            return name.NullOrEmpty() ? "MF_Records_UnnamedPlace".Translate(mapId).ToString() : name;
        }
    }
}
