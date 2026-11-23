using UnityEngine;
using Verse;

namespace MomentFrames
{
    [StaticConstructorOnStartup]
    internal static class AlbumBridgeUI
    {
        // Use paper input on Unity, and readable light ink on 1.7's native dark edit surface.
        private static Texture2D background;
        private static readonly Color Ink = new Color(.16f, .15f, .13f);
        internal static Color PlaceholderInk => RuntimeAudio.Waylith ? new Color(.82f,.80f,.75f) : AlbumStyle.Theme.MutedInk;
        internal static string TextArea(Rect rect, string value, string controlName)
            => Input(rect, value, controlName, true);
        internal static string TextField(Rect rect, string value, string controlName)
            => Input(rect, value, controlName, false);
        internal static void InputHint(Rect rect, string text)
        {
            var style = AlbumStyle.StyleFor(text, GameFont.Small);
            style.wordWrap = false; style.alignment = TextAnchor.MiddleLeft;
            style.normal.textColor = RuntimeAudio.Waylith ? new Color(.82f, .81f, .77f) : new Color(.36f, .34f, .29f);
            Color previous = GUI.color;
            try { GUI.color = Color.white; GUI.Label(rect, text, style); }
            finally { GUI.color = previous; }
            TooltipHandler.TipRegion(rect, text);
        }
        private static string Input(Rect rect, string value, string controlName, bool multiline)
        {
            if (background == null) background = Solid(new Color(.995f, .984f, .948f));
            var style = AlbumStyle.StyleFor(value ?? "", GameFont.Small);
            style.wordWrap = multiline; style.richText = false; style.alignment = multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;
            style.padding = new RectOffset(12, 12, multiline ? 10 : 3, multiline ? 10 : 3);
            // 1.7 keeps its native dark edit surface instead of this style's paper texture.
            Color inputInk = RuntimeAudio.Waylith ? new Color(.97f, .96f, .92f) : Ink;
            foreach (var state in new[] { style.normal, style.hover, style.focused, style.active, style.onNormal, style.onHover, style.onFocused, style.onActive })
            { state.background = background; state.textColor = inputInk; }
            Color color = GUI.color, selection = GUI.skin.settings.selectionColor, cursor = GUI.skin.settings.cursorColor;
            bool enabled = GUI.enabled;
            try
            {
                GUI.color = Color.white; GUI.skin.settings.selectionColor = new Color(.34f, .53f, .72f, .5f); GUI.skin.settings.cursorColor = inputInk;
                Widgets.DrawBoxSolid(rect.ExpandedBy(1f), GUI.GetNameOfFocusedControl() == controlName ? new Color(.25f, .45f, .66f) : new Color(.50f, .45f, .36f));
                GUI.SetNextControlName(controlName);
                return multiline ? GUI.TextArea(rect, value ?? "", style) : GUI.TextField(rect, value ?? "", style);
            }
            finally { GUI.color = color; GUI.enabled = enabled; GUI.skin.settings.selectionColor = selection; GUI.skin.settings.cursorColor = cursor; }
        }
        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color); texture.Apply(false, true); return texture;
        }
    }

    /// <summary>Settlement prints use a screen-sized preview; opening the photo still uses its untouched full original.</summary>
    internal sealed class SettlementPhotoBinding
    {
        private string path;
        private int retryAt;
        internal Material Material { get; private set; }
        internal void Bind(PhotoRecord record)
        {
            var store = PhotoStore.Instance;
            string original = record?.HasFile == true ? store?.FilePath(record) : null;
            string desired = original == null ? null : PhotoPreviews.DisplayPath(original, store.ThumbPath(record));
            if (desired == path && (Material != null && PhotoTextureCache.Owns(path, Material) || Time.frameCount < retryAt)) return;
            Unbind(); path = desired; Material = PhotoTextureCache.Acquire(path);
            if (Material == null) retryAt = Time.frameCount + 120;
        }
        internal void Unbind()
        {
            if (PhotoTextureCache.Owns(path, Material)) PhotoTextureCache.Release(path);
            path = null; Material = null; retryAt = 0;
        }
    }
}
