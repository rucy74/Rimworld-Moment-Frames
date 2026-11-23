using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>An explicitly chosen owned animal and colonist; ordinary group eligibility stays human-only.</summary>
    internal static class PetPhotos
    {
        internal const string Kind = "petphoto";
        internal const int GiveUpTicks = 5000;
        internal static string Caption(string kind, IList<Pawn> subjects, Map map, Pawn animal, string key)
        {
            int seed = 23;
            unchecked { foreach (char c in key ?? "") seed = seed * 31 + c; }
            Rand.PushState(seed);
            try { return PhotoDesc.Pick(kind, subjects, map, animal?.LabelShort); }
            finally { Rand.PopState(); }
        }
        internal static IEnumerable<Pawn> Animals(Map map) => map?.mapPawns.AllPawnsSpawned
            .Where(PetMilestones.OwnedAnimal)
            .OrderBy(p => p.LabelShort).ThenBy(p => p.thingIDNumber) ?? Enumerable.Empty<Pawn>();

        internal static Pawn DefaultOwner(Pawn animal)
        {
            var map = animal?.Map;
            if (map == null) return null;
            Pawn master = animal.playerSettings?.Master;
            if (master != null && master.Map == map && GroupPhoto.CanJoin(master)) return master;
            Pawn bonded = animal.relations?.DirectRelations.Where(r => r.def == PawnRelationDefOf.Bond)
                .Select(r => r.otherPawn).FirstOrDefault(p => p.Map == map && GroupPhoto.CanJoin(p));
            return bonded ?? map.mapPawns.FreeColonistsSpawned.Where(GroupPhoto.CanJoin).OrderBy(p => p.thingIDNumber).FirstOrDefault();
        }

        internal static bool EligibleAnimal(Pawn animal, LordJob_MFGroupPhoto current = null) => WhyNotAnimal(animal, current).NullOrEmpty();
        internal static string WhyNotAnimal(Pawn animal, LordJob_MFGroupPhoto current = null)
        {
            if (animal == null || animal.Dead) return "MF_Why_Dead".Translate();
            if (!animal.Spawned) return "MF_Why_OtherMap".Translate();
            if (!PetMilestones.OwnedAnimal(animal)) return "MF_Pet_Unavailable".Translate();
            if (!animal.Awake()) return "MF_Why_Asleep".Translate();
            if (animal.Downed) return "MF_Why_Downed".Translate();
            if (animal.InMentalState) return "MF_Why_Mental".Translate();
            if (animal.roping?.HasAnyRope == true) return "MF_Pet_Roped".Translate();
            if (animal.GetLord() != null && animal.GetLord()?.LordJob != current) return "MF_Why_Busy".Translate();
            if (GroupPhoto.Busy(animal) || (animal.needs?.food?.CurLevelPercentage ?? 1f) < .15f
                || (animal.needs?.rest?.CurLevelPercentage ?? 1f) < .15f || animal.health.hediffSet.BleedRateTotal > 0f
                || animal.mindState?.enemyTarget != null) return "MF_Why_Unfit".Translate();
            return "";
        }

        internal static bool CanContinue(LordJob_MFGroupPhoto job)
        {
            var animal = job.PetPawn; var owner = job.PetOwner;
            return animal != null && owner != null && job.lord != null && job.lord.ownedPawns.Count == 2
                && job.lord.ownedPawns.Contains(animal) && job.lord.ownedPawns.Contains(owner)
                && animal.Map == job.PhotoMap && owner.Map == job.PhotoMap
                && EligibleAnimal(animal, job) && PhotoGatheringPlan.CanJoin(owner, job)
                && owner.roping?.HasAnyRope != true;
        }

        private static List<IntVec3> Positions(Building_PhotoSpot spot)
        {
            var cells = GroupFormation.Cells(spot.Position, 3, spot.Map, spot.PreparationDirection);
            return cells.Count >= 3 ? new List<IntVec3> { cells[0], cells[2] } : new List<IntVec3>();
        }

        internal static bool CanStart(Building_PhotoSpot spot, Pawn owner, Pawn animal, out string why)
        {
            why = "";
            if (spot?.Spawned != true || spot.Faction != Faction.OfPlayer || !MFDefs.Ready)
                why = "MF_Pet_Unavailable".Translate();
            else if (owner == null || animal == null) why = "MF_Pet_SelectBoth".Translate();
            else if (GroupPhoto.ActiveJob(spot.Map, spot.Position) != null) why = "MF_GroupBusy".Translate();
            else if (owner.Map != spot.Map || animal.Map != spot.Map) why = "MF_Why_OtherMap".Translate();
            else if (!GroupPhoto.CanJoin(owner)) why = GroupPhoto.WhyNot(owner);
            else if (!EligibleAnimal(animal)) why = WhyNotAnimal(animal);
            else if (owner.roping?.HasAnyRope == true) why = "MF_Pet_Roped".Translate();
            else if (spot.Map.dangerWatcher.DangerRating == StoryDanger.High) why = "MF_Why_Unfit".Translate();
            else
            {
                var cells = Positions(spot);
                if (cells.Count != 2) why = "MF_Pet_NoRoom".Translate();
                else if (!owner.CanReach(cells[0], PathEndMode.OnCell, Danger.None)
                    || !animal.CanReach(cells[1], PathEndMode.OnCell, Danger.None)) why = "MF_Pet_NoPath".Translate();
            }
            return why.NullOrEmpty();
        }

        internal static LordJob_MFGroupPhoto Start(Building_PhotoSpot spot, Pawn owner, Pawn animal)
        {
            if (!CanStart(spot, owner, animal, out string why))
            { Messages.Message(why, MessageTypeDefOf.RejectInput, false); return null; }
            var members = new List<Pawn> { owner, animal };
            foreach (var p in members) PhotoGatheringAccess.Leave(p);
            foreach (var p in members) if (p.CurJob != null) p.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
            var job = new LordJob_MFGroupPhoto(spot.Position, members, Positions(spot), spot.PreparationDirection);
            job.MakePetPhoto(owner, animal);
            LordMaker.MakeNewLord(Faction.OfPlayer, job, spot.Map, members);
            Messages.Message("MF_PetPairStarted".Translate(owner.LabelShort, animal.LabelShort), spot, MessageTypeDefOf.NeutralEvent, false);
            return job;
        }
    }
}
