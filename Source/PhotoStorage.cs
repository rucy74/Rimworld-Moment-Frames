using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Verse;

namespace MomentFrames
{
    internal static class PhotoStorage
    {
        internal static List<PhotoFileReference> References(PhotoStore store, string onlyRecord = null)
        {
            string folder = store.FolderPath;
            return store.AllRecords.Where(r => r.HasFile && !r.eventOnly && (onlyRecord == null || r.id == onlyRecord))
                .SelectMany(r => new[] { Reference(folder, r, null) }.Concat(r.edits.Where(e => e.HasFile).Select(e => Reference(folder, r, e)))).ToList();
        }
        private static PhotoFileReference Reference(string folder, PhotoRecord r, PhotoEdit e) => new PhotoFileReference
        {
            Folder = folder, RecordId = r.id, EditId = e?.id, File = e == null ? r.file : e.file,
            Hash = e == null ? r.sourceSha256 : e.sourceSha256, Baseline = e == null ? r.sourceBaselineFile : e.sourceBaselineFile,
            Title = e != null && !e.title.NullOrEmpty() ? e.title : r.title, Date = r.dateLabel
        };
        internal static bool Current(PhotoStore store, PhotoFileReference r)
        {
            if (!ReferenceEquals(store, PhotoStore.Instance) || store.FolderPath != r.Folder) return false;
            var record = store.GetAny(r.RecordId); if (record == null) return false;
            var edit = r.EditId == null ? null : record.Edit(r.EditId); if (r.EditId != null && edit == null) return false;
            return (edit == null ? record.file : edit.file) == r.File && (edit == null ? record.sourceSha256 : edit.sourceSha256) == r.Hash
                && (edit == null ? record.sourceBaselineFile : edit.sourceBaselineFile) == r.Baseline;
        }
        // Called only on the main thread after a verified new copy has been written.
        internal static bool Connect(PhotoStore store, PhotoFileReference r, string file, string hash)
        {
            if (!Current(store, r) || !File.Exists(PhotoStorageFiles.Local(r.Folder, file))) return false;
            var record = store.GetAny(r.RecordId); var edit = r.EditId == null ? null : record.Edit(r.EditId);
            if (edit == null) { record.file = file; record.sourceSha256 = hash; record.sourceBaselineFile = null; }
            else { edit.file = file; edit.sourceSha256 = hash; edit.sourceBaselineFile = null; }
            PhotoTextureCache.ForgetMissing(); store.NotifyChanged(); return true;
        }
    }
}
