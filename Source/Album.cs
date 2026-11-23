using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MomentFrames
{
    /// <summary>Bottom-bar tab: the colony album. A leather cover with two paper pages of tilted polaroids (filters by kind / person / year), a
    /// chronicle tab listing milestones in order, and a gallery (one big photo, a rail of the others, the sentence under it) that also opens from
    /// any card, chronicle row, frame, card popup or a pawn's chronicle tab. Everything is drawn from the records; nothing is cached beyond the filtered list.</summary>
    public class MainTabWindow_Album : MainTabWindow
    {
        private enum Tab { Photos, Chronicle, Gallery, Settlement, Imported, Archive }
        private Vector2 archiveScroll;
        private long archiveBytes;
        private int archiveBytesVersion = -1;
        private readonly ImportedPhotosTab imports = new ImportedPhotosTab();

        private Tab tab = Tab.Photos;
        private string filterKind;          // null = all
        private bool contextualFilters;    // Temporary navigation from a person/year/photo; not a lasting album preference.
        private string filterPerson;        // pawn load id (or a bare name for records from before v2.4); null = all
        private string filterPersonName;    // display name of the person filter
        private int filterYear;             // 0 = all
        private int page;
        private string search = "", cachedSearch;
        private bool includeBirthdays, cachedBirthdays;
        private int hiddenBirthdays;   // birthday photos the default leaves out of the current list (same year and search)
        private bool versionsOpen;     // the edited versions band of the photo named below is open
        private string versionsOpenFor;
        private PhotoRecord reunionRequest;   // opened after this frame's drawing, because the album closes for it
        private bool favoritesOnly, cachedFavorites;
        private bool filtersExpanded;
        private Vector2 galleryLayoutScroll;
        private readonly HashSet<string> expandedSurveys = new HashSet<string>();
        private List<PhotoRecord> groupedSource;
        private List<ChronicleGroups.Row> groupedRows;
        private List<PhotoRecord> burstSource;
        private PhotoBursts bursts;
        private Vector2 burstScroll;
        private List<ChronicleGroups.Row> measuredRows;
        private float[] chronicleHeights;
        private float chronicleHeight, chronicleWidth;
        private string chronicleFont;
        private float chronicleScale;
        private int chronicleRevision = -1;
        private FilterSnapshot peopleReturn;
        private string peopleReturnPhoto, peopleReturnVersion;
        private string bookAnchor;
        private bool includeManual;         // chronicle: show manual shots too
        private Vector2 chronicleScroll;
        private int cachedVersion = -1;
        private PhotoStore cachedStore;   // a new game has a new store: never reuse the list across games
        private bool cacheValid;
        private Tab cachedTab;
        private bool cachedChrono, cachedManual;
        private string cachedKind, cachedPerson;
        private int cachedYear;
        private List<PhotoRecord> filtered = new List<PhotoRecord>();
        // Gallery.
        private string galleryId;                 // the photo shown big
        private string galleryVersion;            // which version of it: null = the one it is shown as, "" = the original, else an edit id
        private bool galleryChronological;        // entered from the chronicle: its order and milestone rule
        private Vector2 railScroll;
        private bool railFollow;                  // scroll the rail to the current photo on the next draw
        private Vector2 versionScroll;
        private string versionPhotoId, versionViewing;
        private Vector2 storyScroll;
        private string storyPhotoId, storyVersion;
        private readonly List<string> held = new List<string>();   // visible originals held by this gallery
        private readonly Dictionary<string, Material> heldOwnership = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FilterSnapshot> filterSnapshots = new List<FilterSnapshot>();
        private PhotoStore comparisonStore;
        private int comparisonVersion = -1;
        private string comparisonPhoto;
        private PhotoRecord comparisonBirthday, comparisonView;
        private sealed class FilterSnapshot
        {
            internal PhotoStore Store;
            internal int Version, Year;
            internal Tab Tab;
            internal bool Chrono, Manual, Birthdays, Favorites, Contextual;
            internal string Kind, Person, PersonName, Search;
            internal List<PhotoRecord> List;
            internal int HiddenBirthdays;
        }

        private const float FooterH = 34f, Pad = 22f, Spine = 26f;
        private const float Border = 9f, Caption = 40f, Tape = 24f;
        private const int Cols = 2, Rows = 2;
        private const float RailW = 330f;
        private static float RailRowH => Mathf.Max(120f, AlbumPresentation.TitleHeight + 2f * AlbumPresentation.DateHeight + 24f);
        internal static Color Ink => AlbumStyle.Theme.Ink;
        internal static Color InkFaint => AlbumStyle.Theme.MutedInk;
        internal static Color Cream => AlbumStyle.Theme.Paper;
        private static Color CoverText => AlbumStyle.Theme.CoverInk;
        private static bool HasIntegrations => OptionalPhotoBridges.RimGramInstalled || OptionalPhotoBridges.RimTalkInstalled;
        private static float GalleryTitleHeight => AlbumStyle.TextHeight("한글\n한글", 900f, GameFont.Medium) + 4f;
        private static float GalleryInfoHeight(float width) => 48f + GalleryTitleHeight + 2f * HeaderRowHeight
            + 2f * GalleryActionHeight(width - 28f) + AlbumStyle.TextHeight("한글\n한글", 900f, GameFont.Small);
        private static float GalleryActionHeight(float width)
        {
            float height = HeaderRowHeight;
            string[] primary = { "MF_GalleryRevision_Text", "MF_Edit", "MF_Feedback_ReshootShort", "MF_GalleryRevision_Reunion" };
            string[] secondary = HasIntegrations ? new[] { "MF_Feedback_Compare", "MF_Feedback_PhotoTools", "MF_Archive_Move", "MF_GalleryRevision_Integrations" }
                : new[] { "MF_Feedback_Compare", "MF_Feedback_PhotoTools", "MF_Archive_Move" };
            foreach (var row in new[] { primary, secondary })
                foreach (string key in row)
                    height = Mathf.Max(height, AlbumStyle.TextHeight(AlbumPresentation.Title(key.Translate()),
                        Mathf.Max(1f, (width - 8f * (row.Length - 1)) / row.Length - 8f), GameFont.Small) + 8f);
            return height;
        }

        /// <summary>All three tabs share the book's page size.</summary>
        public override Vector2 RequestedTabSize => new Vector2(Mathf.Min(1280f, UI.screenWidth - 20f), Mathf.Min(860f, UI.screenHeight - 70f));
        protected override float Margin => 0f;

        private static int PerSpread => Cols * Rows * 2;

        public const float MinOpacity = 0.25f;
        public static readonly Vector2 MinSize = new Vector2(1000f, 600f);

        /// <summary>A window of its own (user, 2026-09-26: "fixed at the bottom left — let me move it, resize it, and set its transparency"): drag it
        /// by any empty spot, resize it from the bottom-right corner, and the leather and paper can be made see-through. Where it was left and the
        /// transparency are kept in the mod settings.</summary>
        public MainTabWindow_Album()
        {
            draggable = true;
            resizeable = true;
            doWindowBackground = false;   // the leather is the background, drawn at the chosen opacity
        }

        private static float Opacity => Mathf.Clamp(MomentFramesMod.Settings?.albumOpacity ?? 1f, MinOpacity, 1f);

        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            var s = MomentFramesMod.Settings;
            if (s == null || s.albumW < MinSize.x || s.albumH < MinSize.y) return;
            float w = Mathf.Min(s.albumW, UI.screenWidth), h = Mathf.Min(s.albumH, UI.screenHeight);
            windowRect = new Rect(Mathf.Clamp(s.albumX, 0f, UI.screenWidth - w), Mathf.Clamp(s.albumY, 0f, UI.screenHeight - h), w, h);
        }

        /// <summary>Back to the size and place a main tab normally has (the reset button).</summary>
        private void ResetPlacement()
        {
            var s = MomentFramesMod.Settings;
            if (s != null) s.albumX = s.albumY = s.albumW = s.albumH = -1f;
            Vector2 size = RequestedTabSize;
            windowRect = new Rect(0f, UI.screenHeight - 35f - size.y, size.x, size.y);
            SaveSettings();
        }

        private void RememberPlacement()
        {
            var s = MomentFramesMod.Settings;
            if (s == null) return;
            s.albumX = windowRect.x;
            s.albumY = windowRect.y;
            s.albumW = windowRect.width;
            s.albumH = windowRect.height;
        }

        private static void SaveSettings()
        {
            try { LoadedModManager.GetMod<MomentFramesMod>()?.WriteSettings(); }
            catch (Exception e) { Log.Warning("[MomentFrames] could not save the album window settings: " + e.Message); }
        }

        private static readonly System.Reflection.FieldInfo ResizerF = HarmonyLib.AccessTools.Field(typeof(Window), "resizer");
        private WindowResizer minSizeSetOn;

        /// <summary>The window's minimum size (Verse keeps the resizer private; its default minimum of 150 would crush the book). Looked up once
        /// per resizer, not on every GUI event.</summary>
        private void KeepMinimumSize()
        {
            if (minSizeSetOn != null || ResizerF == null) return;
            var rz = ResizerF.GetValue(this) as WindowResizer;
            if (rz == null) return;
            rz.minWindowSize = MinSize;
            minSizeSetOn = rz;
        }

        private static MainButtonDef Def => DefDatabase<MainButtonDef>.GetNamed("MF_Album", false);

        /// <summary>Opens the album on one pawn (the pawn's chronicle tab): the photo pages or the chronicle filtered to them, or the gallery on
        /// rec with the rail listing that pawn's photos (in chronicle order when chronological; withManual = not only milestones).</summary>
        public static void OpenFor(Pawn p, PhotoRecord rec = null, bool chronological = false, bool withManual = true)
        {
            var def = Def;
            if (def == null || p == null) return;
            if (def.TabWindow is MainTabWindow_Album w)
            {
                w.filterKind = null;
                w.search = "";
                w.filterYear = 0;
                w.filterPerson = p.GetUniqueLoadID();
                w.contextualFilters = true;
                w.favoritesOnly = false;
                w.filterPersonName = p.LabelShort;
                w.includeManual = withManual;
                if (rec == null) { w.bookAnchor = null; w.galleryId = null; }
                if (rec != null) w.ShowGallery(rec, chronological);
                else w.SetTab(chronological ? Tab.Chronicle : Tab.Photos);
                w.page = 0;
            }
            if (Find.MainTabsRoot.OpenTab != def) Find.MainTabsRoot.SetCurrentTab(def);
        }

        /// <summary>Opens the album's photo pages on one year (the year's letter).</summary>
        public static void OpenYear(int year)
        {
            var def = Def;
            if (def == null) return;
            if (def.TabWindow is MainTabWindow_Album w)
            {
                w.filterKind = null;
                w.search = "";
                w.filterPerson = null;
                w.filterPersonName = null;
                w.filterYear = year;
                w.contextualFilters = true;
                w.favoritesOnly = false;
                w.bookAnchor = null;
                w.galleryId = null;
                w.includeManual = true;
                w.SetTab(Tab.Photos);
                w.page = 0;
            }
            if (Find.MainTabsRoot.OpenTab != def) Find.MainTabsRoot.SetCurrentTab(def);
        }

        /// <summary>Year the album is filtered to, 0 = all (probe).</summary>
        public int YearFilter => filterYear;

        /// <summary>Load id of the person the album is filtered to, or null (probe).</summary>
        public string PersonFilter => filterPerson;

        /// <summary>Opens the gallery on this photo (frames, the card popup, letters, collage and projector). The rail lists the photos under the
        /// current filters; if the photo is not among them the filters are cleared so it is.</summary>
        public static void Open(PhotoRecord rec, bool fromChronicle = false)
        {
            var def = Def;
            if (def == null || rec == null) return;
            if (def.TabWindow is MainTabWindow_Album w) w.ShowGallery(rec, fromChronicle);
            if (Find.MainTabsRoot.OpenTab != def) Find.MainTabsRoot.SetCurrentTab(def);
        }

        /// <summary>Opens the gallery on one version of a photo (the editor calls this with the edit it just saved).</summary>
        public static void ShowVersion(PhotoRecord rec, string version)
        {
            var def = Def;
            if (def == null || rec == null) return;
            if (def.TabWindow is MainTabWindow_Album w)
            {
                w.ShowGallery(rec, w.galleryChronological);
                w.galleryVersion = version;
            }
            if (Find.MainTabsRoot.OpenTab != def) Find.MainTabsRoot.SetCurrentTab(def);
        }

        /// <summary>The version the gallery shows (probe): null = the one the photo is shown as.</summary>
        public string GalleryVersion => galleryVersion;

        /// <summary>The photo the gallery shows (probe).</summary>
        public PhotoRecord GalleryCurrent => (tab == Tab.Gallery || (tab == Tab.Imported && imports.Detail)) ? PhotoStore.Instance?.Get(galleryId) : null;

        private void ShowGallery(PhotoRecord rec, bool fromChronicle)
        {
            if (rec.Archived) { Find.WindowStack.Add(new Dialog_MFPhotoZoom(rec)); return; }
            galleryChronological = fromChronicle;
            galleryId = rec.id;
            bookAnchor = rec.id;
            galleryVersion = null;
            railFollow = true;
            if (PhotoSources.Imported(rec))
            { imports.Open(PhotoStore.Instance, rec); SetTab(Tab.Imported); galleryChronological = false; return; }
            SetTab(Tab.Gallery);
            var list = Filtered();
            if (!list.Any(r => r.id == rec.id))
            {
                filterKind = null;
                search = "";
                filterPerson = null;
                filterPersonName = null;
                filterYear = 0;
                includeManual = true;
                favoritesOnly = false;
                cacheValid = false;
                // A person's own photo (a birthday) shows only with that person's filter.
                if (Kinds.PersonalOnly(rec.kind) && rec.people.Count > 0)
                {
                    filterPerson = rec.peopleIds.Count > 0 ? rec.peopleIds[0] : rec.people[0];
                    contextualFilters = true;
                    filterPersonName = rec.people[0];
                }
            }
        }

        private void SetTab(Tab t)
        {
            if (tab == t) return;
            if (tab == Tab.Settlement) SettlementRecordsTab.Reset();
            string keep = galleryId ?? bookAnchor;
            tab = t;
            if (t == Tab.Photos)
            {
                var list = Filtered();
                var prints = Bursts(list);
                int index = prints.PrintIndex(keep);
                if (index >= 0) page = AlbumPresentation.SpreadFor(index, prints.Prints.Count);
            }
            if (t != Tab.Gallery && !(t == Tab.Imported && imports.Detail)) ReleaseHeld();
        }

        /// <summary>Probe helper: switch tab programmatically.</summary>
        public void ShowChronicle(bool chronicle, bool withManual = false) { SetTab(chronicle ? Tab.Chronicle : Tab.Photos); includeManual = withManual; page = 0; }

        public override void PostClose()
        {
            if (contextualFilters) ClearFilters();
            includeBirthdays = false;   // the default comes back however the album was opened
            cacheValid = false;
            SettlementRecordsTab.Reset();
            ReleaseHeld();
            RememberPlacement();
            SaveSettings();
            base.PostClose();
        }

        public static string KindLabel(string kind)
        {
            string key = "MF_Kind_" + (kind ?? "manual");
            return DescriptionCandidates.HasText(key) ? key.Translate().ToString() : kind;
        }

        /// <summary>Is this person (load id, or a bare name for records from before v2.4) in the photo?</summary>
        internal static bool Matches(PhotoRecord r, string person, string name)
        {
            if (r.peopleIds.Count > 0) return r.peopleIds.Contains(person);
            return !name.NullOrEmpty() && r.people.Contains(name);
        }

        /// <summary>People in photos or verified event records: id (or legacy bare name) -> latest display name.</summary>
        internal static Dictionary<string, string> PeopleIndex(PhotoStore store)
        {
            var names = new Dictionary<string, string>();
            if (store == null) return names;
            foreach (var rec in store.Records.Where(x => !PhotoSources.Imported(x) && (x.HasFile||x.eventOnly)).OrderBy(x => x.ticksAbs))
            {
                if(rec.eventPeopleIds.Count>0)
                    for(int i=0;i<rec.eventPeopleIds.Count;i++)
                        names[rec.eventPeopleIds[i]]=i<rec.eventPeopleNames.Count?rec.eventPeopleNames[i]:rec.eventPeopleIds[i];
                else foreach(var n in rec.eventPeopleNames)names[n]=n;
                if (rec.peopleIds.Count > 0)
                    for (int i = 0; i < rec.peopleIds.Count; i++)
                        names[rec.peopleIds[i]] = i < rec.people.Count ? rec.people[i] : rec.peopleIds[i];
                else
                    foreach (var n in rec.people) names[n] = n;
            }
            return names;
        }

        private bool Chronological => tab == Tab.Chronicle || (tab == Tab.Gallery && galleryChronological);

        private PhotoBursts Bursts(List<PhotoRecord> list)
        {
            if (!ReferenceEquals(burstSource, list) || bursts == null)
            { burstSource = list; bursts = new PhotoBursts(list); }
            return bursts;
        }

        private List<PhotoRecord> Filtered()
        {
            var store = PhotoStore.Instance;
            if (tab == Tab.Imported) return imports.Visible(store);
            if (store == null) { filtered.Clear(); return filtered; }
            if (!ReferenceEquals(cachedStore, store))
            { peopleReturn = null; expandedSurveys.Clear(); groupedSource = null; }
            // Compared field by field: this runs on every GUI event, and building a key string each time was garbage for nothing.
            if (cacheValid && ReferenceEquals(cachedStore, store) && cachedVersion == store.Version && cachedTab == tab && cachedChrono == galleryChronological
                && cachedKind == filterKind && cachedPerson == filterPerson && cachedYear == filterYear && cachedManual == includeManual
                && cachedSearch == search && cachedBirthdays == includeBirthdays && cachedFavorites == favoritesOnly) return filtered;
            cacheValid = true;
            cachedStore = store;
            cachedVersion = store.Version;
            cachedTab = tab;
            cachedChrono = galleryChronological;
            cachedKind = filterKind;
            cachedPerson = filterPerson;
            cachedYear = filterYear;
            cachedManual = includeManual;
            cachedSearch = search;
            cachedBirthdays = includeBirthdays;
            cachedFavorites = favoritesOnly;
            filterSnapshots.RemoveAll(snapshot => !ReferenceEquals(snapshot.Store, store) || snapshot.Version != store.Version);
            var reusable = filterSnapshots.FirstOrDefault(snapshot => snapshot.Tab == tab && snapshot.Chrono == galleryChronological
                && snapshot.Kind == filterKind && snapshot.Person == filterPerson && snapshot.PersonName == filterPersonName && snapshot.Year == filterYear
                && snapshot.Manual == includeManual && snapshot.Search == search && snapshot.Birthdays == includeBirthdays && snapshot.Favorites == favoritesOnly);
            if (reusable != null)
            {
                filtered = reusable.List;
                hiddenBirthdays = reusable.HiddenBirthdays;
                if (tab == Tab.Photos)
                {
                    var prints = Bursts(filtered); int keepIndex = prints.PrintIndex(bookAnchor);
                    if (keepIndex >= 0) page = AlbumPresentation.SpreadFor(keepIndex, prints.Prints.Count);
                }
                return filtered;
            }
            IEnumerable<PhotoRecord> q = store.Records.Where(r => !PhotoSources.Imported(r) && (r.HasFile || (tab == Tab.Chronicle && r.eventOnly)));
            if (favoritesOnly) q = q.Where(r => r.favorite);
            bool hideBirthdays = !favoritesOnly && !includeBirthdays && filterPerson == null && (filterKind == null || !Kinds.PersonalOnly(filterKind));
            var hiddenBirthdayPhotos = hideBirthdays && filterKind == null ? q.Where(r => Kinds.PersonalOnly(r.kind)) : Enumerable.Empty<PhotoRecord>();
            if (Chronological && !includeManual && !favoritesOnly) q = q.Where(r => Kinds.IsMilestone(r.kind) || Kinds.IsSurvey(r.kind)
                || (Kinds.PersonalOnly(r.kind) && (includeBirthdays || filterPerson != null || filterKind == Kinds.Birthday)));
            if (filterKind != null) q = q.Where(r => r.kind == filterKind);
            // A survey keeps its whole-map picture on the photo tab; its four regions stay in the gallery, the chronicle and the settlement tab.
            if (tab == Tab.Photos && filterKind == null && !favoritesOnly && search.NullOrEmpty()) q = q.Where(r => r.kind != Kinds.SettlementQuarter);
            if (hideBirthdays) q = q.Where(r => !Kinds.PersonalOnly(r.kind));
            if (filterYear != 0) q = q.Where(r => r.year == filterYear);
            if (filterPerson != null) q = q.Where(r => Matches(r, filterPerson, filterPersonName)
                ||(Chronological&&AlbumPresentation.EventMatches(r,filterPerson,filterPersonName)));
            q = q.Where(r => AlbumPresentation.SearchMatches(r, search));
            hiddenBirthdays = hiddenBirthdayPhotos.Count(r => (filterYear == 0 || r.year == filterYear) && AlbumPresentation.SearchMatches(r, search));
            filtered = (Chronological ? q.OrderBy(r => r.ticksAbs) : q.OrderByDescending(r => r.ticksAbs)).ToList();
            filterSnapshots.Add(new FilterSnapshot { Store = store, Version = store.Version, Tab = tab, Chrono = galleryChronological, Kind = filterKind,
                Person = filterPerson, PersonName = filterPersonName, Year = filterYear, Manual = includeManual, Search = search, Birthdays = includeBirthdays, Favorites = favoritesOnly, List = filtered, HiddenBirthdays = hiddenBirthdays });
            if (filterSnapshots.Count > 8) filterSnapshots.RemoveAt(0);
            if (tab == Tab.Photos)
            {
                var prints = Bursts(filtered); int anchorIndex = prints.PrintIndex(bookAnchor);
                if (anchorIndex >= 0) page = AlbumPresentation.SpreadFor(anchorIndex, prints.Prints.Count);
            }
            return filtered;
        }

        internal string EmptyMessageKey => PhotoStore.Instance?.Records.Any(r => r.HasFile && !PhotoSources.Imported(r)) == true
            ? "MF_DesignAlbum_NoMatches" : "MF_NoPhotosAlbum";

        internal void ClearSearch() {search="";cacheValid=false;railFollow=true;}

        internal void ClearFilters()
        {
            contextualFilters = false;
            peopleReturn = null;
            filterKind = null;
            filterPerson = filterPersonName = null;
            filterYear = 0;
            search = "";
            favoritesOnly = false;
            includeBirthdays = false;
            bookAnchor = null;
            includeManual = true;
            page = 0;
            chronicleScroll = railScroll = Vector2.zero;
            cacheValid = false;
            railFollow = true;
        }

        private void DrawEmpty(Rect area, Color ink)
        {
            bool hasPhotos = EmptyMessageKey == "MF_DesignAlbum_NoMatches";
            if(!hasPhotos)
            {
                using(AlbumStyle.Begin(true))
                {
                    float width=Mathf.Min(620,area.width-24);
                    string text=PhotoGuide.EmptyKey.Translate();
                    float th=AlbumStyle.TextHeight(text,width)+16, h=SharedPhotoUI.ControlHeight;
                    float y=Mathf.Max(area.y+10,area.center.y-(th+h*2+20)/2);
                    float x=area.center.x-width/2;
                    SharedPhotoUI.Label(new Rect(x,y,width,th),text);
                    float half=(width-8)/2;
                    if(SharedPhotoUI.Button(new Rect(x,y+th,half,h),"MF_Learn_Manual".Translate(),Find.CurrentMap!=null))PhotoGuide.Manual();
                    if(SharedPhotoUI.Button(new Rect(x+half+8,y+th,half,h),"MF_Learn_Group".Translate(),Find.CurrentMap!=null))PhotoGuide.Open("family");
                    if(SharedPhotoUI.QuietButton(new Rect(x,y+th+h+8,width,h),"MF_Learn_Title".Translate()))PhotoGuide.Open();
                }
                return;
            }
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = ink;
            float top = area.center.y - (hasPhotos ? 64f : 16f);
            AlbumStyle.Label(new Rect(area.x + 12f, top, area.width - 24f, 32f), EmptyMessageKey.Translate());
            if (hasPhotos)
            {
                Text.Font = GameFont.Tiny;
                AlbumStyle.Label(new Rect(area.x + 12f, top + 34f, area.width - 24f, 40f), "MF_DesignAlbum_NoMatchesHint".Translate());
                Text.Font = GameFont.Small;
            }
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            if (hasPhotos && FitButton(new Rect(area.center.x - 100f, top + 80f, 200f, 32f), "MF_DesignAlbum_ClearFilters".Translate()))
                ShowAllPhotos();
        }

        public override void DoWindowContents(Rect inRect)
        {
            using (AlbumStyle.Begin())
            {
                KeepMinimumSize();
                drawShadow = Opacity >= 0.99f;
                AlbumStyle.DrawCover(inRect, Opacity);
                GUI.color = Color.white;
                var list = Filtered();
                if (tab == Tab.Photos) list = Bursts(list).Prints;
                float HeaderH = HeaderHeight(inRect.width - 2f * Pad);
                DrawHeader(new Rect(inRect.x + Pad, inRect.y + 12f, inRect.width - 2f * Pad, HeaderH));
                list = Filtered();
                if (tab == Tab.Photos) list = Bursts(list).Prints;
                float footer = tab == Tab.Photos ? FooterH + 10f : 6f;
                Rect body = new Rect(inRect.x + Pad, inRect.y + HeaderH + 16f, inRect.width - 2f * Pad, inRect.height - HeaderH - 16f - footer);
                if (tab == Tab.Photos) DrawPhotos(body, list);
                else if (tab == Tab.Chronicle) DrawChronicle(body, list);
                else if (tab == Tab.Settlement) SettlementRecordsTab.Draw(body, PhotoStore.Instance);
                else if (tab == Tab.Imported) DrawImported(body);
                else if (tab == Tab.Archive) DrawArchive(body);
                else DrawGallery(body, list);
                DrawFooter(new Rect(inRect.x + Pad, inRect.yMax - FooterH - 6f, inRect.width - 2f * Pad, FooterH), list);
            }
            if (reunionRequest != null)
            {
                var request = reunionRequest;
                reunionRequest = null;
                Find.WindowStack.Add(new Dialog_Reunion(request));
                Find.MainTabsRoot.SetCurrentTab(null, false);
            }
        }

        internal void OpenImported(PhotoSource? source = null)
        {
            imports.Visible(PhotoStore.Instance);
            if (source.HasValue) imports.SelectSource(source.Value);
            imports.Detail = false; peopleReturn = null; ReleaseHeld(); SetTab(Tab.Imported);
        }

        internal float ArchiveTabWidth => peopleReturn == null && (PhotoStore.Instance?.ArchivedRecords.Any(r => r.HasFile) ?? false) ? CoverWidth("MF_Archive_Tab", 110f) : 0f;

        internal void ShowArchive() { peopleReturn = null; ReleaseHeld(); archiveScroll = Vector2.zero; SetTab(Tab.Archive); }

        private void DrawArchive(Rect body)
        {
            var store = PhotoStore.Instance;
            // A deleted moment that keeps its chronicle line stays archived without a file; the tab shows only photos.
            var photos = store?.ArchivedRecords.Where(r => r.HasFile).OrderByDescending(r => r.archivedTicksAbs).ThenBy(r => r.id).ToList();
            if (photos == null || photos.Count == 0) { SetTab(Tab.Photos); return; }
            if (archiveBytesVersion != store.Version) { archiveBytesVersion = store.Version; archiveBytes = PhotoArchive.Bytes(store); }
            AlbumStyle.DrawPaper(body);
            Rect area = body.ContractedBy(12f);
            float gap = 10f, buttonH = SharedPhotoUI.ControlHeight;
            string empty = "MF_Archive_Empty".Translate();
            Text.Font = GameFont.Small;
            float emptyW = Mathf.Clamp(Text.CalcSize(empty).x + 32f, 120f, area.width * .4f);
            string note = "MF_Archive_Note".Translate(photos.Count, StorageUsage.Bytes(archiveBytes));
            float noteW = area.width - 24f - emptyW;
            float noteH = Mathf.Max(buttonH, AlbumStyle.TextHeight(note, noteW)) + 8f;
            SharedPhotoUI.Label(new Rect(area.x + 8f, area.y, noteW, noteH), note, true);
            if (FitButton(new Rect(area.xMax - 8f - emptyW, area.y + 4f, emptyW, buttonH), empty)) AskDeleteArchived(photos);
            Rect grid = new Rect(area.x, area.y + noteH, area.width, Mathf.Max(1f, area.height - noteH));
            float inner = grid.width - 18f;
            int columns = Mathf.Clamp(Mathf.FloorToInt(inner / 250f), 2, 6);
            float cellW = (inner - (columns - 1) * gap) / columns, cardH = cellW * .9f, rowH = cardH + buttonH + gap + 6f;
            int rows = (photos.Count + columns - 1) / columns;
            Rect view = new Rect(0f, 0f, inner, Mathf.Max(grid.height, rows * rowH));
            PhotoRecord restore = null, delete = null;
            Widgets.BeginScrollView(grid, ref archiveScroll, view);
            for (int i = 0; i < photos.Count; i++)
            {
                float y = (i / columns) * rowH, x = (i % columns) * (cellW + gap);
                if (y + rowH < archiveScroll.y || y > archiveScroll.y + grid.height) continue;
                Rect cell = new Rect(x, y, cellW, cardH);
                DrawCard(cell, photos[i], i);
                float restoreW = (cellW - 22f) * .6f;
                if (FitButton(new Rect(x + 8f, y + cardH + 4f, restoreW, buttonH), "MF_Archive_Restore".Translate())) restore = photos[i];
                if (FitButton(new Rect(x + 14f + restoreW, y + cardH + 4f, cellW - 22f - restoreW, buttonH), "MF_Archive_Delete".Translate())) delete = photos[i];
            }
            Widgets.EndScrollView();
            if (restore != null)
            {
                store.Restore(restore);
                Messages.Message("MF_Archive_Restored".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }
            if (delete != null) AskDeleteArchived(new List<PhotoRecord> { delete });
        }

        /// <summary>Asks, then deletes archived photos for good and says how much room it made.</summary>
        internal static void AskDeleteArchived(IList<PhotoRecord> photos)
        {
            if (photos == null || photos.Count == 0) return;
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(PhotoArchive.DeleteQuestion(photos), () =>
            {
                var store = PhotoStore.Instance;
                if (store == null) return;
                long freed = store.DeleteArchived(photos);
                Messages.Message("MF_Archive_Deleted".Translate(StorageUsage.Bytes(freed)), MessageTypeDefOf.TaskCompletion, false);
            }, true));
        }

        /// <summary>The same step the menu's question leads to. Moves on to a neighbour so the gallery does not jump back to the first photo.</summary>
        internal void ArchivePhotos(IList<PhotoRecord> photos)
        {
            var store = PhotoStore.Instance;
            if (store == null) return;
            if (photos.Any(PhotoArchive.InNightFilm)) { Messages.Message("MF_Archive_InNight".Translate(), MessageTypeDefOf.RejectInput, false); return; }
            var list = Filtered();
            int index = Math.Max(0, list.FindIndex(r => r.id == galleryId));
            var next = list.Skip(index + 1).Concat(list.Take(index).Reverse()).FirstOrDefault(r => !photos.Contains(r));
            bool moved = false;
            foreach (var photo in photos) moved |= store.Archive(photo);
            if (!moved) return;
            galleryVersion = null;
            if (next != null) galleryId = next.id;
            else { galleryId = null; SetTab(Tab.Photos); }
            Messages.Message("MF_Archive_Moved".Translate(), MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>Why the gallery's button is greyed out for this photo; null when it can move.</summary>
        internal string ArchiveBlocked(PhotoRecord rec) => !PhotoArchive.Offered(rec) ? "MF_Archive_SurveyHint".Translate().ToString()
            : PhotoArchive.InNightFilm(rec) ? "MF_Archive_InNight".Translate().ToString() : null;

        /// <summary>A single photo goes straight to the question; a burst first asks how much of it.</summary>
        internal void ArchiveButton(PhotoRecord rec)
        {
            var options = ArchiveOptions(rec);
            if (options.Count > 1) Find.WindowStack.Add(new FloatMenu(options));
            else options[0].action?.Invoke();
        }

        internal List<FloatMenuOption> ArchiveOptions(PhotoRecord rec)
        {
            var options = new List<FloatMenuOption> { ArchiveOption("MF_Archive_Move".Translate(), new List<PhotoRecord> { rec }) };
            var store = PhotoStore.Instance;
            var shots = store == null ? null : new PhotoBursts(store.Records.ToList()).Shots(rec);
            if (shots?.Count > 1) options.Add(ArchiveOption("MF_Archive_MoveBurst".Translate(shots.Count), shots));
            return options;
        }

        private FloatMenuOption ArchiveOption(string label, IList<PhotoRecord> photos)
        {
            if (photos.Any(PhotoArchive.InNightFilm)) return new FloatMenuOption(label + " (" + "MF_Archive_InNight".Translate() + ")", null);
            return new FloatMenuOption(label, () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(PhotoArchive.Question(photos), () => ArchivePhotos(photos))));
        }

        private void DrawImported(Rect body)
        {
            var list = imports.Visible(PhotoStore.Instance);
            if (!imports.Detail)
            { imports.Draw(body, PhotoStore.Instance, DrawCard, rec => ShowGallery(rec, false)); return; }
            float h = HeaderRowHeight;
            if (CoverButton(new Rect(body.x, body.y, 280f, h), "MF_Imports_Back".Translate(), false))
            { imports.Detail = false; ReleaseHeld(); return; }
            Rect content = new Rect(body.x, body.y + h + 8, body.width, Mathf.Max(1, body.height - h - 8));
            if (list.Count == 0) SharedPhotoUI.Label(content, "MF_DesignNav_NoMatches".Translate(), true);
            else DrawGallery(content, list);
        }

        // ---------- header: title, tabs, filters, export ----------

        private static float CoverWidth(string key, float least)
        {
            var words = new GUIContent(AlbumPresentation.Title(key.Translate()));
            return Mathf.Max(least, AlbumStyle.StyleFor(words.text, GameFont.Small).CalcSize(words).x + 18f);
        }

        private bool CoverButton(Rect r, string label, bool active, string tip = null)
        {
            Widgets.DrawBoxSolid(r, active ? Cream : Color.Lerp(AlbumStyle.Theme.Cover, CoverText, 0.1f));
            if (active) Widgets.DrawBoxSolid(new Rect(r.x + 3f, r.yMax - 2f, r.width - 6f, 2f), AlbumStyle.Theme.Accent);
            if (Mouse.IsOver(r)) Widgets.DrawBoxSolid(r, new Color(1f, 1f, 1f, 0.08f));
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = active ? Ink : CoverText;
            AlbumStyle.HeaderLabel(r.ContractedBy(6f), AlbumPresentation.Title(label));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            if (!tip.NullOrEmpty()) TooltipHandler.TipRegion(r, tip);
            return Widgets.ButtonInvisible(r);
        }

        internal static float HeaderRowHeight => Mathf.Max(36f, AlbumStyle.StyleFor("한글", GameFont.Small).fontSize * 1.65f);
        private float IncludeWidth(string key) => AlbumStyle.StyleFor(key.Translate(), GameFont.Small).CalcSize(new GUIContent(key.Translate())).x + 42f;
        private float IncludeSpace => IncludeWidth("MF_Phase2_IncludeBirthdays") + (Chronological ? IncludeWidth("MF_IncludeManual") + 10f : 0f);
        private bool SeparateIncludes(float width) => width < IncludeSpace + 480f;
        internal float HeaderHeight(float width) => (tab == Tab.Settlement || tab == Tab.Imported || tab == Tab.Archive ? 2f : filtersExpanded ? (SeparateIncludes(width) ? 6f : 5f) : 3f) * (HeaderRowHeight + 6f) + 4f;

        internal string FilterSummary()
        {
            var parts = new List<string>();
            if (filterKind != null) parts.Add(KindLabel(filterKind));
            if (filterPerson != null) parts.Add(filterPersonName);
            if (filterYear != 0) parts.Add("MF_YearLabel".Translate(filterYear));
            if (parts.Count == 0) parts.Add("MF_UI12_AllFilters".Translate());
            if (includeBirthdays) parts.Add("MF_UI12_WithBirthdays".Translate());
            else if (!favoritesOnly && filterPerson == null && (filterKind == null || !Kinds.PersonalOnly(filterKind)))
                parts.Add(hiddenBirthdays > 0 ? "MF_UI12_BirthdaysHidden".Translate(hiddenBirthdays).ToString() : "MF_UI12_WithoutBirthdays".Translate().ToString());
            if (Chronological) parts.Add((includeManual ? "MF_UI12_WithManual" : "MF_UI12_WithoutManual").Translate());
            if (!search.NullOrEmpty()) parts.Add("MF_UI12_SearchSummary".Translate(search));
            return string.Join(" / ", parts);
        }

        internal bool HasActiveFilters => filterKind != null || filterPerson != null || filterYear != 0 || favoritesOnly || !search.NullOrEmpty();
        internal void ShowAllPhotos()
        {
            ClearFilters();
            includeBirthdays = true;
        }

        internal static void OpenModSettings()
        {
            var mod = LoadedModManager.GetMod<MomentFramesMod>();
            if (mod != null) Find.WindowStack.Add(new Dialog_ModSettings(mod));
        }

        internal void ToggleFilterPanel()
        {
            filtersExpanded = !filtersExpanded;
            GUI.FocusControl(null);
            galleryLayoutScroll = Vector2.zero;
        }

        private void DrawHeader(Rect r)
        {
            float h = HeaderRowHeight, step = h + 6f;
            float[] tools = { CoverWidth("MF_UI12_Screen", 144f), CoverWidth("MF_Playtest_ModSettings", 144f), CoverWidth("MF_Phase2_AlbumTools", 176f), CoverWidth("MF_Learn_Title", 134f) };
            if (tools.Sum() + 24f > r.width - 200f) tools = new[] { 144f, 144f, 176f, 134f };
            float right = r.xMax - 6f;
            Rect NextTool(int i) { right -= tools[i]; var tool = new Rect(right, r.y, tools[i], h); right -= 6f; return tool; }
            Rect screenTool = NextTool(0), settingsTool = NextTool(1), albumTool = NextTool(2), guideTool = NextTool(3);
            Text.Font = GameFont.Medium; GUI.color = CoverText;
            AlbumStyle.BodyLabel(new Rect(r.x + 6f, r.y, guideTool.x - r.x - 12f, h), "MF_AlbumTitle".Translate());
            Text.Font = GameFont.Small; GUI.color = Color.white;
            if (CoverButton(screenTool, "MF_UI12_Screen".Translate(), false))
                Find.WindowStack.Add(new Dialog_AlbumScreen(ResetPlacement, SaveSettings));
            if (CoverButton(settingsTool, "MF_Playtest_ModSettings".Translate(), false)) OpenModSettings();
            if (CoverButton(albumTool, "MF_Phase2_AlbumTools".Translate(), false, "MF_Feedback_AlbumToolsHint".Translate())) ToolsMenu();
            if (CoverButton(guideTool, "MF_Learn_Title".Translate(), false)) PhotoGuide.Open();
            float x = r.x + 6f, y = r.y + step;
            float[] tabs = { CoverWidth("MF_Tab_Photos", 120f), CoverWidth("MF_Tab_Chronicle", 120f), CoverWidth("MF_Tab_Gallery", 120f), CoverWidth("MF_Phase2_SettlementTab", 170f), CoverWidth("MF_Imports_Tab", 160f) };
            // The tab exists only while something is in it: nothing changes for anyone who never uses it.
            float archiveTab = ArchiveTabWidth;
            if (tabs.Sum() + 36f + archiveTab > r.width - (peopleReturn != null ? 220f : 0f)) tabs = new[] { 120f, 120f, 120f, 170f, 160f };
            if (CoverButton(new Rect(x, y, tabs[0], h), "MF_Tab_Photos".Translate(), tab == Tab.Photos)) SetTab(Tab.Photos);
            x += tabs[0] + 6f;
            if (CoverButton(new Rect(x, y, tabs[1], h), "MF_Tab_Chronicle".Translate(), tab == Tab.Chronicle)) SetTab(Tab.Chronicle);
            x += tabs[1] + 6f;
            if (CoverButton(new Rect(x, y, tabs[2], h), "MF_Tab_Gallery".Translate(), tab == Tab.Gallery))
            {
                if (tab != Tab.Gallery) { galleryChronological = false; railFollow = true; SetTab(Tab.Gallery); }
            }
            x += tabs[2] + 6f;
            if (CoverButton(new Rect(x, y, tabs[3], h), "MF_Phase2_SettlementTab".Translate(), tab == Tab.Settlement)) SetTab(Tab.Settlement);
            x += tabs[3] + 6f;
            if (CoverButton(new Rect(x, y, tabs[4], h), "MF_Imports_Tab".Translate(), tab == Tab.Imported)) OpenImported();
            x += tabs[4] + 6f;
            if (archiveTab > 0f && CoverButton(new Rect(x, y, archiveTab, h), "MF_Archive_Tab".Translate(), tab == Tab.Archive)) ShowArchive();
            if (peopleReturn != null && CoverButton(new Rect(r.xMax - 214f, y, 208f, h), "MF_FavoriteBackPhoto".Translate(), false)) ReturnFromPeople();
            if (tab == Tab.Settlement || tab == Tab.Imported || tab == Tab.Archive) return;
            y += step; x = r.x + 6f;
            float favoriteWidth = Mathf.Max(170f, AlbumStyle.StyleFor("MF_FavoritesOnly".Translate(), GameFont.Small).CalcSize(new GUIContent("MF_FavoritesOnly".Translate())).x + 24f);
            float expandWidth = Mathf.Max(200f, IncludeWidth("MF_UI12_Expand"));
            Rect expand = new Rect(r.xMax - expandWidth - 6f, y, expandWidth, h);
            Rect favoriteToggle = new Rect(expand.x - favoriteWidth - 6f, y, favoriteWidth, h);
            float clearWidth = HasActiveFilters ? 144f : 0f;
            Rect summary = new Rect(x, y, Mathf.Max(1f, favoriteToggle.x - x - 10f - clearWidth), h);
            GUI.color = CoverText;
            AlbumStyle.HeaderLabel(summary, AlbumPresentation.Title(FilterSummary()));
            GUI.color = Color.white;
            TooltipHandler.TipRegion(summary, FilterSummary());
            if (clearWidth > 0f && CoverButton(new Rect(favoriteToggle.x - clearWidth - 6f, y, clearWidth, h), "MF_Playtest_AllPhotos".Translate(), false)) ShowAllPhotos();
            if (CoverButton(favoriteToggle, "MF_FavoritesOnly".Translate(), favoritesOnly))
            { favoritesOnly = !favoritesOnly; cacheValid = false; railFollow = true; page = 0; }
            // Closing this panel only hides controls; no filter or query is reset.
            if (CoverButton(expand, (filtersExpanded ? "MF_UI12_Collapse" : "MF_UI12_Expand").Translate(), filtersExpanded))
                ToggleFilterPanel();
            if (!filtersExpanded) return;
            y += step;
            bool separate = SeparateIncludes(r.width);
            float checkboxSpace = separate ? 0f : IncludeSpace;
            float filterWidth = Mathf.Max(96f, (r.width - checkboxSpace - 30f) / 3f);
            string kindLabel = "MF_Filter_Kind".Translate() + ": " + (filterKind == null ? "MF_All".Translate().ToString() : KindLabel(filterKind));
            if (CoverButton(new Rect(x, y, filterWidth, h), kindLabel, filterKind != null, kindLabel)) KindMenu();
            x += filterWidth + 6f;
            string personLabel = "MF_Filter_Person".Translate() + ": " + (filterPerson == null ? "MF_All".Translate().ToString() : filterPersonName);
            if (CoverButton(new Rect(x, y, filterWidth, h), personLabel, filterPerson != null, personLabel)) PersonMenu();
            x += filterWidth + 6f;
            string yearLabel = "MF_Filter_Year".Translate() + ": " + (filterYear == 0 ? "MF_All".Translate().ToString() : "MF_YearLabel".Translate(filterYear).ToString());
            if (CoverButton(new Rect(x, y, filterWidth, h), yearLabel, filterYear != 0, yearLabel)) YearMenu();
            x += filterWidth + 6f;
            if (separate) { y += step; x = r.x + 6f; }
            Rect birthdays = new Rect(x, y, IncludeWidth("MF_Phase2_IncludeBirthdays"), h);
            GUI.color = CoverText;
            AlbumStyle.HeaderLabel(new Rect(x, y, birthdays.width - 30f, h), "MF_Phase2_IncludeBirthdays".Translate());
            GUI.color = Color.white;
            Widgets.Checkbox(new Vector2(birthdays.xMax - 25f, y + (h - 24f) / 2f), ref includeBirthdays, 24f);
            if (Widgets.ButtonInvisible(new Rect(x, y, birthdays.width - 30f, h))) includeBirthdays = !includeBirthdays;
            x += birthdays.width + 10f;
            if (Chronological)
            {
                GUI.color = CoverText;
                Rect label = new Rect(x, y, IncludeWidth("MF_IncludeManual") - 30f, h);
                AlbumStyle.HeaderLabel(label, "MF_IncludeManual".Translate());
                GUI.color = Color.white;
                Widgets.Checkbox(new Vector2(label.xMax + 4f, y + (h - 24f) / 2f), ref includeManual, 24f);
                if (Widgets.ButtonInvisible(label)) includeManual = !includeManual;
            }
            y += step;
            float resetWidth=Mathf.Max(150f,AlbumStyle.StyleFor("MF_UX_ClearAll".Translate()).CalcSize(new GUIContent("MF_UX_ClearAll".Translate())).x+24f);
            Rect searchBox = new Rect(r.x + 6f, y, r.width - 62f-resetWidth, h);
            GUI.color = Color.white;
            string next = AlbumBridgeUI.TextField(searchBox, search ?? "", "MFAlbumSearch");
            if (next.Length > 200) next = next.Substring(0, 200);
            if (next != search) { search = next; cacheValid = false; railFollow = true; }
            if (search.NullOrEmpty() && !GUI.GetNameOfFocusedControl().Contains("MFAlbumSearch"))
            {
                GUI.color = new Color(.36f, .34f, .29f);
                AlbumStyle.HeaderLabel(searchBox.ContractedBy(8f), AlbumPresentation.Title("MF_Phase2_AlbumSearch".Translate()));
                GUI.color = Color.white;
                TooltipHandler.TipRegion(searchBox, "MF_Phase2_AlbumSearch".Translate());
            }
            if (CoverButton(new Rect(searchBox.xMax+8f,y,40f,h),"×",false,"MF_UX_ClearSearch".Translate()))ClearSearch();
            if(CoverButton(new Rect(r.xMax-resetWidth,y,resetWidth,h),"MF_UX_ClearAll".Translate(),false,"MF_DesignAlbum_ClearFilters".Translate()))ClearFilters();
        }

        private void ToolsMenu()
        {
            var options = new List<FloatMenuOption> {
                new FloatMenuOption("MF_StorageTitle".Translate(), () => Find.WindowStack.Add(new Dialog_PhotoStorage())),
                new FloatMenuOption("MF_Export".Translate(), Export),
                new FloatMenuOption("MF_OpenFolder".Translate(), () => {
                    string folder = PhotoStore.Instance?.FolderPath;
                    if (!folder.NullOrEmpty() && Directory.Exists(folder)) Application.OpenURL("file:///" + folder.Replace('\\', '/'));
                }),
                new FloatMenuOption("MF_Imports_Tab".Translate(), () => OpenImported(PhotoSource.Computer)),
                new FloatMenuOption("MF_Relics".Translate(), () => OpenImported(PhotoSource.Legacy)),
                new FloatMenuOption("MF_Extras_Remembrance".Translate(), () => Find.WindowStack.Add(new Dialog_MFRemembrance())),
                new FloatMenuOption("MF_Extras_OtherDays".Translate(), () => Find.WindowStack.Add(new Dialog_ExternalPhotos(true))),
                new FloatMenuOption("MF_Extras_RefreshFolder".Translate(), () => Find.WindowStack.Add(new Dialog_ExternalPhotos(false)))
            };
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void KindMenu()
        {
            var store = PhotoStore.Instance;
            var opts = new List<FloatMenuOption> { new FloatMenuOption("MF_All".Translate(), () => { filterKind = null; page = 0; }) };
            if (store != null)
                foreach (var k in store.Records.Where(x => x.HasFile && !PhotoSources.Imported(x)).Select(x => x.kind).Distinct().OrderBy(KindLabel))
                    opts.Add(new FloatMenuOption(KindLabel(k), () => { filterKind = k; page = 0; }));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private void PersonMenu()
        {
            var store = PhotoStore.Instance;
            var opts = new List<FloatMenuOption> { new FloatMenuOption("MF_All".Translate(), () => { filterPerson = null; filterPersonName = null; page = 0; }) };
            if (store != null)
            {
                foreach (var kv in PeopleIndex(store).OrderBy(kv => kv.Value))
                {
                    string id = kv.Key, name = kv.Value;
                    opts.Add(new FloatMenuOption(name, () => { filterPerson = id; filterPersonName = name; page = 0; }));
                }
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private void YearMenu()
        {
            var store = PhotoStore.Instance;
            var opts = new List<FloatMenuOption> { new FloatMenuOption("MF_All".Translate(), () => { filterYear = 0; page = 0; }) };
            if (store != null)
                foreach (int yr in store.Records.Where(x => x.HasFile && !PhotoSources.Imported(x) && x.year > 0).Select(x => x.year).Distinct().OrderByDescending(v => v))
                {
                    int y = yr;
                    opts.Add(new FloatMenuOption("MF_YearLabel".Translate(y), () => { filterYear = y; page = 0; }));
                }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private void Export() => AlbumExport.Open(PhotoStore.Instance);

        // ---------- photos: two paper pages ----------

        private void DrawPhotos(Rect body, List<PhotoRecord> list)
        {
            float pageW = (body.width - Spine) / 2f;
            Rect left = new Rect(body.x, body.y, pageW, body.height);
            Rect right = new Rect(body.x + pageW + Spine, body.y, pageW, body.height);
            DrawPaper(left, false);
            DrawPaper(right, true);
            // Spine shadow.
            Widgets.DrawBoxSolid(new Rect(left.xMax, body.y, Spine, body.height), new Color(0f, 0f, 0f, 0.35f));
            int pages = Mathf.Max(1, (list.Count + PerSpread - 1) / PerSpread);
            page = Mathf.Clamp(page, 0, pages - 1);
            int first = page * PerSpread;
            if (list.Count == 0)
            {
                DrawEmpty(left, InkFaint);
                return;
            }
            for (int i = 0; i < PerSpread; i++)
            {
                int idx = first + i;
                if (idx >= list.Count) break;
                bool onRight = i >= Cols * Rows;
                int slot = onRight ? i - Cols * Rows : i;
                Rect pg = onRight ? right : left;
                float cellW = (pg.width - 30f) / Cols, cellH = (pg.height - 30f) / Rows;
                Rect cell = new Rect(pg.x + 15f + (slot % Cols) * cellW, pg.y + 15f + (slot / Cols) * cellH, cellW, cellH);
                DrawCard(cell, list[idx], idx);
            }
        }

        private static void DrawPaper(Rect r, bool flip)
        {
            AlbumStyle.DrawPaper(r, flip, Opacity);
            AlbumStyle.DrawWash(new Rect(r.x + 8f, r.y + 8f, Mathf.Min(220f, r.width - 16f), Mathf.Min(110f, r.height - 16f)), Opacity);
        }

        /// <summary>Stable small angle per photo (polaroid look); 0 when the album-tilt setting is off.</summary>
        /// <summary>One chronicle row, for the album's chronicle and a colonist's own tab alike: the tilted print, the title, what, when and who,
        /// and the note. One drawing for both, because two copies had drifted (the colonist tab kept a photo's original title after its shown
        /// edit was given one of its own). row = the area right of the time line; people = the names line, or null. Clicks are the caller's.</summary>
        internal static void DrawChronicleRow(Rect row, PhotoRecord rec, PhotoStore store, float thumbW, string people)
        {
            ChronicleNarrative.DrawRow(row,rec,store,thumbW,people);
        }

        public static float TiltOf(PhotoRecord rec)
        {
            if (MomentFramesMod.Settings != null && !MomentFramesMod.Settings.albumTilt) return 0f;
            int h = rec.id.GetHashCode();
            return ((h & 0xFF) / 255f - 0.5f) * 5.5f;   // -2.75 .. +2.75 degrees, stable per photo
        }

        private static string Tip(PhotoRecord rec) =>
            rec.DisplayTitle + "\n" + rec.dateLabel + (rec.people.Count > 0 ? "\n" + (rec.kind == PetPhotos.Kind || rec.kind == PetMilestones.Kind ? "MF_PetSubjects" : "MF_People").Translate(string.Join(", ", rec.people)) : "")
            +(AlbumPresentation.EventPeople(rec).NullOrEmpty()?"":"\n"+AlbumPresentation.EventPeople(rec))
            + (rec.DisplayDesc.NullOrEmpty() ? "" : "\n\n" + rec.DisplayDesc) + (rec.Delayed ? "\n* " + "MF_Delayed".Translate() : "");

        private void DrawCard(Rect cell, PhotoRecord rec, int index)
        {
            var store = PhotoStore.Instance;
            Texture2D tex = PhotoTextureCache.ThumbDeferred(store.ThumbPath(rec), store.FilePath(rec));
            float aspect = tex != null && tex.height > 0 ? (float)tex.width / tex.height : rec.Aspect;
            float titleH = AlbumPresentation.TitleHeight, dateH = AlbumPresentation.DateHeight;
            float captionH = titleH + dateH + 12f;
            float maxW = Mathf.Max(1f, cell.width - 2f * Border - 24f), maxH = Mathf.Max(1f, cell.height - Border - captionH - 30f);
            Rect photoArea = new Rect(cell.center.x - maxW / 2f, cell.y + 22f, maxW, maxH);
            float w = maxW, h = w / aspect;
            if (h > maxH) { h = maxH; w = h * aspect; }
            Rect photo = new Rect(cell.center.x - w / 2f, cell.y + 22f + (maxH - h) / 2f, w, h);
            Rect card = new Rect(photoArea.x - Border, photoArea.y - Border, photoArea.width + 2f * Border, photoArea.height + Border + captionH);
            float tilt = TiltOf(rec);
            Matrix4x4 m = GUI.matrix;
            try
            {
                UI.RotateAroundPivot(tilt, card.center);
                // Shadow, card, photo.
                var shots = rec.Archived ? null : Bursts(Filtered()).Shots(rec);
                if (shots?.Count > 1)
                {
                    Widgets.DrawBoxSolid(new Rect(card.x + 6f, card.y + 6f, card.width, card.height), AlbumStyle.Theme.MutedInk);
                    Widgets.DrawBoxSolid(new Rect(card.x + 3f, card.y + 3f, card.width, card.height), Cream);
                }
                Widgets.DrawBoxSolid(new Rect(card.x + 3f, card.y + 4f, card.width, card.height), new Color(0f, 0f, 0f, 0.22f));
                Widgets.DrawBoxSolid(card, Cream);
                if (tex != null) GUI.DrawTexture(photo, tex, ScaleMode.StretchToFill);
                else
                {
                    Widgets.DrawBoxSolid(photo, AlbumStyle.Theme.Mat);
                    GUI.color = InkFaint;
                    AlbumStyle.Label(photo.ContractedBy(8f), (PhotoThumbnailLoader.Pending(store.ThumbPath(rec)) ? "MF_BurstLoading" : "MF_UX_MissingShort").Translate());
                    GUI.color = Color.white;
                }
                if (shots?.Count > 1)
                    BurstBadge(new Rect(photo.xMax - 84f, photo.yMax - 36f, 80f, 32f), shots.Count);
                if (Mouse.IsOver(card)) Widgets.DrawBoxSolid(photo, new Color(1f, 1f, 1f, 0.08f));
                // Caption: title, then date (and a delayed mark).
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperCenter;
                GUI.color = Ink;
                AlbumPresentation.CardTitle(new Rect(card.x + 7f, photoArea.yMax + 4f, card.width - 14f, titleH), rec.DisplayTitle ?? "");
                Text.Font = GameFont.Tiny;
                GUI.color = InkFaint;
                AlbumPresentation.CardDate(new Rect(card.x + 7f, photoArea.yMax + titleH + 6f, card.width - 14f, dateH), rec.dateLabel + (rec.Delayed ? " *" : "")
                    + (rec.edits.Count > 0 ? "  \u270E" + rec.edits.Count : ""));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                // Tape across the top edge.
                Matrix4x4 m2 = GUI.matrix;
                Rect tape = new Rect(card.center.x - 40f, card.y - 10f, 80f, Tape);
                UI.RotateAroundPivot(-7f, tape.center);
                GUI.DrawTexture(tape, MFIcons.Tape, ScaleMode.StretchToFill);
                GUI.matrix = m2;
            }
            finally
            {
                GUI.matrix = m;
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
            }
            if (Mouse.IsOver(card)) TooltipHandler.TipRegion(card, Tip(rec));   // the text is built only for the card under the mouse
            if (!rec.Archived && AlbumFavorites.Button(new Rect(card.xMax - 36f, card.y - 8f, 36f, 36f), rec.favorite, true))
            { AlbumFavorites.Toggle(store, rec); return; }
            if (Widgets.ButtonInvisible(card))
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                ShowGallery(rec, false);
            }
        }

        // ---------- chronicle: one wide page, milestones in order ----------

        private void DrawChronicle(Rect body, List<PhotoRecord> list)
        {
            DrawPaper(body, false);
            Rect inner = new Rect(body.x + 40f, body.y + 20f, body.width - 80f, body.height - 40f);
            if (list.Count == 0)
            {
                DrawEmpty(inner, InkFaint);
                return;
            }
            const float RowH = 100f, YearH = 34f, ThumbW = 120f;
            if (!ReferenceEquals(groupedSource, list) || groupedRows == null)
            {
                groupedSource = list;
                groupedRows = ChronicleGroups.Build(list, PhotoStore.Instance.Settlement.Snapshots, expandedSurveys);
            }
            float width = inner.width - 16f;
            if (!ReferenceEquals(measuredRows, groupedRows) || chronicleWidth != width || chronicleFont != AlbumStyle.EffectiveFont
                || chronicleScale != AlbumStyle.Scale || chronicleRevision != ChronicleNarrative.Revision)
            {
                measuredRows = groupedRows; chronicleWidth = width; chronicleFont = AlbumStyle.EffectiveFont;
                chronicleScale = AlbumStyle.Scale; chronicleRevision = ChronicleNarrative.Revision;
                chronicleHeights = new float[groupedRows.Count]; chronicleHeight = 0f;
                int measuredYear = int.MinValue;
                for (int i = 0; i < groupedRows.Count; i++)
                {
                    var item = groupedRows[i];
                    if (item.Record.year != measuredYear) { chronicleHeight += YearH; measuredYear = item.Record.year; }
                    chronicleHeights[i] = item.Key != null ? RowH : ChronicleNarrative.RowHeight(item.Record, width - 108f, ThumbW);
                    chronicleHeight += chronicleHeights[i];
                }
            }
            float total = chronicleHeight;
            int lastYear = int.MinValue;
            Rect view = new Rect(0f, 0f, inner.width - 16f, total);
            Widgets.BeginScrollView(inner, ref chronicleScroll, view);
            float y = 0f;
            lastYear = int.MinValue;
            float lineX = 92f;
            Color lineInk = InkFaint;
            lineInk.a = 0.6f;
            Widgets.DrawBoxSolid(new Rect(lineX, 0f, 2f, total), lineInk);
            var store = PhotoStore.Instance;
            for (int rowIndex = 0; rowIndex < groupedRows.Count; rowIndex++)
            {
                var item = groupedRows[rowIndex];
                var rec = item.Record;
                float itemHeight = chronicleHeights[rowIndex];
                if (rec.year != lastYear)
                {
                    lastYear = rec.year;
                    Text.Font = GameFont.Medium;
                    GUI.color = Ink;
                    // ToString(): a TaggedString would get the game's automatic date colouring (sky blue) instead of ink.
                    AlbumStyle.Label(new Rect(0f, y + 2f, 86f, YearH - 2f), "MF_YearLabel".Translate(rec.year).ToString());
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                    y += YearH;
                }
                if (y + itemHeight >= chronicleScroll.y - RowH && y <= chronicleScroll.y + inner.height + RowH)
                {
                    Rect row = new Rect(lineX + 16f, y, view.width - lineX - 16f, itemHeight);
                    // Dot on the line.
                    Widgets.DrawBoxSolid(new Rect(lineX - 3f, y + itemHeight / 2f - 4f, 8f, 8f), AlbumStyle.Theme.Accent);
                    if (item.Key != null)
                    {
                        var representative = item.Photos.FirstOrDefault(p => p.kind == Kinds.SettlementOverview) ?? rec;
                        var thumbnail = PhotoTextureCache.ThumbDeferred(store.ThumbPath(representative), store.FilePath(representative));
                        if (thumbnail != null) GUI.DrawTexture(new Rect(row.x, row.y + 8f, ThumbW, RowH - 16f), thumbnail, ScaleMode.ScaleToFit);
                        string label = AlbumPresentation.Title("MF_FavoriteSurveyGroup".Translate(item.Stamp / 4, item.Stamp % 4 + 1, item.Photos.Count));
                        float ax = row.x + ThumbW + 22f, ay = row.y + 30f;
                        bool open = expandedSurveys.Contains(item.Key);
                        Widgets.DrawLine(new Vector2(ax - 5f, ay - 5f), new Vector2(ax + (open ? 0f : 5f), ay + (open ? 5f : 0f)), Ink, 2f);
                        Widgets.DrawLine(new Vector2(ax + (open ? 0f : 5f), ay + (open ? 5f : 0f)), new Vector2(ax + (open ? 5f : -5f), ay + (open ? -5f : 5f)), Ink, 2f);
                        GUI.color = Ink;
                        AlbumStyle.HeaderLabel(new Rect(row.x + ThumbW + 38f, row.y + 12f, row.width - ThumbW - 42f, 40f), label);
                        GUI.color = InkFaint;
                        AlbumStyle.Label(new Rect(row.x + ThumbW + 16f, row.y + 54f, row.width - ThumbW - 20f, 36f), PhotoLocations.MapLabel(rec.mapId, store));
                        GUI.color = Color.white;
                        if (Widgets.ButtonInvisible(row))
                        {
                            if (!expandedSurveys.Add(item.Key)) expandedSurveys.Remove(item.Key);
                            groupedSource = null; // rebuild on the next GUI pass, not during enumeration
                        }
                    }
                    else
                    {
                        DrawChronicleRow(row, rec, store, ThumbW, rec.people.Count > 0 ? string.Join(", ", rec.people) : null);
                        if (!rec.eventOnly && Widgets.ButtonInvisible(row)) ShowGallery(rec, true);
                    }
                }
                y += itemHeight;
            }
            Widgets.EndScrollView();
        }

        // ---------- gallery: one big photo, the rail, the sentence ----------

        private void ReleaseHeld()
        {
            foreach (var h in held)
                if (heldOwnership.TryGetValue(h, out var material) && PhotoTextureCache.Owns(h, material)) PhotoTextureCache.Release(h);
            held.Clear();
            heldOwnership.Clear();
        }

        /// <summary>Loads only the visible original. The bounded idle cache makes tab return and recent stepping reuse it.</summary>
        private Texture2D Hold(List<PhotoRecord> list, int idx)
        {
            var store = PhotoStore.Instance;
            if (store == null) return null;
            var want = new List<string>(1);
            string current = store.VersionPath(list[idx], galleryVersion);
            if (!current.NullOrEmpty()) want.Add(current);
            for (int i = held.Count - 1; i >= 0; i--)
                if (!want.Contains(held[i]) || !heldOwnership.TryGetValue(held[i], out var material) || !PhotoTextureCache.Owns(held[i], material))
                {
                    if (heldOwnership.TryGetValue(held[i], out material) && PhotoTextureCache.Owns(held[i], material)) PhotoTextureCache.Release(held[i]);
                    heldOwnership.Remove(held[i]); held.RemoveAt(i);
                }
            foreach (var w in want)
                if (!held.Contains(w))
                {
                    var material = PhotoTextureCache.Acquire(w);
                    if (material != null) { held.Add(w); heldOwnership[w] = material; }
                }
            return PhotoTextureCache.Peek(current);
        }

        private void Go(List<PhotoRecord> list, int idx)
        {
            idx = Mathf.Clamp(idx, 0, list.Count - 1);
            if (!SelectGalleryPhoto(list[idx], true)) return;
            SoundDefOf.PageChange.PlayOneShotOnCamera();
        }

        internal bool SelectGalleryPhoto(PhotoRecord rec, bool follow)
        {
            if (rec == null || rec.id == galleryId) return false;
            galleryId = rec.id;
            galleryVersion = null;
            railFollow = follow;
            bookAnchor = rec.id;
            return true;
        }

        internal static void GalleryLayout(Rect body, out Rect photoArea, out Rect info, out Rect rail)
        {
            const float gap = 12f;
            float railW = Mathf.Clamp(body.width * 0.26f, 260f, RailW);
            float infoH = GalleryInfoHeight(body.width - railW - gap);
            photoArea = new Rect(body.x, body.y, body.width - railW - gap, body.height - infoH - gap);
            info = new Rect(body.x, photoArea.yMax + gap, photoArea.width, infoH);
            rail = new Rect(photoArea.xMax + gap, body.y, railW, body.height);
        }

        private void DrawGallery(Rect body, List<PhotoRecord> list)
        {
            // Short windows still expose the photo and every action at the chosen font size.
            float contentWidth = body.width - 18f;
            float minimum = GalleryInfoHeight(contentWidth - Mathf.Clamp(contentWidth * .26f, 260f, RailW) - 12f) + 192f;
            var current = list.FirstOrDefault(p => p.id == galleryId) ?? list.FirstOrDefault();
            if (Bursts(list).Shots(current)?.Count > 1) minimum += BurstBandH;
            if (current?.edits.Count > 0) minimum += VersionHeight(current) + 6f;
            if (body.height >= minimum || list.Count == 0) { DrawGalleryContent(body, list); return; }
            Rect view = new Rect(0f, 0f, body.width - 18f, minimum);
            Widgets.BeginScrollView(body, ref galleryLayoutScroll, view);
            DrawGalleryContent(view, list);
            Widgets.EndScrollView();
        }

        private void DrawGalleryContent(Rect body, List<PhotoRecord> list)
        {
            if (list.Count == 0)
            {
                DrawEmpty(body, CoverText);
                return;
            }
            int idx = list.FindIndex(r => r.id == galleryId);
            if (idx < 0) { idx = 0; SelectGalleryPhoto(list[0], true); }
            GalleryLayout(body, out Rect photoArea, out Rect info, out Rect rail);
            // Keys and the wheel over the photo step through the list.
            var ev = Event.current;
            int step = 0;
            if (ev.type == EventType.KeyDown)
            {
                if (ev.keyCode == KeyCode.LeftArrow || ev.keyCode == KeyCode.PageUp) step = -1;
                else if (ev.keyCode == KeyCode.RightArrow || ev.keyCode == KeyCode.PageDown) step = 1;
                if (step != 0) ev.Use();
            }
            else if (ev.type == EventType.ScrollWheel && Mouse.IsOver(photoArea)
                && !(list[idx].edits.Count > 0 && VersionsOpen(list[idx]) && Mouse.IsOver(VersionBandRect(VersionAreaOf(list, idx, photoArea)))))
            {
                step = ev.delta.y > 0f ? 1 : -1;
                ev.Use();
            }
            if (step != 0) { Go(list, idx + step); idx = Mathf.Clamp(idx + step, 0, list.Count - 1); }
            var rec = list[idx];
            if (!galleryVersion.NullOrEmpty() && rec.Edit(galleryVersion) == null) galleryVersion = null;   // deleted meanwhile
            // Only the mount follows the theme; the photograph keeps its original colours.
            Widgets.DrawBoxSolid(photoArea, AlbumStyle.Theme.Mat);
            Texture2D tex = Hold(list, idx);
            Rect mount = photoArea.ContractedBy(10f);
            var sessionShots = Bursts(list).Shots(rec);
            bool hasBurst = sessionShots?.Count > 1;
            Rect burstBand = new Rect(photoArea.x + 10f, photoArea.yMax - BurstBandH - 8f, photoArea.width - 20f, BurstBandH);
            Rect versionArea = VersionAreaOf(list, idx, photoArea);
            if (hasBurst) mount.height = Mathf.Max(1f, mount.height - BurstBandH - 6f);
            if (rec.edits.Count > 0) mount.height = Mathf.Max(1f, mount.height - VersionHeight(rec) - 6f);
            if (tex != null && rec.stickers != null && rec.stickers.Count > 0)
            {
                Rect image = AlbumStickers.ImageRect(mount, tex.width / (float)tex.height);
                GUI.DrawTexture(image, tex, ScaleMode.StretchToFill);
                AlbumStickers.Draw(rec, mount, image);
            }
            else if (tex != null) Widgets.DrawTextureFitted(mount, tex, 1f);
            else
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = Ink;
                Rect missingText = new Rect(mount.x + 52f, mount.center.y - 74f, Mathf.Max(40f, mount.width - 104f), 72f);
                AlbumStyle.Label(missingText, "MF_StorageUnavailable".Translate());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                if (SharedPhotoUI.Button(new Rect(mount.center.x - 120f, mount.center.y + 14f, 240f, SharedPhotoUI.ControlHeight), "MF_StorageTitle".Translate()))
                    Find.WindowStack.Add(new Dialog_PhotoStorage(rec.id));
            }
            // Previous / next strips.
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            if (idx > 0)
            {
                Rect l = new Rect(photoArea.x, photoArea.y, 52f, mount.yMax - photoArea.y);
                if (Mouse.IsOver(l)) Widgets.DrawBoxSolid(l, new Color(1f, 1f, 1f, 0.07f));
                GUI.color = Ink;
                AlbumStyle.Label(l, "<");
                if (Widgets.ButtonInvisible(l)) Go(list, idx - 1);
            }
            if (idx < list.Count - 1)
            {
                Rect rr = new Rect(photoArea.xMax - 52f, photoArea.y, 52f, mount.yMax - photoArea.y);
                Rect nextHit = GalleryNextHit(rr, GalleryZoomRect(photoArea));
                if (Mouse.IsOver(nextHit)) Widgets.DrawBoxSolid(nextHit, new Color(1f, 1f, 1f, 0.07f));
                GUI.color = Ink;
                AlbumStyle.Label(rr, ">");
                if (Widgets.ButtonInvisible(nextHit)) Go(list, idx + 1);
            }
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            if (rec.edits.Count > 0) DrawVersions(versionArea, rec);
            if (hasBurst) DrawBurst(burstBand, rec, sessionShots);
            Rect zoomButton = GalleryZoomRect(photoArea);
            if (FitButton(zoomButton, "MF_Feedback_Zoom".Translate(), rec.HasFile)) Find.WindowStack.Add(new Dialog_MFPhotoZoom(rec, galleryVersion));
            DrawInfo(info, rec, idx, list.Count);
            DrawRail(rail, list, idx);
        }

        internal static Rect GalleryZoomRect(Rect photoArea)
        {
            float width = Mathf.Max(98f, AlbumStyle.StyleFor("MF_Feedback_Zoom".Translate(), GameFont.Small).CalcSize(new GUIContent("MF_Feedback_Zoom".Translate())).x + 20f);
            return new Rect(photoArea.xMax - width - 10f, photoArea.y + 10f, width, Mathf.Max(32f, HeaderRowHeight));
        }

        /// <summary>The next-photo strip's clickable part: controls take the click in drawing order, so it must not reach under the zoom button.</summary>
        internal static Rect GalleryNextHit(Rect strip, Rect zoom)
        {
            float top = Mathf.Min(strip.yMax, Mathf.Max(strip.y, zoom.yMax));
            return new Rect(strip.x, top, strip.width, strip.yMax - top);
        }

        private const float VersionThumbH = 40f, VersionCellW = 110f;
        private static float BurstBandH => SharedPhotoUI.ControlHeight + 80f;

        private static void BurstBadge(Rect area, int count)
        {
            Widgets.DrawBoxSolid(area, AlbumStyle.Theme.Paper);
            GUI.color = Ink;
            AlbumStyle.Label(area.ContractedBy(3f), "MF_BurstCount".Translate(count));
            GUI.color = Color.white;
        }

        private void DrawBurst(Rect band, PhotoRecord rec, List<PhotoRecord> shots)
        {
            var store = PhotoStore.Instance;
            GUI.color = Ink;
            float controls = SharedPhotoUI.ControlHeight;
            AlbumStyle.HeaderLabel(new Rect(band.x, band.y, band.width - 186f, controls), AlbumPresentation.Title("MF_BurstTitle".Translate(shots.Count)));
            GUI.color = Color.white;
            bool representative = Bursts(Filtered()).Representative(rec)?.id == rec.id;
            if (AlbumStyle.Button(new Rect(band.xMax - 180f, band.y, 180f, controls),
                (representative ? "MF_BurstRepresentative" : "MF_BurstSetRepresentative").Translate(), !representative, preserveSize: true) && !representative)
                PhotoBursts.SetRepresentative(store, rec);
            Rect viewport = new Rect(band.x, band.y + controls + 4f, band.width, 76f);
            Rect content = new Rect(0, 0, shots.Count * 104f, 60f);
            Widgets.BeginScrollView(viewport, ref burstScroll, content);
            for (int i = 0; i < shots.Count; i++)
            {
                if ((i + 1) * 104f < burstScroll.x || i * 104f > burstScroll.x + viewport.width) continue;
                var shot = shots[i]; Rect cell = new Rect(i * 104f, 0, 98f, 60f);
                Widgets.DrawBoxSolid(cell, shot.id == rec.id ? AlbumStyle.Theme.Accent : AlbumStyle.Theme.Mat);
                var texture = PhotoTextureCache.ThumbDeferred(store.ThumbPath(shot), store.FilePath(shot));
                if (texture != null) GUI.DrawTexture(cell.ContractedBy(4f), texture, ScaleMode.ScaleToFit);
                TooltipHandler.TipRegion(cell, shot.DisplayTitle + "\n" + shot.dateLabel);
                if (Widgets.ButtonInvisible(cell) && SelectGalleryPhoto(shot, true)) SoundDefOf.PageChange.PlayOneShotOnCamera();
            }
            Widgets.EndScrollView();
        }
        private static float VersionBandH => SharedPhotoUI.ControlHeight+120f;

        /// <summary>Folded to one bar unless opened, so edited photos keep their size. Viewing a version other than the one in use keeps it open.</summary>
        private bool VersionsOpen(PhotoRecord rec)
        {
            if (versionsOpenFor != rec.id) { versionsOpenFor = rec.id; versionsOpen = false; }
            string used = rec.Shown?.id ?? "";
            if (galleryVersion != null && galleryVersion != used && (galleryVersion.Length == 0 || rec.Edit(galleryVersion) != null)) versionsOpen = true;
            return versionsOpen;
        }

        private float VersionHeight(PhotoRecord rec) => VersionsOpen(rec) ? VersionBandH : SharedPhotoUI.ControlHeight;

        private Rect VersionAreaOf(List<PhotoRecord> list, int index, Rect photoArea)
        {
            if (Bursts(list).Shots(list[index])?.Count > 1) photoArea.height -= BurstBandH + 6f;
            return photoArea;
        }

        private void DrawVersionBar(Rect photoArea, PhotoRecord rec)
        {
            float h = SharedPhotoUI.ControlHeight;
            Rect bar = new Rect(photoArea.x + 10f, photoArea.yMax - h - 8f, photoArea.width - 20f, h);
            string label = "+ " + "MF_EditCount".Translate(rec.edits.Count) + " / " + "MF_DesignAlbum_UsedVersion".Translate(VersionLabel(rec, rec.Shown?.id ?? ""));
            if (FitButton(bar, AlbumPresentation.Title(label))) versionsOpen = true;
            TooltipHandler.TipRegion(bar, "MF_DesignAlbum_VersionHint".Translate());
        }

        internal static Rect VersionBandRect(Rect photoArea) =>
            new Rect(photoArea.x + 10f, photoArea.yMax - VersionBandH - 8f, photoArea.width - 20f, VersionBandH);

        internal static Rect VersionViewportRect(Rect band) =>
            new Rect(band.x + 4f, band.y + SharedPhotoUI.ControlHeight+38f, band.width - 8f, 82f);

        internal static Rect VersionContentRect(int count) => new Rect(0f, 0f, count * VersionCellW + 8f, VersionThumbH + 24f);

        private static string VersionLabel(PhotoRecord rec, string version)
        {
            var edit = version.NullOrEmpty() ? null : rec.Edit(version);
            if (edit == null) return "MF_VersionOriginal".Translate();
            string label = edit.title.NullOrEmpty() ? "MF_VersionEdit".Translate(rec.edits.IndexOf(edit) + 1).ToString() : edit.title;
            if (edit.filter != PhotoFilters.None) label += " · " + PhotoFilters.Label(edit.filter);
            return label;
        }

        /// <summary>Versions have their own scroll view below the full picture. The text names the viewed and used versions separately:
        /// clicking a thumbnail only views it; the More menu changes the version used by the album and frames.</summary>
        private void DrawVersions(Rect photoArea, PhotoRecord rec)
        {
            var store = PhotoStore.Instance;
            if (store == null) return;
            if (!VersionsOpen(rec)) { DrawVersionBar(photoArea, rec); return; }
            int n = rec.edits.Count + 1;
            Rect band = VersionBandRect(photoArea), viewport = VersionViewportRect(band), content = VersionContentRect(n);
            Color bandColor = AlbumStyle.Theme.Cover;
            bandColor.a = 0.95f;
            Widgets.DrawBoxSolid(band, bandColor);
            string viewing = galleryVersion ?? rec.Shown?.id ?? "";
            string shown = rec.Shown?.id ?? "";
            if (versionPhotoId != rec.id)
            {
                versionPhotoId = rec.id;
                versionViewing = null;
                versionScroll = Vector2.zero;
            }
            if (versionViewing != viewing)
            {
                int at = viewing.NullOrEmpty() ? 0 : rec.edits.FindIndex(e => e.id == viewing) + 1;
                float x = at * VersionCellW;
                if (x < versionScroll.x) versionScroll.x = x;
                else if (x + VersionCellW > versionScroll.x + viewport.width) versionScroll.x = x + VersionCellW - viewport.width;
                versionViewing = viewing;
            }
            float maxScroll = Mathf.Max(0f, content.width - viewport.width);
            var ev = Event.current;
            if (ev.type == EventType.ScrollWheel && Mouse.IsOver(viewport))
            {
                versionScroll.x += (ev.delta.x + ev.delta.y) * 35f;
                ev.Use();
            }
            versionScroll.x = Mathf.Clamp(versionScroll.x, 0f, maxScroll);
            versionScroll.y = 0f;
            Text.Font = GameFont.Tiny;
            GUI.color = CoverText;
            Rect statusRect = new Rect(band.x + 8f, band.y + 2f, band.width - 58f, 30f);
            if (SharedPhotoUI.Button(new Rect(band.xMax - 46f, band.y + 2f, 38f, 28f), "−")) { versionsOpen = false; galleryVersion = null; }
            string status = "MF_DesignAlbum_VersionStatus".Translate(VersionLabel(rec, viewing), VersionLabel(rec, shown));
            Rect viewingRect = new Rect(statusRect.x, statusRect.y, statusRect.width * 0.4f - 8f, statusRect.height);
            Rect usedRect = new Rect(statusRect.x + statusRect.width * 0.4f, statusRect.y, statusRect.width * 0.6f, statusRect.height);
            AlbumStyle.Label(viewingRect, "MF_DesignAlbum_ViewingVersion".Translate(VersionLabel(rec, viewing)).ToString());
            AlbumStyle.Label(usedRect, AlbumPresentation.Title("MF_DesignAlbum_UsedVersion".Translate(VersionLabel(rec, shown))));
            TooltipHandler.TipRegion(statusRect, status + "\n" + "MF_DesignAlbum_VersionHint".Translate());
            GUI.color = Color.white;
            if(SharedPhotoUI.Button(new Rect(band.x+8,band.y+32,band.width-16,SharedPhotoUI.ControlHeight),AlbumPresentation.Title((viewing==shown?"MF_UX_VersionApplied":"MF_UX_ApplyVersion").Translate()),viewing!=shown))
                store.SetShown(rec,viewing.NullOrEmpty()?null:viewing);
            Text.Anchor = TextAnchor.UpperCenter;
            Widgets.BeginScrollView(viewport, ref versionScroll, content);
            int first = Mathf.Max(0, (int)(versionScroll.x / VersionCellW));
            int last = Mathf.Min(n, (int)((versionScroll.x + viewport.width) / VersionCellW) + 1);
            for (int i = first; i < last; i++)
            {
                string version = i == 0 ? "" : rec.edits[i - 1].id;
                string label = VersionLabel(rec, version);
                Rect cell = new Rect(4f + i * VersionCellW, 2f, VersionCellW - 8f, VersionThumbH + 20f);
                Rect thumbRect = new Rect(cell.x + 5f, cell.y, cell.width - 10f, VersionThumbH);
                var tex = PhotoTextureCache.Thumb(store.VersionPath(rec, version, true), store.VersionPath(rec, version));
                if (tex != null) Widgets.DrawTextureFitted(thumbRect, tex, 1f);
                if (version == viewing)
                {
                    GUI.color = AlbumStyle.Theme.Accent;
                    Widgets.DrawBox(thumbRect.ExpandedBy(2f), 2);
                }
                GUI.color = version == shown ? AlbumStyle.Theme.Accent : CoverText;
                AlbumStyle.Label(new Rect(cell.x, thumbRect.yMax + 1f, cell.width, 18f), (version == shown ? "\u2605 " : "") + label);
                GUI.color = Color.white;
                if (Mouse.IsOver(cell)) Widgets.DrawHighlight(thumbRect);
                TooltipHandler.TipRegion(cell, label + "\n" + (version == viewing ? "MF_DesignAlbum_Viewing".Translate() + "\n" : "")
                    + (version == shown ? "MF_DesignAlbum_Used".Translate() + "\n" : "") + "MF_DesignAlbum_VersionHint".Translate());
                if (Widgets.ButtonInvisible(cell)) galleryVersion = version;
            }
            Widgets.EndScrollView();
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        internal static void GalleryInfoLayout(Rect info, out Rect title, out Rect metadata, out Rect story, out Rect actions)
        {
            Rect inner = info.ContractedBy(14f);
            float titleH = GalleryTitleHeight, metadataH = HeaderRowHeight * 2f, actionH = GalleryActionHeight(inner.width) * 2f + 8f;
            title = new Rect(inner.x, inner.y, inner.width - 80f, titleH);
            metadata = new Rect(inner.x, title.yMax, inner.width, metadataH);
            actions = new Rect(inner.x, inner.yMax - actionH, inner.width, actionH);
            story = new Rect(inner.x, metadata.yMax + 2f, inner.width, Mathf.Max(1f, actions.y - metadata.yMax - 10f));
        }

        internal static Rect GalleryActionRect(Rect actions, int column, int columns)
        {
            float width = (actions.width - 8f * (columns - 1)) / columns;
            return new Rect(actions.x + column * (width + 8f), actions.y, width, actions.height);
        }

        private void DrawPeopleLinks(Rect rect, PhotoRecord rec)
        {
            if (rec.people.Count == 0) return;
            float x = rect.x;
            for (int i = 0; i < rec.people.Count; i++)
            {
                string name = rec.people[i], id = i < rec.peopleIds.Count ? rec.peopleIds[i] : name;
                float width = Mathf.Clamp(AlbumStyle.StyleFor(name, GameFont.Small).CalcSize(new GUIContent(name)).x + 22f, 65f, 180f);
                if (x + width > rect.xMax - 50f && i < rec.people.Count - 1)
                {
                    if (FitButton(new Rect(x, rect.y, rect.xMax - x, rect.height), "…"))
                        Find.WindowStack.Add(new FloatMenu(rec.people.Select((n, at) => new FloatMenuOption(n,
                            () => FollowPerson(rec, at < rec.peopleIds.Count ? rec.peopleIds[at] : n, n))).ToList()));
                    break;
                }
                Rect button = new Rect(x, rect.y, Mathf.Min(width, rect.xMax - x), rect.height);
                if (FitButton(button, name)) FollowPerson(rec, id, name);
                TooltipHandler.TipRegion(button, "MF_FavoritePersonHint".Translate(name));
                x += width + 6f;
            }
        }

        internal void FollowPerson(PhotoRecord origin, string id, string name)
        {
            if (origin == null || id.NullOrEmpty()) return;
            if (peopleReturn == null || !ReferenceEquals(peopleReturn.Store, PhotoStore.Instance))
            {
                peopleReturn = new FilterSnapshot { Store = PhotoStore.Instance, Kind = filterKind, Person = filterPerson,
                    PersonName = filterPersonName, Year = filterYear, Manual = includeManual, Birthdays = includeBirthdays,
                    Favorites = favoritesOnly, Search = search, Chrono = galleryChronological, Contextual = contextualFilters };
                peopleReturnPhoto = origin.id; peopleReturnVersion = galleryVersion;
            }
            filterKind = null; filterYear = 0; search = ""; favoritesOnly = false;
            filterPerson = id; filterPersonName = name; includeBirthdays = includeManual = true;
            contextualFilters = true;
            galleryChronological = false; cacheValid = false; railFollow = true; page = 0;
            SetTab(Tab.Gallery);
        }

        internal void ReturnFromPeople()
        {
            var back = peopleReturn; peopleReturn = null;
            if (back == null || !ReferenceEquals(back.Store, PhotoStore.Instance)) return;
            filterKind = back.Kind; filterPerson = back.Person; filterPersonName = back.PersonName;
            filterYear = back.Year; search = back.Search; includeManual = back.Manual;
            includeBirthdays = back.Birthdays; favoritesOnly = back.Favorites; cacheValid = false;
            var rec = PhotoStore.Instance.Get(peopleReturnPhoto);
            if (rec != null) { ShowGallery(rec, back.Chrono); galleryVersion = peopleReturnVersion; }
            contextualFilters = back.Contextual;
        }

        private void DrawInfo(Rect info, PhotoRecord rec, int idx, int count)
        {
            DrawPaper(info, false);
            GalleryInfoLayout(info, out Rect title, out Rect metadata, out Rect story, out Rect actions);
            float favoriteWidth = Mathf.Max(142f, AlbumStyle.StyleFor("MF_FavoriteOn".Translate(), GameFont.Small).CalcSize(new GUIContent("MF_FavoriteOn".Translate())).x + 48f);
            Rect favorite = new Rect(title.xMax - favoriteWidth, title.y, favoriteWidth, HeaderRowHeight);
            title.width -= favoriteWidth + 8f;
            if (AlbumFavorites.Button(favorite, rec.favorite)) AlbumFavorites.Toggle(PhotoStore.Instance, rec);
            // Text belongs to the version on screen, even when a different version is used in the album and frames.
            string onScreen = galleryVersion ?? rec.Shown?.id ?? "";
            var onScreenEdit = onScreen.NullOrEmpty() ? null : rec.Edit(onScreen);
            Text.Font = GameFont.Medium;
            GUI.color = Ink;
            string name = rec.TitleOf(onScreen);
            float eventIcon = 20f * AlbumStyle.Scale;
            PhotoEventIcons.DrawEvent(new Rect(title.x, title.y + 3f, eventIcon, eventIcon), rec, Ink);
            Rect nameRect = new Rect(title.x + eventIcon + 6f, title.y, title.width - eventIcon - 6f, title.height);
            AlbumPresentation.CardTitle(nameRect, name, TextAnchor.UpperLeft, GameFont.Medium);
            TooltipHandler.TipRegion(title, name);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperRight;
            GUI.color = InkFaint;
            AlbumPresentation.CardDate(new Rect(favorite.xMax, title.y + 6f, 80f, AlbumPresentation.DateHeight), (idx + 1) + " / " + count);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            string sub = KindLabel(rec.kind) + " · " + rec.dateLabel + (rec.Delayed ? " · *" : "")
                + (onScreenEdit != null ? " · " + "MF_VersionEdit".Translate(rec.edits.IndexOf(onScreenEdit) + 1) : rec.edits.Count > 0 ? " · " + "MF_EditCount".Translate(rec.edits.Count) : "");
            // The bundled handwriting fonts lack the middle dot. Display the supported separator without changing stored text.
            Rect details = new Rect(metadata.x, metadata.y, metadata.width, HeaderRowHeight);
            GUI.color = Ink;
            AlbumStyle.HeaderLabel(details, AlbumPresentation.Title(sub));
            TooltipHandler.TipRegion(details, sub);
            DrawPeopleLinks(new Rect(metadata.x, details.yMax, metadata.width, metadata.height - details.height), rec);
            GUI.color = Ink;
            string originalDesc = rec.DescOf(onScreen);
            string desc = AlbumPresentation.Title(originalDesc); // Display-only punctuation, identical to the title/metadata; preserve stored prose.
            if (!desc.NullOrEmpty())
            {
                if (storyPhotoId != rec.id || storyVersion != onScreen)
                {
                    storyPhotoId = rec.id;
                    storyVersion = onScreen;
                    storyScroll = Vector2.zero;
                }
                Rect view = new Rect(0f, 0f, story.width - 16f, Mathf.Max(story.height, AlbumStyle.TextHeight(desc, story.width - 16f)));
                Widgets.BeginScrollView(story, ref storyScroll, view);
                AlbumStyle.BodyLabel(view, desc);
                Widgets.EndScrollView();
                TooltipHandler.TipRegion(story, originalDesc);
            }
            GUI.color = Color.white;

            var store = PhotoStore.Instance;
            float rowHeight = (actions.height - 8f) / 2f;
            Rect primary = new Rect(actions.x, actions.y, actions.width, rowHeight);
            Rect secondary = new Rect(actions.x, primary.yMax + 8f, actions.width, rowHeight);
            bool mapAlive = store != null && rec.HasCenter && Find.Maps.Any(m => m.uniqueID == rec.mapId);
            bool canReunite = Reunion.Possible(rec);
            Rect textRect = GalleryActionRect(primary, 0, 4), editRect = GalleryActionRect(primary, 1, 4), reshootRect = GalleryActionRect(primary, 2, 4), reunionRect = GalleryActionRect(primary, 3, 4);
            if (FitButton(textRect, "MF_GalleryRevision_Text".Translate())) Find.WindowStack.Add(new FloatMenu(GalleryTextOptions(rec)));
            TooltipHandler.TipRegion(textRect, "MF_GalleryRevision_TextHint".Translate());
            if (FitButton(editRect, "MF_Edit".Translate(), rec.HasFile)) Find.WindowStack.Add(new Dialog_PhotoEdit(rec, galleryVersion ?? rec.Shown?.id ?? ""));
            TooltipHandler.TipRegion(editRect, "MF_GalleryRevision_EditHint".Translate());
            if (FitButton(reshootRect, "MF_Feedback_ReshootShort".Translate(), mapAlive))
            {
                var r2 = store.Reshoot(rec);
                if (r2 != null) Messages.Message("MF_ReshootQueued".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }
            TooltipHandler.TipRegion(reshootRect, (mapAlive ? "MF_GalleryRevision_ReshootHint" : "MF_NoMapForReshoot").Translate());
            if (FitButton(reunionRect, "MF_GalleryRevision_Reunion".Translate(), canReunite)) reunionRequest = rec;
            TooltipHandler.TipRegion(reunionRect, (canReunite ? "MF_GalleryRevision_ReunionHint" : "MF_GalleryRevision_ReunionUnavailable").Translate());
            EnsureComparisons(rec);
            int secondaryColumns = HasIntegrations ? 4 : 3;
            Rect compareRect = GalleryActionRect(secondary, 0, secondaryColumns);
            if (FitButton(compareRect, "MF_Feedback_Compare".Translate(), comparisonBirthday != null || comparisonView != null)) Find.WindowStack.Add(new FloatMenu(ComparisonOptions(rec)));
            TooltipHandler.TipRegion(compareRect, (comparisonBirthday != null || comparisonView != null ? "MF_GalleryRevision_CompareHint" : "MF_GalleryRevision_CompareUnavailable").Translate());
            Rect moreRect = GalleryActionRect(secondary, 1, secondaryColumns);
            if (FitButton(moreRect, "MF_Feedback_PhotoTools".Translate())) Find.WindowStack.Add(new FloatMenu(GalleryMoreOptions(rec)));
            TooltipHandler.TipRegion(moreRect, "MF_Feedback_PhotoToolsHint".Translate());
            Rect archiveRect = GalleryActionRect(secondary, 2, secondaryColumns);
            string archiveBlocked = ArchiveBlocked(rec);
            if (FitButton(archiveRect, "MF_Archive_Move".Translate(), archiveBlocked == null)) ArchiveButton(rec);
            TooltipHandler.TipRegion(archiveRect, archiveBlocked ?? "MF_Archive_MoveHint".Translate().ToString());
            if (HasIntegrations)
            {
                Rect bridgesRect = GalleryActionRect(secondary, 3, secondaryColumns);
                if (FitButton(bridgesRect, "MF_GalleryRevision_Integrations".Translate(), rec.HasFile)) Find.WindowStack.Add(new FloatMenu(GalleryIntegrationOptions(rec)));
                TooltipHandler.TipRegion(bridgesRect, "MF_GalleryRevision_IntegrationsHint".Translate());
            }
        }

        internal List<FloatMenuOption> GalleryTextOptions(PhotoRecord rec)
        {
            string viewing = galleryVersion ?? rec.Shown?.id ?? "";
            var edit = viewing.NullOrEmpty() ? null : rec.Edit(viewing);
            return new List<FloatMenuOption> {
                new FloatMenuOption(edit == null ? "MF_Rename".Translate() : "MF_RenameEdit".Translate(), () => Find.WindowStack.Add(new Dialog_RenamePhoto(rec, edit))),
                new FloatMenuOption(edit == null ? "MF_EditDesc".Translate() : "MF_EditNote".Translate(), () => Find.WindowStack.Add(new Dialog_EditDescription(rec, edit))) };
        }

        internal List<FloatMenuOption> GalleryIntegrationOptions(PhotoRecord rec)
        {
            string viewing = galleryVersion ?? rec.Shown?.id ?? "";
            var options = new List<FloatMenuOption>();
            var store = PhotoStore.Instance;
            if (store != null && rec.HasFile)
            {
                if (OptionalPhotoBridges.RimGramInstalled) options.Add(new FloatMenuOption("RimGram", () => Find.WindowStack.Add(new Dialog_MFPhotoBridge(store, rec, viewing, false))));
                if (OptionalPhotoBridges.RimTalkInstalled) options.Add(new FloatMenuOption("RimTalk", () => Find.WindowStack.Add(new Dialog_MFPhotoBridge(store, rec, viewing, true))));
            }
            if (options.Count == 0) options.Add(new FloatMenuOption("MF_GalleryRevision_NoIntegrations".Translate(), null));
            return options;
        }

        private List<FloatMenuOption> ComparisonOptions(PhotoRecord rec)
        {
            var options = new List<FloatMenuOption>(); var store = PhotoStore.Instance;
            if (store == null || !rec.HasFile) return options;
            EnsureComparisons(rec);
            var birthday = comparisonBirthday;
            if (birthday != null) options.Add(new FloatMenuOption("MF_Phase2_BirthdayCompare".Translate(), () => Find.WindowStack.Add(new Dialog_MFBirthdayComparison(birthday, rec))));
            var priorView = comparisonView;
            if (priorView != null) options.Add(new FloatMenuOption("MF_Phase2_ViewCompare".Translate(), () => Find.WindowStack.Add(new Dialog_ThenNow(priorView, rec))));
            return options;
        }

        private void EnsureComparisons(PhotoRecord record)
        {
            var store = PhotoStore.Instance;
            if (ReferenceEquals(comparisonStore, store) && comparisonVersion == (store?.Version ?? -1) && comparisonPhoto == record.id) return;
            comparisonStore = store; comparisonVersion = store?.Version ?? -1; comparisonPhoto = record.id;
            comparisonBirthday = store == null ? null : AlbumPresentation.PreviousBirthday(record, store.Records);
            comparisonView = store == null ? null : AlbumPresentation.PreviousView(record, store.Records);
        }

        /// <summary>The same menu the More button opens. Kept as one entry point so the probe can exercise the real actions and conditions.</summary>
        internal List<FloatMenuOption> GalleryMoreOptions(PhotoRecord rec)
        {
            string viewing = galleryVersion ?? rec.Shown?.id ?? "";
            var edit = viewing.NullOrEmpty() ? null : rec.Edit(viewing);
            var opts = new List<FloatMenuOption>();
            var store = PhotoStore.Instance;
            if (store != null)
            {
                if (rec.HasFile) opts.Add(new FloatMenuOption("MF_Feedback_ZoomOriginal".Translate(), () => Find.WindowStack.Add(new Dialog_MFPhotoZoom(rec, viewing))));
                string shown = rec.Shown?.id ?? "";
                if (rec.edits.Count > 0 && viewing != shown)
                    opts.Add(new FloatMenuOption("MF_DesignAlbum_UseVersion".Translate(), () =>
                    {
                        store.SetShown(rec, viewing.NullOrEmpty() ? null : viewing);
                        galleryVersion = null;
                    }));
                var original = rec.reshootOf.NullOrEmpty() ? null : store.Get(rec.reshootOf);
                if (original != null)
                    opts.Add(new FloatMenuOption("MF_ViewOriginal".Translate(), () => ShowGallery(original, galleryChronological)));
                var latest = store.LatestReshootOf(rec);
                if (latest != null)
                    opts.Add(new FloatMenuOption("MF_ViewReshoot".Translate(), () => ShowGallery(latest, galleryChronological)));
                if (edit != null)
                    opts.Add(new FloatMenuOption("MF_DeleteEdit".Translate(), () =>
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MF_DeleteEditConfirm".Translate(), () =>
                        {
                            store.DeleteEdit(rec, viewing);
                            galleryVersion = null;
                        }, true))));
            }
            if (store != null && rec.HasFile) opts.Add(new FloatMenuOption("MF_Feedback_ExportFiles".Translate(), () => Find.WindowStack.Add(new Dialog_MFPhotoBridge(store, rec, viewing, false, true))));
            opts.Add(new FloatMenuOption("MF_OpenFolder".Translate(), () =>
            {
                string folder = store?.FolderPath;
                if (!folder.NullOrEmpty() && Directory.Exists(folder)) Application.OpenURL("file:///" + folder.Replace('\\', '/'));
            }));
            opts.Add(new FloatMenuOption("MF_Extras_Remembrance".Translate(), () => Find.WindowStack.Add(new Dialog_MFRemembrance(filterPerson, filterPersonName))));
            return opts;
        }

        /// <summary>The themed gallery button fits its own label without changing the font used by the scrollable story.</summary>
        private static bool FitButton(Rect r, string label, bool active = true)
        {
            return AlbumStyle.Button(r, label, active, preserveSize: true);
        }

        private void DrawRail(Rect rail, List<PhotoRecord> list, int idx)
        {
            var session = Bursts(list);
            idx = session.PrintIndex(list[idx].id);
            list = session.Prints;
            DrawPaper(rail, true);
            Rect inner = rail.ContractedBy(8f);
            Rect view = new Rect(0f, 0f, inner.width - 16f, list.Count * RailRowH);
            if (railFollow)
            {
                float rowY = idx * RailRowH;
                if (rowY < railScroll.y || rowY + RailRowH > railScroll.y + inner.height)
                    railScroll.y = Mathf.Clamp(rowY - inner.height / 2f + RailRowH / 2f, 0f, Mathf.Max(0f, view.height - inner.height));
                railFollow = false;
            }
            Widgets.BeginScrollView(inner, ref railScroll, view);
            var store = PhotoStore.Instance;
            int first = Mathf.Max(0, (int)(railScroll.y / RailRowH) - 1);
            int last = Mathf.Min(list.Count - 1, (int)((railScroll.y + inner.height) / RailRowH) + 1);
            float ThumbW = Mathf.Clamp(view.width * .3f, 62f, 94f);
            for (int i = first; i <= last; i++)
            {
                var rec = list[i];
                Rect row = new Rect(0f, i * RailRowH, view.width, RailRowH);
                if (i == idx)
                {
                    Color selected = AlbumStyle.Theme.Accent;
                    selected.a = 0.18f;
                    Widgets.DrawBoxSolid(row, selected);
                }
                else if (Mouse.IsOver(row)) Widgets.DrawBoxSolid(row, new Color(0f, 0f, 0f, 0.05f));
                Texture2D tex = PhotoTextureCache.ThumbDeferred(store.ThumbPath(rec), store.FilePath(rec));
                float aspect = tex != null && tex.height > 0 ? (float)tex.width / tex.height : rec.Aspect;
                float tw = ThumbW, th = tw / aspect;
                if (th > RailRowH - 14f) { th = RailRowH - 14f; tw = th * aspect; }
                Rect photo = new Rect(row.x + 6f + (ThumbW - tw) / 2f, row.y + (RailRowH - th) / 2f, tw, th);
                Widgets.DrawBoxSolid(new Rect(photo.x - 3f, photo.y - 3f, photo.width + 6f, photo.height + 6f), i == idx ? Ink : Cream);
                if (tex != null) GUI.DrawTexture(photo, tex, ScaleMode.StretchToFill);
                if (session.Shots(rec)?.Count > 1) BurstBadge(new Rect(photo.x, photo.yMax - 24f, photo.width, 24f), session.Shots(rec).Count);
                float tx = row.x + ThumbW + 16f, tw2 = row.width - ThumbW - 22f;
                Text.Font = GameFont.Small;
                GUI.color = Ink;
                float eventIcon = 18f * AlbumStyle.Scale;
                PhotoEventIcons.DrawEvent(new Rect(tx, row.y + 14f, eventIcon, eventIcon), rec, Ink);
                AlbumPresentation.CardTitle(new Rect(tx + eventIcon + 5f, row.y + 10f, tw2 - eventIcon - 5f, AlbumPresentation.TitleHeight), rec.DisplayTitle ?? "", TextAnchor.UpperLeft);
                Text.Font = GameFont.Tiny;
                GUI.color = Ink;
                AlbumPresentation.CardTitle(new Rect(tx, row.y + AlbumPresentation.TitleHeight + 16f, tw2, 2f * AlbumPresentation.DateHeight), rec.dateLabel ?? "", TextAnchor.UpperLeft, GameFont.Tiny);
                TooltipHandler.TipRegion(row, Tip(rec));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                if (Widgets.ButtonInvisible(row) && i != idx)
                {
                    SelectGalleryPhoto(rec, false);
                    SoundDefOf.Click.PlayOneShotOnCamera();
                }
            }
            Widgets.EndScrollView();
        }

        // ---------- footer: page turn ----------

        private void DrawFooter(Rect r, List<PhotoRecord> list)
        {
            if (tab != Tab.Photos) return;
            int pages = Mathf.Max(1, (list.Count + PerSpread - 1) / PerSpread);
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = CoverText;
            AlbumStyle.Label(new Rect(r.center.x - 80f, r.y, 160f, r.height), "MF_PageOf".Translate(page + 1, pages));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            if (CoverButton(new Rect(r.center.x - 130f, r.y + 4f, 40f, 26f), "<", false) && page > 0) { page--; bookAnchor = list[page * PerSpread].id; SoundDefOf.PageChange.PlayOneShotOnCamera(); }
            if (CoverButton(new Rect(r.center.x + 90f, r.y + 4f, 40f, 26f), ">", false) && page < pages - 1) { page++; bookAnchor = list[page * PerSpread].id; SoundDefOf.PageChange.PlayOneShotOnCamera(); }
            if (Event.current.type == EventType.ScrollWheel && Mouse.IsOver(windowRect.AtZero()))
            {
                int np = Mathf.Clamp(page + (Event.current.delta.y > 0f ? 1 : -1), 0, pages - 1);
                if (np != page) { page = np; bookAnchor = list[page * PerSpread].id; SoundDefOf.PageChange.PlayOneShotOnCamera(); }
                Event.current.Use();
            }
        }
    }

    /// <summary>album.html in the photo folder, relative image paths, no server. The page is the mod's Html/album.html — a leather-bound book:
    /// the colony's cover with its latest big moments fanned, year chapters of taped polaroids, filters by kind and person, a lightbox slideshow
    /// (user, 2026-09-26: the plain list looked careless). The game only fills in the data (JSON between the MF_DATA/MF_END markers); the page's
    /// script lays it out. Without the template file the old plain page is written. (user, 2026-11-08: back from the 10-17 folder export,
    /// which felt unfinished.) Edited and imported photos can sit in a subfolder, so the page names each by its path from the folder.</summary>
    public static class AlbumExport
    {
        /// <summary>Kinds that get a large card with a ribbon.</summary>
        private static readonly HashSet<string> Big = new HashSet<string> { "colonystart", "wedding", "anniversary", "population", "mechboss", "battle", "disaster", "memorynight", "ship", "gravship", "ending", "landing" };

        private static readonly Dictionary<string, string> Emoji = new Dictionary<string, string>
        {
            { "role", "\U0001F451" }, { "bestow", "\U0001F451" }, { "title", "\U0001F396" }, { "wedding", "\U0001F48D" }, { "party", "\U0001F389" },
            { "funeral", "\U0001F56F" }, { "ritual", "\U0001F525" }, { "baby", "\U0001F476" }, { "death", "\U0001F54A" }, { "recruit", "\U0001F91D" },
            { "legendary", "\U0001F3C6" }, { "ship", "\U0001F680" }, { "gravship", "\U0001F6F8" }, { "landing", "\U0001F6EC" }, { "mechboss", "\U0001F4A5" }, { "battle", "\u2694\uFE0F" }, { "disaster", "\U0001F32A\uFE0F" }, { "sky", "\U0001F30C" }, { "memorynight", "\U0001F4FD\uFE0F" },
            { "colonystart", "\U0001F3D5" }, { "anniversary", "\U0001F4C5" }, { "population", "\U0001F465" }, { "mechlink", "\U0001F517" },
            { "relicfound", "\U0001F3FA" }, { "reliquary", "\u26E9" }, { "relic", "\U0001F5C3" }, { "resurrect", "\u2728" }, { "monolith", "\U0001F5FF" },
            { "group", "\U0001F4F8" }, { "journey", "\U0001F9ED" }, { "birthday", "\U0001F382" }, { "manual", "\U0001F4F7" }, { "reshoot", "\U0001F501" },
            { "anomaly", "\U0001F441" }, { "contained", "\U0001F512" }, { "ending", "\U0001F305" }, { "specialtree", "\U0001F333" }, { "rareseen", "\U0001F984" },
            { "raretamed", "\U0001F984" }, { "xenogerm", "\U0001F9EC" }, { "sanguophage", "\U0001FA78" }, { "persona", "\U0001F5E1" }, { "ideoreform", "\U0001F4DC" },
            { "surgeryfail", "\U0001FA79" }, { "test", "\U0001F9EA" },
        };

        private const string DataStart = "/*MF_DATA*/", DataEnd = "/*MF_END*/";

        /// <summary>The album's export button and the photo guide's: write the page, say where it is, open it in the browser.</summary>
        public static void Open(PhotoStore store)
        {
            if (store == null) return;
            try
            {
                string path = Write(store);
                Messages.Message("MF_Exported".Translate(path), MessageTypeDefOf.TaskCompletion, false);
                Application.OpenURL("file:///" + path.Replace('\\', '/'));
            }
            catch (Exception e)
            {
                Log.Error("[MomentFrames] album export failed: " + e);
            }
        }

        public static string Write(PhotoStore store)
        {
            var recs = store.Records.Where(r => r.HasFile && !Kinds.PersonalOnly(r.kind) && Href(store, r) != null).OrderBy(r => r.ticksAbs).ToList();
            string tpl = Template();
            string html = tpl != null ? Fill(tpl, store, recs) : Plain(store, recs);
            Directory.CreateDirectory(store.FolderPath);
            string path = Path.Combine(store.FolderPath, "album.html");
            File.WriteAllText(path, html, new UTF8Encoding(false));
            return path;
        }

        /// <summary>The shown version (edit or original) as a link from the photo folder, each part URL-escaped; null when its file is gone
        /// (or, which the resolver never gives, lies outside the folder).</summary>
        internal static string Href(PhotoStore store, PhotoRecord r)
        {
            string path = store.FilePath(r);
            if (path.NullOrEmpty() || !File.Exists(path)) return null;
            string root = Path.GetFullPath(store.FolderPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return string.Join("/", full.Substring(root.Length).Split('\\', '/').Select(Uri.EscapeDataString).ToArray());
        }

        /// <summary>The page from the mod folder, or null (then the plain page).</summary>
        public static string Template()
        {
            try
            {
                string root = LoadedModManager.GetMod<MomentFramesMod>()?.Content?.RootDir;
                if (root.NullOrEmpty()) return null;
                string p = Path.Combine(Path.Combine(root, "Html"), "album.html");
                if (!File.Exists(p)) return null;
                string t = File.ReadAllText(p, Encoding.UTF8);
                return t.Contains(DataStart) && t.Contains(DataEnd) ? t : null;
            }
            catch (Exception e)
            {
                Log.Warning("[MomentFrames] album template: " + e.Message);
                return null;
            }
        }

        private static string Fill(string tpl, PhotoStore store, List<PhotoRecord> recs)
        {
            var j = new StringBuilder("{");
            Str(j, "colony", RelicImport.ColonyName()).Append(',');
            Str(j, "made", DateTime.Now.ToString("yyyy-MM-dd")).Append(",\"L\":{");
            string[] keys = { "kicker", "title", "statPhotos", "statPeople", "statYears", "statMilestones", "chapterCount", "milestone", "all", "everyone", "slideshow", "pause", "footer" };
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) j.Append(',');
                Str(j, keys[i], ("MF_Html_" + keys[i]).Translate().ToString());
            }
            j.Append("},\"photos\":[");
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (i > 0) j.Append(',');
                j.Append('{');
                Str(j, "f", Href(store, r)).Append(',');
                Str(j, "t", r.DisplayTitle).Append(',');
                Str(j, "k", r.kind).Append(',');
                Str(j, "kl", MainTabWindow_Album.KindLabel(r.kind)).Append(',');
                Str(j, "e", Emoji.TryGetValue(r.kind ?? "", out var e) ? e : "\U0001F4F7").Append(',');
                Str(j, "dl", r.dateLabel).Append(',');
                j.Append("\"y\":").Append(r.year).Append(',');
                Str(j, "yl", "MF_YearLabel".Translate(r.year).ToString()).Append(",\"p\":[");
                for (int k = 0; k < r.people.Count; k++) { if (k > 0) j.Append(','); j.Append('"').Append(Json(r.people[k])).Append('"'); }
                j.Append("],");
                Str(j, "d", r.DisplayDesc).Append(',');
                j.Append("\"m\":").Append(Big.Contains(r.kind ?? "") ? "true" : "false").Append(',');
                j.Append("\"a\":").Append(r.DisplayAspect.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                j.Append('}');
            }
            j.Append("]}");
            int a = tpl.IndexOf(DataStart, StringComparison.Ordinal), b = tpl.IndexOf(DataEnd, StringComparison.Ordinal);
            string page = tpl.Substring(0, a) + DataStart + j + tpl.Substring(b);
            return page.Replace("<html lang=\"ko\">", "<html lang=\"" + HtmlLang() + "\">");   // the template file is written in Korean
        }

        /// <summary>The page's lang attribute from the game's language (it picks the browser's fonts, CJK ones especially); unknown = empty.</summary>
        private static string HtmlLang()
        {
            string f = LanguageDatabase.activeLanguage?.folderName ?? "";
            string[,] map = { { "Korean", "ko" }, { "English", "en" }, { "Japanese", "ja" }, { "ChineseSimplified", "zh-Hans" }, { "ChineseTraditional", "zh-Hant" },
                { "German", "de" }, { "French", "fr" }, { "Russian", "ru" }, { "Spanish", "es" }, { "PortugueseBrazilian", "pt-BR" }, { "Portuguese", "pt" },
                { "Polish", "pl" }, { "Italian", "it" }, { "Turkish", "tr" }, { "Ukrainian", "uk" }, { "Czech", "cs" } };
            for (int i = 0; i < map.GetLength(0); i++)
                if (f.StartsWith(map[i, 0], StringComparison.OrdinalIgnoreCase)) return map[i, 1];
            return "";
        }

        private static StringBuilder Str(StringBuilder sb, string key, string value) => sb.Append('"').Append(key).Append("\":\"").Append(Json(value)).Append('"');

        /// <summary>JSON string content: quotes, backslashes and control characters escaped; "</" broken so the text can never close the script.</summary>
        public static string Json(string s)
        {
            if (s.NullOrEmpty()) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    case '<': sb.Append("\\u003c"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>The plain page (no template in the mod folder).</summary>
        private static string Plain(PhotoStore store, List<PhotoRecord> recs)
        {
            var sb = new StringBuilder();
            string title = Html("MF_AlbumTitle".Translate().ToString());
            sb.Append("<!doctype html><html lang=\"").Append(HtmlLang()).Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>").Append(title).Append("</title><style>")
              .Append("body{margin:0;background:#2b1d14;color:#e8d8bd;font-family:Segoe UI,Malgun Gothic,sans-serif}")
              .Append("header{padding:28px 32px 8px}h1{margin:0;font-weight:600;letter-spacing:.04em}h2{margin:34px 32px 10px;color:#c9a97a;font-weight:500;border-bottom:1px solid #4a3324;padding-bottom:6px}")
              .Append(".grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:22px;padding:0 32px}")
              .Append("figure{margin:0;background:#f5f0e6;color:#3a2a1c;padding:10px 10px 12px;transform:rotate(var(--t));box-shadow:4px 6px 14px rgba(0,0,0,.45);cursor:pointer;transition:transform .15s}figure:hover{transform:rotate(0) scale(1.03)}")
              .Append("figure img{width:100%;display:block;background:#333}figcaption{padding:8px 2px 0;font-size:14px}figcaption small{color:#7a6650;display:block;margin-top:3px}figcaption .d{color:#4a3a2c;font-size:13px;margin-top:6px;white-space:pre-line}")
              .Append("#lb{position:fixed;inset:0;background:rgba(0,0,0,.92);display:none;align-items:center;justify-content:center;flex-direction:column}#lb.on{display:flex}")
              .Append("#lb img{max-width:92vw;max-height:74vh;box-shadow:0 0 40px #000}#lb .cap{margin-top:14px;font-size:16px;text-align:center;max-width:80vw}#lb .cap small{color:#b39b7a;display:block}#lb .cap .d{display:block;color:#d9c6a8;font-size:14px;margin-top:6px;white-space:pre-line}")
              .Append("#lb .bar{margin-top:12px}#lb button{background:#3b2a1d;color:#e8d8bd;border:1px solid #6a4d35;padding:6px 14px;margin:0 4px;border-radius:4px;cursor:pointer;font-size:14px}")
              .Append("footer{padding:30px 32px;color:#7a6650;font-size:12px}")
              .Append("</style></head><body><header><h1>").Append(title).Append("</h1></header>");
            int lastYear = int.MinValue;
            bool open = false;
            int i = 0;
            var items = new StringBuilder();
            foreach (var r in recs)
            {
                if (r.year != lastYear)
                {
                    if (open) sb.Append("</div>");
                    lastYear = r.year;
                    sb.Append("<h2>").Append(Html("MF_YearLabel".Translate(r.year).ToString())).Append("</h2><div class=\"grid\">");
                    open = true;
                }
                string title0 = r.DisplayTitle ?? "", date0 = r.dateLabel ?? "", people0 = string.Join(", ", r.people), kind0 = MainTabWindow_Album.KindLabel(r.kind), desc0 = r.DisplayDesc ?? "";
                string sub0 = kind0 + " · " + date0 + (people0.NullOrEmpty() ? "" : " · " + people0);
                string f = Href(store, r) ?? "";
                float tilt = MainTabWindow_Album.TiltOf(r) * 0.75f;
                sb.Append("<figure data-i=\"").Append(i).Append("\" style=\"--t:").Append(tilt.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("deg\"><img loading=\"lazy\" src=\"").Append(f).Append("\" alt=\"").Append(Html(title0)).Append("\"><figcaption>").Append(Html(title0)).Append("<small>").Append(Html(sub0)).Append("</small>");
                if (!desc0.NullOrEmpty()) sb.Append("<span class=\"d\">").Append(Html(desc0)).Append("</span>");
                sb.Append("</figcaption></figure>");
                // JS strings: plain text with JS escaping (HTML entities would show literally in textContent).
                items.Append(i > 0 ? "," : "").Append("{\"f\":\"").Append(f).Append("\",\"t\":\"").Append(Js(title0)).Append("\",\"s\":\"").Append(Js(sub0)).Append("\",\"d\":\"").Append(Js(desc0)).Append("\"}");
                i++;
            }
            if (open) sb.Append("</div>");
            sb.Append("<div id=\"lb\"><img id=\"lbi\"><div class=\"cap\"><span id=\"lbt\"></span><small id=\"lbs\"></small><span class=\"d\" id=\"lbd\"></span></div><div class=\"bar\"><button onclick=\"go(-1)\">&larr;</button><button id=\"pl\" onclick=\"play()\">&#9654;</button><button onclick=\"go(1)\">&rarr;</button><button onclick=\"hide()\">&times;</button></div></div>")
              .Append("<footer>MomentFrames · ").Append(recs.Count).Append("</footer><script>")
              .Append("var it=[").Append(items).Append("],cur=0,tm=null;")
              .Append("function show(i){cur=(i+it.length)%it.length;document.getElementById('lbi').src=it[cur].f;document.getElementById('lbt').textContent=it[cur].t;document.getElementById('lbs').textContent=it[cur].s;document.getElementById('lbd').textContent=it[cur].d;document.getElementById('lb').classList.add('on');}")
              .Append("function go(d){show(cur+d);}function hide(){document.getElementById('lb').classList.remove('on');stop();}")
              .Append("function play(){if(tm){stop();return;}tm=setInterval(function(){go(1)},3000);document.getElementById('pl').textContent='\\u23F8';}")
              .Append("function stop(){if(tm){clearInterval(tm);tm=null;}document.getElementById('pl').textContent='\\u25B6';}")
              .Append("document.querySelectorAll('figure').forEach(function(f){f.onclick=function(){show(+f.dataset.i)}});")
              .Append("document.addEventListener('keydown',function(e){if(!document.getElementById('lb').classList.contains('on'))return;if(e.key==='ArrowRight')go(1);else if(e.key==='ArrowLeft')go(-1);else if(e.key==='Escape')hide();else if(e.key===' '){e.preventDefault();play();}});")
              .Append("</script></body></html>");
            return sb.ToString();
        }

        /// <summary>HTML text escaping that leaves Korean (and every other non-ASCII character) as it is — WebUtility.HtmlEncode would turn it into numeric entities.</summary>
        private static string Html(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        private static string Js(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("<", "\\u003c").Replace("\r", " ").Replace("\n", "\\n");
    }
}
