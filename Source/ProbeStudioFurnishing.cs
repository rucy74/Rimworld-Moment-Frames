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
        private static List<Building_PhotoStudioLamp> furnishingLamps;
        private static Building_PhotoSeat furnishingSeat;
        private static Vector3? furnishingAlignedOffset;
        private static int furnishingExpectedTakes;
        private static void FurnishingDetail(string name)
        {
            StudioCheck(PhotoCapture.Capture(map, furnishingSeat.DrawPos + new Vector3(0,0,.35f), 5f, result =>
            {
                if(result?.png==null) throw new InvalidOperationException("seat detail capture returned no PNG");
                File.WriteAllBytes(Path.Combine(dir,name),result.png);
            }, 1.5f, realLight:true), "actual game close-up requested: "+name);
        }
        private static void FurnishingPower(bool on)
        {
            // This fixture supplies power explicitly; it does not claim to simulate a player power network.
            foreach(var lamp in furnishingLamps) lamp.GetComp<CompPowerTrader>().PowerOn=on && lamp.GetComp<CompFlickable>().SwitchIsOn;
        }
        private static void FurnishingGather()
        {
            foreach(var p in studioFamily)FixReady(p);
            studioJob=(LordJob_MFGroupPhoto)GroupPhoto.StartPrepared(map,studioBackdrop.Position,studioFamily,Rot4.North,
                faces:studioFamily.ToDictionary(p=>p.GetUniqueLoadID(),p=>0))?.LordJob;
            StudioCheck(studioJob!=null,"furnished studio starts through production gathering");studioTicks=0;
            Find.TickManager.DoSingleTick();FurnishingPower(true);
        }
        private static void StudioFurnishingStep()
        {
            switch(phase)
            {
                case 800:
                    deadline=Time.realtimeSinceStartup+300f;
                    MomentFramesMod.Settings.pxPerCell=96;
                    studioBackdrop.ClearFrame();
                    studioFamily=map.mapPawns.FreeColonistsSpawned.Where(p=>!p.DevelopmentalStage.Baby()).OrderBy(p=>p.ageTracker.AgeBiologicalYears).Take(3).ToList();
                    foreach(var j in map.lordManager.lords.Select(l=>l.LordJob).OfType<LordJob_MFGroupPhoto>().ToList())j.Cancel();
                    foreach(var pawn in map.mapPawns.FreeColonistsSpawned.ToList())
                    {
                        pawn.jobs?.StopAll();
                        if(pawn.carryTracker?.CarriedThing!=null)pawn.carryTracker.TryDropCarriedThing(center+new IntVec3(0,0,-16),ThingPlaceMode.Direct,out var dropped);
                    }
                    foreach(var pawn in map.mapPawns.FreeColonistsSpawned.Where(p=>!studioFamily.Contains(p)).ToList())
                    {
                        pawn.jobs?.StopAll();pawn.Position=center+new IntVec3(0,0,-16);pawn.Notify_Teleported();
                        if(!pawn.DevelopmentalStage.Baby())pawn.drafter.Drafted=true;
                    }
                    // Extend the disposable room so both side lights fit inside it.
                    var room=CellRect.FromLimits(studioBackdrop.Position.x-8,center.z-6,studioBackdrop.Position.x+8,center.z+5);
                    foreach(var c in room)map.roofGrid.SetRoof(c,null);
                    foreach(var c in room)
                    {
                        foreach(var t in c.GetThingList(map).ToList())if(t is Building && !(t is Building_PhotoStudio))t.Destroy();
                        map.areaManager.NoRoof[c]=false;map.terrainGrid.SetTerrain(c,TerrainDef.Named("WoodPlankFloor"));
                    }
                    foreach(var c in room.EdgeCells)
                    {
                        bool door=c.x==studioBackdrop.Position.x && c.z==room.minZ;
                        var wall=ThingMaker.MakeThing(door?ThingDefOf.Door:ThingDefOf.Wall,ThingDefOf.WoodLog);wall.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(wall,c,map);
                    }
                    PreparationRefreshRegions();
                    foreach(var c in room)map.roofGrid.SetRoof(c,RoofDefOf.RoofConstructed);
                    PreparationRefreshRegions();
                    StudioCheck(studioBackdrop.CanPlaceSet(0).Accepted,"sofa and both lights fit ordinary blueprint placement over backdrop");
                    StudioCheck(studioBackdrop.PlaceSet(0),"set creates material-cost blueprints");
                    var blueprints=map.listerThings.AllThings.OfType<Blueprint_Build>().Where(b=>b.def.entityDefToBuild?.defName.StartsWith("MF_Studio")==true).ToList();
                    StudioCheck(blueprints.Count==3,"set has exactly three blueprints");studioBackdrop.PlaceSet(0);
                    StudioCheck(map.listerThings.AllThings.OfType<Blueprint_Build>().Count(b=>b.def.entityDefToBuild?.defName.StartsWith("MF_Studio")==true)==3,"placing the same set twice does not duplicate blueprints");
                    foreach(var b in blueprints)
                    {
                        var def=(ThingDef)b.def.entityDefToBuild;var c=b.Position;var rot=b.Rotation;b.Destroy();
                        StudioCheck(!GenSpawn.SpawningWipes(def,studioBackdrop.def),"furniture cannot wipe backdrop: "+def.defName);
                        var built=ThingMaker.MakeThing(def);built.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(built,c,map,rot);
                    }
                    furnishingSeat=map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSeat>().Single();
                    furnishingLamps=studioBackdrop.Lamps().ToList();FurnishingPower(true);
                    StudioCheck(furnishingLamps.Count==2 && furnishingLamps.All(l=>!l.Lit),"automatic lights remain off before gathering");
                    StudioCheck(!studioBackdrop.CanPlaceSet(1).Accepted,"different furniture cannot replace an existing sofa silently");
                    var seats=studioBackdrop.SeatCells();StudioCheck(seats.Count==3,"three sofa places discovered");
                    var cells=Building_PhotoStudio.Formation(map,studioBackdrop.Position,Rot4.North,8);
                    StudioCheck(cells.Count==8&&cells.Distinct().Count()==8&&seats.All(c=>cells.Take(3).Contains(c)),"sofa first, then five distinct standing places");
                    foreach(var name in new[]{"MF_StudioSofa","MF_StudioBench","MF_StudioChair"})
                    {
                        var art=ThingDef.Named(name).graphicData;
                        var north=art.DrawOffsetForRot(Rot4.North);
                        StudioCheck(north.z<0 && north.y==0,"seat art moves forward without changing draw depth: "+name);
                        foreach(var rotation in new[]{Rot4.East,Rot4.South,Rot4.West})
                            StudioCheck((art.DrawOffsetForRot(rotation)-north.RotatedBy(rotation.AsAngle)).sqrMagnitude<.00001f,"seat artwork offset rotates with furniture: "+name+" "+rotation);
                    }
                    furnishingAlignedOffset=furnishingSeat.def.graphicData.drawOffsetNorth;
                    // Same people, frame and lighting: temporarily restore the old artwork position for a true before photo.
                    furnishingSeat.def.graphicData.drawOffsetNorth=Vector3.zero;
                    Find.CameraDriver.SetRootPosAndSize(studioBackdrop.DrawPos,9f);
                    Find.WindowStack.Add(new Dialog_StudioFurnishing(studioBackdrop));Wait(.6f);phase=801;return;
                case 801:
                    Shot("furnishing-menu.png");Wait(.5f);phase=802;return;
                case 802:
                    Find.WindowStack.TryRemove(typeof(Dialog_StudioFurnishing),false);
                    FurnishingGather();StudioCheck(furnishingLamps.All(l=>l.Lit),"automatic lights turn on during gathering");
                    furnishingLamps[0].SetMode(2);StudioCheck(!furnishingLamps[0].Lit,"off mode overrides active gathering");
                    furnishingLamps[0].SetMode(0);FurnishingPower(false);StudioCheck(furnishingLamps.All(l=>!l.Lit),"power loss extinguishes automatic lights");
                    FurnishingPower(true);phase=803;return;
                case 803:
                    FurnishingPower(true);if(!StudioAdvance())return;Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
                    StudioCaptureCopy("seating-family-before.png");
                    FurnishingDetail("seating-family-before-detail.png");phase=812;return;
                case 812:
                    if(PhotoCapture.Busy)return;
                    furnishingSeat.def.graphicData.drawOffsetNorth=furnishingAlignedOffset;
                    furnishingExpectedTakes=studioJob.Takes+1;studioJob.ShootNow();phase=813;return;
                case 813:
                    FurnishingPower(true);
                    if(studioJob.Takes<furnishingExpectedTakes){if(!studioJob.Shooting)Find.TickManager.DoSingleTick();return;}
                    StudioCaptureCopy("furnished-family.png");
                    FurnishingDetail("seating-family-after-detail.png");phase=814;return;
                case 814:
                    if(PhotoCapture.Busy)return;
                    StudioCheck(furnishingLamps.All(l=>l.Lit),"lights remain on during retake hold");
                    CloseLog();Wait(1.2f);phase=815;return;
                case 815:
                    Shot("furnished-family-room.png");Wait(.5f);phase=804;return;
                case 804:
                    foreach(var lamp in furnishingLamps)lamp.SetMode(2);
                    furnishingExpectedTakes=studioJob.Takes+1;studioJob.ShootNow();phase=811;return;
                case 811:
                    FurnishingPower(true);
                    if(studioJob.Takes<furnishingExpectedTakes){if(!studioJob.Shooting)Find.TickManager.DoSingleTick();return;}
                    StudioCaptureCopy("furnished-family-unlit.png");
                    foreach(var lamp in furnishingLamps)lamp.SetMode(0);
                    FurnishingPower(true);
                    furnishingLamps[0].SetMode(1);studioJob.Cancel();
                    StudioCheck(furnishingLamps[0].Lit&&!furnishingLamps[1].Lit,"dismissal releases automatic light but preserves always-on choice");
                    furnishingLamps[0].SetMode(0);studioBackdrop.SetBackdrop(1);
                    furnishingSeat.Destroy();furnishingSeat=(Building_PhotoSeat)ThingMaker.MakeThing(ThingDef.Named("MF_StudioBench"));furnishingSeat.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(furnishingSeat,studioBackdrop.Position+new IntVec3(0,0,-1),map);
                    for(int i=0;i<15;i++)Find.TickManager.DoSingleTick();FurnishingGather();phase=805;return;
                case 805:
                    FurnishingPower(true);if(!StudioAdvance())return;Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
                    StudioCaptureCopy("furnished-garden.png");studioJob.Cancel();studioBackdrop.SetBackdrop(2);
                    furnishingSeat.Destroy();furnishingSeat=(Building_PhotoSeat)ThingMaker.MakeThing(ThingDef.Named("MF_StudioChair"));furnishingSeat.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(furnishingSeat,studioBackdrop.Position+new IntVec3(0,0,-1),map);
                    for(int i=0;i<15;i++)Find.TickManager.DoSingleTick();FurnishingGather();phase=806;return;
                case 806:
                    FurnishingPower(true);if(!StudioAdvance())return;Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
                    StudioCaptureCopy("furnished-birthday.png");
                    GameDataSaveLoader.SaveGame("mf-furnished-active");Wait(.5f);phase=807;return;
                case 807:
                    GameDataSaveLoader.LoadGame("mf-furnished-active");Wait(.5f);phase=808;return;
                case 808:
                    map=Find.CurrentMap;studioBackdrop=map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoStudio>().Single(s=>s.HasCanvas);
                    furnishingLamps=studioBackdrop.Lamps().ToList();studioJob=GroupPhoto.ActiveJob(map,studioBackdrop.Position);
                    StudioCheck(studioBackdrop.BackdropStyle==2,"selected backdrop artwork survives real save/load");
                    StudioCheck(studioJob!=null&&!studioJob.Finished,"retake gathering survives real save/load");
                    Find.TickManager.DoSingleTick();FurnishingPower(true);
                    StudioCheck(furnishingLamps.Count==2&&furnishingLamps.All(l=>l.Lit),"loaded active gathering reacquires both lights");
                    studioJob.Cancel();StudioCheck(furnishingLamps.All(l=>!l.Lit),"loaded gathering releases lights on cancellation");
                    furnishingLamps[0].SetMode(1);furnishingLamps[1].SetMode(2);
                    GameDataSaveLoader.SaveGame("mf-furnished-modes");Wait(.5f);phase=809;return;
                case 809:
                    GameDataSaveLoader.LoadGame("mf-furnished-modes");Wait(.5f);phase=810;return;
                case 810:
                    map=Find.CurrentMap;furnishingLamps=map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoStudioLamp>().OrderBy(l=>l.thingIDNumber).ToList();
                    StudioCheck(furnishingLamps.Select(l=>l.Mode).OrderBy(x=>x).SequenceEqual(new[]{1,2}),"manual on and off choices survive real save/load");
                    File.WriteAllText(Path.Combine(dir,"runtime-log.txt"),string.Join("\n",Log.Messages.Select(m=>m.text)));
                    File.WriteAllText(Path.Combine(dir,"studio-result.json"),"{\"ok\":true,\"runtime\":"+Json(VersionControl.CurrentVersionString)+",\"facialAnimation\":"+(PhotoFaces.Available?"true":"false")+",\"checks\":["+string.Join(",",checks.Select(Json))+"]}");
                    active=false;Application.Quit();return;
            }
        }
    }
}
