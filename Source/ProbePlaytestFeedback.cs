using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>PT-017..023 regression in an explicitly disposable native game profile.</summary>
    public static partial class Probe
    {
        private static bool playtestFeedbackMode;
        private static readonly List<Pawn> feedbackAdults = new List<Pawn>();
        private static readonly List<Pawn> feedbackBabies = new List<Pawn>();
        private static readonly List<Building_Bed> feedbackBabyBeds = new List<Building_Bed>();
        private static List<string> feedbackAdultIds, feedbackBabyIds, feedbackCarrierIds;
        private static Building_PhotoSpot feedbackSpot;
        private static Building_Bed feedbackSleepBed;
        private static Dialog_BirthdaySetup feedbackBirthdayDialog;
        private static Dialog_GroupPhotoSetup feedbackGroupDialog;
        private static LordJob_MFGroupPhoto feedbackJob;
        private static IntVec3 feedbackPlace;
        private static int feedbackTicks, feedbackTimeBefore;
        private static string feedbackPhotoId;
        private static bool feedbackBirthdayArrived;

        private static void FeedbackCheck(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("playtest feedback: " + label);
            checks.Add("PASS: " + label);
        }
        private static bool FeedbackAdvance(Func<bool> until, int budget = 1)
        {
            for (int i = 0; i < budget && !until(); i++) { Find.TickManager.DoSingleTick(); feedbackTicks++; }
            if (feedbackTicks > 8000) throw new TimeoutException("feedback phase " + phase + ": " + feedbackJob?.Result
                + ", inPlace=" + feedbackJob?.InPlace + ", roster=" + feedbackJob?.Roster + ", photo=" + feedbackJob?.PhotoId
                + ", jobs=" + string.Join("; ", feedbackAdults.Select(p => p.LabelShort + ":" + p.CurJobDef + "@" + p.Position)));
            return until();
        }
        private static void FeedbackSettle()
        {
            foreach (var p in map.mapPawns.FreeColonistsSpawned) p.Drawer.tweener.ResetTweenedPosToRoot();
        }
        private static bool FeedbackRequire(string name)
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
            Require(name); return true;
        }
        private static void FeedbackClose()
        {
            feedbackBirthdayDialog?.Close(); feedbackBirthdayDialog = null;
            feedbackGroupDialog?.Close(); feedbackGroupDialog = null;
        }
        private static void FeedbackDisband()
        {
            FeedbackClose(); feedbackJob?.Cancel();
            for (int i = 0; i < 130; i++) Find.TickManager.DoSingleTick();
            foreach (var p in feedbackAdults) FixReady(p);
            feedbackTicks = 0;
        }
        private static void FeedbackFinish()
        {
            File.WriteAllText(Path.Combine(dir, "playtest-feedback-result.json"), "{\"ok\":true,\"checks\":["
                + string.Join(",", checks.Select(Json)) + "],\"limits\":\"Disposable Core/Harmony fixture, optional official Biotech; prepared 22C map."
                + " Natural player save, full modpack, subjective appearance and original Ruby/Julian incident are untested."
                + " Selection invokes the same handlers as native guest rows; no OS mouse automation."
                + " Deadline fixture ages the native lord timestamp; danger fixture spawns hostile mechanoids and invalidates the watcher cache.\"}");
            active = false; Application.Quit();
        }
        private static void FeedbackSetup()
        {
            FxSet(false); FixSetup(); Find.MainTabsRoot.EscapeCurrentTab(false);
            map.Biome.constantOutdoorTemperature = 22f;
            MomentFramesMod.Settings.msColonyStart = false; MomentFramesMod.Settings.birthdayMode = Birthday.ModeOff;
            MomentFramesMod.Settings.albumTheme = "graphite"; MomentFramesMod.Settings.albumFont = "pencil";
            MomentFramesMod.Settings.albumTextSize = 130;
            feedbackAdults.AddRange(map.mapPawns.FreeColonistsSpawned.Where(p => p.DevelopmentalStage.Adult()));
            while (feedbackAdults.Count < 7)
            {
                var p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    forceGenerateNewPawn: true, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f));
                GenSpawn.Spawn(p, center + new IntVec3(-12, 0, feedbackAdults.Count), map); feedbackAdults.Add(p);
            }
            for (int i = 0; i < feedbackAdults.Count; i++)
            {
                var p = feedbackAdults[i]; FixReady(p); p.Position = center + new IntVec3(i * 2 - 8, 0, -8);
                p.Notify_Teleported(true, true);
            }
            feedbackAdultIds = feedbackAdults.Select(p => p.GetUniqueLoadID()).ToList();
            feedbackSpot = (Building_PhotoSpot)ThingMaker.MakeThing(ThingDef.Named("MF_PhotoSpot"));
            feedbackSpot.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(feedbackSpot, center + new IntVec3(8, 0, 0), map);
            feedbackPlace = center + new IntVec3(-8, 0, 3); // Picked location; deliberately no photo spot here.
            Find.CameraDriver.SetRootPosAndSize(feedbackSpot.DrawPos, 15f);
        }
        private static void FeedbackReject(Pawn p, string label, string expected = null)
        {
            string reason = GroupPhoto.WhyNot(p);
            FeedbackCheck(!GroupPhoto.CanJoin(p) && !reason.NullOrEmpty() && (expected == null || reason == expected.Translate().ToString()),
                label + " rejects with a non-empty condition-specific reason: " + reason);
            var plan = PhotoGatheringPlan.Create(map, feedbackSpot.Position, new[] { p }, Rot4.North);
            FeedbackCheck(!plan.CanStart && plan.ShortProblems.TryGetValue(p, out var brief) && !brief.NullOrEmpty(),
                label + " is also explained by preparation preflight");
        }
        private static void FeedbackBabyRandomLimits()
        {
            foreach(var p in feedbackAdults.Skip(1)) p.drafter.Drafted=true;
            FeedbackCheck(feedbackGroupDialog.MaxRandomCount==2,
                "random total is capped at one available caregiver plus one baby when two babies need that same caregiver");
            for(int seed=0;seed<8;seed++)
            {
                feedbackGroupDialog.SelectRandomGuests(3,new System.Random(seed));
                var chosen=feedbackGroupDialog.Chosen;
                FeedbackCheck(chosen.Count==2 && chosen.Count(p=>p.DevelopmentalStage.Baby())==1
                    && chosen.Contains(feedbackAdults[0]) && feedbackGroupDialog.CanStart,
                    "random total respects the caregiver limit and exact advertised count, seed="+seed);
            }
            foreach(var p in feedbackAdults) {p.drafter.Drafted=false;FixReady(p);}
            feedbackGroupDialog.SelectGuests(2);
            foreach(var baby in feedbackBabies) feedbackGroupDialog.ToggleGuest(baby);
            FeedbackCheck(feedbackGroupDialog.Chosen.Count==4 && feedbackGroupDialog.CanStart,
                "restoring caregivers permits both babies and two distinct caregivers after limited random selection");
        }
        private static void FeedbackReasons()
        {
            var p = feedbackAdults[1];
            p.drafter.Drafted = true; FeedbackReject(p, "drafted participant", "MF_Why_Drafted"); p.drafter.Drafted = false; FixReady(p);
            var forced = JobMaker.MakeJob(JobDefOf.Goto, p.Position + new IntVec3(1, 0, 0)); forced.playerForced = true;
            p.jobs.StartJob(forced, JobCondition.InterruptForced);
            FeedbackReject(p, "player-issued job", "MF_Why_Busy"); FixReady(p);
            var party = LordMaker.MakeNewLord(Faction.OfPlayer,
                new LordJob_Joinable_Party(center, p, GatheringDefOf.Party), map, new[] { p });
            FeedbackReject(p, "other gathering");
            map.lordManager.RemoveLord(party); FixReady(p);
            p.needs.food.CurLevel = 0f; FeedbackReject(p, "starving participant", "MF_Why_Starving"); FixReady(p);
            p.needs.rest.CurLevel = 0f; FeedbackReject(p, "exhausted participant", "MF_Why_Exhausted"); FixReady(p);
            var bloodLoss = HediffMaker.MakeHediff(HediffDefOf.BloodLoss, p); bloodLoss.Severity = .3f; p.health.AddHediff(bloodLoss);
            FeedbackReject(p, "blood-loss participant", "MF_Why_BloodLoss"); FixReady(p);
            var cut = HediffMaker.MakeHediff(HediffDefOf.Cut, p, p.RaceProps.body.corePart); cut.Severity = 2f; p.health.AddHediff(cut);
            FeedbackReject(p, "bleeding participant", "MF_Why_Bleeding"); FixReady(p);
            var flu = HediffMaker.MakeHediff(HediffDef.Named("Flu"), p); flu.Severity = .1f; p.health.AddHediff(flu);
            FeedbackCheck(!GroupPhoto.WhyNot(p).NullOrEmpty(), "treatment-needed participant keeps its reason"); FixReady(p);
            FeedbackCheck(GroupPhoto.CanJoin(p) && GroupPhoto.WhyNot(p).NullOrEmpty(), "healthy free participant becomes available with no stale warning");
        }
        private static void FeedbackSleep(Pawn p)
        {
            feedbackSleepBed = (Building_Bed)ThingMaker.MakeThing(ThingDef.Named("SleepingSpot"));
            feedbackSleepBed.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(feedbackSleepBed, center + new IntVec3(-18, 0, -8), map);
            feedbackSleepBed.CompAssignableToPawn.TryAssignPawn(p); p.Position = feedbackSleepBed.Position; p.Notify_Teleported(true, true);
            p.needs.rest.CurLevel = .6f; p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, feedbackSleepBed), JobCondition.InterruptForced);
            feedbackTicks = 0;
        }
        private static void FeedbackRefreshAfterLoad()
        {
            map = Find.CurrentMap;
            var available = PhotoCapture.VisiblePawns(map).ToList();
            feedbackAdults.Clear(); feedbackAdults.AddRange(feedbackAdultIds.Select(id => available.First(p => p.GetUniqueLoadID() == id)));
            feedbackBabies.Clear();
            if (feedbackBabyIds != null) feedbackBabies.AddRange(feedbackBabyIds.Select(id => available.First(p => p.GetUniqueLoadID() == id)));
            feedbackSpot = map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSpot>().First();
            feedbackJob = map.lordManager.lords.Select(l => l.LordJob).OfType<LordJob_MFGroupPhoto>()
                .FirstOrDefault(j => !j.Finished && !j.IsBirthday);
            feedbackBabyBeds.Clear();
            foreach (var baby in feedbackBabies)
                feedbackBabyBeds.Add(map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>().First(b => b.OwnersForReading.Contains(baby)));
        }
        private static void FeedbackEditingDeadline()
        {
            var lord = GroupPhoto.StartPrepared(map, feedbackSpot.Position, feedbackAdults.Take(3).ToList(), Rot4.North);
            feedbackJob = lord?.LordJob as LordJob_MFGroupPhoto;
            FeedbackCheck(feedbackJob != null, "start ordinary editing safety fixture");
            feedbackGroupDialog = new Dialog_GroupPhotoSetup(feedbackSpot, feedbackJob); Find.WindowStack.Add(feedbackGroupDialog);
            Traverse.Create(feedbackJob).Field("startTick").SetValue(Find.TickManager.TicksGame - GroupPhoto.GiveUpTicks + 30);
            for (int i = 0; i < 120; i++) Find.TickManager.DoSingleTick();
            FeedbackCheck(!feedbackJob.Finished && feedbackJob.Takes == 0,
                "time spent editing the non-pausing roster does not consume the remaining gathering deadline or auto-shoot");
            feedbackGroupDialog.Close(); feedbackGroupDialog = null;
            for (int i = 0; i < 90 && !feedbackJob.Finished; i++) Find.TickManager.DoSingleTick();
            FeedbackCheck(feedbackJob.Finished && feedbackJob.Takes == 0,
                "closing the editor resumes the remaining gathering deadline instead of resetting it indefinitely");
            FeedbackDisband();
        }
        private static void FeedbackEditingDanger()
        {
            FeedbackCheck(Birthday.StartPrepared(feedbackAdults[0], 32, feedbackPlace, feedbackAdults.Take(3).ToList(), Rot4.North, null),
                "start birthday editing safety fixture");
            feedbackJob = Birthday.Active(feedbackAdults[0]);
            feedbackBirthdayDialog = new Dialog_BirthdaySetup(feedbackAdults[0], 32, feedbackJob, feedbackPlace);
            Find.WindowStack.Add(feedbackBirthdayDialog);
            for(int i=0;i<8;i++)
            {
                var hostile=PawnGenerator.GeneratePawn(PawnKindDef.Named("Mech_Lancer"),Faction.OfMechanoids);
                GenSpawn.Spawn(hostile,center+new IntVec3(25+i,0,15),map);
            }
            var danger = Traverse.Create(map.dangerWatcher);
            danger.Field("lastUpdateTick").SetValue(-10000);
            FeedbackCheck(map.dangerWatcher.DangerRating == StoryDanger.High, "native danger watcher observes actual hostile mechanoid combat power");
            for (int i = 0; i < GroupPhoto.TickEvery + 1 && !feedbackJob.Finished; i++) Find.TickManager.DoSingleTick();
            FeedbackCheck(feedbackJob.Finished && feedbackJob.Result == LordJob_MFGroupPhoto.Outcome.Danger && feedbackJob.Takes == 0,
                "open non-pausing birthday editor still cancels the gathering when danger rises");
            ClearHostiles(map); FeedbackDisband();
        }
        private static void FeedbackCreateBabies()
        {
            for (int i = 0; i < 2; i++)
            {
                var baby = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    forceGenerateNewPawn: true, fixedBiologicalAge: 1f, fixedChronologicalAge: 1f,
                    allowDowned: true, developmentalStages: DevelopmentalStage.Baby));
                baby.relations.AddDirectRelation(PawnRelationDefOf.Parent, feedbackAdults[i]);
                var bed = (Building_Bed)ThingMaker.MakeThing(ThingDef.Named("BabySleepingSpot"));
                bed.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bed, center + new IntVec3(-20 + i * 3, 0, -2), map);
                GenSpawn.Spawn(baby, bed.Position, map); bed.CompAssignableToPawn.TryAssignPawn(baby);
                baby.needs.food.CurLevel = .95f; baby.needs.rest.CurLevel = .9f;
                baby.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, bed), JobCondition.InterruptForced);
                feedbackBabies.Add(baby); feedbackBabyBeds.Add(bed);
            }
            feedbackBabyIds = feedbackBabies.Select(p => p.GetUniqueLoadID()).ToList();
            feedbackGroupDialog = new Dialog_GroupPhotoSetup(feedbackSpot); Find.WindowStack.Add(feedbackGroupDialog);
            feedbackGroupDialog.SelectGuests(2);
            foreach (var baby in feedbackBabies)
                FeedbackCheck(feedbackGroupDialog.ToggleGuest(baby) && feedbackGroupDialog.Chosen.Contains(baby),
                    "ordinary guest-row handler selects baby " + baby.LabelShort);
            var carriers = feedbackBabies.Select(b => feedbackGroupDialog.GuardianFor(b)).ToList();
            FeedbackCheck(carriers.All(p => p != null) && carriers.Distinct().Count() == 2
                && carriers.All(p => feedbackGroupDialog.Chosen.Contains(p)), "two ordinary babies receive separate automatically selected caregivers");
            FeedbackCheck(carriers.All(p => !feedbackGroupDialog.ToggleGuest(p)), "caregiver rows cannot be unchecked while their babies remain selected");
            feedbackCarrierIds = carriers.Select(p => p.GetUniqueLoadID()).ToList();
            FeedbackCheck(feedbackGroupDialog.Chosen.Count == 4 && feedbackGroupDialog.CanStart,
                "ordinary two-baby draft has exactly two babies and two caregivers and passes preflight");
            feedbackTicks = 0;
        }
        private static void PlaytestFeedbackStep()
        {
            switch (phase)
            {
                case 0:
                    FeedbackSetup(); FeedbackReasons();
                    feedbackGroupDialog = new Dialog_GroupPhotoSetup(feedbackSpot); Find.WindowStack.Add(feedbackGroupDialog);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal; feedbackTimeBefore = Find.TickManager.TicksGame;
                    Wait(1.5f); phase = 1; return;
                case 1:
                    FeedbackCheck(!feedbackGroupDialog.forcePause && !Find.WindowStack.WindowsForcePause
                        && Find.TickManager.TicksGame > feedbackTimeBefore, "ordinary preparation lets actual game time advance at the user's normal speed");
                    CloseLog(); Shot("feedback-group-unpaused.png"); Wait(.6f); phase = 2; return;
                case 2:
                    if (!FeedbackRequire("feedback-group-unpaused.png")) return;
                    FeedbackClose();
                    foreach (var p in feedbackAdults) FixReady(p);
                    Birthday.OpenSetup(feedbackAdults[0], 31, null, feedbackPlace);
                    feedbackBirthdayDialog = Find.WindowStack.Windows.OfType<Dialog_BirthdaySetup>().Last();
                    feedbackBirthdayDialog.SelectGuests(2);
                    foreach (var guest in feedbackAdults.Skip(1))
                        FeedbackCheck(feedbackBirthdayDialog.ToggleGuest(guest) && feedbackBirthdayDialog.Chosen.Contains(guest),
                            "picked-place birthday guest-row handler can add person beyond a two-person draft: " + guest.LabelShort);
                    FeedbackCheck(feedbackBirthdayDialog.Chosen.Count == feedbackAdults.Count,
                        "picked-place birthday supports all seven individually checked people without a one/two-person cap");
                    feedbackTimeBefore = Find.TickManager.TicksGame; Wait(1.5f); phase = 3; return;
                case 3:
                    FeedbackCheck(!feedbackBirthdayDialog.forcePause && !Find.WindowStack.WindowsForcePause
                        && Find.TickManager.TicksGame > feedbackTimeBefore, "birthday preparation lets actual game time advance at the user's normal speed");
                    Find.TickManager.Pause(); FeedbackClose(); foreach (var p in feedbackAdults) FixReady(p);
                    FeedbackSleep(feedbackAdults[1]); phase = 4; return;
                case 4:
                    if (!FeedbackAdvance(() => !feedbackAdults[1].Awake())) return;
                    FeedbackCheck(feedbackAdults[1].InBed() && GroupPhoto.CanJoin(feedbackAdults[1]) && GroupPhoto.WhyNot(feedbackAdults[1]).NullOrEmpty(),
                        "healthy sleeping adult can be summoned and is not falsely marked downed or unavailable");
                    Birthday.OpenSetup(feedbackAdults[0], 31, null, feedbackPlace);
                    feedbackBirthdayDialog = Find.WindowStack.Windows.OfType<Dialog_BirthdaySetup>().Last();
                    feedbackBirthdayDialog.SelectGuests(2);
                    foreach (var guest in feedbackAdults.Skip(1))
                        FeedbackCheck(feedbackBirthdayDialog.ToggleGuest(guest), "birthday handler includes selected sleeping/awake guest " + guest.LabelShort);
                    CloseLog(); Shot("feedback-birthday-sleeping-selected.png"); Wait(.6f); phase = 5; return;
                case 5:
                    if (!FeedbackRequire("feedback-birthday-sleeping-selected.png")) return;
                    FeedbackCheck(Birthday.StartPrepared(feedbackAdults[0], 31, feedbackPlace,
                        feedbackBirthdayDialog.Chosen, Rot4.North, null), "birthday confirmation summons all seven selected people including the sleeper");
                    feedbackJob = Birthday.Active(feedbackAdults[0]); FeedbackClose(); feedbackTicks = 0; phase = 6; return;
                case 6:
                    if (!FeedbackAdvance(() => feedbackAdults[1].Awake() && feedbackAdults[1].Position != feedbackSleepBed.Position)) return;
                    FeedbackCheck(feedbackAdults[1].GetLord()?.LordJob == feedbackJob && feedbackJob.Members.Contains(feedbackAdults[1]),
                        "sleeping guest wakes and starts walking under the photo duty rather than dropping from the roster");
                    feedbackJob.EditingBirthday = true; phase = 7; return;
                case 7:
                    if (!FeedbackAdvance(() => feedbackJob.InPlace == feedbackJob.Members.Count)) return;
                    FeedbackCheck(feedbackJob.Members.Count == 7 && feedbackJob.Takes == 0 && !feedbackJob.Shooting,
                        "all seven selected birthday guests arrive naturally while roster editing suppresses only the shutter");
                    FeedbackCheck(feedbackJob.Members.All(p => GroupPhoto.CanJoin(p, feedbackJob) && GroupPhoto.WhyNot(p, feedbackJob).NullOrEmpty()),
                        "healthy members of the same birthday lord remain selectable with no false other-gathering warning");
                    FeedbackSettle(); feedbackBirthdayArrived = true; feedbackJob.EditingBirthday = false; phase = 8; return;
                case 8:
                    if (!FeedbackAdvance(() => feedbackJob.Shooting || feedbackJob.Takes > 0)) return;
                    FeedbackCheck(feedbackBirthdayArrived && feedbackJob.Members.Count == 7,
                        "birthday automatically begins photography after all selected people arrive without ShootNow");
                    phase = 9; return;
                case 9:
                    if (PhotoCapture.Busy || feedbackJob.Shooting) return;
                    var birthday = PhotoStore.Instance.Get(feedbackJob.PhotoId);
                    FeedbackCheck(birthday?.HasFile == true && birthday.peopleIds.Count == 7 && feedbackAdults.All(birthday.Has)
                        && feedbackJob.Takes == 1 && feedbackJob.Finished, "automatic birthday saves all seven people then finishes the gathering");
                    File.Copy(PhotoStore.Instance.FilePath(birthday), Path.Combine(dir, "feedback-birthday-auto-seven.png"), true);
                    FeedbackCheck(birthday.HasPeopleBox, "automatic birthday photograph contains measured person crop bounds");
                    feedbackPhotoId = birthday.id;
                    for (int i = 0; i < 130; i++) Find.TickManager.DoSingleTick();
                    FeedbackCheck(feedbackAdults.All(p => p.GetLord() == null), "all birthday guests are automatically released after the successful photograph");
                    GameDataSaveLoader.SaveGame("mf-feedback-birthday-complete"); GameDataSaveLoader.LoadGame("mf-feedback-birthday-complete");
                    Wait(1f); phase = 10; return;
                case 10:
                    FeedbackRefreshAfterLoad();
                    var loadedBirthday = PhotoStore.Instance.Get(feedbackPhotoId);
                    FeedbackCheck(loadedBirthday?.HasFile == true && feedbackAdults.All(loadedBirthday.Has)
                        && feedbackAdults.All(p => p.GetLord() == null), "real load after automatic birthday keeps the photo and released attendees");
                    foreach (var p in feedbackAdults) FixReady(p);
                    FeedbackEditingDeadline(); FeedbackEditingDanger();
                    var adultLord = GroupPhoto.StartPrepared(map, feedbackSpot.Position, feedbackAdults.Take(3).ToList(), Rot4.North);
                    feedbackJob = adultLord?.LordJob as LordJob_MFGroupPhoto;
                    FeedbackCheck(feedbackJob != null, "start ordinary adult-only automatic gathering after safety checks");
                    feedbackJob.EditingPhotoSetup = true; feedbackTicks = 0; phase = 26; return;
                case 26:
                    if (!FeedbackAdvance(() => feedbackJob.InPlace == 3)) return;
                    FeedbackSettle(); feedbackJob.EditingPhotoSetup = false; phase = 27; return;
                case 27:
                    if (!FeedbackAdvance(() => feedbackJob.Shooting || feedbackJob.Takes > 0)) return;
                    phase = 28; return;
                case 28:
                    if (PhotoCapture.Busy || feedbackJob.Shooting) return;
                    var adultPhoto = PhotoStore.Instance.Get(feedbackJob.PhotoId);
                    FeedbackCheck(adultPhoto?.HasFile == true && feedbackAdults.Take(3).All(adultPhoto.Has)
                        && feedbackJob.Takes == 1 && feedbackJob.Holding && !feedbackJob.Finished,
                        "adult-only ordinary photograph keeps its existing retake formation after the first saved shot");
                    File.Copy(PhotoStore.Instance.FilePath(adultPhoto), Path.Combine(dir, "feedback-group-adult-holding.png"), true);
                    FeedbackDisband();
                    if (!ModsConfig.BiotechActive)
                    {
                        checks.Add("INFO: Biotech absent; ordinary baby transport fixture not exercised in this Core run.");
                        FeedbackFinish(); return;
                    }
                    FeedbackCreateBabies(); FeedbackBabyRandomLimits(); Wait(.5f); phase = 11; return;
                case 11:
                    CloseLog(); Shot("feedback-group-two-babies.png"); Wait(.6f); phase = 12; return;
                case 12:
                    if (!FeedbackRequire("feedback-group-two-babies.png")) return;
                    FeedbackCheck(feedbackGroupDialog.StartSelected(), "ordinary preparation confirmation starts the two-baby gathering");
                    feedbackJob = GroupPhoto.ActiveJob(map, feedbackSpot.Position); FeedbackClose(); phase = 13; return;
                case 13:
                    if (!FeedbackAdvance(() => feedbackBabies.All(b => BirthdayCarry.Holder(b) != null))) return;
                    FeedbackCheck(feedbackBabies.All(b => !b.Spawned && feedbackJob.CarrierForBaby(b)?.carryTracker.CarriedThing == b)
                        && feedbackJob.Members.Count == 2, "native ordinary pickup holds both babies in their separate two standing caregivers");
                    GameDataSaveLoader.SaveGame("mf-feedback-two-babies-transit"); GameDataSaveLoader.LoadGame("mf-feedback-two-babies-transit");
                    Wait(1f); phase = 14; return;
                case 14:
                    FeedbackRefreshAfterLoad();
                    FeedbackCheck(feedbackJob != null && feedbackBabies.All(b => feedbackJob.CarrierForBaby(b)?.carryTracker.CarriedThing == b)
                        && feedbackJob.CarriedPhotoBabies.Count() == 2, "real load in ordinary transit keeps both babies and their caregiver assignments");
                    feedbackJob.EditingPhotoSetup = true; phase = 15; return;
                case 15:
                    if (!FeedbackAdvance(() => feedbackJob.InPlace == 2)) return;
                    FeedbackCheck(feedbackBabies.All(b => BirthdayCarry.Holder(b)?.Position == feedbackJob.CellFor(feedbackJob.CarrierForBaby(b))),
                        "both caregivers carry their babies to their real assigned standing places");
                    FeedbackSettle(); feedbackJob.EditingPhotoSetup = false; phase = 16; return;
                case 16:
                    if (!FeedbackAdvance(() => feedbackJob.Shooting || feedbackJob.Takes > 0)) return;
                    phase = 17; return;
                case 17:
                    if (PhotoCapture.Busy || feedbackJob.Shooting) return;
                    var group = PhotoStore.Instance.Get(feedbackJob.PhotoId);
                    var intended = feedbackBabyIds.Concat(feedbackCarrierIds).ToList();
                    FeedbackCheck(group?.HasFile == true && group.peopleIds.Count == 4 && intended.All(id => group.peopleIds.Contains(id))
                        && feedbackJob.Takes == 1 && feedbackJob.Finished,
                        "ordinary automatic photograph saves both carried babies and both caregivers exactly once then finishes the baby gathering"
                        + (group?.HasFile == true && group.peopleIds.Count == 4 && feedbackJob.Takes == 1 && feedbackJob.Finished ? ""
                            : " (record " + (group == null ? "none" : "file " + group.file + ", people " + group.peopleIds.Count + " " + string.Join(",", group.peopleIds))
                              + ", intended " + string.Join(",", intended) + ", takes " + feedbackJob.Takes + ", finished " + feedbackJob.Finished + ")"));
                    FeedbackCheck(group.HasPeopleBox, "ordinary carried-baby photograph has native person crop bounds");
                    File.Copy(PhotoStore.Instance.FilePath(group), Path.Combine(dir, "feedback-group-carried-two-babies.png"), true);
                    feedbackPhotoId = group.id; feedbackTicks = 0; phase = 18; return;
                case 18:
                    if (!FeedbackAdvance(() => feedbackBabies.All(b => b.Spawned && BirthdayCarry.Holder(b) == null))) return;
                    FeedbackCheck(feedbackBabies.Select((b, i) => b.Position == feedbackBabyBeds[i].Position).All(ok => ok),
                        "after ordinary photograph both caregivers walk their babies back to their assigned safe beds");
                    FeedbackCheck(feedbackJob.Takes == 1, "returning caregivers do not create duplicate photographs");
                    FeedbackDisband();
                    GameDataSaveLoader.SaveGame("mf-feedback-two-babies-returned"); GameDataSaveLoader.LoadGame("mf-feedback-two-babies-returned");
                    Wait(1f); phase = 19; return;
                case 19:
                    FeedbackRefreshAfterLoad();
                    FeedbackCheck(PhotoStore.Instance.Get(feedbackPhotoId)?.HasFile == true && feedbackBabies.All(b => b.Spawned && BirthdayCarry.Holder(b) == null)
                        && feedbackAdults.All(p => p.GetLord() == null), "real load after ordinary baby return preserves the photograph, safe babies and released caregivers");
                    foreach (var p in feedbackAdults) FixReady(p);
                    feedbackGroupDialog = new Dialog_GroupPhotoSetup(feedbackSpot); Find.WindowStack.Add(feedbackGroupDialog);
                    feedbackGroupDialog.SelectGuests(2);
                    foreach (var baby in feedbackBabies)
                        if (!feedbackGroupDialog.ToggleGuest(baby)) throw new InvalidOperationException("cancellation fixture cannot select baby " + baby.LabelShort);
                    FeedbackCheck(feedbackGroupDialog.StartSelected(), "start ordinary two-baby cancellation before any photo");
                    feedbackJob = GroupPhoto.ActiveJob(map, feedbackSpot.Position); feedbackJob.EditingPhotoSetup = true;
                    FeedbackClose(); feedbackJob.EditingPhotoSetup = true; feedbackTicks = 0; phase = 20; return;
                case 20:
                    if (!FeedbackAdvance(() => feedbackBabies.All(b => BirthdayCarry.Holder(b) != null))) return;
                    FeedbackCheck(feedbackJob.Takes == 0, "both babies are actually held before cancelling the ordinary gathering");
                    feedbackJob.Cancel(); feedbackTicks = 0; phase = 21; return;
                case 21:
                    if (!FeedbackAdvance(() => feedbackBabies.All(b => b.Spawned && BirthdayCarry.Holder(b) == null))) return;
                    FeedbackCheck(feedbackJob.Finished && feedbackJob.Takes == 0
                        && feedbackBabies.Select((b, i) => b.Position == feedbackBabyBeds[i].Position).All(ok => ok),
                        "ordinary cancellation returns both carried babies to safe beds without producing a photograph");
                    FeedbackCheck(feedbackAdults.All(p => p.GetLord() == null), "ordinary baby cancellation releases all caregivers");
                    FeedbackFinish(); return;
            }
        }
    }
}
