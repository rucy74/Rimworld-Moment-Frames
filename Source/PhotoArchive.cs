using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Verse;

namespace MomentFrames
{
    /// <summary>What moving a photo to the archive touches: the menu's conditions and the question it asks. Archiving deletes nothing: the
    /// files stay, and everything that shows a photo keeps its id, so restoring puts it back. Deleting for good is a second, separate step
    /// from the archive tab (PhotoStore.DeleteArchived), like emptying a recycle bin.</summary>
    internal static class PhotoArchive
    {
        /// <summary>Settlement surveys stay: a missing quarter would leave a hole in the change-over-time view, and their schedule is a setting.</summary>
        internal static bool Offered(PhotoRecord rec) => rec != null && rec.HasFile && !rec.eventOnly && !Kinds.IsSurvey(rec.kind);

        /// <summary>A film being shown reads its photos by id while the night runs.</summary>
        internal static bool InNightFilm(PhotoRecord rec) => rec != null && Find.Maps.Any(m => m.lordManager.lords
            .Any(l => l.LordJob is LordJob_MFMemoryNight night && night.FilmIds.Contains(rec.id)));

        internal static string Question(IList<PhotoRecord> photos)
        {
            string text = (photos.Count > 1 ? "MF_Archive_ConfirmBurst".Translate(photos.Count) : "MF_Archive_Confirm".Translate()).ToString();
            if (photos.Any(p => Kinds.KeepsLineWhenArchived(p.kind))) text += "\n\n" + "MF_Archive_ConfirmLine".Translate();
            string uses = UsesText(photos);
            if (!uses.NullOrEmpty()) text += "\n\n" + "MF_Archive_ConfirmUses".Translate(uses);
            return text;
        }

        private static string UsesText(IList<PhotoRecord> photos)
        {
            var ids = new HashSet<string>(photos.Select(p => p.id));
            int frames = 0, projectors = 0, trees = 0;
            foreach (var map in Find.Maps)
                foreach (var building in map.listerBuildings.allBuildingsColonist)
                {
                    if (building is Building_PhotoFrame frame) { if (ids.Contains(frame.photoId ?? "")) frames++; }
                    else if (building is Building_PhotoCollage collage) { if (Enumerable.Range(0, Building_PhotoCollage.Slots).Any(i => ids.Contains(collage.SlotId(i)))) frames++; }
                    else if (building is Building_PhotoProjector projector) { if (ids.Any(projector.ChoseExactly)) projectors++; }
                    else if (building is Building_PhotoTree tree) { if (Enumerable.Range(0, MemoryTreeLayout.Count).Any(i => ids.Contains(tree.SlotId(i) ?? ""))) trees++; }
                }
            var parts = new List<string>();
            if (frames > 0) parts.Add("MF_Archive_UsesFrames".Translate(frames));
            if (projectors > 0) parts.Add("MF_Archive_UsesProjectors".Translate(projectors));
            if (trees > 0) parts.Add("MF_Archive_UsesTrees".Translate(trees));
            return string.Join(", ", parts);
        }

        /// <summary>The question before deleting archived photos for good.</summary>
        internal static string DeleteQuestion(IList<PhotoRecord> photos)
        {
            string text = (photos.Count > 1 ? "MF_Archive_DeleteAll".Translate(photos.Count) : "MF_Archive_DeleteOne".Translate()).ToString()
                + "\n\n" + "MF_Archive_DeleteWarning".Translate();
            if (photos.Any(p => Kinds.KeepsLineWhenArchived(p.kind))) text += "\n\n" + "MF_Archive_ConfirmLine".Translate();
            return text;
        }

        /// <summary>Every file a photo owns in its colony folder: the original and each edit, with their thumbnails, previews and protected
        /// copies. Full paths, never outside the folder. Several photos can name the same protected copy (same picture); the caller decides.</summary>
        internal static HashSet<string> FilesOf(PhotoStore store, PhotoRecord rec)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (store == null || rec == null) return files;
            string root = Path.GetFullPath(store.FolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            void Add(string relative)
            {
                if (relative.NullOrEmpty()) return;
                string full;
                try { full = Path.GetFullPath(Path.Combine(root, relative)); }
                catch (Exception) { return; }
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
                files.Add(full);
                files.Add(Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full) + ".thumb.png"));
                files.Add(PhotoPreviews.PathFor(full));
            }
            Add(rec.file); Add(rec.sourceBaselineFile);
            if (rec.edits != null) foreach (var e in rec.edits) { Add(e.file); Add(e.sourceBaselineFile); }
            return files;
        }

        internal static long Bytes(PhotoStore store)
        {
            long total = 0;
            foreach (var rec in store.ArchivedRecords)
            {
                // The plain files and any protected copy of them all take room.
                var names = new[] { rec.file }.Concat(rec.edits.Where(e => e.HasFile).Select(e => e.file)).Where(f => !f.NullOrEmpty());
                var paths = new HashSet<string>(names.SelectMany(f => new[] { Path.Combine(store.FolderPath, f),
                    Path.Combine(store.FolderPath, Path.GetFileNameWithoutExtension(f) + ".thumb.png") }));
                paths.UnionWith(new[] { store.OriginalPath(rec), store.OriginalThumbPath(rec) });
                paths.UnionWith(rec.edits.Where(e => e.HasFile).SelectMany(e => new[] { store.EditPath(e), store.EditThumbPath(e) }));
                foreach (string path in paths)
                {
                    try { if (!path.NullOrEmpty() && File.Exists(path)) total += new FileInfo(path).Length; }
                    catch (Exception e) { Log.WarningOnce("[MomentFrames] archive size: " + e.Message, 0x4D464131); }
                }
            }
            return total;
        }
    }
}
