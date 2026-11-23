using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool playtestOctoberMode;
        private static LordJob_MFGroupPhoto octoberJob;
        private static List<Pawn> octoberPeople;
        private static Building_PhotoStudio octoberStudio;
        private static int octoberFaceCount, octoberBaked, octoberMode, octoberCaptures;
        private static string octoberRecord;
        private static LordJob_MFGroupPhoto octoberPetJob;
        private static string octoberPetRecord;
        private static void OctoberCheck(bool ok,string label)
        { checks.Add((ok?"PASS: ":"FAIL: ")+label); if(!ok)throw new InvalidOperationException(label); }
        private static void OctoberFaces(PhotoRecord record,PhotoFaces.Lease __result)
        {
            if(record?.kind!="group")return;
            octoberFaceCount=__result.Owned.Count;
            OctoberCheck(record.faceChoices.Count==3 && record.captureReactionMode==octoberMode,"actual group request prepares faces and reaction mode BEFORE capture");
            OctoberCheck(record.peopleIds.Count==3,"settled fixture exposes all three subjects to the capture frame; roster="+string.Join(",",record.peopleIds));
        }
        private static void OctoberBake(PhotoReactions.Plan plan,int __result)
        { if(plan?.Record?.kind=="group")octoberBaked=__result; }
        private static void OctoberRequest(int mode)
        {
            octoberMode=mode;octoberFaceCount=0;octoberBaked=-1;
            var faces=octoberPeople.Select((p,i)=>new {p,i}).ToDictionary(x=>x.p.GetUniqueLoadID(),x=>x.i==2?2:1);
            var reactions=octoberPeople.ToDictionary(p=>p.GetUniqueLoadID(),p=>ReactionChoices.Heart);
            if(octoberJob==null)
            {
                OctoberPreparePreviousJob();
                var lord=GroupPhoto.StartPrepared(map,octoberStudio.Position,octoberPeople,Rot4.North,null,null,mode,false,reactions,false,faces);
                OctoberCheck(lord!=null,"actual group preparation accepts three studio participants");octoberJob=(LordJob_MFGroupPhoto)lord.LordJob;
                OctoberCheckPreviousJob();
            }
            else octoberJob.SetPreparationOptions(octoberPeople,mode,false,reactions,false,faces);
            foreach(var pawn in octoberPeople)
            {
                pawn.pather.StopDead();pawn.Position=octoberJob.CellFor(pawn);pawn.Rotation=Rot4.South;
                pawn.Drawer.tweener.ResetTweenedPosToRoot();
                pawn.jobs.StartJob(JobMaker.MakeJob(MFDefs.Pose,octoberJob.CellFor(pawn)),JobCondition.InterruptForced);
            }
            OctoberCheck(octoberJob.InPlace==3,"three actual pawns are in their photo seats");
        }
        private static void OctoberShutter()
        {
            octoberJob.ShootNow();
            AccessTools.Method(typeof(LordJob_MFGroupPhoto),"TryShoot").Invoke(octoberJob,new object[]{Find.TickManager.TicksGame});
            var rec=PhotoStore.Instance.Records.Where(r=>r.kind=="group").OrderByDescending(r=>r.ticksAbs).LastOrDefault(r=>!r.HasFile);
            OctoberCheck(rec!=null && PhotoCapture.Busy,"group shutter accepted with a pending capture");octoberRecord=rec.id;
            if(PhotoFaces.Available)OctoberCheck(octoberFaceCount==3,"actual group shutter installs three selected face overrides; actual="+octoberFaceCount+"; "+string.Join("; ",octoberPeople.Select((p,i)=>p.LabelShort+" facing="+p.Rotation+" supported="+PhotoFaces.Supports(p,i==2?2:1)+" temporary="+PhotoFaces.HasTemporary(p)+" frame="+(PhotoFaces.FrameFor(p,i==2?2:1)!=null))));
        }
        private static void OctoberUiChecks()
        {
            var rec=PhotoStore.Instance.Get(octoberRecord);rec.desc="성자 · 헤어리. 이 사진에 함께 붙인 설명이다.";rec.favorite=true;
            MainTabWindow_Album.OpenFor(octoberPeople[0],rec);
            var album=Find.MainTabsRoot.OpenTab.TabWindow as MainTabWindow_Album;
            OctoberCheck(album!=null && album.HasActiveFilters,"person navigation visibly exposes an active filter");
            album.ShowAllPhotos();OctoberCheck(!album.HasActiveFilters,"all-photos action clears contextual and favorite filters");
            using(AlbumStyle.Begin())
            {
                string display=AlbumPresentation.Title(rec.desc);
                OctoberCheck(AlbumStyle.StyleFor(display).font==AlbumStyle.StyleFor("성자 헤어리").font && rec.desc.Contains("·"),"caption display keeps handwriting while original stored text is unchanged");
            }
            MainTabWindow_Album.OpenFor(octoberPeople[0],rec);
            Find.MainTabsRoot.SetCurrentTab(null);
            OctoberCheck(!album.HasActiveFilters,"closing a contextual person view clears its temporary filter");
            MainTabWindow_Album.Open(rec);
            MainTabWindow_Album.OpenModSettings();
            OctoberCheck(Find.WindowStack.Windows.Any(w=>w is Dialog_ModSettings) && album.GalleryCurrent==rec,"mod settings opens over the album without losing its photo");
            Find.WindowStack.TryRemove(typeof(Dialog_ModSettings),false);
            AccessTools.Field(typeof(MainTabWindow_Album),"favoritesOnly").SetValue(album,true);
            album.FollowPerson(rec,octoberPeople[0].GetUniqueLoadID(),octoberPeople[0].LabelShort);
            album.ReturnFromPeople();Find.MainTabsRoot.SetCurrentTab(null);
            OctoberCheck((bool)AccessTools.Field(typeof(MainTabWindow_Album),"favoritesOnly").GetValue(album),"returning from person navigation preserves the prior manual favorite filter");
            album.ShowAllPhotos();MainTabWindow_Album.Open(rec);CloseLog();
        }
        private static void PlaytestOctoberStep()
        {
            switch(phase)
            {
                case 0:
                    deadline=Time.realtimeSinceStartup+240;map=Find.CurrentMap;center=map.Center;
                    Find.TickManager.Pause();Find.Storyteller.storytellerComps.Clear();Find.Storyteller.incidentQueue.Clear();map.fogGrid.ClearAllFog();CloseLog();
                    FxSet(false);MomentFramesMod.Settings.autoCaptureEnabled=false;MomentFramesMod.Settings.brightPhotos=true;
                    octoberPeople=map.mapPawns.FreeColonistsSpawned.Take(3).ToList();
                    foreach(var p in octoberPeople){p.needs.food.CurLevelPercentage=1;p.needs.rest.CurLevelPercentage=1;}
                    foreach(var other in map.mapPawns.AllPawnsSpawned.Where(p=>!octoberPeople.Contains(p)).ToList())
                    {other.Position=CellFinder.StandableCellNear(center+new IntVec3(20,0,20),map,5f);other.Drawer.tweener.ResetTweenedPosToRoot();}
                    OctoberBubbleChecks();PlaytestIconChecks();PlaytestStudioChecks();PlaytestRecruitChecks();
                    if(PhotoFaces.Available)PlaytestFaceChecks(octoberPeople[0]);else checks.Add("INFO: Facial Animation absent; preview compatibility not exercised");
                    foreach(var cell in CellRect.CenteredOn(center,12,9).ClipInsideMap(map))
                    {
                        foreach(var thing in cell.GetThingList(map).ToList())if(!(thing is Pawn))thing.Destroy();
                        map.terrainGrid.SetTerrain(cell,TerrainDef.Named("WoodPlankFloor"));map.roofGrid.SetRoof(cell,null);
                    }
                    octoberStudio=(Building_PhotoStudio)ThingMaker.MakeThing(ThingDef.Named("MF_StudioBackdrop"));octoberStudio.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(octoberStudio,center,map,Rot4.North);octoberStudio.SetBackdrop(1);
                    var sofa=ThingMaker.MakeThing(ThingDef.Named("MF_StudioSofa"));sofa.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(sofa,center+new IntVec3(0,0,-1),map,Rot4.North);PreparationRefreshRegions();
                    PlaytestLocationChecks();
                    ProjectionGeometry.Fit(1.5f,40,40,out float fw,out float fh);
                    OctoberCheck(Math.Abs(ProjectionGeometry.Throw(false,0,fw,fh,40,40,true)-fh/2-1.2f)<.001f,"maximum projection leaves a constant 1.2-cell gap at its actual near edge");
                    var party=new LordJob_Joinable_Party(center,octoberPeople[1],GatheringDefOf.Party);
                    var partyLord=LordMaker.MakeNewLord(Faction.OfPlayer,party,map,octoberPeople.Take(2));
                    OctoberCheck(GroupPhoto.CanJoin(octoberPeople[0]) && !GroupPhoto.CanJoin(octoberPeople[1]),"ordinary party guest can be called while its organizer remains protected");
                    PhotoGatheringAccess.Leave(octoberPeople[0]);OctoberCheck(octoberPeople[0].GetLord()==null && octoberPeople[1].GetLord()==partyLord,"calling a guest leaves the party organizer and party intact");
                    partyLord.Notify_PawnLost(octoberPeople[1],PawnLostCondition.LeftVoluntarily);
                    var harmony=new Harmony("MomentFrames.Probe.PlaytestOctober");
                    harmony.Patch(AccessTools.Method(typeof(PhotoFaces),"Begin"),postfix:new HarmonyMethod(typeof(Probe),nameof(OctoberFaces)));
                    harmony.Patch(AccessTools.Method(typeof(PhotoReactions),"Bake"),postfix:new HarmonyMethod(typeof(Probe),nameof(OctoberBake)));
                    phase=1;Wait(.5f);return;
                case 1:
                    OctoberRequest(octoberCaptures==0?PhotoReactions.ModeAfter:PhotoReactions.ModePhoto);phase=10;Wait(.3f);return;
                case 10:
                    OctoberShutter();phase=2;return;
                case 2:
                    if(PhotoCapture.Busy)return;
                    var record=PhotoStore.Instance.Get(octoberRecord);
                    OctoberCheck(record?.HasFile==true,"group photo actually written to disk in mode "+octoberMode);
                    OctoberCheck(octoberBaked==(octoberMode==PhotoReactions.ModePhoto?3:0),"photo reactions obey requested mode "+octoberMode+"; stamped="+octoberBaked);
                    OctoberCheck(octoberPeople.All(p=>!PhotoFaces.HasTemporary(p)),"face override leases released after shutter");
                    File.Copy(PhotoStore.Instance.FilePath(record),Path.Combine(dir,"studio-mode-"+octoberMode+".png"));
                    if(++octoberCaptures<2){phase=1;Wait(.3f);return;}
                    octoberJob.Cancel();
                    OctoberCheck(GroupPhoto.CanJoin(octoberPeople[0]),"a dismissed photo no longer blocks participants while the game is paused");
                    var pet=PetPhotos.Animals(map).FirstOrDefault(p=>PetPhotos.EligibleAnimal(p));
                    if(pet==null){pet=PawnGenerator.GeneratePawn(PawnKindDef.Named("Husky"),Faction.OfPlayer);GenSpawn.Spawn(pet,center+new IntVec3(6,0,0),map);}
                    OctoberCheck(PetPhotos.CanStart(octoberStudio,octoberPeople[0],pet,out string petWhy),"pet setup available after group cleanup: "+petWhy);
                    octoberPetJob=PetPhotos.Start(octoberStudio,octoberPeople[0],pet);
                    OctoberCheck(octoberPetJob!=null,"actual pet-photo action accepts an owner and animal at the studio");
                    foreach(var p in octoberPetJob.lord.ownedPawns)
                    {p.pather.StopDead();p.Position=octoberPetJob.CellFor(p);p.Drawer.tweener.ResetTweenedPosToRoot();p.jobs.StartJob(JobMaker.MakeJob(MFDefs.Pose,p.Position),JobCondition.InterruptForced);}
                    foreach(var p in octoberPeople.Skip(1)){p.Position=center+new IntVec3(15,0,0);p.Drawer.tweener.ResetTweenedPosToRoot();}
                    phase=20;Wait(.3f);return;
                case 20:
                    octoberPetJob.ShootNow();AccessTools.Method(typeof(LordJob_MFGroupPhoto),"TryShoot").Invoke(octoberPetJob,new object[]{Find.TickManager.TicksGame});
                    var petRecord=PhotoStore.Instance.Records.LastOrDefault(r=>r.kind==PetPhotos.Kind);
                    OctoberCheck(petRecord!=null && PhotoCapture.Busy,"actual owner and pet shutter accepted");octoberPetRecord=petRecord.id;phase=21;return;
                case 21:
                    if(PhotoCapture.Busy)return;
                    var petPhoto=PhotoStore.Instance.Get(octoberPetRecord);
                    OctoberCheck(petPhoto?.HasFile==true && petPhoto.peopleIds.Count==2,"owner and pet photograph saved with both subjects");
                    OctoberCheck(GroupPhoto.CanJoin(octoberPeople[0]),"completed pet photo also releases eligibility while paused");
                    File.Copy(PhotoStore.Instance.FilePath(petPhoto),Path.Combine(dir,"studio-pet.png"));
                    OctoberBackdropAndLightChecks();OctoberUiChecks();phase=3;Wait(.5f);return;
                case 3:
                    CloseLog();Shot("album-feedback.png");phase=24;Wait(.4f);return;
                case 24:
                    map.GetComponent<PhotoLocationMapComponent>().Rename(octoberStudio,"정원 가족사진 자리");
                    Find.WindowStack.Add(new Dialog_PhotoLocation(map,octoberStudio,(s,p)=>{}));phase=22;Wait(.4f);return;
                case 22:
                    Shot("photo-locations.png");phase=25;Wait(.4f);return;
                case 25:
                    Find.WindowStack.TryRemove(typeof(Dialog_PhotoLocation),false);
                    Find.WindowStack.Add(new Dialog_BirthdaySetup(octoberPeople[0],24,null,octoberStudio.Position));phase=23;Wait(.4f);return;
                case 23:
                    Shot("birthday-location.png");phase=26;Wait(.4f);return;
                case 26:
                    Find.WindowStack.TryRemove(typeof(Dialog_BirthdaySetup),false);Find.MainTabsRoot.SetCurrentTab(null);
                    Find.WindowStack.Add(new Dialog_StudioFurnishing(octoberStudio));phase=27;Wait(.4f);return;
                case 27:
                    Shot("studio-decorate.png");phase=4;Wait(.4f);return;
                case 4:
                    File.WriteAllText(Path.Combine(dir,"playtest-result.json"),"{\"ok\":true,\"runtime\":"+Json(VersionControl.CurrentVersionString)+",\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                    File.WriteAllText(Path.Combine(dir,"runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                    active=false;Application.Quit();return;
            }
        }
    }
}
