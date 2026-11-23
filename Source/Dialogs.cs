using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MomentFrames
{
    public class Dialog_ChoosePhoto : Window
    {
        private readonly string title;
        private readonly string currentId;
        private string pendingId;
        internal PhotoSource Source;
        private List<PhotoRecord> sourcePhotos;
        private readonly Action<string> onPick;
        private readonly HashSet<string> allowedIds;
        private readonly bool browsing;
        private Vector2 scroll;
        private List<PhotoRecord> list;
        private List<PhotoRecord> visible;
        private PhotoStore listStore;
        private int listVersion = -1;
        private string search = "", listSearch;
        internal readonly PhotoPickerFilters Filters=new PhotoPickerFilters();
        private const float Pad = 10f;
        internal Vector2 DrawnCardSize { get; private set; }
        internal int DrawnColumns { get; private set; }
        internal int DrawnThumbnailCount { get; private set; }
        internal Rect DrawnGridArea { get; private set; }

        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(900f, UI.screenWidth - 40f), Mathf.Min(720f, UI.screenHeight - 40f));

        public Dialog_ChoosePhoto(Building_PhotoFrame frame) : this(frame.LabelCap, frame.photoId, frame.SetPhoto) { }

        /// <summary>Generic picker: title for the header, the id currently shown (highlighted), and what to do with the chosen id.</summary>
        public Dialog_ChoosePhoto(string title, string currentId, Action<string> onPick) : this(title, currentId, onPick, null, false) { }

        public Dialog_ChoosePhoto(string title, string currentId, Action<string> onPick, IEnumerable<string> photoIds, bool browsing)
        {
            this.title = title;
            this.currentId = currentId; pendingId = currentId;
            Source = PhotoSources.Of(PhotoStore.Instance?.Get(currentId));
            this.onPick = onPick;
            allowedIds = photoIds == null ? null : new HashSet<string>(photoIds);
            this.browsing = browsing;
            doCloseX = false;
            doCloseButton = false;
            doWindowBackground = false;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            forcePause = true;
            closeOnAccept = false;
        }

        /// <summary>The shown title and note, the original text and the names can all be found, regardless of letter case.</summary>
        public static bool MatchesSearch(PhotoRecord rec, string query)
        {
            if (rec == null) return false;
            query = (query ?? "").Trim();
            if (query.Length == 0) return true;
            bool Has(string text) => text != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            return Has(rec.dateLabel) || Has(rec.DisplayTitle) || Has(rec.DisplayDesc) || Has(rec.title) || Has(rec.desc) || rec.people?.Any(Has) == true;
        }

        private List<PhotoRecord> Photos(PhotoStore store)
        {
            // Sorted once per album change; searching only filters that list when the query changes.
            if (!ReferenceEquals(listStore, store) || list == null || (store != null && listVersion != store.Version))
            {
                listStore = store;
                listVersion = store?.Version ?? -1;
                list = store == null ? new List<PhotoRecord>() : store.Records
                    .Where(r => r.HasFile && (allowedIds == null || allowedIds.Contains(r.id)))
                    .OrderByDescending(r => r.ticksAbs).ToList();
                listSearch = null;
            }
            if (visible == null || listSearch != Filters.Key + "/" + Source)
            {
                sourcePhotos = list.Where(r => PhotoSources.Of(r) == Source).ToList();
                visible = Filters.Apply(sourcePhotos);
                listSearch = Filters.Key + "/" + Source;
            }
            return visible;
        }

        internal IReadOnlyList<PhotoRecord> VisiblePhotos => Photos(PhotoStore.Instance);
        internal string PendingPhotoId => pendingId;
        internal bool PreviewPhoto(string id)
        {
            if (!Photos(PhotoStore.Instance).Any(r => r.id == id)) return false;
            pendingId = id; return true;
        }
        internal void SetSearch(string value)
        {
            value = value ?? "";
            if (value.Length > 100) value = value.Substring(0, 100);
            if (search == value) return;
            search = value;Filters.Search=value;
            scroll = Vector2.zero;
        }
        internal bool SelectPhoto(string id)
        {
            Photos(PhotoStore.Instance);
            if (id == null || !list.Any(rec => rec.id == id)) return false;
            onPick?.Invoke(id);
            SoundDefOf.Click.PlayOneShotOnCamera();
            Close();
            return true;
        }

        private static GUIStyle WrappedStyle(string text, GameFont font = GameFont.Small)
        {
            var style = AlbumStyle.StyleFor(text, font);
            style.wordWrap = true;
            style.clipping = TextClipping.Clip;
            return style;
        }

        public override void DoWindowContents(Rect inRect)
        {
            using (AlbumStyle.Begin())
            {
                AlbumStyle.DrawPaper(inRect);
                Rect page = inRect.ContractedBy(inRect.width < 500f ? 18f : 24f);
                float control = SharedPhotoUI.ControlHeight;
                string heading = browsing ? title : "MF_PhotoPickerTitle".Translate(title).ToString();
                float headingWidth = Mathf.Max(1f, page.width - control - 12f);
                var headingStyle = WrappedStyle(heading, GameFont.Medium);
                float headingLine = headingStyle.CalcHeight(new GUIContent("Ag"), headingWidth);
                float headingHeight = Mathf.Max(control, Mathf.Min(headingLine * 2f, headingStyle.CalcHeight(new GUIContent(heading ?? ""), headingWidth)));
                Rect headingRect = new Rect(page.x, page.y, headingWidth, headingHeight);
                AlbumStyle.DrawWash(new Rect(page.x - 4f, page.y + headingHeight * .45f, headingWidth, headingHeight * .55f));
                GUI.color = AlbumStyle.Theme.Ink;
                GUI.Label(headingRect, heading ?? "", headingStyle);
                GUI.color = Color.white;
                TooltipHandler.TipRegion(headingRect, heading ?? "");
                if (SharedPhotoUI.QuietButton(new Rect(page.xMax - control, page.y, control, control), "×")) { Close(); return; }

                Text.Font = GameFont.Small;
                var store = PhotoStore.Instance;
                var photos = Photos(store);
                float y=headingRect.yMax+14f;
                float third = (page.width - 16f) / 3f;
                foreach (var value in new[] { PhotoSource.Colony, PhotoSource.Computer, PhotoSource.Legacy })
                {
                    var button = new Rect(page.x + (int)value * (third + 8f), y, third, control);
                    if (SharedPhotoUI.Button(button, PhotoSources.Label(value))) { Source = value; scroll = Vector2.zero; }
                    if (Source == value) Widgets.DrawBoxSolid(new Rect(button.x + 3, button.yMax - 3, button.width - 6, 2), AlbumStyle.Theme.Accent);
                }
                photos = Photos(store); y += control + 8f;
                if(Filters.Draw(new Rect(page.x,y,page.width,Filters.Height),sourcePhotos,new[]{pendingId}))scroll=Vector2.zero;
                search=Filters.Search;photos=Photos(store);y+=Filters.Height+8f;

                string closeLabel = "CloseButton".Translate();
                float closeWidth = Mathf.Min(page.width, Mathf.Max(140f, AlbumStyle.StyleFor(closeLabel).CalcSize(new GUIContent(closeLabel)).x + 24f));
                float closeHeight = Mathf.Max(control, AlbumStyle.TextHeight(closeLabel, closeWidth - 12f) + 12f);
                Rect footer = new Rect(page.x, page.yMax - closeHeight, closeWidth, closeHeight);
                if (SharedPhotoUI.Button(footer, closeLabel)) { Close(); return; }
                if (!browsing)
                {
                    var selected = list.FirstOrDefault(r => r.id == pendingId);
                    var confirm = new Rect(page.xMax - closeWidth, footer.y, closeWidth, closeHeight);
                    if (SharedPhotoUI.Button(confirm, "MF_Imports_Select".Translate(), selected != null)) { SelectPhoto(pendingId); return; }
                    var note = new Rect(footer.xMax + 8, footer.y, Mathf.Max(1, confirm.x - footer.xMax - 16), closeHeight);
                    SharedPhotoUI.Label(note, selected == null ? "MF_Imports_NoSelection".Translate().ToString() : selected.DisplayTitle, true);
                    if (selected != null) TooltipHandler.TipRegion(note, selected.DisplayTitle + " · " + PhotoSources.Label(PhotoSources.Of(selected)));
                }
                Rect area = new Rect(page.x, y, page.width, Mathf.Max(1f, footer.y - 12f - y));
                DrawnGridArea = area;
                DrawnThumbnailCount = 0;
                if (photos.Count == 0)
                {
                    DrawnCardSize = Vector2.zero; DrawnColumns = 0;
                    string empty = list.Count == 0 ? "MF_NoPhotosYet" : sourcePhotos.Count > 0 ? "MF_DesignNav_NoMatches"
                        : Source == PhotoSource.Computer ? "MF_Imports_EmptyComputer" : Source == PhotoSource.Legacy ? "MF_Imports_EmptyLegacy" : "MF_NoPhotosYet";
                    SharedPhotoUI.Label(area, empty.Translate(), true);
                    return;
                }
                float gridWidth = Mathf.Max(1f, area.width - 16f);
                float desiredWidth = Mathf.Max(208f, 208f * AlbumStyle.Scale);
                int cols = Mathf.Max(1, Mathf.FloorToInt((gridWidth + Pad) / (desiredWidth + Pad)));
                float cellWidth = (gridWidth - (cols - 1) * Pad) / cols;
                float textWidth = Mathf.Max(1f, cellWidth - 20f);
                // Keep two title lines at the user's chosen size; clipping leaves the date in its own row.
                float lineHeight = AlbumStyle.TextHeight("Ag", textWidth, GameFont.Small);
                float titleHeight = lineHeight * 2f + 2f;
                float dateHeight = lineHeight * 2f + 2f;
                float imageHeight = textWidth / 1.5f;
                float cellHeight = 10f + imageHeight + 6f + titleHeight + 2f + dateHeight + 10f;
                DrawnCardSize = new Vector2(cellWidth, cellHeight); DrawnColumns = cols;
                int rows = (photos.Count + cols - 1) / cols;
                float viewHeight = rows * (cellHeight + Pad) - Pad;
                scroll.y = Mathf.Clamp(scroll.y, 0f, Mathf.Max(0f, viewHeight - area.height));
                Rect view = new Rect(0f, 0f, gridWidth, viewHeight);
                Widgets.BeginScrollView(area, ref scroll, view);
                // Only visible rows load a thumbnail, even when a long game has hundreds of photographs.
                int first = Mathf.Max(0, (int)(scroll.y / (cellHeight + Pad))) * cols;
                int last = Mathf.Min(photos.Count, ((int)((scroll.y + area.height) / (cellHeight + Pad)) + 1) * cols);
                for (int i = first; i < last; i++)
                {
                    var rec = photos[i];
                    int row = i / cols, column = i % cols;
                    Rect cell = new Rect(column * (cellWidth + Pad), row * (cellHeight + Pad), cellWidth, cellHeight);
                    bool current = rec.id == pendingId;
                    Widgets.DrawBoxSolid(cell, current ? Color.Lerp(AlbumStyle.Theme.Mat, AlbumStyle.Theme.Accent, .16f) : AlbumStyle.Theme.Mat);
                    if (current) Widgets.DrawBoxSolid(new Rect(cell.x, cell.yMax - 3f, cell.width, 3f), AlbumStyle.Theme.Accent);
                    if (Mouse.IsOver(cell))
                    {
                        Color hover = AlbumStyle.Theme.Accent; hover.a = .10f;
                        Widgets.DrawBoxSolid(cell, hover);
                    }
                    Rect img = new Rect(cell.x + 10f, cell.y + 10f, textWidth, imageHeight);
                    var tex = PhotoTextureCache.Thumb(store.ThumbPath(rec), store.FilePath(rec));
                    DrawnThumbnailCount++;
                    if (tex != null) Widgets.DrawTextureFitted(img, tex, 1f);
                    else SharedPhotoUI.Label(img, "MF_MissingFile".Translate(), true);
                    if (current)
                    {
                        string selectedLabel = "MF_PhotoPickerCurrent".Translate();
                        float badgeWidth = Mathf.Min(img.width, AlbumStyle.StyleFor(selectedLabel).CalcSize(new GUIContent(selectedLabel)).x + 14f);
                        float badgeHeight = AlbumStyle.TextHeight(selectedLabel, Mathf.Max(1f, badgeWidth - 12f)) + 8f;
                        Rect badge = new Rect(img.x, img.y, badgeWidth, badgeHeight);
                        Widgets.DrawBoxSolid(badge, AlbumStyle.Theme.Mat);
                        SharedPhotoUI.Label(badge.ContractedBy(4f), selectedLabel);
                    }
                    GUI.color = AlbumStyle.Theme.Ink;
                    GUI.Label(new Rect(cell.x + 10f, img.yMax + 6f, textWidth, titleHeight), rec.DisplayTitle ?? "", WrappedStyle(rec.DisplayTitle));
                    GUI.color = AlbumStyle.Theme.MutedInk;
                    GUI.Label(new Rect(cell.x + 10f, img.yMax + 8f + titleHeight, textWidth, dateHeight), rec.dateLabel ?? "", WrappedStyle(rec.dateLabel));
                    GUI.color = Color.white;
                    if (Mouse.IsOver(cell))
                        TooltipHandler.TipRegion(cell, rec.DisplayTitle + "\n" + rec.dateLabel + (rec.people?.Count > 0 ? "\n" + (rec.kind == PetPhotos.Kind || rec.kind == PetMilestones.Kind ? "MF_PetSubjects" : "MF_People").Translate(string.Join(", ", rec.people)) : ""));
                    if (Widgets.ButtonInvisible(cell))
                    {
                        if (browsing) SelectPhoto(rec.id); else PreviewPhoto(rec.id);
                        break;
                    }
                }
                Widgets.EndScrollView();
            }
        }
    }

    /// <summary>Edit the sentence under a photo (shown in the gallery, the chronicle and album.html). Multi-line; empty is allowed.</summary>
    public class Dialog_EditDescription : Window
    {
        private readonly PhotoRecord rec;
        private readonly PhotoEdit edit;   // an edited version's own note instead of the photo's sentence
        private string text;
        private bool focused;

        public override Vector2 InitialSize => new Vector2(620f, 300f);

        public Dialog_EditDescription(PhotoRecord rec, PhotoEdit edit = null)
        {
            this.rec = rec;
            this.edit = edit;
            text = edit != null ? (edit.note.NullOrEmpty() ? rec.desc ?? "" : edit.note) : rec.desc ?? "";
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;   // a stray click must not lose what was written
            closeOnAccept = false;
            forcePause = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 30f, 28f), edit == null ? "MF_EditDescTitle".Translate() : "MF_EditNoteTitle".Translate(rec.edits.IndexOf(edit) + 1));
            GUI.SetNextControlName("MF_DescField");
            text = Widgets.TextArea(new Rect(inRect.x, inRect.y + 32f, inRect.width, inRect.height - 32f - 44f), text);
            if (text != null && text.Length > 600) text = text.Substring(0, 600);
            if (!focused)
            {
                UI.FocusControl("MF_DescField", this);
                focused = true;
            }
            if (Widgets.ButtonText(new Rect(inRect.xMax - 250f, inRect.yMax - 34f, 120f, 32f), "CancelButton".Translate())) Close();
            if (Widgets.ButtonText(new Rect(inRect.xMax - 120f, inRect.yMax - 34f, 120f, 32f), "OK".Translate()))
            {
                string t = (text ?? "").Trim();
                if (edit == null) rec.SetDescription(t);
                else edit.note = t == (rec.desc ?? "").Trim() ? null : t;   // the photo's own sentence left as it was: nothing of its own
                PhotoStore.Instance?.NotifyChanged();
                Close();
            }
        }
    }

    /// <summary>Edit a photo's caption — the title shown under it everywhere (frame inspect line, album, chronicle, album.html, letters).</summary>
    public class Dialog_RenamePhoto : Window
    {
        private readonly PhotoRecord rec;
        private readonly PhotoEdit edit;   // an edited version's own title instead of the photo's
        private string text;
        private bool focused;

        public override Vector2 InitialSize => new Vector2(520f, 150f);

        public Dialog_RenamePhoto(PhotoRecord rec, PhotoEdit edit = null)
        {
            this.rec = rec;
            this.edit = edit;
            text = edit != null && !edit.title.NullOrEmpty() ? edit.title : rec.title ?? "";
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;   // a stray click must not lose what was written
            forcePause = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 30f, 28f), edit == null ? "MF_RenameTitle".Translate() : "MF_RenameEditTitle".Translate(rec.edits.IndexOf(edit) + 1));
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (enter) Event.current.Use();
            GUI.SetNextControlName("MF_RenameField");
            text = Widgets.TextField(new Rect(inRect.x, inRect.y + 32f, inRect.width, 30f), text, 80);
            if (!focused)
            {
                UI.FocusControl("MF_RenameField", this);
                focused = true;
            }
            if (Widgets.ButtonText(new Rect(inRect.xMax - 250f, inRect.yMax - 34f, 120f, 32f), "CancelButton".Translate())) Close();
            if (Widgets.ButtonText(new Rect(inRect.xMax - 120f, inRect.yMax - 34f, 120f, 32f), "OK".Translate()) || enter) Apply();
        }

        private void Apply()
        {
            string t = (text ?? "").Trim();
            if (edit != null)
            {
                edit.title = t.Length == 0 || t == rec.title ? null : t;   // empty or the photo's own title: the version has none of its own
                PhotoStore.Instance?.NotifyChanged();
                Close();
                return;
            }
            if (t.Length == 0)
            {
                Messages.Message("MF_RenameEmpty".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            rec.title = t;
            PhotoStore.Instance?.NotifyChanged();
            Close();
        }
    }

    /// <summary>Which part of a trimmed photo a collage window shows: the three choices drawn side by side, the suggested one marked (where its
    /// people stood, else the middle). Opens when a tall photo or a panorama goes into a window, and from the board's "Visible part" button.</summary>
    public class Dialog_MFSlotCrop : Window
    {
        private readonly Building_PhotoCollage board;
        private readonly int slot;
        private static readonly int[] Order = { FrameLook.CropStart, FrameLook.CropMiddle, FrameLook.CropEnd };

        public Dialog_MFSlotCrop(Building_PhotoCollage board, int slot)
        {
            this.board = board;
            this.slot = slot;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(840f, 340f);

        public static string PosLabel(int pos, bool wide) =>
            (pos == FrameLook.CropStart ? (wide ? "MF_CropLeft" : "MF_CropTop") : pos == FrameLook.CropEnd ? (wide ? "MF_CropRight" : "MF_CropBottom") : "MF_CropMiddle").Translate();

        public override void DoWindowContents(Rect inRect)
        {
            var rec = board.RecordAt(slot);
            Texture tex = board.SlotTexture(slot);
            if (rec == null || tex == null || tex.height <= 0 || !board.Spawned)
            {
                Close();
                return;
            }
            float boxAspect = board.WindowAspect, photoAspect = (float)tex.width / tex.height;
            bool wide = photoAspect > boxAspect;
            int suggested = board.RecommendedCrop(slot), current = board.CropAt(slot);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "MF_CropTitle".Translate(slot + 1));
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 36f, inRect.width, 24f), rec.title + " — " + "MF_CropPrompt".Translate());
            const float gap = 18f;
            float pw = (inRect.width - 2f * gap) / 3f, ph = pw / boxAspect;
            for (int k = 0; k < Order.Length; k++)
            {
                int pos = Order[k];
                var r = new Rect(k * (pw + gap), 70f, pw, ph);
                GUI.DrawTextureWithTexCoords(r, tex, FrameLook.CoverUV(photoAspect, boxAspect, pos));
                GUI.color = pos == current ? new Color(1f, 0.8f, 0.3f) : new Color(1f, 1f, 1f, 0.35f);
                Widgets.DrawBox(r.ExpandedBy(3f), pos == current ? 3 : 1);
                GUI.color = Color.white;
                Widgets.DrawHighlightIfMouseover(r);
                string label = PosLabel(pos, wide);
                if (pos == suggested) label += " (" + "MF_CropRecommended".Translate() + ")";
                Text.Anchor = TextAnchor.UpperCenter;
                Widgets.Label(new Rect(r.x, r.yMax + 8f, r.width, 26f), label);
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonInvisible(r))
                {
                    board.SetCrop(slot, pos);
                    Close();
                }
            }
        }
    }
}
