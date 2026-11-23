using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal static class ExternalPhotoImport
    {
        private sealed class RawHash {internal long size,modified;internal string hash;}
        private static readonly Dictionary<string,RawHash> rawHashes=new Dictionary<string,RawHash>(StringComparer.OrdinalIgnoreCase);
        private static bool HashValue(string value)=>value?.Length==64&&value.All(Uri.IsHexDigit);
        private static string Baseline(PhotoStore store, string relative,string expectedHash=null)
        {
            if (relative.NullOrEmpty()) return null;
            string file=Path.GetFileName(relative);
            string extension=Path.GetExtension(file);
            if (Path.GetDirectoryName(relative) != ExternalPhotoFiles.BaselineFolder || file.Length != 68
                || !(extension.Equals(".png",StringComparison.OrdinalIgnoreCase)||extension.Equals(".jpg",StringComparison.OrdinalIgnoreCase)) || !file.Take(64).All(Uri.IsHexDigit)
                ||(!expectedHash.NullOrEmpty()&&!file.Substring(0,64).Equals(expectedHash,StringComparison.OrdinalIgnoreCase))) return null;
            string path=Path.Combine(store.FolderPath,relative); return File.Exists(path)?path:null;
        }
        private static string Resolve(PhotoStore store,string file,string baseline,string expectedHash)
        {
            if(!ExternalPhotoFiles.SafeName(file))return null;
            string original=Baseline(store,baseline,expectedHash);if(original!=null)return original;
            string raw=Path.Combine(store.FolderPath,file);if(expectedHash.NullOrEmpty())return raw;
            if(!HashValue(expectedHash))return null;
            try
            {
                var info=new FileInfo(raw);if(!info.Exists)return null;
                long modified=info.LastWriteTimeUtc.Ticks,size=info.Length;
                if(!rawHashes.TryGetValue(raw,out var cached)||cached.size!=size||cached.modified!=modified)
                {
                    if(rawHashes.Count>=512)rawHashes.Clear();
                    cached=new RawHash{size=size,modified=modified,hash=ExternalPhotoFiles.HashFile(raw)};rawHashes[raw]=cached;
                }
                return cached.hash.Equals(expectedHash,StringComparison.OrdinalIgnoreCase)?raw:null;
            }
            catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
        }
        internal static string ResolveOriginal(PhotoStore store, PhotoRecord record) => record?.HasFile != true ? null:Resolve(store,record.file,record.sourceBaselineFile,record.sourceSha256);
        internal static string ResolveEdit(PhotoStore store, PhotoEdit edit) => edit?.HasFile != true ? null:Resolve(store,edit.file,edit.sourceBaselineFile,edit.sourceSha256);
        private static string ResolveThumb(PhotoStore store,string file,string baseline,string hash)
        {
            string full=Resolve(store,file,baseline,hash);if(full==null)return null;
            // A missing protected thumbnail is generated from the verified full source by the bounded texture cache.
            if(!hash.NullOrEmpty())return Path.Combine(store.FolderPath,ExternalPhotoFiles.BaselineFolder,hash+".thumb.png");
            return Path.Combine(store.FolderPath,Path.GetFileNameWithoutExtension(file)+".thumb.png");
        }
        internal static string ResolveOriginalThumb(PhotoStore store,PhotoRecord record)=>record?.HasFile!=true?null:ResolveThumb(store,record.file,record.sourceBaselineFile,record.sourceSha256);
        internal static string ResolveEditThumb(PhotoStore store,PhotoEdit edit)=>edit?.HasFile!=true?null:ResolveThumb(store,edit.file,edit.sourceBaselineFile,edit.sourceSha256);
        private static ExternalPhotoEntry Entry(PhotoRecord r, PhotoEdit e = null) => new ExternalPhotoEntry
        {
            file=e?.file??r.file,hash=e?.sourceSha256??r.sourceSha256,recordId=r.id,editId=e?.id,
            kind=r.kind,title=e != null && !e.title.NullOrEmpty()?e.title:r.title,note=e != null && !e.note.NullOrEmpty()?e.note:r.desc,
            date=r.dateLabel,dateSlug=r.dateSlug,year=r.year,ticks=r.ticksAbs,aspect=e?.aspect??r.Aspect
        };
        private static void Protect(PhotoStore store, PhotoRecord r, PhotoEdit edit, bool history)
        {
            string file=edit?.file??r.file;if(!ExternalPhotoFiles.SafeName(file))return;
            string existing=edit?.sourceSha256??r.sourceSha256;
            string raw=Path.Combine(store.FolderPath,file);if(!File.Exists(raw))return;
            string baselineExisting=edit?.sourceBaselineFile??r.sourceBaselineFile;
            if(existing.NullOrEmpty()||Baseline(store,baselineExisting,existing)==null)
            {
                byte[] bytes=File.ReadAllBytes(raw);string hash=ExternalPhotoFiles.Hash(bytes);
                if(!existing.NullOrEmpty()&&existing!=hash)return; // An external replacement is never promoted to the original baseline.
                string thumbPath=Path.Combine(store.FolderPath,Path.GetFileNameWithoutExtension(file)+".thumb.png");
                // Only a just-completed new capture may reuse its own fresh thumbnail. Legacy observations and missing-baseline
                // repairs generate from the verified PNG, so an externally changed thumbnail cannot be promoted to the baseline.
                byte[] thumb=history&&existing.NullOrEmpty()&&File.Exists(thumbPath)?File.ReadAllBytes(thumbPath):null;
                if(thumb==null)PhotoCapture.MakeThumb(bytes,ref thumb);
                string baseline=ExternalPhotoFiles.Preserve(store.FolderPath,hash,bytes,thumb);
                if(edit==null){r.sourceSha256=hash;r.sourceBaselineFile=baseline;}
                else{edit.sourceSha256=hash;edit.sourceBaselineFile=baseline;}
            }
            if(history)ExternalPhotoFiles.Append(store.FolderPath,Entry(r,edit));
        }
        internal static void RememberRecord(PhotoStore store,PhotoRecord record)
        {try{if(store!=null&&record!=null)Protect(store,record,null,true);}catch(Exception e){Warn(e);}}
        /// <summary>A new photo whose protected copy the capture worker already made and checked: the same fields and ledger line as Protect, with nothing read
        /// back on the game thread. Anything unexpected goes the ordinary way.</summary>
        internal static void RememberCaptured(PhotoStore store, PhotoRecord record, PreparedCapture prepared)
        {
            if (prepared == null || store == null || record == null || !record.sourceSha256.NullOrEmpty() || !ExternalPhotoFiles.SafeName(record.file)
                || !File.Exists(Path.Combine(store.FolderPath, record.file)) || Baseline(store, prepared.relative, prepared.hash) == null)
            { RememberRecord(store, record); return; }
            record.sourceSha256 = prepared.hash; record.sourceBaselineFile = prepared.relative;
            try { ExternalPhotoFiles.Append(store.FolderPath, Entry(record)); }
            catch (Exception e) { Warn(e); }
        }
        internal static void PrepareRecord(PhotoStore store, PhotoRecord record, byte[] png, byte[] thumb)
        {
            try
            {
                string hash = ExternalPhotoFiles.Hash(png);
                // Supplied render bytes avoid re-reading/decoding the new full-size image.
                string baseline = ExternalPhotoFiles.Preserve(store.FolderPath, hash, png);
                record.sourceSha256 = hash; record.sourceBaselineFile = baseline;
                string path = Path.ChangeExtension(Path.Combine(store.FolderPath, baseline), ".thumb.png");
                if (thumb != null && !File.Exists(path)) PhotoFiles.TryDerived(path, () => thumb);
            }
            catch (Exception e) { Warn(e); }
        }
        internal static void RememberEdit(PhotoStore store,PhotoRecord record,PhotoEdit edit)
        {try{if(store!=null&&record!=null&&edit!=null)Protect(store,record,edit,true);}catch(Exception e){Warn(e);}}
        internal static void ObserveCurrent(PhotoStore store)
        {
            if(store==null)return;
            foreach(var r in store.AllRecords.Where(r=>r.HasFile))
                try
                {
                    if(r.sourceSha256.NullOrEmpty()||Baseline(store,r.sourceBaselineFile,r.sourceSha256)==null)Protect(store,r,null,false);
                    foreach(var e in r.edits.Where(e=>e.HasFile&&(e.sourceSha256.NullOrEmpty()||Baseline(store,e.sourceBaselineFile,e.sourceSha256)==null)))Protect(store,r,e,false);
                }
                catch(Exception e){Warn(e);}
        }
        private static void Warn(Exception e)
        {try{Log.Warning("[MomentFrames] photo baseline/index unavailable: "+e.Message);}catch{}}
        internal static List<ExternalPhotoCandidate> Scan(PhotoStore store)
        {
            var entries=ScanReferences(store);
            var result=ExternalPhotoFiles.Scan(store.FolderPath,entries,ExternalPhotoFiles.History(store.FolderPath));
            CompleteScan(result);
            return result;
        }
        internal static List<ExternalPhotoEntry> ScanReferences(PhotoStore store)
        {
            var entries=store.AllRecords.Where(r=>r.HasFile).SelectMany(r=>new[]{Entry(r)}.Concat(r.edits.Where(e=>e.HasFile).Select(e=>Entry(r,e)))).ToList();
            // Import content is also a registered reference even when its new local filename differs from the source filename.
            entries.AddRange(store.AllRecords.Where(r=>!r.externalSourceHash.NullOrEmpty()).Select(r=>new ExternalPhotoEntry{file=r.file,hash=r.externalSourceHash,recordId=r.id}));
            return entries;
        }
        internal static void CompleteScan(List<ExternalPhotoCandidate> result)
        {
            rawHashes.Clear(); // Apply a deliberate reread only after its worker finished and the owner is still current.
            foreach(var candidate in result.Where(c=>c.kind==ExternalPhotoKind.NewImage))
            {
                var old=RelicImport.FromName(candidate.file);
                if(old==null)continue;
                candidate.kind=ExternalPhotoKind.OtherDay;
                candidate.history=new ExternalPhotoEntry{file=candidate.file,title=old.title,note=old.desc,date=old.dateLabel,dateSlug=old.dateSlug,kind=old.kind,year=old.year,aspect=old.aspect,Exact=false};
            }
        }
        internal static bool ImportFile(PhotoStore store, string path, out PhotoRecord record, out string failure)
        {
            record = null; failure = null;
            try
            {
                if (store == null || !ReferenceEquals(store, PhotoStore.Instance) || Current.ProgramState != ProgramState.Playing)
                    throw new InvalidOperationException("MF_Extras_ImportUnavailable".Translate());
                if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("MF_Extras_InvalidPng".Translate());
                if (new FileInfo(path).Length > ExternalPhotoFiles.MaximumBytes)
                    throw new InvalidDataException("MF_Extras_ImageTooLarge".Translate());
                byte[] png = File.ReadAllBytes(path);
                if (png.Length > ExternalPhotoFiles.MaximumBytes)
                    throw new InvalidDataException("MF_Extras_ImageTooLarge".Translate());
                if (!ExternalPhotoFiles.Png(png, out float aspect, out bool tooLarge))
                    throw new InvalidDataException((tooLarge ? "MF_Extras_ImageTooLarge" : "MF_Extras_InvalidPng").Translate());
                string hash = ExternalPhotoFiles.Hash(png);
                if (store.AllRecords.Any(r => r.sourceSha256 == hash || r.externalSourceHash == hash || r.edits.Any(e => e.sourceSha256 == hash)))
                    throw new InvalidOperationException("MF_Extras_Duplicate".Translate());
                byte[] thumb = null; PhotoCapture.MakeThumb(png, ref thumb);
                if (thumb == null) throw new InvalidDataException("MF_Extras_InvalidPng".Translate());
                var imported = new PhotoRecord {
                    id = Guid.NewGuid().ToString("N"), kind = Kinds.External, eventKey = "external:" + hash,
                    title = Path.GetFileNameWithoutExtension(path), desc = "MF_Extras_ImportedNote".Translate(),
                    dateLabel = "MF_Extras_UnknownDate".Translate(), year = 0, ticksAbs = -1, capturedTicksAbs = -1,
                    mapId = -1, aspect = aspect, sourceSha256 = hash, externalSourceHash = hash,
                    externalSourceFile = Path.GetFileName(path)
                };
                ExternalPhotoFiles.WriteNew(store.FolderPath, "external-photo", png, thumb, file => {
                    imported.file = file;
                    if (!store.CommitRecord(imported)) throw new InvalidOperationException("Import registration failed");
                });
                RememberRecord(store, imported); record = imported;
                return true;
            }
            catch (Exception e) { failure = e.Message; return false; }
        }

        internal static bool Import(PhotoStore store,ExternalPhotoCandidate candidate,out string failure)
        {
            failure=null;
            try
            {
                if(store==null||!ReferenceEquals(store,PhotoStore.Instance)||candidate==null||candidate.kind==ExternalPhotoKind.MissingImage||candidate.duplicate)
                    throw new InvalidOperationException("MF_Extras_ImportUnavailable".Translate());
                if(!ExternalPhotoFiles.SafeName(candidate.file)||Path.GetFullPath(candidate.path)!=Path.GetFullPath(Path.Combine(store.FolderPath,candidate.file)))
                    throw new InvalidDataException("Image is outside this colony's folder");
                if(new FileInfo(candidate.path).Length>ExternalPhotoFiles.MaximumBytes)throw new InvalidDataException("MF_Extras_ImageTooLarge".Translate());
                byte[] png=File.ReadAllBytes(candidate.path);
                if(ExternalPhotoFiles.Hash(png)!=candidate.hash)throw new InvalidDataException("MF_Extras_ImageChanged".Translate());
                if(!ExternalPhotoFiles.Png(png,out float aspect,out bool tooLarge))throw new InvalidDataException((tooLarge?"MF_Extras_ImageTooLarge":"MF_Extras_InvalidPng").Translate());
                if(store.AllRecords.Any(r=>r.sourceSha256==candidate.hash||r.externalSourceHash==candidate.hash||r.edits.Any(e=>e.sourceSha256==candidate.hash)))
                    throw new InvalidOperationException("MF_Extras_Duplicate".Translate());
                byte[] thumb=null;PhotoCapture.MakeThumb(png,ref thumb);if(thumb==null)throw new InvalidDataException("MF_Extras_InvalidPng".Translate());
                if(candidate.kind==ExternalPhotoKind.ChangedImage)
                {
                    var record=store.AllRecords.FirstOrDefault(r=>r.id==candidate.recordId);
                    if(record==null)throw new InvalidOperationException("MF_Extras_ImportUnavailable".Translate());
                    var edit=new PhotoEdit{id=Guid.NewGuid().ToString("N"),aspect=aspect,filter=PhotoFilters.None,madeAbs=Find.TickManager?.TicksAbs??-1,sourceSha256=candidate.hash,externalImage=true};
                    ExternalPhotoFiles.WriteNew(store.FolderPath,"external-edit",png,thumb,file=>{edit.file=file;if(!store.CommitEdit(record,edit))throw new InvalidOperationException("Import registration failed");});
                    RememberEdit(store,record,edit);
                }
                else
                {
                    var previous=candidate.history;
                    var record=new PhotoRecord{id=Guid.NewGuid().ToString("N"),kind=candidate.kind==ExternalPhotoKind.OtherDay?Kinds.OtherDay:Kinds.External,
                        eventKey="external:"+candidate.hash,title=previous?.title.NullOrEmpty()==false?previous.title:Path.GetFileNameWithoutExtension(candidate.file),
                        desc="MF_Extras_ImportedNote".Translate()+(previous?.note.NullOrEmpty()==false?"\n"+previous.note:""),
                        dateLabel=previous?.date.NullOrEmpty()==false?previous.date:"MF_Extras_UnknownDate".Translate().ToString(),dateSlug=previous?.dateSlug,
                        year=previous?.year??0,ticksAbs=previous?.ticks??-1,capturedTicksAbs=-1,mapId=-1,aspect=aspect,
                        sourceSha256=candidate.hash,externalSourceHash=candidate.hash,externalSourceFile=candidate.file};
                    ExternalPhotoFiles.WriteNew(store.FolderPath,"external-photo",png,thumb,file=>{record.file=file;if(!store.CommitRecord(record))throw new InvalidOperationException("Import registration failed");});
                    RememberRecord(store,record);
                }
                return true;
            }
            catch(Exception e){failure=e.Message;Warn(e);return false;}
        }
    }
}
