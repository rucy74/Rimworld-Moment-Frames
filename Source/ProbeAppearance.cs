using System;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool appearanceMode, appearanceMeasured;
        private static int appearanceShot, appearanceCombinations;
        private static readonly string[] appearanceNames = { "graphite-gallery", "graphite-chronicle", "graphite-year", "leather-gallery", "darkroom-gallery", "brush-large-chronicle", "pen-large-year", "appearance-settings" };

        private static void AppearanceCheck(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("appearance: " + message);
            checks.Add("PASS: appearance — " + message);
        }
        private static void AppearanceStep()
        {
            var s = MomentFramesMod.Settings;
            switch (phase)
            {
                case 0:
                    FxSet(false); Setup(); Wait(1f); phase = 1; return;
                case 1:
                    testRec = PhotoStore.Instance.Request(map, center, "group", "probe-appearance", "정착지에서 함께 보낸 날", "함께 찍은 사진을 연대기의 한 페이지에 남겼다.\n이 기록은 글꼴과 크기를 비교하는 시험용 사연이다.", new[] { p1, p2 });
                    if (testRec == null) throw new InvalidOperationException("appearance fixture capture rejected");
                    phase = 2; return;
                case 2:
                    if (!testRec.HasFile)
                    {
                        if (!PhotoStore.Instance.Records.Contains(testRec)) throw new InvalidOperationException("appearance fixture capture was dropped; check render window visibility");
                        return;
                    }
                    var life = new LifeEvent { kind = LifeEvents.Joined, year = testRec.year - 1, ticksAbs = testRec.ticksAbs - 60000,
                        pawnIds = new System.Collections.Generic.List<string> { p1.GetUniqueLoadID() }, names = new System.Collections.Generic.List<string> { p1.LabelShort }, dateLabel = "5499년 가을 2일" };
                    life.ChooseNote(); PhotoStore.Instance.AddLife(life);
                    Find.WindowStack.Add(new Window_AppearanceMeasure()); phase = 3; return;
                case 3:
                    if (!appearanceMeasured) return;
                    Find.WindowStack.TryRemove(typeof(Window_AppearanceMeasure), false);
                    AppearanceOpen(); Wait(1f); phase = 4; return;
                case 4:
                    CloseLog(); Shot(appearanceNames[appearanceShot] + ".png"); Wait(.8f); phase = 5; return;
                case 5:
                    Require(appearanceNames[appearanceShot] + ".png");
                    if (++appearanceShot < appearanceNames.Length) { AppearanceOpen(); Wait(.8f); phase = 4; return; }
                    s.albumTheme = "graphite"; s.albumFont = "original"; s.albumTextSize = 100;
                    File.WriteAllText(Path.Combine(dir, "appearance-result.json"), "{\"ok\":true,\"combinations\":" + appearanceCombinations + ",\"screenshots\":8,\"checks\":[" + string.Join(",", checks.Select(Json))
                        + "],\"limits\":\"Isolated Core/Harmony quick-test profile; staged caption/life fixture. Natural play and broad mod compatibility are not tested.\"}");
                    active = false; Application.Quit(); return;
            }
        }
        private static void AppearanceOpen()
        {
            Find.WindowStack.TryRemove(typeof(Dialog_MFYearPhotos), false);
            Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), false);
            Find.MainTabsRoot.EscapeCurrentTab(false);
            Find.Selector.ClearSelection();
            var s = MomentFramesMod.Settings;
            s.albumTheme = appearanceShot == 3 ? "leather" : appearanceShot == 4 ? "darkroom" : "graphite";
            s.albumFont = appearanceShot == 5 ? "brush" : appearanceShot == 6 ? "pen" : "original";
            s.albumTextSize = appearanceShot == 5 || appearanceShot == 6 ? 130 : 100;
            if (appearanceShot == 1 || appearanceShot == 5)
            {
                Find.Selector.Select(p1, false);
                InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Chronicle));
                var tab = new ITab_Pawn_Chronicle();
                AccessTools.Method(typeof(ITab_Pawn_Chronicle), "UpdateSize").Invoke(tab, null);
                var tabRect = (Rect)AccessTools.Property(typeof(InspectTabBase), "TabRect").GetValue(tab, null);
                AppearanceCheck(tabRect.y >= 7.9f && tabRect.xMax <= UI.screenWidth && tabRect.yMax <= UI.screenHeight,
                    "actual pawn chronicle rectangle remains on screen at " + UI.screenWidth + "x" + UI.screenHeight + "/" + s.albumTextSize);
            }
            else if (appearanceShot == 2 || appearanceShot == 6)
            {
                var letter = DesignLetter(testRec.year); letter.year = testRec.year;
                Find.WindowStack.Add(new Dialog_MFYearPhotos(letter));
            }
            else if (appearanceShot == 7) Find.WindowStack.Add(new Dialog_ModSettings(LoadedModManager.GetMod<MomentFramesMod>()));
            else MainTabWindow_Album.Open(testRec);
        }
        private class Window_AppearanceMeasure : Window
        {
            internal Window_AppearanceMeasure() { doWindowBackground = false; closeOnAccept = false; closeOnCancel = false; }
            public override Vector2 InitialSize => new Vector2(800f, 540f);
            public override void DoWindowContents(Rect rect)
            {
                if (appearanceMeasured || !active) return;
                try { MeasureAppearance(); appearanceMeasured = true; }
                catch (Exception e) { Fail(e); }
            }
        }
        private static void MeasureAppearance()
        {
            var s = MomentFramesMod.Settings;
            AppearanceCheck(UI.screenWidth >= 1000 && UI.screenHeight >= 600, "native OnGUI screen is at least 1000x600, not a hidden 0x0 capture");
            foreach (string key in new[] { "light", "pencil", "pen", "brush" })
            {
                Font font = AlbumStyle.LoadFont(key);
                AppearanceCheck(font != null && (VersionControl.CurrentMinor >= 7 || font.HasCharacter('한') && font.HasCharacter('A')), key + " loads the bundled TTF and Korean/Latin glyphs");
                font.RequestCharactersInTexture("A한기록", 21, FontStyle.Normal);
                string metric = "";
                foreach (char ch in "A한기록") { font.GetCharacterInfo(ch, out CharacterInfo ci, 21, FontStyle.Normal); metric += ch + ":" + ci.advance + ","; }
                checks.Add("INFO: font metrics " + key + " at 21 — " + metric);
                font.GetCharacterInfo('한', out CharacterInfo glyph, 21, FontStyle.Normal);
                AppearanceCheck(Mathf.Abs(glyph.advance - (key == "pen" ? 13 : key == "brush" ? 14 : 17)) <= 1,
                    key + " glyph advance matches the original TTF rather than OS fallback");
            }
            SettingsRoundTrip();
            ScopeExceptionRoundTrip();
            GUIStyle[][] all = { Text.fontStyles, Text.textFieldStyles, Text.textAreaStyles, Text.textAreaReadOnlyStyles };
            GUIStyle[][] original = all.Select(a => (GUIStyle[])a.Clone()).ToArray();
            var heights = Enumerable.Range(0, 3).Select(i => Text.LineHeightOf((GameFont)i)).ToArray();
            Color color = GUI.color; var anchor = Text.Anchor; var gameFont = Text.Font; bool wrap = Text.WordWrap;
            foreach (var theme in AlbumStyle.Themes)
                foreach (string font in AlbumStyle.FontChoices)
                    foreach (int size in AlbumStyle.Sizes)
                    {
                        s.albumTheme = theme.Key; s.albumFont = font; s.albumTextSize = size;
                        using (AlbumStyle.Begin())
                        {
                            Text.Font = GameFont.Small;
                            AppearanceCheck(AlbumStyle.Theme.Key == theme.Key && Mathf.Abs(AlbumStyle.Scale - size / 100f) < .00001f
                                && !ReferenceEquals(Text.fontStyles[1], original[0][1]), theme.Key + "/" + font + "/" + size + " dispatches through the native style scope (actual=" + AlbumStyle.Theme.Key + "/" + AlbumStyle.Scale + ", clone=" + !ReferenceEquals(Text.fontStyles[1], original[0][1]) + ")");
                            string effective = font == "original" ? theme.DefaultFont : font;
                            var expectedFont = effective == "system" ? original[0][1].font : AlbumStyle.LoadFont(effective);
                            AppearanceCheck(Text.fontStyles[1].font == expectedFont && (effective == "system" || Text.fontStyles[1].fontSize == Mathf.RoundToInt(21f * size / 100f)),
                                "chosen font and size are used by native drawing for " + theme.Key + "/" + font + "/" + size);
                            var style = AlbumStyle.StyleFor("정착지의 기억");
                            float h = AlbumStyle.TextHeight("정착지의 기억을 여러 줄에 기록한다.\n함께한 사람들의 이름도 남긴다.", 240f);
                            AppearanceCheck(h >= style.CalcHeight(new GUIContent("정착지의 기억"), 240f) && h < 400f, "body measurement is usable for " + theme.Key + "/" + font + "/" + size);
                            MainTabWindow_Album.GalleryLayout(new Rect(0, 0, 956, 720), out Rect image, out Rect info, out Rect rail);
                            MainTabWindow_Album.GalleryInfoLayout(info, out Rect title, out Rect meta, out Rect story, out Rect actions);
                            AppearanceCheck(image.height > 100 && title.yMax <= meta.yMin && meta.yMax <= story.yMin && story.yMax <= actions.yMin,
                                "gallery image/story/actions fit " + theme.Key + "/" + font + "/" + size);
                            AppearanceCheck(ITab_Pawn_Chronicle.PhotoRowHeight(testRec, 536) >= 150, "chronicle measures a full photo/note row " + theme.Key + "/" + font + "/" + size);
                            var page = Dialog_MFYearPhotos.LayoutFor(new Rect(0, 0, 964, 604), 3, AlbumStyle.Scale);
                            AppearanceCheck(page.Cards.All(r => ContainsRect(page.PhotoArea, r)) && page.Hint.yMax < page.AlbumButton.yMin
                                && page.AlbumButton.xMax < page.NightButton.xMin && page.NightButton.xMax < page.CloseButton.xMin,
                                "three-print year review and actions fit " + theme.Key + "/" + font + "/" + size);
                            if (font == "pencil") AppearanceCheck(AlbumStyle.StyleFor("쀍힣 ·↺").font == original[0][1].font, "rare Korean and symbols fall back using the measured style");
                            appearanceCombinations++;
                        }
                        AppearanceCheck(all.Select((a, i) => a.Select((st, j) => ReferenceEquals(st, original[i][j])).All(x => x)).All(x => x)
                            && heights.SequenceEqual(Enumerable.Range(0, 3).Select(i => Text.LineHeightOf((GameFont)i)))
                            && GUI.color == color && Text.Anchor == anchor && Text.Font == gameFont && Text.WordWrap == wrap,
                            "all GUIStyles/line heights/text state restore after " + theme.Key + "/" + font + "/" + size);
                    }
            s.albumTheme = "graphite"; s.albumFont = "original"; s.albumTextSize = 100;
        }
        private static void SettingsRoundTrip()
        {
            string path = Path.Combine(dir, "appearance-settings.xml");
            MomentFramesSettings fixture = new MomentFramesSettings { albumTheme = "darkroom", albumFont = "brush", albumTextSize = 130,
                albumOpacity = .45f, albumX = 23f, albumY = 34f, albumW = 1000f, albumH = 600f, spanCells = 17 };
            Scribe.saver.InitSaving(path, "Appearance"); Scribe_Deep.Look(ref fixture, "settings"); Scribe.saver.FinalizeSaving();
            fixture = null;
            Scribe.loader.InitLoading(path); Scribe_Deep.Look(ref fixture, "settings"); Scribe.loader.FinalizeLoading();
            AppearanceCheck(fixture.albumTheme == "darkroom" && fixture.albumFont == "brush" && fixture.albumTextSize == 130
                && fixture.albumOpacity == .45f && fixture.albumX == 23f && fixture.albumY == 34f && fixture.spanCells == 17,
                "real Scribe round trip retains independent theme/font/size plus old placement/opacity/capture settings");
            var legacy = new XmlDocument(); legacy.Load(path);
            foreach (string key in new[] { "albumTheme", "albumFont", "albumTextSize" })
            {
                var node = legacy.SelectSingleNode("/Appearance/settings/" + key);
                AppearanceCheck(node != null, "saved XML contains " + key);
                node.ParentNode.RemoveChild(node);
            }
            string legacyPath = Path.Combine(dir, "appearance-legacy-settings.xml"); legacy.Save(legacyPath);
            fixture = null;
            Scribe.loader.InitLoading(legacyPath); Scribe_Deep.Look(ref fixture, "settings"); Scribe.loader.FinalizeLoading();
            AppearanceCheck(fixture.albumTheme == "graphite" && fixture.albumFont == "original" && fixture.albumTextSize == 100
                && fixture.albumOpacity == .45f && fixture.albumX == 23f && fixture.albumY == 34f && fixture.spanCells == 17,
                "old settings XML without appearance fields loads defaults and retains prior values");
            fixture.albumTheme = "missing"; fixture.albumFont = null; fixture.albumTextSize = 300;
            AlbumStyle.Normalize(fixture);
            AppearanceCheck(fixture.albumTheme == "graphite" && fixture.albumFont == "original" && fixture.albumTextSize == 100,
                "invalid or missing appearance values migrate to usable defaults");
        }
        private static void ScopeExceptionRoundTrip()
        {
            var oldAnchor = Text.Anchor; bool oldWrap = Text.WordWrap;
            var style = Text.fontStyles[1]; float height = Text.LineHeightOf(GameFont.Small);
            try
            {
                Text.Anchor = TextAnchor.LowerRight; Text.WordWrap = false;
                try
                {
                    using (AlbumStyle.Begin())
                    {
                        AppearanceCheck(Text.WordWrap && Text.Anchor == TextAnchor.UpperLeft,
                            "scope provides predictable drawing state after a non-wrapping caller");
                        var outer = Text.fontStyles[1];
                        try { using (AlbumStyle.Begin()) { throw new InvalidOperationException("intentional appearance probe exception"); } }
                        catch (InvalidOperationException e) { if (e.Message != "intentional appearance probe exception") throw; }
                        AppearanceCheck(ReferenceEquals(Text.fontStyles[1], outer), "nested exceptional scope restores the outer styles");
                        throw new InvalidOperationException("intentional appearance probe exception");
                    }
                }
                catch (InvalidOperationException e) { if (e.Message != "intentional appearance probe exception") throw; }
                AppearanceCheck(ReferenceEquals(Text.fontStyles[1], style) && Text.LineHeightOf(GameFont.Small) == height
                    && !Text.WordWrap && Text.Anchor == TextAnchor.LowerRight, "exceptional scope restores caller styles, cached heights and text state");
            }
            finally { Text.Anchor = oldAnchor; Text.WordWrap = oldWrap; }
        }
    }
}
