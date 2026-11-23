using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal sealed class ImportedPhotosTab
    {
        internal PhotoSource Source = PhotoSource.Computer;
        internal bool Detail;
        internal string Colony;
        internal readonly PhotoPickerFilters Filters = new PhotoPickerFilters();
        private PhotoStore owner;
        private int version = -1;
        private PhotoSource cachedSource;
        private string cachedColony;
        private List<PhotoRecord> source = new List<PhotoRecord>();
        private readonly Dictionary<string, string> colonies = new Dictionary<string, string>();
        private Vector2 scroll;
        private List<PhotoRecord> groupedList;
        private List<IGrouping<string, PhotoRecord>> groups;

        internal void SelectSource(PhotoSource value)
        {
            if (Source == value) return;
            Source = value; Colony = null; Detail = false; scroll = Vector2.zero;
            Filters.Search = ""; Filters.FavoritesOnly = false; Filters.Year = 0;
        }

        private void Ensure(PhotoStore store)
        {
            if (!ReferenceEquals(owner, store))
            {
                owner = store; version = -1; Colony = null; Detail = false; scroll = Vector2.zero;
                Filters.Search = ""; Filters.FavoritesOnly = false; Filters.Year = 0;
            }
            if (version == (store?.Version ?? 0) && cachedSource == Source && cachedColony == Colony) return;
            version = store?.Version ?? 0; cachedSource = Source; cachedColony = Colony;
            var all = store?.Records.Where(r => r.HasFile && PhotoSources.Of(r) == Source).ToList() ?? new List<PhotoRecord>();
            colonies.Clear();
            foreach (var group in all.GroupBy(PhotoSources.ColonyKey))
                if (Source == PhotoSource.Legacy) colonies[group.Key] = PhotoSources.ColonyName(store,
                    group.FirstOrDefault(r => !r.importedColonyName.NullOrEmpty()) ?? group.First());
            if (Colony != null && !colonies.ContainsKey(Colony)) Colony = cachedColony = null;
            source = all.Where(r => Colony == null || PhotoSources.ColonyKey(r) == Colony).ToList();
        }

        internal List<PhotoRecord> Visible(PhotoStore store) { Ensure(store); return Filters.Apply(source); }
        internal void Open(PhotoStore store, PhotoRecord record)
        {
            Ensure(store); SelectSource(PhotoSources.Of(record));
            if (!Visible(store).Contains(record))
            { Colony = null; Filters.Search = ""; Filters.FavoritesOnly = false; Filters.Year = 0; }
            Detail = true;
        }

        internal void Draw(Rect area, PhotoStore store, Action<Rect, PhotoRecord, int> drawCard, Action<PhotoRecord> open)
        {
            AlbumStyle.DrawPaper(area);
            area = area.ContractedBy(12f);
            float h = SharedPhotoUI.ControlHeight, gap = 8f, width = (area.width - 2 * gap) / 3f;
            foreach (var value in new[] { PhotoSource.Computer, PhotoSource.Legacy })
            {
                int i = value == PhotoSource.Computer ? 0 : 1;
                Rect button = new Rect(area.x + i * (width + gap), area.y, width, h);
                if (SharedPhotoUI.Button(button, PhotoSources.Label(value))) SelectSource(value);
                if (Source == value) Widgets.DrawBoxSolid(new Rect(button.x + 3, button.yMax - 3, button.width - 6, 2), AlbumStyle.Theme.Accent);
            }
            if (SharedPhotoUI.Button(new Rect(area.x + 2 * (width + gap), area.y, width, h),
                (Source == PhotoSource.Computer ? "MF_Imports_AddFile" : "MF_Imports_AddColony").Translate()))
            {
                if (Source == PhotoSource.Legacy) Find.WindowStack.Add(new Dialog_ImportRelics());
                else Find.WindowStack.Add(new Dialog_StudioImageFile(path =>
                {
                    if (ExternalPhotoImport.ImportFile(store, path, out var rec, out var error)) open(rec);
                    else Messages.Message(error, MessageTypeDefOf.RejectInput, false);
                }, true));
            }
            var photos = Visible(store);
            float y = area.y + h + gap;
            float filterX = area.x;
            if (Source == PhotoSource.Legacy)
            {
                string label = Colony != null && colonies.TryGetValue(Colony, out var name) ? name : "MF_Imports_AllColonies".Translate().ToString();
                var selector = new Rect(area.x, y, area.width * .28f, h);
                if (SharedPhotoUI.Button(selector, label))
                {
                    var options = new List<FloatMenuOption> { new FloatMenuOption("MF_Imports_AllColonies".Translate(), () => { Colony = null; scroll = Vector2.zero; }) };
                    foreach (var entry in colonies.OrderBy(p => p.Value))
                    { string key = entry.Key; options.Add(new FloatMenuOption(entry.Value, () => { Colony = key; scroll = Vector2.zero; })); }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
                TooltipHandler.TipRegion(selector, label);
                filterX = selector.xMax + gap;
            }
            string before = Filters.Key;
            float favoriteW = Mathf.Min(area.width * .28f, Mathf.Max(170f,
                AlbumStyle.StyleFor("MF_FavoritesOnly".Translate()).CalcSize(new GUIContent("MF_FavoritesOnly".Translate())).x + 24f));
            Rect favorite = new Rect(area.xMax - favoriteW, y, favoriteW, h);
            Rect search = new Rect(filterX, y, Mathf.Max(1, favorite.x - filterX - h - gap * 2), h);
            Filters.Search = AlbumBridgeUI.TextField(search, Filters.Search, "MFImportsSearch");
            if (Filters.Search.Length > 100) Filters.Search = Filters.Search.Substring(0, 100);
            if (Filters.Search.NullOrEmpty() && GUI.GetNameOfFocusedControl() != "MFImportsSearch")
                AlbumBridgeUI.InputHint(search.ContractedBy(8), "MF_Phase2_AlbumSearch".Translate());
            if (SharedPhotoUI.Button(new Rect(search.xMax + gap, y, h, h), "×")) Filters.Search = "";
            if (SharedPhotoUI.Button(favorite, "MF_FavoritesOnly".Translate())) Filters.FavoritesOnly = !Filters.FavoritesOnly;
            if (Filters.FavoritesOnly) Widgets.DrawBoxSolid(new Rect(favorite.x + 3, favorite.yMax - 3, favorite.width - 6, 2), AlbumStyle.Theme.Accent);
            if (before != Filters.Key) scroll = Vector2.zero;
            photos = Visible(store); y += h + gap;
            float countH = AlbumStyle.TextHeight("Ag", area.width) + 8;
            SharedPhotoUI.Label(new Rect(area.x + 8, y, area.width - 16, countH), "MF_Imports_Count".Translate(photos.Count), true);
            y += countH;
            Rect grid = new Rect(area.x, y, area.width, Mathf.Max(1, area.yMax - y));
            if (photos.Count == 0)
            {
                SharedPhotoUI.Label(grid, (source.Count == 0 ? (Source == PhotoSource.Computer ? "MF_Imports_EmptyComputer" : "MF_Imports_EmptyLegacy") : "MF_DesignNav_NoMatches").Translate(), true);
                return;
            }
            if (!ReferenceEquals(groupedList, photos))
            { groupedList = photos; groups = photos.GroupBy(r => Source == PhotoSource.Legacy ? PhotoSources.ColonyKey(r) : "").ToList(); }
            float available = Mathf.Max(1, grid.width - 18), cardH = Mathf.Max(230, 180 + AlbumPresentation.TitleHeight + AlbumPresentation.DateHeight);
            int columns = Mathf.Max(1, Mathf.FloorToInt(available / Mathf.Max(230, 210 * AlbumStyle.Scale)));
            float cardW = available / columns, headingH = Source == PhotoSource.Legacy ? h : 0;
            float total = groups.Sum(g => headingH + Mathf.CeilToInt(g.Count() / (float)columns) * cardH + gap);
            scroll.y = Mathf.Clamp(scroll.y, 0, Mathf.Max(0, total - grid.height));
            Widgets.BeginScrollView(grid, ref scroll, new Rect(0, 0, available, total));
            float rowY = 0; int index = 0;
            foreach (var group in groups)
            {
                if (headingH > 0 && rowY + headingH >= scroll.y && rowY <= scroll.y + grid.height)
                    SharedPhotoUI.Label(new Rect(8, rowY, available - 16, headingH), colonies.TryGetValue(group.Key, out var name) ? name : "MF_Imports_UnknownColony".Translate().ToString());
                rowY += headingH; int slot = 0;
                foreach (var rec in group)
                {
                    float top = rowY + (slot / columns) * cardH;
                    if (top + cardH >= scroll.y && top <= scroll.y + grid.height)
                        drawCard(new Rect((slot % columns) * cardW, top, cardW, cardH), rec, index);
                    slot++; index++;
                }
                rowY += Mathf.CeilToInt(slot / (float)columns) * cardH + gap;
            }
            Widgets.EndScrollView();
        }
    }
}
