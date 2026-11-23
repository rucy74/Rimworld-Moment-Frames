using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>The saved annual letter, opened as a page of three prints. Nothing new is saved: old letters find their year's photos
    /// when opened, and missing or sparse years remain readable. The normal letter choices are retained on Letter_MFYear.</summary>
    public class Dialog_MFYearPhotos : Window
    {
        private readonly Letter_MFYear letter;
        private PhotoStore cachedStore;
        private int cachedVersion = -1;
        private List<PhotoRecord> photos = new List<PhotoRecord>();
        private List<PhotoRecord> highlights = new List<PhotoRecord>();
        private Vector2 scroll;
        internal DisplayLayout LastDisplayLayout;
        internal bool Rendered;

        public override Vector2 InitialSize => SizeFor(new Vector2(UI.screenWidth, UI.screenHeight), AlbumStyle.Scale);
        internal static Vector2 SizeFor(Vector2 screen, float scale) => new Vector2(
            Mathf.Min(1000f, Mathf.Max(1f, screen.x - 40f)),
            Mathf.Min(640f + ExtraHeight(scale), Mathf.Max(1f, screen.y - 40f)));

        public Dialog_MFYearPhotos(Letter_MFYear letter)
        {
            this.letter = letter;
            doCloseX = true;
            absorbInputAroundWindow = true;
            forcePause = true;
            closeOnAccept = false;
            Refresh();
        }

        internal int Year => letter.year;
        internal IReadOnlyList<PhotoRecord> RepresentativePhotos { get { Refresh(); return highlights; } }
        internal int YearPhotoCount { get { Refresh(); return photos.Count; } }

        internal static List<PhotoRecord> PhotosFor(IEnumerable<PhotoRecord> records, int year) =>
            (records ?? Enumerable.Empty<PhotoRecord>())
                .Where(r => r != null && r.HasFile && r.year == year && !Kinds.PersonalOnly(r.kind) && r.kind != Kinds.Relic)
                .Distinct().OrderBy(r => r.ticksAbs).ThenBy(r => r.id, StringComparer.Ordinal).ToList();

        /// <summary>Prefer milestones, then a kind and people not yet represented, then a moment farther from the selected photos.
        /// Stable ties use the event time and id. No random state or collection order changes the page.</summary>
        internal static List<PhotoRecord> SelectHighlights(IEnumerable<PhotoRecord> records, int year)
        {
            var candidates = PhotosFor(records, year).Where(r => !Kinds.IsSurvey(r.kind)).ToList();
            var selected = new List<PhotoRecord>();
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            var people = new HashSet<string>(StringComparer.Ordinal);
            while (selected.Count < 3 && candidates.Count > 0)
            {
                var pick = candidates.OrderByDescending(r => Kinds.IsMilestone(r.kind))
                    .ThenByDescending(r => !kinds.Contains(r.kind ?? ""))
                    .ThenByDescending(r => PeopleOf(r).Count(p => !people.Contains(p)))
                    .ThenByDescending(r => selected.Count == 0 ? 0L : selected.Min(s => Math.Abs((long)r.ticksAbs - s.ticksAbs)))
                    .ThenBy(r => r.ticksAbs).ThenBy(r => r.id, StringComparer.Ordinal).First();
                selected.Add(pick);
                candidates.Remove(pick);
                kinds.Add(pick.kind ?? "");
                foreach (string person in PeopleOf(pick)) people.Add(person);
            }
            return selected.OrderBy(r => r.ticksAbs).ThenBy(r => r.id, StringComparer.Ordinal).ToList();
        }

        private static IEnumerable<string> PeopleOf(PhotoRecord rec)
        {
            var ids = rec.peopleIds;
            var names = rec.people;
            return ids != null && ids.Count > 0
                ? ids.Where(p => !p.NullOrEmpty()).Select(p => "id:" + p).Distinct()
                : (names ?? Enumerable.Empty<string>()).Where(p => !p.NullOrEmpty()).Select(p => "name:" + p).Distinct();
        }

        private void Refresh()
        {
            var store = PhotoStore.Instance;
            int version = store?.Version ?? -1;
            if (store == cachedStore && version == cachedVersion) return;
            cachedStore = store;
            cachedVersion = version;
            photos = PhotosFor(store?.Records, Year);
            highlights = SelectHighlights(photos, Year);
        }

        internal struct PageLayout
        {
            public Rect Paper, Kicker, Title, Summary, PhotoArea, Hint, AlbumButton, NightButton, CloseButton;
            public Rect[] Cards;
            public float PrintTitleHeight, PrintDateHeight;
        }

        internal struct DisplayLayout
        {
            public PageLayout Page;
            public Rect BodyViewport;
            public float BodyHeight, BodyWidth;
            public bool Scrolls => BodyHeight > BodyViewport.height + .1f;
        }

        /// <summary>One layout used by drawing and the probe. One or two surviving photos keep their print size and sit in the centre.</summary>
        internal static PageLayout LayoutFor(Rect area, int photoCount) => LayoutFor(area, photoCount, 1f);

        private static float ExtraHeight(float scale) => Mathf.Max(0f, scale - 1f) * 352f;

        internal static PageLayout LayoutFor(Rect area, int photoCount, float scale)
        {
            // A short display keeps the original print area; labels fit their bounds instead of taking height from the photographs.
            float extra = Mathf.Min(Mathf.Max(0f, scale - 1f), Mathf.Max(0f, area.height - 604f) / 352f);
            Rect paper = area.ContractedBy(6f);
            float left = paper.x + 22f, width = paper.width - 44f;
            float kickerExtra = 24f * extra, titleExtra = 48f * extra, summaryExtra = 36f * extra;
            float buttonHeight = 38f + 40f * extra, hintHeight = 36f + 100f * extra;
            float buttonY = paper.yMax - 24f - buttonHeight;
            float photoY = paper.y + 131f + kickerExtra + titleExtra + summaryExtra;
            float hintY = buttonY - 13f - hintHeight;
            var page = new PageLayout
            {
                Paper = paper,
                Kicker = new Rect(left, paper.y + 17f, width - 20f, 20f + kickerExtra),
                Title = new Rect(left, paper.y + 38f + kickerExtra, width - 20f, 40f + titleExtra),
                Summary = new Rect(left, paper.y + 83f + kickerExtra + titleExtra, width, 30f + summaryExtra),
                PhotoArea = new Rect(left, photoY, width, Mathf.Max(40f, hintY - photoY - 20f)),
                Hint = new Rect(left, hintY, width, hintHeight),
                CloseButton = new Rect(left + width - 104f, buttonY, 104f, buttonHeight),
                PrintTitleHeight = 42f + 80f * extra,
                PrintDateHeight = 18f + 24f * extra
            };
            float actionW = (width - 104f - 24f) / 2f;
            page.AlbumButton = new Rect(left, buttonY, actionW, buttonHeight);
            page.NightButton = new Rect(page.AlbumButton.xMax + 12f, buttonY, actionW, buttonHeight);
            int count = Mathf.Clamp(photoCount, 0, 3);
            page.Cards = new Rect[count];
            if (count == 0) return page;
            const float gap = 20f;
            float cardW = Mathf.Min(282f, (width - gap * 2f) / 3f);
            float cardH = Mathf.Min(292f + 104f * extra, page.PhotoArea.height - 10f);
            float start = page.PhotoArea.center.x - (cardW * count + gap * (count - 1)) / 2f;
            for (int i = 0; i < count; i++)
                page.Cards[i] = new Rect(start + i * (cardW + gap), page.PhotoArea.center.y - cardH / 2f, cardW, cardH);
            return page;
        }

        internal static Rect PrintImageFor(Rect card, PageLayout page) => new Rect(card.x + 10f, card.y + 12f,
            card.width - 20f, card.height - 21f - page.PrintTitleHeight - page.PrintDateHeight);

        internal static DisplayLayout DisplayFor(Rect area, int photoCount, float scale)
        {
            var footer = LayoutFor(area, photoCount, scale);
            float buttonHeight = Mathf.Max(38f + 40f * Mathf.Max(0f, scale - 1f),
                AlbumStyle.TextHeight("MF_DesignYear_Album".Translate(), footer.AlbumButton.width - 12f) + 12f,
                AlbumStyle.TextHeight("MF_DesignYear_Night".Translate(), footer.NightButton.width - 12f) + 12f,
                AlbumStyle.TextHeight("CloseButton".Translate(), footer.CloseButton.width - 12f) + 12f);
            float buttonY = footer.Paper.yMax - 24f - buttonHeight;
            footer.AlbumButton.y = footer.NightButton.y = footer.CloseButton.y = buttonY;
            footer.AlbumButton.height = footer.NightButton.height = footer.CloseButton.height = buttonHeight;
            var viewport = new Rect(area.x, area.y, area.width, Mathf.Max(1f, buttonY - area.y - 13f));
            float naturalHeight = Mathf.Max(area.height, 604f + ExtraHeight(scale));
            var page = LayoutFor(new Rect(0f, 0f, area.width, naturalHeight), photoCount, scale);
            float width = area.width;
            if (page.Hint.yMax > viewport.height)
            {
                width -= 16f;
                page = LayoutFor(new Rect(0f, 0f, width, naturalHeight), photoCount, scale);
            }
            page.AlbumButton = footer.AlbumButton; page.NightButton = footer.NightButton; page.CloseButton = footer.CloseButton;
            return new DisplayLayout { Page = page, BodyViewport = viewport, BodyHeight = page.Hint.yMax, BodyWidth = width };
        }

        public override void DoWindowContents(Rect inRect)
        {
            Refresh();
            using (AlbumStyle.Begin())
            {
                var display = DisplayFor(inRect, highlights.Count, AlbumStyle.Scale);
                var page = display.Page;
                LastDisplayLayout = display;
                Rendered = true;
                var theme = AlbumStyle.Theme;
                Color oldColor = GUI.color;
                GameFont oldFont = Text.Font;
                TextAnchor oldAnchor = Text.Anchor;
                try
                {
                    GUI.color = Color.white;
                    AlbumStyle.DrawCover(inRect);
                    Rect paper = inRect.ContractedBy(6f);
                    Widgets.DrawBoxSolid(new Rect(paper.x + 3f, paper.y + 4f, paper.width, paper.height), new Color(0f, 0f, 0f, 0.25f));
                    AlbumStyle.DrawPaper(paper);
                    scroll.y = Mathf.Clamp(scroll.y, 0f, Mathf.Max(0f, display.BodyHeight - display.BodyViewport.height));
                    Widgets.BeginScrollView(display.BodyViewport, ref scroll, new Rect(0f, 0f, display.BodyWidth, display.BodyHeight));
                    AlbumStyle.DrawWash(new Rect(page.Title.x, page.Title.y, Mathf.Min(360f, page.Title.width), page.Title.height));
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.WordWrap = true;
                    Text.Font = GameFont.Tiny;
                    GUI.color = theme.MutedInk;
                    AlbumStyle.Label(page.Kicker, "MF_DesignYear_Kicker".Translate());
                    Text.Font = GameFont.Medium;
                    GUI.color = theme.Ink;
                    AlbumStyle.Label(page.Title, "MF_YearLetterTitle".Translate(Year));
                    Text.Font = GameFont.Small;
                    Rect summaryText = page.Summary; summaryText.width = Mathf.Max(1f, summaryText.width - 172f);
                    AlbumStyle.BodyLabel(summaryText, "MF_DesignYear_Summary".Translate(photos.Count, photos.Count(r => Kinds.IsMilestone(r.kind))));
                    Rect selectionHelp = new Rect(page.Summary.xMax - 158f, page.Summary.y, 158f, page.Summary.height);
                    GUI.color = theme.MutedInk;
                    Text.Anchor = TextAnchor.UpperRight;
                    AlbumStyle.HeaderLabel(selectionHelp, "MF_Phase2_HighlightHelp".Translate());
                    Text.Anchor = TextAnchor.UpperLeft;
                    TooltipHandler.TipRegion(selectionHelp, "MF_Phase2_HighlightCriteria".Translate());
                    GUI.color = Color.white;
                    Color line = theme.Ink; line.a = .22f;
                    Widgets.DrawBoxSolid(new Rect(page.Summary.x, page.Summary.yMax + 9f, page.Summary.width, 1f), line);
                    GUI.color = Color.white;
                    if (highlights.Count == 0)
                    {
                        Text.Anchor = TextAnchor.MiddleCenter;
                        GUI.color = theme.MutedInk;
                        AlbumStyle.Label(page.PhotoArea, "MF_DesignYear_NoPhotos".Translate());
                        Text.Anchor = TextAnchor.UpperLeft;
                    }
                    else
                        for (int i = 0; i < highlights.Count; i++) DrawPrint(page.Cards[i], highlights[i], i, page);

                    Text.Font = GameFont.Small;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    GUI.color = theme.MutedInk;
                    AlbumStyle.Label(page.Hint, "MF_DesignYear_Hint".Translate());
                    Widgets.EndScrollView();
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                    if (AlbumStyle.Button(page.AlbumButton, "MF_DesignYear_Album".Translate())) OpenAlbum();
                    string problem = NightProblem();
                    if (AlbumStyle.Button(page.NightButton, "MF_DesignYear_Night".Translate(), active: problem == null)) StartNight();
                    if (problem != null) TooltipHandler.TipRegion(page.NightButton, problem);
                    if (AlbumStyle.Button(page.CloseButton, "CloseButton".Translate())) Close();
                }
                finally
                {
                    GUI.color = oldColor;
                    Text.Font = oldFont;
                    Text.Anchor = oldAnchor;
                }
            }
        }

        private void DrawPrint(Rect card, PhotoRecord rec, int index, PageLayout page)
        {
            var theme = AlbumStyle.Theme;
            var store = PhotoStore.Instance;
            Texture2D tex = store == null ? null : PhotoTextureCache.Thumb(store.ThumbPath(rec), store.FilePath(rec));
            Rect image = PrintImageFor(card, page);
            GUI.color = Color.white;
            Widgets.DrawBoxSolid(new Rect(card.x + 3f, card.y + 4f, card.width, card.height), new Color(0f, 0f, 0f, 0.15f));
            Widgets.DrawBoxSolid(card, theme.Mat);
            Widgets.DrawBoxSolid(image, new Color(0.20f, 0.18f, 0.15f));
            if (tex != null) Widgets.DrawTextureFitted(image, tex, 1f);
            else
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = theme.Dark ? theme.MutedInk : Color.white;
                AlbumStyle.Label(image, "MF_MissingFile".Translate());
            }
            Text.Anchor = TextAnchor.UpperCenter;
            Text.Font = GameFont.Small;
            GUI.color = theme.Ink;
            string title = (rec.DisplayTitle ?? "").Replace("\n", " ");
            AlbumPresentation.CardTitle(new Rect(card.x + 8f, image.yMax + 6f, card.width - 16f, page.PrintTitleHeight), title);
            Text.Font = GameFont.Tiny;
            GUI.color = theme.MutedInk;
            AlbumStyle.Label(new Rect(card.x + 8f, card.yMax - page.PrintDateHeight - 3f, card.width - 16f, page.PrintDateHeight), rec.dateLabel ?? "");
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(card.center.x - 32f, card.y - 7f, 64f, 17f), MFIcons.Tape, ScaleMode.StretchToFill);
            if (Mouse.IsOver(card))
            {
                Widgets.DrawBoxSolid(image, new Color(1f, 1f, 1f, 0.08f));
                TooltipHandler.TipRegion(card, title + "\n" + (rec.dateLabel ?? "")
                    + (rec.DisplayDesc.NullOrEmpty() ? "" : "\n\n" + rec.DisplayDesc));
            }
            if (Widgets.ButtonInvisible(card)) OpenPhoto(index);
        }

        internal void OpenPhoto(int index)
        {
            Refresh();
            if (index < 0 || index >= highlights.Count) return;
            var rec = highlights[index];
            Close();
            MainTabWindow_Album.Open(rec);
        }

        internal void OpenAlbum()
        {
            Close();
            letter.OpenYearAlbum();
        }

        internal string NightProblem()
        {
            Refresh();
            if (photos.Count < MemoryNight.MinPhotos) return "MF_MemoryNightFewPhotos".Translate(MemoryNight.MinPhotos);
            var projector = letter.NightProjector();
            if (projector == null) return "MF_YearLetterNoProjector".Translate();
            // The button sets the film to this year before starting it, so the projector's currently selected film cannot veto it.
            if (projector.Map.lordManager.lords.Any(l => l.LordJob is LordJob_MFMemoryNight)) return "MF_MemoryNightAlready".Translate();
            IntVec3 spot = MemoryNight.GatherCell(projector);
            if (!projector.Map.mapPawns.FreeColonistsSpawned.Any(p => MemoryNight.HostProblem(p, spot) == null))
                return "MF_MemoryNightNobody".Translate();
            return null;
        }

        internal bool StartNight()
        {
            if (NightProblem() != null) return false;
            var projector = letter.NightProjector();
            bool started = letter.HoldNight(projector);
            if (started) Close();
            return started;
        }

        public override void PostClose()
        {
            base.PostClose();
            // The old ChoiceLetter's Close option also removes the notification. All close routes retain that behaviour.
            Find.LetterStack.RemoveLetter(letter);
        }
    }
}
