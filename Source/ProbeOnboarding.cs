using System;
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
        private static Dialog_PhotoGuide onboardingGuide;
        private static Dialog_BirthdaySetup onboardingBirthday;
        private static Building_PhotoProjector onboardingProjector;
        private static int onboardingCase, onboardingScale;
        private static float onboardingUIScale;
        private static List<string> onboardingChosen;
        private static bool onboardingDetectorChecked;
        private const string OnboardingPatchId="MomentFrames.Probe.Onboarding";
        private static void OnboardingGeometryControl()
        {
            if(onboardingDetectorChecked)return;
            using(AlbumStyle.Begin(true))
                StudioCheck(!Dialog_PhotoGuide.LabelFits(new Rect(0,0,20,8),"A deliberately oversized button label"),"label overflow detector rejects a known undersized button in native OnGUI");
            onboardingDetectorChecked=true;
        }
        private static void CheckGuide(string label)
        {
            StudioCheck(onboardingGuide.LoadedImages>0,label+": real packaged photo decoded by native guide");
            StudioCheck(onboardingGuide.LayoutIssues.Count==0,label+": native labels fit buttons; "+string.Join(";",onboardingGuide.LayoutIssues));
            var bounds=new Rect(0,0,onboardingGuide.windowRect.width,onboardingGuide.windowRect.height);
            StudioCheck(onboardingGuide.Buttons.Values.All(b=>b.xMin>=0&&b.yMin>=0&&b.xMax<=bounds.xMax&&b.yMax<=bounds.yMax),label+": controls stay inside window");
            StudioCheck(!onboardingGuide.forcePause,label+": guide does not force game pause");
        }
        private static void OnboardingStep()
        {
            switch(phase)
            {
                case 1200:
                    SteamHideTips();   // the guide pictures are cut from these screens, and a tooltip at the offscreen mouse landed on the button bar
                    deadline=Time.realtimeSinceStartup+180;
                    new Harmony(OnboardingPatchId).Patch(AccessTools.Method(typeof(Dialog_PhotoGuide),nameof(Dialog_PhotoGuide.DoWindowContents)),postfix:new HarmonyMethod(typeof(Probe),nameof(OnboardingGeometryControl)));
                    foreach(var w in Find.WindowStack.Windows.Where(w=>w.GetType().Name=="EditWindow_Log").ToList())w.Close();
                    onboardingScale=MomentFramesMod.Settings.albumTextSize;onboardingUIScale=Prefs.UIScale;
                    MomentFramesMod.Settings.albumTextSize=115;
                    MomentFramesMod.Settings.autoCaptureEnabled=false;
                    MomentFramesMod.Settings.fxPopup=false;
                    // Current-guide fixture: real studio seat and production furnishing dialog.
                    var guideSofa=ThingMaker.MakeThing(ThingDef.Named("MF_StudioSofa"));
                    guideSofa.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(guideSofa,studioBackdrop.Position+new IntVec3(0,0,-1),map,studioBackdrop.Rotation);
                    // Leave actual blueprint positions free in the demonstration room.
                    foreach(var item in studioBackdrop.Furniture(0).Where(p=>p.def.defName=="MF_StudioLamp"))
                        foreach(var t in item.cell.GetThingList(map).OfType<Building>().ToList())t.Destroy();
                    Find.Selector.ClearSelection();Find.Selector.Select(studioBackdrop);
                    Find.CameraDriver.SetRootPosAndSize(studioBackdrop.DrawPos,12);
                    Wait(.5f);phase=1204;return;
                case 1204:
                    Shot("place-controls.png");Wait(.3f);phase=1206;return;
                case 1206:
                    Find.WindowStack.Add(new Dialog_StudioFurnishing(studioBackdrop));Wait(.4f);phase=1207;return;
                case 1207:
                    Shot("furnishing-current.png");Wait(.3f);phase=1208;return;
                case 1208:
                    Find.WindowStack.WindowOfType<Dialog_StudioFurnishing>().Close();phase=1205;return;
                case 1205:
                    StudioCheck(PhotoGuide.EmptyKey=="MF_Learn_EmptyManual","manual-only first album explains manual capture");
                    PhotoGuide.Album();Wait(.5f);phase=1201;return;
                case 1201:
                    Shot("album-empty-manual.png");Wait(.2f);phase=1202;return;
                case 1202:
                    MomentFramesMod.Settings.autoCaptureEnabled=true;
                    StudioCheck(PhotoGuide.EmptyKey=="MF_Learn_EmptyAuto","automatic first album explains event capture");
                    Find.MainTabsRoot.SetCurrentTab(null);PhotoGuide.Open();onboardingGuide=Find.WindowStack.WindowOfType<Dialog_PhotoGuide>();
                    StudioCheck(onboardingGuide!=null,"public guide entry opens a native window");Wait(.4f);phase=1203;return;
                case 1203:
                    StudioCheck(onboardingDetectorChecked,"overflow detector positive control executed before layout checks");
                    CheckGuide("guide home");Shot("guide-home.png");onboardingCase=0;Wait(.2f);phase=1210;return;
                case 1210:
                    if(onboardingCase>=15){phase=1220;return;}
                    onboardingGuide.Select(PhotoGuide.Topics[onboardingCase/3],onboardingCase%3);Wait(.25f);phase=1211;return;
                case 1211:
                    string label=PhotoGuide.Topics[onboardingCase/3]+"-"+(onboardingCase%3);
                    CheckGuide(label);StudioCheck(onboardingGuide.LoadedImages==((label=="family-2"||label=="display-1")?2:1),label+": only current page photographs retained");
                    Shot("guide-"+label+".png");Wait(.2f);onboardingCase++;phase=1210;return;
                case 1220:
                    onboardingGuide.Select("family",1);onboardingGuide.SetEnlarged(true);Wait(.3f);phase=1221;return;
                case 1221:
                    StudioCheck(onboardingGuide.windowRect.width==UI.screenWidth-24,"image enlargement uses the full available screen width");
                    CheckGuide("image enlargement");Shot("guide-image-enlarged.png");Wait(.2f);phase=1222;return;
                case 1222:
                    onboardingGuide.Select("background",1);MomentFramesMod.Settings.albumTextSize=150;
                    onboardingGuide.windowRect=new Rect(20,20,700,620);Wait(.3f);phase=1223;return;
                case 1223:
                    CheckGuide("small window large text");Shot("guide-small-text150.png");Wait(.2f);phase=1224;return;
                case 1224:
                    onboardingGuide.Close();StudioCheck(onboardingGuide.LoadedImages==0,"closing guide releases all owned image textures");
                    MomentFramesMod.Settings.albumTextSize=115;
                    PreparationSections.GroupLayout=PreparationSections.GroupExpressions=false;
                    studioDialog=new Dialog_GroupPhotoSetup(studioBackdrop);studioDialog.SelectFamily(studioChild);Find.WindowStack.Add(studioDialog);
                    onboardingChosen=studioDialog.Chosen.Select(p=>p.GetUniqueLoadID()).ToList();
                    Wait(.4f);phase=1230;return;
                case 1230:
                    StudioCheck(studioDialog.LastContentEnd<=studioDialog.LastContentHeight+1,"collapsed group scroll includes every control");
                    StudioCheck(studioDialog.CanStart,"default collapsed preparation can start without optional settings");
                    StudioCheck(Find.WindowStack.GetsInput(null),"the map under a preparation window still takes clicks");
                    StudioCheck(Traverse.Create(studioDialog).Field("reactionMode").GetValue<int>()==PhotoReactions.ModePhoto
                        &&MomentFramesMod.Settings.captureReactionMode==PhotoReactions.ModeAfter,"preparation starts with emotes saved in the photo; automatic photos keep map only");
                    Shot("setup-default.png");Wait(.2f);phase=1236;return;
                case 1236:
                    studioDialog.ToggleSections();Wait(.4f);phase=1237;return;
                case 1237:
                    StudioCheck(studioDialog.ExpandedSettings&&studioDialog.ExpandedExpressions,"the settings shortcut opens both sections");
                    StudioCheck(studioDialog.LastContentEnd<=studioDialog.LastContentHeight+1,"opened sections fit the scroll");
                    Shot("setup-opened.png");Wait(.4f);phase=1238;return;
                case 1238:
                    Traverse.Create(studioDialog).Field("scroll").SetValue(Vector2.zero);Wait(.4f);phase=1239;return;
                case 1239:
                    Shot("setup-opened-top.png");Wait(.4f);phase=1272;return;
                case 1272:
                    studioDialog.ToggleSections();Wait(.2f);phase=1273;return;
                case 1273:
                    StudioCheck(!studioDialog.ExpandedSettings&&!studioDialog.ExpandedExpressions,"pressing the settings shortcut again folds both sections");
                    phase=1231;return;
                case 1231:
                    var faces=Traverse.Create(studioDialog).Field("faces").GetValue<Dictionary<string,int>>();faces[studioFamily[0].GetUniqueLoadID()]=1;
                    var icons=Traverse.Create(studioDialog).Field("reactions").GetValue<Dictionary<string,int>>();icons[studioFamily[1].GetUniqueLoadID()]=0;
                    studioDialog.ExpandedSettings=true;studioDialog.ExpandedExpressions=true;
                    Traverse.Create(studioDialog).Field("scroll").SetValue(new Vector2(0,100000));Wait(.4f);phase=1232;return;
                case 1232:
                    StudioCheck(studioDialog.LastContentEnd<=studioDialog.LastContentHeight+1,"expanded group scroll includes every control");
                    Shot("setup-expressions.png");Wait(.2f);phase=1233;return;
                case 1233:
                    studioDialog.ExpandedSettings=false;studioDialog.ExpandedExpressions=false;
                    StudioCheck(studioDialog.Chosen.Select(p=>p.GetUniqueLoadID()).SequenceEqual(onboardingChosen),"folding preserves participant selection");
                    StudioCheck(Traverse.Create(studioDialog).Field("faces").GetValue<Dictionary<string,int>>()[studioFamily[0].GetUniqueLoadID()]==1,"folding preserves custom face choice");
                    StudioCheck(Traverse.Create(studioDialog).Field("reactions").GetValue<Dictionary<string,int>>()[studioFamily[1].GetUniqueLoadID()]==0,"folding preserves custom emote choice");
                    PhotoGuide.Open("family",studioDialog);onboardingGuide=Find.WindowStack.WindowOfType<Dialog_PhotoGuide>();Wait(.3f);phase=1234;return;
                case 1234:
                    CheckGuide("context help");StudioCheck(onboardingGuide.Buttons.ContainsKey("return")&&!onboardingGuide.Buttons.ContainsKey("action"),"context help returns to draft instead of starting a second gathering");
                    Shot("guide-context.png");Wait(.2f);phase=1235;return;
                case 1235:
                    onboardingGuide.Close();StudioCheck(Find.WindowStack.Windows.Contains(studioDialog)&&studioDialog.CanStart,"closing help restores the intact preparation window");
                    studioDialog.Close();PreparationSections.BirthdayLayout=PreparationSections.BirthdayExpressions=false;
                    onboardingBirthday=new Dialog_BirthdaySetup(studioChild,6,null,studioBackdrop.Position);Find.WindowStack.Add(onboardingBirthday);
                    Wait(.4f);phase=1240;return;
                case 1240:
                    StudioCheck(onboardingBirthday.LastContentEnd<=onboardingBirthday.LastContentHeight+1,"collapsed birthday scroll includes every control");
                    Shot("birthday-default.png");Wait(.2f);phase=1241;return;
                case 1241:
                    onboardingBirthday.ExpandedSettings=true;onboardingBirthday.ExpandedExpressions=true;
                    MomentFramesMod.Settings.albumTextSize=150;onboardingBirthday.windowRect=new Rect(20,20,720,620);
                    Traverse.Create(onboardingBirthday).Field("scroll").SetValue(new Vector2(0,100000));Wait(.4f);phase=1242;return;
                case 1242:
                    StudioCheck(onboardingBirthday.LastContentEnd<=onboardingBirthday.LastContentHeight+1,"large text birthday scroll includes every control");
                    Shot("birthday-small-text150.png");Wait(.2f);phase=1243;return;
                case 1243:
                    onboardingBirthday.Close();MomentFramesMod.Settings.albumTextSize=115;
                    MomentFramesMod.Settings.autoCaptureEnabled=false;
                    PreparationSections.GroupLayout=PreparationSections.GroupExpressions=false;
                    PhotoGuide.ActivatePlace("family",studioBackdrop);studioDialog=Find.WindowStack.WindowOfType<Dialog_GroupPhotoSetup>();
                    StudioCheck(studioDialog!=null,"guide place action opens production preparation");studioDialog.SelectFamily(studioChild);
                    onboardingChosen=studioDialog.Chosen.Select(p=>p.GetUniqueLoadID()).ToList();
                    StudioCheck(studioFamily.All(p=>onboardingChosen.Contains(p.GetUniqueLoadID())),"family selection includes the fixture child and both parents");
                    File.WriteAllText(Path.Combine(dir,"selected-family.txt"),string.Join("\n",studioDialog.Chosen.Select(p=>p.GetUniqueLoadID()+" "+p.LabelShort)));
                    StudioCheck(studioDialog.StartSelected(),"collapsed preparation starts actual group gathering");studioDialog.Close();
                    studioJob=GroupPhoto.ActiveJob(map,studioBackdrop.Position);studioTicks=0;phase=1250;return;
                case 1250:
                    if(!StudioAdvance())return;Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
                    var record=PhotoStore.Instance.Get(studioJob.PhotoId);
                    StudioCheck(record!=null&&record.HasFile,"default preparation completes automatic group photograph");
                    StudioCheck(new HashSet<string>(studioJob.Members.Select(p=>p.GetUniqueLoadID())).SetEquals(onboardingChosen),"gathering membership exactly matches selected family IDs");
                    StudioCheck(new HashSet<string>(record.peopleIds).IsSupersetOf(onboardingChosen),"saved photograph contains all selected family members (visible bystanders may also be recorded)");
                    File.WriteAllText(Path.Combine(dir,"captured-family.txt"),string.Join("\n",record.peopleIds.Zip(record.people,(id,name)=>id+" "+name)));
                    File.Copy(PhotoStore.Instance.FilePath(record),Path.Combine(dir,"default-group-result.png"),true);
                    Find.Selector.ClearSelection();Find.Selector.Select(studioBackdrop);
                    Find.CameraDriver.SetRootPosAndSize(studioBackdrop.DrawPos,12);
                    Wait(1.4f);phase=1255;return;
                case 1255:
                    Shot("finish-controls.png");Wait(.3f);phase=1262;return;
                case 1262:
                    PhotoGuide.ActivatePlace("family",studioBackdrop);studioDialog=Find.WindowStack.WindowOfType<Dialog_GroupPhotoSetup>();
                    StudioCheck(new HashSet<string>(studioDialog.Chosen.Select(p=>p.GetUniqueLoadID())).SetEquals(onboardingChosen)&&studioJob.EditingPhotoSetup,"guide reopens the existing group for editing");studioDialog.Close();studioJob.Cancel();
                    PhotoGuide.Manual();StudioCheck(Find.DesignatorManager.SelectedDesignator is Designator_TakePhoto,"manual guide action selects actual drag capture tool");
                    Find.DesignatorManager.Deselect();
                    PhotoGuide.ActivatePlace("background",studioBackdrop);var background=Find.WindowStack.WindowOfType<Dialog_StudioBackground>();
                    StudioCheck(background!=null,"guide studio action opens production background editor");Wait(.4f);phase=1256;return;
                case 1256:
                    Shot("background-guide-entry.png");Wait(.2f);phase=1270;return;
                case 1270:
                    Find.WindowStack.WindowOfType<Dialog_StudioBackground>().Load(PhotoStore.Instance.FilePath(PhotoStore.Instance.Get(studioJob.PhotoId)));
                    Wait(.4f);phase=1271;return;
                case 1271:
                    Shot("background-preview.png");Wait(.2f);phase=1257;return;
                case 1257:
                    Find.WindowStack.WindowOfType<Dialog_StudioBackground>().Close();
                    PhotoGuide.Album();Wait(.4f);phase=1251;return;
                case 1251:
                    StudioCheck(Find.MainTabsRoot.OpenTab?.defName=="MF_Album","guide album action opens album tab");
                    Shot("album-guide-entry.png");Wait(.2f);phase=1252;return;
                case 1252:
                    Find.MainTabsRoot.SetCurrentTab(null);onboardingGuide=new Dialog_PhotoGuide("display");Find.WindowStack.Add(onboardingGuide);
                    onboardingGuide.RunAction();StudioCheck(Find.WindowStack.Windows.Contains(onboardingGuide),"missing display leaves readable instructions open");
                    Wait(.3f);phase=1253;return;
                case 1253:
                    Shot("guide-missing-display.png");Wait(.2f);phase=1254;return;
                case 1254:
                    onboardingGuide.Close();
                    onboardingProjector=(Building_PhotoProjector)ThingMaker.MakeThing(ThingDef.Named("MF_Projector"),ThingDefOf.WoodLog);
                    onboardingProjector.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(onboardingProjector,center+new IntVec3(8,0,-13),map,Rot4.North);
                    onboardingProjector.SetSelectedFilm(new[]{studioJob.PhotoId});onboardingProjector.SetCustomProjectionSize(12,8);
                    onboardingProjector.SetProjectionDepth(ProjectionGeometry.FrontDepth);
                    PhotoGuide.ActivatePlace("display",onboardingProjector);
                    StudioCheck(Find.Selector.SelectedObjects.Contains(onboardingProjector),"guide display action selects the actual projector");
                    StudioCheck(onboardingProjector.Current?.id==studioJob.PhotoId,"projector displays the freshly captured family photograph");
                    onboardingProjector.ImageLayout(out var off,out _,out _);Find.CameraDriver.SetRootPosAndSize(onboardingProjector.DrawPos+off,10);
                    Wait(1.2f);phase=1258;return;
                case 1258:
                    Shot("projector-real-photo.png");Wait(.2f);phase=1259;return;
                case 1259:
                    Find.WindowStack.Add(new Dialog_ProjectionSize(onboardingProjector));Wait(.4f);phase=1260;return;
                case 1260:
                    Shot("projector-size.png");Wait(.2f);phase=1261;return;
                case 1261:
                    Find.WindowStack.WindowOfType<Dialog_ProjectionSize>().Close();
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    var chronicled=map.mapPawns.FreeColonistsSpawned.First();
                    Find.Selector.ClearSelection();Find.Selector.Select(chronicled,false);
                    InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Chronicle));
                    Find.CameraDriver.SetRootPosAndSize(chronicled.DrawPos,14f);
                    Wait(.6f);phase=1274;return;
                case 1274:
                    StudioCheck(InspectTabManager.GetSharedInstance(typeof(ITab_Pawn_Chronicle)).IsVisible
                        &&((MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow).OpenTabType==typeof(ITab_Pawn_Chronicle),"the Chronicle tab shows and opens with its setting on");
                    Shot("chronicle-tab-on.png");Wait(.4f);phase=1275;return;
                case 1275:
                    MomentFramesMod.Settings.chronicleTab=false;Wait(.6f);phase=1276;return;
                case 1276:
                    StudioCheck(!InspectTabManager.GetSharedInstance(typeof(ITab_Pawn_Chronicle)).IsVisible
                        &&((MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow).OpenTabType==null,"turning the setting off removes the Chronicle tab and closes it if open");
                    Shot("chronicle-tab-off.png");Wait(.4f);phase=1277;return;
                case 1277:
                    MomentFramesMod.Settings.chronicleTab=true;
                    var tabSetting=Path.Combine(dir,"chronicle-tab-setting.xml");
                    var tabFixture=new MomentFramesSettings{chronicleTab=false};
                    Scribe.saver.InitSaving(tabSetting,"TabSetting");Scribe_Deep.Look(ref tabFixture,"settings");Scribe.saver.FinalizeSaving();
                    tabFixture=null;Scribe.loader.InitLoading(tabSetting);Scribe_Deep.Look(ref tabFixture,"settings");Scribe.loader.FinalizeLoading();
                    StudioCheck(!tabFixture.chronicleTab,"the Chronicle tab setting stays off after saving and loading");
                    new Harmony(OnboardingPatchId).Unpatch(AccessTools.Method(typeof(Dialog_PhotoGuide),nameof(Dialog_PhotoGuide.DoWindowContents)),HarmonyPatchType.Postfix,OnboardingPatchId);
                    MomentFramesMod.Settings.albumTextSize=onboardingScale;Prefs.UIScale=onboardingUIScale;
                    File.WriteAllText(Path.Combine(dir,"runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                    File.WriteAllText(Path.Combine(dir,"onboarding-result.json"),"{\"ok\":true,\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                    active=false;Application.Quit();return;
            }
        }
    }
}
