using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        // Only entered by the disposable probe flag. The room, workers and raw materials
        // are fixtures; the three studio pieces, generator and wiring must be built by AI.
        // Never set PowerOn, inject fuel, complete a Frame, or spawn a finished target here.
        private static readonly HashSet<string> constructionJobs = new HashSet<string>();
        private static readonly HashSet<string> constructionFrames = new HashSet<string>();
        private static List<Pawn> constructionWorkers;
        private static List<Building_PhotoStudio.FurniturePlan> constructionPlans;
        private static Building constructionGenerator;
        private static IntVec3 constructionGeneratorCell;
        private static int constructionStart, constructionStageTick, constructionConduits;
        private static int constructionLastReport;
        private static bool constructionHaulShot, constructionWorkShot;

        private static void ConstructionMaterials(ThingDef def, int count, IntVec3 at)
        {
            while(count>0)
            {
                var thing=ThingMaker.MakeThing(def);thing.stackCount=Math.Min(count,def.stackLimit);
                count-=thing.stackCount;GenPlace.TryPlaceThing(thing,at,map,ThingPlaceMode.Near);
                thing.SetForbidden(false);
            }
        }
        private static int ConstructionResource(ThingDef def) => map.listerThings.ThingsOfDef(def).Sum(t=>t.stackCount)
            + map.mapPawns.FreeColonistsSpawned.Where(p=>p.carryTracker?.CarriedThing?.def==def).Sum(p=>p.carryTracker.CarriedThing.stackCount);
        private static void ConstructionObserve()
        {
            // Whoever takes the job counts, not only the listed builders: a colonist outside the list can finish the frame first.
            foreach(var p in map.mapPawns.FreeColonistsSpawned)
            {
                var job=p.CurJob;if(job==null)continue;
                foreach(var target in new[]{job.targetA.Thing,job.targetB.Thing,job.targetC.Thing}.Where(t=>t!=null))
                {
                    var def=target.def.entityDefToBuild as ThingDef ?? target.def;
                    if(def.defName.StartsWith("MF_Studio")||def.defName=="WoodFiredGenerator"||def.defName=="PowerConduit")
                        constructionJobs.Add(job.def.defName+":"+def.defName);
                }
            }
            foreach(var frame in map.listerThings.AllThings.OfType<Frame>())
                constructionFrames.Add(frame.def.entityDefToBuild.defName);
        }
        private static void ConstructionAdvance(int ticks=24)
        {
            // Let the normal game update run. DoSingleTick while paused leaves the pawn
            // draw-position cache frozen even though AI reaches its destination.
            Find.TickManager.CurTimeSpeed=ticks==6?TimeSpeed.Normal:TimeSpeed.Superfast;
            ConstructionObserve();
            if(Find.TickManager.TicksGame-constructionStart>60000)throw new TimeoutException("natural construction exceeded one day; see construction-progress.txt");
            if(Find.TickManager.TicksGame-constructionLastReport>=300)
            {
                constructionLastReport=Find.TickManager.TicksGame;
                File.WriteAllText(Path.Combine(dir,"construction-progress.txt"),"phase="+phase+" elapsedTicks="+(constructionLastReport-constructionStart)
                    +"\n"+string.Join("\n",constructionWorkers.Select(p=>p.LabelShort+" "+p.Position+" "+p.CurJob))
                    +"\nframes="+string.Join(",",constructionFrames)+"\njobs="+string.Join(",",constructionJobs)
                    +"\nremaining="+string.Join(",",map.listerThings.AllThings.Where(t=>t is Blueprint_Build||t is Frame).Select(t=>t.def.defName+"@"+t.Position)));
                if(constructionGenerator!=null)
                    File.AppendAllText(Path.Combine(dir,"power-observations.txt"),"phase="+phase+" tick="+constructionLastReport
                        +" generator="+constructionGenerator.GetComp<CompPowerTrader>().PowerOn+" output="+constructionGenerator.GetComp<CompPowerTrader>().PowerOutput
                        +" lights="+string.Join(",",furnishingLamps.Select(l=>l.Mode+"/switch="+l.GetComp<CompFlickable>().SwitchIsOn+"/power="+l.GetComp<CompPowerTrader>().PowerOn))+"\n");
            }
        }
        private static bool ConstructionElapsed(int ticks) => Find.TickManager.TicksGame-constructionStageTick>=ticks;
        private static void ConstructionStage(int next) {phase=next;constructionStageTick=Find.TickManager.TicksGame;}
        private static void ConstructionGather()
        {
            foreach(var p in studioFamily)FixReady(p);
            studioJob=(LordJob_MFGroupPhoto)GroupPhoto.StartPrepared(map,studioBackdrop.Position,studioFamily,Rot4.North,
                faces:studioFamily.ToDictionary(p=>p.GetUniqueLoadID(),p=>0))?.LordJob;
            StudioCheck(studioJob!=null,"naturally built studio starts production gathering");
        }
        private static void StudioConstructionStep()
        {
            switch(phase)
            {
                case 900:
                    deadline=Time.realtimeSinceStartup+360f;MomentFramesMod.Settings.pxPerCell=96;
                    constructionStart=Find.TickManager.TicksGame;constructionLastReport=constructionStart;
                    constructionJobs.Clear();constructionFrames.Clear();studioBackdrop.ClearFrame();
                    foreach(var p in map.mapPawns.FreeColonistsSpawned.ToList())
                    {FixReady(p);p.jobs.StopAll();p.Position=center+new IntVec3(0,0,-10);p.Notify_Teleported();}
                    constructionWorkers=map.mapPawns.FreeColonistsSpawned.Where(p=>p.DevelopmentalStage.Adult()&&!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)&&!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)).Take(2).ToList();
                    StudioCheck(constructionWorkers.Count>=1,"fixture has an able constructor and hauler");
                    foreach(var p in constructionWorkers)
                    {
                        p.workSettings.EnableAndInitialize();
                        foreach(var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                            if(!p.WorkTypeIsDisabled(work))p.workSettings.SetPriority(work,0);
                        p.workSettings.SetPriority(WorkTypeDefOf.Construction,1);p.workSettings.SetPriority(WorkTypeDefOf.Hauling,2);
                        p.skills.GetSkill(SkillDefOf.Construction).Level=20;
                        for(int hour=0;hour<24;hour++)p.timetable.SetAssignment(hour,TimeAssignmentDefOf.Work);
                    }
                    studioFamily=map.mapPawns.FreeColonistsSpawned.Where(p=>p.DevelopmentalStage.Adult()).Take(2).Concat(new[]{studioChild}).ToList();
                    foreach(var p in map.mapPawns.FreeColonistsSpawned.Except(constructionWorkers))
                        if(!p.DevelopmentalStage.Baby())p.drafter.Drafted=true;
                    var room=CellRect.FromLimits(studioBackdrop.Position.x-8,center.z-6,studioBackdrop.Position.x+8,center.z+5);
                    foreach(var c in room)map.roofGrid.SetRoof(c,null);
                    foreach(var c in room)
                    {
                        foreach(var t in c.GetThingList(map).ToList())if(t is Building&&!(t is Building_PhotoStudio))t.Destroy();
                        map.terrainGrid.SetTerrain(c,TerrainDef.Named("WoodPlankFloor"));map.areaManager.Home[c]=true;map.areaManager.NoRoof[c]=false;
                    }
                    foreach(var c in room.EdgeCells)
                    {
                        var wall=ThingMaker.MakeThing(c.x==studioBackdrop.Position.x&&c.z==room.minZ?ThingDefOf.Door:ThingDefOf.Wall,ThingDefOf.WoodLog);
                        wall.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(wall,c,map);
                    }
                    PreparationRefreshRegions();foreach(var c in room)map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);PreparationRefreshRegions();
                    // Exact supplies for the targets (wood has additional generator fuel). No unrelated construction.
                    foreach(var t in map.listerThings.AllThings.Where(t=>t.def==ThingDefOf.Steel||t.def==ThingDefOf.WoodLog||t.def==ThingDefOf.ComponentIndustrial||t.def==ThingDefOf.Cloth).ToList())t.Destroy();
                    constructionPlans=studioBackdrop.Furniture(0);
                    StudioCheck(studioBackdrop.PlaceSet(0),"production set command places ordinary sofa and light blueprints");
                    StudioCheck(constructionPlans.All(p=>p.cell.GetThingList(map).OfType<Blueprint_Build>().Any(b=>b.def.entityDefToBuild==p.def)),"all three targets begin as blueprints");
                    constructionGeneratorCell=studioBackdrop.Position+new IntVec3(0,0,8);
                    var generatorDef=ThingDef.Named("WoodFiredGenerator");
                    StudioCheck(GenConstruct.CanPlaceBlueprintAt(generatorDef,constructionGeneratorCell,Rot4.North,map).Accepted,"generator blueprint has valid open ground");
                    GenConstruct.PlaceBlueprintForBuild(generatorDef,constructionGeneratorCell,map,Rot4.North,Faction.OfPlayer,null);
                    constructionConduits=0;
                    // One continuous line from the generator, ending within vanilla connection range of both lamps.
                    for(int z=7;z>=-1;z--)
                    {
                        var cell=studioBackdrop.Position+new IntVec3(0,0,z);var def=ThingDef.Named("PowerConduit");
                        StudioCheck(GenConstruct.CanPlaceBlueprintAt(def,cell,Rot4.North,map).Accepted,"conduit blueprint valid at "+z);
                        GenConstruct.PlaceBlueprintForBuild(def,cell,map,Rot4.North,Faction.OfPlayer,null);constructionConduits++;
                    }
                    var supply=studioBackdrop.Position+new IntVec3(-4,0,-9);
                    ConstructionMaterials(ThingDefOf.WoodLog,185,supply);ConstructionMaterials(ThingDefOf.Cloth,45,supply+IntVec3.East*2);
                    ConstructionMaterials(ThingDefOf.Steel,170+constructionConduits,supply+IntVec3.East*4);ConstructionMaterials(ThingDefOf.ComponentIndustrial,4,supply+IntVec3.East*6);
                    Find.CameraDriver.SetRootPosAndSize(studioBackdrop.DrawPos+new Vector3(0,0,1),13f);
                    CloseLog();Shot("construction-blueprints.png");ConstructionStage(901);return;
                case 901:
                    ConstructionAdvance();
                    if(!constructionHaulShot&&constructionWorkers.Any(p=>p.carryTracker.CarriedThing!=null))
                    {constructionHaulShot=true;Shot("construction-hauling.png");}
                    if(!constructionWorkShot&&constructionWorkers.Any(p=>p.CurJobDef==JobDefOf.FinishFrame))
                    {constructionWorkShot=true;Shot("construction-working.png");}
                    furnishingSeat=map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSeat>().FirstOrDefault();
                    furnishingLamps=studioBackdrop.Lamps().ToList();
                    constructionGenerator=constructionGeneratorCell.GetThingList(map).OfType<Building>().FirstOrDefault(b=>b.def.defName=="WoodFiredGenerator");
                    if(furnishingSeat==null||furnishingLamps.Count!=2||constructionGenerator==null||constructionGenerator.GetComp<CompRefuelable>().Fuel<=0)return;
                    if(map.listerThings.AllThings.OfType<Building>().Count(b=>b.def.defName=="PowerConduit")!=constructionConduits)return;
                    if(furnishingLamps.Any(l=>l.GetComp<CompPowerTrader>().PowerNet!=constructionGenerator.GetComp<CompPowerTrader>().PowerNet)||constructionGenerator.GetComp<CompPowerTrader>().PowerNet==null)return;
                    StudioCheck(new[]{"MF_StudioSofa","MF_StudioLamp","WoodFiredGenerator","PowerConduit"}.All(constructionFrames.Contains),"AI created frames for furniture, lights, generator and wiring");
                    StudioCheck(new[]{"MF_StudioSofa","MF_StudioLamp","WoodFiredGenerator","PowerConduit"}.All(n=>constructionJobs.Contains("FinishFrame:"+n)),"colonists performed FinishFrame work on all target kinds");
                    StudioCheck(constructionJobs.Any(j=>j.StartsWith("HaulToContainer:MF_Studio")),"colonists hauled actual materials to studio construction");
                    StudioCheck(constructionJobs.Contains("Refuel:WoodFiredGenerator"),"colonist carried wood and refuelled generator");
                    StudioCheck(ConstructionResource(ThingDefOf.Cloth)==0&&ConstructionResource(ThingDefOf.Steel)==0&&ConstructionResource(ThingDefOf.ComponentIndustrial)==0,"all listed cloth, steel and components consumed by construction");
                    StudioCheck(ConstructionResource(ThingDefOf.WoodLog)<150,"sofa wood and generator fuel both consumed");
                    StudioCheck(furnishingLamps.All(l=>!l.Lit),"automatic lamps off after real construction before gathering");
                    StudioCheck(constructionGenerator.GetComp<CompPowerTrader>().PowerOutput>0,"fuelled generator produces real power");
                    File.WriteAllText(Path.Combine(dir,"construction-evidence.txt"),"constructionTicks="+(Find.TickManager.TicksGame-constructionStart)+"\nworkers="+constructionWorkers.Count+"\nfuel="+constructionGenerator.GetComp<CompRefuelable>().Fuel+"\nwoodRemaining="+ConstructionResource(ThingDefOf.WoodLog)+"\nframes="+string.Join(",",constructionFrames)+"\njobs="+string.Join(",",constructionJobs));
                    Shot("construction-completed.png");ConstructionGather();ConstructionStage(902);return;
                case 902:
                    ConstructionAdvance(6);
                    if(studioJob.Finished&&studioJob.Takes==0)throw new InvalidOperationException("gather failed: "+studioJob.Result);
                    if(studioJob.Takes==0)return;
                    StudioCheck(furnishingLamps.All(l=>l.Lit),"automatic gathering powers both lights through the real network");
                    StudioCheck(studioJob.Members.All(p=>Mathf.Abs(p.DrawPos.x-p.Position.x-.5f)<.7f&&Mathf.Abs(p.DrawPos.z-p.Position.z-.5f)<.7f),"rendered participants reach the photographed seats");
                    StudioCaptureCopy("construction-family-lit.png");
                    constructionGenerator.GetComp<CompFlickable>().SwitchIsOn=false;ConstructionStage(903);return;
                case 903:
                    ConstructionAdvance();if(!ConstructionElapsed(300))return;
                    StudioCheck(furnishingLamps.All(l=>!l.Lit),"generator switched off removes actual network power from both lamps");
                    furnishingExpectedTakes=studioJob.Takes+1;studioJob.ShootNow();ConstructionStage(904);return;
                case 904:
                    if(studioJob.Takes<furnishingExpectedTakes){if(!studioJob.Shooting)ConstructionAdvance(6);return;}
                    StudioCaptureCopy("construction-family-no-power.png");
                    constructionGenerator.GetComp<CompFlickable>().SwitchIsOn=true;ConstructionStage(905);return;
                case 905:
                    // Vanilla PowerNet brings consumers back in random batches, not all at the same tick.
                    ConstructionAdvance();if(!furnishingLamps.All(l=>l.Lit)&&!ConstructionElapsed(1800))return;
                    StudioCheck(furnishingLamps.All(l=>l.Lit),"automatic lamps recover after generator power returns");
                    File.AppendAllText(Path.Combine(dir,"construction-evidence.txt"),"\nrecoveryTicks="+(Find.TickManager.TicksGame-constructionStageTick));
                    furnishingLamps[0].SetMode(2);ConstructionStage(906);return;
                case 906:
                    ConstructionAdvance();if(!ConstructionElapsed(120))return;
                    StudioCheck(!furnishingLamps[0].Lit&&furnishingLamps[1].Lit,"manual off overrides powered active gathering");
                    furnishingLamps[0].SetMode(1);studioJob.Cancel();ConstructionStage(907);return;
                case 907:
                    ConstructionAdvance();if(!furnishingLamps[0].Lit&&!ConstructionElapsed(1800))return;
                    StudioCheck(furnishingLamps[0].Lit&&!furnishingLamps[1].Lit,"dismissal switches automatic light off while manual on stays lit");
                    furnishingLamps[0].SetMode(0);ConstructionGather();ConstructionStage(908);return;
                case 908:
                    ConstructionAdvance(6);if(studioJob.Takes==0)return;
                    GameDataSaveLoader.SaveGame("mf-construction-powered");Wait(.5f);phase=909;return;
                case 909:
                    GameDataSaveLoader.LoadGame("mf-construction-powered");Wait(.5f);phase=910;return;
                case 910:
                    map=Find.CurrentMap;studioBackdrop=map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoStudio>().Single(s=>s.HasCanvas);
                    furnishingLamps=studioBackdrop.Lamps().ToList();studioJob=GroupPhoto.ActiveJob(map,studioBackdrop.Position);
                    constructionGenerator=map.listerThings.AllThings.OfType<Building>().Single(b=>b.def.defName=="WoodFiredGenerator");
                    constructionWorkers=map.mapPawns.FreeColonistsSpawned.Where(p=>p.DevelopmentalStage.Adult()).ToList();ConstructionStage(911);return;
                case 911:
                    ConstructionAdvance();if(!furnishingLamps.All(l=>l.Lit)&&!ConstructionElapsed(1800))return;
                    StudioCheck(studioJob!=null&&!studioJob.Finished&&furnishingLamps.Count==2&&furnishingLamps.All(l=>l.Lit),"real save/load restores gathering, automatic lights and power network");
                    studioJob.Cancel();ConstructionStage(912);return;
                case 912:
                    ConstructionAdvance();if(!ConstructionElapsed(120))return;
                    StudioCheck(furnishingLamps.All(l=>!l.Lit),"loaded gathering cancellation releases both powered lights");
                    StudioCheck(!NativePhotoOrder.Active,"native capture ordering scope restored");
                    StudioCheck(!RuntimeAudio.Waylith || NativePhotoOrder.SortCount>=3,"1.7 native draw ordering exercised on each photo");
                    if(GenCommandLine.TryGetCommandLineArg("momentFramesStudioMatrix",out _)){phase=950;return;}
                    File.WriteAllText(Path.Combine(dir,"runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                    File.WriteAllText(Path.Combine(dir,"studio-result.json"),"{\"ok\":true,\"runtime\":"+Json(VersionControl.CurrentVersionString)+",\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                    active=false;Application.Quit();return;
            }
        }
    }
}
