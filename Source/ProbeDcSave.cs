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
    /// <summary>DC post (2026-11-06, -momentFramesDcSave with -momentFramesLoad and -momentFramesSteamScreen): the author's own colony, loaded from a COPY of
    /// one save in a disposable profile with that save's mod list. In the colony's own yard a wedding, a party and a new pet are started and filmed
    /// on the real screen, the mod's flash and card included (SteamFxClock). Nothing of the colony is rebuilt; only the event's people are brought close.</summary>
    public static partial class Probe
    {
        private static bool dcSaveMode;
        private static readonly string[] DcClips = { "wedding", "party", "pet" };
        private static int dcClip, dcShotFrame = -1, dcTicks, dcSkip, dcStarted;
        private static IntVec3 dcAt;
        private static Func<PhotoRecord> dcDone;

        private static void DcSaveStep()
        {
            var s = MomentFramesMod.Settings;
            var store = PhotoStore.Instance;
            switch (phase)
            {
                case 0:
                    deadline = Time.realtimeSinceStartup + 1500f;
                    map = Find.CurrentMap;
                    s.autoCaptureEnabled = true;
                    s.fxFlash = s.fxPopup = true;
                    s.fxShutter = s.fxFreeze = s.fxCamera = false;
                    Prefs.DevMode = false; Find.PlaySettings.showLearningHelper = false;
                    ClearHostiles(map);
                    Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear();
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    dcAt = DcSpot();
                    checks.Add("INFO: dc — colony '" + map.Parent?.LabelCap + "', " + map.mapPawns.FreeColonistsSpawnedCount + " colonists, spot " + dcAt + ", " + store.Records.Count + " records");
                    Directory.CreateDirectory(Path.Combine(dir, "steam"));
                    phase = 1; Wait(1f); return;
                case 1:
                    {
                        foreach (var l in map.lordManager.lords.Where(l => l.faction == Faction.OfPlayer).ToList()) map.lordManager.RemoveLord(l);
                        // The colony's own time of day: a jump to another day made a date banner mod cover the screen (r1).
                        dcShotFrame = -1; dcSkip = 0; dcStarted = Find.TickManager.TicksAbs;
                        switch (DcClips[dcClip])
                        {
                            case "wedding": DcWedding(); break;
                            case "party": DcParty(); break;
                            default: DcPet(); break;
                        }
                        phase = 2; return;
                    }
                case 2:
                    {
                        if (!SteamFrameReady()) return;
                        if (dcSkip > 0) { SteamTicks(Math.Min(120, dcSkip)); dcSkip -= 120; return; }
                        if (store.HasPending) { if (!PhotoCapture.Busy) SteamTicks(2); return; }
                        var rec = dcDone();
                        if (rec != null && dcShotFrame < 0) dcShotFrame = steamFrame;
                        if (rec != null && rec.HasFile) dcTicks = 6;
                        if (rec != null && rec.HasFile && !PhotoCapture.Busy && steamFrame >= dcShotFrame + 34)
                        {
                            SteamEnd();
                            File.WriteAllText(Path.Combine(dir, "steam", steamSeq, "shot.txt"), dcShotFrame.ToString());
                            checks.Add("PASS: dc " + DcClips[dcClip] + " photographed: " + rec.title);
                            phase = ++dcClip < DcClips.Length ? 1 : 3;
                            return;
                        }
                        if (steamFrame > 400) throw new TimeoutException("dc " + DcClips[dcClip] + ": no photo in 400 frames");
                        SteamTicks(dcTicks);
                        // Other mods' windows (a talk box, notices) are not the picture; only the mod's own card stays.
                        foreach (var w in Find.WindowStack.Windows.Where(w => !(w is Window_PhotoPop)).ToList()) Find.WindowStack.TryRemove(w, false);
                        SteamReactionClock();
                        DcTrace();
                        SteamFrame();
                        return;
                    }
                default:
                    Finish();
                    return;
            }
        }

        /// <summary>Who is where, facing which way and doing what, every filmed frame: to tell a pawn walking sideways from one only drawn aside.</summary>
        private static void DcTrace()
        {
            string path = Path.Combine(dir, "steam", steamSeq, "trace.tsv");
            if (steamFrame == 0) File.WriteAllText(path, "frame\tticks\tpawn\tcell\tfacing\tmoving\tjob\tdrawn\n");
            var sb = new System.Text.StringBuilder();
            foreach (var p in map.mapPawns.AllPawnsSpawned.Where(p => p.Position.InHorDistOf(steamAt.ToIntVec3(), 12f)))
                sb.Append(steamFrame).Append('\t').Append(Find.TickManager.TicksGame).Append('\t').Append(p.LabelShort).Append('\t').Append(p.Position)
                  .Append('\t').Append(p.Rotation.ToStringHuman()).Append('\t').Append(p.pather?.Moving == true).Append('\t').Append(p.CurJobDef?.defName)
                  .Append('\t').Append(p.DrawPos.x.ToString("0.00")).Append(',').Append(p.DrawPos.z.ToString("0.00")).Append('\n');
            File.AppendAllText(path, sb.ToString());
        }

        /// <summary>Where the colony gathers: its party spot, else open ground near the beds.</summary>
        private static IntVec3 DcSpot()
        {
            var party = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.def.defName == "PartySpot");
            if (party != null) return party.Position;
            var beds = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Where(b => !b.Medical && b.def.building.bed_humanlike).ToList();
            IntVec3 near = beds.Count > 0 ? new IntVec3((int)beds.Average(b => b.Position.x), 0, (int)beds.Average(b => b.Position.z)) : map.Center;
            return CellFinder.TryFindRandomCellNear(near, map, 14, c => c.Standable(map) && !c.Roofed(map) && c.GetRoom(map)?.PsychologicallyOutdoors == true, out var found) ? found : near;
        }

        private static List<Pawn> DcAdults() => map.mapPawns.FreeColonistsSpawned.Where(p => p.RaceProps.Humanlike && p.ageTracker.Adult && !p.Downed && !p.InMentalState).ToList();

        private static void DcGather(IEnumerable<Pawn> people, int radius)
        {
            var list = people.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                double angle = i * 2 * Math.PI / Math.Max(1, list.Count);
                var want = dcAt + new IntVec3((int)Math.Round(Math.Cos(angle) * radius), 0, (int)Math.Round(Math.Sin(angle) * radius * .8));
                if (!CellFinder.TryFindRandomCellNear(want, map, 3, c => c.Standable(map), out var near)) near = want;
                list[i].jobs?.EndCurrentJob(JobCondition.InterruptForced);
                Teleport(list[i], near);
            }
        }

        private static void DcWedding()
        {
            var adults = DcAdults();
            var a = adults.FirstOrDefault(p => LovePartnerRelationUtility.ExistingLovePartner(p) is Pawn q && adults.Contains(q)) ?? adults.First();
            var b = LovePartnerRelationUtility.ExistingLovePartner(a) as Pawn;
            if (b == null || !adults.Contains(b)) b = adults.First(x => x != a);
            DcGather(adults, 6);
            if (MFDebug.StartWedding(a, b, dcAt) == null) throw new InvalidOperationException("no marriage ceremony for " + a.LabelShort + " and " + b.LabelShort);
            checks.Add("INFO: dc wedding — " + a.LabelShort + " and " + b.LabelShort);
            dcTicks = 10;
            dcSkip = 4800;
            SteamBegin("dc-wedding", dcAt.ToVector3Shifted() + new Vector3(0f, 0f, 1.2f), 6.6f, 80, true);
            dcDone = () =>
            {
                if (steamFrame == 40) dcTicks = 60;
                if (a.CurJobDef == JobDefOf.MarryAdjacentPawn && b.CurJobDef == JobDefOf.MarryAdjacentPawn)
                {
                    a.rotationTracker.FaceCell(b.Position); b.rotationTracker.FaceCell(a.Position);
                    if ((a.jobs.curDriver as JobDriver_MarryAdjacentPawn)?.TicksLeftToMarry <= 130) dcTicks = 2;
                }
                return PhotoStore.Instance.Records.FirstOrDefault(r => r.kind == "wedding" && r.ticksAbs >= dcStarted);
            };
        }

        private static void DcParty()
        {
            var host = DcAdults().First();
            DcGather(map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed).Take(10), 4);
            var job = new LordJob_Joinable_Party(dcAt, host, GatheringDefOf.Party);
            HarmonyLib.Traverse.Create(job).Field("durationTicks").SetValue(2400);
            Verse.AI.Group.LordMaker.MakeNewLord(Faction.OfPlayer, job, map, new[] { host });
            string key = Hooks.Key("party", job);
            dcTicks = 9;
            dcSkip = 1100;
            SteamBegin("dc-party", dcAt.ToVector3Shifted(), 6.4f, 84, true);
            dcDone = () => PhotoStore.Instance.Records.FirstOrDefault(r => r.eventKey == key);
        }

        private static void DcPet()
        {
            var owner = DcAdults().First();
            DcGather(new[] { owner }, 2);
            var cat = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Cat"));
            if (!CellFinder.TryFindRandomCellNear(owner.Position + new IntVec3(3, 0, 0), map, 2, c => c.Standable(map), out var spot)) spot = owner.Position;
            GenSpawn.Spawn(cat, spot, map, Rot4.West);
            owner.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, spot + new IntVec3(-2, 0, 0)), JobCondition.InterruptForced);
            owner.jobs.jobQueue.EnqueueLast(JobMaker.MakeJob(JobDefOf.Wait, 3000));
            cat.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 3000), JobCondition.InterruptForced);
            string key = PetMilestones.Key(cat);
            bool tamed = false;
            dcTicks = 5;
            SteamBegin("dc-pet", spot.ToVector3Shifted() + new Vector3(-1f, 0f, -0.6f), 5.6f, 110, true);
            dcDone = () =>
            {
                if (!tamed && steamFrame >= 22) { tamed = true; InteractionWorker_RecruitAttempt.DoRecruit(owner, cat, false); }
                return PhotoStore.Instance.Records.FirstOrDefault(r => r.eventKey == key);
            };
        }
    }
}
