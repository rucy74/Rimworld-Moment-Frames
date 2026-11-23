using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>Per-game photo registry: records (saved with the game) + image files on disk + deferred capture queue.</summary>
    public class PhotoStore : GameComponent
    {
        private class Pending
        {
            public Map map;
            public Vector3 center;
            public PhotoRecord record;
            public int expireTick;
            public int afterTick;      // not captured before this tick (an explosion clearing, a pod opening)
            public Action onDone;      // runs once the capture has succeeded, failed or been dropped (not saved)
        }

        private static void Fire(Action a)
        {
            if (a == null) return;
            try { a(); }
            catch (Exception e) { Log.Error("[MomentFrames] capture continuation failed: " + e); }
        }

        public static PhotoStore Instance => Current.Game?.GetComponent<PhotoStore>();

        private List<PhotoRecord> records = new List<PhotoRecord>();
        private List<PhotoRecord> archivedRecords = new List<PhotoRecord>();
        private List<PhotoRecord> legacyHeld;   // load only: an earlier build held mid-event shots back until the event ended ("provisional")
        private HashSet<string> marks = new HashSet<string>();
        private List<Visit> visits = new List<Visit>();   // the journey: every place the colonists have stood on, in order (Journey.cs)
        private List<LifeEvent> lifeEvents = new List<LifeEvent>();   // moments without a photo, for the colonists' own chronicles (LifeEvents.cs)
        private DescriptionHistory descriptionHistory = new DescriptionHistory();
        private SettlementChronicle settlement = new SettlementChronicle();
        public SettlementChronicle Settlement => settlement;
        public DescriptionHistory Descriptions => descriptionHistory;
        private HashSet<string> knownColonists;   // free colonists seen so far; null until the first poll, so those already there are not "new"
        private int lastBattleTick = -1;   // game tick of the last fight photographed (Battles.cs: at most one a season after the firsts)

        public int LastBattleTick { get => lastBattleTick; set => lastBattleTick = value; }

        private int lastDisasterTick = -1, lastSkyTick = -1;   // Conditions.cs: the last disaster / sky event photographed
        public int LastDisasterTick { get => lastDisasterTick; set => lastDisasterTick = value; }
        public int LastSkyTick { get => lastSkyTick; set => lastSkyTick = value; }
        private string gameId;
        private int nextIndex = 1;
        private readonly List<Pending> pending = new List<Pending>();
        private bool restoreQueued;

        public PhotoStore(Game game)
        {
            SettlementChronicle.ResetActive();
            MemoryNightMusic.Reset();
            MemoryNightSfx.Reset();
            // A new game (new or loaded) must not keep textures/materials of the previous one.
            PhotoTextureCache.Clear();
            Patch_GravshipLaunch.Reset();
            Reminisce.Clear();
            Building_PhotoProjector.ResetClock();
            GroupPhoto.Reset();
            MidEvent.Reset();
            Journey.Reset();
            PhotoCapture.Reset();
            SettlementRecordsTab.Reset();
            PhotoReactions.Reset();
            Places.Reset();
            Historic.Reset();
            Battles.Reset();
            Conditions.Reset();
        }

        /// <summary>A record was edited in place (caption): invalidate every cache keyed on Version.</summary>
        public void NotifyChanged() => Version++;

        public IReadOnlyList<PhotoRecord> Records => records;
        /// <summary>Photos moved out of the album. Only the archive tab and the bookkeeping below read them; everywhere else they are simply absent.</summary>
        public IReadOnlyList<PhotoRecord> ArchivedRecords => archivedRecords;
        /// <summary>For questions about what exists rather than what is shown: was this photographed, whose file is this.</summary>
        public IEnumerable<PhotoRecord> AllRecords => archivedRecords == null ? records : records.Concat(archivedRecords);
        public IReadOnlyList<Visit> Visits => visits;
        public IReadOnlyList<LifeEvent> LifeEvents => lifeEvents;

        internal HashSet<string> KnownColonists { get => knownColonists; set => knownColonists = value; }
        internal List<Pawn> InitialColonists = new List<Pawn>();

        public IEnumerable<LifeEvent> LifeOf(Pawn p)
        {
            string id = p?.GetUniqueLoadID();
            return id == null ? Enumerable.Empty<LifeEvent>() : lifeEvents.Where(e => e.pawnIds.Contains(id));
        }

        public void AddLife(LifeEvent e)
        {
            if (e == null) return;
            lifeEvents.Add(e);
            Version++;
        }

        /// <summary>"Places of memory" pins on the colony map are switched on (the play-settings toggle; saved with the colony).</summary>
        public bool showPlaces;
        public bool Visited(RimWorld.Planet.PlanetTile tile) => visits.Any(v => v.tile == tile);

        public void AddVisit(Visit v)
        {
            if (v == null) return;
            visits.Add(v);
            Version++;
        }
        public bool HasPending => pending.Count > 0 || PhotoCapture.Busy;

        /// <summary>Bumped whenever the record list or a file changes; UI caches compare it instead of rescanning every frame.</summary>
        public int Version { get; private set; }

        public IEnumerable<PhotoRecord> For(Pawn p) => records.Where(r => r.HasFile && r.Has(p));
        public IEnumerable<PhotoRecord> ChronicleFor(Pawn p) => p == null ? Enumerable.Empty<PhotoRecord>()
            : records.Where(r => (r.HasFile || r.eventOnly) && (r.Has(p) || AlbumPresentation.EventMatches(r, p.GetUniqueLoadID(), p.LabelShort)))
                .Concat(archivedRecords.Where(r => Kinds.KeepsLineWhenArchived(r.kind) && (r.Has(p) || AlbumPresentation.EventMatches(r, p.GetUniqueLoadID(), p.LabelShort))));

        private readonly Dictionary<string, KeyValuePair<int, int>> countCache = new Dictionary<string, KeyValuePair<int, int>>();

        /// <summary>Number of photos this pawn appears in (cached per Version — the photo tab asks every frame).</summary>
        public int CountFor(Pawn p)
        {
            if (p == null) return 0;
            string id = p.GetUniqueLoadID();
            if (countCache.TryGetValue(id, out var c) && c.Key == Version) return c.Value;
            int n = For(p).Count();
            countCache[id] = new KeyValuePair<int, int>(Version, n);
            return n;
        }

        public string GameId
        {
            get
            {
                if (gameId.NullOrEmpty()) gameId = Guid.NewGuid().ToString("N").Substring(0, 12);
                return gameId;
            }
        }

        /// <summary>Worked out again only when the folder setting or the game id changes (map displays and the album ask many times a frame, and
        /// the same string back also lets them tell cheaply that nothing moved).</summary>
        public string FolderPath
        {
            get
            {
                string setting = MomentFramesMod.Settings?.folderOverride, id = GameId;
                if (folderPath != null && setting == folderSetting && id == folderGame) return folderPath;
                string root = setting.NullOrEmpty() ? Path.Combine(GenFilePaths.SaveDataFolderPath, "MomentFrames") : setting.Trim();
                try { root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)); }
                catch { root = Path.Combine(GenFilePaths.SaveDataFolderPath, "MomentFrames"); }
                folderSetting = setting;
                folderGame = id;
                folderPath = Path.Combine(root, id);
                return folderPath;
            }
        }

        private string folderSetting, folderGame, folderPath;

        /// <summary>The image that stands for this photo everywhere (album, frames, projector, HTML): the chosen edit if there is one, else the
        /// original. Every display asks here, so picking another version needs no change anywhere else.</summary>
        public string FilePath(PhotoRecord r)
        {
            var e = r?.Shown;
            return e != null ? EditPath(e) : OriginalPath(r);
        }

        public string ThumbPath(PhotoRecord r)
        {
            var e = r?.Shown;
            return e != null ? EditThumbPath(e) : OriginalThumbPath(r);
        }

        public string OriginalPath(PhotoRecord r) => r == null || !r.HasFile ? null : ExternalPhotoImport.ResolveOriginal(this, r);
        public string OriginalThumbPath(PhotoRecord r) => r == null || !r.HasFile ? null : ExternalPhotoImport.ResolveOriginalThumb(this, r);
        public string EditPath(PhotoEdit e) => e == null || !e.HasFile ? null : ExternalPhotoImport.ResolveEdit(this, e);
        public string EditThumbPath(PhotoEdit e) => e == null || !e.HasFile ? null : ExternalPhotoImport.ResolveEditThumb(this, e);

        /// <summary>A particular version: null = whatever is shown, "" = the original, otherwise an edit id.</summary>
        public string VersionPath(PhotoRecord r, string version, bool thumb = false)
        {
            if (version == null) return thumb ? ThumbPath(r) : FilePath(r);
            if (version.Length == 0) return thumb ? OriginalThumbPath(r) : OriginalPath(r);
            var e = r?.Edit(version);
            return e == null ? null : (thumb ? EditThumbPath(e) : EditPath(e));
        }

        /// <summary>Bakes an edit of the original (crop + filter) to its own PNG and thumbnail, and makes it the version shown. The original
        /// file is only read. Null when the original is missing or the image could not be written (logged).</summary>
        public PhotoEdit SaveEdit(PhotoRecord rec, Rect crop, string filter, List<PhotoImageIcon> icons = null)
        {
            string src = OriginalPath(rec);
            if (src == null || !File.Exists(src)) return null;
            try
            {
                byte[] png = PhotoEditing.Bake(File.ReadAllBytes(src), crop, filter, out float aspect, icons);
                if (png == null) return null;
                int n = 1;
                string stem = Path.GetFileNameWithoutExtension(rec.file);
                // Edits of either format share the thumbnail name stem_eN.thumb.png, so a number is free only when no file of that stem exists.
                while (File.Exists(Path.Combine(FolderPath, stem + "_e" + n + ".png")) || File.Exists(Path.Combine(FolderPath, stem + "_e" + n + ".jpg"))
                    || File.Exists(Path.Combine(FolderPath, stem + "_e" + n + ".thumb.png"))) n++;
                var e = new PhotoEdit
                {
                    id = Guid.NewGuid().ToString("N").Substring(0, 8),
                    file = stem + "_e" + n + PhotoImageFormat.ExtensionOf(png),
                    filter = filter ?? PhotoFilters.None,
                    cx = crop.x, cy = crop.y, cw = crop.width, ch = crop.height,
                    aspect = aspect,
                    madeAbs = Find.TickManager != null ? Find.TickManager.TicksAbs : -1,
                    imageIcons = PhotoImageIcons.Copy(icons)
                };
                Directory.CreateDirectory(FolderPath);
                PhotoFiles.WriteOriginal(EditPath(e), png);
                PhotoFiles.TryDerived(EditThumbPath(e), () => {
                    byte[] thumb = null; PhotoCapture.MakeThumb(png, ref thumb); return thumb;
                });
                PhotoTextureCache.Forget(EditPath(e));
                PhotoTextureCache.Forget(EditThumbPath(e));
                rec.edits.Add(e);
                rec.shownEdit = e.id;
                Version++;
                try { ExternalPhotoImport.RememberEdit(this, rec, e); }
                catch (Exception ex) { Log.Warning("[MomentFrames] edit baseline failed: " + ex.Message); }
                return e;
            }
            catch (Exception ex)
            {
                Log.Warning("[MomentFrames] could not save the edited photo: " + ex.Message);
                return null;
            }
        }

        /// <summary>Which version stands for the photo: an edit id, or null for the original.</summary>
        public void SetShown(PhotoRecord rec, string editId)
        {
            if (rec == null) return;
            string want = editId.NullOrEmpty() || rec.Edit(editId) == null ? null : editId;
            if (rec.shownEdit == want) return;
            rec.shownEdit = want;
            Version++;
        }

        internal bool CommitRecord(PhotoRecord record)
        {
            if (record == null || record.id.NullOrEmpty() || !record.HasFile || AllRecords.Any(r => r.id == record.id || r.file == record.file)) return false;
            records.Add(record); NotifyChanged(); return true;
        }

        internal bool CommitEdit(PhotoRecord record, PhotoEdit edit)
        {
            if (record == null || !AllRecords.Contains(record) || edit == null || edit.id.NullOrEmpty() || !edit.HasFile) return false;
            if (record.edits == null) record.edits = new List<PhotoEdit>();
            if (record.edits.Any(e => e.id == edit.id || e.file == edit.file)) return false;
            record.edits.Add(edit); record.shownEdit = edit.id; NotifyChanged(); return true;
        }

        /// <summary>Removes an edited version and its files. If it was the one shown, the original comes back.</summary>
        public void DeleteEdit(PhotoRecord rec, string editId)
        {
            var e = rec?.Edit(editId);
            if (e == null) return;
            // A content-addressed baseline can be shared by multiple originals/edits; deleting a UI edit removes its raw files only.
            foreach (var path in new[] { Path.Combine(FolderPath, e.file), Path.Combine(FolderPath, Path.GetFileNameWithoutExtension(e.file) + ".thumb.png") })
            {
                if (path == null) continue;
                PhotoTextureCache.Forget(path);
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { Log.Warning("[MomentFrames] could not delete " + path + ": " + ex.Message); }
            }
            rec.edits.Remove(e);
            if (rec.shownEdit == editId) rec.shownEdit = null;
            Version++;
        }

        public PhotoRecord Get(string id) => id.NullOrEmpty() ? null : records.FirstOrDefault(r => r.id == id);
        public PhotoRecord GetAny(string id) => Get(id) ?? (id.NullOrEmpty() ? null : archivedRecords.FirstOrDefault(r => r.id == id));

        /// <summary>Moves a photo out of the album. The files stay, and whatever shows it keeps its id, so Restore puts it back where it was.</summary>
        public bool Archive(PhotoRecord rec)
        {
            if (!PhotoArchive.Offered(rec) || !records.Remove(rec)) return false;
            rec.archivedTicksAbs = Math.Max(0, Find.TickManager.TicksAbs);
            archivedRecords.Add(rec);
            Version++;
            return true;
        }

        /// <summary>Deletes archived photos for good: every file they own (PhotoArchive.FilesOf) that no other photo also uses. A moment that
        /// keeps a line in someone's chronicle (Kinds.KeepsLineWhenArchived) stays as that line without a picture; any other record goes.
        /// All saves of this colony share the folder, so an older save then shows these photos as missing. Returns the bytes freed.</summary>
        public long DeleteArchived(IList<PhotoRecord> photos)
        {
            var doomed = (photos ?? new List<PhotoRecord>()).Where(r => r != null && r.HasFile && archivedRecords.Contains(r)).Distinct().ToList();
            if (doomed.Count == 0) return 0;
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var other in AllRecords) if (!doomed.Contains(other)) keep.UnionWith(PhotoArchive.FilesOf(this, other));
            long freed = 0;
            foreach (var rec in doomed)
            {
                foreach (string path in PhotoArchive.FilesOf(this, rec))
                {
                    if (keep.Contains(path)) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        if (!info.Exists) continue;
                        long size = info.Length;
                        PhotoTextureCache.Forget(path);
                        info.Delete();
                        freed += size;
                    }
                    catch (Exception e) { Log.Warning("[MomentFrames] could not delete " + path + ": " + e.Message); }
                }
                rec.edits?.Clear(); rec.shownEdit = null;
                rec.file = null; rec.sourceSha256 = null; rec.sourceBaselineFile = null;
                if (!Kinds.KeepsLineWhenArchived(rec.kind)) archivedRecords.Remove(rec);
            }
            Version++;
            return freed;
        }

        public bool Restore(PhotoRecord rec)
        {
            if (rec == null || !archivedRecords.Remove(rec)) return false;
            rec.archivedTicksAbs = -1;
            int at = rec.ticksAbs < 0 ? -1 : records.FindIndex(r => r.ticksAbs > rec.ticksAbs);   // undated imports live at the end
            records.Insert(at < 0 ? records.Count : at, rec);
            Version++;
            return true;
        }
        public bool HasMark(string key) => !key.NullOrEmpty() && marks.Contains(key);
        public bool AddMark(string key) => !key.NullOrEmpty() && marks.Add(key);

        internal void SetCaptureDeadline(PhotoRecord rec, int ticksGameDeadline)
        {
            if (rec == null) return;
            rec.captureDeadlineTick = ticksGameDeadline;
            foreach (var p in pending.Where(p => p.record == rec)) p.expireTick = Math.Min(p.expireTick, ticksGameDeadline);
        }

        internal void SetEventTime(PhotoRecord rec, int ticksAbs)
        {
            if (rec == null) return;
            var map = Find.Maps.FirstOrDefault(m => m.uniqueID == rec.mapId);
            if (map == null) return;
            rec.ticksAbs = ticksAbs; rec.dateLabel = DateLabel(map, ticksAbs);
            rec.dateSlug = DateSlug(map, ticksAbs); rec.year = Year(map, ticksAbs);
        }

        /// <summary>Was this event key already photographed?</summary>
        public bool HasKey(string key) => !key.NullOrEmpty() && AllRecords.Any(r => r.eventKey == key);

        /// <summary>A photo turns out to show a different moment than assumed when it was taken (a role-change ritual became an appointment, a
        /// bestowing ceremony granted its title): new title, kind, people and sentence. False when nothing was photographed for the key.</summary>
        public bool Retitle(string key, string title, string kind, IEnumerable<Pawn> people, string desc = null)
        {
            var rec = key.NullOrEmpty() ? null : AllRecords.FirstOrDefault(r => r.eventKey == key);
            if (rec == null) return false;
            if (!title.NullOrEmpty()) rec.title = title;
            if (!kind.NullOrEmpty()) rec.kind = kind;
            if (people != null)
            {
                CapturedPhotoRoster.FreezeEvent(rec, people);
                if (!rec.HasFile) rec.SetPeople(people);
            }
            if (desc != null) rec.SetDescription(desc);
            NotifyChanged();
            return true;
        }

        /// <summary>Register a photo and queue its capture. Returns null if the event key was already photographed.</summary>
        public PhotoRecord Request(Map map, IntVec3 center, string kind, string eventKey, string title, string desc, IEnumerable<Pawn> people, float? span = null, float aspect = PhotoCapture.DefaultAspect, Action onDone = null, int delayTicks = 0)
        {
            Vector3 c = center.IsValid && center.InBounds(map) ? center.ToVector3Shifted() : map.Center.ToVector3Shifted();
            return Request(map, c, kind, eventKey, title, desc, people, span, aspect, onDone, delayTicks);
        }

        /// <summary>Makes the record (not yet captured) and puts it in the album. Returns null if the event key was already photographed. An empty
        /// sentence is filled from the templates for the kind.</summary>
        private PhotoRecord Create(Map map, ref Vector3 center, string kind, string eventKey, string title, string desc, IEnumerable<Pawn> people, float? span, float aspect, bool publish = true)
        {
            if (map == null || Current.ProgramState != ProgramState.Playing) return null;
            if (HasKey(eventKey)) return null;
            float fittedSpan = span ?? (MomentFramesMod.Settings?.spanCells ?? MomentFramesSettings.DefaultSpan);
            PhotoCapture.KeepInside(map, ref center, ref fittedSpan, ref aspect);
            int ticksAbs = Find.TickManager.TicksAbs;
            Vector3 frameCenter = center;
            var actors = people?.Where(p => p != null).Distinct().ToList() ?? new List<Pawn>();
            var pawns = actors.Where(p => PhotoCapture.InFrame(p, map, frameCenter, fittedSpan, aspect)).ToList();
            bool generated = desc.NullOrEmpty();
            var rec = new PhotoRecord
            {
                id = Guid.NewGuid().ToString("N"),
                kind = kind ?? "manual",
                eventKey = eventKey ?? "",
                title = CaptionText.Clean(title ?? ""),
                desc = CaptionText.Clean(desc ?? ""),
                generatedDesc = generated,
                ticksAbs = ticksAbs,
                dateLabel = DateLabel(map, ticksAbs),
                dateSlug = DateSlug(map, ticksAbs),
                spanCells = fittedSpan,
                aspect = PhotoCapture.ClampAspect(aspect),
                mapId = map.uniqueID,
                center = new Vector3(center.x, 0f, center.z),
                year = Year(map, ticksAbs)
            };
            rec.SetPeople(pawns);   // names index-aligned with ids
            CapturedPhotoRoster.FreezeEvent(rec, actors);
            PhotoLocations.Capture(rec, map);
            if (generated && publish) rec.desc = PhotoDesc.ForRecord(rec, pawns, map);
            if (publish) { records.Add(rec); Version++; }
            return rec;
        }

        internal PhotoRecord CreateBattleCandidate(Map map, Vector3 center, float span, float aspect, string key, IEnumerable<Pawn> people)
        {
            var rec = Create(map, ref center, Battles.Kind, key, "", null, people, span, aspect, false);
            if (rec != null) { rec.eventSubtype = "battle-engagement"; rec.automaticCapture = true; }
            return rec;
        }

        internal void AnnounceBattle(PhotoRecord rec)
        {
            ExternalPhotoImport.RememberRecord(this, rec);
            Announce(rec);
            PhotoFx.Play(rec);
        }

        /// <summary>Register a photo and queue its capture (taken when that map is being viewed, within a day). Centre is a world position (x/z in cells); aspect = image width/height.
        /// Returns null if the event key was already photographed. onDone (optional) runs after the capture finished or was dropped.</summary>
        public PhotoRecord Request(Map map, Vector3 center, string kind, string eventKey, string title, string desc, IEnumerable<Pawn> people, float? span = null, float aspect = PhotoCapture.DefaultAspect, Action onDone = null, int delayTicks = 0)
            => RequestCore(map, center, kind, eventKey, title, desc, people, span, aspect, onDone, delayTicks, false);

        private PhotoRecord RequestCore(Map map, Vector3 center, string kind, string eventKey, string title, string desc, IEnumerable<Pawn> people, float? span, float aspect, Action onDone, int delayTicks, bool automaticCapture)
        {
            if (SettingsCapturePolicy.ShouldCancel(automaticCapture, MomentFramesMod.Settings)) return null;
            var rec = Create(map, ref center, kind, eventKey, title, desc, people, span, aspect);
            if (rec == null) return null;
            rec.automaticCapture = automaticCapture;
            if (map != Find.CurrentMap)
            {
                rec.deferredCapture = true;
                rec.captureDeadlineTick = Find.TickManager.TicksGame + 60000;
            }
            pending.Add(new Pending { map = map, center = center, record = rec, expireTick = Find.TickManager.TicksGame + 60000, afterTick = Find.TickManager.TicksGame + delayTicks, onDone = onDone });
            return rec;
        }

        /// <summary>Automatic event producers opt in explicitly; the shared Request API remains usable by manual commands.</summary>
        internal PhotoRecord RequestAutomatic(Map map, IntVec3 center, string kind, string eventKey, string title, string desc, IEnumerable<Pawn> people, float? span = null, float aspect = PhotoCapture.DefaultAspect, Action onDone = null, int delayTicks = 0)
        {
            if (!SettingsCapturePolicy.Automatic(MomentFramesMod.Settings) || map == null) return null;
            Vector3 c = center.IsValid && center.InBounds(map) ? center.ToVector3Shifted() : map.Center.ToVector3Shifted();
            return RequestCore(map, c, kind, eventKey, title, desc, people, span, aspect, onDone, delayTicks, true);
        }

        internal PhotoRecord RequestAutomatic(Map map, Vector3 center, string kind, string eventKey, string title, string desc, IEnumerable<Pawn> people, float? span = null, float aspect = PhotoCapture.DefaultAspect, Action onDone = null, int delayTicks = 0)
            => RequestCore(map, center, kind, eventKey, title, desc, people, span, aspect, onDone, delayTicks, true);

        internal PhotoRecord RequestAutomaticNow(Map map, Vector3 center, string kind, string eventKey, string title, string desc, IEnumerable<Pawn> people, float? span, float aspect, Action onDone)
            => RequestNowCore(map, center, kind, eventKey, title, desc, people, span, aspect, onDone, true);

        /// <summary>Explicit large building bounds, with the same queue, automatic gate and failure handling as normal photos.</summary>
        internal PhotoRecord RequestAutomaticArea(Map map, CellRect area, string kind, string eventKey, string title, string desc, IEnumerable<Pawn> people)
        {
            if (map == null) return null;
            area = area.ClipInsideMap(map);
            if (area.Width < 1 || area.Height < 1) return null;
            var center = new Vector3(area.minX + area.Width / 2f, 0f, area.minZ + area.Height / 2f);
            float aspect = area.Width / (float)area.Height;
            var rec = RequestCore(map, center, kind, eventKey, title, desc, people, area.Height, aspect, null, 0, true);
            if (rec == null) return null;
            rec.eventSubtype = "mf-survey-area"; rec.center = center; rec.spanCells = area.Height; rec.aspect = aspect;
            rec.SetPeople(people?.Where(p => PhotoCapture.VisualPosition(p, map, out var d) &&
                d.x >= area.minX && d.x < area.minX + area.Width && d.z >= area.minZ && d.z < area.minZ + area.Height));
            // The event roster remains the supplied actors. Only the shutter roster describes who was visible in the area.
            var request = pending.LastOrDefault(p => p.record == rec);
            if (request != null) request.center = center;
            return rec;
        }

        /// <summary>Register a photo and start its capture right now (end of the next frame), bypassing the queue. Returns null — and nothing is scheduled — if the
        /// capture cannot start immediately (map not current, another capture busy). Used where something is postponed until the photo exists; onDone always fires when a record is returned.</summary>
        public PhotoRecord RequestNow(Map map, Vector3 center, string kind, string eventKey, string title, string desc, IEnumerable<Pawn> people, float? span, float aspect, Action onDone, bool preserveLighting = false, Action<PhotoRecord> prepare = null)
            => RequestNowCore(map, center, kind, eventKey, title, desc, people, span, aspect, onDone, false, preserveLighting, prepare);

        private PhotoRecord RequestNowCore(Map map, Vector3 center, string kind, string eventKey, string title, string desc, IEnumerable<Pawn> people, float? span, float aspect, Action onDone, bool automaticCapture, bool preserveLighting = false, Action<PhotoRecord> prepare = null)
        {
            if (SettingsCapturePolicy.ShouldCancel(automaticCapture, MomentFramesMod.Settings)) return null;
            if (map == null || map != Find.CurrentMap || PhotoCapture.Busy) return null;
            var rec = Create(map, ref center, kind, eventKey, title, desc, people, span, aspect);
            if (rec == null) return null;
            rec.automaticCapture = automaticCapture;
            // Face overrides and reaction mode must exist before Capture prepares the pawn renderers.
            try { prepare?.Invoke(rec); }
            catch (Exception e) { Drop(rec); Log.Warning("[MomentFrames] photo preparation failed: " + e); return null; }
            if (rec.kind == "gravship")
            {
                rec.eventPeopleNames = new List<string>(rec.people);
                rec.eventPeopleIds = new List<string>(rec.peopleIds);
            }
            if (!PhotoCapture.Capture(map, center, rec.spanCells, result => { try { OnCaptured(rec, result); } finally { Fire(onDone); } }, rec.Aspect, preserveLighting || Kinds.RealLight(rec.kind), rec, FolderPath))
            {
                Drop(rec);
                return null;
            }
            return rec;
        }

        private readonly HashSet<string> retried = new HashSet<string>();   // not saved: at most one more try per moment per session

        /// <summary>A photo that was never taken leaves the album. A one-time moment is marked under its event key before it is requested, and
        /// the mark alone kept it from ever being tried again (a map not viewed within the day, a black render): it gets one more try. Only
        /// one, so a render that keeps failing cannot turn into a failure message on every poll.</summary>
        internal bool CancelPending(string photoId)
        {
            var rec = Get(photoId);
            if (rec == null || rec.HasFile || rec.eventOnly) return false;
            // Cancel this owner's unfinished record only. Other captures keep their camera lease.
            rec.captureCancelled = true;
            var removed = pending.Where(p => ReferenceEquals(p.record, rec)).ToList();
            pending.RemoveAll(p => ReferenceEquals(p.record, rec));
            Drop(rec);
            foreach (var request in removed) Fire(request.onDone);
            return true;
        }

        private void Drop(PhotoRecord rec)
        {
            if (rec != null && !rec.HasFile && (rec.deferredCapture || rec.kind == "gravship")
                && !SettingsCapturePolicy.ShouldCancel(rec.automaticCapture, MomentFramesMod.Settings))
            {
                if (!rec.eventOnly)
                {
                    rec.eventOnly = true;
                    rec.SetPeople(null); // The frozen event roster is separate from an unmade photo's visible people.
                    rec.desc = CaptionText.Clean(rec.desc + "\n" + (rec.kind == "gravship" ? "MF_DepartureRecordOnly" : "MF_DeferredRecordOnly").Translate());
                    Version++;
                }
                return;
            }
            if (records.Remove(rec)) Version++;
            string key = rec?.eventKey;
            // A pet's first ownership remains remembered even if that photograph fails;
            // re-taming later must not invent a new first day.
            if (rec.kind != PetMilestones.Kind && !key.NullOrEmpty() && marks.Contains(key) && retried.Add(key)) marks.Remove(key);
        }

        public void Remove(PhotoRecord rec)
        {
            if (rec == null) return;
            records.Remove(rec);
            Version++;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].record != rec) continue;
                var p = pending[i];
                pending.RemoveAt(i);
                Fire(p.onDone);
            }
        }

        /// <summary>After a load, records that never got their image (event on another map, then saved) are queued again.</summary>
        private void RestorePendingAfterLoad()
        {
            restoreQueued = false;
            for (int i = records.Count - 1; i >= 0; i--)
            {
                var rec = records[i];
                if (rec.HasFile || rec.eventOnly) continue;
                Map m = Find.Maps.FirstOrDefault(x => x.uniqueID == rec.mapId);
                if (m == null || !rec.HasCenter || Conditions.CaptureStatus(rec, m) == Conditions.CaptureState.Ended)
                {
                    Drop(rec);
                    continue;
                }
                if (pending.Any(p => p.record == rec)) continue;
                if (!RestoreCaptureBounds(rec, m)) { Drop(rec); continue; }
                int expiry = Find.TickManager.TicksGame + 60000;
                if (rec.captureDeadlineTick >= 0) expiry = Math.Min(expiry, rec.captureDeadlineTick);
                else if (rec.kind == Conditions.KindSky || rec.kind == Conditions.KindDisaster)
                    rec.captureDeadlineTick = expiry; // legacy pending: keep this restored limit through a final-frame wait or another load
                pending.Add(new Pending { map = m, center = rec.center, record = rec, expireTick = expiry });
            }
        }

        internal static bool RestoreCaptureBounds(PhotoRecord rec, Map map)
        {
            if (rec.eventSubtype != "mf-survey-area")
            {
                PhotoCapture.KeepInside(map, ref rec.center, ref rec.spanCells, ref rec.aspect);
                return true;
            }
            var area = ExplicitCaptureArea(rec, map);
            if (area.Width < 1 || area.Height < 1) return false;
            rec.center = new Vector3(area.minX + area.Width / 2f, 0f, area.minZ + area.Height / 2f);
            rec.spanCells = area.Height; rec.aspect = area.Width / (float)area.Height;
            return true;
        }

        internal static CellRect ExplicitCaptureArea(PhotoRecord rec, Map map)
        {
            float halfH = rec.spanCells / 2f, halfW = rec.spanCells * rec.Aspect / 2f;
            return CellRect.FromLimits(Mathf.RoundToInt(rec.center.x - halfW), Mathf.RoundToInt(rec.center.z - halfH),
                Mathf.RoundToInt(rec.center.x + halfW) - 1, Mathf.RoundToInt(rec.center.z + halfH) - 1).ClipInsideMap(map);
        }

        /// <summary>Photograph the same map area as an existing photo (before/after). Queued like an event photo. Null if that map is gone.</summary>
        public PhotoRecord Reshoot(PhotoRecord original)
        {
            if (original == null) return null;
            Map map = Find.Maps.FirstOrDefault(m => m.uniqueID == original.mapId);
            if (map == null || !original.HasCenter) return null;
            bool explicitArea = SettlementChronicle.IsSurvey(original) || original.eventSubtype == "mf-survey-area";
            var rect = explicitArea ? ExplicitCaptureArea(original, map) : PhotoCapture.CoveredRect(original.center, original.spanCells, map, original.Aspect);
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => rect.Contains(p.Position)).ToList();
            var rec = Request(map, original.center, Kinds.Reshoot, "", "", "", people, original.spanCells, original.Aspect);
            if (rec == null) return null;
            rec.reshootOf = original.id;
            rec.captureReactionMode = original.captureReactionMode;
            rec.reactionPawnIds = new List<string>(original.reactionPawnIds ?? new List<string>());
            rec.reactionIcons = new List<int>(original.reactionIcons ?? new List<int>());
            if (explicitArea)
            {
                rec.eventSubtype = "mf-survey-area";
                rec.center = original.center; rec.spanCells = original.spanCells; rec.aspect = original.Aspect;
                var queued = pending.LastOrDefault(p => p.record == rec);
                if (queued != null) queued.center = rec.center;
            }
            string baseTitle = original.title;
            rec.title = "MF_ReshootTitle".Translate(baseTitle.NullOrEmpty() ? original.dateLabel : baseTitle);
            return rec;
        }

        /// <summary>Copies a photo from another game's folder into this one as a relic record: no people (their ids belong to that save), never a
        /// milestone, dated today, the original date kept in the label. Null when it was already imported or the file cannot be read.</summary>
        public PhotoRecord ImportRelic(RelicSource src, RelicPhoto p)
        {
            if (src == null || p == null || Current.ProgramState != ProgramState.Playing) return null;
            string key = src.Key(p);
            if (AllRecords.Any(r => r.eventKey == key)) return null;
            try
            {
                byte[] png = File.ReadAllBytes(src.FullPath(p));
                string thumbSrc = src.ThumbPath(p);
                byte[] thumb = File.Exists(thumbSrc) ? File.ReadAllBytes(thumbSrc) : null;
                float aspect = PhotoCapture.MakeThumb(png, ref thumb);   // image proportions; makes the thumbnail when that game had none
                Map home = Find.AnyPlayerHomeMap ?? Find.CurrentMap;
                int now = Find.TickManager.TicksAbs;
                string when = p.dateLabel.NullOrEmpty() ? p.dateSlug : p.dateLabel;
                var rec = new PhotoRecord
                {
                    id = Guid.NewGuid().ToString("N"),
                    kind = Kinds.Relic,
                    importedColonyId = src.gameId, importedColonyName = src.name,
                    eventKey = key,
                    title = p.title.NullOrEmpty() ? "MF_Kind_relic".Translate().ToString() : p.title,
                    desc = "MF_RelicDesc".Translate(src.name).ToString() + (p.desc.NullOrEmpty() ? "" : "\n" + p.desc),
                    ticksAbs = now,
                    capturedTicksAbs = now,
                    dateLabel = "MF_RelicDate".Translate(when).ToString(),
                    dateSlug = p.dateSlug.NullOrEmpty() ? DateSlug(home, now) : p.dateSlug,
                    year = Year(home, now),
                    spanCells = MomentFramesMod.Settings?.spanCells ?? MomentFramesSettings.DefaultSpan,
                    aspect = PhotoCapture.ClampAspect(aspect),
                    mapId = -1
                };
                Directory.CreateDirectory(FolderPath);
                string name = $"{nextIndex:0000}_{rec.dateSlug}_{Slug(rec.kind)}_{Slug(rec.title)}_{rec.id.Substring(0, 8)}";
                nextIndex++;
                rec.file = name + PhotoImageFormat.ExtensionOf(png);
                PhotoFiles.WriteOriginal(Path.Combine(FolderPath, rec.file), png);
                if (thumb != null) PhotoFiles.TryDerived(ThumbPath(rec), () => thumb);
                records.Add(rec);
                Version++;
                ExternalPhotoImport.RememberRecord(this, rec);
                return rec;
            }
            catch (Exception e)
            {
                Log.Warning("[MomentFrames] could not import " + p.file + ": " + e.Message);
                return null;
            }
        }

        public PhotoRecord LatestReshootOf(PhotoRecord original) =>
            original == null ? null : records.Where(r => r.HasFile && r.reshootOf == original.id).OrderByDescending(r => r.ticksAbs).FirstOrDefault();

        private int lastCheckedDay = -1;   // not saved: the daily check simply runs once more after a load (marks make it idempotent)
        private int lastPollTick = -1;     // not saved: same, the short poll is idempotent through marks

        public override void GameComponentOnGUI() { PhotoReactions.Draw(); PhotoGatheringHUD.Draw(); }

        public override void GameComponentUpdate()
        {
            PhotoThumbnailLoader.Update();
            FramePhotoTextures.Update();
            PhotoPreviews.Update();
            PhotoReactions.Update();
            MemoryNightMusic.Update();
            MemoryNightSfx.Update();
            Probe.Update();
            if (Current.ProgramState != ProgramState.Playing) return;
            // Projector slideshow clock: real seconds while unpaused (one float add per frame).
            if (!Find.TickManager.Paused) Building_PhotoProjector.Advance(Time.deltaTime);
            // Once per in-game day (an int compare per frame): anniversaries, population milestones, "years ago today" letters.
            int day = GenDate.DaysPassed;
            if (day != lastCheckedDay)
            {
                lastCheckedDay = day;
                try { Historic.DailyCheck(this, Find.TickManager.TicksAbs); }
                catch (Exception e) { Log.Warning("[MomentFrames] daily check failed: " + e); }
            }
            int tick = Find.TickManager.TicksGame;
            if (lastPollTick < 0 || tick - lastPollTick >= Historic.PollTicks || tick < lastPollTick)
            {
                lastPollTick = tick;
                Historic.FrequentCheck(this, tick);
                try { MomentFrames.LifeEvents.Poll(this); }
                catch (Exception e) { Log.WarningOnce("[MomentFrames] life events poll failed: " + e, 0x4D464C31); }
                try { Battles.Poll(this, tick); }
                catch (Exception e) { Log.WarningOnce("[MomentFrames] fight poll failed: " + e, 0x4D464231); }
                try { Conditions.Poll(this, tick); }
                catch (Exception e) { Log.WarningOnce("[MomentFrames] sky poll failed: " + e, 0x4D465331); }
            }
            if (restoreQueued) RestorePendingAfterLoad();
            try { settlement.Update(this); }
            catch (Exception e) { Log.WarningOnce("[MomentFrames] settlement records update failed: " + e, 0x4D465343); }
            if (pending.Count == 0 || PhotoCapture.Busy) return;
            for (int i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                bool dead = SettingsCapturePolicy.ShouldCancel(p.record.automaticCapture, MomentFramesMod.Settings) || p.map == null || !Find.Maps.Contains(p.map) || Find.TickManager.TicksGame > p.expireTick
                    || (p.record.captureDeadlineTick >= 0 && Find.TickManager.TicksGame > p.record.captureDeadlineTick) || !records.Contains(p.record);
                if (dead)
                {
                    pending.RemoveAt(i);
                    Drop(p.record);
                    Fire(p.onDone);
                    return;
                }
                var conditionState = Conditions.CaptureStatus(p.record, p.map);
                if (conditionState == Conditions.CaptureState.Ended)
                {
                    pending.RemoveAt(i); Drop(p.record); Fire(p.onDone); return;
                }
                if (conditionState == Conditions.CaptureState.Wait || p.map != Find.CurrentMap || Find.TickManager.TicksGame < p.afterTick) continue;
                pending.RemoveAt(i);
                var rec = p.record;
                Action onDone = p.onDone;
                if (!RequiredSubjectsPresent(rec, p.map)) { Drop(rec); Fire(onDone); return; }
                if (!BattleSubjectPresent(rec, p.map)) { Fire(onDone); return; }
                Action<PhotoCapture.Result> captured = result => { try { OnCaptured(rec, result); } finally { Fire(onDone); } };
                bool started;
                if (rec.eventSubtype == "mf-survey-area")
                {
                    var area = ExplicitCaptureArea(rec, p.map);
                    started = PhotoCapture.CaptureArea(p.map, area, captured, 4096, rec, FolderPath);
                }
                else started = PhotoCapture.Capture(p.map, p.center, rec.spanCells, captured, rec.Aspect, Kinds.RealLight(rec.kind), rec, FolderPath);
                if (!started)
                {
                    Drop(rec);
                    Fire(onDone);
                }
                return;
            }
        }

        /// <summary>Commit all five survey images together. Unique new files only; existing photographs are never overwritten.</summary>
        internal bool StoreSurvey(Map map, SettlementSnapshot snapshot, PhotoCapture.Result[] results, CellRect[] areas)
        {
            try
            {
                using (var write = BeginStoreSurvey(map, snapshot, results, areas))
                    while (write.MoveNext()) if (write.Current) return true;
            }
            catch (Exception e) { Log.Warning("[MomentFrames] survey commit failed: " + e); }
            return false;
        }
        internal IEnumerator<bool> BeginStoreSurvey(Map map, SettlementSnapshot snapshot, PhotoCapture.Result[] results, CellRect[] areas)
        {
            if (PhotoStore.Instance != this || map != Find.CurrentMap || results?.Length != 5 || areas?.Length != 5 ||
                results.Any(r => r?.png == null) || snapshot.complete || snapshot.failed ||
                snapshot.automatic && (!SettingsCapturePolicy.Automatic(MomentFramesMod.Settings) || MomentFramesMod.Settings.settlementPeriod == 0)) yield break;
            var added = new List<PhotoRecord>(); var written = new List<string>(); bool committed = false;
            int firstIndex = nextIndex; nextIndex += 5;
            try
            {
                Directory.CreateDirectory(FolderPath);
                int now = Find.TickManager.TicksAbs;
                SettlementLayout.Period(snapshot.stamp, out int year, out int quarter);
                for (int i = 0; i < 5; i++)
                {
                    if (!SurveyCurrent(map, snapshot)) yield break;
                    CellRect area = areas[i];
                    int capturedAbs = results[i].capturedAbs >= 0 ? results[i].capturedAbs : now;
                    string part = i == 0 ? "MF_Phase2_WholeMap".Translate().ToString() : ("MF_Phase2_Quarter" + (i - 1)).Translate().ToString();
                    var record = new PhotoRecord {
                        id = Guid.NewGuid().ToString("N"), kind = i == 0 ? SettlementChronicle.OverviewKind : SettlementChronicle.QuarterKind,
                        title = "MF_Phase2_SurveyTitle".Translate(year, quarter, part), desc = "MF_Phase2_SurveyNote".Translate(part, area.Width, area.Height), generatedDesc = true,
                        ticksAbs = capturedAbs, capturedTicksAbs = capturedAbs, year = Year(map, capturedAbs), mapId = map.uniqueID,
                        dateSlug = DateSlug(map, capturedAbs), dateLabel = DateLabel(map, capturedAbs), spanCells = area.Height,
                        aspect = area.Width / (float)area.Height, center = new Vector3(area.minX + area.Width / 2f, 0f, area.minZ + area.Height / 2f),
                        automaticCapture = snapshot.automatic, eventKey = "survey:" + snapshot.id + ":" + i
                    };
                    PhotoLocations.Capture(record, map);
                    if (i > 0 && results[i].capturedPeopleIds != null)
                    {
                        record.peopleIds = new List<string>(results[i].capturedPeopleIds);
                        record.people = new List<string>(results[i].capturedPeopleNames ?? new List<string>());
                    }
                    else if (i == 0)
                    {
                        // The overview is a composite, not a fifth simultaneous shutter. Keep the union of the four real render rosters.
                        var known = new HashSet<string>();
                        for (int partIndex = 1; partIndex < 5; partIndex++)
                            for (int person = 0; person < (results[partIndex].capturedPeopleIds?.Count ?? 0); person++)
                            {
                                string id = results[partIndex].capturedPeopleIds[person];
                                if (!known.Add(id)) continue;
                                record.peopleIds.Add(id);
                                record.people.Add(person < (results[partIndex].capturedPeopleNames?.Count ?? 0)
                                    ? results[partIndex].capturedPeopleNames[person] : id);
                            }
                    }
                    string name = $"{firstIndex + i:0000}_{record.dateSlug}_survey_{record.id}";
                    record.file = name + PhotoImageFormat.ExtensionOf(results[i].png);
                    WriteNew(Path.Combine(FolderPath, record.file), results[i].png, written);
                    snapshot.writtenImages=i+1;
                    string thumbPath = Path.Combine(FolderPath, name + ".thumb.png");
                    if (results[i].thumb != null && PhotoFiles.TryDerived(thumbPath, () => results[i].thumb)) written.Add(thumbPath);
                    // Only immutable bytes and a captured folder path go to the worker. Record/Unity state stays here.
                    string folder = FolderPath;
                    byte[] png = results[i].png, thumb = results[i].thumb;
                    var protection = System.Threading.Tasks.Task.Run(() => {
                        try {
                            string hash = ExternalPhotoFiles.Hash(png);
                            string baseline = ExternalPhotoFiles.Preserve(folder, hash, png, thumb);
                            return Tuple.Create(hash, baseline, (Exception)null);
                        }
                        catch (Exception error) { return Tuple.Create((string)null, (string)null, error); }
                    });
                    while (!protection.IsCompleted)
                    {
                        if (!SurveyCurrent(map, snapshot)) yield break;
                        yield return false;
                    }
                    if (!SurveyCurrent(map, snapshot)) yield break;
                    var protectedPhoto = protection.Result;
                    record.sourceSha256 = protectedPhoto.Item1;
                    record.sourceBaselineFile = protectedPhoto.Item2;
                    if (protectedPhoto.Item3 != null) Log.Warning("[MomentFrames] survey baseline unavailable; original retained: " + protectedPhoto.Item3.Message);
                    PhotoPreviews.Remember(OriginalPath(record), results[i].preview);
                    added.Add(record);
                    yield return false; // one image and its immutable backup per frame
                }
                if (!SurveyCurrent(map, snapshot)) yield break;
                records.AddRange(added); committed = true;
                snapshot.photoIds = added.Select(r => r.id).ToList(); Version++;
                foreach (var record in added) ExternalPhotoImport.RememberRecord(this, record);
                yield return true;
            }
            finally
            {
                if (!committed) foreach (string path in written)
                    try { File.Delete(path); } catch (Exception cleanup) { Log.Warning("[MomentFrames] survey cleanup failed: " + cleanup.Message); }
            }
        }
        private bool SurveyCurrent(Map map, SettlementSnapshot snapshot) => ReferenceEquals(Instance, this) && map == Find.CurrentMap
            && !snapshot.complete && !snapshot.failed && (!snapshot.automatic || SettingsCapturePolicy.Automatic(MomentFramesMod.Settings) && MomentFramesMod.Settings.settlementPeriod > 0);
        private static void WriteNew(string path, byte[] data, List<string> written)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { written.Add(path); stream.Write(data, 0, data.Length); }
        }

        private void OnCaptured(PhotoRecord rec, PhotoCapture.Result result)
        {
            try { StoreCaptured(rec, result); }
            finally
            {
                // What the worker wrote for a photo that was not kept does not stay behind; a copy the record now points to does.
                var prepared = result?.prepared;
                prepared?.Release(prepared.In(FolderPath) && rec.sourceSha256 == prepared.hash);
            }
        }

        private void StoreCaptured(PhotoRecord rec, PhotoCapture.Result result)
        {
            if (!records.Contains(rec) || rec.HasFile) return; // cancelled or completed requests must not write/reword another image
            if (SettingsCapturePolicy.ShouldCancel(rec.automaticCapture, MomentFramesMod.Settings)) { Drop(rec); return; }
            Map map = Find.Maps.FirstOrDefault(m => m.uniqueID == rec.mapId);
            if ((rec.kind == Conditions.KindSky || rec.kind == Conditions.KindDisaster) &&
                rec.captureDeadlineTick >= 0 && Find.TickManager.TicksGame > rec.captureDeadlineTick)
            {
                Drop(rec); return;
            }
            var conditionState = Conditions.CaptureStatus(rec, map);
            if (conditionState != Conditions.CaptureState.Ready)
            {
                if (conditionState == Conditions.CaptureState.Wait && rec.captureDeadlineTick >= Find.TickManager.TicksGame)
                    pending.Add(new Pending { map = map, center = rec.center, record = rec, expireTick = rec.captureDeadlineTick });
                else Drop(rec);
                return;
            }
            // Writing a large picture takes seconds: who was there at the shutter counts, wherever they have walked since.
            if (!(result?.subjectsAtShutter ?? RequiredSubjectsPresent(rec, map)))
            {
                Drop(rec); Messages.Message((rec.kind==PetPhotos.Kind?"MF_PetMissingSubject":"MF_PhotoMissingSubject").Translate(), MessageTypeDefOf.RejectInput, false); return;
            }
            if (!BattleSubjectPresent(rec, map)) return;
            var photographed = result?.captionContext?.People ?? CapturedPhotoRoster.Observe(rec, map);
            var shutterIds = result?.capturedPeopleIds ?? photographed.Select(p => p.GetUniqueLoadID()).ToList();
            var shutterNames = result?.capturedPeopleNames ?? photographed.Select(p => p.LabelShort).ToList();
            if (result == null || result.png == null || shutterIds.Count != shutterNames.Count
                || (!rec.requiredPerson.NullOrEmpty() && !shutterIds.Contains(rec.requiredPerson)))
            {
                Log.Warning("[MomentFrames] capture rejected: image=" + (result?.png != null) + ", required=" + rec.requiredPerson
                    + ", registered=" + string.Join(",", rec.peopleIds) + ", visible=" + string.Join(",", photographed.Select(p => p.GetUniqueLoadID())));
                Drop(rec);
                Messages.Message("MF_PhotoFailed".Translate(rec.title), MessageTypeDefOf.RejectInput, false);
                return;
            }
            bool changed = !rec.peopleIds.SequenceEqual(shutterIds);
            rec.peopleIds = new List<string>(shutterIds);
            rec.people = new List<string>(shutterNames);
            bool written = false;
            // If the folder setting changed while the worker wrote, its files are in the old place and this photo is written the ordinary way.
            var prepared = result.prepared != null && result.prepared.In(FolderPath) ? result.prepared : null;
            try
            {
                Directory.CreateDirectory(FolderPath);
                // The record id suffix keeps names unique even across save branches that restore an older nextIndex.
                string name = $"{nextIndex:0000}_{rec.dateSlug}_{Slug(rec.kind)}_{Slug(rec.title)}_{rec.id.Substring(0, 8)}";
                nextIndex++;
                string file = name + PhotoImageFormat.ExtensionOf(result.png);
                PhotoFiles.WriteOriginal(Path.Combine(FolderPath, file), result.png, prepared?.image);
                if (result.thumb != null) PhotoFiles.TryDerived(Path.Combine(FolderPath, name + ".thumb.png"), () => result.thumb, prepared?.thumb);
                // The original is authoritative; thumbnails can be regenerated if their save failed.
                // Keep file unset until selection so the completed-photo guard remains unconditional everywhere else.
                if (PhotoDesc.CanApplyCaptured(rec))
                {
                    try
                    {
                        if (!PhotoDesc.TryApplyCaptured(rec, result.captionContext) && changed)
                            rec.desc = PhotoDesc.ForRecord(rec, photographed, map);
                    }
                    catch (Exception e) { try { Log.Warning("[MomentFrames] could not finish the photo caption: " + e.Message); } catch { } }
                }
                rec.file = file;
                rec.capturedTicksAbs = result.capturedAbs >= 0 ? result.capturedAbs : Find.TickManager.TicksAbs;
                written = true;
                Version++;
            }
            catch (Exception e)
            {
                Log.Error("[MomentFrames] Could not write photo file: " + e);
                rec.file = null;
                Drop(rec);
            }
            // Effects are kept outside the save path: a broken effect must never turn a saved photo into a failure.
            if (written)
            {
                try { PhotoCapture.ApplyPeopleBox(rec, result); }
                catch (Exception e) { try { Log.Warning("[MomentFrames] could not measure where the people stood: " + e.Message); } catch { } }
                try { ExternalPhotoImport.RememberCaptured(this, rec, prepared); }
                catch (Exception e) { try { Log.Warning("[MomentFrames] photo baseline failed: " + e.Message); } catch { } }
                PhotoPreviews.Remember(OriginalPath(rec), result.preview);
                try { Announce(rec); }
                catch (Exception e) { try { Log.Warning("[MomentFrames] photo announcement failed: " + e.Message); } catch { } }
                try { Conditions.NotifySaved(this, rec); }
                catch (Exception e) { try { Log.Warning("[MomentFrames] photo event notification failed: " + e.Message); } catch { } }
                try { PhotoFx.Play(rec); }
                catch (Exception e) { try { Log.Warning("[MomentFrames] photo effect failed: " + e.Message); } catch { } }
                try { PhotoReactions.NotifySaved(result.reactions); }
                catch (Exception e) { try { Log.Warning("[MomentFrames] photo reaction failed: " + e.Message); } catch { } }
            }
        }

        internal PhotoRecord RecordEvent(Map map, string kind, string key, string title, string note)
        {
            Vector3 center = map.Center.ToVector3Shifted();
            var rec = Create(map, ref center, kind, key, title, note, null, 9f, PhotoCapture.DefaultAspect);
            if (rec != null) rec.eventOnly = true;
            return rec;
        }

        internal static bool RequiredSubjectsPresent(PhotoRecord rec, Map map)
        {
            if (rec.requiredSubjects == null || rec.requiredSubjects.Count == 0) return true;
            return map != null && rec.requiredSubjects.All(id => PhotoCapture.VisiblePawns(map).Any(p => p.GetUniqueLoadID() == id
                && !p.Dead && p.Faction == Faction.OfPlayer && (!p.RaceProps.Animal || PetMilestones.OwnedAnimal(p))
                && rec.Has(p) && PhotoCapture.InFrame(p, map, rec.center, rec.spanCells, rec.Aspect)));
        }

        private bool BattleSubjectPresent(PhotoRecord rec, Map map)
        {
            if (rec.requiredThings == null || rec.requiredThings.Count == 0) return true;
            var rect = map != null ? PhotoCapture.CoveredRect(rec.center, rec.spanCells, map, rec.Aspect) : CellRect.Empty;
            if (map != null && map.listerThings.AllThings.Any(t => rec.requiredThings.Contains(t.ThingID) && rect.Contains(t.Position))) return true;
            rec.eventOnly = true;
            rec.desc = (rec.kind == "gravship" ? "MF_DepartureRecordOnly" : "MF_BattleNoSubject").Translate();
            rec.SetPeople(null); Version++;
            return false;
        }

        /// <summary>"Photo taken" in the message log. The shutter sound / card replaces the chime when effects are on.</summary>
        private static void Announce(PhotoRecord rec)
        {
            bool fx = MomentFramesMod.Settings != null && (MomentFramesMod.Settings.fxShutter || MomentFramesMod.Settings.fxPopup);
            Messages.Message((rec.kind == Kinds.Birthday ? "MF_PhotoTakenBirthday" : "MF_PhotoTaken").Translate(rec.title), fx ? MessageTypeDefOf.SilentInput : MessageTypeDefOf.PositiveEvent, false);
        }

        private static string Slug(string s)
        {
            if (s.NullOrEmpty()) return "x";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
                if (sb.Length >= 40) break;
            }
            string r = sb.ToString().Trim('-');
            return r.Length == 0 ? "x" : r;
        }

        private static Vector2 DateLocation(PlanetTile tile)
        {
            try { if (tile.Valid && Find.WorldGrid != null) return Find.WorldGrid.LongLatOf(tile); }
            catch { }
            return Vector2.zero; // a world-only event still has a calendar date, even without a map or valid tile
        }

        internal static string DateLabel(Map map, int ticksAbs) => DateLabel(map?.Tile ?? PlanetTile.Invalid, ticksAbs);
        internal static string DateLabel(PlanetTile tile, int ticksAbs) => GenDate.DateFullStringAt(ticksAbs, DateLocation(tile));
        internal static int Year(Map map, int ticksAbs) => Year(map?.Tile ?? PlanetTile.Invalid, ticksAbs);
        internal static int Year(PlanetTile tile, int ticksAbs) => GenDate.Year(ticksAbs, DateLocation(tile).x);

        private static string DateSlug(Map map, int ticksAbs)
        {
            try
            {
                float lon = Find.WorldGrid.LongLatOf(map.Tile).x;
                int year = GenDate.Year(ticksAbs, lon);
                int q = (int)GenDate.Quadrum(ticksAbs, lon) + 1;
                int day = GenDate.DayOfQuadrum(ticksAbs, lon) + 1;
                return $"{year}-{q}-{day:00}";
            }
            catch { return "0000-0-00"; }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            MemoryNightMusic.ExposeData();
            MemoryNightSfx.ExposeData();
            Battles.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // Freeze legacy baselines before their fields are serialized, not after the records have been written.
                try { ExternalPhotoImport.ObserveCurrent(this); }
                catch (Exception e) { Log.Warning("[MomentFrames] photo baseline scan failed: " + e.Message); }
            }
            Scribe_Values.Look(ref gameId, "gameId");
            Scribe_Values.Look(ref nextIndex, "nextIndex", 1);
            Scribe_Collections.Look(ref records, "records", LookMode.Deep);
            Scribe_Collections.Look(ref archivedRecords, "archivedRecords", LookMode.Deep);
            if (Scribe.mode != LoadSaveMode.Saving) Scribe_Collections.Look(ref legacyHeld, "provisional", LookMode.Deep);
            Scribe_Collections.Look(ref marks, "marks", LookMode.Value);
            Scribe_Collections.Look(ref visits, "visits", LookMode.Deep);
            Scribe_Collections.Look(ref lifeEvents, "lifeEvents", LookMode.Deep);
            Scribe_Deep.Look(ref descriptionHistory, "descriptionHistory");
            Scribe_Deep.Look(ref settlement, "settlementChronicle");
            if (settlement == null) settlement = new SettlementChronicle();
            if (descriptionHistory == null) descriptionHistory = new DescriptionHistory();
            Scribe_Collections.Look(ref knownColonists, "knownColonists", LookMode.Value);
            Scribe_Collections.Look(ref InitialColonists, "initialColonists", LookMode.Reference);
            Scribe_Values.Look(ref lastBattleTick, "lastBattleTick", -1);
            Scribe_Values.Look(ref lastDisasterTick, "lastDisasterTick", -1);
            Scribe_Values.Look(ref lastSkyTick, "lastSkyTick", -1);
            Scribe_Values.Look(ref showPlaces, "showPlaces", false);
            // Beside the photos, a small index so a later colony can bring them over with their titles and dates (never throws).
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                RelicImport.WriteIndex(this);
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (records == null) records = new List<PhotoRecord>();
                if (visits == null) visits = new List<Visit>();
                visits.RemoveAll(v => v == null);
                if (lifeEvents == null) lifeEvents = new List<LifeEvent>();
                lifeEvents.RemoveAll(e => e == null || e.kind.NullOrEmpty());
                records.RemoveAll(r => r == null || r.id.NullOrEmpty());
                if (archivedRecords == null) archivedRecords = new List<PhotoRecord>();
                archivedRecords.RemoveAll(r => r == null || r.id.NullOrEmpty());
                // Older saves held mid-event shots back until the event ended; they are simply album photos now.
                if (legacyHeld != null)
                    foreach (var r in legacyHeld)
                        if (r != null && !r.id.NullOrEmpty() && r.HasFile && !records.Any(x => x.id == r.id || (!r.eventKey.NullOrEmpty() && x.eventKey == r.eventKey)))
                            records.Add(r);
                legacyHeld = null;
                Version++;
                if (marks == null) marks = new HashSet<string>();
                pending.Clear();
                restoreQueued = true;
            }
        }
    }
}
