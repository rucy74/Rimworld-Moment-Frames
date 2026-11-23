using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    // Ordinary party guests may leave; a finished photo can release its roster even while paused.
    // The organizer and ceremonies/caravans retain their current duties.
    internal static class PhotoGatheringAccess
    {
        private static readonly System.Reflection.FieldInfo Organizer = AccessTools.Field(typeof(LordJob_Joinable_Gathering), "organizer");
        internal static bool CanLeave(Pawn pawn)
        {
            var job = pawn?.GetLord()?.LordJob;
            if (job is LordJob_MFGroupPhoto photo && photo.Finished) return true;
            return job?.GetType() == typeof(LordJob_Joinable_Party) && Organizer != null && Organizer.GetValue(job) is Pawn organizer && organizer != pawn;
        }
        internal static void Leave(Pawn pawn)
        {
            if (CanLeave(pawn)) pawn.GetLord().Notify_PawnLost(pawn, PawnLostCondition.LeftVoluntarily);
        }
        internal static string Blocked(Pawn pawn)
        {
            string activity = pawn?.GetLord()?.LordJob?.GetReport(pawn);
            return activity.NullOrEmpty() ? "MF_Why_OtherGathering".Translate().ToString() : "MF_Playtest_GatheringBlocked".Translate(activity).ToString();
        }
        internal static string Warning(Pawn pawn)
        {
            string medical = GroupPhoto.MedicalRestWarning(pawn);
            string party = CanLeave(pawn) && pawn?.GetLord()?.LordJob is LordJob_Joinable_Party ? "MF_Playtest_LeaveParty".Translate().ToString() : "";
            return string.Join("\n", new[] { medical, party }.Where(s => !s.NullOrEmpty()));
        }
    }
}
