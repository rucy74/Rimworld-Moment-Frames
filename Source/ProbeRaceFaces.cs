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
        private static string[][] raceCases;
        private static int raceCaseIndex, raceChoice;
        private static Pawn racePawn;
        private static PhotoFacePreview racePreview;
        private static List<string> raceResults=new List<string>();
        private static bool raceCaptured;
        private static string raceError;
        private static string RacePrefix=>raceCaseIndex+"-"+racePawn.def.defName;
        private static void RaceFacesStep()
        {
            switch(phase)
            {
                case 750:
                    GenCommandLine.TryGetCommandLineArg("momentFramesRaceFaces",out var cases);
                    raceCases=File.ReadAllLines(cases).Where(s=>!string.IsNullOrWhiteSpace(s)).Select(s=>s.Split('\t')).ToArray();
                    StudioCheck(PhotoFaces.Available,"FA available for race tests");
                    deadline=Time.realtimeSinceStartup+300f;
                    MomentFramesMod.Settings.brightPhotos=true;
                    Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
                    foreach(var p in map.mapPawns.FreeColonistsSpawned.ToList()) {p.Position=center+new IntVec3(20,0,20);p.Notify_Teleported();}
                    for(int i=-3;i<=3;i++)for(int z=-3;z<=3;z++)map.terrainGrid.SetTerrain(center+new IntVec3(i,0,z),TerrainDefOf.Soil);
                    phase=751;return;
                case 751:
                    if(raceCaseIndex>=raceCases.Length)
                    {
                        File.WriteAllText(Path.Combine(dir,"race-results.json"),"{\"ok\":true,\"cases\":["+string.Join(",",raceResults)+"],\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                        File.WriteAllText(Path.Combine(dir,"runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                        active=false;Application.Quit();return;
                    }
                    var row=raceCases[raceCaseIndex];float age=float.Parse(row[2]);
                    var kind=DefDatabase<PawnKindDef>.GetNamed(row[0]);
                    var request=new PawnGenerationRequest(kind,Faction.OfPlayer,forceGenerateNewPawn:true,canGeneratePawnRelations:false,fixedBiologicalAge:age,fixedChronologicalAge:age,fixedGender:(Gender)Enum.Parse(typeof(Gender),row[3]),developmentalStages:age<13?DevelopmentalStage.Child:DevelopmentalStage.Adult);
                    request.IsCreepJoiner=kind.defName=="Tinimar_CreepJoiner_grey";
                    racePawn=PawnGenerator.GeneratePawn(request);
                    StudioCheck(racePawn.def.defName==row[1],"generated intended race "+row[1]);
                    if(row[4]!="keep")foreach(var a in racePawn.apparel.WornApparel.ToList()){racePawn.apparel.Remove(a);a.Destroy();}
                    foreach(var name in row[4].Split(',').Where(s=>s.Length>0 && s!="keep"))
                    {
                        var def=ThingDef.Named(name);
                        var item=(Apparel)ThingMaker.MakeThing(def,def.MadeFromStuff?GenStuff.DefaultStuffFor(def):null);
                        racePawn.apparel.Wear(item,false,false);
                    }
                    GenSpawn.Spawn(racePawn,center,map,Rot4.South);
                    FixReady(racePawn);racePawn.jobs.StopAll();racePawn.Rotation=Rot4.South;
                    Find.CameraDriver.SetRootPosAndSize(racePawn.DrawPos,4f);
                    for(int i=0;i<3;i++)Find.TickManager.DoSingleTick();
                    racePawn.Rotation=Rot4.South;racePawn.Drawer.renderer.SetAllGraphicsDirty();
                    racePreview=new PhotoFacePreview();raceChoice=0;
                    CloseLog();Wait(.5f);phase=752;return;
                case 752:
                    var faces=new List<string>();
                    foreach(var c in racePawn.AllComps.Where(c=>c.GetType().Namespace=="FacialAnimation"))
                    {
                        object ft=AccessTools.Property(c.GetType(),"FaceType")?.GetValue(c,null);
                        faces.Add(c.GetType().Name+":"+ft+":"+(ft==null?"":AccessTools.Field(ft.GetType(),"texPath")?.GetValue(ft)));
                    }
                    File.WriteAllText(Path.Combine(dir,RacePrefix+"-diagnostic.txt"),string.Join("\n",faces)+"\nbody="+racePawn.story.bodyType+"\ngenes="+string.Join(",",racePawn.genes.GenesListForReading.Select(g=>g.def.defName)));
                    phase=753;return;
                case 753:
                    bool supported=PhotoFaces.Supports(racePawn,raceChoice);
                    var shapeFields=racePawn.AllComps.Select(c=>Tuple.Create(c,AccessTools.Field(c.GetType(),"Shape"))).Where(t=>t.Item2!=null).ToList();
                    var before=shapeFields.Select(t=>t.Item2.GetValue(t.Item1)).ToArray();
                    var preview=racePreview.Get(racePawn,raceChoice);
                    StudioCheck(preview!=null,RacePrefix+" preview "+raceChoice);
                    SavePreview(preview,RacePrefix+"-preview-"+raceChoice+".png");
                    StudioCheck(before.SequenceEqual(shapeFields.Select(t=>t.Item2.GetValue(t.Item1))),RacePrefix+" preview leaves live shapes unchanged "+raceChoice);
                    var rec=new PhotoRecord{peopleIds=new List<string>{racePawn.GetUniqueLoadID()},facePawnIds=new List<string>{racePawn.GetUniqueLoadID()},faceChoices=new List<int>{raceChoice}};
                    using(var lease=PhotoFaces.Begin(map,rec))
                    {
                        StudioCheck(lease.Owned.Count==(supported&&raceChoice>0?1:0),RacePrefix+" supported pose applied or skipped "+raceChoice);
                        if(lease.Owned.Count>0)
                        {
                            var owned=lease.Owned[0];var held=owned.Item3.Cast<object>().ToArray();
                            using(var other=PhotoFaces.Begin(map,rec)) StudioCheck(other.Owned.Count==0 && held.SequenceEqual(owned.Item3.Cast<object>()),RacePrefix+" existing temporary pose respected "+raceChoice);
                        }
                    }
                    StudioCheck(!PhotoFaces.HasTemporary(racePawn),RacePrefix+" temporary pose released "+raceChoice);
                    raceCaptured=false;raceError=null;
                    string file=RacePrefix+"-photo-"+raceChoice+".png";
                    StudioCheck(PhotoCapture.Capture(map,racePawn.DrawPos+new Vector3(0,0,.3f),3f,r=>
                    {
                        if(r?.png==null)raceError="empty capture";
                        else File.WriteAllBytes(Path.Combine(dir,file),r.png);
                        raceCaptured=true;
                    },1f,reactionRecord:rec),RacePrefix+" actual capture starts "+raceChoice);
                    raceResults.Add("{\"kind\":"+Json(racePawn.kindDef.defName)+",\"race\":"+Json(racePawn.def.defName)+",\"choice\":"+raceChoice+",\"supported\":"+(supported?"true":"false")+",\"file\":"+Json(file)+"}");
                    phase=754;return;
                case 754:
                    if(!raceCaptured)return;
                    StudioCheck(raceError==null,RacePrefix+" actual PNG saved "+raceChoice);
                    StudioCheck(!PhotoFaces.HasTemporary(racePawn),RacePrefix+" capture restores animation ownership "+raceChoice);
                    raceChoice++;
                    if(raceChoice<=4){phase=753;return;}
                    SavePreview(racePreview.Get(racePawn,0),RacePrefix+"-preview-reset.png");
                    racePreview.Dispose();racePreview=null;
                    racePawn.DeSpawn();racePawn.Destroy();racePawn=null;raceCaseIndex++;
                    phase=751;return;
            }
        }
    }
}
