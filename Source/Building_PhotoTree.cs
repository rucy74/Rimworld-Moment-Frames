using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>A fixed, saved arrangement of up to 35 miniature prints. The album itself is scanned when its version changes,
    /// never each tick. File checks are limited to the selected 35 records; the management window can refresh them while paused.</summary>
    [StaticConstructorOnStartup]
    public class Building_PhotoTree : Building, IPhotoDisplay
    {
        public MemoryTreeSelection Selection = new MemoryTreeSelection();

        private readonly PhotoBinding[] bindings = Enumerable.Range(0, MemoryTreeLayout.Count).Select(_ => new PhotoBinding()).ToArray();
        private readonly PhotoRecord[] records = new PhotoRecord[MemoryTreeLayout.Count];
        private readonly bool[] available = new bool[MemoryTreeLayout.Count];
        private PhotoStore seenStore;
        private int seenVersion = -1, nextFileCheckFrame = -1;
        private CompQuality quality;

        private static readonly Color Card = new Color32(244, 237, 221, 255);
        private static readonly Color Rim = new Color32(196, 185, 159, 255);
        private static readonly Color CaptionLine = new Color32(184, 165, 135, 255);
        private static readonly Color StringColor = new Color32(217, 194, 147, 255);
        private static readonly Color Empty = new Color32(180, 174, 161, 255);
        private static readonly Color Shadow = new Color(0.09f, 0.07f, 0.045f, 0.24f);

        // The approved structure is already coloured wood. Stuff still determines cost/stats/quality; it must not tint the art.
        public override Color DrawColor => Color.white;
        public bool IsFine => (quality ?? (quality = GetComp<CompQuality>()))?.Quality >= QualityCategory.Excellent;
        public bool FixedDisplay => true;

        public int lastDrawFrame = -1, drawnPhotoCount, drawnStringCount, drawnCardCount, drawnPlaceholderCount;
        public int mapInputDispatchCount, mapClickCount, lastClickedSlot = -1;
        public int SelectionSyncCount { get; private set; }
        public int FileCheckCount { get; private set; }

        public string SlotId(int slot) => slot >= 0 && slot < MemoryTreeLayout.Count && Selection?.slots != null && slot < Selection.slots.Count
            ? Selection.slots[slot]?.photoId ?? "" : "";

        public PhotoRecord RecordAt(int slot)
        {
            EnsureCurrent();
            return slot >= 0 && slot < records.Length ? records[slot] : null;
        }

        public string BoundPathAt(int slot) => slot >= 0 && slot < bindings.Length ? bindings[slot].BoundPath : null;
        public Texture BoundTextureAt(int slot) => slot >= 0 && slot < bindings.Length ? bindings[slot].Mat?.mainTexture : null;
        public int BoundPhotoCount => bindings.Count(b => b.Mat != null);

        public IEnumerable<PhotoRecord> Displayed
        {
            get
            {
                EnsureCurrent();
                BindSelected();
                var ids = new HashSet<string>();
                for (int i = 0; i < records.Length; i++)
                    if (records[i] != null && available[i] && bindings[i].Mat != null && ids.Add(records[i].id)) yield return records[i];
            }
        }

        private bool Exists(PhotoStore store, PhotoRecord record)
        {
            FileCheckCount++;
            return store != null && record != null && record.HasFile && !record.Archived && File.Exists(store.FilePath(record));
        }

        /// <summary>Forced by the management UI: reruns selection and clears any old missing-file result for files that now exist.</summary>
        public void RefreshSelection() => Refresh(true);

        /// <summary>For an open management window: checks version changes and selected files without rescanning the album each GUI event.</summary>
        public void EnsureSelection() => EnsureCurrent();

        private void Refresh(bool forceFiles)
        {
            if (Selection == null) Selection = new MemoryTreeSelection();
            PhotoStore store = PhotoStore.Instance;
            bool changed = Selection.Sync(store?.AllRecords ?? Enumerable.Empty<PhotoRecord>(), r => Exists(store, r));
            SelectionSyncCount++;
            seenStore = store;
            seenVersion = store?.Version ?? -1;
            var byId = new Dictionary<string, PhotoRecord>();
            if (store != null)
                foreach (PhotoRecord r in store.AllRecords)
                    if (r != null && !r.id.NullOrEmpty()) byId[r.id] = r;
            for (int i = 0; i < records.Length; i++)
            {
                PhotoRecord record = null;
                string id = SlotId(i);
                if (!id.NullOrEmpty()) byId.TryGetValue(id, out record);
                bool exists = Exists(store, record);
                changed |= records[i] != record || available[i] != exists;
                records[i] = record;
                available[i] = exists;
                bool retryMissing = forceFiles && bindings[i].Mat == null;
                if (retryMissing || !exists) bindings[i].Unbind();
                if (retryMissing && exists)
                {
                    PhotoTextureCache.Forget(store.FilePath(record));
                    PhotoTextureCache.Forget(store.ThumbPath(record));
                }
            }
            nextFileCheckFrame = Time.frameCount + 120;
            BindSelected();
            if (changed) Reminisce.Dirty();
        }

        private void EnsureCurrent()
        {
            PhotoStore store = PhotoStore.Instance;
            if (!ReferenceEquals(store, seenStore) || seenVersion != (store?.Version ?? -1)) Refresh(false);
            else if (Time.frameCount >= nextFileCheckFrame)
            {
                // Only selected records, not the whole album. Covers files changed externally without a store version increment.
                nextFileCheckFrame = Time.frameCount + 120;
                bool changed = false;
                for (int i = 0; i < records.Length; i++)
                {
                    bool exists = Exists(store, records[i]);
                    if (exists == available[i]) continue;
                    available[i] = exists;
                    changed = true;
                    bindings[i].Unbind();
                    if (exists)
                    {
                        PhotoTextureCache.Forget(store.FilePath(records[i]));
                        PhotoTextureCache.Forget(store.ThumbPath(records[i]));
                    }
                }
                if (changed) Reminisce.Dirty();
            }
        }

        private void BindSelected()
        {
            for (int i = 0; i < bindings.Length; i++) bindings[i].Bind(available[i] ? records[i] : null, true);
        }

        private void UnbindAll()
        {
            foreach (PhotoBinding binding in bindings) binding.Unbind();
        }

        public void SetPhoto(int slot, string id)
        {
            if (Selection.Pick(slot, id, true)) RefreshSelection();
        }

        public void SetPinned(int slot, bool pin)
        {
            if (Selection.SetPinned(slot, pin)) RefreshSelection();
        }

        public void ClearSlot(int slot)
        {
            if (Selection.Clear(slot)) RefreshSelection();
        }

        public void Configure(bool enabled, IEnumerable<string> kinds)
        {
            if (Selection.Configure(enabled, kinds)) RefreshSelection();
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            // Map loading calls SpawnSetup on a worker thread. Decode textures only on first draw/UI access, like the existing frames.
            seenStore = null;
            seenVersion = -1;
            nextFileCheckFrame = -1;
            Reminisce.Dirty();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref Selection, "mfTreeSelection");
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            if (Selection == null) Selection = new MemoryTreeSelection();
            UnbindAll();
            seenStore = null;
            seenVersion = -1;
            nextFileCheckFrame = -1;
            Array.Clear(records, 0, records.Length);
            Array.Clear(available, 0, available.Length);
            if (def.MadeFromStuff && Stuff == null) SetStuffDirect(GenStuff.DefaultStuffFor(def));
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            UnbindAll();
            base.DeSpawn(mode);
            Reminisce.Dirty();
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            UnbindAll();
            base.Destroy(mode);
            Reminisce.Dirty();
        }

        public Rect SlotCrop(int slot)
        {
            MemoryTreeLayout.Slot layout = MemoryTreeLayout.At(slot);
            if (layout == null) return new Rect(0f, 0f, 1f, 1f);
            Texture texture = BoundTextureAt(slot);
            float aspect = texture != null && texture.height > 0 ? (float)texture.width / texture.height : records[slot]?.DisplayAspect ?? PhotoCapture.DefaultAspect;
            Vector2 size = layout.ImageSize;
            return FrameLook.CoverUV(aspect, size.x / size.y);
        }

        private void DrawString(int slot, Vector3 drawLoc)
        {
            var layout = MemoryTreeLayout.At(slot);
            Vector3 from = layout.AnchorAt(drawLoc), to = layout.HookAt(drawLoc);
            Vector3 delta = to - from, middle = (from + to) / 2f;
            middle.y = drawLoc.y + FrameLook.Layer * 2f;
            float angle = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
            FrameLook.Quad(middle, MemoryTreeLayout.StringWidth, Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z), FrameLook.Solid(StringColor), angle);
            drawnStringCount++;
        }

        private void DrawCard(int slot, Vector3 drawLoc)
        {
            var layout = MemoryTreeLayout.At(slot);
            Vector2 size = layout.CellSize, imageSize = layout.ImageSize;
            Vector3 c = layout.CenterAt(drawLoc);
            c.y = drawLoc.y + FrameLook.Layer * 3f;
            const float px = MemoryTreeLayout.PixelCells;
            // Miniature card borders scale from the 6/6/16 px concept; full-sized frame borders would swallow these prints.
            FrameLook.Quad(c + new Vector3(2f * px, 0f, -3f * px), size.x + 4f * px, size.y + 4f * px, FrameLook.Clear(Shadow), layout.rotationDeg);
            c.y += FrameLook.Layer;
            FrameLook.Quad(c, size.x, size.y, FrameLook.Solid(Rim), layout.rotationDeg);
            c.y += FrameLook.Layer;
            FrameLook.Quad(c, size.x - 2.8f * px, size.y - 2.8f * px, FrameLook.Solid(Card), layout.rotationDeg);
            Vector3 image = layout.ImageCenterAt(drawLoc);
            image.y = c.y + FrameLook.Layer;
            Material material = bindings[slot].Mat;
            if (material == null)
            {
                FrameLook.Quad(image, imageSize.x, imageSize.y, FrameLook.Solid(Empty), layout.rotationDeg);
                drawnPlaceholderCount++;
            }
            else
            {
                Texture texture = material.mainTexture;
                float aspect = texture != null && texture.height > 0 ? (float)texture.width / texture.height : records[slot].DisplayAspect;
                Fading.DrawPhoto(Matrix4x4.TRS(image, layout.Rotation, new Vector3(imageSize.x, 1f, imageSize.y)), material,
                    Fading.TintFor(records[slot], IsFine), FrameLook.CoverMesh(aspect, imageSize.x / imageSize.y));
                drawnPhotoCount++;
            }
            Vector3 caption = layout.CenterAt(drawLoc) + layout.Rotation * new Vector3(0f, 0f, -size.y / 2f + 9.2f * px);
            caption.y = image.y + FrameLook.Layer;
            FrameLook.Quad(caption, 26f * px, 1.6f * px, FrameLook.Solid(CaptionLine), layout.rotationDeg);
            drawnCardCount++;
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);
            lastDrawFrame = Time.frameCount;
            drawnPhotoCount = drawnStringCount = drawnCardCount = drawnPlaceholderCount = 0;
            EnsureCurrent();
            BindSelected();
            // All strings are below all cards, matching the concept and avoiding a later string crossing an earlier print.
            for (int i = 0; i < records.Length; i++) if (!SlotId(i).NullOrEmpty()) DrawString(i, drawLoc);
            for (int i = 0; i < records.Length; i++) if (!SlotId(i).NullOrEmpty()) DrawCard(i, drawLoc);
        }

        /// <summary>The UI coordinates of the real rotated card corners, for map clicks and native probe checks.</summary>
        public Vector2[] CardUICorners(int slot)
        {
            MemoryTreeLayout.Slot layout = MemoryTreeLayout.At(slot);
            if (layout == null || !Spawned) return Array.Empty<Vector2>();
            Vector3 drawLoc = DrawPos;
            drawLoc.y += FrameLook.Layer * 4f; // the actual outer-card altitude in DrawCard
            return layout.CornersAt(drawLoc).Select(p => p.MapToUIPosition()).ToArray();
        }

        public int PhotoAtUI(Vector2 point)
        {
            EnsureCurrent();
            for (int i = records.Length - 1; i >= 0; i--)
                if (records[i] != null && available[i] && bindings[i].Mat != null && MemoryTreeLayout.Contains(point, CardUICorners(i))) return i;
            return -1;
        }

        /// <summary>Called after the engine's map-click handlers, with windows and targeting already given their input priority.</summary>
        public bool HandlePhotoInput()
        {
            if (!Spawned || Find.CurrentMap != Map || Find.Selector.SingleSelectedThing != this || Find.DesignatorManager.SelectedDesignator != null || Find.Targeter.IsTargeting) return false;
            Event ev = Event.current;
            if (ev == null || ev.type != EventType.MouseDown || ev.button != 0 || ev.shift || ev.control || ev.alt) return false;
            if (Find.WindowStack.Windows.Any(w => w.absorbInputAroundWindow || (w.layer == WindowLayer.Dialog && w.windowRect.Contains(ev.mousePosition)))) return false;
            int slot = PhotoAtUI(ev.mousePosition);
            if (slot < 0) return false;
            PhotoRecord record = RecordAt(slot);
            ev.Use();
            MainTabWindow_Album.Open(record);
            mapClickCount++;
            lastClickedSlot = slot;
            return true;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos()) yield return gizmo;
            if (Faction != Faction.OfPlayer) yield break;
            yield return new Command_Action
            {
                defaultLabel = "MF_TreeManage".Translate(),
                defaultDesc = "MF_TreeManageDesc".Translate(),
                icon = MFIcons.Frame,
                action = () => Find.WindowStack.Add(new Dialog_PhotoTree(this))
            };
        }

        public override string GetInspectString()
        {
            EnsureCurrent();
            string mode = (Selection.autoFill ? "MF_TreeAutoOn" : "MF_TreeAutoOff").Translate();
            int count = Enumerable.Range(0, MemoryTreeLayout.Count).Count(i => !SlotId(i).NullOrEmpty());
            string line = "MF_TreeInspect".Translate(count, MemoryTreeLayout.Count, mode);
            string original = base.GetInspectString();
            return original.NullOrEmpty() ? line : original + "\n" + line;
        }
    }

    /// <summary>ThingOverlays invokes DrawGUIOverlay only on Repaint, so it cannot consume clicks. This actual UIRoot_Play input
    /// path runs after the window stack and targeting/designators, before the ordinary map selector's low-priority handling.</summary>
    [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.HandleMapClicks))]
    internal static class Patch_MemoryTreePhotoClick
    {
        private static void Postfix()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap) return;
            if (!(Find.Selector.SingleSelectedThing is Building_PhotoTree tree)) return;
            tree.mapInputDispatchCount++;
            tree.HandlePhotoInput();
        }
    }
}
