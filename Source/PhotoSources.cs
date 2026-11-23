using System;
using System.IO;
using Verse;

namespace MomentFrames
{
    internal enum PhotoSource { Colony, Computer, Legacy }

    // Provenance belongs to the original record, not to an externally edited version of it.
    internal static class PhotoSources
    {
        internal static PhotoSource Of(PhotoRecord record) => record?.kind == Kinds.Relic ? PhotoSource.Legacy
            : record?.kind == Kinds.External ? PhotoSource.Computer : PhotoSource.Colony;
        internal static bool Imported(PhotoRecord record) => Of(record) != PhotoSource.Colony;
        internal static string Label(PhotoSource source) => (source == PhotoSource.Colony ? "MF_Imports_Current"
            : source == PhotoSource.Computer ? "MF_Imports_Computer" : "MF_Imports_Legacy").Translate();

        internal static string ColonyKey(PhotoRecord record)
        {
            if (Of(record) != PhotoSource.Legacy) return "";
            if (!record.importedColonyId.NullOrEmpty()) return record.importedColonyId;
            string key = record.eventKey;
            if (key?.StartsWith("relic:", StringComparison.Ordinal) == true)
            {
                int end = key.IndexOf(':', 6);
                if (end > 6) return key.Substring(6, end - 6);
            }
            return "";
        }

        // Read only the index header, once per imported-list revision. Never scan pictures or run on ticks.
        internal static string ColonyName(PhotoStore store, PhotoRecord record)
        {
            if (!record.importedColonyName.NullOrEmpty()) return record.importedColonyName;
            string id = ColonyKey(record);
            try
            {
                if (store != null && !id.NullOrEmpty() && id != "." && id != ".."
                    && id.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                {
                    string index = Path.Combine(Path.GetDirectoryName(store.FolderPath), id, RelicImport.IndexFile);
                    if (File.Exists(index))
                        using (var reader = new StreamReader(index))
                        {
                            string[] header = (reader.ReadLine() ?? "").Split('\t');
                            if (header.Length >= 3 && header[0] == "#MomentFrames" && header[2] == id && !header[1].NullOrEmpty())
                                return header[1];
                        }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            return id.NullOrEmpty() ? "MF_Imports_UnknownColony".Translate().ToString()
                : "MF_Imports_ColonyId".Translate(id.Substring(0, Math.Min(8, id.Length))).ToString();
        }
    }
}
