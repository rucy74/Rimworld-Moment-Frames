using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>A draft editor: only Save changes the record. All close/cancel paths preserve the existing stickers and photograph.</summary>
    public sealed class Dialog_AlbumStickers : Window
    {
        private readonly PhotoRecord record;
        private readonly string viewingVersion;
        private readonly List<PhotoSticker> draft;
        private int selected = -1, dragging = -1;
        private Vector2 dragOffset;
        private string heldPath;
        private Material heldMaterial;
        internal IReadOnlyList<PhotoSticker> Draft => draft;
        internal int SelectedIndex => selected;
        internal Rect DrawnFrame { get; private set; }
        internal Rect DrawnImage { get; private set; }
        internal string HeldPath => heldPath;
        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(Mathf.Max(1f, Mathf.Min(880f, UI.screenWidth - 24f)),
            Mathf.Max(1f, Mathf.Min(760f, UI.screenHeight - 24f)));

        public Dialog_AlbumStickers(PhotoRecord record, string viewingVersion = null)
        {
            this.record = record;
            this.viewingVersion = viewingVersion ?? record?.Shown?.id ?? "";
            draft = AlbumStickers.Copy(record?.stickers);
            doWindowBackground = false;
            doCloseButton = false;
            doCloseX = false;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            absorbInputAroundWindow = true;
            forcePause = true;
        }

        internal bool SelectSticker(int index)
        {
            if (index < -1 || index >= draft.Count) return false;
            selected = index;
            return true;
        }

        internal bool AddSticker(PhotoIcon icon)
        {
            // Start in separated positions along the upper margin; dragging can use any paper edge.
            var position = new Vector2((draft.Count + 1f) / (AlbumStickers.MaxCount + 1f), .035f);
            if (!AlbumStickers.Add(draft, icon, position)) return false;
            selected = draft.Count - 1;
            return true;
        }

        internal bool MoveSticker(int index, Vector2 mousePosition)
        {
            if (index < 0 || index >= draft.Count || DrawnFrame.width <= 0f || DrawnFrame.height <= 0f
                || !AlbumStickers.Finite(mousePosition.x) || !AlbumStickers.Finite(mousePosition.y)) return false;
            var request = new PhotoSticker(draft[index].icon, (mousePosition.x - DrawnFrame.x) / DrawnFrame.width,
                (mousePosition.y - DrawnFrame.y) / DrawnFrame.height);
            if (!AlbumStickers.TryPlacedRect(request, DrawnFrame, DrawnImage, out Rect safe)) return false;
            return AlbumStickers.Move(draft, index, new Vector2((safe.center.x - DrawnFrame.x) / DrawnFrame.width,
                (safe.center.y - DrawnFrame.y) / DrawnFrame.height));
        }

        internal bool RemoveSelected()
        {
            if (!AlbumStickers.Remove(draft, selected)) return false;
            selected = draft.Count == 0 ? -1 : Mathf.Min(selected, draft.Count - 1);
            dragging = -1;
            return true;
        }

        internal bool SaveDraft()
        {
            bool changed = AlbumStickers.Commit(record, draft);
            Close();
            return changed;
        }

        internal void CancelDraft() => Close();

        private Texture2D DisplayTexture()
        {
            var store = PhotoStore.Instance;
            string desired = record != null && record.HasFile && store != null ? store.VersionPath(record, viewingVersion) : null;
            if (desired != heldPath || (heldMaterial != null && !PhotoTextureCache.Owns(heldPath, heldMaterial)))
            {
                ReleaseTexture();
                heldPath = desired;
                heldMaterial = PhotoTextureCache.Acquire(heldPath);
            }
            return heldMaterial == null ? null : heldMaterial.mainTexture as Texture2D;
        }

        private void ReleaseTexture()
        {
            if (PhotoTextureCache.Owns(heldPath, heldMaterial)) PhotoTextureCache.Release(heldPath);
            heldPath = null; heldMaterial = null;
        }

        public override void PostClose()
        {
            ReleaseTexture();
            base.PostClose();
        }

        private float DrawPalette(Rect row)
        {
            const float gap = 8f;
            int count = AlbumStickers.Palette.Length;
            int maxColumns = Mathf.Max(1, Mathf.FloorToInt((row.width + gap) / (40f + gap)));
            int rows = Mathf.CeilToInt(count / (float)maxColumns);
            int columns = Mathf.CeilToInt(count / (float)rows);
            float cell = Mathf.Min(60f, (row.width - (columns - 1f) * gap) / columns);
            bool active = draft.Count < AlbumStickers.MaxCount;
            for (int i = 0; i < count; i++)
            {
                PhotoIcon icon = AlbumStickers.Palette[i];
                Rect rect = new Rect(row.x + (i % columns) * (cell + gap),
                    row.y + (i / columns) * (row.height + gap), cell, row.height);
                if (AlbumStyle.Button(rect, "", active)) AddSticker(icon);
                Color color = AlbumStickers.ColorFor(icon); if (!active) color.a = .45f;
                float size = Mathf.Min(30f, Mathf.Min(rect.width, rect.height) - 10f);
                PhotoIconArt.Draw(new Rect(rect.center.x - size / 2f, rect.center.y - size / 2f, size, size), icon, color);
                TooltipHandler.TipRegion(rect, ("MF_Sticker_" + icon).Translate());
            }
            return rows * row.height + (rows - 1f) * gap;
        }

        private void DrawCanvas(Rect frame)
        {
            DrawnFrame = frame;
            Texture2D texture = DisplayTexture();
            float aspect = texture != null && texture.height > 0 ? texture.width / (float)texture.height
                : record?.Edit(viewingVersion)?.aspect ?? record?.Aspect ?? PhotoCapture.DefaultAspect;
            DrawnImage = AlbumStickers.ImageRect(frame, aspect);
            Widgets.DrawBoxSolid(frame, AlbumStyle.Theme.Mat);
            if (texture != null) GUI.DrawTexture(DrawnImage, texture, ScaleMode.StretchToFill);
            else
            {
                Widgets.DrawBoxSolid(DrawnImage, AlbumStyle.Theme.Paper);
                SharedPhotoUI.Label(DrawnImage.ContractedBy(12f), "MF_MissingFile".Translate(), true);
            }
            AlbumStickers.Draw(draft, frame, DrawnImage);
            var ev = Event.current;
            for (int i = draft.Count - 1; i >= 0; i--)
            {
                if (!AlbumStickers.TryPlacedRect(draft[i], frame, DrawnImage, out Rect rect)) continue;
                if (i == selected)
                {
                    Color old = GUI.color; GUI.color = AlbumStyle.Theme.Accent;
                    Widgets.DrawBox(rect.ExpandedBy(3f)); GUI.color = old;
                }
                if (ev.type == EventType.MouseDown && ev.button == 0 && rect.ExpandedBy(3f).Contains(ev.mousePosition))
                {
                    SelectSticker(i); dragging = i; dragOffset = ev.mousePosition - rect.center; ev.Use(); break;
                }
            }
            if (dragging >= 0 && ev.type == EventType.MouseDrag && ev.button == 0)
            {
                MoveSticker(dragging, ev.mousePosition - dragOffset); ev.Use();
            }
            if (dragging >= 0 && ev.type == EventType.MouseUp && ev.button == 0) { dragging = -1; ev.Use(); }
            if (ev.type == EventType.MouseDown && ev.button == 0 && frame.Contains(ev.mousePosition)) SelectSticker(-1);
        }

        public override void DoWindowContents(Rect inRect)
        {
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(inRect);
                Rect page = inRect.ContractedBy(inRect.width < 520f ? 16f : 24f);
                float control = SharedPhotoUI.ControlHeight;
                string title = "MF_Stickers_Title".Translate();
                float headingH = Mathf.Max(control, AlbumStyle.TextHeight(title, page.width - control - 12f, GameFont.Medium));
                SharedPhotoUI.Heading(new Rect(page.x, page.y, page.width - control - 12f, headingH), title);
                if (SharedPhotoUI.Button(new Rect(page.xMax - control, page.y, control, control), "×")) { CancelDraft(); return; }
                string hint = "MF_Stickers_Hint".Translate();
                float hintH = AlbumStyle.TextHeight(hint, page.width);
                float y = page.y + headingH + 7f;
                SharedPhotoUI.Label(new Rect(page.x, y, page.width, hintH), hint, true);
                y += hintH + 10f;
                y += DrawPalette(new Rect(page.x, y, page.width, control)) + 6f;
                string count = (draft.Count >= AlbumStickers.MaxCount ? "MF_Stickers_Limit" : "MF_Stickers_Count").Translate(draft.Count, AlbumStickers.MaxCount);
                float countH = AlbumStyle.TextHeight(count, page.width);
                SharedPhotoUI.Label(new Rect(page.x, y, page.width, countH), count, true);
                y += countH + 8f;
                float footerY = page.yMax - control;
                Rect canvas = new Rect(page.x, y, page.width, Mathf.Max(30f, footerY - 12f - y));
                DrawCanvas(canvas);
                float gap = 10f, buttonW = Mathf.Max(40f, (page.width - gap * 2f) / 3f);
                if (SharedPhotoUI.Button(new Rect(page.x, footerY, buttonW, control), "MF_Stickers_Delete".Translate(), selected >= 0)) RemoveSelected();
                if (SharedPhotoUI.Button(new Rect(page.x + buttonW + gap, footerY, buttonW, control), "MF_Stickers_Cancel".Translate())) { CancelDraft(); return; }
                if (SharedPhotoUI.Button(new Rect(page.x + (buttonW + gap) * 2f, footerY, buttonW, control), "MF_Stickers_Save".Translate())) SaveDraft();
            }
        }
    }
}
