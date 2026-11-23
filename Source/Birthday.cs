using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>Birthday photos. On a colonist's birthday a letter asks whether to take a picture and where: where they are, at the photo spot, or
    /// a place picked on the map (the people they are close to are called along) — or, if the player chose so, the picture is taken at once where
    /// they are. These photos belong to the person: their own chronicle tab shows them, the album only when it is filtered to that person or to
    /// birthdays (a colony of fifteen has a birthday every four days).</summary>
    public static class Birthday
    {
        public const int ModeOff = 0, ModeAsk = 1, ModeAuto = 2;
        public const float HereMinSpan = 9f, HereMaxSpan = 19f;
        public const float NearbyCells = 6f;      // "where they are": whoever stands this close is in the picture too
        public const int FriendOpinion = 20, MaxFriends = 6;
        public const int LetterTicks = 60000;     // the letter waits a day

        public const int MinEvery = 1, MaxEvery = 10;

        /// <summary>Every Nth birthday (the player's interval, 1 = every year), and always the one that makes them an adult.</summary>
        public static bool Counts(Pawn p, int age)
        {
            int every = Mathf.Clamp(MomentFramesMod.Settings.birthdayEvery, MinEvery, MaxEvery);
            return age % every == 0 || age == Mathf.RoundToInt(p.ageTracker.AdultMinAge);
        }

        public static string Key(Pawn p, int age) => "birthday:" + p.GetUniqueLoadID() + ":" + age;
        public static string Title(Pawn p, int age) => "MF_BirthdayTitle".Translate(p.LabelShort, age);

        internal static bool FixedSubject(Pawn p) => p != null && (p.DevelopmentalStage.Baby() || p.Downed);
        internal static LordJob_MFGroupPhoto Active(Pawn p) => p?.MapHeld?.lordManager.lords.Select(l => l.LordJob)
            .OfType<LordJob_MFGroupPhoto>().FirstOrDefault(j => !j.Finished && j.BirthdayOf == p.GetUniqueLoadID());

        public static void OpenSetup(Pawn p, int age, LordJob_MFGroupPhoto editing = null, IntVec3? place = null)
        {
            if (p == null || p.Dead || !BirthdayCarry.Available(p) || !p.IsFreeColonist) return;
            editing = editing ?? Active(p);
            if (editing?.Shooting == true) return;
            Find.WindowStack.Add(new Dialog_BirthdaySetup(p, age, editing, place));
        }

        internal static void CompleteLetter(Pawn p, int age)
        {
            foreach (var letter in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_MFBirthday>().Where(l => l.pawn == p && l.age == age).ToList())
                Find.LetterStack.RemoveLetter(letter);
        }

        internal static bool StartPrepared(Pawn p, int age, IntVec3 place, IList<Pawn> selected, Rot4 direction, LordJob_MFGroupPhoto editing, Pawn guardian = null, bool requireGuardian = false, int reactionMode = -1, bool peopleFocus = false, IDictionary<string,int> reactions=null, bool useSavedFrame=true, IDictionary<string,int> faces=null, IDictionary<string,IntVec3> positions=null)
        {
            var plan = BirthdayPreparationPlan.Create(p, place, selected, direction, editing, guardian, requireGuardian, positions);
            if (!plan.CanStart)
            { Messages.Message(plan.Reason, MessageTypeDefOf.RejectInput, false); return false; }
            Map map = plan.Map;
            place = plan.Place;
            direction = Building_PhotoStudio.At(map,place)?.Rotation ?? direction;
            bool fixedSubject = FixedSubject(p);
            var moving = plan.Moving;
            var cells = plan.Cells;
            if (editing != null)
            {
                if (!editing.ReplaceBirthdayRoster(place, moving, cells, direction, guardian)) return false;
                editing.SetPreparationOptions(moving,reactionMode,peopleFocus,reactions,useSavedFrame,faces); return true;
            }
            if (fixedSubject && moving.Count == 0)
            {
                var solo = ShootHere(p, age, true, true);
                if(solo!=null)
                {
                    solo.captureReactionMode = reactionMode;
                    if(faces!=null){solo.facePawnIds=faces.Keys.OrderBy(id=>id).ToList();solo.faceChoices=solo.facePawnIds.Select(id=>PhotoFaces.Choice(faces,id)).ToList();}
                    if(reactions!=null)
                    {solo.reactionPawnIds=reactions.Keys.OrderBy(id=>id).ToList();solo.reactionIcons=solo.reactionPawnIds.Select(id=>reactions[id]).ToList();}
                }
                return solo != null;
            }
            var original = PhotoStore.Instance.AllRecords.FirstOrDefault(r => r.kind == Kinds.Birthday && r.eventKey == Key(p, age) && r.HasFile);
            var job = new LordJob_MFGroupPhoto(place, moving, cells);
            job.MakeBirthday(p, age, fixedSubject, direction, original?.id, guardian);
            job.SetPreparationOptions(moving,reactionMode,peopleFocus,reactions,useSavedFrame,faces);
            foreach (var member in moving) member.jobs?.EndCurrentJob(JobCondition.InterruptForced, false);
            foreach (var participant in moving) PhotoGatheringAccess.Leave(participant);
            LordMaker.MakeNewLord(Faction.OfPlayer, job, map, moving);
            GroupPhoto.ForgetEligible();
            Messages.Message("MF_GroupStarted".Translate(selected.Count), p, MessageTypeDefOf.NeutralEvent, false);
            return true;
        }

        public static void OnBirthday(Pawn p, int age)
        {
            var s = MomentFramesMod.Settings;
            var store = PhotoStore.Instance;
            if (!SettingsCapturePolicy.Automatic(s) || store == null || s.birthdayMode == ModeOff || !Hooks.Playing) return;
            if (p == null || p.Dead || !p.Spawned || !p.IsFreeColonist || p.Map == null || !p.Map.IsPlayerHome) return;
            if (!Counts(p, age) || store.HasKey(Key(p, age))) return;
            if (s.birthdayMode == ModeAuto) { ShootHereCore(p, age, false, false, true); return; }
            SendLetter(p, age);
        }

        /// <summary>Still possible when the letter is answered: alive, here, not photographed yet.</summary>
        public static bool Possible(Pawn p, int age, out string why)
        {
            why = null;
            if (p == null || p.Dead || !p.Spawned || p.Map == null) { why = "MF_BirthdayGone".Translate(p?.LabelShort ?? "?"); return false; }
            if (PhotoStore.Instance?.HasKey(Key(p, age)) == true) { why = "MF_BirthdayDone".Translate(); return false; }
            return true;
        }

        /// <summary>The people they are close to: family by blood, a partner, or anyone who thinks well of them; only those free to come.</summary>
        public static List<Pawn> Friends(Pawn p)
        {
            if (!MomentFramesMod.Settings.birthdayFriends || p?.MapHeld == null) return new List<Pawn>();
            return p.MapHeld.mapPawns.FreeColonistsSpawned
                .Where(o => o != p && GroupPhoto.CanJoin(o) && o.relations != null
                    && (o.relations.FamilyByBlood.Contains(p) || LovePartnerRelationUtility.LovePartnerRelationExists(o, p) || o.relations.OpinionOf(p) >= FriendOpinion))
                .OrderByDescending(o => o.relations.OpinionOf(p))
                .Take(MaxFriends)
                .ToList();
        }

        public static Building_PhotoSpot SpotFor(Pawn p) => p?.MapHeld?.GetComponent<PhotoLocationMapComponent>()?.BirthdaySpot;

        public static string Desc(Pawn p, IEnumerable<Pawn> people, Map map)
        {
            var ordered = new List<Pawn> { p };
            ordered.AddRange(people.Where(o => o != p));
            return PhotoDesc.Pick(Kinds.Birthday, ordered, map);
        }

        /// <summary>A picture of them where they are now, with whoever stands close.</summary>
        public static PhotoRecord ShootHere(Pawn p, int age, bool allowRetake = false, bool onlySelf = false)
            => ShootHereCore(p, age, allowRetake, onlySelf, false);

        private static PhotoRecord ShootHereCore(Pawn p, int age, bool allowRetake, bool onlySelf, bool automatic)
        {
            var store = PhotoStore.Instance;
            if (store == null || p == null || !p.Spawned || p.Dead || !p.IsFreeColonist || (!allowRetake && !Possible(p, age, out _)) || Active(p) != null) return null;
            var people = new List<Pawn> { p };
            if (!onlySelf) people.AddRange(p.Map.mapPawns.FreeColonistsSpawned.Where(o => o != p && o.Position.InHorDistOf(p.Position, NearbyCells)));
            Hooks.AutoFrame(p.Map, p.Position, people, HereMinSpan, HereMaxSpan, out Vector3 center, out float span, out float aspect);
            var original = store.AllRecords.FirstOrDefault(r => r.eventKey == Key(p, age) && r.HasFile);
            string key = Key(p, age) + (original != null ? ":retake:" + Guid.NewGuid().ToString("N") : "");
            PhotoRecord record = null;
            Action onDone = () => { if (record?.HasFile == true) CompleteLetter(p, age); };
            record = automatic ? store.RequestAutomatic(p.Map, center, Kinds.Birthday, key, Title(p, age), null, people, span, aspect, onDone)
                : store.Request(p.Map, center, Kinds.Birthday, key, Title(p, age), null, people, span, aspect, onDone);
            if (record != null) { record.requiredPerson = p.GetUniqueLoadID(); record.eventAge = age; record.reshootOf = original?.id; }
            return record;
        }

        /// <summary>Calls them (and the people they are close to) to stand behind a cell — the photo spot, or a place picked on the map — the way a
        /// group photo lines up; the picture is theirs. Null when they cannot come now (the reason is shown).</summary>
        public static Lord StartLineUp(Pawn p, int age, IntVec3 cell)
        {
            var guests = new List<Pawn> { p }; guests.AddRange(Friends(p));
            return StartPrepared(p, age, cell, guests, Rot4.North, null) ? Active(p)?.lord : null;
        }

        /// <summary>The player clicks where the camera stands; the line-up it would make is drawn under the mouse.</summary>
        public static void PickPlace(Pawn p, int age)
        {
            if (p == null || p.Dead || !p.Spawned || !p.IsFreeColonist) return;
            Map map = p.Map;
            int count = (FixedSubject(p) ? 0 : 1) + Friends(p).Count;
            CameraJumper.TryJump(p);
            Messages.Message("MF_BirthdayPickPrompt".Translate(), MessageTypeDefOf.NeutralEvent, false);
            var parms = new TargetingParameters { canTargetLocations = true, canTargetPawns = false, canTargetBuildings = false, canTargetItems = false };
            Find.Targeter.BeginTargeting(parms, t => OpenSetup(p, age, null, t.Cell), null,
                t => t.IsValid && t.Cell.InBounds(map) && t.Cell.Standable(map), null, null, MFIcons.Camera, true, null,
                t => { if (t.IsValid && t.Cell.InBounds(map)) GenDraw.DrawFieldEdges(GroupFormation.CachedCells(t.Cell, count, map), Color.white); });
        }

        public static void SendLetter(Pawn p, int age)
        {
            var def = DefDatabase<LetterDef>.GetNamedSilentFail("MF_BirthdayLetter");
            if (def == null) return;
            var letter = (ChoiceLetter_MFBirthday)LetterMaker.MakeLetter(def);
            letter.pawn = p;
            letter.age = age;
            letter.Label = "MF_BirthdayLetterLabel".Translate(p.LabelShort);
            letter.title = Title(p, age);
            letter.Text = CaptionText.Clean("MF_BirthdayLetterText".Translate(p.LabelShort, age));
            letter.lookTargets = p;
            letter.StartTimeout(LetterTicks);
            Find.LetterStack.ReceiveLetter(letter);
        }
    }

    /// <summary>"It is Victoria's birthday — take a photo?" with the three places and "not this time". Waits a day.</summary>
    public class ChoiceLetter_MFBirthday : ChoiceLetter
    {
        public Pawn pawn;
        public int age;

        /// <summary>How far the deadline moves while it waits: a real second at normal speed, so the dialog opens within a second of the
        /// danger ending and the danger check runs once a second, not every tick.</summary>
        public const int WaitStepTicks = 60;

        /// <summary>The game opens a timed choice letter by itself on its last tick (LetterWithTimeout), as a dialog that holds the game.
        /// During a raid or a fight (PhotoFx.Busy) that is put off: the deadline moves on, so the letter stays and opens once the danger
        /// is over (2026-11-17, user decision "습격이 끝나면 열기" — 2.5.2 opened it in the middle of raids). It is already the day after
        /// the birthday either way; the photo keeps the age it was asked for.</summary>
        public override bool ShouldAutomaticallyOpenLetter
        {
            get
            {
                if (!base.ShouldAutomaticallyOpenLetter) return false;
                if (!PhotoFx.Busy()) return true;
                disappearAtTick = Find.TickManager.TicksGame + WaitStepTicks;
                return false;
            }
        }

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                bool ok = pawn != null && !pawn.Dead && pawn.Spawned && pawn.IsFreeColonist;
                string why = "MF_BirthdayGone".Translate(pawn?.LabelShort ?? "?");
                var here = new DiaOption("MF_BirthdayHere".Translate())
                {
                    action = () => Birthday.OpenSetup(pawn, age, null, pawn.Position),
                    resolveTree = true
                };
                if (!ok) here.Disable(why);
                yield return here;
                var spot = ok ? Birthday.SpotFor(pawn) : null;
                var atSpot = new DiaOption("MF_BirthdaySpot".Translate())
                {
                    action = () => Birthday.OpenSetup(pawn, age, null, spot.Position),
                    resolveTree = true
                };
                if (!ok) atSpot.Disable(why);
                else if (spot == null) atSpot.Disable("MF_BirthdayNoSpot".Translate());
                yield return atSpot;
                var pick = new DiaOption("MF_BirthdayPick".Translate())
                {
                    action = () => Birthday.PickPlace(pawn, age),
                    resolveTree = true
                };
                if (!ok) pick.Disable(why);
                yield return pick;
                if (ArchivedOnly) { yield return Option_Close; yield break; }
                yield return new DiaOption("MF_BirthdaySkip".Translate())
                {
                    action = () => Find.LetterStack.RemoveLetter(this),
                    resolveTree = true
                };
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref age, "age", 0);
        }
    }

    /// <summary>Every biological birthday passes through here (the game's own letters and growth moments come from the same method).</summary>
    [HarmonyPatch(typeof(Pawn_AgeTracker), "BirthdayBiological")]
    internal static class Patch_Birthday
    {
        private static void Postfix(Pawn ___pawn, int birthdayAge)
        {
            try { Birthday.OnBirthday(___pawn, birthdayAge); }
            catch (Exception e) { Log.Warning("[MomentFrames] birthday hook failed: " + e.Message); }
        }
    }
}
