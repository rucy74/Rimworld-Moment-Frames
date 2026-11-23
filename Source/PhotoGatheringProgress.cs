using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    internal static class PhotoGatheringProgress
    {
        internal static int Total(LordJob_MFGroupPhoto job) => job==null?0:job.Roster+job.CarriedPhotoBabies.Count()
            +(job.FixedBirthdaySubject && job.BirthdayGuardian==null?1:0);
        private static bool SubjectReady(LordJob_MFGroupPhoto job)
        {
            var subject=job.BirthdayPawn;
            if(!job.FixedBirthdaySubject || subject==null || subject.Dead || !PhotoCapture.VisualPosition(subject,job.PhotoMap,out _)) return false;
            var guardian=job.BirthdayGuardian;
            return guardian==null || (job.lord.ownedPawns.Contains(guardian) && guardian.carryTracker.CarriedThing==subject
                && guardian.Position==job.CellFor(guardian) && !guardian.pather.Moving);
        }
        internal static int Arrived(LordJob_MFGroupPhoto job) => job==null?0:job.InPlace+job.CarriedPhotoBabies.Count(job.BabyReady)
            +(job.BirthdayGuardian==null && SubjectReady(job)?1:0);
        internal static bool Ready(LordJob_MFGroupPhoto job) => job!=null && !job.Finished && !job.Shooting && !job.Holding
            && job.Complete && Total(job)>0 && Arrived(job)==Total(job);
        internal static string Summary(LordJob_MFGroupPhoto job)
        {
            if(job==null || job.Finished) return "";
            string title=(job.IsBirthday?"MF_ProgressBirthday":job.ReunionOf.NullOrEmpty()?"MF_ProgressGroup":"MF_ProgressReunion").Translate();
            string state=(job.Shooting?"MF_ProgressShooting":job.Holding?"MF_ProgressHolding":Ready(job)?"MF_ProgressAutoReady":"MF_ProgressGathering").Translate();
            return "MF_ProgressLine".Translate(title,Arrived(job),Total(job),state);
        }
        internal static string Details(LordJob_MFGroupPhoto job)
        {
            if(job==null || job.Finished || Ready(job) || job.Shooting) return "";
            var waiting=job.Members.Where(p=>p!=null&&!p.Dead)
                .Where(p=>!job.lord.ownedPawns.Contains(p)||job.Dropped.Contains(p)||job.NotInPlace.Contains(p))
                .Select(p=>
                {
                    bool missing=!job.lord.ownedPawns.Contains(p);string why=job.DropReason(p);
                    if(missing && why.NullOrEmpty())
                    {why=p.Map!=job.PhotoMap?"MF_Why_OtherMap".Translate().ToString():GroupPhoto.WhyNot(p);if(why.NullOrEmpty())why="MF_Why_Unfit".Translate();}
                    return p.LabelShort+(why.NullOrEmpty()?"":" ("+why+")");
                }).ToList();
            if(job.FixedBirthdaySubject && !SubjectReady(job) && job.BirthdayPawn!=null && !job.Members.Contains(job.BirthdayPawn))
                waiting.Insert(0,job.BirthdayPawn.LabelShort);
            foreach(var baby in job.CarriedPhotoBabies.Where(b=>b!=job.BirthdayPawn && !job.BabyReady(b)))
                waiting.Add(baby.LabelShort);
            if(waiting.Count==0) return "";
            string names=string.Join(", ",waiting.Take(2));
            if(waiting.Count>2) names="MF_ProgressOthers".Translate(names,waiting.Count-2);
            return "MF_ProgressWaiting".Translate(names);
        }
        internal static string PawnSummary(Pawn pawn)
        {
            var job=pawn?.GetLord()?.LordJob as LordJob_MFGroupPhoto;
            if(job==null || job.Finished) return "";
            string line=Summary(job),details=Details(job);
            return details.NullOrEmpty()?line:line+"\n"+details;
        }
    }
    [HarmonyPatch(typeof(Pawn),nameof(Pawn.GetInspectString))]
    internal static class PhotoGatheringPawnInspect
    {
        private static void Postfix(Pawn __instance,ref string __result)
        {
            string progress=PhotoGatheringProgress.PawnSummary(__instance);
            if(!progress.NullOrEmpty()) __result=__result.NullOrEmpty()?progress:progress+"\n"+__result;
        }
    }
}
