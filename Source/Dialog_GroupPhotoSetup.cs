using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    public sealed class Dialog_GroupPhotoSetup : Window
    {
        private Building_PhotoSpot spot;
        private readonly Map setupMap;
        private readonly LordJob_MFGroupPhoto editing;
        private readonly HashSet<Pawn> selected = new HashSet<Pawn>();
        private readonly System.Random random = new System.Random();
        private HashSet<Pawn> previous;
        private Dictionary<Pawn,Pawn> previousGuardians;
        private readonly Dictionary<Pawn,Pawn> babyGuardians = new Dictionary<Pawn,Pawn>();
        private Rot4 direction;
        private Vector2 scroll;
        private string nameSearch = "";
        private int reactionMode;
        private readonly Dictionary<string,int> reactions = new Dictionary<string,int>();
        private readonly Dictionary<string,int> faces = new Dictionary<string,int>();
        private readonly Dictionary<string,IntVec3> positions = new Dictionary<string,IntVec3>();
        private readonly HashSet<Pawn> flagged = new HashSet<Pawn>();
        private readonly PhotoFacePreview facePreview = new PhotoFacePreview();
        private bool rememberReaction, peopleFocus;
        internal bool UseSavedFrame = true;
        private PhotoGatheringPlan plan;
        private int planFrame = -1;
        internal bool ExpandedSettings { get => PreparationSections.GroupLayout; set => PreparationSections.GroupLayout=value; }
        internal bool ExpandedExpressions { get => PreparationSections.GroupExpressions; set => PreparationSections.GroupExpressions=value; }
        private bool previewing;
        private Rect setupRect;
        internal float LastContentEnd, LastContentHeight;
        private float sectionsY;
        private static Dialog_GroupPhotoSetup previewDialog;
        private bool ValidSpot => spot!=null && spot.Spawned && !spot.Destroyed && spot.Map==setupMap && spot.Faction==Faction.OfPlayer;
        private IEnumerable<Pawn> Roster => setupMap.mapPawns.FreeColonistsSpawned.Concat(PhotoCapture.VisiblePawns(setupMap)
            .Where(p=>p.DevelopmentalStage.Baby() && p.IsFreeColonist)).Concat(selected).Where(p=>p!=null).Distinct();
        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(Mathf.Min(800f,UI.screenWidth-40f),Mathf.Min(790f,UI.screenHeight-40f));
        public Dialog_GroupPhotoSetup(Building_PhotoSpot spot, LordJob_MFGroupPhoto editing = null)
        {
            this.spot=spot; setupMap=spot.Map; this.editing=editing; direction=editing?.FormationDirection ?? spot.PreparationDirection;
            reactionMode=editing?.CaptureReactionMode>=0?editing.CaptureReactionMode:PhotoReactions.PreparationMode;
            peopleFocus=editing?.PeopleFocus??true; UseSavedFrame=editing?.UseSavedFrame??true;
            if(editing!=null) foreach(var pair in editing.ReactionAssignments) reactions[pair.Key]=pair.Value;
            if(editing!=null) foreach(var pair in editing.FaceAssignments) faces[pair.Key]=pair.Value;
            if(editing!=null) PhotoPositions.Copy(PhotoPositions.Capture(editing.Members.ToList(),editing.Cells.ToList(),editing.spot,direction),positions);
            doCloseX=false; doWindowBackground=false; forcePause=false; absorbInputAroundWindow=false; closeOnAccept=false;
            foreach(var p in editing?.Members ?? Roster.Where(Eligible).ToList()) selected.Add(p);
            if(editing!=null) foreach(var baby in editing.CarriedPhotoBabies)
            { selected.Add(baby);babyGuardians[baby]=editing.CarrierForBaby(baby); }
            EnsureBabyGuardians();
            if(editing!=null) ReactionChoices.EnsurePreparation(reactions,editing.Members.Concat(editing.CarriedPhotoBabies)
                .Where(p=>p!=null).Select(p=>p.GetUniqueLoadID()),ReactionChoices.Automatic);
            ReactionChoices.EnsurePreparation(reactions,Roster.Select(p=>p.GetUniqueLoadID()));
            if(editing!=null) editing.EditingPhotoSetup=true;
        }
        public override void PostClose() { facePreview.Dispose(); if(editing!=null) editing.EditingPhotoSetup=false; if(previewDialog==this)previewDialog=null; base.PostClose(); }
        private bool Eligible(Pawn p) => p!=null && p.DevelopmentalStage.Baby()
            ? BirthdayCarry.Available(p) && !p.Dead && p.IsFreeColonist && p.MapHeld==setupMap : PhotoGatheringPlan.CanJoin(p,editing);
        internal Pawn GuardianFor(Pawn baby) => babyGuardians.TryGetValue(baby,out var guardian)?guardian:null;
        private bool RequiredGuardian(Pawn p) => babyGuardians.Values.Contains(p);
        private void EnsureBabyGuardians()
        {
            foreach(var baby in babyGuardians.Keys.Where(p=>!selected.Contains(p)).ToList()) babyGuardians.Remove(baby);
            foreach(var baby in selected.Where(p=>p.DevelopmentalStage.Baby()).OrderBy(p=>p.thingIDNumber).ToList())
            {
                if(!babyGuardians.TryGetValue(baby,out var guardian) || guardian==null)
                {
                    guardian=BirthdayCarry.Recommend(baby,editing,babyGuardians.Values);
                    if(guardian!=null) babyGuardians[baby]=guardian;
                }
                if(guardian!=null) selected.Add(guardian);
            }
        }
        internal List<Pawn> Chosen => selected.OrderBy(p=>p.ageTracker.AgeBiologicalYears).ThenBy(p=>p.thingIDNumber).ToList();
        internal bool CanUndoSelection => previous!=null;
        internal bool CanStart => Plan.CanStart;
        internal int MaxRandomCount
        {
            get
            {
                var available=Roster.Where(Eligible).ToList();
                return available.Count(p=>!p.DevelopmentalStage.Baby())
                    + BirthdayCarry.RecommendDistinct(available.Where(p=>p.DevelopmentalStage.Baby()),editing).Count;
            }
        }
        private void Invalidate() => planFrame=-1;
        private PhotoGatheringPlan Plan
        {
            get { if(planFrame!=Time.frameCount) { plan=PhotoGatheringPlan.Create(ValidSpot?setupMap:null,spot?.Position ?? IntVec3.Invalid,Chosen,direction,editing,babyGuardians,positions); planFrame=Time.frameCount; } return plan; }
        }
        private void Remember(HashSet<Pawn> before,Dictionary<Pawn,Pawn> guardians)
        { if(!before.SetEquals(selected)) {previous=before;previousGuardians=guardians;} Invalidate(); }
        internal void SelectGuests(int preset)
        {
            var before=new HashSet<Pawn>(selected);var guardians=new Dictionary<Pawn,Pawn>(babyGuardians);selected.Clear();babyGuardians.Clear();
            if(preset!=2) foreach(var p in Roster.Where(Eligible).ToList()) selected.Add(p);
            EnsureBabyGuardians();Remember(before,guardians);
        }
        internal void SelectFamily(Pawn subject)
        {
            var before=new HashSet<Pawn>(selected);var guardians=new Dictionary<Pawn,Pawn>(babyGuardians);
            selected.Clear();babyGuardians.Clear();
            foreach(var p in Roster.Where(p=>Eligible(p)&&Building_PhotoStudio.ImmediateFamily(subject,p)).ToList()) selected.Add(p);
            EnsureBabyGuardians();Remember(before,guardians);
        }
        internal void SelectRandomGuests(int total,System.Random rng=null)
        {
            var before=new HashSet<Pawn>(selected);var guardians=new Dictionary<Pawn,Pawn>(babyGuardians);var available=Roster.Where(Eligible).ToList();
            rng=rng??random;var pairs=BirthdayCarry.RecommendDistinct(available.Where(p=>p.DevelopmentalStage.Baby()),editing,rng);
            var pool=available.Where(p=>!p.DevelopmentalStage.Baby()).Concat(pairs.Keys).ToList();
            selected.Clear();babyGuardians.Clear();int target=Mathf.Clamp(total,0,pool.Count);
            for(int i=0;i<pool.Count && selected.Count<target;i++)
            {
                int at=rng.Next(i,pool.Count);var p=pool[at];pool[at]=pool[i];pool[i]=p;
                if(selected.Contains(p)) continue;
                if(p.DevelopmentalStage.Baby())
                {
                    var guardian=pairs[p];
                    if(selected.Count+(selected.Contains(guardian)?1:2)>target) continue;
                    babyGuardians[p]=guardian;selected.Add(guardian);
                }
                selected.Add(p);
            }
            Remember(before,guardians);
        }
        internal bool ToggleGuest(Pawn p)
        {
            if(p==null || RequiredGuardian(p) || (!Eligible(p) && !selected.Contains(p))) return false;
            if(!selected.Remove(p)) selected.Add(p);EnsureBabyGuardians();Invalidate();return true;
        }
        internal bool UndoSelection()
        {
            if(previous==null) return false; selected.Clear();
            foreach(var p in previous.Where(p=>p!=null && !p.Dead && BirthdayCarry.Available(p) && p.MapHeld==setupMap)) selected.Add(p);
            babyGuardians.Clear();
            if(previousGuardians!=null) foreach(var pair in previousGuardians.Where(pair=>selected.Contains(pair.Key) && pair.Value!=null && !pair.Value.Dead && pair.Value.Map==setupMap)) babyGuardians[pair.Key]=pair.Value;
            EnsureBabyGuardians();previous=null;previousGuardians=null;Invalidate();return true;
        }
        internal void SetDirection(Rot4 value) { direction=spot is Building_PhotoStudio?spot.Rotation:value; Invalidate(); }
        internal bool StartSelected()
        {
            if(!ValidSpot) return false;
            ReactionChoices.EnsurePreparation(reactions,Chosen.Select(p=>p.GetUniqueLoadID()));
            var lord=GroupPhoto.StartPrepared(setupMap,spot.Position,Chosen,direction,editing,babyGuardians,reactionMode,peopleFocus,reactions,UseSavedFrame,faces,positions);
            if(lord==null) return false; spot.PreparationDirection=direction;
            SharedPhotoUI.RememberReaction(reactionMode,rememberReaction); return true;
        }
        private string Role(Pawn p)
        {
            string role=RequiredGuardian(p)?"MF_GroupBabyGuardianRequired".Translate().ToString()
                : GuardianFor(p)!=null?"MF_GroupBabyWithGuardian".Translate(GuardianFor(p).LabelShort).ToString():"";
            if(selected.Contains(p) && Plan.ShortProblems.TryGetValue(p,out var why)) return role.NullOrEmpty()?why:role+"\n"+why;
            string status=Eligible(p)?PhotoGatheringAccess.Warning(p):GroupPhoto.WhyNot(p,editing,true);
            return role.NullOrEmpty()?status:status.NullOrEmpty()?role:role+"\n"+status;
        }
        private static float GuestIconSize => 20f * AlbumStyle.Scale;
        private PhotoIcon? GuestStatus(Pawn p) => PhotoEventIcons.ForParticipant(p, Eligible(p), selected.Contains(p), editing,
            selected.Contains(p) && Plan.ShortProblems.ContainsKey(p));
        private float GuestNameWidth(Pawn p, float rowWidth) => Mathf.Max(1f,
            rowWidth - 126f - (GuestStatus(p).HasValue ? GuestIconSize + 6f : 0f));
        private static readonly string[] DirectionKeys={"MF_DirectionNorth","MF_DirectionEast","MF_DirectionSouth","MF_DirectionWest"};
        public override void DoWindowContents(Rect r)
        {
            SharedPhotoUI.KeepCameraFree(this);
            using(AlbumStyle.Begin(true))
            {
                AlbumStyle.DrawPaper(r);
                if(!ValidSpot || editing?.Finished==true) { Close(); return; }
                Rect page=r.ContractedBy(24f); float control=SharedPhotoUI.ControlHeight;
                if(previewing)
                {
                    string info="MF_BirthdayPreviewInfo".Translate();float height=AlbumStyle.TextHeight(info,page.width-control*2-20f);
                    SharedPhotoUI.Label(new Rect(page.x,page.y,page.width-control*2-20f,height),info);
                    PhotoGuide.HelpButton(new Rect(page.xMax-control*2-8,page.y,control,control),"family",this);
                if(SharedPhotoUI.Button(new Rect(page.xMax-control,page.y,control,control),"×")) {Close();return;}
                    if(SharedPhotoUI.Button(new Rect(page.x,page.y+height+6f,page.width,control),"MF_GroupSetupReturn".Translate()))
                    {previewing=false;previewDialog=null;windowRect=setupRect;}
                    return;
                }
                string title="MF_GroupSetupTitle".Translate(); float titleHeight=Mathf.Max(control,AlbumStyle.TextHeight(title,page.width-control*2-20f,GameFont.Medium));
                AlbumStyle.DrawWash(new Rect(page.x-5f,page.y+titleHeight*.5f,page.width-control,titleHeight*.5f));
                SharedPhotoUI.Heading(new Rect(page.x,page.y,page.width-control*2-20f,titleHeight),title);
                PhotoGuide.HelpButton(new Rect(page.xMax-control*2-8,page.y,control,control),"family",this);
                if(SharedPhotoUI.Button(new Rect(page.xMax-control,page.y,control,control),"×")) {Close();return;}
                var current=Plan; string status=current.CanStart?(current.BabyGuardians.Count>0?"MF_GroupBabyReady":"MF_GroupSetupReady").Translate().ToString():current.Reason+PhotoPreparationChoices.RemoveHint(current.Reason,current.Problems,current.ShortProblems,p=>!RequiredGuardian(p));
                string framingAdvice = current.CanStart && UseSavedFrame ? PhotoFramingAdvice.ForPlan(spot, current.Moving, current.Cells) : null;
                if (!framingAdvice.NullOrEmpty()) status += "\n" + framingAdvice;
                float statusHeight=AlbumStyle.TextHeight(status,page.width)+8f;
                string startLabel=(editing!=null?"MF_GroupSetupApply":"MF_BirthdayStartGather").Translate(Chosen.Count);
                float footerControl=Mathf.Max(control,AlbumStyle.TextHeight(startLabel,page.width-12f)+12f);
                float footerHeight=statusHeight+footerControl+12f;
                Rect area=Dialog_BirthdaySetup.SettingsArea(page,titleHeight,footerHeight); float width=area.width-20f;
                PhotoPreparationChoices.Remember(flagged,current.Problems.Keys);
                var allRoster=Roster.OrderBy(p=>!flagged.Contains(p)).ThenBy(p=>p.LabelShort).ToList();
                ReactionChoices.EnsurePreparation(reactions,allRoster.Select(p=>p.GetUniqueLoadID()));
                var roster=PhotoPreparationChoices.Filter(allRoster,nameSearch,p=>RequiredGuardian(p)||flagged.Contains(p));
                int cols=width>=600f?2:1;float cellWidth=(width-(cols-1)*8f)/cols;
                float rowHeight=Mathf.Max(78f,roster.DefaultIfEmpty().Max(p=>p==null?0:Mathf.Max(GuestStatus(p).HasValue?GuestIconSize:0f,
                    AlbumStyle.TextHeight(p.LabelShort,GuestNameWidth(p,cellWidth)))+AlbumStyle.TextHeight(Role(p),cellWidth-126f)+16f));
                float third=(width-16f)/3f; var keys=new[]{"MF_PreparationAll","MF_PreparationRandom","MF_GroupSetupClear"};
                float choices=Mathf.Max(control,keys.Max(k=>AlbumStyle.TextHeight(k.Translate(),third-12f))+12f);
                float directionHeight=Mathf.Max(control,DirectionKeys.Max(k=>AlbumStyle.TextHeight(k.Translate(),width/4f-18f))+12f);
                bool savedFrame=spot.Framed;
                bool studio=spot is Building_PhotoStudio;
                float frameHeight=studio?SharedPhotoUI.StudioFrameHeight(savedFrame,UseSavedFrame,width):SharedPhotoUI.FrameChoiceHeight(savedFrame);
                string layoutSummary=PreparationSections.LayoutSummary(positions.Count,savedFrame&&UseSavedFrame,peopleFocus,studio);
                string expressionSummary=PreparationSections.ExpressionSummary(Chosen,reactions,faces,reactionMode);
                float rowsHeight=Mathf.Ceil(roster.Count/(float)cols)*(rowHeight+6f);
                string changeLabel="MF_PreparationChangeSpot".Translate(),previewLabel="MF_BirthdayPreview".Translate();
                float spotButton=Mathf.Min(170f,(width-16f)*.3f);
                float spotRow=Mathf.Max(control,AlbumStyle.TextHeight(changeLabel,spotButton-12f)+12f,AlbumStyle.TextHeight(previewLabel,spotButton-12f)+12f);
                float content=spotRow+control+12+choices+8+SharedPhotoUI.SearchHeight+rowsHeight+8
                    +PreparationSections.FoldHeight(layoutSummary,width)+8+PreparationSections.FoldHeight(expressionSummary,width)+8+control+8;
                if(spot is Building_PhotoStudio)content+=control+8;
                if(ExpandedSettings)content+=control+8+(studio?0:directionHeight+16)+frameHeight;
                if(ExpandedExpressions)content+=PreparationSections.ExpressionHeight(width,Chosen.Count);
                facePreview.Begin(area,scroll);
                Widgets.BeginScrollView(area,ref scroll,new Rect(0,0,width,content));float y=0;
                string location="MF_PreparationSelectedSpot".Translate(spot.LabelShort,spot.Position.x,spot.Position.z);
                var locationRect=new Rect(0,y,width-spotButton*2-16f,spotRow);
                SharedPhotoUI.QuietButton(locationRect,location,false);
                TooltipHandler.TipRegion(locationRect,location+"\n"+"MF_PreparationSpotPolicy".Translate());
                if(SharedPhotoUI.Button(new Rect(width-spotButton*2-8f,y,spotButton,spotRow),changeLabel)) ChooseSpot();
                if(SharedPhotoUI.Button(new Rect(width-spotButton,y,spotButton,spotRow),previewLabel)) BeginPreview(control);
                y+=spotRow+8;
                string optionsLink="MF_Learn_SetupOptions".Translate();
                if(ExpandedSettings&&ExpandedExpressions)optionsLink=optionsLink.Replace('↓','↑');
                float linkWidth=Mathf.Min(width*.4f,AlbumStyle.StyleFor(optionsLink,GameFont.Small).CalcSize(new GUIContent(optionsLink)).x+20);
                string undo="MF_PreparationUndo".Translate();float undoWidth=CanUndoSelection?AlbumStyle.StyleFor(undo,GameFont.Small).CalcSize(new GUIContent(undo)).x+12f:0f;
                SharedPhotoUI.QuietButton(new Rect(0,y,width-undoWidth-linkWidth-16f,control),"MF_PreparationGuests".Translate(Chosen.Count),false);
                if(CanUndoSelection && SharedPhotoUI.QuietButton(new Rect(width-linkWidth-8f-undoWidth,y,undoWidth,control),undo)) UndoSelection();
                if(SharedPhotoUI.QuietButton(new Rect(width-linkWidth,y,linkWidth,control),optionsLink)) ToggleSections();
                y+=control+4f;
                if(SharedPhotoUI.Button(new Rect(0,y,third,choices),keys[0].Translate())) SelectGuests(0);
                if(SharedPhotoUI.Button(new Rect(third+8f,y,third,choices),keys[1].Translate()))
                {
                    int available=MaxRandomCount;
                    if(available>0) Find.WindowStack.Add(new FloatMenu(new[]{2,4,6,8,Chosen.Count}.Select(n=>Mathf.Clamp(n,1,available)).Distinct().OrderBy(n=>n)
                        .Select(n=>new FloatMenuOption("MF_PreparationRandomCount".Translate(n),()=>SelectRandomGuests(n))).ToList()));
                }
                if(SharedPhotoUI.Button(new Rect(2f*(third+8f),y,third,choices),keys[2].Translate())) SelectGuests(2); y+=choices+8f;
                if(spot is Building_PhotoStudio)
                {
                    var familyRect=new Rect(0,y,width,control);
                    if(SharedPhotoUI.Button(familyRect,"MF_StudioFamily".Translate()))
                        Find.WindowStack.Add(new FloatMenu(allRoster.Where(Eligible).Select(p=>new FloatMenuOption(p.LabelShort,()=>SelectFamily(p))).ToList()));
                    TooltipHandler.TipRegion(familyRect,"MF_StudioFamilyHelp".Translate());y+=control+8f;
                }
                string nextSearch=SharedPhotoUI.SearchField(new Rect(0,y,width,SharedPhotoUI.SearchHeight),nameSearch,
                    Chosen.Count,Chosen.Count(p=>!roster.Contains(p)));
                if(nextSearch!=nameSearch) {nameSearch=nextSearch;scroll.y=0f;}
                y+=SharedPhotoUI.SearchHeight;
                for(int i=0;i<roster.Count;i++)
                {
                    var p=roster[i];var row=new Rect((i%cols)*(cellWidth+8f),y+(i/cols)*(rowHeight+6f),cellWidth,rowHeight);
                    bool on=selected.Contains(p),available=Eligible(p);var tint=AlbumStyle.Theme.Accent;tint.a=on ? .12f : Mouse.IsOver(row) ? .07f : 0f;Widgets.DrawBoxSolid(row,tint);
                    float portraitY=row.center.y;
                    SharedPhotoUI.Check(new Rect(row.x+12f,portraitY-10f,20f,20f),on,available);
                    facePreview.Draw(p,new Rect(row.x+42f,portraitY-32f,64f,64f),PhotoFaces.Choice(faces,p.GetUniqueLoadID()));string role=Role(p);
                    var statusIcon=GuestStatus(p);float iconInset=statusIcon.HasValue?GuestIconSize+6f:0f,nameWidth=GuestNameWidth(p,cellWidth);
                    float nameHeight=Mathf.Max(statusIcon.HasValue?GuestIconSize:0f,AlbumStyle.TextHeight(p.LabelShort,nameWidth)),roleHeight=role.NullOrEmpty()?0:AlbumStyle.TextHeight(role,cellWidth-126f);
                    float nameY=row.y+(row.height-nameHeight-roleHeight)/2f;
                    if(statusIcon.HasValue) PhotoIconArt.Draw(new Rect(row.x+114f,nameY+(nameHeight-GuestIconSize)/2f,GuestIconSize,GuestIconSize),statusIcon.Value,AlbumStyle.Theme.MutedInk);
                    SharedPhotoUI.Label(new Rect(row.x+114f+iconInset,nameY,nameWidth,nameHeight),p.LabelShort,!available);
                    if(roleHeight>0) SharedPhotoUI.Label(new Rect(row.x+114f,nameY+nameHeight,cellWidth-126f,roleHeight),role,true);
                    if((available||on) && !RequiredGuardian(p) && Widgets.ButtonInvisible(row)) ToggleGuest(p);
                    if(p.DevelopmentalStage.Baby()) TooltipHandler.TipRegion(row,"MF_GroupBabySelectHelp".Translate());
                }
                y+=rowsHeight+8;sectionsY=y;
                ExpandedSettings=PreparationSections.Fold(width,ref y,layoutSummary,ExpandedSettings);
                if(ExpandedSettings)
                {
                    PositionControl(width,ref y,control);
                    if(studio)SharedPhotoUI.StudioFrameChoice(new Rect(0,y,width,frameHeight),savedFrame,ref UseSavedFrame);
                    else
                    {
                        for(int i=0;i<4;i++)
                        {
                            var rect=new Rect(i*width/4,y,width/4-6,directionHeight);
                            if(SharedPhotoUI.Button(rect,DirectionKeys[i].Translate()))SetDirection(new Rot4(i));
                            if(direction.AsInt==i)Widgets.DrawBoxSolid(new Rect(rect.x+2,rect.yMax-4,rect.width-4,3),AlbumStyle.Theme.Accent);
                        }
                        y+=directionHeight+16;
                        SharedPhotoUI.FrameChoice(new Rect(0,y,width,frameHeight),savedFrame,ref UseSavedFrame,ref peopleFocus);
                    }
                    y+=frameHeight;
                }
                ExpandedExpressions=PreparationSections.Fold(width,ref y,expressionSummary,ExpandedExpressions);
                if(ExpandedExpressions)PreparationSections.Expressions(width,ref y,Chosen,allRoster,reactions,faces,ref reactionMode,ref rememberReaction,facePreview,
                    ()=>{if(reactionMode==PhotoReactions.ModeOff)reactionMode=PhotoReactions.ModePhoto;});
                SessionControls(width,ref y,control);
                LastContentEnd=y;LastContentHeight=content;
                Widgets.EndScrollView();
                SharedPhotoUI.Label(new Rect(page.x,page.yMax-footerHeight,page.width,statusHeight),status,current.CanStart);
                if(SharedPhotoUI.Button(new Rect(page.x,page.yMax-footerControl,page.width,footerControl),startLabel,Plan.CanStart) && StartSelected()) Close();
                facePreview.DrawHover(r);
            }
        }
        internal void ToggleSections()
        {
            if(ExpandedSettings&&ExpandedExpressions) { ExpandedSettings=false;ExpandedExpressions=false;scroll.y=0f;return; }
            ExpandedSettings=true;ExpandedExpressions=true;scroll.y=sectionsY;
        }
        private void BeginPreview(float control)
        {
            CameraJumper.TryJump(new TargetInfo(spot.Position,spot.Map));setupRect=windowRect;previewing=true;previewDialog=this;
            float previewWidth=Mathf.Min(520f,UI.screenWidth-24f), infoHeight=AlbumStyle.TextHeight("MF_BirthdayPreviewInfo".Translate(),previewWidth-48f-control-12f);
            windowRect=new Rect(12f,12f,previewWidth,infoHeight+control+60f);
        }
        internal static List<IntVec3> PreviewCellsFor(Building_PhotoSpot spot) => previewDialog?.spot==spot && previewDialog.previewing && previewDialog.ValidSpot ? previewDialog.Plan.Cells : null;
        private void PositionControl(float width,ref float y,float height)
        {
            if(SharedPhotoUI.Button(new Rect(0,y,width,height),"MF_Positions".Translate(),Plan.Moving.Count>0 && Plan.Cells.Count==Plan.Moving.Count))
                Find.WindowStack.Add(new Dialog_PhotoPositions(setupMap,spot.Position,direction,Plan.Moving,Plan.Cells,faces,p=>{PhotoPositions.Copy(p,positions);Invalidate();}));
            y+=height+8;
        }
        private void SessionControls(float width,ref float y,float height)
        {
            if(SharedPhotoUI.Button(new Rect(0,y,width,height),"MF_Sessions".Translate()))
                PhotoSessionUI.Menu(()=>PhotoSession.Capture(spot,Chosen,Plan.Moving,Plan.Cells,direction,reactionMode,peopleFocus,UseSavedFrame,reactions,faces,babyGuardians),LoadSession);
            y+=height+8;
        }
        private void LoadSession(PhotoSession saved)
        {
            var people=PhotoSessionUI.Resolve(saved,setupMap);if(people==null)return;
            spot=saved.Spot(setupMap);direction=spot is Building_PhotoStudio?spot.Rotation:new Rot4(saved.rotation);
            selected.Clear();foreach(var p in people)selected.Add(p);
            babyGuardians.Clear();
            for(int i=0;i<System.Math.Min(saved.babyIds.Count,saved.guardianIds.Count);i++)
            {
                var baby=people.FirstOrDefault(p=>p.GetUniqueLoadID()==saved.babyIds[i]);var adult=people.FirstOrDefault(p=>p.GetUniqueLoadID()==saved.guardianIds[i]);
                if(baby!=null && adult!=null)babyGuardians[baby]=adult;
            }
            EnsureBabyGuardians();PhotoPositions.Copy(saved.Positions,positions);
            PhotoPositions.Copy(ReactionChoices.Read(saved.people,saved.reactions),reactions);PhotoPositions.Copy(saved.Faces,faces);
            reactionMode=saved.reactionMode;peopleFocus=saved.peopleFocus;UseSavedFrame=saved.useFrame;Invalidate();
        }
        private void ChooseSpot()
        {
            var options=setupMap.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSpot>().Where(s=>s.Spawned&&!s.Destroyed)
                .OrderBy(s=>s.thingIDNumber).Select(s=>new FloatMenuOption("MF_PreparationSelectedSpot".Translate(s.LabelShort,s.Position.x,s.Position.z),
                    ()=>{spot=s;direction=s.PreparationDirection;Invalidate();})).ToList();
            if(options.Count>0) Find.WindowStack.Add(new FloatMenu(options));
        }
        internal static void DrawPreview()
        {
            if(PhotoCapture.HideGuides || previewDialog==null || !previewDialog.previewing || !previewDialog.ValidSpot || previewDialog.setupMap!=Find.CurrentMap)return;
            GenDraw.DrawFieldEdges(previewDialog.Plan.Cells,Color.cyan);
        }
        internal static void StopPreviewDrawing() { previewDialog=null; }
    }
}
