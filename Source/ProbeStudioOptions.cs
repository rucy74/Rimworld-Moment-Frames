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
        private static PhotoSession optionsSession;
        private static Dictionary<string,IntVec3> optionsPositions;
        private static string optionsBackground;
        private static Window optionsWindow;
        private static int optionsRotation;
        private static void StudioOptionsStep()
        {
            switch(phase)
            {
                case 1100:
                    deadline=Time.realtimeSinceStartup+240;
                    var locations=map.GetComponent<PhotoLocationMapComponent>();locations.Select(studioArch);
                    StudioCheck(Birthday.SpotFor(studioChild)==studioArch,"birthday resolves explicitly selected arch rather than first built spot");
                    studioArch.DeSpawn();StudioCheck(Birthday.SpotFor(studioChild)==null,"missing preferred spot does not silently pick another");
                    GenSpawn.Spawn(studioArch,center+new IntVec3(8,0,0),map);locations.Select(studioBackdrop);
                    foreach(var p in studioFamily)FixReady(p);
                    var plan=PhotoGatheringPlan.Create(map,studioBackdrop.Position,studioFamily,Rot4.North);
                    StudioCheck(plan.CanStart,"initial studio plan ready");
                    optionsPositions=PhotoPositions.Capture(plan.Moving,plan.Cells,studioBackdrop.Position,Rot4.North);
                    string a=plan.Moving[0].GetUniqueLoadID(),b=plan.Moving[1].GetUniqueLoadID();
                    var first=optionsPositions[a];optionsPositions[a]=optionsPositions[b];optionsPositions[b]=first;
                    var custom=PhotoGatheringPlan.Create(map,studioBackdrop.Position,studioFamily,Rot4.North,positions:optionsPositions);
                    StudioCheck(custom.CanStart && custom.Cells[0]==plan.Cells[1] && custom.Cells[1]==plan.Cells[0],"manual placement swaps exact production destinations");
                    var invalid=new Dictionary<string,IntVec3>(optionsPositions){[a]=new IntVec3(40,0,40)};
                    StudioCheck(!PhotoGatheringPlan.Create(map,studioBackdrop.Position,studioFamily,Rot4.North,positions:invalid).CanStart,"outside slot cannot start gathering");
                    invalid[a]=invalid[b];StudioCheck(!PhotoGatheringPlan.Create(map,studioBackdrop.Position,studioFamily,Rot4.North,positions:invalid).CanStart,"duplicate slot cannot start gathering");
                    for(int rot=0;rot<4;rot++)
                    {
                        var offset=new IntVec3(2,0,-1);var cell=studioBackdrop.Position+Building_PhotoStudio.Rotate(offset,new Rot4(rot));
                        StudioCheck(PhotoPositions.Local(cell,studioBackdrop.Position,new Rot4(rot))==offset,"local slot round trip rotation "+rot);
                    }
                    var reactions=studioFamily.ToDictionary(p=>p.GetUniqueLoadID(),p=>ReactionChoices.None);
                    var faces=studioFamily.ToDictionary(p=>p.GetUniqueLoadID(),p=>1);
                    optionsSession=PhotoSession.Capture(studioBackdrop,studioFamily,custom.Moving,custom.Cells,Rot4.North,PhotoReactions.ModeAfter,true,true,reactions,faces);
                    PhotoSessionsGameComponent.Instance.Save(optionsSession,"Family fixture");
                    var guardianMap=new Dictionary<Pawn,Pawn>{{studioBaby,studioFamily[0]}};
                    var babyPeople=studioFamily.Concat(new[]{studioBaby}).ToList();
                    var babyPlan=PhotoGatheringPlan.Create(map,studioBackdrop.Position,babyPeople,Rot4.North,babyGuardians:guardianMap);
                    var babySession=PhotoSession.Capture(studioBackdrop,babyPeople,babyPlan.Moving,babyPlan.Cells,Rot4.North,PhotoReactions.ModeAfter,true,true,reactions,faces,guardianMap);
                    var babyDialog=new Dialog_GroupPhotoSetup(studioBackdrop);
                    AccessTools.Method(typeof(Dialog_GroupPhotoSetup),"LoadSession").Invoke(babyDialog,new object[]{babySession});
                    StudioCheck(babyDialog.GuardianFor(studioBaby)==studioFamily[0] && babyDialog.CanStart,"configuration restores baby with its chosen guardian");
                    PhotoSessionsGameComponent.Instance.Save(babySession,"Baby fixture");babyDialog.PostClose();
                    optionsWindow=new Dialog_PhotoPositions(map,studioBackdrop.Position,Rot4.North,custom.Moving,custom.Cells,faces,p=>{});
                    Find.WindowStack.Add(optionsWindow);Wait(.6f);phase=1101;return;
                case 1101:
                    Shot("positions-ui.png");Wait(.3f);phase=1102;return;
                case 1102:
                    optionsWindow.Close();
                    // Engine-created fixture tests decoder size limits, JPEG support and independent storage.
                    var fixture=new Texture2D(3000,1500,TextureFormat.RGB24,false);var pixels=new Color32[3000*1500];
                    for(int i=0;i<pixels.Length;i++)pixels[i]=(i%3000<1500)?new Color32(40,150,100,255):new Color32(180,65,45,255);
                    fixture.SetPixels32(pixels);fixture.Apply();
                    string jpg=Path.Combine(dir,"source.jpg");byte[] jpgBytes=fixture.EncodeToJPG(90);File.WriteAllBytes(jpg,jpgBytes);UnityEngine.Object.Destroy(fixture);
                    var resized=StudioBackgrounds.Load(jpg);
                    StudioCheck(resized.width==2048&&resized.height==1024,"large JPEG reduced to 2048 without stretching");
                    int before=PhotoStore.Instance.Records.Count;optionsBackground=StudioBackgrounds.Import(resized);UnityEngine.Object.Destroy(resized);
                    StudioCheck(File.ReadAllBytes(jpg).SequenceEqual(jpgBytes),"source JPEG unchanged");
                    StudioCheck(PhotoStore.Instance.Records.Count==before,"background import does not register an album photo");
                    var png=StudioBackgrounds.Load(StudioBackgrounds.PathFor(optionsBackground));
                    StudioCheck(png.width==2048&&png.height==1024,"background PNG round trip");
                    StudioCheck(StudioBackgrounds.Import(png)==optionsBackground,"same background content deduplicated");UnityEngine.Object.Destroy(png);
                    StudioCheck(StudioBackgrounds.PathFor("../escape.png")==null,"saved background path cannot escape library");
                    StudioCheck(!StudioBackgrounds.Dimensions(new byte[28],out _,out _),"invalid image header rejected before decoding");
                    var crop=StudioBackgrounds.Crop(2048,1024,2,.9f,.1f);
                    StudioCheck(crop.x>=0&&crop.y>=0&&crop.xMax<=1&&crop.yMax<=1&&Mathf.Abs(crop.width*2/crop.height-StudioBackgrounds.Aspect)<.001f,"zoom/pan crop preserves panel aspect and remains in image");
                    var realSource=Path.Combine(dir,"backdrop-source.png");
                    if(File.Exists(realSource))
                    {
                        byte[] original=File.ReadAllBytes(realSource);var real=StudioBackgrounds.Load(realSource);optionsBackground=StudioBackgrounds.Import(real);UnityEngine.Object.Destroy(real);
                        StudioCheck(File.ReadAllBytes(realSource).SequenceEqual(original),"real player photo retained while making independent backdrop");
                    }
                    studioBackdrop.SetPhotoBackground(optionsBackground,1,.5f,.5f);
                    studioArch.SetPhotoBackground(optionsBackground,1,.5f,.5f);StudioCheck(studioArch.BackgroundFile==null,"garden arch keeps real surroundings");
                    var sofa=ThingMaker.MakeThing(ThingDef.Named("MF_StudioSofa"));sofa.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(sofa,studioBackdrop.Position+new IntVec3(0,0,-1),map);
                    studioDialog=new Dialog_GroupPhotoSetup(studioBackdrop);
                    AccessTools.Method(typeof(Dialog_GroupPhotoSetup),"LoadSession").Invoke(studioDialog,new object[]{optionsSession});
                    StudioCheck(studioDialog.Chosen.Select(p=>p.GetUniqueLoadID()).OrderBy(s=>s).SequenceEqual(optionsSession.people.OrderBy(s=>s)),"configuration restores exact participants");
                    Find.WindowStack.Add(studioDialog);Wait(.6f);phase=1103;return;
                case 1103:
                    Shot("setup-ui.png");Wait(.3f);phase=1104;return;
                case 1104:
                    StudioCheck(studioDialog.StartSelected(),"restored configuration starts through setup entry point");studioDialog.Close();
                    studioJob=GroupPhoto.ActiveJob(map,studioBackdrop.Position);studioTicks=0;
                    StudioCheck(studioJob.Members.All(p=>PhotoPositions.Local(studioJob.CellFor(p),studioBackdrop.Position,Rot4.North)==optionsSession.Positions[p.GetUniqueLoadID()]),"restored per-person positions reach the actual gathering job");
                    phase=1105;return;
                case 1105:
                    if(!StudioAdvance())return;
                    Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;StudioCaptureCopy("custom-background.png");studioJob.Cancel();
                    optionsWindow=new Dialog_StudioBackground(studioBackdrop);Find.WindowStack.Add(optionsWindow);Wait(.6f);phase=1106;return;
                case 1106:
                    Shot("background-ui.png");Wait(.3f);phase=1107;return;
                case 1107:
                    optionsWindow.Close();
                    studioBackdrop.SetPhotoBackground(optionsBackground,1.7f,.8f,.2f);
                    GameDataSaveLoader.SaveGame("studio-options-fixture");Wait(.5f);phase=1108;return;
                case 1108:
                    GameDataSaveLoader.LoadGame("studio-options-fixture");Wait(.5f);phase=1109;return;
                case 1109:
                    map=Find.CurrentMap;studioBackdrop=map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoStudio>().Single(s=>s.HasCanvas);
                    optionsSession=PhotoSessionsGameComponent.Instance.sessions.Single(s=>s.name=="Family fixture");
                    studioFamily=PhotoSessionUI.Resolve(optionsSession,map);studioChild=studioFamily.Single(p=>p.DevelopmentalStage.Child());
                    StudioCheck(map.GetComponent<PhotoLocationMapComponent>().BirthdaySpot==studioBackdrop,"preferred birthday spot survives actual save/load");
                    StudioCheck(optionsSession.positions.Count==3&&optionsSession.faces.All(v=>v==1)&&optionsSession.reactions.All(v=>v==ReactionChoices.None),"configuration positions, expressions and emotes survive actual save/load");
                    var savedBaby=PhotoSessionsGameComponent.Instance.sessions.Single(s=>s.name=="Baby fixture");
                    StudioCheck(savedBaby.babyIds.Count==1&&savedBaby.guardianIds.Count==1,"baby guardian identity survives actual save/load");
                    StudioCheck(studioBackdrop.BackgroundFile==optionsBackground&&Mathf.Abs(studioBackdrop.BackgroundZoom-1.7f)<.001f&&Mathf.Abs(studioBackdrop.BackgroundX-.8f)<.001f,"custom background and crop survive actual save/load");
                    foreach(var p in studioFamily)FixReady(p);
                    var bplan=BirthdayPreparationPlan.Create(studioChild,studioBackdrop.Position,studioFamily,Rot4.North,null,positions:optionsSession.Positions);
                    StudioCheck(bplan.CanStart,"birthday validates saved custom positions");
                    StudioCheck(Birthday.StartPrepared(studioChild,6,studioBackdrop.Position,studioFamily,Rot4.North,null,faces:optionsSession.Faces,positions:optionsSession.Positions),"birthday starts with manual positions");
                    studioJob=Birthday.Active(studioChild);studioTicks=0;phase=1110;return;
                case 1110:
                    if(!StudioAdvance())return;Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
                    StudioCaptureCopy("birthday-background.png");studioJob.Cancel();optionsRotation=0;phase=1111;return;
                case 1111:
                    if(optionsRotation>=4){phase=1113;return;}
                    var origin=studioBackdrop.Position;studioBackdrop.DeSpawn();GenSpawn.Spawn(studioBackdrop,origin,map,new Rot4(optionsRotation));
                    Find.CameraDriver.SetRootPosAndSize(studioBackdrop.DrawPos,8);Wait(.4f);phase=1112;return;
                case 1112:
                    Shot("background-rotation-"+optionsRotation+".png");Wait(.2f);optionsRotation++;phase=1111;return;
                case 1113:
                    File.WriteAllText(Path.Combine(dir,"runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                    File.WriteAllText(Path.Combine(dir,"studio-options-result.json"),"{\"ok\":true,\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                    active=false;Application.Quit();return;
            }
        }
    }
}
