using System;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Two existing birthday photographs. Unknown years and ages are never synthesized.</summary>
    internal sealed class Dialog_MFBirthdayComparison : Window
    {
        private readonly PhotoRecord previous, current;
        private string previousPath, currentPath;
        private Material previousImage, currentImage;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(1260f, UI.screenWidth - 40f), Mathf.Min(800f, UI.screenHeight - 50f));
        internal Dialog_MFBirthdayComparison(PhotoRecord previous, PhotoRecord current)
        {
            this.previous = previous; this.current = current;
            doCloseX = true; absorbInputAroundWindow = true; closeOnClickedOutside = false;
        }
        public override void PreOpen()
        {
            base.PreOpen();
            var store = PhotoStore.Instance;
            previousPath = store?.FilePath(previous); currentPath = store?.FilePath(current);
            try
            {
                if (!previousPath.NullOrEmpty()) previousImage = PhotoTextureCache.Acquire(previousPath);
                if (!currentPath.NullOrEmpty()) currentImage = PhotoTextureCache.Acquire(currentPath);
            }
            catch (Exception e) { Log.Warning("[MomentFrames] could not load a birthday comparison: " + e.Message); }
        }
        public override void PostClose()
        {
            if (previousImage != null) PhotoTextureCache.Release(previousPath);
            if (currentImage != null) PhotoTextureCache.Release(currentPath);
            previousImage = currentImage = null;
            base.PostClose();
        }
        public override void DoWindowContents(Rect area)
        {
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(area);
                Rect content = area.ContractedBy(20f);
                GUI.color = AlbumStyle.Theme.Ink;
                Text.Font = GameFont.Medium;
                AlbumStyle.BodyLabel(new Rect(content.x, content.y, content.width - 28f, 42f), "MF_Phase2_BirthdayCompare".Translate());
                Text.Font = GameFont.Small;
                const float gap = 24f;
                float width = (content.width - gap) / 2f;
                float top = content.y + 54f;
                Panel(new Rect(content.x, top, width, content.yMax - top), previous, previousImage, "MF_Phase2_PreviousBirthday".Translate());
                Panel(new Rect(content.x + width + gap, top, width, content.yMax - top), current, currentImage, "MF_Phase2_CurrentBirthday".Translate());
                GUI.color = Color.white;
            }
        }
        private static void Panel(Rect area, PhotoRecord record, Material material, string label)
        {
            string heading = label + (record.eventAge >= 0 ? " · " + "MF_Phase2_Age".Translate(record.eventAge) : "");
            float header = AlbumStyle.TextHeight(heading, area.width) + 8f;
            AlbumStyle.BodyLabel(new Rect(area.x, area.y, area.width, header), heading);
            GUI.color = AlbumStyle.Theme.MutedInk;
            float dateHeight = AlbumStyle.TextHeight(record.dateLabel ?? "", area.width, GameFont.Tiny) + 6f;
            Text.Font = GameFont.Tiny;
            AlbumStyle.BodyLabel(new Rect(area.x, area.y + header, area.width, dateHeight), record.dateLabel ?? "");
            Text.Font = GameFont.Small;
            float titleHeight = AlbumPresentation.TitleHeight;
            Rect box = new Rect(area.x, area.y + header + dateHeight + 8f, area.width, Mathf.Max(1f, area.height - header - dateHeight - titleHeight - 24f));
            GUI.color = Color.white;
            Texture image = material?.mainTexture;
            if (image != null && image.height > 0) Widgets.DrawTextureFitted(box, image, 1f);
            else { GUI.color = AlbumStyle.Theme.MutedInk; AlbumStyle.BodyLabel(box, "MF_MissingFile".Translate()); }
            GUI.color = AlbumStyle.Theme.Ink;
            AlbumPresentation.CardTitle(new Rect(area.x, area.yMax - titleHeight, area.width, titleHeight), record.DisplayTitle);
            GUI.color = Color.white;
            if (Widgets.ButtonInvisible(box)) MainTabWindow_Album.Open(record);
        }
    }
}
