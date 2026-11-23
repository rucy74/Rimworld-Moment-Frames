using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal static class AlbumFavorites
    {
        /// <summary>A chosen film remembers an archived photo, so restoring it puts it back on the film.</summary>
        internal static List<string> FilmIds(PhotoStore store, IEnumerable<PhotoRecord> film, IEnumerable<string> selected) =>
            film.Select(r => r.id).Concat(selected.Where(id => store.GetAny(id)?.Archived == true)).ToList();

        internal static void Toggle(PhotoStore store, PhotoRecord record)
        {
            if (store == null || record == null || !record.HasFile || store.Get(record.id) != record) return;
            record.favorite = !record.favorite;
            store.NotifyChanged();
        }

        internal static List<PhotoRecord> Selected(IEnumerable<PhotoRecord> records, IEnumerable<string> ids)
        {
            var wanted = new HashSet<string>(ids ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            return records.Where(r => r.HasFile && wanted.Contains(r.id)).OrderBy(r => r.ticksAbs)
                .ThenBy(r => r.id, StringComparer.Ordinal).ToList();
        }

        internal static bool Button(Rect rect, bool selected, bool compact = false)
        {
            string text = (selected ? "MF_FavoriteOn" : "MF_FavoriteOff").Translate();
            Color old = GUI.color;
            if (Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);
            float size = Mathf.Min(28f, rect.height - 8f);
            PhotoIconArt.Draw(new Rect(rect.x + 5f, rect.center.y - size / 2f, size, size), selected?PhotoIcon.HeartFilled:PhotoIcon.Heart,
                selected ? new Color(.66f, .22f, .28f) : AlbumStyle.Theme.MutedInk);
            if (!compact)
            {
                GUI.color = AlbumStyle.Theme.Ink;
                AlbumStyle.HeaderLabel(new Rect(rect.x + size + 12f, rect.y, rect.width - size - 16f, rect.height), text);
            }
            GUI.color = old;
            TooltipHandler.TipRegion(rect, text + "\n" + "MF_FavoriteHint".Translate());
            return Widgets.ButtonInvisible(rect);
        }
    }

    /// <summary>Draft selection is local to this window; only Apply changes its caller's draft film.</summary>
    internal sealed class Dialog_FavoriteFilm : Window
    {
        private readonly PhotoStore store;
        private readonly Action<List<string>> apply;
        private readonly HashSet<string> selected;
        internal readonly PhotoPickerFilters Filters=new PhotoPickerFilters {FavoritesOnly=true};
        private Vector2 scroll;
        private int version = -1;
        private List<PhotoRecord> records = new List<PhotoRecord>();
        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(960f, UI.screenWidth - 40f), Mathf.Min(820f, UI.screenHeight - 40f));
        internal Dialog_FavoriteFilm(PhotoStore store, IEnumerable<string> initial, Action<List<string>> apply)
        {
            this.store = store; this.apply = apply;
            selected = new HashSet<string>(initial ?? store.Records.Where(r => r.HasFile && r.favorite).Select(r => r.id));
            doCloseX = false; doWindowBackground = false; forcePause = false; closeOnAccept = false;
        }
        public override void DoWindowContents(Rect inRect)
        {
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(inRect); Rect page = inRect.ContractedBy(22f);
                float h = SharedPhotoUI.ControlHeight;
                SharedPhotoUI.Heading(new Rect(page.x, page.y, page.width - h - 10f, h), "MF_FavoriteFilmTitle".Translate());
                if (SharedPhotoUI.Button(new Rect(page.xMax - h, page.y, h, h), "×")) { Close(); return; }
                if (version != store.Version)
                { records = store.Records.Where(r => r.HasFile).OrderBy(r => r.ticksAbs).ToList(); version = store.Version; }
                float y = page.y + h + 10f;
                if(Filters.Draw(new Rect(page.x,y,page.width,Filters.Height),records,selected))scroll=Vector2.zero;
                var shown=Filters.Apply(records);y+=Filters.Height+8f;
                if(SharedPhotoUI.QuietButton(new Rect(page.x,y,page.width,h),"MF_FavoriteClearSelection".Translate()))selected.Clear();
                y+=h+6f;
                string hint = AlbumPresentation.Title("MF_FavoriteFilmHint".Translate(selected.Count(id => store.Get(id) != null), MemoryNight.MinPhotos));
                float hintH = AlbumStyle.TextHeight(hint, page.width) + 10f;
                SharedPhotoUI.Label(new Rect(page.x, y, page.width, hintH), hint, true); y += hintH;
                Rect viewport = new Rect(page.x, y, page.width, page.yMax - h - 12f - y);
                int columns = viewport.width >= 640f ? 2 : 1;
                float width = (viewport.width - 20f - 10f * (columns - 1)) / columns, rowH = 178f;
                float thumbWidth = Mathf.Min(190f, width * .42f), textX = thumbWidth + 50f;
                Widgets.BeginScrollView(viewport, ref scroll, new Rect(0f, 0f, viewport.width - 20f, Mathf.Max(viewport.height - 1f, Mathf.Ceil(shown.Count / (float)columns) * (rowH + 8f))));
                if (shown.Count == 0) SharedPhotoUI.Label(new Rect(10f, 12f, viewport.width - 40f, 90f), "MF_FavoriteEmpty".Translate(), true);
                for (int i = 0; i < shown.Count; i++)
                {
                    var rec = shown[i]; Rect cell = new Rect(i % columns * (width + 10f), i / columns * (rowH + 8f), width, rowH);
                    if (cell.yMax < scroll.y || cell.y > scroll.y + viewport.height) continue;
                    bool chosen = selected.Contains(rec.id);
                    Widgets.DrawBoxSolid(cell, chosen ? new Color(.66f, .22f, .28f, .12f) : AlbumStyle.Theme.Mat);
                    SharedPhotoUI.Check(new Rect(cell.x + 8f, cell.y + 12f, 22f, 22f), chosen, true);
                    var image = PhotoTextureCache.Thumb(store.ThumbPath(rec), store.FilePath(rec));
                    if (image != null) GUI.DrawTexture(new Rect(cell.x + 38f, cell.y + 10f, thumbWidth, rowH - 20f), image, ScaleMode.ScaleToFit);
                    SharedPhotoUI.Label(new Rect(cell.x + textX, cell.y + 10f, width - textX - 10f, rowH - 66f), AlbumPresentation.Title(rec.DisplayTitle));
                    SharedPhotoUI.Label(new Rect(cell.x + textX, cell.y + rowH - 50f, width - textX - 10f, 44f), AlbumPresentation.Title(rec.dateLabel), true);
                    if (Widgets.ButtonInvisible(cell)) { if (!selected.Add(rec.id)) selected.Remove(rec.id); }
                }
                Widgets.EndScrollView();
                var film = AlbumFavorites.Selected(records, selected);
                if (SharedPhotoUI.Button(new Rect(page.x, page.yMax - h, page.width, h), "MF_FavoriteFilmApply".Translate(film.Count), film.Count >= MemoryNight.MinPhotos))
                { apply(AlbumFavorites.FilmIds(store, film, selected)); Close(); }
            }
        }
    }
}
