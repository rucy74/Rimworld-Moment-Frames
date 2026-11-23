using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace MomentFrames
{
    /// <summary>Film and faces first. Duration folds away; confirming is the only operation that changes the projector's film.</summary>
    public sealed class Dialog_MemoryNightSetup : Window
    {
        private readonly Building_PhotoProjector projector;
        private readonly Map map;
        private readonly HashSet<Pawn> selected = new HashSet<Pawn>();
        private readonly System.Random random = new System.Random();
        private string kind, person, personName;
        private int year, watchTicks = MemoryNight.DefaultWatchTicks;
        private bool details;
        private List<string> selectedFilm;
        private Vector2 scroll;
        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(940f, UI.screenWidth - 40f), Mathf.Min(860f, UI.screenHeight - 40f));
        public Dialog_MemoryNightSetup(Building_PhotoProjector projector, int year = 0)
        {
            this.projector = projector; map = projector.Map;
            kind = projector.FilterKind; person = projector.FilterPerson; personName = projector.FilterPersonName;
            this.year = year != 0 ? year : projector.FilterYear;
            if (year != 0) { kind = person = personName = ""; }
            else if (kind == Building_PhotoProjector.FilmSelected) selectedFilm = projector.SelectedPhotoIds.ToList();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.Where(Eligible)) selected.Add(pawn);
            doCloseX = false; doWindowBackground = false; forcePause = false; closeOnAccept = false;
        }
        private bool Eligible(Pawn pawn) => GroupPhoto.CanJoin(pawn, null, true)
            && pawn.Map == map && pawn.CanReach(MemoryNight.GatherCell(projector), PathEndMode.Touch, Danger.None);
        private List<PhotoRecord> Film => selectedFilm != null ? AlbumFavorites.Selected(PhotoStore.Instance.Records, selectedFilm) : PhotoStore.Instance?.Records.Where(r => r.HasFile
            && (kind == Building_PhotoProjector.FilmMilestones ? Kinds.IsMilestone(r.kind) : kind.NullOrEmpty() || r.kind == kind)
            && (person.NullOrEmpty() || MainTabWindow_Album.Matches(r, person, personName)) && (year == 0 || r.year == year))
            .OrderBy(r => r.ticksAbs).ToList() ?? new List<PhotoRecord>();
        private string FilmLabel => selectedFilm != null ? "MF_FavoriteSelectedFilm".Translate().ToString() : year != 0 ? "MF_FilmYear".Translate(year).ToString()
            : !person.NullOrEmpty() ? "MF_FilmPerson".Translate(personName).ToString()
            : kind == Building_PhotoProjector.FilmMilestones ? "MF_FilmMilestones".Translate().ToString()
            : !kind.NullOrEmpty() ? "MF_FilmKind".Translate(MainTabWindow_Album.KindLabel(kind)).ToString()
            : "MF_FilmAll".Translate().ToString();
        private void SetFilm(string k = "", string p = "", string name = "", int y = 0)
        { selectedFilm = null; kind = k; person = p; personName = name; year = y; }
        private void ChooseFilm()
        {
            var store = PhotoStore.Instance;
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("MF_FilmAll".Translate(), () => SetFilm()),
                new FloatMenuOption("MF_FilmMilestones".Translate(), () => SetFilm(Building_PhotoProjector.FilmMilestones))
            };
            if (store != null)
            {
                options.Insert(0, new FloatMenuOption("MF_FavoriteChooseFilm".Translate(), () =>
                    Find.WindowStack.Add(new Dialog_FavoriteFilm(store, selectedFilm, ids => { SetFilm(); selectedFilm = ids; }))));
                foreach (var key in store.Records.Where(r => r.HasFile).Select(r => r.kind).Distinct().OrderBy(MainTabWindow_Album.KindLabel))
                { string k = key; options.Add(new FloatMenuOption("MF_FilmKind".Translate(MainTabWindow_Album.KindLabel(k)), () => SetFilm(k))); }
                foreach (var pair in MainTabWindow_Album.PeopleIndex(store).OrderBy(kv => kv.Value))
                { string id = pair.Key, name = pair.Value; options.Add(new FloatMenuOption("MF_FilmPerson".Translate(name), () => SetFilm("", id, name))); }
                foreach (int value in store.Records.Where(r => r.HasFile && r.year != 0).Select(r => r.year).Distinct().OrderBy(y => y))
                { int y = value; options.Add(new FloatMenuOption("MF_FilmYear".Translate(y), () => SetFilm("", "", "", y))); }
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
        private string Status(Pawn pawn)
        {
            if (!Eligible(pawn))
            {
                string why = GroupPhoto.WhyNot(pawn, null, true);
                return why.NullOrEmpty() ? "MF_NightReachWarning".Translate().ToString() : why;
            }
            string medical = GroupPhoto.MedicalRestWarning(pawn);
            if (!medical.NullOrEmpty()) return medical;
            return !pawn.Awake() ? "MF_NightWakeWarning".Translate().ToString() : "";
        }
        public override void DoWindowContents(Rect inRect)
        {
            SharedPhotoUI.KeepCameraFree(this);
            if (!projector.Spawned || projector.Map != map || MemoryNight.Active(projector) != null) { Close(); return; }
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(inRect); Rect page = inRect.ContractedBy(24f);
                float control = SharedPhotoUI.ControlHeight;
                float titleHeight = Mathf.Max(control, AlbumStyle.TextHeight("MF_NightPrepare".Translate(), page.width - control - 12f, GameFont.Medium) + 8f);
                SharedPhotoUI.Heading(new Rect(page.x, page.y, page.width - control - 12f, titleHeight), "MF_NightPrepare".Translate());
                if (SharedPhotoUI.Button(new Rect(page.xMax - control, page.y, control, control), "×")) { Close(); return; }
                var film = Film;
                var roster = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.LabelShort).ToList();
                string why = film.Count < MemoryNight.MinPhotos ? "MF_MemoryNightFewPhotos".Translate(MemoryNight.MinPhotos).ToString()
                    : selected.Count == 0 ? "MF_NightChooseGuests".Translate().ToString()
                    : selected.Any(p => !Eligible(p)) ? "MF_NightGuestsChanged".Translate().ToString()
                    : "MF_NightStartHint".Translate().ToString();
                float hintHeight = AlbumStyle.TextHeight(why, page.width) + 8f;
                float footerHeight = hintHeight + control + 12f;
                Rect viewport = new Rect(page.x, page.y + titleHeight + 12f, page.width, Mathf.Max(40f, page.height - titleHeight - footerHeight - 22f));
                float width = viewport.width - 20f;
                int columns = width >= 600f ? 2 : 1;
                float cellWidth = (width - 8f * (columns - 1)) / columns;
                float rowHeight = Mathf.Max(86f, roster.Select(p => AlbumStyle.TextHeight(p.LabelShort, cellWidth - 122f)
                    + AlbumStyle.TextHeight(Status(p), cellWidth - 122f) + 18f).DefaultIfEmpty(86f).Max());
                string filmChoice = AlbumPresentation.Title("MF_NightFilmChoice".Translate(FilmLabel, film.Count));
                float filmButtonHeight = Mathf.Max(control, AlbumStyle.TextHeight(filmChoice, width - 16f) + 12f);
                float timingHeight=AlbumStyle.TextHeight("MF_UX_NightTiming".Translate(watchTicks/3600,film.Count),width)+12f;
                float contentHeight = timingHeight+filmButtonHeight + 180f + control * (details ? 6f : 4f) + 54f
                    + (details ? AlbumStyle.TextHeight("MF_NightTimeHint".Translate(), width) + 8f : 0f)
                    + Mathf.Ceil(roster.Count / (float)columns) * (rowHeight + 8f);
                Widgets.BeginScrollView(viewport, ref scroll, new Rect(0f, 0f, width, contentHeight));
                float y = 0f;
                if (SharedPhotoUI.Button(new Rect(0f, y, width, filmButtonHeight), filmChoice)) ChooseFilm();
                y += filmButtonHeight + 8f;
                if (SharedPhotoUI.Button(new Rect(0f, y, width, control), "MF_FavoriteChooseFilm".Translate()))
                    Find.WindowStack.Add(new Dialog_FavoriteFilm(PhotoStore.Instance, selectedFilm, ids => { SetFilm(); selectedFilm = ids; }));
                y += control + 8f;
                for (int i = 0; i < Mathf.Min(4, film.Count); i++)
                {
                    float cardWidth = (width - 24f) / 4f;
                    Rect card = new Rect(i * (cardWidth + 8f), y, cardWidth, 138f);
                    Widgets.DrawBoxSolid(card, AlbumStyle.Theme.Mat);
                    var texture = PhotoTextureCache.Thumb(PhotoStore.Instance.ThumbPath(film[i]), PhotoStore.Instance.FilePath(film[i]));
                    if (texture != null) GUI.DrawTexture(card.ContractedBy(6f), texture, ScaleMode.ScaleToFit);
                    TooltipHandler.TipRegion(card, film[i].DisplayTitle);
                }
                y += 148f;
                SharedPhotoUI.Label(new Rect(0,y,width,timingHeight),"MF_UX_NightTiming".Translate(watchTicks/3600,film.Count),true);y+=timingHeight;
                if (SharedPhotoUI.QuietButton(new Rect(0f, y, width, control), (details ? "MF_NightDetailsClose" : "MF_NightDetails").Translate())) details = !details;
                y += control + 8f;
                if (details)
                {
                    SharedPhotoUI.Label(new Rect(0f, y, width, control), "MF_NightDuration".Translate(watchTicks / 3600)); y += control;
                    watchTicks = Mathf.RoundToInt(Widgets.HorizontalSlider(new Rect(6f, y, width - 12f, control), watchTicks / 3600f, 1f, 6f)) * 3600; y += control;
                    string timing = "MF_NightTimeHint".Translate();
                    float height = AlbumStyle.TextHeight(timing, width) + 8f;
                    SharedPhotoUI.Label(new Rect(0f, y, width, height), timing, true); y += height;
                }
                SharedPhotoUI.Label(new Rect(0f, y, width, control), "MF_NightGuests".Translate(selected.Count)); y += control + 6f;
                float third = (width - 16f) / 3f;
                if (SharedPhotoUI.Button(new Rect(0f, y, third, control), "MF_PreparationAll".Translate()))
                { selected.Clear(); foreach (var p in roster.Where(Eligible)) selected.Add(p); }
                if (SharedPhotoUI.Button(new Rect(third + 8f, y, third, control), "MF_PreparationRandom".Translate()))
                { selected.Clear(); foreach (var p in roster.Where(Eligible).OrderBy(p => random.Next()).Take(6)) selected.Add(p); }
                if (SharedPhotoUI.Button(new Rect((third + 8f) * 2f, y, third, control), "MF_NightClearGuests".Translate())) selected.Clear();
                y += control + 8f;
                for (int i = 0; i < roster.Count; i++)
                {
                    var pawn = roster[i]; bool eligible = Eligible(pawn), chosen = selected.Contains(pawn);
                    Rect row = new Rect(i % columns * (cellWidth + 8f), y + i / columns * (rowHeight + 8f), cellWidth, rowHeight);
                    if (chosen) Widgets.DrawBoxSolid(row, new Color(AlbumStyle.Theme.Accent.r, AlbumStyle.Theme.Accent.g, AlbumStyle.Theme.Accent.b, .12f));
                    SharedPhotoUI.Check(new Rect(row.x + 10f, row.center.y - 11f, 22f, 22f), chosen, eligible);
                    SharedPhotoUI.Portrait(pawn, new Rect(row.x + 42f, row.center.y - 32f, 64f, 64f));
                    string status = Status(pawn); float nameHeight = AlbumStyle.TextHeight(pawn.LabelShort, cellWidth - 122f);
                    SharedPhotoUI.Label(new Rect(row.x + 114f, row.y + 10f, cellWidth - 122f, nameHeight), pawn.LabelShort, !eligible);
                    SharedPhotoUI.Label(new Rect(row.x + 114f, row.y + nameHeight + 13f, cellWidth - 122f, rowHeight - nameHeight - 16f), status, true);
                    if (Widgets.ButtonInvisible(row) && eligible) { if (!selected.Add(pawn)) selected.Remove(pawn); }
                    if (!status.NullOrEmpty()) TooltipHandler.TipRegion(row, status);
                }
                Widgets.EndScrollView();
                SharedPhotoUI.Label(new Rect(page.x, page.yMax - footerHeight, page.width, hintHeight), why, true);
                bool canStart = film.Count >= MemoryNight.MinPhotos && selected.Count > 0 && selected.All(Eligible);
                if (SharedPhotoUI.Button(new Rect(page.x, page.yMax - control, page.width, control), "MF_NightStartGathering".Translate(), canStart))
                {
                    if (selectedFilm != null) projector.SetSelectedFilm(selectedFilm);
                    else projector.SetFilm(kind, person, personName, year);
                    if (MemoryNight.StartPrepared(projector, selected, watchTicks)) Close();
                    else Messages.Message("MF_MemoryNightNobody".Translate(), MessageTypeDefOf.RejectInput, false);
                }
            }
        }
    }

    public sealed class Dialog_MemoryNightProgress : Window
    {
        private readonly LordJob_MFMemoryNight job;
        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(880f, UI.screenWidth - 40f), Mathf.Min(370f, UI.screenHeight - 40f));
        internal Dialog_MemoryNightProgress(LordJob_MFMemoryNight job)
        {
            this.job = job; doCloseX = false; doWindowBackground = false; forcePause = false; closeOnAccept = false;
            absorbInputAroundWindow = true;
        }
        public override void DoWindowContents(Rect inRect)
        {
            if (job.Finished) { Close(); return; }
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(inRect); Rect page = inRect.ContractedBy(22f);
                float control = SharedPhotoUI.ControlHeight;
                SharedPhotoUI.Heading(new Rect(page.x, page.y, page.width - control - 8f, control + 10f), "MF_MemoryNightOpen".Translate());
                if (SharedPhotoUI.Button(new Rect(page.xMax - control, page.y, control, control), "×")) { Close(); return; }
                string progress = job.Watching
                    ? "MF_NightProgressWatching".Translate(job.ArrivedCount, job.Expected.Count,
                        job.SlideIndex(job.FilmIds.Count) + 1, job.FilmIds.Count, Mathf.CeilToInt(job.TicksLeft / 60f)).ToString()
                    : "MF_NightGathering".Translate(job.ArrivedCount, job.Expected.Count).ToString();
                float y = page.y + control + 18f;
                float progressHeight = AlbumStyle.TextHeight(progress, page.width) + 8f;
                SharedPhotoUI.Label(new Rect(page.x, y, page.width, progressHeight), progress); y += progressHeight + 4f;
                string missing = string.Join(" · ", job.Missing.Take(6).Select(p => p?.LabelShort ?? "?"));
                string hint = job.Watching ? "MF_NightTimeHint".Translate().ToString()
                    : "MF_NightMissing".Translate(missing).ToString();
                SharedPhotoUI.Label(new Rect(page.x, y, page.width, Mathf.Max(30f, page.yMax - y - control - 10f)), hint, true);
                TooltipHandler.TipRegion(new Rect(page.x, y, page.width, Mathf.Max(30f, page.yMax - y - control - 10f)),
                    string.Join(" · ", job.Missing.Select(p => p?.LabelShort ?? "?")));
                float third = (page.width - 16f) / 3f;
                if (job.Watching)
                {
                    if (SharedPhotoUI.Button(new Rect(page.x, page.yMax - control, third * 2f + 8f, control), "MF_NightContinueWatching".Translate())) Close();
                }
                else
                {
                    if (SharedPhotoUI.Button(new Rect(page.x, page.yMax - control, third, control), "MF_NightKeepWaiting".Translate())) { job.ContinueWaiting(); Close(); }
                    if (SharedPhotoUI.Button(new Rect(page.x + third + 8f, page.yMax - control, third, control), "MF_NightStartPresent".Translate(), job.ArrivedCount > 0))
                    { if (job.StartWithPresent()) Close(); }
                }
                if (SharedPhotoUI.Button(new Rect(page.x + (third + 8f) * 2f, page.yMax - control, third, control), "MF_NightCancel".Translate()))
                { job.Cancelled = true; Close(); }
            }
        }
    }
}
