using System;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>The album's existing display controls; no new settings or default values.</summary>
    internal sealed class Dialog_AlbumScreen : Window
    {
        private readonly Action reset, save;
        internal Dialog_AlbumScreen(Action reset, Action save)
        {
            this.reset = reset; this.save = save;
            doCloseX = false; closeOnClickedOutside = true; absorbInputAroundWindow = true;
            forcePause = false; doWindowBackground = false;
        }
        public override Vector2 InitialSize => new Vector2(510f, 290f);
        public override void DoWindowContents(Rect rect)
        {
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(rect);
                Rect inner = rect.ContractedBy(20f);
                GUI.color = AlbumStyle.Theme.Ink;
                Text.Font = GameFont.Medium;
                AlbumStyle.BodyLabel(new Rect(inner.x, inner.y, inner.width - 52f, 48f), "MF_UI12_Screen".Translate());
                if (AlbumStyle.Button(new Rect(inner.xMax - 42f, inner.y, 42f, 42f), "×", preserveSize: true)) Close();
                Text.Font = GameFont.Small;
                var settings = MomentFramesMod.Settings;
                if (settings != null)
                {
                    AlbumStyle.HeaderLabel(new Rect(inner.x, inner.y + 62f, inner.width, 40f),
                        "MF_AlbumOpacity".Translate() + "  " + Mathf.RoundToInt(settings.albumOpacity * 100f) + "%");
                    GUI.color = Color.white;
                    settings.albumOpacity = Widgets.HorizontalSlider(new Rect(inner.x, inner.y + 108f, inner.width, 24f),
                        settings.albumOpacity, MainTabWindow_Album.MinOpacity, 1f, false, null, null, null, .05f);
                }
                Rect resetRect = new Rect(inner.x, inner.y + 158f, inner.width, 48f);
                if (AlbumStyle.Button(resetRect, AlbumPresentation.Title("MF_UI12_Reset".Translate()), preserveSize: true)) reset?.Invoke();
                TooltipHandler.TipRegion(resetRect, "MF_AlbumReset".Translate());
            }
        }
        public override void PostClose() { base.PostClose(); save?.Invoke(); }
    }
}
