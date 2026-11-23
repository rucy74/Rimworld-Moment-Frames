using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MomentFrames
{
    /// <summary>"Chronicle" tab in a person's inspect pane, to the right of Health: every photo they appear in, oldest first, by year, as a
    /// timeline (small photo, title, date, who else was there, the sentence) — the album chronicle for one life. A row opens the album gallery
    /// on that photo with the rail listing this person's photos in the same order; the button opens the album chronicle filtered to them.</summary>
    public class ITab_Pawn_Chronicle : ITab
    {
        private const float HeaderH = 76f, LineX = 12f;

        /// <summary>One line of the timeline: a photo, or a moment without one (LifeEvents.cs).</summary>
        private struct Row
        {
            public PhotoRecord rec;
            public LifeEvent life;
            public int ticks, year;
            public float HeightFor(string pawnId, float width)
            {
                if (rec != null) return RecordOnly(rec)?RecordOnlyHeight(rec,width):PhotoRowHeight(rec, width);
                float w = width - LineX - 26f;
                return 36f * AlbumStyle.Scale + AlbumStyle.TextHeight(life.LineFor(pawnId), w, GameFont.Small)
                    + (life.NoteFor(pawnId).NullOrEmpty() ? 14f : 12f + AlbumStyle.TextHeight(life.NoteFor(pawnId), w, GameFont.Small)) + 16f;
            }
        }

        private readonly List<Row> rows = new List<Row>();
        private PhotoStore rowsStore;
        private int rowsVersion = -1;
        private string rowsKey;

        private Vector2 scroll;
        private bool milestonesOnly;
        private PhotoStore cachedStore;
        private int cachedVersion = -1;
        private string cachedKey;
        private List<PhotoRecord> entries = new List<PhotoRecord>();

        public ITab_Pawn_Chronicle()
        {
            size = new Vector2(600f, 520f);
            labelKey = "MF_TabChronicle";
        }

        protected override void UpdateSize()
        {
            // TabRect itself calls UpdateSize. Use PaneTopY directly, including the base tab strip's 30px.
            size = new Vector2(Mathf.Min(600f, UI.screenWidth), Mathf.Min(520f, Mathf.Max(0f, PaneTopY - 38f)));
        }

        /// <summary>Colonists, prisoners and slaves always have the tab (so it does not come and go); anyone else only once they are in a photo.</summary>
        public override bool IsVisible
        {
            get
            {
                if (MomentFramesMod.Settings?.chronicleTab == false) return false;
                var p = SelPawn;
                var store = PhotoStore.Instance;
                if (p == null || store == null || p.RaceProps == null || !p.RaceProps.Humanlike) return false;
                if (p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony || p.IsSlaveOfColony) return true;
                return store.ChronicleFor(p).Any();
            }
        }

        /// <summary>This pawn's photos, oldest first; milestones only when asked (probe too). Cached per store version.</summary>
        public List<PhotoRecord> EntriesOf(Pawn p, bool onlyMilestones)
        {
            var store = PhotoStore.Instance;
            if (store == null || p == null) { entries.Clear(); return entries; }
            string key = p.GetUniqueLoadID() + "|" + onlyMilestones;
            if (ReferenceEquals(cachedStore, store) && cachedVersion == store.Version && cachedKey == key) return entries;
            cachedStore = store;
            cachedVersion = store.Version;
            cachedKey = key;
            entries = store.ChronicleFor(p).Where(r => !onlyMilestones || Kinds.IsMilestone(r.kind) || Kinds.PersonalOnly(r.kind)).OrderBy(r => r.ticksAbs).ToList();
            return entries;
        }

        /// <summary>The photos (milestones only when asked) and the moments without a photo (always), oldest first.</summary>
        private List<Row> RowsOf(Pawn p, bool onlyMilestones)
        {
            var store = PhotoStore.Instance;
            var photos = EntriesOf(p, onlyMilestones);
            string key = p.GetUniqueLoadID() + "|" + onlyMilestones;
            if (ReferenceEquals(rowsStore, store) && rowsVersion == store.Version && rowsKey == key) return rows;
            rowsStore = store;
            rowsVersion = store.Version;
            rowsKey = key;
            rows.Clear();
            foreach (var r in photos) rows.Add(new Row { rec = r, ticks = r.ticksAbs, year = r.year });
            foreach (var e in store.LifeOf(p)) rows.Add(new Row { life = e, ticks = e.ticksAbs, year = e.year });
            rows.Sort((x, y) => x.ticks.CompareTo(y.ticks));
            return rows;
        }

        protected override void FillTab()
        {
            using (AlbumStyle.Begin()) FillStyled();
        }

        private void FillStyled()
        {
            var p = SelPawn;
            var store = PhotoStore.Instance;
            if (p == null || store == null) return;
            int total = store.CountFor(p);
            var list = RowsOf(p, milestonesOnly);
            int shownPhotos = list.Count(r => r.rec?.HasFile==true && !r.rec.Archived), shownLife = list.Count - shownPhotos;
            Rect all = new Rect(0f, 0f, size.x, size.y).ContractedBy(12f);
            AlbumStyle.DrawPaper(new Rect(0f, 0f, size.x, size.y));
            AlbumStyle.DrawWash(new Rect(all.x + 4f, all.y + 24f, 264f, 17f));
            Text.Font = GameFont.Medium;
            GUI.color = AlbumStyle.Theme.Ink;
            AlbumStyle.Label(new Rect(all.x, all.y, all.width - 12f, 34f), "MF_TabChronicleTitle".Translate(p.LabelShort));
            Text.Font = GameFont.Small;
            GUI.color = AlbumStyle.Theme.MutedInk;
            AlbumStyle.Label(new Rect(all.x, all.y + 38f, 172f, 26f), "MF_DesignNav_ChronicleCounts".Translate(shownPhotos, shownLife));
            GUI.color = Color.white;
            if (list.Count > 0)
            {
                var box = new Rect(all.x + 181f, all.y + 38f, 198f, 28f);
                Widgets.Checkbox(new Vector2(box.xMax - 24f, box.y + 2f), ref milestonesOnly);
                GUI.color = AlbumStyle.Theme.Ink;
                AlbumStyle.Label(new Rect(box.x, box.y, box.width - 30f, box.height), "MF_DesignNav_MilestonePhotosOnly".Translate());
                GUI.color = Color.white;
                if (AlbumStyle.Button(new Rect(all.xMax - 160f, all.y + 36f, 160f, 32f), "MF_TabOpenAlbum".Translate()))
                    MainTabWindow_Album.OpenFor(p, null, true, !milestonesOnly);
            }
            Rect body = new Rect(all.x, all.y + HeaderH, all.width, all.height - HeaderH);
            if (list.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = MainTabWindow_Album.InkFaint;
                AlbumStyle.Label(body.ContractedBy(20f), (total == 0 ? "MF_TabNoPhotos" : "MF_TabNoMilestones").Translate(p.LabelShort));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }
            Rect inner = body.ContractedBy(12f);
            float height = 0f;
            string id = p.GetUniqueLoadID();
            int lastYear = int.MinValue;
            float yearH = 36f * Mathf.Max(1f, AlbumStyle.Scale);
            Text.Font = GameFont.Small;
            foreach (var row in list) { if (row.year != lastYear) { height += yearH; lastYear = row.year; } height += row.HeightFor(id, inner.width - 16f); }
            Rect view = new Rect(0f, 0f, inner.width - 16f, height);
            Widgets.BeginScrollView(inner, ref scroll, view);
            Color line = AlbumStyle.Theme.MutedInk; line.a = .38f;
            Widgets.DrawBoxSolid(new Rect(LineX, 0f, 1f, height), line);
            float y = 0f;
            lastYear = int.MinValue;
            foreach (var row in list)
            {
                if (row.year != lastYear)
                {
                    lastYear = row.year;
                    GUI.color = MainTabWindow_Album.Ink;
                    // ToString(): a TaggedString would get the game's automatic date colouring instead of ink.
                    AlbumStyle.DrawWash(new Rect(LineX + 12f, y + 18f, 96f, 13f));
                    AlbumStyle.Label(new Rect(LineX + 12f, y + 2f, view.width - LineX - 12f, yearH - 4f), "MF_YearLabel".Translate(row.year));
                    GUI.color = Color.white;
                    y += yearH;
                }
                float h = row.HeightFor(id, view.width);
                if (y + h >= scroll.y && y <= scroll.y + inner.height)
                {
                    if (row.rec != null) DrawRow(new Rect(0f, y, view.width, h), row.rec, p, id, store);
                    else DrawLife(new Rect(0f, y, view.width, h), row.life, id);
                }
                y += h;
            }
            Widgets.EndScrollView();
        }

        /// <summary>A moment without a photo: a hollow mark on the time line, the line of text and its date.</summary>
        private static void DrawLife(Rect rowAll, LifeEvent e, string id)
        {
            Rect row = new Rect(LineX + 12f, rowAll.y, rowAll.width - LineX - 12f, rowAll.height);
            Widgets.DrawBoxSolid(new Rect(LineX - 4f, row.y + 10f, 9f, 9f), AlbumStyle.Theme.MutedInk);
            Widgets.DrawBoxSolid(new Rect(LineX - 2f, row.y + 12f, 5f, 5f), AlbumStyle.Theme.Paper);
            float tx = row.x + 10f, tw = row.xMax - tx - 4f;
            Text.Font = GameFont.Tiny;
            GUI.color = MainTabWindow_Album.InkFaint;
            float dateH = 25f * AlbumStyle.Scale;
            AlbumStyle.Label(new Rect(tx, row.y + 1f, tw, dateH), e.dateLabel ?? "");
            Text.Font = GameFont.Small;
            string title = e.LineFor(id);
            float titleH = AlbumStyle.TextHeight(title, tw, GameFont.Small);
            GUI.color = MainTabWindow_Album.Ink;
            AlbumStyle.BodyLabel(new Rect(tx, row.y + dateH + 5f, tw, titleH), title);
            string note = e.NoteFor(id);
            if (!note.NullOrEmpty())
            {
                GUI.color = MainTabWindow_Album.Ink;
                float noteY = row.y + dateH + titleH + 13f;
                AlbumStyle.BodyLabel(new Rect(tx, noteY, tw, row.yMax - noteY - 12f), note);
                TooltipHandler.TipRegion(row, e.LineFor(id) + "\n" + e.dateLabel + "\n\n" + note);
            }
            GUI.color = Color.white;
        }

        private void DrawRow(Rect rowAll, PhotoRecord rec, Pawn p, string id, PhotoStore store)
        {
            if(RecordOnly(rec)){DrawRecordOnly(rowAll,rec);return;}
            Rect row = new Rect(LineX + 12f, rowAll.y, rowAll.width - LineX - 12f, rowAll.height);
            Widgets.DrawBoxSolid(new Rect(LineX - 3f, row.y + 11f, 7f, 7f), AlbumStyle.Theme.Ink);
            var others = new List<string>();
            for (int i = 0; i < rec.people.Count; i++)
            {
                bool self = rec.peopleIds.Count > 0 ? i < rec.peopleIds.Count && rec.peopleIds[i] == id : rec.people[i] == p.LabelShort;
                if (!self) others.Add(rec.people[i]);
            }
            var previousBirthday = AlbumPresentation.PreviousBirthday(rec, store.Records);
            Rect photoContent = row;
            if (previousBirthday != null) photoContent.height -= 40f;
            DrawPhotoRow(photoContent, rec, store, others.Count > 0 ? string.Join(", ", others) : null);
            if (previousBirthday != null && AlbumStyle.Button(new Rect(row.x + 156f, row.yMax - 38f, row.width - 162f, 32f), "MF_Phase2_BirthdayCompare".Translate()))
            {
                Find.WindowStack.Add(new Dialog_MFBirthdayComparison(previousBirthday, rec));
                return;
            }
            if (Mouse.IsOver(row))
            {
                string note = rec.DisplayDesc;
                string eventPeople=AlbumPresentation.EventPeople(rec);
                TooltipHandler.TipRegion(row, rec.DisplayTitle + "\n" + rec.dateLabel+(eventPeople.NullOrEmpty()?"":"\n"+eventPeople)
                    + (note.NullOrEmpty() ? "" : "\n\n" + note)+(rec.Delayed?"\n* "+"MF_Delayed".Translate():""));
            }
            if (Widgets.ButtonInvisible(row))
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                MainTabWindow_Album.OpenFor(p, rec, true, !milestonesOnly);
            }
        }

        internal static float PhotoRowHeight(PhotoRecord rec, float width)
        {
            float textW = Mathf.Max(80f, width - LineX - 12f - 156f - 4f);
            return Mathf.Max(132f, 66f * AlbumStyle.Scale + Mathf.Max(EventIconSize,
                AlbumStyle.TextHeight(rec.DisplayTitle, textW - EventIconSize - 6f, GameFont.Small))
                + ChronicleNarrative.Height(rec,textW)) + 18f + (rec.kind == Kinds.Birthday ? 40f : 0f);
        }
        private static bool RecordOnly(PhotoRecord rec)=>rec.Archived||rec.eventOnly&&!rec.HasFile;

        private static float RecordOnlyHeight(PhotoRecord rec,float width)
        {
            float w=Mathf.Max(80f,width-LineX-26f);
            return 52f*AlbumStyle.Scale+AlbumStyle.TextHeight(rec.DisplayTitle,w,GameFont.Small)
                +ChronicleNarrative.Height(rec,w)
                +(AlbumPresentation.EventPeople(rec).NullOrEmpty()?0f:AlbumStyle.TextHeight(AlbumPresentation.EventPeople(rec),w,GameFont.Small)+8f)+20f;
        }
        private static void DrawRecordOnly(Rect rowAll,PhotoRecord rec)
        {
            Rect row=new Rect(LineX+12f,rowAll.y,rowAll.width-LineX-12f,rowAll.height);
            Widgets.DrawBoxSolid(new Rect(LineX-4f,row.y+10f,9f,9f),AlbumStyle.Theme.MutedInk);
            Widgets.DrawBoxSolid(new Rect(LineX-2f,row.y+12f,5f,5f),AlbumStyle.Theme.Paper);
            float x=row.x+10f,w=row.width-14f,y=row.y+1f;
            GUI.color=AlbumStyle.Theme.MutedInk;Text.Font=GameFont.Tiny;
            float dateH=25f*AlbumStyle.Scale;
            AlbumStyle.Label(new Rect(x,y,w,dateH),(rec.dateLabel??"")+" · "+(rec.Archived&&rec.HasFile?"MF_Archive_RecordLine":"MF_RecordOnly").Translate());y+=dateH+5f;
            GUI.color=AlbumStyle.Theme.Ink;Text.Font=GameFont.Small;
            float titleH=AlbumStyle.TextHeight(rec.DisplayTitle,w,GameFont.Small);
            AlbumStyle.BodyLabel(new Rect(x,y,w,titleH),rec.DisplayTitle);y+=titleH+6f;
            string eventPeople=AlbumPresentation.EventPeople(rec);
            if(!eventPeople.NullOrEmpty())
            {float h=AlbumStyle.TextHeight(eventPeople,w,GameFont.Small);GUI.color=AlbumStyle.Theme.MutedInk;AlbumStyle.BodyLabel(new Rect(x,y,w,h),eventPeople);y+=h+6f;}
            GUI.color=AlbumStyle.Theme.Ink;ChronicleNarrative.Draw(new Rect(x,y,w,Mathf.Max(1f,row.yMax-y-10f)),rec);
            TooltipHandler.TipRegion(row,rec.DisplayTitle+"\n"+rec.dateLabel+(eventPeople.NullOrEmpty()?"":"\n"+eventPeople)+"\n\n"+rec.DisplayDesc);
            GUI.color=Color.white;
        }
        private static float EventIconSize => 20f * AlbumStyle.Scale;

        private static void DrawPhotoRow(Rect row, PhotoRecord rec, PhotoStore store, string people)
        {
            Texture2D tex = PhotoTextureCache.Thumb(store.ThumbPath(rec), store.FilePath(rec));
            var card = new Rect(row.x + 6f, row.y + 5f, 142f, 118f);
            Widgets.DrawBoxSolid(new Rect(card.x + 2f, card.y + 3f, card.width, card.height), new Color(0f, 0f, 0f, .12f));
            Widgets.DrawBoxSolid(card, AlbumStyle.Theme.Mat);
            var image = new Rect(card.x + 6f, card.y + 7f, card.width - 12f, card.height - 14f);
            GUI.color = Color.white;
            if (tex != null) Widgets.DrawTextureFitted(image, tex, 1f);
            else {SharedPhotoUI.Label(image,"MF_UX_MissingShort".Translate(),true);TooltipHandler.TipRegion(image,"MF_UX_MissingPhoto".Translate());}
            GUI.DrawTexture(new Rect(card.center.x - 25f, card.y - 5f, 50f, 12f), MFIcons.Tape);
            float tx = row.x + 156f, tw = row.xMax - tx - 4f, y = row.y + 3f;
            Text.Font = GameFont.Tiny;
            GUI.color = AlbumStyle.Theme.MutedInk;
            float dateH = 25f * AlbumStyle.Scale;
            AlbumStyle.Label(new Rect(tx, y, tw, dateH), rec.dateLabel ?? ""); y += dateH + 4f;
            Text.Font = GameFont.Small;
            GUI.color = AlbumStyle.Theme.Ink;
            float titleW = Mathf.Max(1f, tw - EventIconSize - 6f);
            float titleH = Mathf.Max(EventIconSize, AlbumStyle.TextHeight(rec.DisplayTitle, titleW));
            PhotoEventIcons.DrawEvent(new Rect(tx, y + (titleH - EventIconSize) / 2f, EventIconSize, EventIconSize), rec, AlbumStyle.Theme.MutedInk);
            AlbumStyle.BodyLabel(new Rect(tx + EventIconSize + 6f, y, titleW, titleH), rec.DisplayTitle); y += titleH + 5f;
            if (!people.NullOrEmpty())
            {
                Text.Font = GameFont.Tiny; GUI.color = AlbumStyle.Theme.MutedInk;
                AlbumStyle.Label(new Rect(tx, y, tw, dateH), people); y += dateH + 4f;
            }
            Text.Font = GameFont.Small; GUI.color = AlbumStyle.Theme.Ink;
            ChronicleNarrative.Draw(new Rect(tx, y, tw, Mathf.Max(1f, row.yMax - y - 10f)), rec);
            GUI.color = Color.white;
        }
    }

    /// <summary>Puts the chronicle tab on every humanlike race (vanilla and modded) just before Health in the tab list — the pane draws the
    /// list right to left, so it sits to the right of Health.</summary>
    [StaticConstructorOnStartup]
    internal static class PawnChronicleTabSetup
    {
        static PawnChronicleTabSetup()
        {
            var tab = InspectTabManager.GetSharedInstance(typeof(ITab_Pawn_Chronicle));
            var health = InspectTabManager.GetSharedInstance(typeof(ITab_Pawn_Health));
            int added = 0;
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.race == null || !def.race.Humanlike || def.inspectorTabs == null || def.inspectorTabs.Contains(typeof(ITab_Pawn_Chronicle))) continue;
                int at = def.inspectorTabs.IndexOf(typeof(ITab_Pawn_Health));
                def.inspectorTabs.Insert(at < 0 ? 0 : at, typeof(ITab_Pawn_Chronicle));
                if (def.inspectorTabsResolved == null) def.inspectorTabsResolved = new List<InspectTabBase>();
                int atResolved = def.inspectorTabsResolved.IndexOf(health);
                def.inspectorTabsResolved.Insert(atResolved < 0 ? 0 : atResolved, tab);
                added++;
            }
            if (added == 0) Log.Warning("[MomentFrames] no humanlike race found for the chronicle tab");
        }
    }
}
