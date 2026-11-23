using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    [StaticConstructorOnStartup]
    public class Building_PhotoFrame : Building, IPhotoDisplay
    {
        public string photoId;

        private readonly FramePhotoBinding binding = new FramePhotoBinding();
        private Material photoMat => binding.Mat;
        private PhotoRecord cachedRecord;
        private string cachedRecordId;
        private int cachedRecordVersion = -1;
        private MFFrameProps props;
        private int style = -1;     // -1 = never chosen: the default by quality
        private CompQuality quality;

        private MFFrameProps Props => props ?? (props = def.GetModExtension<MFFrameProps>() ?? new MFFrameProps());

        /// <summary>How this frame looks: chosen with its button, otherwise gilded when excellent or better and the material's colour below.</summary>
        public FrameStyle Style => style >= 0 && style < FrameLook.StyleCount ? (FrameStyle)style : DefaultStyle;

        public FrameStyle DefaultStyle => IsFine ? FrameStyle.Gilded : FrameStyle.Material;

        /// <summary>Excellent or better: gilded by default and the print fades at half speed (what the separate fine frames did).</summary>
        public bool IsFine => (quality ?? (quality = GetComp<CompQuality>()))?.Quality >= QualityCategory.Excellent;

        public void SetStyle(FrameStyle s) => style = (int)s;

        /// <summary>A wall frame is drawn on the wall it faces, so a click on that wall picks the frame first (clicking again reaches the wall).</summary>
        public override CellRect? CustomRectForSelector
        {
            get
            {
                if (!Spawned || !Props.wallHung) return null;
                CellRect r = this.OccupiedRect();
                IntVec3 f = Rotation.FacingCell;
                return CellRect.FromLimits(Mathf.Min(r.minX, r.minX + f.x), Mathf.Min(r.minZ, r.minZ + f.z), Mathf.Max(r.maxX, r.maxX + f.x), Mathf.Max(r.maxZ, r.maxZ + f.z));
            }
        }

        /// <summary>A wall frame hangs over the wall but stands on the open cell in front of it, and the picture hides which is which: a player who
        /// wants to floor "under" the frame clicks the wall and is refused (user, 2026-09-20). Selecting it outlines the cell it really occupies.</summary>
        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            if (Map == null) return;
            GenDraw.DrawFieldEdges(this.OccupiedRect().Cells.ToList(), Color.white);
        }

        /// <summary>The photo shown (file present) — for reminiscing and the memorial stand.</summary>
        public IEnumerable<PhotoRecord> Displayed
        {
            get
            {
                var r = Record;
                if (r != null && r.HasFile) yield return r;
            }
        }

        public bool FixedDisplay => true;

        public virtual PhotoRecord Record
        {
            get
            {
                if (photoId.NullOrEmpty()) { cachedRecord = null; cachedRecordId = null; return null; }
                var store = PhotoStore.Instance;
                int version = store?.Version ?? -1;
                if (cachedRecord == null || cachedRecordId != photoId || cachedRecordVersion != version)
                {
                    cachedRecord = store?.Get(photoId);
                    cachedRecordId = photoId;
                    cachedRecordVersion = version;
                }
                return cachedRecord;
            }
        }

        public void SetPhoto(string id)
        {
            photoId = id;
            Bind();
            Reminisce.Dirty();
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            Reminisce.Dirty();
        }

        /// <summary>True once the photo texture is loaded and will be drawn.</summary>
        public bool HasPhotoTexture => photoMat != null;

        /// <summary>The bounded texture currently drawn on the map, at the selected frame display quality.</summary>
        public Texture BoundTexture => photoMat != null ? photoMat.mainTexture : null;

        public void RefreshBinding() => Bind();

        /// <summary>Probe diagnostics: the image file the frame is showing.</summary>
        public string BoundPath => binding.BoundPath;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref photoId, "photoId");
            Scribe_Values.Look(ref style, "mfStyle", -1);
            if (FrameQuality.FromOldSave(this) && style < 0) style = (int)FrameStyle.Gilded;   // an old fine frame keeps its gold
            // Frames saved by v1 had no material; give them the default stuff so stats and colour work.
            if (Scribe.mode == LoadSaveMode.PostLoadInit && def.MadeFromStuff && Stuff == null)
                SetStuffDirect(GenStuff.DefaultStuffFor(def));
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Unbind();
            base.DeSpawn(mode);
            Reminisce.Dirty();
        }

        private void Unbind() => binding.Unbind();

        // Fixed display quality. Full originals remain independent of map rendering and camera zoom.
        private void Bind() => binding.Bind(Record);

        /// <summary>Width / height of the bound photo texture; an empty frame keeps the def's 3:2 window.</summary>
        public float PhotoAspect
        {
            get
            {
                Texture t = photoMat != null ? photoMat.mainTexture : null;
                return t != null && t.height > 0 ? (float)t.width / t.height : PhotoCapture.DefaultAspect;
            }
        }

        /// <summary>A standing frame's board is raised this much for its feet.</summary>
        public const float StandLift = 0.1f;

        /// <summary>Photo size (cells) and the photo's centre offset for the current rotation. The photo keeps its own proportions, fitted
        /// inside the def's window (the photo area of the plain style); a style with other borders fits in the same outer size, so a portrait
        /// photo gets a portrait frame whatever the style. A wall frame is centred on the wall cell it faces (it used to lie half on the floor);
        /// a standing one is raised for its feet.</summary>
        public void Layout(out float w, out float h, out Vector3 offset)
        {
            var p = Props;
            float maxW = p.photoWidth, maxH = p.photoHeight;
            offset = Vector3.zero;
            if (p.wallHung)
            {
                if (Rotation.IsHorizontal)
                {
                    maxW *= p.sideScale;
                    maxH *= p.sideScale;
                    offset = new Vector3(Rotation == Rot4.East ? p.wallOffset : -p.wallOffset, 0f, 0f);
                }
                else
                {
                    offset = new Vector3(0f, 0f, Rotation == Rot4.North ? p.wallOffset : -p.wallOffset);
                }
            }
            else offset.z += StandLift;
            FrameLook.Borders(Style, out float side, out float top, out float bottom);
            w = maxW + 2f * (FrameLook.MaterialBorder - side);
            h = maxH + 2f * FrameLook.MaterialBorder - top - bottom;
            float a = PhotoAspect;
            if (a * h > w) h = w / a; else w = h * a;
            offset.z += (bottom - top) / 2f;    // the outer box stays centred; a wider bottom border (polaroid) lifts the photo in it
        }

        /// <summary>The outer box (photo plus the style's borders) and its centre offset — for the feet and the probe.</summary>
        public void Outer(out float w, out float h, out Vector3 offset)
        {
            Layout(out float pw, out float ph, out offset);
            FrameLook.Borders(Style, out float side, out float top, out float bottom);
            w = pw + 2f * side;
            h = ph + top + bottom;
            offset.z -= (bottom - top) / 2f;
        }

        /// <summary>Probe diagnostics: the last frame in which this building was drawn.</summary>
        public int lastDrawFrame = -1;

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            lastDrawFrame = Time.frameCount;
            Bind();
            Layout(out float w, out float h, out Vector3 offset);
            Vector3 pc = drawLoc + offset;
            if (!Props.wallHung)
            {
                // feet under the board and a blot on the floor: it stands, it is not laid on the ground
                Outer(out float ow, out float oh, out Vector3 boxOff);
                FrameLook.Feet(new Vector3(drawLoc.x + boxOff.x, drawLoc.y, drawLoc.z), ow, drawLoc.z + boxOff.z - oh / 2f, DrawColor, Mathf.Clamp(ow / 2f, 0.6f, 1f));
                pc.y += FrameLook.Layer * 2f;
            }
            // an excellent or better frame protects the print (fades at half speed)
            FrameLook.DrawFramed(Style, pc, w, h, photoMat, photoMat != null ? Fading.TintFor(Record, IsFine) : Color.white, DrawColor, thingIDNumber, Props.wallHung);
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            if (Faction != Faction.OfPlayer) yield break;
            yield return new Command_Action
            {
                defaultLabel = "MF_ChoosePhoto".Translate(),
                defaultDesc = "MF_ChoosePhotoDesc".Translate(),
                icon = MFIcons.Frame,
                action = () => Find.WindowStack.Add(new Dialog_ChoosePhoto(this))
            };
            yield return new Command_FrameStyle(this);
            var rec = Record;
            if (rec != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "MF_ViewPhoto".Translate(),
                    defaultDesc = "MF_ViewPhotoDesc".Translate(),
                    icon = MFIcons.Camera,
                    action = () => MainTabWindow_Album.Open(rec)
                };
                yield return new Command_Action
                {
                    defaultLabel = "MF_ClearPhoto".Translate(),
                    defaultDesc = "MF_ClearPhotoDesc".Translate(),
                    icon = TexCommand.ClearPrioritizedWork,
                    action = () => SetPhoto(null)
                };
            }
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            var rec = Record;
            string line = rec == null ? "MF_NoPhoto".Translate().ToString() : (rec.HasFile ? rec.DisplayTitle + " — " + rec.dateLabel : rec.DisplayTitle + " — " + "MF_MissingFile".Translate());
            return s.NullOrEmpty() ? line : s + "\n" + line;
        }
    }

    /// <summary>"Frame style": a menu of the five looks. With several frames selected the choice goes to all of them, and only one menu opens
    /// (grouped buttons would otherwise each open their own).</summary>
    public class Command_FrameStyle : Command_Action
    {
        private readonly Building_PhotoFrame frame;

        public Command_FrameStyle(Building_PhotoFrame frame)
        {
            this.frame = frame;
            defaultLabel = "MF_FrameStyle".Translate();
            defaultDesc = "MF_FrameStyleDesc".Translate(FrameLook.Label(frame.Style));
            icon = MFIcons.Frame;
            action = OpenMenu;
        }

        public override bool InheritInteractionsFrom(Gizmo other) => false;

        private void OpenMenu()
        {
            var frames = Find.Selector.SelectedObjects.OfType<Building_PhotoFrame>().ToList();
            if (!frames.Contains(frame)) frames.Add(frame);
            var opts = new List<FloatMenuOption>();
            for (int i = 0; i < FrameLook.StyleCount; i++)
            {
                var s = (FrameStyle)i;
                string label = FrameLook.Label(s);
                if (frames.All(f => f.Style == s)) label += " " + "MF_StyleCurrent".Translate();
                opts.Add(new FloatMenuOption(label, () => { foreach (var f in frames) f.SetStyle(s); }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }
    }
}
