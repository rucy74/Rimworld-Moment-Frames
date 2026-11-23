using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
        private static bool? playtestToolBodyRan;
        private static void PlaytestToolObserved(bool __runOriginal) => playtestToolBodyRan = __runOriginal;

        // Run after definitions and a test map are ready. With the two optional mods
        // loaded this tests their real startup output and patched draw entry point;
        // without them it checks the absence path, not compatibility coverage.
        private static void PlaytestStudioChecks()
        {
            var overlay = AccessTools.TypeByName("SenseOfDepthForTallBuilding.CompBackSideLayer");
            foreach (var name in new[] { "MF_StudioSofa", "MF_StudioBench", "MF_StudioChair" })
            {
                var def = ThingDef.Named(name);
                StudioCheck(!def.building.isSittable && !def.building.ai_chillDestination,
                    name + " remains a portrait prop, not an ordinary recreation seat");
                if (overlay != null)
                    StudioCheck(def.comps == null || !def.comps.Any(c => overlay.IsAssignableFrom(c.compClass)),
                        name + " has no tall-building overlay after Sense Of Depth startup");
            }
            StudioCheck(!PhotoPoseWorkTools.IsPhotoPose(null) && !PhotoPoseWorkTools.IsPhotoPose(new JobDriver_Wait()),
                "ordinary jobs do not suppress work tools");

            var animator = AccessTools.TypeByName("JobEffects.ToolAnimator");
            var draw = animator == null ? null : AccessTools.Method(animator, "OnPawnRendered", new[] { typeof(Pawn), typeof(Vector3) });
            if (draw == null)
            {
                StudioCheck(PhotoPoseWorkTools.IsPhotoPose(new JobDriver_MFPose()) && PhotoPoseWorkTools.IsPhotoPose(new JobDriver_MFCarryBaby()),
                    "photo posing policy works without the optional tool mod; actual tool compatibility not exercised");
                return;
            }
            var pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned.First();
            var oldDriver = pawn.jobs.curDriver;
            var oldJob = pawn.CurJob;
            var weapon = pawn.equipment?.Primary;
            var carried = pawn.carryTracker?.CarriedThing;
            var observer = AccessTools.Method(typeof(Probe), nameof(PlaytestToolObserved));
            var harmony = new Harmony("MomentFrames.Probe.PlaytestTools");
            try
            {
                harmony.Patch(draw, postfix: new HarmonyMethod(observer));
                foreach (var driver in new JobDriver[] { new JobDriver_MFPose(), new JobDriver_MFCarryBaby() })
                {
                    pawn.jobs.curDriver = driver;
                    playtestToolBodyRan = null;
                    draw.Invoke(null, new object[] { pawn, Vector3.zero });
                    StudioCheck(playtestToolBodyRan == false,
                        driver.GetType().Name + " skips the optional lingering work-tool draw");
                }
                pawn.jobs.curDriver = oldDriver;
                playtestToolBodyRan = null;
                // Null is an ordinary early-return in the real method. It proves our
                // prefix releases the draw path without asking that mod to draw a fixture.
                draw.Invoke(null, new object[] { null, Vector3.zero });
                StudioCheck(playtestToolBodyRan == true, "work-tool draw is not globally disabled");
                StudioCheck(pawn.CurJob == oldJob && pawn.equipment?.Primary == weapon && pawn.carryTracker?.CarriedThing == carried,
                    "tool suppression preserves job, equipment and carried subject");
            }
            finally
            {
                pawn.jobs.curDriver = oldDriver;
                harmony.Unpatch(draw, observer);
            }
        }

        private static void PlaytestLocationChecks()
        {
            var locationMap=Find.CurrentMap;
            var spot=locationMap.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSpot>().First();
            var locations=locationMap.GetComponent<PhotoLocationMapComponent>();
            string before=locations.CustomName(spot);var birthday=locations.BirthdaySpot;
            try
            {
                locations.Rename(spot,"  정원 가족사진 자리  ");
                StudioCheck(PhotoLocationMapComponent.DisplayName(spot)=="정원 가족사진 자리",
                    "place picker uses the user name rather than coordinates");
                StudioCheck(locations.BirthdaySpot==birthday,"renaming preserves the birthday default location");
                locations.Rename(spot,"");
                StudioCheck(!PhotoLocationMapComponent.DisplayName(spot).NullOrEmpty() && locations.CustomName(spot)=="",
                    "clearing a location name restores its furniture label");
            }
            finally { locations.Rename(spot,before); }

            var path=Path.Combine(dir,"photo-location-names.xml");
            PhotoLocationMapComponent fixture=new PhotoLocationMapComponent(locationMap);
            fixture.Rename(spot,"Family portrait garden");
            Scribe.saver.InitSaving(path,"PhotoLocationNames");Scribe_Deep.Look(ref fixture,"locations",locationMap);Scribe.saver.FinalizeSaving();
            fixture=null;Scribe.loader.InitLoading(path);Scribe_Deep.Look(ref fixture,"locations",locationMap);Scribe.loader.FinalizeLoading();
            StudioCheck(fixture.CustomName(spot)=="Family portrait garden","real Scribe round-trip preserves location names by building identity");
            var legacy=Path.Combine(dir,"photo-location-names-legacy.xml");
            var document=new XmlDocument();document.Load(path);
            var node=document.SelectSingleNode("/PhotoLocationNames/locations/mfPhotoLocationNames");
            StudioCheck(node!=null,"location-name dictionary is present in saved data");node.ParentNode.RemoveChild(node);document.Save(legacy);
            fixture=null;Scribe.loader.InitLoading(legacy);Scribe_Deep.Look(ref fixture,"locations",locationMap);Scribe.loader.FinalizeLoading();
            StudioCheck(fixture.CustomName(spot)=="","older saves without location names load with an empty name map");
        }
    }
}
