using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>One tree's saved photographs. Manual choices are pinned; auto controls apply to unpinned slots.</summary>
    public sealed class Dialog_PhotoTree : Window
    {
        private readonly Building_PhotoTree tree;
        private Vector2 scroll;
        private Vector2 detailScroll;
        private int selected;
        public override Vector2 InitialSize => SizeForScreen(UI.screenWidth, UI.screenHeight);

        /// <summary>Verse.Window centers InitialSize without clamping it; these are logical UI pixels, including UI scaling.</summary>
        public static Vector2 SizeForScreen(float width, float height) =>
            new Vector2(Mathf.Max(1f, Mathf.Min(1000f, width - 24f)), Mathf.Max(1f, Mathf.Min(750f, height - 24f)));

        public Dialog_PhotoTree(Building_PhotoTree tree)
        {
            this.tree = tree;
            tree?.RefreshSelection();
            doCloseX = true;
            doCloseButton = true;
            absorbInputAroundWindow = true;
            forcePause = true;
            closeOnClickedOutside = false;
            closeOnAccept = false;
        }

        public int SelectedSlot => selected;
        public void SelectSlot(int slot)
        {
            if (slot < 0 || slot >= MemoryTreeSelection.Capacity || slot == selected) return;
            selected = slot;
            detailScroll = Vector2.zero;
        }

        public void ChooseSelected()
        {
            if (tree == null || tree.Destroyed) return;
            int slot = selected;
            Find.WindowStack.Add(new Dialog_ChoosePhoto("MF_Tree_Slot".Translate(slot + 1),
                tree.Selection.slots[slot].photoId, id => tree.SetPhoto(slot, id)));
        }

        public void OpenSelected()
        {
            var rec = tree?.RecordAt(selected);
            if (rec != null && rec.HasFile && !rec.Archived) MainTabWindow_Album.Open(rec);
        }

        public void ToggleSelectedPin()
        {
            if (tree == null || tree.Destroyed) return;
            var slot = tree.Selection.slots[selected];
            if (!slot.photoId.NullOrEmpty()) tree.SetPinned(selected, !slot.pinned);
        }

        public void RemoveSelected() { if (tree != null && !tree.Destroyed) tree.ClearSlot(selected); }

        private static void Label(Rect rect, string text, GameFont? font = null, Color? color = null)
        {
            Color old = GUI.color;
            GUI.color = color ?? AlbumStyle.Theme.Ink;
            try { AlbumStyle.Label(rect, text, font); }
            finally { GUI.color = old; }
        }

        private void KindMenu()
        {
            var store = PhotoStore.Instance;
            var kinds = (store?.Records ?? Array.Empty<PhotoRecord>()).Where(r => r != null && !r.kind.NullOrEmpty())
                .Select(r => r.kind).Distinct().OrderBy(k => MainTabWindow_Album.KindLabel(k)).ToList();
            var options = new List<FloatMenuOption> {
                new FloatMenuOption((tree.Selection.allowedKinds.Count == 0 ? "✓ " : "") + "MF_Tree_AllKinds".Translate(),
                    () => tree.Configure(tree.Selection.autoFill, Array.Empty<string>())) };
            foreach (string kind in kinds)
            {
                string current = kind;
                bool included = tree.Selection.allowedKinds.Contains(current);
                options.Add(new FloatMenuOption((included ? "✓ " : "") + MainTabWindow_Album.KindLabel(current), () =>
                {
                    var next = new HashSet<string>(tree.Selection.allowedKinds);
                    if (next.Contains(current)) next.Remove(current); else next.Add(current);
                    tree.Configure(tree.Selection.autoFill, next);
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (tree == null || tree.Destroyed) { Close(); return; }
            tree.EnsureSelection();
            using (AlbumStyle.Begin())
            {
                var theme = AlbumStyle.Theme;
                AlbumStyle.DrawPaper(inRect);
                inRect = inRect.ContractedBy(12f);
                int count = tree.Selection.slots.Count(s => !s.photoId.NullOrEmpty());
                int pinned = tree.Selection.slots.Count(s => s.pinned && !s.photoId.NullOrEmpty());
                Label(new Rect(inRect.x, inRect.y, inRect.width - 44f, 37f), "MF_Tree_Title".Translate(), GameFont.Medium);
                Label(new Rect(inRect.x, inRect.y + 40f, inRect.width, 29f), "MF_Tree_Summary".Translate(count, pinned), GameFont.Small, theme.MutedInk);
                float controlsY = inRect.y + 77f;
                bool auto = tree.Selection.autoFill;
                Widgets.Checkbox(new Vector2(inRect.x, controlsY + 3f), ref auto, 24f);
                if (auto != tree.Selection.autoFill) tree.Configure(auto, tree.Selection.allowedKinds);
                Label(new Rect(inRect.x + 32f, controlsY, Mathf.Min(292f, inRect.width - 32f), 32f), "MF_Tree_AutoFill".Translate(), GameFont.Small);
                TooltipHandler.TipRegion(new Rect(inRect.x, controlsY, Mathf.Min(326f, inRect.width), 32f), "MF_Tree_AutoFillTip".Translate());
                bool stackedControls = inRect.width < 600f;
                Rect filters = new Rect(inRect.x + (stackedControls ? 0f : 338f), controlsY + (stackedControls ? 38f : 0f), Mathf.Min(250f, inRect.width), 32f);
                string filterLabel = tree.Selection.allowedKinds.Count == 0 ? "MF_Tree_AllKinds".Translate().ToString()
                    : "MF_Tree_SelectedKinds".Translate(tree.Selection.allowedKinds.Count).ToString();
                if (AlbumStyle.Button(filters, "MF_Tree_Filters".Translate(filterLabel))) KindMenu();
                TooltipHandler.TipRegion(filters, "MF_Tree_FilterTip".Translate());
                float top = filters.yMax + 12f;
                float detailWidth = Mathf.Min(290f, Mathf.Max(220f, inRect.width * .35f));
                Rect area = new Rect(inRect.x, top, inRect.width - detailWidth - 14f, inRect.yMax - top - 55f);
                DrawSlots(area);
                DrawSelected(new Rect(area.xMax + 14f, area.y, detailWidth, area.height));
            }
        }

        private void DrawSlots(Rect area)
        {
            const float gap = 8f, height = 132f;
            int cols = Mathf.Clamp(Mathf.FloorToInt((area.width - 16f + gap) / (126f + gap)), 1, 4);
            float width = (area.width - 16f - gap * (cols - 1)) / cols;
            int rows = Mathf.CeilToInt(MemoryTreeSelection.Capacity / (float)cols);
            Rect view = new Rect(0f, 0f, area.width - 16f, rows * (height + gap) - gap);
            Widgets.BeginScrollView(area, ref scroll, view);
            int first = Mathf.Max(0, (int)(scroll.y / (height + gap))) * cols;
            int last = Mathf.Min(MemoryTreeSelection.Capacity, ((int)((scroll.y + area.height) / (height + gap)) + 1) * cols);
            for (int i = first; i < last; i++)
            {
                Rect card = new Rect((i % cols) * (width + gap), (i / cols) * (height + gap), width, height);
                Widgets.DrawBoxSolid(card, i == selected ? Color.Lerp(AlbumStyle.Theme.Mat, AlbumStyle.Theme.Accent, .25f) : AlbumStyle.Theme.Mat);
                var slot = tree.Selection.slots[i];
                Label(new Rect(card.x + 6f, card.y + 3f, width - 12f, 22f),
                    "MF_Tree_Slot".Translate(i + 1) + (slot.pinned ? "  ◆" : ""), GameFont.Tiny);
                var rec = tree.RecordAt(i);
                Rect photo = new Rect(card.x + 7f, card.y + 27f, width - 14f, 75f);
                var tex = rec == null || rec.Archived ? null : PhotoTextureCache.Thumb(PhotoStore.Instance.ThumbPath(rec), PhotoStore.Instance.FilePath(rec));
                if (tex != null) Widgets.DrawTextureFitted(photo, tex, 1f);
                else Label(photo, (rec == null || rec.Archived && !rec.HasFile ? "MF_Tree_EmptySlot" : rec.Archived ? "MF_Archive_InArchive" : "MF_Tree_MissingPhoto").Translate(), GameFont.Tiny, AlbumStyle.Theme.MutedInk);
                if (rec != null) Label(new Rect(card.x + 6f, card.y + 106f, width - 12f, 21f), rec.DisplayTitle ?? "", GameFont.Tiny);
                TooltipHandler.TipRegion(card, rec == null ? "MF_Tree_EmptySlot".Translate().ToString() : rec.DisplayTitle + "\n" + rec.dateLabel);
                if (Widgets.ButtonInvisible(card)) SelectSlot(i);
            }
            Widgets.EndScrollView();
        }

        private void DrawSelected(Rect area)
        {
            // Keep the photograph, metadata and controls in one scrollable column. A shorter screen must not move buttons over the title/date.
            Rect view = new Rect(0f, 0f, area.width - 16f, 505f);
            Widgets.BeginScrollView(area, ref detailScroll, view);
            try { DrawSelectedContents(view); }
            finally { Widgets.EndScrollView(); }
        }

        private void DrawSelectedContents(Rect area)
        {
            var slot = tree.Selection.slots[selected];
            var rec = tree.RecordAt(selected);
            Label(new Rect(area.x, area.y, area.width, 32f), "MF_Tree_Slot".Translate(selected + 1), GameFont.Small);
            Rect photo = new Rect(area.x, area.y + 38f, area.width, 180f);
            Widgets.DrawBoxSolid(photo, AlbumStyle.Theme.Mat);
            var store = PhotoStore.Instance;
            var tex = rec == null || rec.Archived ? null : PhotoTextureCache.Thumb(store.ThumbPath(rec), store.FilePath(rec));
            if (tex != null) Widgets.DrawTextureFitted(photo.ContractedBy(7f), tex, 1f);
            else Label(photo.ContractedBy(10f), (rec == null || rec.Archived && !rec.HasFile ? "MF_Tree_EmptySlot" : rec.Archived ? "MF_Archive_InArchive" : "MF_Tree_MissingPhoto").Translate(), GameFont.Small, AlbumStyle.Theme.MutedInk);
            float y = photo.yMax + 10f;
            if (rec != null)
            {
                Label(new Rect(area.x, y, area.width, 42f), rec.DisplayTitle ?? "", GameFont.Small); y += 43f;
                Label(new Rect(area.x, y, area.width, 26f), rec.dateLabel ?? "", GameFont.Tiny, AlbumStyle.Theme.MutedInk); y += 32f;
            }
            else { Label(new Rect(area.x, y, area.width, 38f), "MF_Tree_NoPhotos".Translate(), GameFont.Small); y += 46f; }
            if (AlbumStyle.Button(new Rect(area.x, y, area.width, 34f), "MF_Tree_Choose".Translate())) ChooseSelected(); y += 42f;
            if (AlbumStyle.Button(new Rect(area.x, y, area.width / 2f - 4f, 34f), "MF_Tree_Open".Translate(), rec != null && rec.HasFile && !rec.Archived)) OpenSelected();
            if (AlbumStyle.Button(new Rect(area.x + area.width / 2f + 4f, y, area.width / 2f - 4f, 34f),
                (slot.pinned ? "MF_Tree_Unpin" : "MF_Tree_Pin").Translate(), rec != null)) ToggleSelectedPin(); y += 42f;
            if (AlbumStyle.Button(new Rect(area.x, y, area.width, 34f), "MF_Tree_Remove".Translate(), rec != null)) RemoveSelected();
            TooltipHandler.TipRegion(new Rect(area.x, y, area.width, 34f), "MF_Tree_RemoveTip".Translate());
            Label(new Rect(area.x, y + 46f, area.width, 65f),
                (tree.Selection.autoFill && tree.Selection.slots.All(s => s.pinned) ? "MF_Tree_FullPinned" : "MF_Tree_Policy").Translate(),
                GameFont.Tiny, AlbumStyle.Theme.MutedInk);
        }
    }
}
