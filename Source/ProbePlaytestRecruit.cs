using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static void RecruitCheck(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("playtest recruitment: " + label);
            checks.Add("PASS: " + label);
        }

        // Uses the real SetFaction hook and queue/store paths on the marked probe profile.
        // The image result is deliberately synthetic: these checks prove ownership,
        // failure/retry and file-commit semantics, not the visual quality of a capture.
        private static void PlaytestRecruitChecks()
        {
            if (!active || PhotoStore.Instance == null || Find.CurrentMap == null)
                throw new InvalidOperationException("recruitment checks require the marked disposable probe profile");
            var store = PhotoStore.Instance;
            var state = Current.Game.GetComponent<JoinCaptureState>();
            RecruitCheck(state != null && state.Pending.Count == 0, "recruitment fixture has no unrelated witnessed arrival");
            var settings = MomentFramesMod.Settings;
            bool wasAuto = settings.autoCaptureEnabled, wasRecruit = settings.msRecruit;
            var people = new List<Pawn>();
            int tick = Find.TickManager.TicksGame;
            Pawn NewJoin()
            {
                var p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfAncients);
                people.Add(p);
                GenSpawn.Spawn(p, CellFinder.StandableCellNear(Find.CurrentMap.Center, Find.CurrentMap, 8f), Find.CurrentMap);
                p.SetFaction(Faction.OfPlayer);
                return p;
            }
            PhotoRecord Record(Pawn p) => store.Records.FirstOrDefault(r => r.eventKey == "recruit:" + p.GetUniqueLoadID());
            bool Marked(Pawn p) => store.HasMark("recruit:captured:" + p.GetUniqueLoadID());
            void Fail(PhotoRecord rec)
            {
                // Null image goes through the real validation/drop path. Remove then
                // drains the queued continuation as the capture wrapper normally does.
                AccessTools.Method(typeof(PhotoStore), "OnCaptured").Invoke(store, new object[] { rec, null });
                store.Remove(rec);
            }
            try
            {
                settings.autoCaptureEnabled = settings.msRecruit = true;
                store.AddMark("recruit:first"); // Legacy first-ever marker must not block a new person.
                var p = NewJoin();
                var arrival = state.Pending.Single(a => a.Pawn == p);
                RecruitCheck(arrival.ObservedTick == tick, "real SetFaction creates witnessed arrival at the transition tick");
                JoinCapture.Poll(store, tick);
                var first = Record(p);
                RecruitCheck(first != null && first.requiredPerson == p.GetUniqueLoadID()
                    && arrival.Attempts == 1 && !Marked(p), "new person queues despite legacy first marker; request is not success");
                JoinCapture.Poll(store, tick);
                RecruitCheck(Record(p) == first && arrival.Attempts == 1, "pending request is not duplicated by another poll");
                Fail(first);
                RecruitCheck(!Marked(p) && Record(p) == null && state.Pending.Contains(arrival),
                    "failed image leaves witnessed arrival retryable without consuming recruitment");
                JoinCapture.Poll(store, tick);
                RecruitCheck(Record(p) == null, "failure retry waits instead of immediately spamming another capture");
                JoinCapture.Poll(store, tick + JoinCapture.RetryTicks);
                var retry = Record(p);
                RecruitCheck(retry != null && retry.id != first.id && arrival.Attempts == 2 && !Marked(p),
                    "one bounded retry creates a fresh request");
                var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6ZQAAAABJRU5ErkJggg==");
                var result = new PhotoCapture.Result { png = png, thumb = png,
                    capturedPeopleIds = new List<string> { p.GetUniqueLoadID() }, capturedPeopleNames = new List<string> { p.LabelShort } };
                AccessTools.Method(typeof(PhotoStore), "OnCaptured").Invoke(store, new object[] { retry, result });
                RecruitCheck(retry.HasFile && File.Exists(store.OriginalPath(retry)) && !Marked(p),
                    "actual original file commit precedes the recruitment completion marker");
                JoinCapture.Poll(store, tick + JoinCapture.RetryTicks);
                RecruitCheck(Marked(p) && !state.Pending.Contains(arrival),
                    "poll observes completed file without a runtime callback, as after restoring a queued record");
                JoinCapture.Completed(p, null);
                RecruitCheck(!state.Pending.Any(a => a.Pawn == p), "completed person's hook cannot duplicate the saved recruitment");

                var failed = NewJoin();
                JoinCapture.Poll(store, tick);
                Fail(Record(failed));
                JoinCapture.Poll(store, tick + JoinCapture.RetryTicks);
                Fail(Record(failed));
                JoinCapture.Poll(store, tick + JoinCapture.RetryTicks * 2);
                RecruitCheck(!state.Pending.Any(a => a.Pawn == failed) && Record(failed) == null && !Marked(failed),
                    "two image failures stop without falsely recording success or retrying forever");

                var cancelled = NewJoin();
                JoinCapture.Poll(store, tick);
                RecruitCheck(store.CancelPending(Record(cancelled).id), "explicit cancellation reaches real capture queue");
                JoinCapture.Poll(store, tick + JoinCapture.RetryTicks);
                RecruitCheck(!state.Pending.Any(a => a.Pawn == cancelled) && Record(cancelled) == null && !Marked(cancelled),
                    "explicit cancellation is not treated as a retryable render failure");

                var disabled = NewJoin();
                settings.msRecruit = false;
                JoinCapture.Poll(store, tick);
                settings.msRecruit = true;
                JoinCapture.Poll(store, tick);
                RecruitCheck(!state.Pending.Any(a => a.Pawn == disabled) && Record(disabled) == null,
                    "recruitment OFF discards pending event and re-enable does not backfill it");
                settings.autoCaptureEnabled = false;
                var off = NewJoin();
                settings.autoCaptureEnabled = true;
                JoinCapture.Poll(store, tick);
                RecruitCheck(!state.Pending.Any(a => a.Pawn == off) && Record(off) == null,
                    "automatic capture OFF records no witnessed request to backfill on re-enable");

                var born = NewJoin();
                store.AddMark("life-origin:" + born.GetUniqueLoadID());
                JoinCapture.Poll(store, tick);
                RecruitCheck(!state.Pending.Any(a => a.Pawn == born) && Record(born) == null,
                    "late birth/adoption classification suppresses the SetFaction recruitment candidate");
                RecruitCheck(!RecordEventPolicy.NewPlayerFaction(false, true, true, true),
                    "a loaded player faction remains insufficient evidence of a new recruitment");
            }
            finally
            {
                foreach (var p in people)
                {
                    foreach (var a in state.Pending.Where(a => a.Pawn == p).ToList()) state.Pending.Remove(a);
                    foreach (var rec in store.Records.Where(r => r.eventKey == "recruit:" + p.GetUniqueLoadID()).ToList()) store.Remove(rec);
                    if (!p.Destroyed) p.Destroy();
                }
                settings.autoCaptureEnabled = wasAuto;
                settings.msRecruit = wasRecruit;
            }
        }
    }
}
