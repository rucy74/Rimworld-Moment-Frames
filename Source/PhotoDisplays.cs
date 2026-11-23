using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Something on the map that shows photos: the single frames, the collage board and the memorial stand (fixed — the shown photos
    /// only change through the player) and the projector (changes by itself: a recreation target, but not indexed for "remember when?").</summary>
    public interface IPhotoDisplay
    {
        /// <summary>Records shown right now (files present).</summary>
        IEnumerable<PhotoRecord> Displayed { get; }
        bool FixedDisplay { get; }
    }

    /// <summary>Free-standing 3x2 board with four photos side by side. Its own def and its own Scribe key (photoIds),
    /// so the single-photo frames (photoId) are untouched — existing saves keep every hung photo.</summary>
    [StaticConstructorOnStartup]
    public class Building_PhotoCollage : Building, IPhotoDisplay
    {
        public const int Slots = 4;
        private List<string> photoIds = Blank();
        private readonly FramePhotoBinding[] slots = { new FramePhotoBinding(), new FramePhotoBinding(), new FramePhotoBinding(), new FramePhotoBinding() };
        private readonly PhotoRecord[] cached = new PhotoRecord[Slots];
        private int cachedVersion = -1;
        private PhotoStore cachedStore;
        private MFFrameProps props;

        /// <summary>A matted frame: a moulding in the board's material, a cream mat, four equal windows cut in it.</summary>
        public const float Border = 0.1f, Margin = 0.1f, Gap = 0.09f;
        private static readonly Color MatColor = new Color(0.90f, 0.87f, 0.80f), CutColor = new Color(0.99f, 0.98f, 0.95f), EmptyColor = new Color(0.60f, 0.56f, 0.50f);

        private static List<string> Blank() => new List<string> { "", "", "", "" };

        // Which part of each photo its window shows (FrameLook.CropMiddle/Start/End); set to the suggestion when a photo goes in.
        private List<int> crops = BlankCrops();

        private static List<int> BlankCrops() => new List<int> { 0, 0, 0, 0 };

        /// <summary>A photo trimmed to less than this share of itself asks which part to keep; 3:2 and 16:9 photos go in without asking.</summary>
        public const float AskBelow = 0.85f;

        private MFFrameProps Props => props ?? (props = def.GetModExtension<MFFrameProps>() ?? new MFFrameProps { photoWidth = 2.7f, photoHeight = 1.8f, wallHung = false });

        public string SlotId(int i) => i >= 0 && i < Slots && i < photoIds.Count ? photoIds[i] ?? "" : "";

        public PhotoRecord RecordAt(int i)
        {
            var store = PhotoStore.Instance;
            if (store == null || i < 0 || i >= Slots) return null;
            if (!ReferenceEquals(cachedStore, store) || cachedVersion != store.Version)
            {
                cachedStore = store;
                cachedVersion = store.Version;
                for (int k = 0; k < Slots; k++) cached[k] = SlotId(k).NullOrEmpty() ? null : store.Get(SlotId(k));
            }
            return cached[i];
        }

        public void SetSlot(int i, string id)
        {
            if (i < 0 || i >= Slots) return;
            while (photoIds.Count < Slots) photoIds.Add("");
            photoIds[i] = id ?? "";
            cachedVersion = -1;
            BindAll();
            while (crops.Count < Slots) crops.Add(FrameLook.CropMiddle);
            crops[i] = RecommendedCrop(i);
            Reminisce.Dirty();
        }

        public float WindowAspect
        {
            get
            {
                SlotLayout(0, out _, out float w, out float h);
                return w / h;
            }
        }

        public int CropAt(int i) => i >= 0 && i < crops.Count ? crops[i] : FrameLook.CropMiddle;

        public void SetCrop(int i, int pos)
        {
            while (crops.Count < Slots) crops.Add(FrameLook.CropMiddle);
            if (i >= 0 && i < Slots) crops[i] = Mathf.Clamp(pos, FrameLook.CropMiddle, FrameLook.CropEnd);
        }

        /// <summary>The photo in window i is trimmed enough to ask which part to keep (a tall photo, a panorama).</summary>
        public bool NeedsChoice(int i)
        {
            var r = RecordAt(i);
            return r != null && r.HasFile && FrameLook.Visible(r.DisplayAspect, WindowAspect) < AskBelow;
        }

        public int RecommendedCrop(int i)
        {
            var r = RecordAt(i);
            return r == null ? FrameLook.CropMiddle : Recommend(r.DisplayAspect, WindowAspect, r.ShownPeopleBox);
        }

        public Texture SlotTexture(int i) => i >= 0 && i < Slots && slots[i].Mat != null ? slots[i].Mat.mainTexture : null;

        /// <summary>The position whose visible part holds most of where the people stood (box: fractions of the shown image, origin top-left);
        /// the middle when that is unknown or when it does as well (a tie keeps the middle).</summary>
        public static int Recommend(float photoAspect, float boxAspect, Rect? people)
        {
            if (people == null) return FrameLook.CropMiddle;
            Rect b = people.Value;
            var p = Rect.MinMaxRect(b.xMin, 1f - b.yMax, b.xMax, 1f - b.yMin);   // to UV rows (bottom-up)
            int best = FrameLook.CropMiddle;
            float bestArea = -1f;
            foreach (int pos in new[] { FrameLook.CropMiddle, FrameLook.CropStart, FrameLook.CropEnd })
            {
                Rect uv = FrameLook.CoverUV(photoAspect, boxAspect, pos);
                float area = Mathf.Max(0f, Mathf.Min(uv.xMax, p.xMax) - Mathf.Max(uv.xMin, p.xMin)) * Mathf.Max(0f, Mathf.Min(uv.yMax, p.yMax) - Mathf.Max(uv.yMin, p.yMin));
                if (area > bestArea + 1e-5f)
                {
                    bestArea = area;
                    best = pos;
                }
            }
            return best;
        }

        public int FilledCount
        {
            get { int n = 0; for (int i = 0; i < Slots; i++) if (RecordAt(i) != null) n++; return n; }
        }

        public int BoundCount
        {
            get { int n = 0; for (int i = 0; i < Slots; i++) if (slots[i].Mat != null) n++; return n; }
        }

        public IEnumerable<PhotoRecord> Displayed
        {
            get
            {
                for (int i = 0; i < Slots; i++)
                {
                    var r = RecordAt(i);
                    if (r != null && r.HasFile) yield return r;
                }
            }
        }

        public bool FixedDisplay => true;

        public override void ExposeData()
        {
            base.ExposeData();
            FrameQuality.FromOldSave(this);
            Scribe_Collections.Look(ref photoIds, "photoIds", LookMode.Value);
            Scribe_Collections.Look(ref crops, "crops", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (crops == null) crops = BlankCrops();
                while (crops.Count < Slots) crops.Add(FrameLook.CropMiddle);
                if (photoIds == null) photoIds = Blank();
                while (photoIds.Count < Slots) photoIds.Add("");
                for (int i = 0; i < photoIds.Count; i++) if (photoIds[i] == null) photoIds[i] = "";
                if (def.MadeFromStuff && Stuff == null) SetStuffDirect(GenStuff.DefaultStuffFor(def));
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            Reminisce.Dirty();
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            UnbindAll();
            base.DeSpawn(mode);
            Reminisce.Dirty();
        }

        public void RefreshBinding() => BindAll();

        private void UnbindAll()
        {
            for (int i = 0; i < Slots; i++) Unbind(i);
        }

        private void Unbind(int i) => slots[i].Unbind();

        private void BindAll()
        {
            for (int i = 0; i < Slots; i++) slots[i].Bind(RecordAt(i));
        }

        /// <summary>Centre offset and size of one of the four windows cut in the mat (2x2, all the same size). The photo is centre-cropped to
        /// fill its window (<see cref="SlotCrop"/>): fitted by their own proportions, photos left windows of four different sizes (user,
        /// 2026-09-25: "stuck on a board").</summary>
        public void SlotLayout(int i, out Vector3 offset, out float w, out float h)
        {
            var p = Props;
            w = (p.photoWidth - 2f * Margin - Gap) / 2f;
            h = (p.photoHeight - 2f * Margin - Gap) / 2f;
            int col = i % 2, row = i / 2;
            float cx = -p.photoWidth / 2f + Margin + w / 2f + col * (w + Gap);
            float cz = p.photoHeight / 2f - Margin - h / 2f - row * (h + Gap) + Building_PhotoFrame.StandLift;
            offset = new Vector3(cx, 0f, cz);
        }

        private float SlotAspect(int i)
        {
            Texture t = slots[i].Mat != null ? slots[i].Mat.mainTexture : null;
            return t != null && t.height > 0 ? (float)t.width / t.height : PhotoCapture.DefaultAspect;
        }

        /// <summary>The part of the photo in window i that shows (UV): full height or full width, at the chosen position.</summary>
        public Rect SlotCrop(int i)
        {
            SlotLayout(i, out _, out float w, out float h);
            return FrameLook.CoverUV(SlotAspect(i), w / h, CropAt(i));
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            BindAll();
            var p = Props;
            float ow = p.photoWidth + 2f * Border, oh = p.photoHeight + 2f * Border;
            Vector3 c = drawLoc;
            c.z += Building_PhotoFrame.StandLift;
            FrameLook.Feet(drawLoc, ow, c.z - oh / 2f, DrawColor);
            c.y += FrameLook.Layer;
            FrameLook.Shadow(c, ow, oh, 0.04f, -0.06f, 0.25f);
            c.y += FrameLook.Layer;
            FrameLook.Moulding(c, p.photoWidth, p.photoHeight, Border, DrawColor);
            c.y += FrameLook.Layer * 3f;
            FrameLook.Quad(c, p.photoWidth, p.photoHeight, FrameLook.Solid(MatColor));
            for (int i = 0; i < Slots; i++)
            {
                SlotLayout(i, out Vector3 off, out float w, out float h);
                Vector3 s = drawLoc + off;
                s.y = c.y + FrameLook.Layer;
                FrameLook.Quad(s, w + 0.035f, h + 0.035f, FrameLook.Solid(CutColor));   // the mat's bevelled cut
                s.y += FrameLook.Layer;
                if (slots[i].Mat == null)
                {
                    FrameLook.Quad(s, w, h, FrameLook.Solid(EmptyColor));
                    continue;
                }
                Fading.DrawPhoto(Matrix4x4.TRS(s, Quaternion.identity, new Vector3(w, 1f, h)), slots[i].Mat, Fading.TintFor(RecordAt(i), false), FrameLook.CoverMesh(SlotAspect(i), w / h, CropAt(i)));
            }
        }

        private string SlotLabel(int i)
        {
            var r = RecordAt(i);
            return "MF_Slot".Translate(i + 1) + ": " + (r == null ? "MF_EmptySlot".Translate().ToString() : r.title);
        }

        private List<FloatMenuOption> SlotOptions(Action<int> act, bool filledOnly)
        {
            var opts = new List<FloatMenuOption>();
            for (int i = 0; i < Slots; i++)
            {
                if (filledOnly && RecordAt(i) == null) continue;
                int k = i;
                opts.Add(new FloatMenuOption(SlotLabel(i), () => act(k)));
            }
            return opts;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            if (Faction != Faction.OfPlayer) yield break;
            yield return new Command_Action
            {
                defaultLabel = "MF_ChoosePhoto".Translate(),
                defaultDesc = "MF_ChooseSlotDesc".Translate(),
                icon = MFIcons.Frame,
                action = () => Find.WindowStack.Add(new FloatMenu(SlotOptions(i =>
                    Find.WindowStack.Add(new Dialog_ChoosePhoto(LabelCap + " — " + SlotLabel(i), SlotId(i), id =>
                    {
                        SetSlot(i, id);
                        if (NeedsChoice(i)) Find.WindowStack.Add(new Dialog_MFSlotCrop(this, i));   // a tall photo or a panorama: which part to keep
                    })), false)))
            };
            if (FilledCount == 0) yield break;
            var trimmed = Enumerable.Range(0, Slots).Where(NeedsChoice).ToList();
            var crop = new Command_Action
            {
                defaultLabel = "MF_SlotCrop".Translate(),
                defaultDesc = "MF_SlotCropDesc".Translate(),
                icon = MFIcons.SpotFrame,
                action = () => Find.WindowStack.Add(new FloatMenu(trimmed.Select(i => new FloatMenuOption(SlotLabel(i), () => Find.WindowStack.Add(new Dialog_MFSlotCrop(this, i)))).ToList()))
            };
            if (trimmed.Count == 0) crop.Disable("MF_SlotCropNone".Translate());
            yield return crop;
            yield return new Command_Action
            {
                defaultLabel = "MF_ViewPhoto".Translate(),
                defaultDesc = "MF_ViewPhotoDesc".Translate(),
                icon = MFIcons.Camera,
                action = () => Find.WindowStack.Add(new FloatMenu(SlotOptions(i => MainTabWindow_Album.Open(RecordAt(i)), true)))
            };
            yield return new Command_Action
            {
                defaultLabel = "MF_ClearPhoto".Translate(),
                defaultDesc = "MF_ClearSlotDesc".Translate(),
                icon = TexCommand.ClearPrioritizedWork,
                action = () => Find.WindowStack.Add(new FloatMenu(SlotOptions(i => SetSlot(i, null), true)))
            };
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            var titles = Displayed.Select(r => r.title).ToList();
            string line = "MF_CollageCount".Translate(titles.Count, Slots) + (titles.Count > 0 ? ": " + string.Join(" · ", titles) : "");
            return s.NullOrEmpty() ? line : s + "\n" + line;
        }
    }

    /// <summary>Shows the colony's photos one after another, in the order taken, on the wall or floor in front of it. The pace is REAL time
    /// (a setting, default 15 s per photo), not game time: at the 300–900 TPS this player runs, a game hour is 3–8 real seconds. The clock only
    /// runs while the game is unpaused. Nothing is saved: the photo shown is a pure function of that clock and the record list.</summary>
    [StaticConstructorOnStartup]
    public class Building_PhotoProjector : Building, IPhotoDisplay
    {
        public const float ImageWidth = 2.4f, ImageHeight = 1.6f, Throw = 1.8f;

        /// <summary>Real seconds of unpaused play since the game was loaded. Advanced once per frame from PhotoStore.GameComponentUpdate.</summary>
        public static float Clock { get; private set; }

        public static float SecondsPerPhoto => Mathf.Max(1f, MomentFramesMod.Settings?.projectorSeconds ?? 15);

        public static void Advance(float deltaSeconds)
        {
            if (deltaSeconds > 0f) Clock += deltaSeconds;
        }

        public static void ResetClock() => Clock = 0f;
        public const string FilmMilestones = "milestone";   // filterKind value: milestones only
        public const string FilmSelected = "mf-selected";
        private List<string> selectedPhotoIds = new List<string>();
        internal IReadOnlyList<string> SelectedPhotoIds => selectedPhotoIds;
        /// <summary>This photo by name, not one that happens to match a filter.</summary>
        internal bool ChoseExactly(string id) => SinglePhoto ? fixedPhotoId == id : filterKind == FilmSelected && selectedPhotoIds.Contains(id);
        private static readonly List<PhotoRecord> none = new List<PhotoRecord>();
        private static Material glowMat;
        // The "film": which photos the reel cycles through. Saved (new def, own keys); "" = all.
        private string filterKind = "";
        private string filterPerson = "";
        private string filterPersonName = "";
        private int filterYear;   // 0 = every year (a memory night on one year's photos)
        private int projectionSize;   // legacy projectors load at their original size
        private float projectionWidth, projectionHeight;
        private int projectionDepth = ProjectionGeometry.BehindDepth; // old saves without a key retain their original drawing altitude
        private string fixedPhotoId = "";
        private int skip;   // "next photo" presses; not saved
        private List<PhotoRecord> reel = new List<PhotoRecord>();
        private PhotoStore reelStore;
        private int reelVersion = -1;
        private string reelKey;
        private readonly PhotoBinding binding = new PhotoBinding();
        private readonly ProjectionBinding largeBinding = new ProjectionBinding();
        private bool LargeProjection => projectionSize > 0 || HasCustomProjection;
        private Material mat => LargeProjection && largeBinding.Material != null ? largeBinding.Material : binding.Mat;
        public int ProjectionSize => projectionSize;
        public bool HasCustomProjection => ProjectionGeometry.Custom(projectionWidth, projectionHeight);
        public float CustomProjectionWidth => HasCustomProjection ? projectionWidth : 0f;
        public float CustomProjectionHeight => HasCustomProjection ? projectionHeight : 0f;
        public int ProjectionDepth => projectionDepth;
        public int FilterYear => filterYear;
        public string FilterPersonName => filterPersonName ?? "";
        public bool SinglePhoto => !fixedPhotoId.NullOrEmpty();
        private LordJob_MFMemoryNight night;
        private int nightCheckedFrame = -1;
        private LordJob_MFMemoryNight Night
        {
            get
            {
                if (nightCheckedFrame != Time.frameCount) { night = MemoryNight.Active(this); nightCheckedFrame = Time.frameCount; }
                return night;
            }
        }

        public static int IndexFor(float seconds, int count) => IndexFor(seconds, count, 0);
        public static int IndexFor(float seconds, int count, int skip) => count <= 0 ? -1 : (((int)(seconds / SecondsPerPhoto) + skip) % count + count) % count;

        public string FilterKind => filterKind ?? "";
        public string FilterPerson => filterPerson ?? "";

        public void SetFilm(string kind, string personId, string personName, int year = 0)
        {
            selectedPhotoIds.Clear(); reelVersion = -1;
            fixedPhotoId = "";
            filterKind = kind ?? "";
            filterPerson = personId ?? "";
            filterPersonName = personName ?? "";
            filterYear = year;
            skip = 0;
            Reminisce.Dirty();
        }

        internal void SetSelectedFilm(IEnumerable<string> ids)
        {
            var chosen = (ids ?? Enumerable.Empty<string>()).Where(id => !id.NullOrEmpty()).Distinct().ToList();
            SetFilm(FilmSelected, "", ""); selectedPhotoIds = chosen;
        }

        public void Next() { if (Night != null) Night.SlideSkip++; else skip++; }

        private bool OnFilm(PhotoRecord r)
        {
            if (!r.HasFile) return false;
            if (filterKind == FilmSelected) return selectedPhotoIds.Contains(r.id);
            if (filterKind == FilmMilestones) { if (!Kinds.IsMilestone(r.kind)) return false; }
            else if (!filterKind.NullOrEmpty() && r.kind != filterKind) return false;
            if (!filterPerson.NullOrEmpty() && !MainTabWindow_Album.Matches(r, filterPerson, filterPersonName)) return false;
            if (filterYear != 0 && r.year != filterYear) return false;
            return true;
        }

        /// <summary>Photos on the film, oldest first (cached per store version and film).</summary>
        public List<PhotoRecord> Reel
        {
            get
            {
                var store = PhotoStore.Instance;
                if (store == null) return none;
                string key = filterKind + "|" + filterPerson + "|" + filterYear;
                if (!ReferenceEquals(reelStore, store) || reelVersion != store.Version || reelKey != key)
                {
                    reelStore = store;
                    reelVersion = store.Version;
                    reelKey = key;
                    reel = store.Records.Where(OnFilm).OrderBy(r => r.ticksAbs).ToList();
                }
                return reel;
            }
        }

        public int ReelCount => Reel.Count;
        public int CurrentIndex
        {
            get
            {
                var job = Night;
                if (job != null && job.FilmIds.Count > 0)
                    return job.SlideIndex(job.FilmIds.Count);
                return SinglePhoto ? 0 : IndexFor(Clock, ReelCount, skip);
            }
        }

        public PhotoRecord Current
        {
            get
            {
                var list = Reel;
                var job = Night;
                if (job != null && job.FilmIds.Count > 0) return PhotoStore.Instance?.Get(job.FilmIds[CurrentIndex]);
                if (SinglePhoto) return PhotoStore.Instance?.Get(fixedPhotoId);
                int i = IndexFor(Clock, list.Count, skip);
                return i < 0 ? null : list[i];
            }
        }

        public string FilmLabel
        {
            get
            {
                if (filterKind == FilmSelected) return "MF_FavoriteSelectedFilm".Translate();
                if (filterYear != 0) return "MF_FilmYear".Translate(filterYear).ToString();
                if (!filterPerson.NullOrEmpty()) return "MF_FilmPerson".Translate(filterPersonName).ToString();
                if (filterKind == FilmMilestones) return "MF_FilmMilestones".Translate().ToString();
                if (!filterKind.NullOrEmpty()) return "MF_FilmKind".Translate(MainTabWindow_Album.KindLabel(filterKind)).ToString();
                return "MF_FilmAll".Translate().ToString();
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            FrameQuality.FromOldSave(this);
            Scribe_Values.Look(ref filterKind, "filterKind", "");
            Scribe_Values.Look(ref filterPerson, "filterPerson", "");
            Scribe_Values.Look(ref filterPersonName, "filterPersonName", "");
            Scribe_Values.Look(ref filterYear, "filterYear", 0);
            Scribe_Collections.Look(ref selectedPhotoIds, "mfSelectedPhotoIds", LookMode.Value);
            Scribe_Values.Look(ref projectionSize, "mfProjectionSize", 0);
            Scribe_Values.Look(ref projectionWidth, "mfProjectionWidth", 0f);
            Scribe_Values.Look(ref projectionHeight, "mfProjectionHeight", 0f);
            Scribe_Values.Look(ref projectionDepth, "mfProjectionDepth", ProjectionGeometry.LegacyDepth);
            Scribe_Values.Look(ref fixedPhotoId, "mfProjectorFixedPhoto", "");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                selectedPhotoIds ??= new List<string>();
                if (filterKind == null) filterKind = "";
                if (filterPerson == null) filterPerson = "";
                if (filterPersonName == null) filterPersonName = "";
                projectionSize = Mathf.Clamp(projectionSize, 0, 2);
                if (!HasCustomProjection) projectionWidth = projectionHeight = 0f;
                projectionDepth = Mathf.Clamp(projectionDepth, ProjectionGeometry.LegacyDepth, ProjectionGeometry.FrontDepth);
                if (fixedPhotoId == null) fixedPhotoId = "";
                if (def.MadeFromStuff && Stuff == null) SetStuffDirect(GenStuff.DefaultStuffFor(def));
            }
        }

        public IEnumerable<PhotoRecord> Displayed
        {
            get
            {
                var c = Current;
                if (c != null) yield return c;
            }
        }

        public bool FixedDisplay => SinglePhoto && Night == null;

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Unbind();
            base.DeSpawn(mode);
        }

        private void Unbind() { binding.Unbind(); largeBinding.Unbind(); }

        private void Bind(PhotoRecord rec)
        {
            binding.Bind(rec);
            if (LargeProjection) largeBinding.Bind(rec); else largeBinding.Unbind();
        }

        /// <summary>Centre offset and size of the projected image for the current rotation and photo aspect.</summary>
        public void ImageLayout(out Vector3 offset, out float w, out float h)
        {
            float a = Current?.DisplayAspect ?? PhotoCapture.DefaultAspect;
            Layout(Rotation, LayoutAspect, projectionSize, out offset, out w, out h, CustomProjectionWidth, CustomProjectionHeight);
            // A night reserves one stable screen. Fit portrait/panorama slides inside it without moving the audience every change.
            if (Night != null) FitImage(a, ref w, ref h);
        }
        internal float LayoutAspect => Night != null ? HasCustomProjection ? projectionWidth / projectionHeight : PhotoCapture.DefaultAspect
            : Current?.DisplayAspect ?? PhotoCapture.DefaultAspect;
        internal void ProjectionBounds(out float width, out float height)
            => ProjectionGeometry.Bounds(projectionSize, CustomProjectionWidth, CustomProjectionHeight, out width, out height);
        public static void FitImage(float aspect, ref float w, ref float h)
        {
            float fittedHeight = w / Mathf.Max(.01f, aspect);
            if (fittedHeight <= h) h = fittedHeight; else w = h * Mathf.Max(.01f, aspect);
        }

        /// <summary>Where the beam lands, for any rotation — the one place this is worked out, so the placement preview and the projected image
        /// cannot drift apart. aspect = image width/height.</summary>
        public static void Layout(Rot4 rot, float aspect, out Vector3 offset, out float w, out float h)
            => Layout(rot, aspect, 0, out offset, out w, out h);

        public static void Layout(Rot4 rot, float aspect, int size, out Vector3 offset, out float w, out float h)
            => Layout(rot, aspect, size, out offset, out w, out h, 0f, 0f);
        public static void Layout(Rot4 rot, float aspect, int size, out Vector3 offset, out float w, out float h, float customWidth, float customHeight)
        {
            size = Mathf.Clamp(size, 0, 2);
            ProjectionGeometry.Bounds(size, customWidth, customHeight, out float maxWidth, out float maxHeight);
            ProjectionGeometry.Fit(aspect, maxWidth, maxHeight, out w, out h);
            float throwDistance = ProjectionGeometry.Throw(rot.IsHorizontal, size, w, h, maxWidth, maxHeight, ProjectionGeometry.Custom(customWidth, customHeight));
            offset = rot.FacingCell.ToVector3() * throwDistance;
        }

        internal static CellRect ProjectionRectAt(IntVec3 pos, Rot4 rot, float aspect, int size)
            => ProjectionRectAt(pos, rot, aspect, size, 0f, 0f);
        internal static CellRect ProjectionRectAt(IntVec3 pos, Rot4 rot, float aspect, int size, float customWidth, float customHeight)
        {
            Layout(rot, aspect, size, out Vector3 off, out float w, out float h, customWidth, customHeight);
            Vector3 c = pos.ToVector3Shifted() + off;
            int minX = Mathf.FloorToInt(c.x - w / 2f), minZ = Mathf.FloorToInt(c.z - h / 2f);
            return CellRect.FromLimits(minX, minZ, Mathf.CeilToInt(c.x + w / 2f) - 1, Mathf.CeilToInt(c.z + h / 2f) - 1);
        }
        internal CellRect ProjectionRect => ProjectionRectAt(Position, Rotation, LayoutAspect, projectionSize, CustomProjectionWidth, CustomProjectionHeight);
        internal bool CanProjectAtSize(int size)
        {
            if (!Spawned || Map == null) return false;
            // Selecting a preset clears the custom aspect even while a memory night is active.
            float aspect = Night != null ? PhotoCapture.DefaultAspect : Current?.DisplayAspect ?? PhotoCapture.DefaultAspect;
            var rect = ProjectionRectAt(Position, Rotation, aspect, size);
            return rect.Min.InBounds(Map) && rect.Max.InBounds(Map);
        }
        public void SetProjectionSize(int size)
        {
            if (!CanProjectAtSize(size)) return;
            projectionSize = Mathf.Clamp(size, 0, 2);
            projectionWidth = projectionHeight = 0f;
            ProjectionChanged();
        }
        internal bool CanProjectCustom(float width, float height)
        {
            if (!Spawned || Map == null || !ProjectionGeometry.Custom(width, height)) return false;
            var rect = ProjectionRectAt(Position, Rotation, width / height, projectionSize, width, height);
            return rect.Min.InBounds(Map) && rect.Max.InBounds(Map);
        }
        internal bool SetCustomProjectionSize(float width, float height)
        {
            if (!CanProjectCustom(width, height)) return false;
            projectionWidth = width; projectionHeight = height; ProjectionChanged(); return true;
        }
        internal void SetProjectionDepth(int value)
        {
            if (value == ProjectionGeometry.BehindDepth || value == ProjectionGeometry.FrontDepth) projectionDepth = value;
        }
        private void ProjectionChanged()
        {
            Unbind();
            var active = MemoryNight.Active(this);
            if (active?.lord?.CurLordToil != null)
            {
                active.lord.CurLordToil.UpdateAllDuties();
                foreach (var pawn in active.lord.ownedPawns)
                    if (pawn.CurJob?.def == JobDefOf.SpectateCeremony) pawn.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced, false);
            }
        }

        /// <summary>The cells the beam covers from this position and rotation (what the preview outlines).</summary>
        public static IEnumerable<IntVec3> BeamCells(IntVec3 pos, Rot4 rot, Map map, int size = 0, float aspect = PhotoCapture.DefaultAspect)
            => BeamCells(pos, rot, map, size, aspect, 0f, 0f);
        public static IEnumerable<IntVec3> BeamCells(IntVec3 pos, Rot4 rot, Map map, int size, float aspect, float customWidth, float customHeight)
        {
            var rect = ProjectionRectAt(pos, rot, aspect, size, customWidth, customHeight);
            foreach (var cell in rect)
                if (map == null || cell.InBounds(map)) yield return cell;
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            GenDraw.DrawFieldEdges(BeamCells(Position, Rotation, Map, projectionSize, LayoutAspect, CustomProjectionWidth, CustomProjectionHeight).ToList(), Color.white);
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);   // the device
            var rec = Current;
            Bind(rec);
            if (mat == null) return;
            MemoryNightSfx.ObserveProjectedPhoto(this, rec);
            if (glowMat == null) glowMat = new Material(ShaderDatabase.Transparent) { mainTexture = BaseContent.WhiteTex, color = new Color(1f, 0.97f, 0.85f, 0.22f) };
            ImageLayout(out Vector3 off, out float w, out float h);
            Vector3 c = drawLoc + off;
            if (projectionDepth == ProjectionGeometry.BehindDepth) c.y = AltitudeLayer.FloorEmplacement.AltitudeFor();
            else if (projectionDepth == ProjectionGeometry.FrontDepth) c.y = AltitudeLayer.MoteOverhead.AltitudeFor();
            c.y += 0.02f;
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(c, Quaternion.identity, new Vector3(w + 0.3f, 1f, h + 0.3f)), glowMat, 0);
            c.y += 0.01f;
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(c, Quaternion.identity, new Vector3(w, 1f, h)), mat, 0);
        }

        internal string DisplayedFilmLabel => Night?.FilmLabel ?? (SinglePhoto ? "MF_ProjectionSingle".Translate().ToString() : FilmLabel);

        private FloatMenuOption FilmOption(string label, string kind, string personId = "", string personName = "", int year = 0)
        {
            bool chosen = !SinglePhoto && filterKind == kind && filterPerson == personId && filterYear == year;
            return new FloatMenuOption(chosen ? "MF_ProjectorFilmChosen".Translate(label).ToString() : label,
                () => SetFilm(kind, personId, personName, year));
        }

        internal List<FloatMenuOption> FilmOptions()
        {
            var store = PhotoStore.Instance;
            var opts = new List<FloatMenuOption>
            {
                new FloatMenuOption("MF_ProjectorCurrentFilm".Translate(DisplayedFilmLabel), null),
                FilmOption("MF_FilmAll".Translate(), ""),
                FilmOption("MF_FilmMilestones".Translate(), FilmMilestones)
            };
            if (store == null) return opts;
            foreach (var k in store.Records.Where(r => r.HasFile).Select(r => r.kind).Distinct().OrderBy(MainTabWindow_Album.KindLabel))
            {
                string kind = k;
                opts.Add(FilmOption("MF_FilmKind".Translate(MainTabWindow_Album.KindLabel(kind)), kind));
            }
            foreach (var kv in MainTabWindow_Album.PeopleIndex(store).OrderBy(kv => kv.Value))
            {
                string id = kv.Key, name = kv.Value;
                opts.Add(FilmOption("MF_FilmPerson".Translate(name), "", id, name));
            }
            foreach (int y in store.Records.Where(r => r.HasFile && r.year != 0).Select(r => r.year).Distinct().OrderBy(y => y))
            {
                int year = y;
                opts.Add(FilmOption("MF_FilmYear".Translate(year), "", year: year));
            }
            return opts;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            if (Faction != Faction.OfPlayer) yield break;
            yield return new Command_Action
            {
                defaultLabel = "MF_ProjectorFilmButton".Translate(DisplayedFilmLabel),
                defaultDesc = "MF_ProjectorCurrentFilm".Translate(DisplayedFilmLabel).ToString() +
                    (SinglePhoto && Night == null && Current != null ? "\n" + Current.title : "") + "\n\n" + "MF_ProjectorFilmDesc".Translate().ToString() +
                    (Night != null ? "\n\n" + "MF_ProjectorFilmAfterNight".Translate().ToString() : ""),
                icon = MFIcons.Frame,
                action = () => Find.WindowStack.Add(new FloatMenu(FilmOptions()))
            };
            yield return new Command_Action
            {
                defaultLabel = "MF_ProjectionSize".Translate(ProjectionSizeLabel),
                defaultDesc = "MF_ProjectionSizeDesc".Translate(), icon = MFIcons.SpotFrame,
                action = () => {
                    var sizes = Enumerable.Range(0, 3).Select(size => new FloatMenuOption(SizeLabel(size), CanProjectAtSize(size) ? (Action)(() => SetProjectionSize(size)) : null)).ToList();
                    sizes.Add(new FloatMenuOption("MF_ProjectionCustomInput".Translate(), () => Find.WindowStack.Add(new Dialog_ProjectionSize(this))));
                    Find.WindowStack.Add(new FloatMenu(sizes));
                }
            };
            yield return new Command_Action
            {
                defaultLabel = "MF_ProjectionDepth".Translate(DepthLabel), defaultDesc = "MF_ProjectionDepthHelp".Translate(), icon = MFIcons.Frame,
                action = () => Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption> {
                    new FloatMenuOption("MF_ProjectionBehind".Translate(), () => SetProjectionDepth(ProjectionGeometry.BehindDepth)),
                    new FloatMenuOption("MF_ProjectionFront".Translate(), () => SetProjectionDepth(ProjectionGeometry.FrontDepth))
                }))
            };
            yield return new Command_Action
            {
                defaultLabel = "MF_ProjectionSingle".Translate(), defaultDesc = "MF_ProjectionSingleDesc".Translate(), icon = MFIcons.Frame,
                action = () => Find.WindowStack.Add(new Dialog_ChoosePhoto(LabelCap, fixedPhotoId, id =>
                { fixedPhotoId = id ?? ""; skip = 0; Reminisce.Dirty(); }))
            };
            var night = new Command_Action
            {
                defaultLabel = "MF_MemoryNightOpen".Translate(),
                defaultDesc = "MF_ProjectorNightWhen".Translate(MemoryNight.MinPhotos) + "\n\n" + "MF_MemoryNightOpenDesc".Translate(),
                icon = MFIcons.Camera,
                action = () => MemoryNight.OpenPreparation(this)
            };
            string why = PhotoStore.Instance?.Records.Count(r => r.HasFile) < MemoryNight.MinPhotos
                ? "MF_MemoryNightFewPhotos".Translate(MemoryNight.MinPhotos).ToString() : MemoryNight.CannotStart(this);
            if (ReelCount < MemoryNight.MinPhotos && PhotoStore.Instance?.Records.Count(r => r.HasFile) >= MemoryNight.MinPhotos) why = null;
            if (why != null && MemoryNight.Active(this) == null) night.Disable(why);
            yield return night;
            var cur = Current;
            if (cur == null) yield break;
            if (!SinglePhoto || Night != null) yield return new Command_Action
            {
                defaultLabel = "MF_ProjectorNext".Translate(),
                defaultDesc = "MF_ProjectorNextDesc".Translate(),
                icon = TexCommand.ForbidOff,
                action = Next
            };
            yield return new Command_Action
            {
                defaultLabel = "MF_ProjectorOpenAlbum".Translate(),
                defaultDesc = "MF_ProjectorOpenAlbumDesc".Translate(),
                icon = MFIcons.Camera,
                action = () => MainTabWindow_Album.Open(cur)
            };
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            var cur = Current;
            var active = MemoryNight.Active(this);
            string line = "MF_ProjectorFilmLine".Translate(DisplayedFilmLabel) + "\n" +
                (cur == null ? "MF_ProjectorIdle".Translate().ToString() : "MF_ProjectorShowing".Translate(cur.title, CurrentIndex + 1,
                    active?.FilmIds.Count ?? (SinglePhoto ? 1 : ReelCount)).ToString());
            line += "\n" + "MF_ProjectionSize".Translate(ProjectionSizeLabel);
            line += "\n" + "MF_ProjectionDepth".Translate(DepthLabel);
            if (active != null) line += "\n" + active.GetReport(null);
            return s.NullOrEmpty() ? line : s + "\n" + line;
        }

        internal static string SizeLabel(int size) => (size == 2 ? "MF_ProjectionLargest" : size == 1 ? "MF_ProjectionLarge" : "MF_ProjectionNormal").Translate();
        internal string ProjectionSizeLabel => HasCustomProjection ? "MF_ProjectionCustomSize".Translate(projectionWidth.ToString("0.##"), projectionHeight.ToString("0.##")) : SizeLabel(projectionSize);
        internal string DepthLabel => (projectionDepth == ProjectionGeometry.FrontDepth ? "MF_ProjectionFront" : projectionDepth == ProjectionGeometry.BehindDepth ? "MF_ProjectionBehind" : "MF_ProjectionLegacy").Translate();
    }

    /// <summary>A small stand to put beside a grave or sarcophagus: shows the latest photo of the person buried next to it, unless a photo was
    /// chosen by hand. Derived every time from the adjacent grave (8 cells) and cached per store version — nothing extra is saved.</summary>
    public class Building_MemorialFrame : Building_PhotoFrame
    {
        private PhotoRecord derived;
        private string derivedFor;
        private int derivedVersion = -1;

        /// <summary>True when the shown photo comes from the grave, not from the player's choice.</summary>
        public bool Derived => photoId.NullOrEmpty() && derived != null;

        /// <summary>The pawn in an adjacent grave or sarcophagus, or null.</summary>
        public Pawn BuriedNeighbour
        {
            get
            {
                if (!Spawned) return null;
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 c = Position + GenAdj.AdjacentCells[i];
                    if (!c.InBounds(Map)) continue;
                    var things = c.GetThingList(Map);
                    for (int k = 0; k < things.Count; k++)
                        if (things[k] is Building_Grave g && g.HasCorpse) return g.Corpse?.InnerPawn;
                }
                return null;
            }
        }

        public override PhotoRecord Record
        {
            get
            {
                if (!photoId.NullOrEmpty()) return base.Record;
                var store = PhotoStore.Instance;
                Pawn pawn = store == null ? null : BuriedNeighbour;
                if (pawn == null)
                {
                    derived = null;
                    derivedFor = null;
                    return null;
                }
                string id = pawn.GetUniqueLoadID();
                if (derivedFor != id || derivedVersion != store.Version)
                {
                    derivedFor = id;
                    derivedVersion = store.Version;
                    derived = null;
                    foreach (var r in store.Records)
                        if (r.HasFile && r.Has(pawn) && (derived == null || r.ticksAbs > derived.ticksAbs)) derived = r;
                }
                return derived;
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            string clear = "MF_ClearPhoto".Translate();
            foreach (var g in base.GetGizmos())
            {
                if (photoId.NullOrEmpty() && g is Command_Action c && c.defaultLabel == clear) continue;   // nothing chosen by hand, nothing to clear
                yield return g;
            }
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            var pawn = BuriedNeighbour;
            string line = pawn != null ? "MF_MemorialOf".Translate(pawn.LabelShort).ToString() : "MF_MemorialNoGrave".Translate().ToString();
            return s.NullOrEmpty() ? line : s + "\n" + line;
        }
    }
}
