using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>Guests first; optional photo settings fold away without losing the draft.</summary>
    public sealed class Dialog_BirthdaySetup : Window
    {
        private readonly Pawn subject;
        private readonly Map setupMap;
        private readonly int age;
        private readonly LordJob_MFGroupPhoto editing;
        private readonly HashSet<Pawn> selected = new HashSet<Pawn>();
        private readonly System.Random guestRandom = new System.Random();
        private HashSet<Pawn> previousSelection;
        private BirthdayPreparationPlan preparation;
        private int preparationFrame = -1;
        private IntVec3 place;
        private Building_PhotoSpot chosenPhotoSpot;
        private bool ChosenSpotValid => chosenPhotoSpot==null || chosenPhotoSpot.Spawned && !chosenPhotoSpot.Destroyed
            && chosenPhotoSpot.Map==setupMap && chosenPhotoSpot.Position==place;
        private Rot4 direction;
        private string nameSearch = "";
        private int reactionMode;
        private readonly Dictionary<string,int> reactions = new Dictionary<string,int>();
        private readonly Dictionary<string,int> faces = new Dictionary<string,int>();
        private readonly Dictionary<string,IntVec3> positions = new Dictionary<string,IntVec3>();
        private readonly PhotoFacePreview facePreview = new PhotoFacePreview();
        private bool rememberReaction, peopleFocus;
        internal bool UseSavedFrame = true;
        private Pawn guardian;
        private bool carryBaby, previewing, pickingAnchor;
        internal bool ExpandedSettings { get => PreparationSections.BirthdayLayout; set => PreparationSections.BirthdayLayout=value; }
        internal bool ExpandedExpressions { get => PreparationSections.BirthdayExpressions; set => PreparationSections.BirthdayExpressions=value; }
        private Vector2 scroll;
        private Rect setupRect;
        internal float LastContentEnd, LastContentHeight;
        private Map SubjectMap => subject?.MapHeld;
        private bool Held => BirthdayCarry.Holder(subject) != null;
        private bool AtSubject => Birthday.FixedSubject(subject) && !carryBaby;
        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(800f, UI.screenWidth - 40f), Mathf.Min(790f, UI.screenHeight - 40f));

        public Dialog_BirthdaySetup(Pawn subject, int age, LordJob_MFGroupPhoto editing, IntVec3? place)
        {
            this.subject = subject; setupMap=subject.MapHeld; this.age = age; this.editing = editing;
            this.place = place ?? editing?.spot ?? Birthday.SpotFor(subject)?.Position ?? subject.PositionHeld;
            chosenPhotoSpot=setupMap.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSpot>().FirstOrDefault(s=>s.Position==this.place);
            direction = editing?.FormationDirection ?? chosenPhotoSpot?.PreparationDirection ?? Rot4.North;
            reactionMode=editing?.CaptureReactionMode>=0?editing.CaptureReactionMode:PhotoReactions.PreparationMode;
            peopleFocus=editing?.PeopleFocus??true; UseSavedFrame=editing?.UseSavedFrame??true;
            if(editing!=null) foreach(var pair in editing.ReactionAssignments) reactions[pair.Key]=pair.Value;
            if(editing!=null) foreach(var pair in editing.FaceAssignments) faces[pair.Key]=pair.Value;
            if(editing!=null) PhotoPositions.Copy(PhotoPositions.Capture(editing.Members.ToList(),editing.Cells.ToList(),editing.spot,direction),positions);
            doCloseX = false; doWindowBackground = false; forcePause = false; absorbInputAroundWindow = false; closeOnAccept = false;
            if (editing != null) { editing.EditingBirthday = true; foreach (var p in editing.Members) selected.Add(p); }
            else { selected.Add(subject); foreach (var p in Birthday.Friends(subject)) selected.Add(p); }
            selected.Add(subject);
            guardian = editing?.BirthdayGuardian ?? BirthdayCarry.Recommend(subject, editing);
            carryBaby = subject.DevelopmentalStage.Baby() && (editing?.BirthdayGuardian != null
                || (editing == null && this.place != subject.PositionHeld));
            if(editing!=null) ReactionChoices.EnsurePreparation(reactions,editing.Members.Concat(editing.CarriedPhotoBabies)
                .Concat(new[] { subject,editing.BirthdayGuardian }).Where(p=>p!=null).Select(p=>p.GetUniqueLoadID()),ReactionChoices.Automatic);
            ReactionChoices.EnsurePreparation(reactions,setupMap.mapPawns.FreeColonistsSpawned.Concat(new[] { subject })
                .Select(p=>p.GetUniqueLoadID()));
            EnsureGuardian();
        }
        private bool Eligible(Pawn p) => p == subject || PhotoGatheringPlan.CanJoin(p, editing);
        private void EnsureGuardian() { if (carryBaby && guardian != null) selected.Add(guardian); }
        public override void PostClose()
        {
            facePreview.Dispose();
            previewUntil = 0f;
            if (pickingAnchor) Find.Targeter.StopTargeting();
            if (editing != null) editing.EditingBirthday = false; base.PostClose();
        }
        internal List<Pawn> Chosen => new[] { subject }.Concat(selected.Where(p => p != subject).OrderBy(p => p.ageTracker.AgeBiologicalYears)).ToList();
        internal Pawn Guardian => carryBaby ? guardian : null;
        private void InvalidatePreparation() => preparationFrame = -1;
        private BirthdayPreparationPlan Preparation
        {
            get
            {
                if (preparationFrame != Time.frameCount)
                {
                    preparation = BirthdayPreparationPlan.Create(subject, place, Chosen, direction, editing, Guardian, carryBaby, positions);
                    preparationFrame = Time.frameCount;
                }
                return preparation;
            }
        }
        internal static float ControlHeight => Mathf.Max(38f, AlbumStyle.StyleFor("Ag", GameFont.Small).fontSize * 1.7f + 8f);
        internal static float ControlHeightFor(float width)
        {
            var labels = new[] { "MF_BirthdayStartGather", "MF_BirthdayApplyGuests", "MF_BirthdayPreview", "MF_GroupShootNow" };
            return Mathf.Max(ControlHeight, labels.Max(key => AlbumStyle.TextHeight(key.Translate(99).ToString(), Mathf.Max(60f, (width - 10f) / 2f - 12f), GameFont.Small)) + 12f);
        }
        internal static Rect SettingsArea(Rect page, float titleHeight, float footerHeight) =>
            new Rect(page.x, page.y + titleHeight + 18f, page.width, Mathf.Max(40f, page.height - titleHeight - footerHeight - 30f));
        private static void Body(Rect r, string text, bool muted = false)
        {
            GUI.color = muted ? AlbumStyle.Theme.MutedInk : AlbumStyle.Theme.Ink;
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft; Text.WordWrap = true;
            AlbumStyle.BodyLabel(r, text); GUI.color = Color.white;
        }
        private static void Heading(Rect r, string text)
        {
            GUI.color = AlbumStyle.Theme.Ink; Text.Font = GameFont.Medium; Text.Anchor = TextAnchor.UpperLeft;
            AlbumStyle.BodyLabel(r, text); Text.Font = GameFont.Small; GUI.color = Color.white;
        }
        private static bool PageButton(Rect r, string text, bool active = true)
        {
            bool clicked = AlbumStyle.Button(r, "", active);
            TextAnchor anchor = Text.Anchor; bool wrap = Text.WordWrap; Color color = GUI.color;
            Text.Anchor = TextAnchor.MiddleCenter; Text.WordWrap = true;
            GUI.color = active ? AlbumStyle.Theme.Ink : AlbumStyle.Theme.MutedInk;
            GUI.Label(r.ContractedBy(6f), text, AlbumStyle.StyleFor(text, GameFont.Small));
            Text.Anchor = anchor; Text.WordWrap = wrap; GUI.color = color;
            return clicked;
        }
        private static bool Choice(Rect r, string text, bool chosen, bool active = true)
        {
            bool clicked = PageButton(r, text, active);
            if (chosen) Widgets.DrawBoxSolid(new Rect(r.x + 2f, r.yMax - 4f, r.width - 4f, 3f), AlbumStyle.Theme.Accent);
            return clicked;
        }
        public override void DoWindowContents(Rect r)
        {
            SharedPhotoUI.KeepCameraFree(this);
            using (AlbumStyle.Begin(true))
            {
                AlbumStyle.DrawPaper(r); Rect page = r.ContractedBy(24f); float control = ControlHeightFor(page.width - 20f);
                if (SubjectMap == null || SubjectMap!=setupMap || subject.Dead || !BirthdayCarry.Available(subject) || editing?.Finished == true)
                { Close(); return; }
                if (previewing)
                {
                    string info = "MF_BirthdayPreviewInfo".Translate(); float infoHeight = AlbumStyle.TextHeight(info, page.width, GameFont.Small);
                    Body(new Rect(page.x, page.y, page.width, infoHeight), info);
                    if (PageButton(new Rect(page.x, page.y + infoHeight + 6f, page.width, ControlHeight), "MF_BirthdayEditGuests".Translate()))
                    {
                        if (pickingAnchor) Find.Targeter.StopTargeting(); pickingAnchor = false;
                        previewing = false; windowRect = setupRect; previewUntil = 0f;
                    }
                    return;
                }
                float titleHeight = Mathf.Max(48f, AlbumStyle.TextHeight(Birthday.Title(subject, age), page.width - control*2 - 20f, GameFont.Medium) + 10f);
                AlbumStyle.DrawWash(new Rect(page.x - 5f, page.y + titleHeight * .45f, page.width - control, titleHeight * .55f));
                Heading(new Rect(page.x, page.y, page.width - control*2 - 20f, titleHeight), Birthday.Title(subject, age));
                PhotoGuide.HelpButton(new Rect(page.xMax-ControlHeight*2-8,page.y,ControlHeight,ControlHeight),"birthday",this);
                if (PageButton(new Rect(page.xMax - ControlHeight, page.y, ControlHeight, ControlHeight), "×")) { Close(); return; }
                if (AtSubject && place != subject.PositionHeld) { place = subject.PositionHeld; chosenPhotoSpot=null; InvalidatePreparation(); }
                EnsureGuardian(); var chosen = Chosen;
                var plan = Preparation;
                int moving = plan.Moving.Count;
                string status = !ChosenSpotValid ? "MF_PreparationSpotGone".Translate().ToString()
                    : plan.CanStart ? "MF_PreparationReady".Translate().ToString()
                    : plan.Reason + PhotoPreparationChoices.RemoveHint(plan.Reason, plan.Problems, plan.ShortProblems, p => p != subject && p != Guardian);
                string framingAdvice = plan.CanStart && UseSavedFrame ? PhotoFramingAdvice.ForPlan(chosenPhotoSpot, plan.Moving, plan.Cells,
                    Birthday.FixedSubject(subject) && guardian == null ? subject : null) : null;
                if (!framingAdvice.NullOrEmpty()) status += "\n" + framingAdvice;
                float statusHeight = AlbumStyle.TextHeight(status, page.width) + 8f;
                float footerHeight = statusHeight + control + 12f;
                Rect settings = SettingsArea(page, titleHeight, footerHeight);
                PhotoPreparationChoices.Remember(flagged, plan.Problems.Keys);
                var allRoster = SubjectMap.mapPawns.FreeColonistsSpawned.Concat(new[] { subject }).Distinct().OrderBy(p => p != subject).ThenBy(p => !flagged.Contains(p)).ThenBy(p => p.LabelShort).ToList();
                ReactionChoices.EnsurePreparation(reactions,allRoster.Select(p=>p.GetUniqueLoadID()));
                var roster = PhotoPreparationChoices.Filter(allRoster,nameSearch,p=>p==subject || carryBaby&&p==guardian || flagged.Contains(p));
                float width = settings.width - 20f;
                int columns = width >= 600f ? 2 : 1;
                float detailControl = Mathf.Max(ControlHeight, DirectionKeys.Max(key => AlbumStyle.TextHeight(key.Translate(), width / 4f - 18f)) + 12f,
                    AlbumStyle.TextHeight("MF_BabyCarryMode".Translate(), (width - 8f) / 2f - 12f) + 12f);
                float cellWidth = (width - (columns - 1) * 8f) / columns;
                float rowHeight = Mathf.Max(78f, ControlHeight, roster.Max(p => Mathf.Max(GuestStatus(p).HasValue ? GuestIconSize : 0f,
                    AlbumStyle.TextHeight(p.LabelShort, GuestNameWidth(p, cellWidth)))
                    + AlbumStyle.TextHeight(RoleFor(p), cellWidth - 126f)) + 16f);
                float third = (width - 16f) / 3f;
                var selectionKeys = new[] { "MF_PreparationAll", "MF_PreparationRandom", "MF_PreparationMore" };
                float selectionHeight = Mathf.Max(ControlHeight, selectionKeys.Max(key => AlbumStyle.TextHeight(key.Translate(), third - 12f)) + 12f);
                bool savedFrame=chosenPhotoSpot?.Framed==true;
                var chosenSpot=SubjectMap.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSpot>().FirstOrDefault(s=>s.Position==place);
                bool studio=chosenSpot is Building_PhotoStudio;
                if(studio)direction=chosenSpot.Rotation;
                float frameHeight=studio?SharedPhotoUI.StudioFrameHeight(savedFrame,UseSavedFrame,width):SharedPhotoUI.FrameChoiceHeight(savedFrame);
                string layoutSummary=PreparationSections.LayoutSummary(positions.Count,savedFrame&&UseSavedFrame,peopleFocus,studio);
                string expressionSummary=PreparationSections.ExpressionSummary(chosen,reactions,faces,reactionMode);
                string location=AtSubject ? "MF_PreparationHere".Translate().ToString() : PhotoLocationMapComponent.DisplayName(chosenSpot);
                float locationButtonWidth=Mathf.Min(160f,(width-16f)*.27f);
                float locationNameWidth=width-locationButtonWidth*2-16f;
                float locationHeight=Mathf.Max(ControlHeight,AlbumStyle.TextHeight(location,locationNameWidth-8f)+8f,
                    AlbumStyle.TextHeight("MF_LocationChange".Translate(),locationButtonWidth-12f)+12f,
                    AlbumStyle.TextHeight("MF_BirthdayPreview".Translate(),locationButtonWidth-12f)+12f);
                float rowsHeight=Mathf.Ceil(roster.Count/(float)columns)*(rowHeight+6);
                float contentHeight=locationHeight+ControlHeight+12+selectionHeight+8+SharedPhotoUI.SearchHeight+rowsHeight+8
                    +PreparationSections.FoldHeight(layoutSummary,width)+8+PreparationSections.FoldHeight(expressionSummary,width)+8+ControlHeight+8;
                if(subject.DevelopmentalStage.Baby()&&carryBaby)contentHeight+=ControlHeight+8;
                if(ExpandedSettings)contentHeight+=ControlHeight+8+detailControl*(subject.DevelopmentalStage.Baby()?3:2)+28+frameHeight-(studio?detailControl+12:0);
                if(ExpandedExpressions)contentHeight+=PreparationSections.ExpressionHeight(width,chosen.Count);
                facePreview.Begin(settings,scroll);
                Widgets.BeginScrollView(settings, ref scroll, new Rect(0f, 0f, width, contentHeight));
                float y = 0f;
                string facing = DirectionKeys[direction.AsInt].Translate();
                var locationRect=new Rect(0,y,locationNameWidth,locationHeight);
                Body(locationRect.ContractedBy(4f),location);
                TooltipHandler.TipRegion(locationRect,location+"\n"+(chosenSpot==null?"":PhotoLocationMapComponent.Context(chosenSpot)+" / ")
                    +facing+"\n"+PhotoLocationMapComponent.CoordinateHint(place));
                var changeRect=new Rect(locationNameWidth+8f,y,locationButtonWidth,locationHeight);
                if(PageButton(changeRect,"MF_LocationChange".Translate(),!AtSubject))ChooseSpot();
                if(AtSubject)TooltipHandler.TipRegion(changeRect,"MF_BirthdayAtBedHelp".Translate());
                if(PageButton(new Rect(width-locationButtonWidth,y,locationButtonWidth,locationHeight),"MF_BirthdayPreview".Translate()))Preview(moving);
                y+=locationHeight+8;
                if (subject.DevelopmentalStage.Baby() && carryBaby)
                {
                    string name = guardian?.LabelShort ?? "MF_BabyChooseGuardian".Translate().ToString();
                    if (QuietButton(new Rect(0f, y, width, ControlHeight), "MF_BabyGuardian".Translate(name), !Held))
                    {
                        var options = SubjectMap.mapPawns.FreeColonistsSpawned.Where(p => BirthdayCarry.CanCarry(p, subject, editing))
                            .Select(p => new FloatMenuOption(p.LabelShort, () => { guardian = p; EnsureGuardian(); InvalidatePreparation(); })).ToList();
                        if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
                    }
                    TooltipHandler.TipRegion(new Rect(0f, y, width, ControlHeight), "MF_BabyCarryHelp".Translate());
                    y += ControlHeight + 8f;
                }
                string undoLabel = "MF_PreparationUndo".Translate();
                float undoWidth = CanUndoSelection ? AlbumStyle.StyleFor(undoLabel, GameFont.Small).CalcSize(new GUIContent(undoLabel)).x + 12f : 0f;
                QuietButton(new Rect(0f, y, width - undoWidth - 8f, ControlHeight), "MF_PreparationGuests".Translate(chosen.Count), false);
                if (CanUndoSelection && QuietButton(new Rect(width - undoWidth, y, undoWidth, ControlHeight), undoLabel)) UndoSelection();
                y += ControlHeight + 4f;
                if (PageButton(new Rect(0f, y, third, selectionHeight), "MF_PreparationAll".Translate())) SelectGuests(1);
                if (PageButton(new Rect(third + 8f, y, third, selectionHeight), "MF_PreparationRandom".Translate()))
                {
                    int available = SubjectMap.mapPawns.FreeColonistsSpawned.Where(Eligible).Concat(new[] { subject, Guardian }).Where(p => p != null).Distinct().Count();
                    int minimum = Guardian != null && Guardian != subject ? 2 : 1;
                    var counts = new[] { 2, 4, 6, 8, Chosen.Count }.Select(n => Mathf.Clamp(n, minimum, available)).Distinct().OrderBy(n => n);
                    Find.WindowStack.Add(new FloatMenu(counts.Select(n => new FloatMenuOption("MF_PreparationRandomCount".Translate(n), () => SelectRandomGuests(n))).ToList()));
                }
                if (PageButton(new Rect(2f * (third + 8f), y, third, selectionHeight), "MF_PreparationMore".Translate()))
                    Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption> {
                        new FloatMenuOption("MF_BirthdayRecommended".Translate(), () => SelectGuests(0)),
                        new FloatMenuOption("MF_BirthdayOnlySelf".Translate(), () => SelectGuests(2))
                    }));
                TooltipHandler.TipRegion(new Rect(third + 8f, y, third, selectionHeight), "MF_PreparationRandomHelp".Translate());
                y += selectionHeight + 8f;
                string nextSearch=SharedPhotoUI.SearchField(new Rect(0f,y,width,SharedPhotoUI.SearchHeight),nameSearch,
                    Chosen.Count,Chosen.Count(p=>!roster.Contains(p)));
                if(nextSearch!=nameSearch) {nameSearch=nextSearch;scroll.y=0f;}
                y+=SharedPhotoUI.SearchHeight;
                for (int i = 0; i < roster.Count; i++)
                    GuestRow(new Rect((i % columns) * (cellWidth + 8f), y + (i / columns) * (rowHeight + 6f), cellWidth, rowHeight), roster[i]);
                y+=rowsHeight+8;
                ExpandedSettings=PreparationSections.Fold(width,ref y,layoutSummary,ExpandedSettings);
                if (ExpandedSettings)
                {
                    PositionControl(width,ref y,ControlHeight);
                    if (subject.DevelopmentalStage.Baby())
                    {
                        float half = (width - 8f) / 2f;
                        if (Choice(new Rect(0f, y, half, detailControl), "MF_BabyCarryMode".Translate(), carryBaby, !Held))
                        { carryBaby = true; chosenPhotoSpot=Birthday.SpotFor(subject); place = chosenPhotoSpot?.Position ?? place; EnsureGuardian(); InvalidatePreparation(); }
                        if (Choice(new Rect(half + 8f, y, half, detailControl), "MF_BabyHereMode".Translate(), !carryBaby, !Held)) { carryBaby = false; InvalidatePreparation(); }
                        y += detailControl + 8f;
                    }
                    if (!AtSubject && QuietButton(new Rect(0f, y, width, detailControl), "MF_BirthdayMoveAnchor".Translate())) BeginPickAnchor();
                    if (AtSubject)
                    {
                        QuietButton(new Rect(0f, y, width, detailControl), "MF_PreparationStay".Translate(), false);
                        TooltipHandler.TipRegion(new Rect(0f, y, width, detailControl), "MF_BirthdayAtBedHelp".Translate());
                    }
                    y += detailControl + 8f;
                    if (studio) SharedPhotoUI.StudioFrameChoice(new Rect(0,y,width,frameHeight),savedFrame,ref UseSavedFrame);
                    else
                    {
                        for (int i = 0; i < 4; i++)
                            if (Choice(new Rect(i * width / 4f, y, width / 4f - 6f, detailControl), DirectionKeys[i].Translate(), direction.AsInt == i)) { direction = new Rot4(i); InvalidatePreparation(); }
                        y += detailControl + 12f;
                        SharedPhotoUI.FrameChoice(new Rect(0,y,width,frameHeight),savedFrame,ref UseSavedFrame,ref peopleFocus);
                    }
                    y+=frameHeight;
                }
                ExpandedExpressions=PreparationSections.Fold(width,ref y,expressionSummary,ExpandedExpressions);
                if(ExpandedExpressions)PreparationSections.Expressions(width,ref y,chosen,allRoster,reactions,faces,ref reactionMode,ref rememberReaction,facePreview,
                    ()=>{if(reactionMode==PhotoReactions.ModeOff)reactionMode=PhotoReactions.ModePhoto;});
                SessionControls(width,ref y,ControlHeight);
                LastContentEnd=y;LastContentHeight=contentHeight;
                Widgets.EndScrollView();
                Body(new Rect(page.x, page.yMax - footerHeight, page.width, statusHeight), status, plan.CanStart);
                bool canStart = Preparation.CanStart && ChosenSpotValid;
                if (PageButton(new Rect(page.x, page.yMax - control, page.width, control),
                    (editing != null ? "MF_BirthdayApplyGuests" : moving == 0 ? "MF_GroupShootNow" : "MF_BirthdayStartGather").Translate(chosen.Count), canStart))
                {
                    ReactionChoices.EnsurePreparation(reactions,chosen.Select(p=>p.GetUniqueLoadID()));
                    if (Birthday.StartPrepared(subject, age, place, chosen, direction, editing, Guardian, carryBaby,reactionMode,peopleFocus,reactions,UseSavedFrame,faces,positions))
                    { SharedPhotoUI.RememberReaction(reactionMode,rememberReaction); Close(); }
                }
                facePreview.DrawHover(r);
            }
        }
        private readonly HashSet<Pawn> flagged = new HashSet<Pawn>();
        private static readonly string[] DirectionKeys = { "MF_DirectionNorth", "MF_DirectionEast", "MF_DirectionSouth", "MF_DirectionWest" };
        private static bool QuietButton(Rect r, string label, bool active = true)
        {
            if (active && Mouse.IsOver(r)) { var c = AlbumStyle.Theme.Accent; c.a = .1f; Widgets.DrawBoxSolid(r, c); }
            GUI.color = active ? AlbumStyle.Theme.Ink : AlbumStyle.Theme.MutedInk;
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.MiddleLeft; Text.WordWrap = false;
            AlbumStyle.HeaderLabel(r.ContractedBy(4f), label);
            GUI.color = Color.white; Text.Anchor = TextAnchor.UpperLeft; Text.WordWrap = true;
            return active && Widgets.ButtonInvisible(r);
        }
        internal void SelectGuests(int preset)
        {
            var before = new HashSet<Pawn>(selected);
            selected.Clear(); selected.Add(subject);
            foreach (var p in preset == 0 ? Birthday.Friends(subject) : preset == 1 ? SubjectMap.mapPawns.FreeColonistsSpawned.Where(Eligible) : Enumerable.Empty<Pawn>()) selected.Add(p);
            EnsureGuardian();
            RememberSelection(before);
        }
        internal void SelectRandomGuests(int count, System.Random random = null)
        {
            var before = new HashSet<Pawn>(selected);
            var pool = SubjectMap.mapPawns.FreeColonistsSpawned.Where(p => p != subject && p != Guardian && Eligible(p)).ToList();
            selected.Clear(); selected.Add(subject); EnsureGuardian();
            var rng = random ?? guestRandom;
            int take = Mathf.Clamp(count - selected.Count, 0, pool.Count);
            for (int i = 0; i < take; i++)
            {
                int at = rng.Next(i, pool.Count);
                var p = pool[at]; pool[at] = pool[i]; pool[i] = p;
                selected.Add(p);
            }
            RememberSelection(before);
        }
        private void RememberSelection(HashSet<Pawn> before)
        {
            if (!before.SetEquals(selected)) previousSelection = before;
            InvalidatePreparation();
        }
        internal bool CanUndoSelection => previousSelection != null;
        internal bool UndoSelection()
        {
            if (previousSelection == null) return false;
            selected.Clear();
            foreach (var p in previousSelection.Where(p => p != null && !p.Dead && p.MapHeld == SubjectMap && BirthdayCarry.Available(p))) selected.Add(p);
            selected.Add(subject); EnsureGuardian();
            previousSelection = null;
            InvalidatePreparation();
            return true;
        }
        internal static RenderTexture PortraitFor(Pawn p) => PortraitsCache.Get(p, new Vector2(64f, 64f), Rot4.South,
            new Vector3(0f, 0f, .3f), 1.4f);
        private string RoleFor(Pawn p)
        {
            string requiredRole = p == subject ? "MF_PreparationSubjectRole".Translate().ToString()
                : carryBaby && p == guardian ? "MF_PreparationGuardianRole".Translate().ToString() : "";
            if (selected.Contains(p) && Preparation.ShortProblems.TryGetValue(p, out string problem))
                return requiredRole.NullOrEmpty() ? problem : requiredRole + "\n" + problem;
            string status=Eligible(p)?PhotoGatheringAccess.Warning(p):GroupPhoto.WhyNot(p,editing,true);
            return requiredRole.NullOrEmpty()?status:status.NullOrEmpty()?requiredRole:requiredRole+"\n"+status;
        }
        private static float GuestIconSize => 20f * AlbumStyle.Scale;
        private PhotoIcon? GuestStatus(Pawn p) => PhotoEventIcons.ForParticipant(p,
            Eligible(p) || p == subject || (carryBaby && p == guardian), selected.Contains(p), editing,
            selected.Contains(p) && Preparation.ShortProblems.ContainsKey(p));
        private float GuestNameWidth(Pawn p, float rowWidth) => Mathf.Max(1f,
            rowWidth - 126f - (GuestStatus(p).HasValue ? GuestIconSize + 6f : 0f));
        internal bool ToggleGuest(Pawn p)
        {
            if (p == subject || (carryBaby && p == guardian) || (!Eligible(p) && !selected.Contains(p))) return false;
            if (!selected.Remove(p)) selected.Add(p);
            InvalidatePreparation();
            return true;
        }
        private void GuestRow(Rect row, Pawn p)
        {
            bool checkedOn = selected.Contains(p), eligible = Eligible(p), required = p == subject || (carryBaby && p == guardian);
            string role = RoleFor(p);
            var tint = AlbumStyle.Theme.Accent; tint.a = checkedOn ? .12f : Mouse.IsOver(row) ? .07f : 0f;
            Widgets.DrawBoxSolid(row, tint);
            float portraitY=row.center.y;
            Rect check = new Rect(row.x + 12f, portraitY - 10f, 20f, 20f);
            GUI.color = eligible ? AlbumStyle.Theme.Ink : AlbumStyle.Theme.MutedInk;
            Widgets.DrawBox(check, 1);
            GUI.color = Color.white;
            if (checkedOn)
            {
                var ink = eligible ? AlbumStyle.Theme.Ink : AlbumStyle.Theme.MutedInk;
                Widgets.DrawLine(new Vector2(check.x + 4f, check.y + 10f), new Vector2(check.x + 8f, check.y + 15f), ink, 2f);
                Widgets.DrawLine(new Vector2(check.x + 8f, check.y + 15f), new Vector2(check.x + 17f, check.y + 5f), ink, 2f);
            }
            facePreview.Draw(p,new Rect(row.x + 42f, portraitY - 32f, 64f, 64f),PhotoFaces.Choice(faces,p.GetUniqueLoadID()));
            var statusIcon = GuestStatus(p);
            float iconInset = statusIcon.HasValue ? GuestIconSize + 6f : 0f;
            float textX = row.x + 114f, nameWidth = GuestNameWidth(p, row.width);
            float lineHeight = Mathf.Max(statusIcon.HasValue ? GuestIconSize : 0f, AlbumStyle.TextHeight(p.LabelShort, nameWidth));
            float roleHeight = role.NullOrEmpty() ? 0f : AlbumStyle.TextHeight(role, row.width - 126f);
            float nameY = row.y + (row.height-lineHeight-roleHeight) / 2f;
            if (statusIcon.HasValue) PhotoIconArt.Draw(new Rect(textX, nameY + (lineHeight - GuestIconSize) / 2f,
                GuestIconSize, GuestIconSize), statusIcon.Value, AlbumStyle.Theme.MutedInk);
            Body(new Rect(textX + iconInset, nameY, nameWidth, lineHeight), p.LabelShort, !eligible);
            if (!role.NullOrEmpty()) Body(new Rect(textX, nameY + lineHeight, row.width - 126f, roleHeight), role, true);
            if (!required && (eligible || checkedOn) && Widgets.ButtonInvisible(row))
                ToggleGuest(p);
            TooltipHandler.TipRegion(row, p.LabelShort + (role.NullOrEmpty() ? "" : "\n" + role));
        }
        private void Preview(int moving)
        {
            CameraJumper.TryJump(new TargetInfo(place, SubjectMap));
            previewMap = SubjectMap; previewPlace = place; previewCount = moving; previewDirection = direction;
            previewUntil = float.MaxValue; setupRect = windowRect;
            float width = Mathf.Min(520f, UI.screenWidth - 24f);
            float infoHeight = AlbumStyle.TextHeight("MF_BirthdayPreviewInfo".Translate(), width - 48f, GameFont.Small);
            windowRect = new Rect(12f, 12f, width, infoHeight + ControlHeight + 60f); previewing = true;
        }
        private void BeginPickAnchor()
        {
            // Retain the roster and guardian while selecting an anchor, including during an existing gathering.
            Preview(Chosen.Count - (Birthday.FixedSubject(subject) ? 1 : 0));
            pickingAnchor = true;
            var parms = new TargetingParameters { canTargetLocations = true, canTargetPawns = false, canTargetBuildings = false, canTargetItems = false };
            Find.Targeter.BeginTargeting(parms, t => { place = t.Cell; chosenPhotoSpot=null; previewPlace = place; pickingAnchor = false; InvalidatePreparation(); }, null,
                t => t.IsValid && t.Cell.InBounds(SubjectMap) && t.Cell.Standable(SubjectMap));
        }
        private void PositionControl(float width,ref float y,float height)
        {
            if(SharedPhotoUI.Button(new Rect(0,y,width,height),"MF_Positions".Translate(),!AtSubject && Preparation.Moving.Count>0 && Preparation.Cells.Count==Preparation.Moving.Count))
                Find.WindowStack.Add(new Dialog_PhotoPositions(setupMap,place,direction,Preparation.Moving,Preparation.Cells,faces,p=>{PhotoPositions.Copy(p,positions);InvalidatePreparation();}));
            y+=height+8;
        }
        private void SessionControls(float width,ref float y,float height)
        {
            if(SharedPhotoUI.Button(new Rect(0,y,width,height),"MF_Sessions".Translate(),!AtSubject && chosenPhotoSpot!=null))
                PhotoSessionUI.Menu(()=>PhotoSession.Capture(chosenPhotoSpot,Chosen,Preparation.Moving,Preparation.Cells,direction,reactionMode,peopleFocus,UseSavedFrame,reactions,faces,
                    Guardian==null?null:new Dictionary<Pawn,Pawn>{{subject,Guardian}}),LoadSession);
            y+=height+8;
        }
        private void LoadSession(PhotoSession saved)
        {
            var people=PhotoSessionUI.Resolve(saved,setupMap);if(people==null)return;
            chosenPhotoSpot=saved.Spot(setupMap);place=chosenPhotoSpot.Position;
            direction=chosenPhotoSpot is Building_PhotoStudio?chosenPhotoSpot.Rotation:new Rot4(saved.rotation);
            selected.Clear();foreach(var p in people.Where(p=>!p.DevelopmentalStage.Baby()||p==subject))selected.Add(p);selected.Add(subject);
            carryBaby=subject.DevelopmentalStage.Baby();
            int at=saved.babyIds.IndexOf(subject.GetUniqueLoadID());
            guardian=at>=0 && at<saved.guardianIds.Count?people.FirstOrDefault(p=>p.GetUniqueLoadID()==saved.guardianIds[at]):null;
            if(carryBaby&&guardian==null)guardian=BirthdayCarry.Recommend(subject,editing);
            EnsureGuardian();PhotoPositions.Copy(saved.Positions,positions);
            PhotoPositions.Copy(ReactionChoices.Read(saved.people,saved.reactions),reactions);PhotoPositions.Copy(saved.Faces,faces);
            ReactionChoices.EnsurePreparation(reactions,Chosen.Select(p=>p.GetUniqueLoadID()));
            reactionMode=saved.reactionMode;peopleFocus=saved.peopleFocus;UseSavedFrame=saved.useFrame;InvalidatePreparation();
        }
        private void ChooseSpot()
        {
            Find.WindowStack.Add(new Dialog_PhotoLocation(SubjectMap,chosenPhotoSpot,(spot,showMap)=>
            {
                if(!spot.Spawned || spot.Destroyed || spot.Map!=SubjectMap)return;
                place=spot.Position;chosenPhotoSpot=spot;direction=spot.PreparationDirection;InvalidatePreparation();
                if(showMap)Preview(Preparation.Moving.Count);
            }));
        }
        private static Map previewMap;
        private static IntVec3 previewPlace;
        private static int previewCount;
        private static Rot4 previewDirection;
        private static float previewUntil;
        internal static void StopPreviewDrawing() { previewMap=null;previewUntil=0f; }
        internal static void DrawPreview()
        {
            if (PhotoCapture.HideGuides || previewMap != Find.CurrentMap || Time.realtimeSinceStartup > previewUntil) return;
            GenDraw.DrawFieldEdges(GroupFormation.Cells(previewPlace, previewCount, previewMap, previewDirection), Color.cyan);
        }
    }
}
