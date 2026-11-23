using System;
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
        // Disposable native test only. This injects a visible button's return value,
        // not an OS click: it verifies the real UI action branch, not pointer hit testing.
        private const string JourneyPatch = "MomentFrames.Probe.GuideJourney";
        private static string journeyButton;
        private static IntVec3 journeySpot;
        private static bool journeyWorkingShot;
        private static int journeyPage;
        private static void JourneyButtonResult(string text, bool active, ref bool __result)
        {
            if(journeyButton==null || !active || text!=journeyButton || Event.current.type!=EventType.Repaint)return;
            File.AppendAllText(Path.Combine(dir,"ui-actions.txt"),"button: "+text+"\n");
            journeyButton=null;__result=true;
        }
        private static void JourneyPress(string text,int next)
        {journeyButton=text;phase=next;Wait(.4f);}
        private static void JourneyGizmo(string key)
        {
            var cmd=studioBackdrop.GetGizmos().OfType<Command_Action>().Single(g=>g.defaultLabel==key.Translate().ToString());
            StudioCheck(!cmd.Disabled,"visible command enabled: "+key);cmd.action();
            File.AppendAllText(Path.Combine(dir,"ui-actions.txt"),"gizmo action: "+key.Translate()+"\n");
        }
        private static void GuideJourneyStep()
        {
            CloseLog();
            foreach(var window in Find.WindowStack.Windows.Where(w=>w.GetType().Name=="EditWindow_Log").ToList())window.Close();
            if(journeyButton!=null)return;
            switch(phase)
            {
                case 1300:
                    deadline=Time.realtimeSinceStartup+300;
                    new Harmony(JourneyPatch).Patch(AccessTools.Method(typeof(SharedPhotoUI),"Button"),postfix:new HarmonyMethod(typeof(Probe),nameof(JourneyButtonResult)));
                    MomentFramesMod.Settings.fxPopup=false;
                    journeySpot=studioBackdrop.Position;
                    foreach(var b in map.listerBuildings.allBuildingsColonist.Where(b=>b is Building_PhotoSpot).ToList())b.Destroy();
                    foreach(var c in CellRect.CenteredOn(journeySpot,9))
                    {
                        foreach(var b in c.GetThingList(map).OfType<Building>().ToList())b.Destroy();
                        map.areaManager.Home[c]=true;
                    }
                    PreparationRefreshRegions();
                    StudioCheck(PhotoGuide.Places("family").Count==0,"first-time map has no completed photo place");
                    StudioCheck(!PhotoStore.Instance.Records.Any(r=>r.HasFile),"first-time album has no photographs");
                    PhotoGuide.Album();Wait(.5f);phase=1301;return;
                case 1301:
                    Shot("01-empty-album.png");Wait(.2f);phase=1302;return;
                case 1302:
                    Find.MainTabsRoot.SetCurrentTab(null);PhotoGuide.Open();onboardingGuide=Find.WindowStack.WindowOfType<Dialog_PhotoGuide>();
                    Wait(.4f);phase=1303;return;
                case 1303:
                    Shot("02-guide-home.png");JourneyPress("MF_Learn_FirstAction".Translate(),1304);return;
                case 1304:
                    StudioCheck(onboardingGuide.Topic=="family"&&onboardingGuide.Step==0,"first-photo button opens the first setup step");
                    onboardingGuide.windowRect=new Rect(20,20,720,620);MomentFramesMod.Settings.albumTextSize=150;
                    JourneyPress("MF_Learn_ChoosePlace".Translate(),1305);return;
                case 1305:
                    StudioCheck(Find.WindowStack.Windows.Contains(onboardingGuide),"missing place keeps instructions open");
                    StudioCheck(onboardingGuide.ActionMessageRect.height>0 && onboardingGuide.ActionMessageRect.yMin>=0 && onboardingGuide.ActionMessageRect.yMax<=onboardingGuide.ContentRect.yMin,"missing-place explanation is visible above the scroll area at 150 percent text");
                    StudioCheck(onboardingGuide.ContentRect.height>SharedPhotoUI.ControlHeight+20,"small notice still leaves scrollable instructions and navigation");
                    CheckGuide("small missing place");
                    Shot("03-no-place-small.png");
                    File.WriteAllText(Path.Combine(dir,"small-guide-layout.txt"),"window="+onboardingGuide.windowRect+"\ncontent="+onboardingGuide.ContentRect+"\nissues="+string.Join(";",onboardingGuide.LayoutIssues));
                    Wait(.2f);phase=1306;return;
                case 1306:
                    MomentFramesMod.Settings.albumTextSize=115;onboardingGuide.Close();
                    constructionWorkers=map.mapPawns.FreeColonistsSpawned.Where(p=>p.DevelopmentalStage.Adult()&&!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)&&!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)).Take(2).ToList();
                    StudioCheck(constructionWorkers.Count>0,"fixture provides an able worker");
                    foreach(var p in constructionWorkers)
                    {
                        p.workSettings.EnableAndInitialize();
                        foreach(var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)if(!p.WorkTypeIsDisabled(work))p.workSettings.SetPriority(work,0);
                        p.workSettings.SetPriority(WorkTypeDefOf.Construction,1);p.workSettings.SetPriority(WorkTypeDefOf.Hauling,2);p.skills.GetSkill(SkillDefOf.Construction).Level=20;
                        for(int hour=0;hour<24;hour++)p.timetable.SetAssignment(hour,TimeAssignmentDefOf.Work);
                    }
                    ConstructionMaterials(ThingDefOf.WoodLog,200,journeySpot+IntVec3.South*8);
                    ConstructionMaterials(ThingDefOf.Cloth,150,journeySpot+IntVec3.South*9);
                    ConstructionMaterials(ThingDefOf.Steel,250,journeySpot+IntVec3.South*10);
                    ConstructionMaterials(ThingDefOf.ComponentIndustrial,10,journeySpot+IntVec3.South*11);
                    var def=ThingDef.Named("MF_StudioBackdrop");
                    StudioCheck(GenConstruct.CanPlaceBlueprintAt(def,journeySpot,Rot4.North,map).Accepted,"studio fits on the selected ground");
                    GenConstruct.PlaceBlueprintForBuild(def,journeySpot,map,Rot4.North,Faction.OfPlayer,null);
                    constructionStart=Find.TickManager.TicksGame;constructionGenerator=null;
                    Find.CameraDriver.SetRootPosAndSize(journeySpot.ToVector3Shifted(),13);Wait(.3f);phase=1307;return;
                case 1307:
                    Shot("04-studio-blueprint.png");Wait(.2f);phase=1308;return;
                case 1308:
                    ConstructionAdvance();
                    if(!journeyWorkingShot&&constructionWorkers.Any(p=>p.CurJobDef==JobDefOf.FinishFrame))
                    {journeyWorkingShot=true;Shot("05-construction.png");}
                    studioBackdrop=map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoStudio>().FirstOrDefault();
                    if(studioBackdrop==null)return;
                    Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
                    StudioCheck(constructionJobs.Contains("FinishFrame:MF_StudioBackdrop"),"colonist constructs backdrop through normal job");
                    Find.Selector.ClearSelection();Find.Selector.Select(studioBackdrop);JourneyGizmo("MF_StudioDecorate");Wait(.5f);phase=1309;return;
                case 1309:
                    StudioCheck(Find.WindowStack.WindowOfType<Dialog_StudioFurnishing>()!=null,"decorate command opens actual furnishing window");
                    Shot("06-furnishing-window.png");JourneyPress("MF_StudioPlaceSet".Translate(),1310);return;
                case 1310:
                    constructionPlans=studioBackdrop.Furniture(0);
                    StudioCheck(constructionPlans.All(p=>p.cell.GetThingList(map).OfType<Blueprint_Build>().Any(b=>b.def.entityDefToBuild==p.def)),"furnishing button places the shown family-set blueprints");
                    Find.WindowStack.WindowOfType<Dialog_StudioFurnishing>().Close();Wait(.3f);phase=1311;return;
                case 1311:
                    ConstructionAdvance();
                    if(!constructionPlans.All(p=>p.cell.GetThingList(map).OfType<Building>().Any(b=>b.def==p.def)))return;
                    Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
                    StudioCheck(constructionJobs.Contains("FinishFrame:MF_StudioSofa"),"colonists construct the sofa normally");
                    Shot("07-furnished.png");JourneyGizmo("MF_GroupSetupTitle");Wait(.4f);phase=1312;return;
                case 1312:
                    studioDialog=Find.WindowStack.WindowOfType<Dialog_GroupPhotoSetup>();StudioCheck(studioDialog!=null,"documented group command opens preparation");
                    studioDialog.SelectFamily(studioChild); // Family menu selection is programmatic, not a pointer test.
                    StudioCheck(studioDialog.CanStart,"family can gather using default optional settings");
                    PhotoGuide.Open("family",studioDialog);onboardingGuide=Find.WindowStack.WindowOfType<Dialog_PhotoGuide>();onboardingGuide.Select("family",1);
                    Wait(.4f);phase=1313;return;
                case 1313:
                    Shot("08-context-help.png");JourneyPress("MF_Learn_Return".Translate(),1314);return;
                case 1314:
                    StudioCheck(Find.WindowStack.Windows.Contains(studioDialog)&&Find.WindowStack.WindowOfType<Dialog_PhotoGuide>()==null,"return button restores the selected family draft");
                    Shot("09-family-ready.png");JourneyPress("MF_BirthdayStartGather".Translate(studioDialog.Chosen.Count),1315);return;
                case 1315:
                    StudioCheck(!Find.WindowStack.Windows.Contains(studioDialog),"gather button closes the actual preparation window");
                    studioJob=GroupPhoto.ActiveJob(map,studioBackdrop.Position);StudioCheck(studioJob!=null,"gather button starts a real group");studioTicks=0;
                    Find.Selector.ClearSelection();Find.Selector.Select(studioBackdrop);Wait(.3f);phase=1316;return;
                case 1316:
                    Shot("10-waiting.png");Wait(.2f);phase=1317;return;
                case 1317:
                    if(!StudioAdvance())return;Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
                    var rec=PhotoStore.Instance.Get(studioJob.PhotoId);StudioCheck(rec?.HasFile==true,"all participants gather and automatically save a real photograph");
                    File.Copy(PhotoStore.Instance.FilePath(rec),Path.Combine(dir,"11-saved-photo.png"),true);
                    StudioCheck(!studioJob.Finished,"participants remain available after the automatic shot");
                    Wait(1);phase=1318;return;
                case 1318:
                    Shot("12-finish-controls.png");Wait(.3f);phase=1322;return;
                case 1322:
                    PhotoGuide.Album();Wait(1.2f);phase=1319;return;
                case 1319:
                    StudioCheck(Find.MainTabsRoot.OpenTab?.defName=="MF_Album","documented album action opens actual album");
                    Shot("13-album-result.png");Wait(.2f);phase=1320;return;
                case 1320:
                    Find.MainTabsRoot.SetCurrentTab(null);Find.Selector.ClearSelection();Find.Selector.Select(studioBackdrop);JourneyGizmo("MF_GroupDismiss");
                    StudioCheck(studioJob.Finished&&GroupPhoto.ActiveJob(map,studioBackdrop.Position)==null,"documented dismiss command releases the gathering");
                    PhotoGuide.Open("family");onboardingGuide=Find.WindowStack.WindowOfType<Dialog_PhotoGuide>();
                    File.WriteAllText(Path.Combine(dir,"reopened-guide.txt"),"topic="+onboardingGuide.Topic+" step="+onboardingGuide.Step+" (previous context step was 1)");
                    Wait(.4f);phase=1321;return;
                case 1321:
                    Shot("14-reopened-guide.png");onboardingGuide.Close();
                    onboardingGuide=new Dialog_PhotoGuide();Find.WindowStack.Add(onboardingGuide);journeyPage=0;phase=1325;Wait(.3f);return;
                case 1325:
                    if(journeyPage>=15){onboardingGuide.Close();phase=1327;return;}
                    onboardingGuide.Select(PhotoGuide.Topics[journeyPage/3],journeyPage%3);Wait(.2f);phase=1326;return;
                case 1326:
                    CheckGuide("guide page "+journeyPage);
                    if(journeyPage==0 || journeyPage==2)Shot("guide-family-"+journeyPage+".png");
                    journeyPage++;phase=1325;Wait(.2f);return;
                case 1327:
                    new Harmony(JourneyPatch).Unpatch(AccessTools.Method(typeof(SharedPhotoUI),"Button"),HarmonyPatchType.Postfix,JourneyPatch);
                    File.WriteAllText(Path.Combine(dir,"runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                    File.WriteAllText(Path.Combine(dir,"journey-result.json"),"{\"ok\":true,\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                    active=false;Application.Quit();return;
            }
        }
    }
}
