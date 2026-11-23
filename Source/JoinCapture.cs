using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace MomentFrames
{
    /// <summary>Only a witnessed faction transition creates this evidence. References can survive a save while the pawn is still in a pod.</summary>
    public sealed class JoinCaptureState : GameComponent
    {
        internal sealed class Arrival : IExposable
        {
            internal Pawn Pawn, Recruiter;
            internal int ObservedTick = -1, Deadline = -1;
            internal string RecordId;
            internal int Attempts, NextAttemptTick;
            public Arrival() { }
            public void ExposeData()
            {
                Scribe_References.Look(ref Pawn, "pawn");
                Scribe_References.Look(ref Recruiter, "recruiter");
                Scribe_Values.Look(ref ObservedTick, "observedTick", -1);
                Scribe_Values.Look(ref Deadline, "deadline", -1);
                Scribe_Values.Look(ref RecordId, "recordId");
                Scribe_Values.Look(ref Attempts, "attempts");
                Scribe_Values.Look(ref NextAttemptTick, "nextAttemptTick");
            }
        }
        internal List<Arrival> Pending = new List<Arrival>();
        public JoinCaptureState(Game game) { }
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref Pending, "witnessedJoinRequests", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Pending ??= new List<Arrival>();
                var known = new HashSet<string>(StringComparer.Ordinal);
                Pending.RemoveAll(a => a?.Pawn == null
                    || !RecordEventPolicy.KeepJoinEvidence(known, a.Pawn.GetUniqueLoadID(), a.ObservedTick, a.Deadline, Find.TickManager.TicksGame));
            }
        }
    }

    /// <summary>A real faction transition may occur before the new colonist leaves an arrival pod. Wait for spawn; never infer it from load.</summary>
    internal static class JoinCapture
    {
        internal const int MaxAttempts = 2, RetryTicks = 250, ArrivalLifetime = 60000;
        private static JoinCaptureState State => Current.Game?.GetComponent<JoinCaptureState>();
        internal static void Completed(Pawn pawn, Pawn recruiter)
        {
            if (!Hooks.Playing || Scribe.mode != LoadSaveMode.Inactive || pawn?.RaceProps == null || !pawn.RaceProps.Humanlike
                || pawn.Faction != Faction.OfPlayer || PhotoStore.Instance == null) return;
            if (!Hooks.AutomaticCapture || MomentFramesMod.Settings?.msRecruit != true)
            {
                if (Prefs.DevMode) Log.Message("[MomentFrames] recruit " + pawn.GetUniqueLoadID()
                    + ": witnessed faction change ignored because automatic or recruitment photography is disabled");
                return;
            }
            var store = PhotoStore.Instance;
            string id = pawn.GetUniqueLoadID();
            if (store.InitialColonists?.Contains(pawn) == true || store.HasMark("life-origin:" + id)
                || store.HasMark("recruit:captured:" + id)
                || store.AllRecords.Any(r => r.kind == "recruit" && r.eventKey == "recruit:" + id)) return;
            var state = State;
            if (state == null) return;
            var arrival = state.Pending.FirstOrDefault(a => a.Pawn == pawn);
            if (arrival == null)
            {
                int tick = Find.TickManager.TicksGame;
                arrival = new JoinCaptureState.Arrival { Pawn = pawn, ObservedTick = tick, Deadline = tick + ArrivalLifetime };
                state.Pending.Add(arrival);
                Trace(arrival, "witnessed faction change");
            }
            if (recruiter != null) arrival.Recruiter = recruiter;
        }
        internal static void Poll(PhotoStore store, int tick)
        {
            var state = State;
            if (store == null || state == null || state.Pending.Count == 0) return;
            foreach (var arrival in state.Pending.ToList())
            {
                Pawn pawn = arrival.Pawn;
                var record = store.GetAny(arrival.RecordId);
                // Continuations are not serialized. On load, the saved arrival still owns
                // the queued record and observes its real result on the next poll.
                if (record?.HasFile == true) { Complete(store, state, arrival, record, tick); continue; }
                if (pawn == null || pawn.Destroyed || pawn.Dead || pawn.Faction != Faction.OfPlayer
                    || !RecordEventPolicy.PendingJoinEvidence(arrival.ObservedTick, arrival.Deadline, tick))
                { Stop(store, state, arrival, "pawn unavailable or arrival expired"); continue; }
                string id = pawn.GetUniqueLoadID();
                // A birth/adoption hook can classify the pawn after SetFaction has already returned.
                if (store.InitialColonists?.Contains(pawn) == true || store.HasMark("life-origin:" + id)
                    || store.HasMark("recruit:captured:" + id))
                { Stop(store, state, arrival, "already recorded or classified as an initial colonist/birth"); continue; }
                var settings = MomentFramesMod.Settings;
                if (!Hooks.AutomaticCapture || settings?.msRecruit != true)
                { Stop(store, state, arrival, "automatic or recruitment photography disabled"); continue; }
                if (record != null)
                {
                    if (record.eventOnly) Stop(store, state, arrival, "capture retained as an event-only record");
                    continue;
                }
                // A restored record from an older build may have no request id in its
                // witnessed arrival. Adopt it rather than queue a second photograph.
                var existing = store.AllRecords.FirstOrDefault(r => r.kind == "recruit" && r.eventKey == "recruit:" + id);
                if (existing != null)
                {
                    arrival.RecordId = existing.id;
                    arrival.Attempts = Math.Max(1, arrival.Attempts);
                    if (existing.HasFile) Complete(store, state, arrival, existing, tick);
                    else if (existing.eventOnly) Stop(store, state, arrival, "existing event-only record");
                    continue;
                }
                if (arrival.Attempts >= MaxAttempts)
                { Stop(store, state, arrival, "capture failed twice"); continue; }
                if (tick < arrival.NextAttemptTick) continue;
                if (!pawn.Spawned || !pawn.IsFreeColonist || pawn.IsQuestLodger()) continue;
                var actors = new List<Pawn> { pawn };
                if (arrival.Recruiter?.Spawned == true && arrival.Recruiter.Map == pawn.Map) actors.Add(arrival.Recruiter);
                arrival.Attempts++;
                arrival.NextAttemptTick = tick + RetryTicks;
                PhotoRecord photo = null;
                try
                {
                    photo = store.RequestAutomatic(pawn.Map, pawn.Position, "recruit", "recruit:" + id,
                        "MF_Recruit".Translate(pawn.LabelShort), null, actors,
                        onDone: () => Complete(store, state, arrival, photo, Find.TickManager.TicksGame));
                    if (photo == null) { Trace(arrival, "request rejected; retry remains bounded"); continue; }
                    photo.requiredPerson = id;
                    photo.captureDeadlineTick = arrival.Deadline;
                    arrival.RecordId = photo.id;
                    Trace(arrival, "queued attempt " + arrival.Attempts);
                }
                catch (Exception e) { Hooks.Fail("recruitment photograph request", e); }
            }
        }

        private static void Complete(PhotoStore store, JoinCaptureState state, JoinCaptureState.Arrival arrival, PhotoRecord photo, int tick)
        {
            if (!ReferenceEquals(PhotoStore.Instance, store) || !state.Pending.Contains(arrival)) return;
            if (photo != null && !string.Equals(arrival.RecordId, photo.id, StringComparison.Ordinal)) return;
            if (photo?.HasFile == true && store.AllRecords.Contains(photo))
            {
                string id = arrival.Pawn?.GetUniqueLoadID() ?? photo.requiredPerson;
                if (!id.NullOrEmpty()) store.AddMark("recruit:captured:" + id);
                state.Pending.Remove(arrival);
                Trace(arrival, "photo saved");
                return;
            }
            if (photo?.captureCancelled == true || photo?.eventOnly == true)
            { Stop(store, state, arrival, "capture cancelled or retained as an event-only record"); return; }
            arrival.RecordId = null;
            arrival.NextAttemptTick = tick + RetryTicks;
            if (arrival.Attempts >= MaxAttempts) Stop(store, state, arrival, "capture failed twice");
            else Trace(arrival, "capture failed; waiting before one retry");
        }

        private static void Stop(PhotoStore store, JoinCaptureState state, JoinCaptureState.Arrival arrival, string reason)
        {
            // Remove ownership before cancellation calls its continuation.
            state.Pending.Remove(arrival);
            if (!arrival.RecordId.NullOrEmpty()) store.CancelPending(arrival.RecordId);
            Trace(arrival, reason);
        }

        private static void Trace(JoinCaptureState.Arrival arrival, string message)
        {
            if (Prefs.DevMode) Log.Message("[MomentFrames] recruit " + (arrival.Pawn?.GetUniqueLoadID() ?? "missing")
                + " observed=" + arrival.ObservedTick + " attempt=" + arrival.Attempts + ": " + message);
        }
    }
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SetFaction), new[] { typeof(Faction), typeof(Pawn) })]
    internal static class Patch_MFJoinedFaction
    {
        private static void Prefix(Pawn __instance, out bool __state)
        {
            __state = true;
            try { __state = __instance?.Faction == Faction.OfPlayer; }
            catch (Exception e) { Hooks.Fail("joining faction baseline", e); }
        }
        private static void Postfix(Pawn __instance, Faction newFaction, Pawn recruiter, bool __state)
        {
            try
            {
                if (RecordEventPolicy.NewPlayerFaction(__state, __instance?.Faction == Faction.OfPlayer,
                    Scribe.mode != LoadSaveMode.Inactive, Hooks.Playing)) JoinCapture.Completed(__instance, recruiter);
            }
            catch (Exception e) { Hooks.Fail("joining faction", e); }
        }
    }
}
