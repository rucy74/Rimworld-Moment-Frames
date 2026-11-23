using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>UI-only playback. Holds one bounded preview, never changes game speed or original images.</summary>
    internal sealed class Dialog_SettlementSequence : Window
    {
        private readonly PhotoStore store;
        private readonly List<SettlementHistory.Entry> entries;
        private readonly string title;
        private readonly SettlementPhotoBinding binding = new SettlementPhotoBinding();
        private int index;
        private bool playing;
        private float advanceAt;
        internal int Index => index;
        internal bool Playing => playing;
        protected override float Margin => 0f;
        private readonly float aspect;
        /// <summary>Shaped to the photos (2026-11-06): a square area photo gets a tall, narrow window, so the picture fills it instead of
        /// sitting in a wide page. Never narrower than the three buttons need.</summary>
        public override Vector2 InitialSize
        {
            get
            {
                float height = Mathf.Min(900f, UI.screenHeight - 50f);
                float photo = height - 44f - SharedPhotoUI.ControlHeight * 4f - 28f;
                float width = photo * aspect + 44f;
                return new Vector2(Mathf.Clamp(width, Mathf.Min(720f, UI.screenWidth - 40f), UI.screenWidth - 40f), height);
            }
        }
        internal Dialog_SettlementSequence(PhotoStore store, IEnumerable<SettlementHistory.Entry> entries, string title)
        {
            this.store = store; this.entries = entries.ToList(); this.title = AlbumPresentation.Title(title);
            aspect = Mathf.Clamp(this.entries.Count > 0 ? store?.Get(this.entries[0].PhotoId)?.Aspect ?? 1f : 1f, 0.5f, 2.5f);
            doCloseX = false; doWindowBackground = false; forcePause = false; closeOnAccept = false;
        }
        internal void Select(int next)
        { index = Mathf.Clamp(next, 0, Mathf.Max(0, entries.Count - 1)); binding.Unbind(); playing = false; }
        internal void TogglePlaying(float now)
        {
            if (entries.Count < 2) return;
            if (index == entries.Count - 1 && !playing) Select(0);
            playing = !playing; advanceAt = now + 2f;
        }
        internal void Advance(float now)
        {
            if (!playing || now < advanceAt) return;
            if (index + 1 < entries.Count) { index++; binding.Unbind(); advanceAt = now + 2f; }
            if (index == entries.Count - 1) playing = false;
        }
        public override void PostClose() { binding.Unbind(); base.PostClose(); }
        public override void DoWindowContents(Rect inRect)
        {
            Advance(Time.realtimeSinceStartup);
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(inRect); Rect page = inRect.ContractedBy(22f);
                float h = SharedPhotoUI.ControlHeight;
                SharedPhotoUI.Heading(new Rect(page.x, page.y, page.width - h - 10f, h), title);
                if (SharedPhotoUI.Button(new Rect(page.xMax - h, page.y, h, h), "×")) { Close(); return; }
                if (entries.Count == 0) return;
                var entry = entries[index]; var rec = store.Get(entry.PhotoId);
                string date = "MF_Phase2_Period".Translate(entry.Stamp / 4, entry.Stamp % 4 + 1) + "  ·  " + (index + 1) + " / " + entries.Count;
                SharedPhotoUI.Label(new Rect(page.x, page.y + h + 4f, page.width, h), AlbumPresentation.Title(date));
                // The photo itself fills the space between the date and the controls, on the page (no mat, no print border).
                Rect image = new Rect(page.x, page.y + h * 2f + 12f, page.width, page.height - h * 4f - 28f);
                if (rec != null)
                {
                    binding.Bind(rec);
                    Texture texture = binding.Material?.mainTexture ?? PhotoTextureCache.Thumb(store.ThumbPath(rec), store.FilePath(rec));
                    if (texture != null) GUI.DrawTexture(image, texture, ScaleMode.ScaleToFit);
                    if (Widgets.ButtonInvisible(image)) { playing = false; Find.WindowStack.Add(new Dialog_MFPhotoZoom(rec)); }
                    TooltipHandler.TipRegion(image, "MF_FavoriteSequenceZoom".Translate());
                }
                int selected = Mathf.RoundToInt(Widgets.HorizontalSlider(new Rect(page.x + 12f, page.yMax - h * 2f, page.width - 24f, h - 6f), index, 0f, Mathf.Max(1, entries.Count - 1)));
                if (selected != index && entries.Count > 1) Select(selected);
                float w = (page.width - 16f) / 3f;
                if (SharedPhotoUI.Button(new Rect(page.x, page.yMax - h, w, h), "MF_FavoritePrevious".Translate(), index > 0)) Select(index - 1);
                if (SharedPhotoUI.Button(new Rect(page.x + w + 8f, page.yMax - h, w, h), (playing ? "MF_FavoritePause" : "MF_FavoritePlay").Translate(), entries.Count > 1)) TogglePlaying(Time.realtimeSinceStartup);
                if (SharedPhotoUI.Button(new Rect(page.x + (w + 8f) * 2f, page.yMax - h, w, h), "MF_FavoriteNext".Translate(), index + 1 < entries.Count)) Select(index + 1);
            }
        }
    }
}
