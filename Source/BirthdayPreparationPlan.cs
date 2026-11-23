using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>A read-only plan, shared by the preparation window and the final gather click.</summary>
    internal sealed class BirthdayPreparationPlan
    {
        internal Map Map;
        internal IntVec3 Place;
        internal List<Pawn> Moving = new List<Pawn>();
        internal List<IntVec3> Cells = new List<IntVec3>();
        internal readonly Dictionary<Pawn, string> Problems = new Dictionary<Pawn, string>();
        internal readonly Dictionary<Pawn, string> ShortProblems = new Dictionary<Pawn, string>();
        internal string Reason;
        internal bool CanStart => Reason.NullOrEmpty();

        private void Block(string why, Pawn pawn = null, string brief = null)
        {
            if (Reason.NullOrEmpty()) Reason = why;
            if (pawn != null && !Problems.ContainsKey(pawn))
            { Problems[pawn] = why; ShortProblems[pawn] = brief ?? why; }
        }
        internal static BirthdayPreparationPlan Create(Pawn subject, IntVec3 place, IList<Pawn> selected, Rot4 direction,
            LordJob_MFGroupPhoto editing, Pawn guardian = null, bool requireGuardian = false, IDictionary<string,IntVec3> positions = null)
        {
            var plan = new BirthdayPreparationPlan { Map = subject?.MapHeld, Place = place };
            if (subject == null || !BirthdayCarry.Available(subject) || subject.Dead || !subject.IsFreeColonist || PhotoStore.Instance == null)
            { plan.Block("MF_BirthdayGone".Translate(subject?.LabelShort ?? "?")); return plan; }
            bool fixedSubject = Birthday.FixedSubject(subject);
            if (fixedSubject && guardian == null && !requireGuardian) plan.Place = subject.PositionHeld;
            plan.Moving = selected.Where(p => p != null && (p != subject || !fixedSubject)).Distinct().ToList();
            if (guardian != null && !plan.Moving.Contains(guardian)) plan.Moving.Insert(0, guardian);
            if (!fixedSubject && !plan.Moving.Contains(subject)) plan.Moving.Insert(0, subject);

            if (requireGuardian && guardian == null) plan.Block("MF_BabyNoGuardian".Translate());
            if (guardian != null && !BirthdayCarry.CanCarry(guardian, subject, editing))
                plan.Block("MF_PreparationGuardianProblem".Translate(guardian.LabelShort), guardian, "MF_PreparationGuardianShort".Translate());
            if (BirthdayCarry.Holder(subject) != null && guardian != BirthdayCarry.Holder(subject))
                plan.Block("MF_BabyNoGuardian".Translate());
            if (editing != null && (editing.Finished || editing.Shooting)) plan.Block("MF_PreparationNotEditing".Translate());
            if (editing == null)
            {
                if (plan.Map.dangerWatcher.DangerRating == StoryDanger.High) plan.Block("MF_GroupDanger".Translate());
                if (Birthday.Active(subject) != null) plan.Block("MF_GroupBusy".Translate());
            }
            foreach (var p in plan.Moving)
            {
                // Existing gathering members are allowed to keep their own lord; use the same rule at confirmation.
                if (p.Map != plan.Map || !PhotoGatheringPlan.CanJoin(p, editing))
                {
                    string why = p.Map != plan.Map ? "MF_Why_OtherMap".Translate().ToString() : GroupPhoto.WhyNot(p, editing, true);
                    if (why.NullOrEmpty()) why = "MF_Why_Unfit".Translate();
                    plan.Block("MF_PreparationPawnProblem".Translate(p.LabelShort, why), p, why);
                }
            }
            plan.Cells = GroupFormation.Cells(plan.Place, plan.Moving.Count, plan.Map, direction);
            int protagonist = plan.Moving.IndexOf(guardian ?? subject);
            if(Building_PhotoStudio.At(plan.Map,plan.Place)!=null) Building_PhotoStudio.Arrange(plan.Moving,plan.Cells,guardian??subject);
            else if (protagonist >= 0 && plan.Cells.Count == plan.Moving.Count)
                PhotoPreparationChoices.PutSubjectAtFront(plan.Cells, protagonist, plan.Place, direction);
            if(!PhotoPositions.Apply(plan.Map,plan.Place,direction,plan.Moving,plan.Cells,positions)) plan.Block("MF_PositionsInvalid".Translate());
            if (plan.Cells.Count != plan.Moving.Count)
                plan.Block("MF_PreparationSpaceProblem".Translate(selected.Distinct().Count(), plan.Cells.Count, plan.Moving.Count));
            else
            {
                for (int i = 0; i < plan.Moving.Count; i++)
                {
                    var p = plan.Moving[i];
                    if (p.Map == plan.Map && !p.CanReach(plan.Cells[i], PathEndMode.OnCell, Danger.Some))
                        plan.Block("MF_PreparationCannotReach".Translate(p.LabelShort), p, "MF_PreparationReachShort".Translate());
                }
                if (guardian != null && !subject.ComfortableTemperatureAtCell(plan.Cells[plan.Moving.IndexOf(guardian)], plan.Map))
                    plan.Block("MF_BabyUnsafePlace".Translate(), guardian, "MF_PreparationTemperatureShort".Translate());
            }
            return plan;
        }
    }
}
