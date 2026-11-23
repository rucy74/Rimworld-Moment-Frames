using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static Job octoberPriorJob;
        private static Thing octoberReservation;
        private static Thing octoberWeapon;

        private static void OctoberBubbleChecks()
        {
            var anchors = new[] { new Vector2(125,150), new Vector2(209,150), new Vector2(252,150) };
            var boxes = PhotoReactions.BubbleLayout(anchors,48,400,300);
            OctoberCheck(boxes.Count == 3 && boxes.Select((b,i) => (b.center-anchors[i]).sqrMagnitude < .01f).All(x=>x),
                "close reactions remain centered above their own subjects without a vertical jump");
            OctoberCheck(!boxes[1].Overlaps(boxes[2]) && boxes[1].width < 48 && boxes[0].width == 48,
                "only nearby reaction bubbles shrink; separated bubble keeps its size");
            var reverse = PhotoReactions.BubbleLayout(anchors.Reverse().ToArray(),48,400,300);
            OctoberCheck(boxes.Select((b,i) => b == reverse[2-i]).All(x=>x),"reaction layout is independent of participant enumeration order");
            foreach(var name in new[]{"MF_StudioSofa","MF_StudioBench","MF_StudioChair"})
                OctoberCheck(ThingDef.Named(name).label.Contains("소품") || ThingDef.Named(name).label.Contains("prop"),name+" visibly identifies itself as a prop");
        }

        private static void OctoberPreparePreviousJob()
        {
            var pawn=octoberPeople[0];octoberWeapon=pawn.equipment?.Primary;
            octoberReservation=ThingMaker.MakeThing(ThingDefOf.WoodLog);
            GenSpawn.Spawn(octoberReservation,center+new IntVec3(7,0,3),map);
            var definition=new JobDef{defName="MFProbePreviousWork",driverClass=typeof(JobDriver_MFProbePreviousWork)};
            octoberPriorJob=JobMaker.MakeJob(definition,octoberReservation);
            JobDriver_MFProbePreviousWork.CleanedUp=false;
            pawn.jobs.StartJob(octoberPriorJob,JobCondition.InterruptForced);
            OctoberCheck(pawn.CurJob==octoberPriorJob && map.reservationManager.ReservedBy(octoberReservation,pawn,octoberPriorJob),
                "previous-job fixture started a real job and reserved its work target");
        }

        private static void OctoberCheckPreviousJob()
        {
            var pawn=octoberPeople[0];
            OctoberCheck(JobDriver_MFProbePreviousWork.CleanedUp && pawn.CurJob!=octoberPriorJob,
                "normal photo summons runs the prior job's finish cleanup before posing");
            OctoberCheck(!map.reservationManager.ReservedBy(octoberReservation,pawn,octoberPriorJob),
                "normal photo summons releases prior work reservations");
            OctoberCheck(pawn.equipment?.Primary==octoberWeapon,"normal job cleanup preserves equipped items");
            octoberReservation.Destroy();
        }

        private static void OctoberBackdropAndLightChecks()
        {
            var lamp=(Building_PhotoStudioLamp)ThingMaker.MakeThing(ThingDef.Named("MF_StudioLamp"));
            lamp.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(lamp,center+new IntVec3(4,0,-2),map);
            var flick=lamp.GetComp<CompFlickable>();var power=lamp.GetComp<CompPowerTrader>();
            lamp.SetMode(1);power.PowerOn=true;lamp.Acquire(octoberJob);lamp.Release(octoberJob);
            OctoberCheck(lamp.Lit,"always-on light stays on after gathering release with supplied power");
            lamp.SetMode(0);OctoberCheck(!flick.SwitchIsOn,"automatic light switches off after gathering release");
            lamp.SetMode(1);power.PowerOn=false;OctoberCheck(!lamp.Lit,"always-on light still requires power");
            lamp.Destroy();

            string input=Path.Combine(dir,"studio-mode-2.png");byte[] before=File.ReadAllBytes(input);
            var image=StudioBackgrounds.Load(input);
            string file=StudioBackgrounds.Import(image);octoberStudio.SetPhotoBackground(file,1,.5f,.5f);
            OctoberCheck(File.Exists(StudioBackgrounds.PathFor(file)) && octoberStudio.BackgroundFile==file,
                "external PNG imports to the studio background folder and applies to the studio");
            OctoberCheck(before.SequenceEqual(File.ReadAllBytes(input)),"background import leaves the original image unchanged");
            octoberStudio.SetBackdrop(1);UnityEngine.Object.Destroy(image);
        }
    }

    // Disposable test job: exercise the public job lifecycle, not a photo-specific
    // imitation of cleanup. No tool-mod API is involved in these assertions.
    public sealed class JobDriver_MFProbePreviousWork : JobDriver
    {
        internal static bool CleanedUp;
        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA,job,1,-1,null,errorOnFailed);
        protected override IEnumerable<Toil> MakeNewToils()
        {
            var wait=Toils_General.Wait(9999);wait.AddFinishAction(()=>CleanedUp=true);yield return wait;
        }
    }
}
