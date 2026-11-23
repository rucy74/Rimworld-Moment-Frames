using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>Memory night (user, 2026-10-02: "추억의 밤 진행해", with "나중에 돌려주는 순간은 추억의 밤과 묶어"): the colony gathers in
    /// front of a slide projector and watches its film together. A party underneath (who joins, presence, calling off, the mood at the end
    /// all come from the game's party), except that people stand facing the picture instead of milling about, the memory is "memory night",
    /// and the evening is photographed after the selected audience has arrived and settled. Gathering time and watching time are separate.
    /// Started from the projector's button, or now and then in the
    /// evening by a colonist (the game's gathering starter); and the year's letter can open one on that year's photos.</summary>
    public static class MemoryNight
    {
        public const string Kind = "memorynight";
        public const int MinPhotos = 3;
        public const int DefaultWatchTicks = MemoryNightTiming.DefaultWatchTicks, SlideTicks = MemoryNightTiming.SlideTicks,
            ArrivalStableTicks = MemoryNightTiming.ArrivalStableTicks, GatherWaitTicks = MemoryNightTiming.GatherWaitTicks;

        public static GatheringDef Def => DefDatabase<GatheringDef>.GetNamedSilentFail("MF_MemoryNight");
        public static ThoughtDef Thought => DefDatabase<ThoughtDef>.GetNamed("MF_MemoryNight");

        /// <summary>The centre of the projected picture.</summary>
        public static Vector3 ScreenCenter(Building_PhotoProjector p)
        {
            p.ImageLayout(out Vector3 offset, out _, out _);
            return p.DrawPos + offset;
        }

        /// <summary>The picture's cells: what the audience faces.</summary>
        public static CellRect ScreenRect(Building_PhotoProjector p) => p.ProjectionRect;

        /// <summary>The side of the picture the projector stands on: where the audience prefers to sit.</summary>
        public static SpectateRectSide AudienceSide(Building_PhotoProjector p)
        {
            if (p.Rotation == Rot4.North) return SpectateRectSide.Down;
            if (p.Rotation == Rot4.South) return SpectateRectSide.Up;
            if (p.Rotation == Rot4.East) return SpectateRectSide.Left;
            return SpectateRectSide.Right;
        }

        /// <summary>Where the gathering is "at" (presence, joining): the open cell in front of the projector, else its own cell.</summary>
        public static IntVec3 GatherCell(Building_PhotoProjector p)
        {
            IntVec3 c = p.Position + p.Rotation.FacingCell;
            return c.InBounds(p.Map) && c.Standable(p.Map) ? c : p.Position;
        }

        /// <summary>Opens a memory night at this projector now (its button, the year's letter). False when nobody can host it.</summary>
        public static bool Start(Building_PhotoProjector p)
        {
            var def = Def;
            if (def == null || p == null || !p.Spawned) return false;
            GatheringWorker_MemoryNight.Asked = p;
            try { return def.Worker.TryExecute(p.Map); }
            finally { GatheringWorker_MemoryNight.Asked = null; }
        }

        internal static bool StartPrepared(Building_PhotoProjector p, IEnumerable<Pawn> selected, int watchTicks)
        {
            if (p == null || !p.Spawned || Def == null || CannotStart(p) != null) return false;
            var people = selected?.Where(x => GroupPhoto.CanJoin(x, null, true) && x.Map == p.Map
                && x.CanReach(GatherCell(p), PathEndMode.Touch, Danger.None)).Distinct().ToList();
            if (people == null || people.Count == 0) return false;
            Pawn host = people.OrderBy(x => x.Position.DistanceToSquared(p.Position)).First();
            foreach (var x in people)
            {
                PhotoGatheringAccess.Leave(x);
                if (x.jobs?.curJob != null) x.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
            }
            var job = new LordJob_MFMemoryNight(GatherCell(p), host, Def, p, people, true, watchTicks);
            LordMaker.MakeNewLord(Faction.OfPlayer, job, p.Map, people);
            Find.WindowStack.Add(new Dialog_MemoryNightProgress(job));
            return true;
        }

        internal static LordJob_MFMemoryNight Active(Building_PhotoProjector p) => p?.Map?.lordManager.lords
            .Select(l => l.LordJob as LordJob_MFMemoryNight).FirstOrDefault(j => j?.Projector == p);

        internal static void OpenPreparation(Building_PhotoProjector p, int year = 0)
        {
            if (p == null || !p.Spawned) return;
            var active = Active(p);
            if (active != null) Find.WindowStack.Add(new Dialog_MemoryNightProgress(active));
            else Find.WindowStack.Add(new Dialog_MemoryNightSetup(p, year));
        }

        /// <summary>Why this colonist cannot host a night here, or null. The game's own host test (awake, free, no other gathering, fit to
        /// gather) plus being able to walk to the projector.</summary>
        public static string HostProblem(Pawn x, IntVec3 spot)
        {
            if (x == null || !x.Spawned) return "not here";
            if (!x.RaceProps.Humanlike || x.IsSubhuman) return "not a person";
            if (x.Downed) return "downed";
            if (x.InBed() || !x.Awake()) return "asleep";
            if (x.InMentalState) return "mental state";
            var lord = x.GetLord();
            if (lord != null) return "in " + (lord.LordJob?.GetType().Name ?? "a lord");
            if (!GatheringsUtility.PawnCanStartOrContinueGathering(x)) return "unfit to gather";
            if (!x.CanReach(spot, PathEndMode.Touch, Danger.None)) return "can't reach";
            return null;
        }

        /// <summary>A reason the button is greyed out, or null.</summary>
        public static string CannotStart(Building_PhotoProjector p)
        {
            if (p.ReelCount < MinPhotos) return "MF_MemoryNightFewPhotos".Translate(MinPhotos);
            if (p.Map.lordManager.lords.Any(l => l.LordJob is LordJob_MFMemoryNight)) return "MF_MemoryNightAlready".Translate();
            return null;
        }

        internal static PhotoRecord Photograph(LordJob_MFMemoryNight job, IntVec3 spot, Action onDone)
        {
            var store = PhotoStore.Instance;
            var p = job.Projector;
            Map map = job.lord?.Map;
            if (store == null || p == null || map == null) return null;
            var people = job.Expected.Where(job.Arrived).ToList();
            if (people.Count == 0) return null;
            Hooks.AutoFrame(map, spot, people, Hooks.GatherMinSpan, Hooks.GatherMaxSpan, out Vector3 center, out float span, out float aspect,
                new[] { p.DrawPos, p.ProjectionRect.Min.ToVector3Shifted(), p.ProjectionRect.Max.ToVector3Shifted() });
            return store.RequestAutomaticNow(map, center, Kind, "memorynight:" + job.lord.loadID,
                "MF_MemoryNightTitle".Translate(job.FilmLabel), "", people, span, aspect, onDone);
        }
    }

    public class GatheringWorker_MemoryNight : GatheringWorker
    {
        /// <summary>The projector a player asked for (its button, the year's letter); otherwise the one with the longest film.</summary>
        internal static Building_PhotoProjector Asked;

        internal static Building_PhotoProjector ProjectorOn(Map map)
        {
            if (map == null) return null;
            if (Asked != null && Asked.Spawned && Asked.Map == map) return Asked;
            return map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoProjector>()
                .Where(p => p.ReelCount >= MemoryNight.MinPhotos).OrderByDescending(p => p.ReelCount).FirstOrDefault();
        }

        /// <summary>A colonist's own: in the evening only, with the setting on and a projector with a film.</summary>
        public override bool CanExecute(Map map, Pawn organizer = null)
        {
            if (Asked == null)
            {
                if (MomentFramesMod.Settings != null && !MomentFramesMod.Settings.memoryNight) return false;
                int hour = GenLocalDate.HourOfDay(map);
                if (hour < 18 || hour > 22) return false;
            }
            if (ProjectorOn(map) == null) return false;
            return base.CanExecute(map, organizer);
        }

        /// <summary>Asked for at a projector: the free colonist nearest to it proposes the night. Otherwise the game's random host.</summary>
        protected override Pawn FindOrganizer(Map map)
        {
            var p = Asked;
            if (p == null || !p.Spawned || p.Map != map) return base.FindOrganizer(map);
            IntVec3 spot = MemoryNight.GatherCell(p);
            return map.mapPawns.FreeColonistsSpawned.Where(x => MemoryNight.HostProblem(x, spot) == null)
                .OrderBy(x => x.Position.DistanceToSquared(spot)).FirstOrDefault();
        }

        protected override bool TryFindGatherSpot(Pawn organizer, out IntVec3 spot)
        {
            spot = IntVec3.Invalid;
            var p = ProjectorOn(organizer?.Map);
            if (p == null) return false;
            spot = MemoryNight.GatherCell(p);
            return organizer.CanReach(spot, PathEndMode.Touch, Danger.None);
        }

        protected override LordJob CreateLordJob(IntVec3 spot, Pawn organizer) => new LordJob_MFMemoryNight(spot, organizer, def, ProjectorOn(organizer.Map));
    }

    public class LordJob_MFMemoryNight : LordJob_Joinable_Party
    {
        private Building_PhotoProjector projector;
        private List<Pawn> expected = new List<Pawn>();
        private List<string> filmIds = new List<string>();
        private PhotoStore checkedFilmStore;
        private int checkedFilmVersion = -1;
        private bool filmAvailable;
        private string filmLabel = "";
        private bool manualSelection, timingVersion;
        private readonly MemoryNightTiming clock = new MemoryNightTiming();
        private int nextPhotoTry;
        private string photoId = "";
        internal bool Watching => clock.Watching;
        internal bool WaitingForDecision => clock.WaitingForDecision;
        internal bool Cancelled, PhotoSucceeded;
        internal int SlideSkip;
        private bool photoPending;
        private int photoQueuedAt;

        internal IReadOnlyList<Pawn> Expected => expected;
        internal IReadOnlyList<string> FilmIds => filmIds;
        internal bool HasFilmPhotos
        {
            get
            {
                var store = PhotoStore.Instance;
                if (store == null || filmIds == null || filmIds.Count == 0) return false;
                if (!ReferenceEquals(checkedFilmStore, store) || checkedFilmVersion != store.Version)
                {
                    checkedFilmStore = store; checkedFilmVersion = store.Version;
                    filmAvailable = filmIds.Any(id => store.Get(id)?.HasFile == true);
                }
                return filmAvailable;
            }
        }
        internal string FilmLabel => filmLabel;
        internal int WatchElapsed => clock.WatchElapsed;
        internal int ArrivedCount => expected.Count(Arrived);
        internal List<Pawn> Missing => expected.Where(p => !Arrived(p)).ToList();
        internal bool Finished => lord == null || !(lord.CurLordToil is LordToil_MFMemoryNight);
        public override int TicksLeft => clock.TicksLeft;
        internal int SlideIndex(int count) => clock.SlideIndex(count, SlideSkip);

        // Logical playback belongs to the gathering, never to a Unity source. This survives save/load and map changes.
        internal string MusicTrackKey = "";
        internal float MusicPosition;
        internal bool MusicStarted, MusicCompleted;

        // A start is consumed at the real gathering's Init, including starts outside the player's view.
        internal bool SfxStartConsumed;

        public LordJob_MFMemoryNight() { }

        public LordJob_MFMemoryNight(IntVec3 spot, Pawn organizer, GatheringDef def, Building_PhotoProjector projector) : base(spot, organizer, def)
        {
            this.projector = projector;
            durationTicks = MemoryNight.DefaultWatchTicks;
            timingVersion = true;
            expected = organizer.Map.mapPawns.FreeColonistsSpawned.Where(x => MemoryNight.HostProblem(x, spot) == null).ToList();
            if (!expected.Contains(organizer)) expected.Add(organizer);
            filmIds = projector.Reel.Select(r => r.id).ToList();
            filmLabel = projector.FilmLabel;
        }

        internal LordJob_MFMemoryNight(IntVec3 spot, Pawn organizer, GatheringDef def, Building_PhotoProjector projector,
            List<Pawn> people, bool manualSelection, int watchTicks) : this(spot, organizer, def, projector)
        {
            expected = people.ToList(); this.manualSelection = manualSelection;
            durationTicks = MemoryNightTiming.FitDuration(watchTicks); clock.Duration = durationTicks;
        }

        public Building_PhotoProjector Projector => projector;

        protected override ThoughtDef AttendeeThought => MemoryNight.Thought;
        protected override ThoughtDef OrganizerThought => MemoryNight.Thought;

        public override string GetReport(Pawn pawn) => (Watching ? "MF_NightWatching" : "MF_NightGathering").Translate(ArrivedCount, expected.Count);

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            var gathering = new LordToil_MFMemoryNight(spot, gatheringDef);
            var end = new LordToil_End(); graph.AddToil(gathering); graph.AddToil(end);
            var cancel = new Transition(gathering, end);
            cancel.AddTrigger(new Trigger_TickCondition(ShouldBeCalledOff));
            cancel.AddTrigger(new Trigger_PawnKilled());
            cancel.AddTrigger(new Trigger_PawnLost(PawnLostCondition.LeftVoluntarily, organizer));
            cancel.AddPreAction(new TransitionAction_Custom(() => ApplyNightOutcome(gathering)));
            cancel.AddPreAction(new TransitionAction_Message(gatheringDef.calledOffMessage, MessageTypeDefOf.NegativeEvent, new TargetInfo(spot, Map)));
            graph.AddTransition(cancel);
            var complete = new Transition(gathering, end);
            complete.AddTrigger(new Trigger_TickCondition(() => clock.Finished));
            complete.AddPreAction(new TransitionAction_Custom(() => ApplyNightOutcome(gathering)));
            complete.AddPreAction(new TransitionAction_Message(gatheringDef.finishedMessage, MessageTypeDefOf.SituationResolved, new TargetInfo(spot, Map)));
            graph.AddTransition(complete);
            return graph;
        }

        private void ApplyNightOutcome(LordToil_MFMemoryNight toil)
        {
            if (!Watching) return;
            foreach (var pawn in lord.ownedPawns)
            {
                if (!toil.Data.presentForTicks.TryGetValue(pawn, out int ticks) || ticks <= 0) continue;
                if (pawn.needs?.mood != null)
                {
                    var memory = (Thought_Memory)ThoughtMaker.MakeThought(MemoryNight.Thought);
                    memory.moodPowerFactor = Mathf.Min((float)ticks / durationTicks + .5f / MemoryNight.Thought.stages[0].baseMoodEffect, 1f);
                    pawn.needs.mood.thoughts.memories.TryGainMemory(memory);
                }
                TaleRecorder.RecordTale(pawn == organizer ? OrganizerTale : AttendeeTale, pawn, organizer);
            }
        }

        protected override bool IsInvited(Pawn pawn) => expected.Contains(pawn);
        public override float VoluntaryJoinPriorityFor(Pawn pawn)
        {
            if (!expected.Contains(pawn) || !GroupPhoto.CanJoin(pawn, this, manualSelection, false)
                || !pawn.CanReach(spot, PathEndMode.Touch, Danger.None)) return 0f;
            return VoluntarilyJoinableLordJobJoinPriorities.SocialGathering;
        }

        internal bool Arrived(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map != lord?.Map || pawn.Downed || pawn.GetLord() != lord
                || pawn.pather?.Moving == true || pawn.CurJob?.def != JobDefOf.SpectateCeremony) return false;
            // Count arrival at the actual reserved audience cell, not merely a broad radius around the projector.
            return pawn.CurJob.targetA.Cell == pawn.Position && !MemoryNight.ScreenRect(projector).Contains(pawn.Position);
        }

        internal void ContinueWaiting() => clock.ContinueWaiting();
        internal bool StartWithPresent()
        {
            if (Watching || Cancelled) return false;
            var present = expected.Where(Arrived).ToList();
            if (present.Count == 0) return false;
            if (!present.Contains(organizer)) organizer = present[0];
            foreach (var pawn in expected.Except(present).ToList())
                if (lord.ownedPawns.Contains(pawn)) lord.Notify_PawnLost(pawn, PawnLostCondition.ForcedToJoinOtherLord);
            expected = present;
            if (!clock.StartWithPresent(present.Count)) return false;
            BeginWatching(); return true;
        }
        private void BeginWatching()
        {
            EnsureAudience();
            var data = (lord.CurLordToil as LordToil_MFMemoryNight)?.Data;
            data?.presentForTicks.Clear();
        }

        internal void EnsureAudience()
        {
            // Lord references can be assigned after ExposeData. Legacy active gatherings recover their joined audience here.
            if (expected == null) expected = new List<Pawn>();
            if (expected.Count == 0 && lord != null) expected = lord.ownedPawns.Where(p => p != null).ToList();
            if (filmIds == null || filmIds.Count == 0) filmIds = projector?.Reel.Select(r => r.id).ToList() ?? new List<string>();
            if (filmLabel.NullOrEmpty()) filmLabel = projector?.FilmLabel ?? "";
        }

        internal void TickFlow()
        {
            if (Cancelled) return;
            EnsureAudience();
            if (!Watching)
            {
                // Nights nobody picked people for (an evening's own night, the year's letter) have no progress window to decide in, so
                // they drop people who died or left the map, and when the wait runs out they start with whoever came or are called off.
                // Before this, one colonist who never arrived kept everyone else standing at the screen until the host gave up (2026-11-01).
                if (!manualSelection) expected.RemoveAll(p => p == null || p.Dead || !p.Spawned || p.Map != lord?.Map);
                bool deciding = WaitingForDecision;
                if (clock.Tick(expected.Count > 0 && expected.All(Arrived))) BeginWatching();
                else if (!manualSelection && WaitingForDecision)
                {
                    if (StartWithPresent())
                        Messages.Message("MF_NightAutoStarted".Translate(expected.Count), projector, MessageTypeDefOf.NeutralEvent, false);
                    else Cancelled = true;
                }
                else if (!deciding && WaitingForDecision)
                {
                    Messages.Message("MF_NightWaitingDecision".Translate(ArrivedCount, expected.Count), projector,
                        MessageTypeDefOf.NeutralEvent, false);
                }
                return;
            }
            clock.Tick(expected.Count > 0 && expected.All(Arrived));
            if (photoPending)
            {
                var pending = PhotoStore.Instance?.Get(photoId);
                if (pending?.HasFile == true) { PhotoSucceeded = true; photoPending = false; }
                else if (clock.WatchElapsed - photoQueuedAt > GroupPhoto.WatchdogTicks)
                {
                    photoPending = false;
                    if (pending != null) PhotoStore.Instance.Remove(pending);
                    nextPhotoTry = clock.WatchElapsed + MemoryNight.SlideTicks;
                }
            }
            if (!clock.CanRequestPhoto(photoPending, PhotoSucceeded, nextPhotoTry, SettingsCapturePolicy.Automatic(MomentFramesMod.Settings), expected.All(Arrived))) return;
            nextPhotoTry = clock.WatchElapsed + MemoryNight.SlideTicks;
            try
            {
                var existing = PhotoStore.Instance?.Records.FirstOrDefault(r => r.eventKey == "memorynight:" + lord.loadID);
                if (existing?.HasFile == true) { photoId = existing.id; PhotoSucceeded = true; return; }
                if (existing != null) PhotoStore.Instance.Remove(existing);
                PhotoRecord rec = null;
                rec = MemoryNight.Photograph(this, spot, () =>
                {
                    photoPending = false;
                    if (rec?.HasFile == true) PhotoSucceeded = true;
                });
                if (rec != null) { photoId = rec.id; photoPending = true; photoQueuedAt = clock.WatchElapsed; }
            }
            catch (Exception e) { Hooks.Fail("memory night photo", e); }
        }

        protected override LordToil CreateGatheringToil(IntVec3 spot, Pawn organizer, GatheringDef gatheringDef) => new LordToil_MFMemoryNight(spot, gatheringDef);

        protected override bool ShouldBeCalledOff() => Cancelled || projector == null || !projector.Spawned || !HasFilmPhotos
            || (manualSelection ? !GroupPhoto.CanJoin(organizer, this, true, false)
                || !GatheringsUtility.AcceptableGameConditionsToContinueGathering(Map) : base.ShouldBeCalledOff());

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref projector, "mfProjector");
            Scribe_Collections.Look(ref expected, "mfNightExpected", LookMode.Reference);
            Scribe_Collections.Look(ref filmIds, "mfNightFilmIds", LookMode.Value);
            Scribe_Values.Look(ref filmLabel, "mfNightFilmLabel", "");
            Scribe_Values.Look(ref manualSelection, "mfNightManual", false);
            Scribe_Values.Look(ref timingVersion, "mfNightTimingVersion", false);
            Scribe_Values.Look(ref clock.GatherElapsed, "mfNightGatherElapsed", 0);
            Scribe_Values.Look(ref clock.ArrivalStable, "mfNightArrivalStable", 0);
            Scribe_Values.Look(ref clock.WatchElapsed, "mfNightWatchElapsed", 0);
            Scribe_Values.Look(ref clock.Watching, "mfNightWatching", false);
            Scribe_Values.Look(ref clock.WaitingForDecision, "mfNightWaitingDecision", false);
            Scribe_Values.Look(ref Cancelled, "mfNightCancelled", false);
            Scribe_Values.Look(ref SlideSkip, "mfNightSlideSkip", 0);
            Scribe_Values.Look(ref PhotoSucceeded, "mfNightPhotoSucceeded", false);
            Scribe_Values.Look(ref photoId, "mfNightPhotoId", "");
            Scribe_Values.Look(ref nextPhotoTry, "mfNightNextPhotoTry", 0);
            Scribe_Values.Look(ref MusicTrackKey, "mfMusicTrack", "");
            Scribe_Values.Look(ref MusicPosition, "mfMusicPosition", 0f);
            Scribe_Values.Look(ref MusicStarted, "mfMusicStarted", false);
            Scribe_Values.Look(ref MusicCompleted, "mfMusicCompleted", false);
            Scribe_Values.Look(ref SfxStartConsumed, "mfSfxStartConsumed", false);
            // Every loaded gathering already exists. Legacy saves lack this key and must also stay silent.
            if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit) SfxStartConsumed = true;
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                photoPending = false; clock.ArrivalStable = 0;
                if (!timingVersion) { durationTicks = MemoryNight.DefaultWatchTicks; timingVersion = true; }
                durationTicks = MemoryNightTiming.FitDuration(durationTicks); clock.Duration = durationTicks;
                clock.WatchElapsed = Mathf.Clamp(clock.WatchElapsed, 0, durationTicks);
                if (expected == null || expected.Count == 0) expected = lord?.ownedPawns.ToList() ?? new List<Pawn>();
                expected.RemoveAll(p => p == null);
                if (filmIds == null || filmIds.Count == 0) filmIds = projector?.Reel.Select(r => r.id).ToList() ?? new List<string>();
                PhotoSucceeded = PhotoStore.Instance?.Get(photoId)?.HasFile == true;
                if (MusicTrackKey == null) MusicTrackKey = "";
                if (float.IsNaN(MusicPosition) || float.IsInfinity(MusicPosition) || MusicPosition < 0f) MusicPosition = 0f;
            }
        }

        public override void Cleanup()
        {
            MemoryNightMusic.End(this);
            base.Cleanup();
        }

        public override void Notify_MapRemoved()
        {
            MemoryNightMusic.End(this);
            base.Notify_MapRemoved();
        }
    }

    /// <summary>The game's party toil (presence, a little social joy) with the audience facing the picture instead of the party duty.</summary>
    public class LordToil_MFMemoryNight : LordToil_Party
    {
        public LordToil_MFMemoryNight(IntVec3 spot, GatheringDef gatheringDef) : base(spot, gatheringDef) { }

        private Building_PhotoProjector Projector => (lord?.LordJob as LordJob_MFMemoryNight)?.Projector;

        public override void Init()
        {
            base.Init();
            if (lord?.LordJob is LordJob_MFMemoryNight job) { job.EnsureAudience(); MemoryNightSfx.BeginNight(job); }
        }

        public override void UpdateAllDuties()
        {
            var p = Projector;
            if (p == null || !p.Spawned) { base.UpdateAllDuties(); return; }
            CellRect screen = MemoryNight.ScreenRect(p);
            SpectateRectSide side = MemoryNight.AudienceSide(p);
            for (int i = 0; i < lord.ownedPawns.Count; i++)
                lord.ownedPawns[i].mindState.duty = new PawnDuty(gatheringDef.duty, spot)
                {
                    spectateRect = screen,
                    spectateRectAllowedSides = SpectateRectSide.All,
                    spectateRectPreferredSide = side
                };
        }

        public override void LordToilTick()
        {
            base.LordToilTick();
            if (lord.LordJob is LordJob_MFMemoryNight job) job.TickFlow();
        }

        public override void Cleanup()
        {
            if (lord?.LordJob is LordJob_MFMemoryNight job) MemoryNightMusic.End(job, job.TicksLeft <= 0);
            base.Cleanup();
        }
    }

    /// <summary>"The year in photos" (the "giving back" half of the same request): when the colony's year turns, a letter about the year that
    /// ended — how many photos, its big moments — that opens the album on that year or a memory night on that year's film. Once a year,
    /// only for a year with at least three photos.</summary>
    public static class YearInPhotos
    {
        internal static Letter_MFYear Check(PhotoStore store, int currentYear)
        {
            if (store == null || MomentFramesMod.Settings == null || !MomentFramesMod.Settings.yearLetter) return null;
            int year = currentYear - 1;
            if (!store.AddMark("yearphotos:" + year)) return null;
            var recs = store.Records.Where(r => r.HasFile && r.year == year && !Kinds.PersonalOnly(r.kind) && r.kind != Kinds.Relic).OrderBy(r => r.ticksAbs).ToList();
            if (recs.Count < MemoryNight.MinPhotos) return null;
            var big = recs.Where(r => Kinds.IsMilestone(r.kind)).Take(6).Select(r => "- " + r.DisplayTitle + " (" + r.dateLabel + ")").ToList();
            var letter = (Letter_MFYear)LetterMaker.MakeLetter("MF_YearLetterTitle".Translate(year),
                "MF_YearLetterText".Translate(year, recs.Count, big.Count > 0 ? string.Join("\n", big) : "MF_YearLetterNone".Translate().ToString()),
                DefDatabase<LetterDef>.GetNamed("MF_YearLetter"));
            letter.year = year;
            Find.LetterStack.ReceiveLetter(letter);
            return letter;
        }
    }

    public class Letter_MFYear : ChoiceLetter
    {
        public int year;

        // ChoiceLetter.OpenLetter is public virtual in the game's assembly. Keep the saved letter and its choices;
        // only its opened presentation changes to the same paper-and-prints language as the album.
        public override void OpenLetter() => Find.WindowStack.Add(new Dialog_MFYearPhotos(this));

        internal void OpenYearAlbum() => MainTabWindow_Album.OpenYear(year);

        internal Building_PhotoProjector NightProjector() =>
            GatheringWorker_MemoryNight.ProjectorOn(Find.AnyPlayerHomeMap)
            ?? Find.AnyPlayerHomeMap?.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoProjector>().FirstOrDefault();

        internal bool HoldNight(Building_PhotoProjector p)
        {
            if (p == null) return false;
            MemoryNight.OpenPreparation(p, year);
            return true;
        }

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                yield return new DiaOption("MF_YearLetterAlbum".Translate()) { action = OpenYearAlbum, resolveTree = true };
                var p = NightProjector();
                var night = new DiaOption("MF_YearLetterNight".Translate(year))
                {
                    action = () => { HoldNight(p); },
                    resolveTree = true
                };
                if (p == null) night.Disable("MF_YearLetterNoProjector".Translate());
                yield return night;
                yield return Option_Close;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref year, "year");
        }
    }
}
