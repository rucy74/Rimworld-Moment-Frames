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
        private static List<Pawn> matrixAdults;
        private static int matrixCase;
        private static List<Pawn> matrixPeople;
        private static string matrixName;
        private static IntVec3 matrixOrigin;
        private static float matrixDeadline;
        private static void MatrixStart()
        {
            if(studioJob!=null&&!studioJob.Finished)studioJob.Cancel();
            foreach(var p in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                FixReady(p);p.jobs.StopAll();
                if(p.carryTracker.CarriedThing!=null)p.carryTracker.TryDropCarriedThing(matrixOrigin+new IntVec3(10,0,-10),ThingPlaceMode.Direct,out _);
                p.Position=matrixOrigin+new IntVec3(7+map.mapPawns.FreeColonistsSpawned.ToList().IndexOf(p)%5,0,-8);p.Notify_Teleported();
                if(!p.DevelopmentalStage.Baby())p.drafter.Drafted=true;
            }
            foreach(var seat in map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSeat>().ToList())seat.Destroy();
            var rotation=matrixCase<4?new Rot4(matrixCase):Rot4.North;
            studioBackdrop.DeSpawn();GenSpawn.Spawn(studioBackdrop,matrixOrigin,map,rotation);studioBackdrop.ClearFrame();studioBackdrop.SetBackdrop(matrixCase==1?1:matrixCase==5?2:0);
            furnishingSeat=(Building_PhotoSeat)ThingMaker.MakeThing(ThingDef.Named(matrixCase==1?"MF_StudioBench":matrixCase==3||matrixCase==5?"MF_StudioChair":"MF_StudioSofa"));
            furnishingSeat.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(furnishingSeat,matrixOrigin+Building_PhotoStudio.Rotate(new IntVec3(0,0,-1),rotation),map,rotation);
            if(matrixCase<4)matrixPeople=matrixAdults.Take(matrixCase%2==0?10:6).ToList();
            else if(matrixCase==4)matrixPeople=matrixAdults.Take(2).Concat(new[]{studioBaby}).ToList();
            else matrixPeople=matrixAdults.Take(5).Concat(new[]{studioChild}).ToList();
            foreach(var p in matrixPeople)FixReady(p);
            var faceChoices=matrixPeople.ToDictionary(p=>p.GetUniqueLoadID(),p=>PhotoFaces.Supports(p,1)?1:0);
            matrixName=matrixCase<4?"matrix-"+matrixPeople.Count+"-"+rotation:matrixCase==4?"matrix-baby":"matrix-birthday";
            if(matrixCase==5)
            {
                StudioCheck(Birthday.StartPrepared(studioChild,6,matrixOrigin,matrixPeople,rotation,null,faces:faceChoices),"birthday with furnished studio starts");
                studioJob=Birthday.Active(studioChild);
            }
            else
            {
                var guardians=matrixCase==4?new Dictionary<Pawn,Pawn>{{studioBaby,matrixAdults[0]}}:null;
                studioJob=(LordJob_MFGroupPhoto)GroupPhoto.StartPrepared(map,matrixOrigin,matrixPeople,rotation,babyGuardians:guardians,faces:faceChoices)?.LordJob;
            }
            StudioCheck(studioJob!=null,matrixName+" production gathering starts");
            var cells=studioJob.Members.Select(p=>studioJob.CellFor(p)).ToList();
            StudioCheck(cells.Count==cells.Distinct().Count(),matrixName+" distinct places for every moving participant");
            var seats=studioBackdrop.SeatCells();
            StudioCheck(seats.Count(c=>cells.Contains(c))==Math.Min(seats.Count,cells.Count),matrixName+" furniture places filled before standing positions");
            if(matrixCase==5)StudioCheck(studioJob.CellFor(studioChild)==seats[0],"birthday child gets the front chair");
            Find.CameraDriver.SetRootPosAndSize(studioBackdrop.DrawPos,10f);Find.TickManager.CurTimeSpeed=TimeSpeed.Normal;
            matrixDeadline=Time.realtimeSinceStartup+90f;phase=952;
        }
        private static void MatrixFaceChecks()
        {
            StudioCheck(PhotoFaces.Choice(new Dictionary<string,int>(),matrixAdults[0].GetUniqueLoadID())==0,"unassigned facial expression remains none");
            if(!PhotoFaces.Available)
            {StudioCheck(!PhotoFaces.Supports(matrixAdults[0],1),"absent FA keeps expression unsupported safely");return;}
            var pawn=matrixAdults.First(p=>PhotoFaces.Supports(p,1));
            var rec=new PhotoRecord{peopleIds=new List<string>{pawn.GetUniqueLoadID()},facePawnIds=new List<string>{pawn.GetUniqueLoadID()},faceChoices=new List<int>{1}};
            using(var lease=PhotoFaces.Begin(map,rec))
            {
                StudioCheck(lease.Owned.Count==1,"supported race receives temporary smile");
                using(var second=PhotoFaces.Begin(map,rec))StudioCheck(second.Owned.Count==0,"foreign temporary expression is not overwritten");
                var owned=lease.Owned[0];var foreign=AccessTools.Method(typeof(object),"MemberwiseClone").Invoke(owned.Item4,null);
                owned.Item3.Add(foreign);lease.Dispose();StudioCheck(owned.Item3.Count==1&&owned.Item3.Contains(foreign),"cleanup preserves another effect's expression");owned.Item3.Remove(foreign);
            }
        }
        private static void StudioMatrixStep()
        {
            switch(phase)
            {
                case 950:
                    deadline=Time.realtimeSinceStartup+600f;map=Find.CurrentMap;matrixOrigin=center+new IntVec3(10,0,6);
                    studioChild=map.mapPawns.FreeColonistsSpawned.First(p=>p.DevelopmentalStage.Child());studioBaby=map.mapPawns.FreeColonistsSpawned.First(p=>p.DevelopmentalStage.Baby());
                    // Furnishings are fixtures here. Their natural construction was independently exercised above.
                    foreach(var c in CellRect.CenteredOn(matrixOrigin,12))
                    {
                        map.roofGrid.SetRoof(c,null);map.areaManager.NoRoof[c]=true;
                        foreach(var t in c.GetThingList(map).ToList())if(!(t is Pawn)&&t!=studioBackdrop&&t.def.destroyable)t.Destroy();
                        map.terrainGrid.SetTerrain(c,TerrainDef.Named("WoodPlankFloor"));map.fogGrid.Unfog(c);
                    }
                    matrixAdults=map.mapPawns.FreeColonistsSpawned.Where(p=>p.DevelopmentalStage.Adult()).ToList();
                    while(matrixAdults.Count<10)
                    {
                        var p=PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist,Faction.OfPlayer,forceGenerateNewPawn:true,canGeneratePawnRelations:false,fixedBiologicalAge:30));
                        GenSpawn.Spawn(p,matrixOrigin+new IntVec3(9,0,-8),map);matrixAdults.Add(p);
                    }
                    var bodies=new[]{BodyTypeDefOf.Male,BodyTypeDefOf.Female,BodyTypeDefOf.Thin,BodyTypeDefOf.Fat,BodyTypeDefOf.Hulk};
                    for(int i=0;i<matrixAdults.Count;i++){matrixAdults[i].story.bodyType=bodies[i%bodies.Length];matrixAdults[i].Drawer.renderer.SetAllGraphicsDirty();}
                    PreparationRefreshRegions();MatrixFaceChecks();
                    bool failed=false;try{NativePhotoOrder.Render(null);}catch(NullReferenceException){failed=true;}
                    StudioCheck(failed&&!NativePhotoOrder.Active,"failed camera render also releases native ordering scope");
                    if(RuntimeAudio.Waylith)
                    {
                        int before=NativePhotoOrder.SortCount;var camera=Find.Camera;var previous=camera.targetTexture;
                        var target=RenderTexture.GetTemporary(16,16,24,RenderTextureFormat.ARGB32);
                        try {camera.targetTexture=target;camera.Render();}
                        finally {camera.targetTexture=previous;RenderTexture.ReleaseTemporary(target);}
                        StudioCheck(NativePhotoOrder.SortCount==before,"unrelated native camera render is not reordered");
                    }
                    matrixCase=0;MatrixStart();return;
                case 952:
                    if(Time.realtimeSinceStartup>matrixDeadline)throw new TimeoutException(matrixName+" gathering: "+studioJob.Result+" "+studioJob.InPlace+"/"+studioJob.Roster);
                    if(studioJob.Takes==0){if(studioJob.Finished)throw new InvalidOperationException(matrixName+" finished without photo "+studioJob.Result);return;}
                    Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
                    StudioCaptureCopy(matrixName+".png");
                    var rec=PhotoStore.Instance.Get(studioJob.PhotoId);
                    StudioCheck(matrixPeople.All(p=>rec.peopleIds.Contains(p.GetUniqueLoadID())),matrixName+" photo metadata retains every selected person");
                    if(matrixCase==4)
                    {
                        StudioCheck(studioJob.Finished,"baby group dismisses so guardian can resume care");
                        StudioCheck(PhotoCapture.VisualPosition(studioBaby,map,out var pos)&&PhotoCapture.InFrame(studioBaby,map,rec.center,rec.spanCells,rec.Aspect),"carried baby has a visible position inside saved frame");
                    }
                    else if(matrixCase==5)StudioCheck(rec.kind==Kinds.Birthday,"birthday record retains its event type");
                    else StudioCheck(studioJob.Holding,"ordinary "+matrixName+" holds for retakes");
                    if(matrixCase++<5){phase=953;Wait(.2f);return;}
                    File.WriteAllText(Path.Combine(dir,"runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                    File.WriteAllText(Path.Combine(dir,"studio-result.json"),"{\"ok\":true,\"runtime\":"+Json(VersionControl.CurrentVersionString)+",\"facialAnimation\":"+(PhotoFaces.Available?"true":"false")+",\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                    active=false;Application.Quit();return;
                case 953: MatrixStart();return;
            }
        }
    }
}
