using System.Globalization;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal sealed class Dialog_ProjectionSize : Window
    {
        private readonly Building_PhotoProjector projector;
        private string widthText, heightText;
        private Vector2 scroll;
        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(620f, UI.screenWidth - 40f), Mathf.Min(540f, UI.screenHeight - 40f));
        internal Dialog_ProjectionSize(Building_PhotoProjector projector)
        {
            this.projector = projector;
            projector.ProjectionBounds(out float width, out float height);
            widthText = width.ToString("0.##", CultureInfo.InvariantCulture); heightText = height.ToString("0.##", CultureInfo.InvariantCulture);
            doWindowBackground = false; doCloseX = false; forcePause = false; absorbInputAroundWindow = true;
        }
        internal bool ReadDimensions(out float width, out float height)
        {
            width = height = 0f;
            return float.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out width)
                && float.TryParse(heightText, NumberStyles.Float, CultureInfo.InvariantCulture, out height)
                && ProjectionGeometry.Custom(width, height);
        }
        internal bool TryApply()
        {
            if (!ReadDimensions(out float width, out float height) || projector == null || !projector.SetCustomProjectionSize(width, height)) return false;
            Close(); return true;
        }
        public override void DoWindowContents(Rect rect)
        {
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(rect); Rect page = rect.ContractedBy(22f);
                float row = SharedPhotoUI.ControlHeight;
                SharedPhotoUI.Heading(new Rect(page.x, page.y, page.width - 48f, row), "MF_ProjectionCustomTitle".Translate());
                if (SharedPhotoUI.Button(new Rect(page.xMax - 42f, page.y, 42f, row), "×")) Close();
                var body = new Rect(page.x, page.y + row + 18f, page.width, page.height - row * 2f - 30f);
                float contentWidth = body.width - 18f, fieldWidth = (contentWidth - 24f) / 2f;
                string help = "MF_ProjectionCustomHelp".Translate(ProjectionGeometry.Minimum.ToString("0.#", CultureInfo.InvariantCulture), ProjectionGeometry.Maximum.ToString("0.#", CultureInfo.InvariantCulture));
                bool valid = ReadDimensions(out float width, out float height);
                bool fits = valid && projector != null && projector.CanProjectCustom(width, height);
                string status = !valid ? "MF_ProjectionInvalid" : !fits ? "MF_ProjectionOffMap" : "MF_ProjectionCustomReady";
                float helpHeight = AlbumStyle.TextHeight(help, contentWidth), statusHeight = AlbumStyle.TextHeight(status.Translate(), contentWidth);
                var content = new Rect(0f, 0f, contentWidth, row * 2f + 22f + helpHeight + statusHeight + 8f);
                Widgets.BeginScrollView(body, ref scroll, content);
                try
                {
                    SharedPhotoUI.Label(new Rect(0f, 0f, fieldWidth, row), "MF_ProjectionWidth".Translate());
                    SharedPhotoUI.Label(new Rect(fieldWidth + 24f, 0f, fieldWidth, row), "MF_ProjectionHeight".Translate());
                    widthText = AlbumBridgeUI.TextField(new Rect(0f, row, fieldWidth, row), widthText, "MF_ProjectionWidthInput");
                    heightText = AlbumBridgeUI.TextField(new Rect(fieldWidth + 24f, row, fieldWidth, row), heightText, "MF_ProjectionHeightInput");
                    float y = row * 2f + 12f;
                    SharedPhotoUI.Label(new Rect(0f, y, contentWidth, helpHeight), help, true); y += helpHeight + 10f;
                    SharedPhotoUI.Label(new Rect(0f, y, contentWidth, statusHeight + 8f), status.Translate(), fits);
                }
                finally { Widgets.EndScrollView(); }
                float half = (page.width - 10f) / 2f;
                if (SharedPhotoUI.Button(new Rect(page.x, page.yMax - row, half, row), "CancelButton".Translate())) Close();
                if (SharedPhotoUI.Button(new Rect(page.x + half + 10f, page.yMax - row, half, row), "MF_ProjectionApply".Translate(), fits)) TryApply();
            }
        }
    }
}
