using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    internal static class BirthdayCarry
    {
        internal static Pawn Holder(Pawn baby) => (baby?.ParentHolder as Pawn_CarryTracker)?.pawn;
        internal static bool Available(Pawn p) => p != null && (p.Spawned || Holder(p)?.Spawned == true) && p.MapHeld != null;
        internal static bool CanCarry(Pawn adult, Pawn baby, LordJob_MFGroupPhoto editing = null)
        {
            if (baby == null || !baby.DevelopmentalStage.Baby() || baby.Dead || !Available(baby) || adult == null
                || !adult.DevelopmentalStage.Adult() || !adult.Spawned || adult.Map != baby.MapHeld
                || adult.Dead || adult.Downed || adult.Drafted || adult.InMentalState
                || !adult.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return false;
            if (!PhotoGatheringPlan.CanJoin(adult, editing)) return false;
            if (adult.carryTracker.CarriedThing == baby) return true;
            return adult.carryTracker.CarriedThing == null && baby.Spawned && !baby.IsForbidden(adult)
                && adult.CanReserveAndReach(baby, PathEndMode.ClosestTouch, Danger.Some);
        }
        internal static Pawn Recommend(Pawn baby, LordJob_MFGroupPhoto editing, IEnumerable<Pawn> unavailable = null, IEnumerable<Pawn> candidates = null) =>
            (candidates ?? baby?.MapHeld?.mapPawns.FreeColonistsSpawned ?? Enumerable.Empty<Pawn>())
            .Where(p => (unavailable == null || !unavailable.Contains(p)) && CanCarry(p, baby, editing))
            .OrderByDescending(p => Holder(baby) == p).ThenByDescending(p => p.relations?.FamilyByBlood.Contains(baby) == true)
            .ThenBy(p => p.Position.DistanceToSquared(baby.PositionHeld)).ThenBy(p => p.thingIDNumber).FirstOrDefault();
        internal static Dictionary<Pawn,Pawn> RecommendDistinct(IEnumerable<Pawn> babies, LordJob_MFGroupPhoto editing, System.Random random = null)
        {
            var result=new Dictionary<Pawn,Pawn>();var owners=new Dictionary<Pawn,Pawn>();var ordered=babies.Distinct().OrderBy(p=>p.thingIDNumber).ToList();
            if(random!=null) for(int i=0;i<ordered.Count;i++)
            {int at=random.Next(i,ordered.Count);var baby=ordered[at];ordered[at]=ordered[i];ordered[i]=baby;}
            bool Assign(Pawn baby,HashSet<Pawn> tried)
            {
                var candidates=baby.MapHeld.mapPawns.FreeColonistsSpawned.Where(p=>CanCarry(p,baby,editing))
                    .OrderByDescending(p=>Holder(baby)==p).ThenByDescending(p=>p.relations?.FamilyByBlood.Contains(baby)==true)
                    .ThenBy(p=>p.Position.DistanceToSquared(baby.PositionHeld)).ThenBy(p=>p.thingIDNumber);
                foreach(var adult in candidates)
                {
                    if(!tried.Add(adult)) continue;
                    if(owners.TryGetValue(adult,out var other) && !Assign(other,tried)) continue;
                    owners[adult]=baby;result[baby]=adult;return true;
                }
                return false;
            }
            foreach(var baby in ordered) Assign(baby,new HashSet<Pawn>());
            return result;
        }
    }

    public class JobGiver_MFCarryBaby : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            var gathering = pawn.GetLord()?.LordJob as LordJob_MFGroupPhoto;
            var baby = gathering?.BabyForCarrier(pawn);
            if (gathering == null || gathering.Finished || baby == null) return null;
            var def = DefDatabase<JobDef>.GetNamedSilentFail("MF_CarryBabyForPhoto");
            if (def == null) return null;
            var job = JobMaker.MakeJob(def, baby, gathering.CellFor(pawn));
            job.count = 1; job.lord = gathering.lord;
            return job;
        }
    }

    /// <summary>Native pickup, walk and hold, followed by the game's safe-place/bed routine. Persists through lord cleanup to finish childcare.</summary>
    public class JobDriver_MFCarryBaby : JobDriver
    {
        private Pawn Baby => job.targetA.Pawn;
        private LordJob_MFGroupPhoto Gathering => job.lord?.LordJob as LordJob_MFGroupPhoto;
        private bool LeftGathering => Gathering == null || Gathering.Finished || pawn.GetLord() != job.lord || Gathering.BabyForCarrier(pawn) != Baby;
        private bool returning;
        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.IsCarryingPawn(Baby)
            || JobDriver_PickupToHold.TryMakePreToilReservations(this, errorOnFailed);
        public override void ExposeData() { base.ExposeData(); Scribe_Values.Look(ref returning, "returningBaby", false); }
        protected override IEnumerable<Toil> MakeNewToils()
        {
            // An interrupted pickup/pose must not leave guests waiting for a baby that cannot arrive.
            AddFinishAction(condition => { if (!returning && Gathering?.Finished == false && Gathering.BabyForCarrier(pawn) == Baby) Gathering.Cancel(); });
            this.FailOnDestroyedNullOrForbidden(TargetIndex.A);
            AddFailCondition(() => Baby.Dead || pawn.Downed || pawn.Drafted || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation));
            var returnHome = ToilMaker.MakeToil("MFReturnBaby");
            returnHome.initAction = () =>
            {
                returning = true;
                if (Gathering?.Finished == false && Gathering.BabyForCarrier(pawn) == Baby) Gathering.Cancel();
                // The gathering can already have been removed from the map. Do not save a dangling lord reference on the return trip.
                job.lord = null;
                if (!pawn.IsCarryingPawn(Baby)) { EndJobWith(JobCondition.Succeeded); return; }
                LocalTargetInfo safe = ChildcareUtility.SafePlaceForBaby(Baby, pawn);
                if (!safe.IsValid) { EndJobWith(JobCondition.Incompletable); return; }
                job.SetTarget(TargetIndex.C, safe);
            };
            returnHome.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return Toils_Jump.JumpIf(returnHome, () => LeftGathering);
            foreach (var toil in JobDriver_PickupToHold.Toils(this)) yield return toil.JumpIf(() => LeftGathering, returnHome);
            var goPose = Toils_Goto.Goto(TargetIndex.B, PathEndMode.OnCell);
            yield return goPose.JumpIf(() => LeftGathering, returnHome).FailOn(() => !pawn.IsCarryingPawn(Baby));
            var pose = ToilMaker.MakeToil("MFBabyPose");
            pose.initAction = () => pawn.pather.StopDead();
            pose.tickIntervalAction = delta =>
            {
                if (LeftGathering) { JumpToToil(returnHome); return; }
                if (!pawn.IsCarryingPawn(Baby)) { EndJobWith(JobCondition.Incompletable); return; }
                pawn.Rotation = Gathering.PoseFacing;
                IntVec3 cell = Gathering.CellFor(pawn);
                if (pawn.Position != cell) { job.SetTarget(TargetIndex.B, cell); JumpToToil(goPose); return; }
                if (!Baby.ComfortableTemperatureAtCell(pawn.Position, pawn.Map) || Baby.needs.food?.CurCategory == HungerCategory.Starving)
                { Gathering.Cancel(); JumpToToil(returnHome); }
            };
            pose.handlingFacing = true; pose.socialMode = RandomSocialMode.Off;
            pose.defaultCompleteMode = ToilCompleteMode.Never;
            yield return pose;
            yield return returnHome;
            yield return Toils_Reserve.ReserveDestinationOrThing(TargetIndex.C);
            yield return Toils_Goto.Goto(TargetIndex.C, PathEndMode.OnCell).FailOnInvalidOrDestroyed(TargetIndex.C)
                .FailOnForbidden(TargetIndex.C).FailOn(() => !pawn.IsCarryingPawn(Baby));
            yield return Toils_Reserve.ReleaseDestinationOrThing(TargetIndex.C);
            yield return Toils_Bed.TuckIntoBed(TargetIndex.C, TargetIndex.A);
        }
    }
}
