using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    internal static class PetMilestones
    {
        internal const string Kind = "petjoined";
        internal const int PollTicks = 60, WaitTicks = 60000;
        internal static string Key(Pawn animal) => animal == null ? "" : Kind + ":" + animal.GetUniqueLoadID();

        internal static bool OwnedAnimal(Pawn animal) => animal?.RaceProps?.Animal == true
            && !animal.Dead && !animal.Destroyed && animal.Faction == Faction.OfPlayer
            && animal.HostFaction == null && !animal.IsQuestLodger();

        /// <summary>Ordinary pets and individually named or bonded livestock, without a photograph for every numbered pen animal.</summary>
        internal static bool EligibleAnimal(Pawn animal)
        {
            if (!OwnedAnimal(animal)) return false;
            if ((animal.RaceProps.petness > 0f && !animal.RaceProps.Roamer)
                || (animal.Name != null && !animal.Name.Numerical)) return true;
            if (ColonyPerson(animal.playerSettings?.Master)) return true;
            return animal.relations?.DirectRelations.Any(r => r.def == PawnRelationDefOf.Bond && ColonyPerson(r.otherPawn)) == true;
        }

        private static bool ColonyPerson(Pawn pawn) => pawn?.RaceProps?.Humanlike == true
            && !pawn.Dead && pawn.Faction == Faction.OfPlayer && pawn.HostFaction == null && !pawn.IsQuestLodger();

        internal static List<Pawn> Subjects(Pawn animal, Pawn recruiter)
        {
            var subjects = new List<Pawn> { animal };
            void Add(Pawn person)
            {
                if (ColonyPerson(person) && person.Spawned && person.Map == animal.Map
                    && person.Position.InHorDistOf(animal.Position, 4f) && !subjects.Contains(person)) subjects.Add(person);
            }
            Add(recruiter);
            Add(animal.playerSettings?.Master);
            if (animal.relations != null)
                foreach (var relation in animal.relations.DirectRelations)
                    if (relation.def == PawnRelationDefOf.Bond) Add(relation.otherPawn);
            return subjects;
        }
    }

    /// <summary>Only an observed change into the player faction can make a family-arrival photo. Loading or seeing an existing pet cannot.</summary>
    public sealed class GameComponent_PetMilestones : GameComponent
    {
        private sealed class Arrival : IExposable
        {
            internal Pawn animal, recruiter;
            internal int joinedTicksAbs, afterTick, deadlineTick;
            public Arrival() { }
            public void ExposeData()
            {
                Scribe_References.Look(ref animal, "animal");
                Scribe_References.Look(ref recruiter, "recruiter");
                Scribe_Values.Look(ref joinedTicksAbs, "joinedTicksAbs");
                Scribe_Values.Look(ref afterTick, "afterTick");
                Scribe_Values.Look(ref deadlineTick, "deadlineTick");
            }
        }

        private List<Arrival> pending = new List<Arrival>();
        private bool ready;
        internal int PendingCount => pending.Count;
        internal bool Ready => ready;

        public GameComponent_PetMilestones(Game game) { }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref pending, "pendingPetArrivals", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars) ready = false;
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (pending == null) pending = new List<Arrival>();
                pending.RemoveAll(a => a == null || a.animal == null);
            }
        }

        public override void FinalizeInit()
        {
            // This is a baseline, never backfill. Persisted new-arrival candidates keep waiting after a save/load.
            var store = PhotoStore.Instance;
            if (store != null)
                foreach (var animal in PawnsFinder.AllMapsWorldAndTemporary_Alive)
                    if (PetMilestones.OwnedAnimal(animal)) store.AddMark(PetMilestones.Key(animal));
            ready = true;
        }

        internal void RememberResident(Pawn animal)
        {
            if (PetMilestones.OwnedAnimal(animal)) PhotoStore.Instance?.AddMark(PetMilestones.Key(animal));
        }

        internal bool ObserveJoin(Pawn animal, Pawn recruiter = null)
        {
            var store = PhotoStore.Instance;
            if (!ready || !Hooks.Playing || Scribe.mode != LoadSaveMode.Inactive
                || store == null || !PetMilestones.OwnedAnimal(animal) || !store.AddMark(PetMilestones.Key(animal))) return false;
            // The first real arrival is remembered even when photos are off or the animal is not an automatic-photo candidate.
            // A later re-tame, purchase or setting change must not relabel that as its first day in the family.
            if (!SettingsCapturePolicy.Automatic(MomentFramesMod.Settings) || MomentFramesMod.Settings?.capturePetArrivals != true) return false;
            int now = Find.TickManager.TicksGame;
            pending.Add(new Arrival { animal = animal, recruiter = recruiter, joinedTicksAbs = Find.TickManager.TicksAbs,
                afterTick = now + PetMilestones.PollTicks, deadlineTick = now + PetMilestones.WaitTicks });
            return true;
        }

        public override void GameComponentTick()
        {
            if (!ready || pending.Count == 0 || !Hooks.Playing || Find.TickManager.TicksGame % PetMilestones.PollTicks != 0) return;
            try
            {
                var store = PhotoStore.Instance;
                if (store == null) return;
                int now = Find.TickManager.TicksGame;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var arrival = pending[i];
                    if (!PetMilestones.OwnedAnimal(arrival.animal) || now > arrival.deadlineTick
                        || !SettingsCapturePolicy.Automatic(MomentFramesMod.Settings) || MomentFramesMod.Settings?.capturePetArrivals != true)
                    { pending.RemoveAt(i); continue; }
                    if (now < arrival.afterTick) continue;
                    // SetFaction runs before orbital purchases land. Wait for an actual spawned animal rather than photograph its pod.
                    if (!arrival.animal.Spawned || arrival.animal.Map == null) continue;
                    pending.RemoveAt(i);
                    if (!PetMilestones.EligibleAnimal(arrival.animal)) continue;
                    var animal = arrival.animal;
                    var subjects = PetMilestones.Subjects(animal, arrival.recruiter);
                    Hooks.AutoFrame(animal.Map, animal.Position, subjects, 11f, 19f,
                        out Vector3 center, out float span, out float aspect);
                    var record = store.RequestAutomatic(animal.Map, center, PetMilestones.Kind, PetMilestones.Key(animal),
                        "MF_PetJoinedTitle".Translate(animal.LabelShort), PetPhotos.Caption(PetMilestones.Kind, subjects, animal.Map, animal, PetMilestones.Key(animal)), subjects, span, aspect);
                    if (record == null) continue;
                    record.requiredSubjects.Add(animal.GetUniqueLoadID());
                    record.eventSubjectIds.Add(animal.GetUniqueLoadID());
                    store.SetEventTime(record, arrival.joinedTicksAbs);
                    store.SetCaptureDeadline(record, arrival.deadlineTick);
                }
            }
            catch (Exception e) { Hooks.Fail("pet arrival", e); }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SetFaction), new[] { typeof(Faction), typeof(Pawn) })]
    internal static class Patch_PetFactionGain
    {
        private static void Prefix(Pawn __instance, out Faction __state) => __state = __instance?.Faction;
        private static void Postfix(Pawn __instance, Faction newFaction, Pawn recruiter, Faction __state)
        {
            try
            {
                if (__state == Faction.OfPlayer || newFaction != Faction.OfPlayer || !PetMilestones.OwnedAnimal(__instance)) return;
                Current.Game?.GetComponent<GameComponent_PetMilestones>()?.ObserveJoin(__instance, recruiter);
            }
            catch (Exception e) { Hooks.Fail("pet faction gain", e); }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup), new[] { typeof(Map), typeof(bool) })]
    internal static class Patch_PetResidentBaseline
    {
        private static void Postfix(Pawn __instance)
        {
            try { Current.Game?.GetComponent<GameComponent_PetMilestones>()?.RememberResident(__instance); }
            catch (Exception e) { Hooks.Fail("pet resident baseline", e); }
        }
    }
}
