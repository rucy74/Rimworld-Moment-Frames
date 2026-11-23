using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>Exact selected roster and assigned cells, shared by group/reunion preview and confirmation.</summary>
    internal sealed class PhotoGatheringPlan
    {
        internal List<Pawn> Moving = new List<Pawn>();
        internal List<Pawn> Attending = new List<Pawn>();
        internal List<IntVec3> Cells = new List<IntVec3>();
        internal readonly Dictionary<Pawn,Pawn> BabyGuardians = new Dictionary<Pawn,Pawn>();
        internal readonly Dictionary<Pawn,string> Problems = new Dictionary<Pawn,string>();
        internal readonly Dictionary<Pawn,string> ShortProblems = new Dictionary<Pawn,string>();
        internal string Reason;
        internal bool CanStart => Reason.NullOrEmpty();
        internal static bool CanJoin(Pawn p,LordJob_MFGroupPhoto editing) => GroupPhoto.CanJoin(p,editing,true);
        private void Block(string why, Pawn pawn = null, string brief = null)
        {
            if (Reason.NullOrEmpty()) Reason = why;
            if (pawn != null && !Problems.ContainsKey(pawn)) { Problems[pawn] = why; ShortProblems[pawn] = brief ?? why; }
        }
        internal static PhotoGatheringPlan Create(Map map, IntVec3 place, IList<Pawn> selected, Rot4 direction,
            LordJob_MFGroupPhoto editing = null, IDictionary<Pawn,Pawn> babyGuardians = null, IDictionary<string,IntVec3> positions = null)
        {
            var plan = new PhotoGatheringPlan();
            if (map == null || !place.InBounds(map)) { plan.Block("MF_GroupSetupNoPlace".Translate()); return plan; }
            plan.Attending = (selected ?? new List<Pawn>()).Where(p => p != null).Distinct().ToList();
            plan.Moving = plan.Attending.Where(p => !p.DevelopmentalStage.Baby()).ToList();
            if (!MFDefs.Ready) plan.Block("MF_GroupDefsMissing".Translate());
            if (map.dangerWatcher.DangerRating == StoryDanger.High) plan.Block("MF_GroupDanger".Translate());
            if (editing == null && GroupPhoto.ActiveJob(map, place) != null) plan.Block("MF_GroupBusy".Translate());
            if (editing != null && (editing.IsBirthday || !editing.ReunionOf.NullOrEmpty() || editing.Finished || editing.Shooting || editing.Takes > 0))
                plan.Block("MF_PreparationNotEditing".Translate());
            if (plan.Attending.Count == 0) plan.Block("MF_GroupSetupEmpty".Translate());
            foreach(var baby in plan.Attending.Where(p => p.DevelopmentalStage.Baby()).ToList())
            {
                if(baby.Dead || !baby.IsFreeColonist || !BirthdayCarry.Available(baby) || baby.MapHeld!=map)
                { plan.Block("MF_PreparationPawnProblem".Translate(baby.LabelShort,"MF_Why_OtherMap".Translate()),baby,"MF_Why_OtherMap".Translate());continue; }
                Pawn guardian;
                if(babyGuardians!=null) babyGuardians.TryGetValue(baby,out guardian);
                else guardian=BirthdayCarry.Recommend(baby,editing,plan.BabyGuardians.Values,plan.Moving);
                if(guardian==null)
                { plan.Block("MF_GroupBabyNoGuardian".Translate(baby.LabelShort),baby,"MF_GroupBabyNoGuardianShort".Translate());continue; }
                if(plan.BabyGuardians.Values.Contains(guardian))
                { plan.Block("MF_GroupBabySharedGuardian".Translate(guardian.LabelShort),baby,"MF_GroupBabyNoGuardianShort".Translate());continue; }
                plan.BabyGuardians[baby]=guardian;
                if(!plan.Moving.Contains(guardian)) plan.Moving.Add(guardian);
                if(!plan.Attending.Contains(guardian)) plan.Attending.Add(guardian);
                if(!BirthdayCarry.CanCarry(guardian,baby,editing))
                    plan.Block("MF_PreparationGuardianProblem".Translate(guardian.LabelShort),baby,"MF_GroupBabyNoGuardianShort".Translate());
                if(BirthdayCarry.Holder(baby)!=null && BirthdayCarry.Holder(baby)!=guardian)
                    plan.Block("MF_GroupBabyNoGuardian".Translate(baby.LabelShort),baby,"MF_GroupBabyNoGuardianShort".Translate());
            }
            foreach (var p in plan.Moving)
            {
                if (p.Map != map || !CanJoin(p,editing))
                {
                    string why = p.Map != map ? "MF_Why_OtherMap".Translate().ToString() : GroupPhoto.WhyNot(p,editing,true);
                    if (why.NullOrEmpty()) why = "MF_Why_Unfit".Translate();
                    plan.Block("MF_PreparationPawnProblem".Translate(p.LabelShort,why), p, why);
                }
            }
            plan.Cells = GroupFormation.Cells(place,plan.Moving.Count,map,direction);
            if(Building_PhotoStudio.At(map,place)!=null) Building_PhotoStudio.Arrange(plan.Moving,plan.Cells);
            if(!PhotoPositions.Apply(map,place,direction,plan.Moving,plan.Cells,positions)) plan.Block("MF_PositionsInvalid".Translate());
            if (plan.Cells.Count != plan.Moving.Count) plan.Block("MF_PreparationSpaceProblem".Translate(plan.Attending.Count,plan.Cells.Count,plan.Moving.Count));
            else
            {
                for (int i = 0; i < plan.Moving.Count; i++)
                    if (plan.Moving[i].Spawned && plan.Moving[i].Map == map && !plan.Moving[i].CanReach(plan.Cells[i],PathEndMode.OnCell,Danger.Some))
                        plan.Block("MF_PreparationCannotReach".Translate(plan.Moving[i].LabelShort),plan.Moving[i],"MF_PreparationReachShort".Translate());
                foreach(var pair in plan.BabyGuardians)
                    if(!pair.Key.ComfortableTemperatureAtCell(plan.Cells[plan.Moving.IndexOf(pair.Value)],map))
                        plan.Block("MF_BabyUnsafePlace".Translate(),pair.Key,"MF_PreparationTemperatureShort".Translate());
            }
            return plan;
        }
    }
}
