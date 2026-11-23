using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>"The same people, in the same place, again." A re-shoot that calls the colonists who were in a photo back to where it was taken
    /// and frames the new picture exactly like the old one. Whoever cannot come (dead, away, no longer one of us) is simply left out — the point
    /// is the ones who are still here.</summary>
    public static class Reunion
    {
        public enum State { Ready, Dead, Away, NotColonist, Busy }

        public struct Candidate
        {
            public string id;
            public string name;
            public Pawn pawn;       // the real living pawn, including people who cannot currently attend
            public State state;
            public string why;      // shown next to the name when they cannot come

            public bool CanCome => state == State.Ready;
        }

        /// <summary>Can this photo be re-shot with its people: the map still exists and the frame is known.</summary>
        public static bool Possible(PhotoRecord rec) =>
            rec != null && rec.kind != PetPhotos.Kind && rec.kind != PetMilestones.Kind
            && rec.HasCenter && rec.peopleIds != null && rec.peopleIds.Count > 0 && MapOf(rec) != null;

        public static Map MapOf(PhotoRecord rec) => rec == null ? null : Find.Maps.FirstOrDefault(m => m.uniqueID == rec.mapId);

        /// <summary>Everyone the photo remembers, with what is in their way now. Names come from the record, so someone long gone is still listed
        /// (that is the point of looking at an old photo).</summary>
        public static List<Candidate> Candidates(PhotoRecord rec)
        {
            var list = new List<Candidate>();
            if (rec?.peopleIds == null) return list;
            Map map = MapOf(rec);
            var seen = new HashSet<string>();
            for (int i = 0; i < rec.peopleIds.Count; i++)
            {
                string id = rec.peopleIds[i];
                if (id.NullOrEmpty() || !seen.Add(id)) continue;
                string name = rec.people != null && i < rec.people.Count ? rec.people[i] : id;
                Pawn found = null;
                foreach (var p in PawnsFinder.AllMapsWorldAndTemporary_Alive)
                    if (p.GetUniqueLoadID() == id) { found = p; break; }
                var c = new Candidate { id = id, name = name, pawn = found };
                if (found == null) { c.state = State.Dead; c.why = "MF_Reunion_Gone".Translate(); }
                else if (found.Dead) { c.state = State.Dead; c.pawn = null; c.why = "MF_Reunion_Dead".Translate(); }
                else if (!found.IsFreeColonist) { c.state = State.NotColonist; c.why = "MF_Reunion_NotOurs".Translate(); }
                else if (!BirthdayCarry.Available(found) || found.MapHeld != map) { c.state = State.Away; c.why = "MF_Reunion_Away".Translate(); }
                else if (!found.DevelopmentalStage.Baby() && !PhotoGatheringPlan.CanJoin(found, null)) { c.state = State.Busy; c.why = GroupPhoto.WhyNot(found, null, true); }
                else { c.state = State.Ready; }
                list.Add(c);
            }
            return list;
        }

        /// <summary>Calls those who can come to the old spot. The line-up, the drop-out rules and the "take it now" button are the group photo's;
        /// only the framing is fixed to the original picture.</summary>
        public static bool Start(PhotoRecord rec, List<Pawn> pawns, IDictionary<Pawn, Pawn> babyGuardians = null)
        {
            if (!Possible(rec)) return false;
            Map map = MapOf(rec);
            if (map == null || pawns == null || pawns.Count == 0) return false;
            IntVec3 spot = rec.center.ToIntVec3();
            if (!spot.InBounds(map)) return false;
            // Retain the chosen original people exactly; a stale selection cannot silently omit someone or add a newcomer.
            var fresh = new HashSet<Pawn>(Candidates(rec).Where(c => c.CanCome).Select(c => c.pawn));
            if (pawns.Any(p => p == null || !fresh.Contains(p)) || pawns.Distinct().Count() != pawns.Count) return false;
            if (babyGuardians != null && babyGuardians.Any(pair => !pawns.Contains(pair.Key) || !pair.Key.DevelopmentalStage.Baby()
                || !BirthdayCarry.CanCarry(pair.Value, pair.Key))) return false;
            var lord = GroupPhoto.StartPrepared(map, spot, pawns, Rot4.North, babyGuardians: babyGuardians);
            var job = lord?.LordJob as LordJob_MFGroupPhoto;
            if (job == null) return false;
            job.MakeReunion(rec);
            return true;
        }
    }

    /// <summary>The old picture and its real people, with one editable attendance list and the album's page style.</summary>
    public class Dialog_Reunion : Window
    {
        private readonly PhotoRecord rec;
        private List<Reunion.Candidate> candidates;
        private readonly HashSet<string> selected = new HashSet<string>();
        private readonly HashSet<string> flagged = new HashSet<string>();
        private readonly Dictionary<Pawn, Pawn> guardianChoices = new Dictionary<Pawn, Pawn>();
        private HashSet<string> previousSelection;
        private PhotoGatheringPlan preparation;
        private int preparationFrame = -1;
        private Vector2 scroll;
        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(800f, UI.screenWidth - 40f), Mathf.Min(760f, UI.screenHeight - 40f));

        public Dialog_Reunion(PhotoRecord rec)
        {
            this.rec = rec;
            candidates = Reunion.Candidates(rec);
            foreach (var c in candidates.Where(c => c.CanCome)) selected.Add(c.id);
            forcePause = false;
            doCloseX = false;
            doWindowBackground = false;
            absorbInputAroundWindow = false;
            closeOnAccept = false;
        }

        private void RefreshPreparation(bool force = false)
        {
            if (!force && preparationFrame == Time.frameCount) return;
            candidates = Reunion.Candidates(rec);
            var chosen = candidates.Where(c => selected.Contains(c.id) && c.CanCome).Select(c => c.pawn).ToList();
            var guardians = new Dictionary<Pawn, Pawn>();
            foreach (var baby in chosen.Where(p => p.DevelopmentalStage.Baby()))
                if (guardianChoices.TryGetValue(baby, out var adult)) guardians[baby] = adult;
            foreach (var baby in chosen.Where(p => p.DevelopmentalStage.Baby() && !guardians.ContainsKey(p)))
            {
                var adult = BirthdayCarry.Recommend(baby, null, guardians.Values, chosen);
                if (adult != null) guardians[baby] = adult;
            }
            preparation = PhotoGatheringPlan.Create(Reunion.MapOf(rec), rec == null ? IntVec3.Invalid : rec.center.ToIntVec3(),
                chosen, Rot4.North, babyGuardians: guardians);
            preparationFrame = Time.frameCount;
        }
        internal IReadOnlyList<Reunion.Candidate> CandidatesForDisplay { get { RefreshPreparation(); return candidates; } }
        internal List<Pawn> SelectedPawns
        {
            get { RefreshPreparation(); return candidates.Where(c => selected.Contains(c.id) && c.CanCome).Select(c => c.pawn).ToList(); }
        }
        internal int SelectedCount => selected.Count;
        internal bool CanUndoSelection => previousSelection != null;
        private string BlockReason
        {
            get
            {
                RefreshPreparation();
                if (!Reunion.Possible(rec)) return "MF_ReunionFailed".Translate();
                if (selected.Count == 0) return "MF_ReunionEmptySelection".Translate();
                if (selected.Any(id => !candidates.Any(c => c.id == id))) return "MF_ReunionFailed".Translate();
                var unavailable = candidates.Where(c => selected.Contains(c.id) && !c.CanCome).ToList();
                if (unavailable.Count > 0) return "MF_ReunionSelectionChanged".Translate(unavailable[0].name + ": " + StateLabel(unavailable[0]));
                return preparation.Reason;
            }
        }
        internal bool CanStart { get { RefreshPreparation(true); return BlockReason.NullOrEmpty(); } }
        internal void SelectAll()
        {
            RefreshPreparation(true);
            var before = new HashSet<string>(selected);
            selected.Clear();
            foreach (var c in candidates.Where(c => c.CanCome)) selected.Add(c.id);
            if (!before.SetEquals(selected)) previousSelection = before;
            preparationFrame = -1;
        }
        internal bool UndoSelection()
        {
            if (previousSelection == null) return false;
            RefreshPreparation(true);
            selected.Clear();
            foreach (var c in candidates.Where(c => c.CanCome && previousSelection.Contains(c.id))) selected.Add(c.id);
            previousSelection = null;
            preparationFrame = -1;
            return true;
        }
        internal void ToggleCandidate(int index)
        {
            RefreshPreparation(true);
            if (index < 0 || index >= candidates.Count) return;
            var c = candidates[index];
            if (selected.Contains(c.id)) selected.Remove(c.id);
            else if (c.CanCome) selected.Add(c.id);
            if (!selected.Contains(c.id))
                foreach (var baby in guardianChoices.Where(pair => pair.Key == c.pawn || pair.Value == c.pawn).Select(pair => pair.Key).ToList()) guardianChoices.Remove(baby);
            preparationFrame = -1;
        }
        internal bool StartSelected()
        {
            RefreshPreparation(true);
            string reason = BlockReason;
            if (!reason.NullOrEmpty())
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (Reunion.Start(rec, SelectedPawns, preparation.BabyGuardians)) return true;
            RefreshPreparation(true);
            string failed = BlockReason;
            Messages.Message(failed.NullOrEmpty() ? "MF_ReunionFailed".Translate().ToString() : failed, MessageTypeDefOf.RejectInput, false);
            return false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            SharedPhotoUI.KeepCameraFree(this);
            using (AlbumStyle.Begin())
            {
                RefreshPreparation();
                AlbumStyle.DrawPaper(inRect);
                Rect page = inRect.ContractedBy(24f);
                float control = SharedPhotoUI.ControlHeight;
                float titleHeight = Mathf.Max(control, AlbumStyle.TextHeight("MF_Reunion".Translate(), page.width - control - 12f, GameFont.Medium));
                AlbumStyle.DrawWash(new Rect(page.x - 5f, page.y + titleHeight * .45f, page.width - control, titleHeight * .55f));
                SharedPhotoUI.Heading(new Rect(page.x, page.y, page.width - control - 12f, titleHeight), "MF_Reunion".Translate());
                if (SharedPhotoUI.Button(new Rect(page.xMax - control, page.y, control, control), "×")) { Close(); return; }

                string reason = BlockReason;
                string status = reason.NullOrEmpty() ? "MF_ReunionFrameHelp".Translate().ToString() : reason;
                if (reason == preparation.Reason && !reason.NullOrEmpty())
                    status += PhotoPreparationChoices.RemoveHint(reason, preparation.Problems, preparation.ShortProblems, p => selected.Contains(p.GetUniqueLoadID()));
                // Shown order only: the arrangement still follows the original photo. Anyone who held the button back stays on top until the window closes.
                foreach (var p in preparation.Problems.Keys) flagged.Add(p.GetUniqueLoadID());
                foreach (var c in candidates) if (selected.Contains(c.id) && !c.CanCome) flagged.Add(c.id);
                var shownOrder = Enumerable.Range(0, candidates.Count).OrderBy(i => !flagged.Contains(candidates[i].id)).ToList();
                var extras = preparation.Attending.Where(p => !selected.Contains(p.GetUniqueLoadID())).ToList();
                if (extras.Count > 0) status += "\n" + "MF_ReunionExtraCarers".Translate(string.Join(", ", extras.Select(p => p.LabelShort)));
                float statusHeight = AlbumStyle.TextHeight(status, page.width) + 8f;
                string call = "MF_ReunionCall".Translate(SelectedCount);
                float buttonHeight = Mathf.Max(control, AlbumStyle.TextHeight(call, page.width - 12f) + 12f);
                float footerHeight = statusHeight + buttonHeight + 12f;
                Rect listRect = Dialog_BirthdaySetup.SettingsArea(page, titleHeight, footerHeight);
                float width = listRect.width - 20f;
                int columns = width >= 600f ? 2 : 1;
                float cellWidth = (width - (columns - 1) * 8f) / columns;
                float nameWidth = cellWidth - 126f;
                float rowHeight = Mathf.Max(78f, control, candidates.Count == 0 ? 0f : candidates.Max(c =>
                    AlbumStyle.TextHeight(c.name, nameWidth) + AlbumStyle.TextHeight(StateLabel(c), nameWidth) + 16f));
                if (candidates.Any(c => selected.Contains(c.id) && c.pawn?.DevelopmentalStage.Baby() == true)) rowHeight += control + 8f;
                float photoWidth = Mathf.Min(150f, width * .34f);
                string originalTitle = rec == null ? "" : rec.title.NullOrEmpty() ? rec.dateLabel : rec.title;
                string caption = "MF_ReunionSub".Translate(originalTitle);
                float previewHeight = Mathf.Max(108f, AlbumStyle.TextHeight(caption, width - photoWidth - 16f) + 8f);
                string count = "MF_ReunionGuests".Translate(SelectedCount, candidates.Count(c => c.CanCome), candidates.Count);
                float summaryHeight = AlbumStyle.TextHeight(count, width) + 8f;
                float half = (width - 8f) / 2f;
                float selectionHeight = Mathf.Max(control, AlbumStyle.TextHeight("MF_PreparationAll".Translate(), half - 12f) + 12f,
                    AlbumStyle.TextHeight("MF_PreparationUndo".Translate(), half - 12f) + 12f);
                float rowsStart = previewHeight + summaryHeight + selectionHeight + 24f;
                float contentHeight = rowsStart + Mathf.Ceil(candidates.Count / (float)columns) * (rowHeight + 6f) + 4f;
                Widgets.BeginScrollView(listRect, ref scroll, new Rect(0f, 0f, width, contentHeight));
                DrawOriginal(new Rect(0f, 0f, photoWidth, 108f));
                SharedPhotoUI.Label(new Rect(photoWidth + 16f, 0f, width - photoWidth - 16f, previewHeight), caption, true);
                SharedPhotoUI.Label(new Rect(0f, previewHeight + 8f, width, summaryHeight), count);
                float y = previewHeight + summaryHeight + 8f;
                if (SharedPhotoUI.Button(new Rect(0f, y, half, selectionHeight), "MF_PreparationAll".Translate())) SelectAll();
                if (CanUndoSelection && SharedPhotoUI.QuietButton(new Rect(half + 8f, y, half, selectionHeight), "MF_PreparationUndo".Translate())) UndoSelection();
                for (int i = 0; i < candidates.Count; i++)
                    CandidateRow(new Rect(i % columns * (cellWidth + 8f), rowsStart + i / columns * (rowHeight + 6f), cellWidth, rowHeight), shownOrder[i]);
                Widgets.EndScrollView();
                SharedPhotoUI.Label(new Rect(page.x, page.yMax - footerHeight, page.width, statusHeight), status, reason.NullOrEmpty());
                if (SharedPhotoUI.Button(new Rect(page.x, page.yMax - buttonHeight, page.width, buttonHeight), call, reason.NullOrEmpty())
                    && StartSelected()) Close();
            }
        }
        private string StateLabel(Reunion.Candidate c)
        {
            if (c.pawn != null && selected.Contains(c.id) && preparation != null && preparation.ShortProblems.TryGetValue(c.pawn, out string brief)) return brief;
            return c.CanCome ? "MF_Reunion_Ready".Translate().ToString()
                : c.why.NullOrEmpty() ? "MF_Why_Unfit".Translate().ToString() : c.why;
        }
        private void DrawOriginal(Rect r)
        {
            Widgets.DrawBoxSolid(r, AlbumStyle.Theme.Mat);
            var store = PhotoStore.Instance;
            var texture = store == null || rec == null || rec.eventOnly ? null : PhotoTextureCache.Thumb(store.ThumbPath(rec), store.FilePath(rec));
            if (texture != null) GUI.DrawTexture(r.ContractedBy(5f), texture, ScaleMode.ScaleToFit);
            else SharedPhotoUI.Label(r.ContractedBy(8f), "MF_NoPhoto".Translate(), true);
        }
        private void CandidateRow(Rect r, int index)
        {
            var c = candidates[index];
            bool chosen = selected.Contains(c.id), enabled = c.CanCome || chosen;
            bool baby = chosen && c.pawn?.DevelopmentalStage.Baby() == true;
            Rect choice = new Rect(r.x + 8f, r.yMax - SharedPhotoUI.ControlHeight - 4f, r.width - 16f, SharedPhotoUI.ControlHeight);
            if (baby)
            {
                preparation.BabyGuardians.TryGetValue(c.pawn, out var guardian);
                if (SharedPhotoUI.Button(choice, "MF_ReunionCarer".Translate(guardian?.LabelShort ?? "MF_ReunionChooseCarer".Translate().ToString()))) ChooseGuardian(c.pawn);
                r.height -= SharedPhotoUI.ControlHeight + 8f;
            }
            Color fill = AlbumStyle.Theme.Accent; fill.a = chosen ? .12f : .025f;
            Widgets.DrawBoxSolid(r, fill);
            SharedPhotoUI.Check(new Rect(r.x + 8f, r.y + (r.height - 24f) / 2f, 24f, 24f), chosen, enabled);
            SharedPhotoUI.Portrait(c.pawn, new Rect(r.x + 38f, r.y + (r.height - 64f) / 2f, 64f, 64f));
            float nameHeight = AlbumStyle.TextHeight(c.name, r.width - 126f);
            SharedPhotoUI.Label(new Rect(r.x + 112f, r.y + 8f, r.width - 126f, nameHeight), c.name, !c.CanCome);
            SharedPhotoUI.Label(new Rect(r.x + 112f, r.y + 8f + nameHeight, r.width - 126f, r.height - nameHeight - 16f), StateLabel(c), true);
            TooltipHandler.TipRegion(r, c.name + "\n" + StateLabel(c));
            if (enabled && Widgets.ButtonInvisible(r)) ToggleCandidate(index);
        }

        private void ChooseGuardian(Pawn baby)
        {
            RefreshPreparation(true);
            var occupied = preparation.BabyGuardians.Where(pair => pair.Key != baby).Select(pair => pair.Value).ToList();
            var options = Reunion.MapOf(rec).mapPawns.FreeColonistsSpawned.Where(p => !occupied.Contains(p) && BirthdayCarry.CanCarry(p, baby))
                .OrderBy(p => p.LabelShort).Select(p => new FloatMenuOption(p.LabelShort + (selected.Contains(p.GetUniqueLoadID()) ? "" : "MF_ReunionCarerAdded".Translate().ToString()),
                    () => { guardianChoices[baby] = p; preparationFrame = -1; })).ToList();
            if (options.Count == 0) options.Add(new FloatMenuOption("MF_GroupBabyNoGuardianShort".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
