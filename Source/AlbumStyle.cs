using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Album pages, the photo tree and birthday preparation opt into these styles.
    /// Styles and Verse's line-height caches are restored before another window draws.</summary>
    [StaticConstructorOnStartup]
    internal static class AlbumStyle
    {
        internal sealed class ThemeSpec
        {
            internal readonly string Key, DefaultFont;
            internal readonly Color Cover, Paper, Mat, Ink, MutedInk, Accent, CoverInk;
            internal readonly bool Sketch, Dark;
            internal ThemeSpec(string key, string cover, string paper, string mat, string ink, string muted, string accent, string font, bool sketch = false)
            {
                Key = key; DefaultFont = font; Cover = Hex(cover); Paper = Hex(paper); Mat = Hex(mat);
                Ink = Hex(ink); MutedInk = Hex(muted); Accent = Hex(accent); Sketch = sketch;
                Dark = Paper.grayscale < .35f;
                CoverInk = Cover.grayscale < .4f ? Hex("#F3EADB") : Ink;
            }
            internal string Label => ("MF_Theme_" + Key).Translate();
        }

        // Palette values come from the preserved browser candidate manifests; layouts adapt to native windows.
        internal static readonly ThemeSpec[] Themes = {
            new ThemeSpec("leather", "#33261F", "#EFE4CF", "#F9F4E9", "#382C24", "#685947", "#B89355", "system"),
            new ThemeSpec("archive", "#DAD4C8", "#F5F1E7", "#FFFFFF", "#343A36", "#646D61", "#708374", "system"),
            new ThemeSpec("darkroom", "#191B1A", "#272A27", "#0E100F", "#EEE9DA", "#B5B6AA", "#B9694D", "system"),
            new ThemeSpec("field", "#414B3C", "#E6DDC3", "#F5EDDB", "#343B2F", "#636B52", "#BC894C", "system"),
            new ThemeSpec("linen", "#253847", "#EDE8DE", "#FAF7F0", "#293640", "#5C6B78", "#BB806D", "system"),
            new ThemeSpec("pencil", "#D1C8B5", "#F2EEDF", "#FCFAF3", "#49473F", "#706B5B", "#B49A61", "pencil", true),
            new ThemeSpec("ink", "#BDB19B", "#EAE1CD", "#FAF5E8", "#34464B", "#627073", "#A26D57", "pen", true),
            new ThemeSpec("crayon", "#DBCBB5", "#F4F0E6", "#FDFBF6", "#514638", "#746755", "#B17A65", "brush", true),
            new ThemeSpec("graphite", "#EFE9DC", "#FAF5E9", "#FFFDF7", "#48453E", "#565147", "#AD7863", "pencil", true),
            new ThemeSpec("watercolor", "#EAECE0", "#FCFAF2", "#FFFDF7", "#3F514D", "#53635A", "#A8796B", "light", true),
            new ThemeSpec("sepia", "#DCD0BA", "#F4ECD9", "#FBF6E9", "#63513D", "#67513D", "#A38457", "light", true)
        };
        internal static readonly string[] FontChoices = { "original", "light", "pencil", "pen", "brush", "system" };
        internal static readonly int[] Sizes = { 85, 100, 115, 130 };
        internal static ThemeSpec Theme => Themes.FirstOrDefault(t => t.Key == MomentFramesMod.Settings?.albumTheme) ?? Themes[8];
        internal static float Scale => (MomentFramesMod.Settings?.albumTextSize ?? 100) / 100f;
        internal static string EffectiveFont => MomentFramesMod.Settings?.albumFont == "original" || MomentFramesMod.Settings == null
            ? Theme.DefaultFont : MomentFramesMod.Settings.albumFont;
        private static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        private static readonly Dictionary<Font, System.Collections.Generic.HashSet<char>> glyphs = new Dictionary<Font, System.Collections.Generic.HashSet<char>>();
        private static readonly Dictionary<string, string> files = new Dictionary<string, string> {
            { "light", "MF-light.ttf" }, { "pencil", "MF-pencil.ttf" },
            { "pen", "MF-pen.ttf" }, { "brush", "MF-brush.ttf" }
        };
        private static AssetBundle fontBundle;
        private static float[] lineHeights, lineSpacing;
        private static GUIStyle[][] arrays;
        private static StyleScope current;
        private static Texture2D paper, wash;
        private static bool texturesLoaded;

        internal static Color Hex(string hex) { ColorUtility.TryParseHtmlString(hex, out Color c); return c; }
        internal static void Normalize(MomentFramesSettings s)
        {
            if (!Themes.Any(t => t.Key == s.albumTheme)) s.albumTheme = "graphite";
            if (!FontChoices.Contains(s.albumFont)) s.albumFont = "original";
            if (!Sizes.Contains(s.albumTextSize)) s.albumTextSize = 100;
        }
        internal static string FontLabel(string key) => ("MF_Font_" + key).Translate();

        internal static Font LoadFont(string key)
        {
            if (!files.TryGetValue(key, out string file)) return null;
            if (fonts.TryGetValue(key, out Font loaded)) return loaded;
            string root = LoadedModManager.GetMod<MomentFramesMod>()?.Content.RootDir;
            if (root == null) return null;
            string path = Path.Combine(root, "Fonts", file);   // the font itself comes from the bundle; only its .coverage file is read
            try
            {
                if (!File.Exists(path + ".coverage")) throw new FileNotFoundException("font coverage file missing", path + ".coverage");
                if (fontBundle == null) fontBundle = AssetBundle.LoadFromFile(Path.Combine(root, "Fonts", "momentframesfonts"));
                if (fontBundle == null) throw new InvalidOperationException("font asset bundle could not be loaded");
                loaded = fontBundle.LoadAsset<Font>("assets/momentframes/" + key + ".ttf");
                if (loaded == null || !loaded.dynamic) throw new InvalidOperationException("bundled font is missing or not dynamic");
                var nativeIndex = typeof(Font).GetProperty("GameFontIndex");
                if (nativeIndex != null)
                {
                    // 1.7's HasCharacter/GetCharacterInfo query the separate world-text backend.
                    // The UI registry assigns imported fonts indexes beyond the three default game fonts.
                    if ((int)nativeIndex.GetValue(loaded, null) < 3) throw new InvalidOperationException("bundled font was not registered by the UI backend");
                }
                else
                {
                    loaded.RequestCharactersInTexture("한ABC", 21, FontStyle.Normal);
                    if (!loaded.HasCharacter('한') || !loaded.HasCharacter('A')) throw new InvalidOperationException("font has no Korean/Latin glyphs");
                    loaded.GetCharacterInfo('한', out CharacterInfo sanity, 21, FontStyle.Normal);
                    int expectedAdvance = key == "pen" ? 13 : key == "brush" ? 14 : 17;
                    if (Mathf.Abs(sanity.advance - expectedAdvance) > 1) throw new InvalidOperationException("font rendered fallback glyphs (advance=" + sanity.advance + ")");
                }
                // Unity HasCharacter includes its implicit OS fallback. Use the original TTF's cmap to choose our explicit fallback.
                glyphs[loaded] = new System.Collections.Generic.HashSet<char>(File.ReadAllText(path + ".coverage"));
                fonts[key] = loaded;
                return loaded;
            }
            catch (Exception e)
            {
                fonts[key] = null;
                Log.Warning("[MomentFrames] " + file + " could not be loaded; using the game font. " + e.Message);
                return null;
            }
        }

        internal static IDisposable Begin(bool readable = false)
        {
            // Text's static initializer reads GUI.skin; settings load before the first OnGUI must not trigger it.
            if (arrays == null)
            {
                arrays = new[] { Text.fontStyles, Text.textFieldStyles, Text.textAreaStyles, Text.textAreaReadOnlyStyles };
                lineHeights = (float[])AccessTools.Field(typeof(Text), "lineHeights")?.GetValue(null);
                lineSpacing = (float[])AccessTools.Field(typeof(Text), "spaceBetweenLines")?.GetValue(null);
            }
            return new StyleScope(readable);
        }
        private sealed class StyleScope : IDisposable
        {
            internal readonly GUIStyle[][] Saved = arrays.Select(a => (GUIStyle[])a.Clone()).ToArray();
            private readonly float[] heights = lineHeights == null ? null : (float[])lineHeights.Clone();
            private readonly float[] spacing = lineSpacing == null ? null : (float[])lineSpacing.Clone();
            private readonly GameFont font = Text.Font;
            private readonly TextAnchor anchor = Text.Anchor;
            private readonly bool wrap = Text.WordWrap;
            private readonly Color color = GUI.color;
            private readonly Matrix4x4 matrix = GUI.matrix;
            private readonly bool enabled = GUI.enabled;
            private readonly StyleScope previous = current;
            private bool disposed;
            internal StyleScope(bool readable)
            {
                current = this;
                Text.WordWrap = true;
                Text.Anchor = TextAnchor.UpperLeft;
                try
                {
                    Font custom = readable ? null : LoadFont(EffectiveFont);
                    var original = Saved;
                    if (readable) for (var scope = previous; scope != null; scope = scope.previous) original = scope.Saved;
                    for (int a = 0; a < arrays.Length; a++)
                        for (int i = 0; i < 3; i++)
                        {
                            GUIStyle style = new GUIStyle(readable ? original[a][i] : Saved[a][i]);
                            int originalSize = style.fontSize > 0 ? style.fontSize : style.font?.fontSize ?? (i == 0 ? 12 : i == 1 ? 18 : 24);
                            style.fontSize = Mathf.RoundToInt((readable ? (i == 0 ? 14 : i == 1 ? 18 : 25) : custom == null ? originalSize : i == 0 ? 16 : i == 1 ? 21 : 29) * (readable ? Mathf.Max(1f, Scale) : Scale));
                            if (readable) { style.contentOffset = Vector2.zero; style.fontStyle = FontStyle.Normal; }
                            if (custom != null) { style.font = custom; style.contentOffset = Vector2.zero; style.fontStyle = FontStyle.Normal; }
                            arrays[a][i] = style;
                        }
                    for (int i = 0; i < 3; i++)
                    {
                        var style = arrays[0][i];
                        if (lineHeights != null) lineHeights[i] = style.CalcHeight(new GUIContent("한W"), 999f);
                        if (lineSpacing != null) lineSpacing[i] = style.CalcHeight(new GUIContent("한W\n한W"), 999f) - 2f * style.CalcHeight(new GUIContent("한W"), 999f);
                    }
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                for (int a = 0; a < arrays.Length; a++) Array.Copy(Saved[a], arrays[a], 3);
                if (heights != null) Array.Copy(heights, lineHeights, 3);
                if (spacing != null) Array.Copy(spacing, lineSpacing, 3);
                Text.Font = font; Text.Anchor = anchor; Text.WordWrap = wrap;
                GUI.color = color; GUI.matrix = matrix; GUI.enabled = enabled;
                current = previous;
            }
        }

        // Gaegu contains 2350 Korean syllables. A whole label falls back when it needs other syllables or symbols;
        // measurement and drawing share this path so a rare pawn name does not produce missing boxes or wrong scroll heights.
        internal static GUIStyle StyleFor(string text, GameFont? font = null)
        {
            int index = (int)(font ?? Text.Font);
            if (index == 0 && !Text.TinyFontSupported) index = 1;
            var style = new GUIStyle(Text.fontStyles[index]) { alignment = Text.Anchor, wordWrap = Text.WordWrap };
            if (current != null && style.font != null && text != null && glyphs.TryGetValue(style.font, out var supported)
                && text.Any(c => !char.IsWhiteSpace(c) && !supported.Contains(c)))
            {
                var original = current.Saved[0][index];
                style.font = original.font;
                style.fontStyle = original.fontStyle;
            }
            return style;
        }
        internal static float TextHeight(string text, float width, GameFont? font = null) => StyleFor(text, font).CalcHeight(new GUIContent(text ?? ""), Mathf.Max(1f, width));
        internal static void BodyLabel(Rect rect, string text) => GUI.Label(rect, text ?? "", StyleFor(text));
        internal static void HeaderLabel(Rect rect, string text)
        {
            var style = StyleFor(text, GameFont.Small); style.wordWrap = false; style.clipping = TextClipping.Clip;
            string visible = text ?? "";
            if (style.CalcSize(new GUIContent(visible)).x > rect.width)
            {
                while (visible.Length > 0 && style.CalcSize(new GUIContent(visible + "…")).x > rect.width) visible = visible.Substring(0, visible.Length - 1);
                visible += "…"; TooltipHandler.TipRegion(rect, text ?? "");
            }
            GUI.Label(rect, visible, style);
        }
        internal static void Label(Rect rect, string text, GameFont? font = null)
        {
            var style = StyleFor(text, font);
            var content = new GUIContent(text ?? "");
            int initial = style.fontSize;
            while (style.fontSize > 11 && (style.CalcHeight(content, Mathf.Max(1f, rect.width)) > rect.height + .1f
                || (!style.wordWrap && style.CalcSize(content).x > rect.width))) style.fontSize--;
            bool trimmed = style.CalcHeight(content, Mathf.Max(1f, rect.width)) > rect.height + .1f
                || (!style.wordWrap && style.CalcSize(content).x > rect.width);
            if (trimmed)
            {
                int low = 0, high = content.text.Length;
                while (low < high)
                {
                    int mid = (low + high + 1) / 2;
                    string candidate = content.text.Substring(0, mid) + "...";
                    bool fits = style.CalcHeight(new GUIContent(candidate), Mathf.Max(1f, rect.width)) <= rect.height + .1f
                        && (style.wordWrap || style.CalcSize(new GUIContent(candidate)).x <= rect.width);
                    if (fits) low = mid; else high = mid - 1;
                }
                if (low > 0 && char.IsHighSurrogate(content.text[low - 1])) low--;
                content.text = content.text.Substring(0, low) + "...";
            }
            GUI.Label(rect, content, style);
            if (style.fontSize < initial || trimmed) TooltipHandler.TipRegion(rect, text ?? "");
        }
        internal static bool Button(Rect rect, string text, bool active = true, bool preserveSize = false)
        {
            Color old = GUI.color;
            TextAnchor anchor = Text.Anchor;
            var t = Theme;
            GUI.color = Color.white;
            Color fill = t.Dark ? Color.Lerp(t.Paper, t.Accent, .3f) : Color.Lerp(t.Paper, t.Accent, .12f);
            if (!active) fill.a = .4f;
            Widgets.DrawBoxSolid(rect, fill);
            Color edge = t.Ink; edge.a = active ? .32f : .12f;
            Outline(rect, edge);
            if (active && Mouse.IsOver(rect)) { var hover = t.Accent; hover.a = .12f; Widgets.DrawBoxSolid(rect, hover); }
            GUI.color = active ? t.Ink : t.MutedInk;
            Text.Anchor = TextAnchor.MiddleCenter;
            if (preserveSize) AlbumPresentation.CardTitle(rect.ContractedBy(4f), text, TextAnchor.MiddleCenter);
            else Label(rect.ContractedBy(4f), text);
            Text.Anchor = anchor; GUI.color = old;
            return active && Widgets.ButtonInvisible(rect);
        }
        private static void LoadTextures()
        {
            if (texturesLoaded) return;
            texturesLoaded = true;
            paper = ContentFinder<Texture2D>.Get("UI/MomentFrames/Themes/Paper", false);
            wash = ContentFinder<Texture2D>.Get("UI/MomentFrames/Themes/Wash", false);
        }
        internal static void DrawCover(Rect rect, float opacity = 1f)
        {
            Color old = GUI.color; GUI.color = Color.white;
            var t = Theme;
            if (t.Sketch) { DrawPaper(rect, opacity: opacity); GUI.color = old; return; }
            var fill = t.Cover; fill.a = opacity; Widgets.DrawBoxSolid(rect, fill);
            if (t.Key == "leather") { GUI.color = new Color(1f, 1f, 1f, .34f * opacity); GUI.DrawTexture(rect, MFIcons.AlbumLeather); }
            GUI.color = old;
        }
        internal static void DrawPaper(Rect rect, bool left = false, float opacity = 1f)
        {
            LoadTextures();
            Color old = GUI.color; GUI.color = Color.white;
            var t = Theme;
            Color fill = t.Paper; fill.a = opacity; Widgets.DrawBoxSolid(rect, fill);
            Texture2D grain = t.Sketch ? paper : MFIcons.AlbumPaper;
            if (grain != null && !t.Dark)
            {
                GUI.color = new Color(1f, 1f, 1f, (t.Sketch ? .28f : .12f) * opacity);
                GUI.DrawTexture(rect, grain, ScaleMode.StretchToFill);
            }
            GUI.color = Color.white;
            var edge = t.Ink; edge.a = .25f * opacity;
            Outline(rect.ContractedBy(4f), edge);
            if (t.Sketch) { edge.a *= .45f; Outline(new Rect(rect.x + 6f, rect.y + 3f, rect.width - 11f, rect.height - 8f), edge); }
            GUI.color = old;
        }
        internal static void DrawWash(Rect rect, float opacity = 1f)
        {
            if (!Theme.Sketch) return;
            LoadTextures();
            Color old = GUI.color;
            GUI.color = new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, .28f * opacity);
            if (wash != null) GUI.DrawTexture(rect, wash, ScaleMode.StretchToFill);
            GUI.color = old;
        }
        private static void Outline(Rect r, Color color)
        {
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), color);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), color);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 1f, r.height), color);
            Widgets.DrawBoxSolid(new Rect(r.xMax - 1f, r.y, 1f, r.height), color);
        }
    }
}
