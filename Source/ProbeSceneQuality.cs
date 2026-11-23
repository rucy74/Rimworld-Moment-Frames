using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool sceneQualityMode, qualityOccluder;
        private static PhotoRecord qualityPhoto;
        private static PhotoCapture.Result qualityImage;
        private static Battles.Fight qualityFight;
        private static Pawn qualityEnemy;
        private static Material qualityCover;
        private static Stopwatch qualityTimer;
        private static string qualityHash;
        private static int qualityCount;
        private static bool qualityRenown;

        // Optional real-mod fixture only; the product foreground renderer has no mod-specific branch.
        private static void QualityRenownFixture()
        {
            var defType=AccessTools.TypeByName("WanderersOfRenown.RenownCharacterDef");
            var factory=AccessTools.TypeByName("WanderersOfRenown.RenownPawnFactory");
            if(defType==null || factory==null)return;
            var database=typeof(DefDatabase<>).MakeGenericType(defType);
            var def=AccessTools.Method(database,"GetNamed",new[]{typeof(string),typeof(bool)}).Invoke(null,new object[]{"WoR_Shidare",true});
            var pawn=(Pawn)AccessTools.Method(factory,"Generate",new[]{defType,typeof(Faction)}).Invoke(null,new object[]{def,Faction.OfPlayer});
            var previous=octoberPeople[1];previous.Position=center+new IntVec3(20,0,20);previous.Notify_Teleported();
            GenSpawn.Spawn(pawn,center,map,Rot4.South);octoberPeople[1]=pawn;qualityRenown=true;
            OctoberCheck(pawn.kindDef.defName=="WoR_ShidareKind","real Renown Rin factory generated the parasol wearer");
        }

        private static void QualityDraw(Map __instance)
        {
            if (!qualityOccluder || __instance != map || octoberPeople == null) return;
            var position = octoberPeople[0].DrawPos; position.y += 1f; position.z += .3f;
            GenDraw.DrawMeshNowOrLater(MeshPool.plane10, Matrix4x4.TRS(position, Quaternion.identity, new Vector3(1.3f, 1f, 1.4f)), qualityCover, false);
        }
        private static void QualityCapture(bool disabled, string kind = "group")
        {
            PhotoForeground.ProbeDisabled = disabled; qualityImage = null;
            for (int i = 0; i < octoberPeople.Count; i++)
            {
                var p = octoberPeople[i];
                if (qualityRenown && i == 1) continue;   // keeps its own holding job
                p.jobs.StopAll(); p.pather.StopDead(); p.Position = center + new IntVec3(i - 1, 0, -1); p.Rotation = Rot4.South; p.Drawer.tweener.ResetTweenedPosToRoot();
            }
            qualityPhoto = new PhotoRecord { id=Guid.NewGuid().ToString("N"), kind=kind, mapId=map.uniqueID,
                center=center.ToVector3Shifted(), spanCells=7, aspect=1.5f, generatedDesc=true };
            qualityPhoto.SetPeople(octoberPeople);
            qualityPhoto.facePawnIds=octoberPeople.Select(p=>p.GetUniqueLoadID()).ToList();
            qualityPhoto.faceChoices=octoberPeople.Select((p,i)=>i==2?2:1).ToList();
            qualityTimer=Stopwatch.StartNew();
            OctoberCheck(PhotoCapture.Capture(map,qualityPhoto.center,7,r=>qualityImage=r,1.5f,reactionRecord:qualityPhoto),"focused photograph accepted");
        }
        private static void QualityImage(string name)
        {
            OctoberCheck(qualityImage?.png!=null,"actual image produced: "+name);
            File.WriteAllBytes(Path.Combine(dir,name+".png"),qualityImage.png);
            checks.Add("TIMING: "+name+" shutter-to-callback "+qualityTimer.Elapsed.TotalMilliseconds.ToString("F2")+" ms");
            checks.Add("TIMING: "+name+" render/readback "+PhotoCapture.LastRenderMilliseconds.ToString("F2")+" ms; worker encode/preview "+PhotoCapture.LastEncodeMilliseconds.ToString("F2")+" ms");
            OctoberCheck(!PhotoForeground.Queuing && octoberPeople.All(p=>!PhotoFaces.HasTemporary(p)),"foreground and facial scopes restored: "+name);
        }
        private static void SceneQualityStep()
        {
            switch(phase)
            {
                case 0:
                    deadline=Time.realtimeSinceStartup+240;map=Find.CurrentMap;center=map.Center;
                    Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;Find.Storyteller.storytellerComps.Clear();Find.Storyteller.incidentQueue.Clear();map.fogGrid.ClearAllFog();CloseLog();FxSet(false);
                    MomentFramesMod.Settings.autoCaptureEnabled=false;MomentFramesMod.Settings.brightPhotos=true;MomentFramesMod.Settings.pxPerCell=192;
                    octoberPeople=map.mapPawns.FreeColonistsSpawned.Take(3).ToList();
                    foreach(var cell in CellRect.CenteredOn(center,12,9).ClipInsideMap(map))
                    {foreach(var thing in cell.GetThingList(map).ToList())if(!(thing is Pawn))thing.Destroy();map.terrainGrid.SetTerrain(cell,TerrainDef.Named("WoodPlankFloor"));map.roofGrid.SetRoof(cell,null);}
                    octoberStudio=(Building_PhotoStudio)ThingMaker.MakeThing(ThingDef.Named("MF_StudioBackdrop"));octoberStudio.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(octoberStudio,center,map,Rot4.North);octoberStudio.SetBackdrop(1);
                    var sofa=ThingMaker.MakeThing(ThingDef.Named("MF_StudioSofa"));sofa.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(sofa,center+new IntVec3(0,0,-1),map,Rot4.North);PreparationRefreshRegions();
                    QualityRenownFixture();
                    for(int i=0;i<octoberPeople.Count;i++)
                    {var p=octoberPeople[i];p.jobs.StopAll();p.pather.StopDead();p.Position=center+new IntVec3(i-1,0,-1);p.Rotation=Rot4.South;p.Drawer.tweener.ResetTweenedPosToRoot();p.Drawer.renderer.SetAllGraphicsDirty();}
                    if(qualityRenown)
                    {
                        var waitJob=JobMaker.MakeJob(JobDefOf.Wait);waitJob.expiryInterval=100000;
                        octoberPeople[1].jobs.StartJob(waitJob,JobCondition.InterruptForced);
                        var appearance=AccessTools.TypeByName("WanderersOfRenown.ShidareAppearance");
                        OctoberCheck((bool)AccessTools.Method(appearance,"Holding").Invoke(null,new object[]{octoberPeople[1]}),"real parasol holding condition is active before both photographs");
                    }
                    qualityCover=SolidColorMaterials.SimpleSolidColorMaterial(new Color(.1f,.5f,.8f));qualityOccluder=!qualityRenown;
                    new Harmony("MomentFrames.Probe.SceneQuality").Patch(AccessTools.Method(typeof(Map),nameof(Map.MapUpdate)),postfix:new HarmonyMethod(typeof(Probe),nameof(QualityDraw)));
                    phase=1;Wait(.3f);return;
                case 1: QualityCapture(true);phase=2;return;
                case 2:
                    if(PhotoCapture.Busy)return;QualityImage("foreground-before");QualityCapture(false);phase=3;return;
                case 3:
                    if(PhotoCapture.Busy)return;QualityImage("foreground-after");
                    if(PhotoForeground.LastRecord!=qualityPhoto){checks.Add("INFO: another capture ran before the check; repeating");QualityCapture(false);return;}
                    OctoberCheck(PhotoForeground.LastPeople==3,"all three people use the common foreground pass (counted "+PhotoForeground.LastPeople+": "+string.Join(", ",octoberPeople.Select(p=>p.LabelShort+" has="+qualityPhoto.Has(p)+" frame="+PhotoCapture.InFrame(p,map,qualityPhoto.center,qualityPhoto.spanCells,qualityPhoto.Aspect)+" visible="+CapturedPhotoRoster.Visible(qualityPhoto,p,map,qualityPhoto.center,qualityPhoto.spanCells,qualityPhoto.Aspect)+" at "+p.Position))+" center "+center+")");QualityCapture(false,"manual");phase=4;return;
                case 4:
                    if(PhotoCapture.Busy)return;QualityImage("foreground-manual-control");OctoberCheck(PhotoForeground.LastPeople==0,"ordinary photographs retain scene layering");
                    qualityOccluder=false;MomentFramesMod.Settings.brightPhotos=false;QualityCapture(false);phase=5;return;
                case 5:
                    if(PhotoCapture.Busy)return;QualityImage("foreground-natural-light");
                    MomentFramesMod.Settings.autoCaptureEnabled=true;MomentFramesMod.Settings.battleMode=Battles.ModeEvery;
                    var pirates=Faction.OfPirates;
                    if(pirates==null){pirates=FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(FactionDefOf.Pirate));Find.FactionManager.Add(pirates);}
                    qualityEnemy=PawnGenerator.GeneratePawn(PawnKindDefOf.Pirate,pirates);GenSpawn.Spawn(qualityEnemy,center+new IntVec3(7,0,-3),map);
                    OctoberCheck(qualityEnemy.HostileTo(Faction.OfPlayer),"fixture has an actual hostile enemy, not a factionless generated pawn");
                    Battles.Reset();Battles.Begin(map,"raid","Probe raiders");qualityFight=Battles.Active.Single();
                    OctoberCheck(!qualityFight.shotAttempted && qualityFight.candidate==null,"threat arrival does not take a photo");
                    Battles.Attacked(octoberPeople[0],octoberPeople[1]);OctoberCheck(!qualityFight.shotAttempted,"friendly target does not trigger first exchange");
                    qualityCount=PhotoStore.Instance.Records.Count();
                    checks.Add("INFO: before launch busy="+PhotoCapture.Busy+" watching="+Battles.Watching(map)+" attacker="+octoberPeople[0].Faction+" target="+qualityEnemy.Faction+" targetSpawned="+qualityEnemy.Spawned+" actorDown="+octoberPeople[0].Downed+" patches="+string.Join(",",MomentFramesMod.FailedPatches));
                    var bullet=(Projectile)ThingMaker.MakeThing(ThingDef.Named("Bullet_AssaultRifle"));GenSpawn.Spawn(bullet,octoberPeople[0].Position,map);
                    bullet.Launch(octoberPeople[0],octoberPeople[0].DrawPos,qualityEnemy.Position+new IntVec3(3,0,0),qualityEnemy,ProjectileHitFlags.None);
                    OctoberCheck(qualityFight.shotAttempted && qualityFight.candidate!=null,"actual projectile launch records first exchange even with a miss destination; attempted="+qualityFight.shotAttempted+" candidate="+(qualityFight.candidate!=null)+" busy="+PhotoCapture.Busy);
                    OctoberCheck(PhotoStore.Instance.Records.Count()==qualityCount,"candidate is absent from album");
                    var clock=Stopwatch.StartNew();for(int i=0;i<100000;i++)Battles.Attacked(octoberPeople[0],qualityEnemy);clock.Stop();
                    checks.Add("TIMING: 100000 already-captured attack callbacks "+clock.Elapsed.TotalMilliseconds.ToString("F3")+" ms");
                    qualityTimer=Stopwatch.StartNew();phase=6;return;
                case 6:
                    if(PhotoCapture.Busy)return;
                    OctoberCheck(qualityFight.candidate.HasFile,"first exchange negative written");
                    File.Copy(Path.Combine(PhotoStore.Instance.FolderPath,qualityFight.candidate.file),Path.Combine(dir,"first-exchange.png"));
                    checks.Add("TIMING: first exchange shutter-to-file "+qualityTimer.Elapsed.TotalMilliseconds.ToString("F2")+" ms");
                    qualityHash=CaptionHash(Path.Combine(PhotoStore.Instance.FolderPath,qualityFight.candidate.file));
                    GameDataSaveLoader.SaveGame("mf-scene-quality");
                    var xml=new XmlDocument();xml.Load(GenFilePaths.FilePathForSavedGame("mf-scene-quality"));
                    OctoberCheck(xml.SelectSingleNode("//activeBattles/li/firstExchangePhoto/file")!=null,"actual save stores the candidate reference");
                    phase=7;GameDataSaveLoader.LoadGame("mf-scene-quality");Wait(.5f);return;
                case 7:
                    map=Find.CurrentMap;Find.TickManager.Pause();CloseLog();qualityFight=Battles.Active.Single();
                    OctoberCheck(qualityFight.shotAttempted && qualityFight.candidateSaved && CaptionHash(Path.Combine(PhotoStore.Instance.FolderPath,qualityFight.candidate.file))==qualityHash,"save/load preserves the exact first-exchange image");
                    qualityEnemy=qualityFight.enemies.First();qualityEnemy.Position=map.Center+new IntVec3(25,0,20);qualityEnemy.Drawer.tweener.ResetTweenedPosToRoot();
                    var finished=Battles.Finish(PhotoStore.Instance,qualityFight,Find.TickManager.TicksGame);
                    OctoberCheck(finished?.HasFile==true && !PhotoCapture.Busy && CaptionHash(PhotoStore.Instance.OriginalPath(finished))==qualityHash,"battle finish publishes the original exchange, without photographing moved actors");
                    OctoberCheck(PhotoStore.Instance.Records.Count()==qualityCount+1,"one album photograph for a fight");
                    OctoberCheck(qualityFight.candidate.captionScene=="MF_Caption_scene_battle_engagement","caption describes the engagement rather than corpses");
                    OctoberCheck(!qualityFight.candidate.desc.NullOrEmpty() && qualityFight.candidate.captionTemplate!=null,"accepted fight selects its frozen-context caption");
                    OctoberCheck(Battles.Finish(PhotoStore.Instance,qualityFight,Find.TickManager.TicksGame)==null,"finishing again does not duplicate the photograph");
                    var hard=new Battles.Fight {type="animals",mostEnemies=100};hard.colonists.AddRange(map.mapPawns.FreeColonistsSpawned);
                    OctoberCheck(!Battles.Hard(hard),"animal count alone still does not qualify a repeat fight");hard.colonistInjured=true;OctoberCheck(Battles.Hard(hard),"actual injury qualifies a hard animal fight");
                    Battles.Reset();var idle=Stopwatch.StartNew();for(int i=0;i<100000;i++)Battles.Attacked(null,LocalTargetInfo.Invalid);idle.Stop();
                    checks.Add("TIMING: 100000 no-fight callbacks "+idle.Elapsed.TotalMilliseconds.ToString("F3")+" ms");
                    var subject=map.mapPawns.FreeColonistsSpawned.First();
                    qualityEnemy.Position=subject.Position+new IntVec3(1,0,0);qualityEnemy.Drawer.tweener.ResetTweenedPosToRoot();
                    Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame+1);
                    Battles.Begin(map,"animals","Probe melee");qualityFight=Battles.Active.Single();
                    var melee=subject.meleeVerbs.TryGetMeleeVerb(qualityEnemy);
                    AccessTools.Field(typeof(Verb),"currentTarget").SetValue(melee,new LocalTargetInfo(qualityEnemy));
                    AccessTools.Method(typeof(Verb_MeleeAttack),"TryCastShot").Invoke(melee,null);
                    OctoberCheck(qualityFight.shotAttempted && qualityFight.candidate!=null,"actual melee swing captures its exchange, including a miss or dodge");
                    phase=8;return;
                case 8:
                    if(PhotoCapture.Busy)return;
                    OctoberCheck(qualityFight.candidate.HasFile,"melee candidate actually written");
                    var negative=Path.Combine(PhotoStore.Instance.FolderPath,qualityFight.candidate.file);
                    PhotoStore.Instance.AddMark("battle:first:animals");MomentFramesMod.Settings.battleMode=Battles.ModeFirsts;
                    OctoberCheck(Battles.Finish(PhotoStore.Instance,qualityFight,Find.TickManager.TicksGame)==null,"first-only setting rejects a repeated animal fight");
                    BattlePhoto.Discard(PhotoStore.Instance,qualityFight);OctoberCheck(!File.Exists(negative),"unaccepted unsaved negative removed");
                    Battles.Reset();MomentFramesMod.Settings.battleMode=Battles.ModeOff;Battles.Begin(map,"raid","disabled");OctoberCheck(Battles.Active.Count==0,"off mode starts no watcher or candidate");
                    File.WriteAllText(Path.Combine(dir,"scene-quality-result.json"),"{\"runtime\":"+Json(VersionControl.CurrentVersionString)+",\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                    File.WriteAllText(Path.Combine(dir,"runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                    active=false;Application.Quit();return;
            }
        }
    }
}
