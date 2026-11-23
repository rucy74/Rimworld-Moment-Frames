using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool photoStudioMode;
        private static Building_PhotoStudio studioBackdrop,studioArch;
        private static List<Pawn> studioFamily;
        private static Pawn studioChild,studioBaby;
        private static LordJob_MFGroupPhoto studioJob;
        private static int studioTicks;
        private static string studioPhoto;
        private static Dialog_GroupPhotoSetup studioDialog;
        private static IList studioFaceList;
        private static void StudioUsageScene()
        {
            // Disposable demonstration set, built with ordinary game things. No player save is edited.
            var room=CellRect.FromLimits(center.x-13,center.z-6,center.x-1,center.z+5);
            foreach(var c in room)
            {
                map.areaManager.NoRoof[c]=true;
                map.terrainGrid.SetTerrain(c,DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
                if(c.x==room.minX||c.x==room.maxX||c.z==room.minZ||c.z==room.maxZ)
                {
                    bool door=c.x==center.x-7&&c.z==room.minZ;
                    var wall=ThingMaker.MakeThing(door?ThingDefOf.Door:ThingDefOf.Wall,ThingDefOf.WoodLog);wall.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(wall,c,map);
                }
            }
            foreach(var c in CellRect.CenteredOn(studioArch.Position,9,7))map.terrainGrid.SetTerrain(c,DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor"));
            foreach(int x in new[]{-11,-3})
            {
                var chair=ThingMaker.MakeThing(ThingDef.Named("DiningChair"),ThingDefOf.WoodLog);chair.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(chair,center+new IntVec3(x,0,-4),map,Rot4.North);
            }
            foreach(var c in new[]{center+new IntVec3(3,0,5),center+new IntVec3(13,0,5)})
            {var tree=(Plant)ThingMaker.MakeThing(ThingDef.Named("Plant_TreeOak"));tree.Growth=1f;GenSpawn.Spawn(tree,c,map);}
            PreparationRefreshRegions();
        }
        private static void StudioStartFamily()
        {
            StudioCheck(studioDialog.StartSelected(),"preparation window starts its displayed selection");
            studioJob=GroupPhoto.ActiveJob(map,studioBackdrop.Position);
            StudioCheck(studioJob!=null,"family gathering starts through production entry point");studioTicks=0;phase=3;
        }
        private static void StudioCheck(bool value,string label)
        {if(!value)throw new InvalidOperationException("photo studio: "+label);checks.Add("PASS: "+label);}
        private static void StudioCaptureCopy(string name)
        {
            File.WriteAllText(Path.Combine(dir,name+".positions.txt"),string.Join("\n",studioJob.Members.Select(p=>p.LabelShort+" cell="+p.Position+" draw="+p.DrawPos+" target="+studioJob.CellFor(p))));
            var rec=PhotoStore.Instance.Get(studioJob.PhotoId);
            StudioCheck(rec?.HasFile==true,"automatic photograph saved: "+name);
            File.Copy(PhotoStore.Instance.FilePath(rec),Path.Combine(dir,name),true);
            StudioCheck(rec.facePawnIds.Count>0,"face choices propagate into photo metadata");
            var rect=PhotoCapture.CoveredRect(rec.center,rec.spanCells,map,rec.Aspect);
            var art=(Building_PhotoStudio)studioJob.Members[0].Map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoStudio>().First(s=>s.Position==studioJob.spot);
            StudioCheck(art.ArtworkBounds.Cells.All(rect.Contains),"automatic frame contains the backdrop bounds");
        }
        private static bool StudioAdvance()
        {
            Find.TickManager.CurTimeSpeed=TimeSpeed.Normal;studioTicks++;
            if(studioTicks>7000)throw new TimeoutException("studio flow: "+studioJob.Result+" in place "+studioJob.InPlace+"/"+studioJob.Roster);
            if(studioJob.Finished && studioJob.Takes==0)throw new InvalidOperationException("gathering failed: "+studioJob.Result);
            return studioJob.Takes>0;
        }
        private static void PhotoStudioStep()
        {
            File.WriteAllText(Path.Combine(dir,"phase.txt"),phase.ToString());
            if (phase >= 1300 && phase < 1400) { GuideJourneyStep(); return; }
            if (phase >= 1200 && phase < 1300) { OnboardingStep(); return; }
            if (phase >= 1100 && phase < 1200) { StudioOptionsStep(); return; }
            if (phase >= 700 && phase < 750) { FacePreviewStep(); return; }
            if (phase >= 750 && phase < 800) { RaceFacesStep(); return; }
            if (phase >= 900 && phase < 950) { StudioConstructionStep(); return; }
            if (phase >= 950 && phase < 1000) { StudioMatrixStep(); return; }
            if (phase >= 800 && phase < 900) { StudioFurnishingStep(); return; }
            switch(phase)
            {
                case 0:
                    deadline=Time.realtimeSinceStartup+300f;map=Find.CurrentMap;center=map.Center;
                    Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;Find.Storyteller.storytellerComps.Clear();Find.Storyteller.incidentQueue.Clear();ClearHostiles(map);CloseLog();
                    Prefs.DevMode=false;Prefs.AdaptiveTrainingEnabled=false;Find.PlaySettings.showLearningHelper=false;
                    Prefs.EdgeScreenScroll=false;Prefs.ZoomToMouse=false;
                    var settings=MomentFramesMod.Settings;settings.autoCaptureEnabled=settings.buildingPhotos=settings.yearLetter=false;settings.settlementPeriod=0;settings.albumTextSize=115;
                    settings.brightPhotos=true;
                    foreach(var animal in map.mapPawns.AllPawnsSpawned.Where(p=>p.RaceProps.Animal).ToList())animal.Destroy();
                    foreach(var c in CellRect.CenteredOn(center,24))map.roofGrid.SetRoof(c,null);
                    foreach(var c in CellRect.CenteredOn(center,24))
                    {
                        foreach(var t in c.GetThingList(map).ToList())if(!(t is Pawn)&&t.def.destroyable)t.Destroy();
                        map.fogGrid.Unfog(c);map.roofGrid.SetRoof(c,null);map.terrainGrid.SetTerrain(c,DefDatabase<TerrainDef>.GetNamed("Soil"));
                    }
                    studioFamily=map.mapPawns.FreeColonistsSpawned.Take(2).ToList();
                    studioChild=PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist,Faction.OfPlayer,forceGenerateNewPawn:true,canGeneratePawnRelations:false,fixedBiologicalAge:6,fixedChronologicalAge:6,developmentalStages:DevelopmentalStage.Child));
                    GenSpawn.Spawn(studioChild,center+new IntVec3(0,0,-8),map);studioFamily.Add(studioChild);
                    studioBaby=PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist,Faction.OfPlayer,forceGenerateNewPawn:true,canGeneratePawnRelations:false,fixedBiologicalAge:1,fixedChronologicalAge:1,allowDowned:true,developmentalStages:DevelopmentalStage.Baby));
                    GenSpawn.Spawn(studioBaby,center+new IntVec3(1,0,-8),map);
                    foreach(var pawn in map.mapPawns.FreeColonistsSpawned.ToList())
                    {FixReady(pawn);pawn.jobs?.StopAll();pawn.Position=center+new IntVec3(-2+map.mapPawns.FreeColonistsSpawned.ToList().IndexOf(pawn),0,-9);pawn.Notify_Teleported();pawn.Rotation=Rot4.South;}
                    foreach(var parent in studioFamily.Take(2))studioChild.relations.AddDirectRelation(PawnRelationDefOf.Parent,parent);
                    studioBackdrop=(Building_PhotoStudio)ThingMaker.MakeThing(ThingDef.Named("MF_StudioBackdrop"));studioBackdrop.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(studioBackdrop,center+new IntVec3(-7,0,0),map);
                    studioArch=(Building_PhotoStudio)ThingMaker.MakeThing(ThingDef.Named("MF_StudioArch"));studioArch.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(studioArch,center+new IntVec3(8,0,0),map);
                    StudioUsageScene();
                    if(GenCommandLine.TryGetCommandLineArg("momentFramesGuideJourney",out _)){phase=1300;return;}
                    if(GenCommandLine.TryGetCommandLineArg("momentFramesOnboarding",out _)){phase=1200;return;}
                    if(GenCommandLine.TryGetCommandLineArg("momentFramesStudioOptions",out _)){phase=1100;return;}
                    if(GenCommandLine.TryGetCommandLineArg("momentFramesRaceFaces",out _)){phase=750;return;}
                    if(GenCommandLine.TryGetCommandLineArg("momentFramesStudioConstruction",out _)){phase=900;return;}
                    if(GenCommandLine.TryGetCommandLineArg("momentFramesFurnishingOnly",out _)){phase=800;return;}
                    PreparationRefreshRegions();Find.CameraDriver.SetRootPosAndSize(studioBackdrop.DrawPos,8f);
                    for(int i=0;i<3;i++)Find.TickManager.DoSingleTick();
                    StudioCheck(studioBackdrop.def.costList.Count==2 && studioBackdrop.def.GetStatValueAbstract(StatDefOf.WorkToBuild)>0,"backdrop requires materials and construction");
                    StudioCheck(studioBackdrop.ArtworkBounds.Width>=studioBackdrop.def.graphicData.drawSize.x && studioBackdrop.ArtworkBounds.Height>=studioBackdrop.def.graphicData.drawSize.y,"art bounds include the full sprite width and height");
                    StudioCheck(GroupFormation.Cells(studioBackdrop.Position,16,map).Count==15,"capacity cannot silently exceed fifteen standing places");
                    for(int rot=0;rot<4;rot++)
                    {var cs=Building_PhotoStudio.Formation(map,studioBackdrop.Position,new Rot4(rot),8);StudioCheck(cs.Count==8&&cs.Distinct().Count()==8,"eight distinct positions in rotation "+rot);}
                    studioDialog=new Dialog_GroupPhotoSetup(studioBackdrop);studioDialog.SelectFamily(studioChild);
                    string diagnostic="chosen="+string.Join(",",studioDialog.Chosen.Select(p=>p.LabelShort))+"\n";
                    foreach(var p in map.mapPawns.FreeColonistsSpawned)diagnostic+=p.LabelShort+" family="+Building_PhotoStudio.ImmediateFamily(studioChild,p)+" why="+GroupPhoto.WhyNot(p)+" relations="+string.Join(",",p.relations.DirectRelations.Select(r=>r.def.defName+":"+r.otherPawn.LabelShort))+"\n";
                    File.WriteAllText(Path.Combine(dir,"family-diagnostic.txt"),diagnostic);
                    StudioCheck(new HashSet<Pawn>(studioDialog.Chosen).SetEquals(studioFamily),"family selection includes both parents and child only");
                    var plan=PhotoGatheringPlan.Create(map,studioBackdrop.Position,studioFamily,Rot4.North);
                    StudioCheck(plan.CanStart && plan.Cells[plan.Moving.IndexOf(studioChild)]==studioBackdrop.Position+new IntVec3(0,0,-1),"child stands at front center");
                    var wall=ThingMaker.MakeThing(ThingDefOf.Wall,ThingDefOf.WoodLog);GenSpawn.Spawn(wall,studioBackdrop.Position+new IntVec3(0,0,-1),map);PreparationRefreshRegions();
                    StudioCheck(!PhotoGatheringPlan.Create(map,studioBackdrop.Position,studioFamily,Rot4.North).CanStart,"blocked posing area prevents gathering");wall.Destroy();PreparationRefreshRegions();
                    Find.CameraDriver.SetRootPosAndSize((studioBackdrop.DrawPos+studioArch.DrawPos)/2f,12f);
                    Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Architect);
                    var architect=(MainTabWindow_Architect)MainButtonDefOf.Architect.TabWindow;
                    architect.selectedDesPanel=Traverse.Create(architect).Field("desPanelsCached").GetValue<List<ArchitectCategoryTab>>().Single(p=>p.def.defName=="Misc");
                    CloseLog();Wait(.6f);phase=109;return;
                case 109:
                    CloseLog();Messages.Clear();Wait(.3f);phase=110;return;
                case 110:
                    Shot("use-01-build-menu.png");Wait(.3f);phase=1101;return;
                case 1101:
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    Find.CameraDriver.SetRootPosAndSize(studioBackdrop.DrawPos,10f);
                    Find.Selector.ClearSelection();Find.Selector.Select(studioBackdrop);Wait(.5f);phase=111;return;
                case 111:
                    CloseLog();Messages.Clear();Shot("use-02-building-selected.png");Wait(.3f);phase=1111;return;
                case 1111:
                    studioBackdrop.GetGizmos().OfType<Command_Action>().Single(g=>g.defaultLabel=="MF_GroupSetupTitle".Translate()).action();
                    studioDialog=Find.WindowStack.WindowOfType<Dialog_GroupPhotoSetup>();studioDialog.SelectFamily(studioChild);
                    var faceDraft=Traverse.Create(studioDialog).Field("faces").GetValue<Dictionary<string,int>>();
                    foreach(var p in studioFamily)faceDraft[p.GetUniqueLoadID()]=p==studioChild?2:1;
                    if(GenCommandLine.TryGetCommandLineArg("momentFramesFacePreview",out _)){phase=700;Wait(.5f);return;}
                    Wait(.5f);phase=1;return;
                case 1:
                    CloseLog();Wait(.3f);phase=101;return;
                case 101:
                    Shot("studio-preparation.png");Wait(.4f);phase=2;return;
                case 2:
                    studioDialog.Close();
                    if(PhotoFaces.Available)
                    {
                        var diagnosticFace=new List<string>{"gender="+studioFamily[0].gender};
                        foreach(var c in studioFamily[0].AllComps.Where(c=>c.GetType().Namespace=="FacialAnimation"))
                        {
                            object ft=AccessTools.Property(c.GetType(),"FaceType")?.GetValue(c,null);
                            string tex=ft==null?"":(string)AccessTools.Field(ft.GetType(),"texPath").GetValue(ft);
                            diagnosticFace.Add(c.GetType().FullName+" "+ft+" tex="+tex+" smile="+(ContentFinder<Texture2D>.Get(tex+"/"+studioFamily[0].gender+"/smile_south",false)!=null));
                        }
                        diagnosticFace.Add("mouth="+AccessTools.Method(typeof(PhotoFaces),"Shape").Invoke(null,new object[]{studioFamily[0],"Mouth","smile",true}));
                        diagnosticFace.Add("lid="+AccessTools.Method(typeof(PhotoFaces),"Shape").Invoke(null,new object[]{studioFamily[0],"Lid","normal",true}));
                        File.WriteAllText(Path.Combine(dir,"face-diagnostic.txt"),string.Join("\n",diagnosticFace));
                        StudioCheck(PhotoFaces.Supports(studioFamily[0],1),"human smile supported by installed face textures");
                        var fake=new PhotoRecord{peopleIds=studioFamily.Select(p=>p.GetUniqueLoadID()).ToList(),facePawnIds=new List<string>{studioFamily[0].GetUniqueLoadID()},faceChoices=new List<int>{1}};
                        using(var owned=PhotoFaces.Begin(map,fake))
                        {
                            StudioCheck(owned.Owned.Count==1,"temporary pose installed immediately, including while paused");
                            using(var second=PhotoFaces.Begin(map,fake))StudioCheck(second.Owned.Count==0,"an existing temporary animation is not replaced");
                            var entry=owned.Owned[0];object foreign=AccessTools.Method(typeof(object),"MemberwiseClone").Invoke(entry.Item4,null);entry.Item3.Add(foreign);owned.Dispose();
                            StudioCheck(entry.Item3.Count==1&&ReferenceEquals(entry.Item3[0],foreign),"cleanup removes only its own animation entry");entry.Item3.Clear();
                            studioFaceList=entry.Item3;
                        }
                        StudioCheck(PhotoCapture.Capture(map,studioBackdrop.DrawPos,9,r=>{},reactionRecord:fake),"capture starts with facial pose for reset check");
                        StudioCheck(studioFaceList.Count==1,"capture owns a live temporary pose");
                        PhotoCapture.Reset();StudioCheck(studioFaceList.Count==0,"capture reset releases its temporary pose immediately");
                        StudioCheck(PhotoCapture.Capture(map,studioBackdrop.DrawPos,9,r=>{},reactionRecord:fake),"capture starts for cancellation check");
                        fake.captureCancelled=true;phase=102;return;
                    }
                    else StudioCheck(!PhotoFaces.Supports(studioFamily[0],1),"no FA dependency: unsupported expression stays unavailable");
                    StudioStartFamily();return;
                case 102:
                    if(PhotoCapture.Busy)return;
                    StudioCheck(studioFaceList.Count==0,"cancelled shutter releases its temporary facial pose");
                    StudioStartFamily();return;
                case 3:
                    if(!StudioAdvance())return;
                    StudioCaptureCopy("studio-family.png");studioPhoto=studioJob.PhotoId;
                    StudioCheck(studioJob.Holding,"ordinary family group holds for a retake");
                    if(PhotoFaces.Available)StudioCheck(studioFaceList.Count==0,"successful capture releases its temporary pose");
                    Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;Find.Selector.ClearSelection();Find.Selector.Select(studioBackdrop);
                    Find.CameraDriver.SetRootPosAndSize(studioBackdrop.DrawPos,10f);Wait(.5f);phase=310;return;
                case 310:
                    Messages.Clear();Shot("use-04-after-capture.png");Wait(.3f);phase=3101;return;
                case 3101:
                    MainTabWindow_Album.Open(PhotoStore.Instance.Get(studioPhoto));Wait(1f);phase=311;return;
                case 311:
                    Shot("use-05-album.png");Wait(.3f);phase=3111;return;
                case 3111:
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    Find.Selector.ClearSelection();Find.CameraDriver.SetRootPosAndSize(studioBackdrop.DrawPos,8f);
                    studioJob.SetPreparationOptions(studioFamily,-1,false,faces:studioFamily.ToDictionary(p=>p.GetUniqueLoadID(),p=>0));
                    studioJob.ShootNow();phase=4;return;
                case 4:
                    if(studioJob.Takes<2){if(!studioJob.Shooting)Find.TickManager.DoSingleTick();return;}
                    StudioCheck(studioJob.PhotoId!=studioPhoto,"retake creates a separate photograph");StudioCaptureCopy("studio-neutral.png");studioJob.Cancel();
                    for(int i=0;i<15;i++)Find.TickManager.DoSingleTick();
                    var group=map.mapPawns.FreeColonistsSpawned.Where(p=>!p.DevelopmentalStage.Baby()).ToList();
                    while(group.Count<8){var p=PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist,Faction.OfPlayer,forceGenerateNewPawn:true,fixedBiologicalAge:30));GenSpawn.Spawn(p,center+new IntVec3(group.Count,0,-10),map);FixReady(p);group.Add(p);}
                    Find.CameraDriver.SetRootPosAndSize(studioArch.DrawPos,9f);
                    studioJob=(LordJob_MFGroupPhoto)GroupPhoto.StartPrepared(map,studioArch.Position,group,Rot4.North,faces:group.ToDictionary(p=>p.GetUniqueLoadID(),p=>0))?.LordJob;
                    StudioCheck(studioJob!=null,"eight-person outdoor group starts");studioTicks=0;phase=5;return;
                case 5:
                    if(!StudioAdvance())return;StudioCaptureCopy("studio-outdoor.png");studioJob.Cancel();for(int i=0;i<15;i++)Find.TickManager.DoSingleTick();
                    foreach(var p in studioFamily)FixReady(p);
                    StudioCheck(Birthday.StartPrepared(studioChild,6,studioBackdrop.Position,studioFamily,Rot4.North,null,faces:studioFamily.ToDictionary(p=>p.GetUniqueLoadID(),p=>1)),"birthday starts at a studio through production entry point");
                    studioJob=Birthday.Active(studioChild);
                    StudioCheck(studioJob.CellFor(studioChild)==studioBackdrop.Position+new IntVec3(0,0,-1),"birthday subject takes the center position");
                    Find.CameraDriver.SetRootPosAndSize(studioBackdrop.DrawPos,8f);studioTicks=0;phase=50;return;
                case 50:
                    if(!StudioAdvance())return;StudioCaptureCopy("studio-birthday.png");
                    StudioCheck(PhotoStore.Instance.Get(studioJob.PhotoId).kind==Kinds.Birthday,"studio birthday keeps birthday album classification");
                    studioJob.Cancel();for(int i=0;i<15;i++)Find.TickManager.DoSingleTick();
                    var guardian=studioFamily[0];FixReady(guardian);var family=new List<Pawn>{guardian,studioFamily[1],studioBaby};
                    var babyPlan=PhotoGatheringPlan.Create(map,studioBackdrop.Position,family,Rot4.North,null,new Dictionary<Pawn,Pawn>{{studioBaby,guardian}});
                    StudioCheck(babyPlan.CanStart&&babyPlan.Moving.Count==2,"baby uses guardian's place instead of a walking slot");
                    Find.CameraDriver.SetRootPosAndSize(studioBackdrop.DrawPos,8f);
                    studioJob=(LordJob_MFGroupPhoto)GroupPhoto.StartPrepared(map,studioBackdrop.Position,family,Rot4.North,babyGuardians:new Dictionary<Pawn,Pawn>{{studioBaby,guardian}},faces:family.ToDictionary(p=>p.GetUniqueLoadID(),p=>0))?.LordJob;
                    StudioCheck(studioJob!=null,"baby family gathering starts");studioTicks=0;phase=6;return;
                case 6:
                    if(!StudioAdvance())return;StudioCaptureCopy("studio-baby.png");
                    Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
                    StudioCheck(studioJob.Finished,"baby family dismisses after the photo so guardian can return baby");
                    ReactionPresetsGameComponent.Instance.Save("Studio fixture",new Dictionary<string,int>(),studioFamily.Select(p=>p.GetUniqueLoadID()),studioFamily.ToDictionary(p=>p.GetUniqueLoadID(),p=>2));
                    studioBackdrop.SetFrame(studioBackdrop.DrawPos,9,1.5f);
                    GameDataSaveLoader.SaveGame("mf-studio-fixture");Wait(.5f);phase=7;return;
                case 7:
                    GameDataSaveLoader.LoadGame("mf-studio-fixture");Wait(.5f);phase=8;return;
                case 8:
                    map=Find.CurrentMap;studioBackdrop=map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoStudio>().Single(s=>s.def.defName=="MF_StudioBackdrop");
                    StudioCheck(studioBackdrop.Framed,"custom frame survives actual game save/load");
                    StudioCheck(ReactionPresetsGameComponent.Instance.presets.Single(p=>p.name=="Studio fixture").faces.All(v=>v==2),"saved per-person face preset survives load");
                    var loaded=PhotoStore.Instance.Get(studioPhoto);StudioCheck(loaded!=null && loaded.faceChoices.Count==3,"photo facial choices survive load");
                    phase=800;return;
            }
        }
    }
}
