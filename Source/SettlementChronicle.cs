using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public sealed class SettlementSnapshot : IExposable
    {
        public string id = Guid.NewGuid().ToString("N");
        public int mapId, stamp, requestedAbs, startedAbs = -1, finishedAbs = -1, attempts, retryAfter;
        public bool automatic, complete, failed;
        internal int writtenImages; // UI only; the five records still commit atomically.
        public List<string> photoIds = new List<string>();
        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id"); Scribe_Values.Look(ref mapId, "mapId");
            Scribe_Values.Look(ref stamp, "stamp"); Scribe_Values.Look(ref requestedAbs, "requestedAbs");
            Scribe_Values.Look(ref startedAbs, "startedAbs", -1); Scribe_Values.Look(ref finishedAbs, "finishedAbs", -1);
            Scribe_Values.Look(ref attempts, "attempts"); Scribe_Values.Look(ref retryAfter, "retryAfter");
            Scribe_Values.Look(ref automatic, "automatic"); Scribe_Values.Look(ref complete, "complete");
            Scribe_Values.Look(ref failed, "failed"); Scribe_Collections.Look(ref photoIds, "photoIds", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) photoIds ??= new List<string>();
        }
    }

    /// <summary>One persistent request owns five new photographs. Temporary image bytes are never saved.
    /// Only the viewed home map is rendered; delayed chapters state their actual capture dates.</summary>
    public sealed class SettlementChronicle : IExposable
    {
        public const string OverviewKind = "settlement-overview", QuarterKind = "settlement-quarter";
        private List<SettlementSnapshot> snapshots = new List<SettlementSnapshot>();
        private readonly List<SettlementSnapshot> pendingSnapshots = new List<SettlementSnapshot>();
        private Dictionary<int, int> observed = new Dictionary<int, int>();
        private List<int> mapKeys, mapValues;
        private int pollTick = -1;
        private SettlementSnapshot active;
        private PhotoCapture.Result[] parts;
        private CellRect[] areas;
        private int part;
        private bool waiting;
        private Task<PhotoCapture.Result> composition;
        private IEnumerator<bool> write;
        private static SettlementChronicle running;
        internal static void ResetActive() { running?.ClearRun(); running = null; }
        public IReadOnlyList<SettlementSnapshot> Snapshots => snapshots;
        internal string Progress(SettlementSnapshot snapshot)
        {
            if(snapshot==null)return "";
            if(snapshot.failed)return "MF_UX_SurveyFailed".Translate();
            if(snapshot.complete)return "MF_UX_SurveyComplete".Translate(snapshot.photoIds.Count);
            if(!ReferenceEquals(active,snapshot))return "MF_UX_SurveyQueued".Translate();
            if(write!=null)return "MF_UX_SurveySaving".Translate(snapshot.writtenImages);
            if(composition!=null)return "MF_UX_SurveyComposing".Translate();
            return "MF_UX_SurveyCapture".Translate(part);
        }
        internal static bool IsSurvey(PhotoRecord r) => r?.kind == OverviewKind || r?.kind == QuarterKind;
        internal static int Stamp(Map map, int abs)
        {
            float longitude = map.Tile.Valid ? Find.WorldGrid.LongLatOf(map.Tile).x : 0f;
            return SettlementLayout.Stamp(GenDate.Year(abs, longitude), (int)GenDate.Quadrum(abs, longitude) + 1);
        }
        internal static int Stamp(PhotoRecord record)
        {
            string[] date = (record?.dateSlug ?? "").Split('-');
            if (date.Length >= 2 && int.TryParse(date[0], out int year) && int.TryParse(date[1], out int quarter) && quarter >= 1 && quarter <= 4)
                return SettlementLayout.Stamp(year, quarter);
            Map map = Find.Maps.FirstOrDefault(m => m.uniqueID == record?.mapId);
            return map != null ? Stamp(map, record.ticksAbs) : SettlementLayout.Stamp(record?.year ?? 5500, 1);
        }
        internal bool Request(PhotoStore store, Map map, int stamp, bool automatic)
        {
            if (map == null || !map.IsPlayerHome || map.IsPocketMap || map.Size.x < 2 || map.Size.z < 2) return false;
            if (automatic && (!SettingsCapturePolicy.Automatic(MomentFramesMod.Settings) || MomentFramesMod.Settings.settlementPeriod == 0)) return false;
            if (snapshots.Any(s => s.mapId == map.uniqueID && !s.complete && !s.failed)) return false;
            if (automatic && snapshots.Any(s => s.mapId == map.uniqueID && s.stamp == stamp && s.automatic)) return false;
            var request = new SettlementSnapshot { mapId = map.uniqueID, stamp = stamp, requestedAbs = Find.TickManager.TicksAbs, automatic = automatic };
            snapshots.Add(request); pendingSnapshots.Add(request);
            store.NotifyChanged(); return true;
        }
        internal void Update(PhotoStore store)
        {
            int tick = Find.TickManager.TicksGame, abs = Find.TickManager.TicksAbs;
            bool automaticOff = !SettingsCapturePolicy.Automatic(MomentFramesMod.Settings) || MomentFramesMod.Settings.settlementPeriod == 0;
            bool cancelled = false;
            // Completed history stays available to the album, but never participates in the per-frame work queue.
            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                var queued = pendingSnapshots[i];
                if (queued.complete || queued.failed) { pendingSnapshots.RemoveAt(i); continue; }
                if (queued.automatic && automaticOff)
                { queued.failed = true; pendingSnapshots.RemoveAt(i); cancelled = true; }
            }
            if (cancelled) store.NotifyChanged();
            if (pollTick < 0 || tick < pollTick || tick - pollTick >= 600)
            {
                pollTick = tick;
                foreach (Map map in Find.Maps.Where(m => m.IsPlayerHome && !m.IsPocketMap))
                {
                    int current = Stamp(map, abs), mode = MomentFramesMod.Settings?.settlementPeriod ?? 0;
                    // A first observation is a baseline, never a backlog of invented historical photos.
                    bool known = observed.TryGetValue(map.uniqueID, out int previous);
                    observed[map.uniqueID] = current;
                    if (!SettingsCapturePolicy.Automatic(MomentFramesMod.Settings) || mode == 0) continue;
                    if (known && SettlementLayout.IsDue(mode, previous, current))
                        Request(store, map, current - 1, true);
                    float lon = map.Tile.Valid ? Find.WorldGrid.LongLatOf(map.Tile).x : 0f;
                    int q = (int)GenDate.Quadrum(abs, lon) + 1;
                    if (GenDate.DayOfQuadrum(abs, lon) == 14 && GenDate.HourOfDay(abs, lon) >= 11 && (mode == 1 || q == 4))
                        Request(store, map, current, true);
                }
            }
            if (active != null && (PhotoStore.Instance != store || active.failed || active.automatic && automaticOff))
            { active.failed = true; ClearRun(); store.NotifyChanged(); }
            if (waiting || PhotoCapture.Busy) return;
            if (active == null)
            {
                int currentMapId = Find.CurrentMap?.uniqueID ?? -1;
                for (int i = 0; i < pendingSnapshots.Count; i++)
                {
                    var queued = pendingSnapshots[i];
                    if (!queued.complete && !queued.failed && queued.retryAfter <= tick && queued.mapId == currentMapId
                        && (!queued.automatic || !automaticOff)) { active = queued; break; }
                }
                if (active == null) return;
                running = this;
                Map map = Find.CurrentMap;
                areas = SettlementLayout.Quadrants(map.Size.x, map.Size.z);
                parts = new PhotoCapture.Result[4]; part = 0; active.writtenImages=0; active.startedAbs = abs;
            }
            Map currentMap = Find.Maps.FirstOrDefault(m => m.uniqueID == active.mapId);
            if (currentMap != Find.CurrentMap) { Retry(store); return; }
            if (part == 4)
            {
                try
                {
                    if (composition == null) { composition = SettlementImageComposition.Begin(parts, areas, currentMap.Size.x, currentMap.Size.z); return; }
                    if (!composition.IsCompleted) return;
                    if (composition.IsFaulted) throw composition.Exception.GetBaseException();
                    var overview = composition.Result;
                    var images = new[] { overview, parts[0], parts[1], parts[2], parts[3] };
                    var bounds = new[] { new CellRect(0, 0, currentMap.Size.x, currentMap.Size.z), areas[0], areas[1], areas[2], areas[3] };
                    if (write == null)
                    {
                        foreach (var image in parts) image.pixels = null;
                        write = store.BeginStoreSurvey(currentMap, active, images, bounds);
                    }
                    if (!write.MoveNext()) { Retry(store); return; }
                    if (write.Current)
                    {
                        active.complete = true; active.finishedAbs = Find.TickManager.TicksAbs; store.NotifyChanged();
                        Messages.Message("MF_Phase2_SurveyDone".Translate(), MessageTypeDefOf.TaskCompletion, false);
                        ClearRun();
                    }
                }
                catch (Exception e) { Log.Warning("[MomentFrames] settlement survey failed: " + e); Retry(store); }
                return;
            }
            SettlementSnapshot request = active; int index = part;
            waiting = PhotoCapture.CaptureArea(currentMap, areas[index], result =>
            {
                if (!ReferenceEquals(active, request) || PhotoStore.Instance != store) return;
                waiting = false;
                if (result?.png == null || currentMap != Find.CurrentMap) { Retry(store); return; }
                parts[index] = result; part++; // next quadrant on a following frame
            }, SettlementResolution.Side(currentMap.Size.x, currentMap.Size.z, areas[index].Width, areas[index].Height));
            if (!waiting) Retry(store);
        }
        private void Retry(PhotoStore store)
        {
            if (active != null) { active.attempts++; active.failed = active.attempts >= 3; active.retryAfter = Find.TickManager.TicksGame + 600; }
            ClearRun(); store.NotifyChanged();
        }
        private void ClearRun()
        {
            write?.Dispose(); write = null; active = null; parts = null; areas = null; part = 0; waiting = false; composition = null;
            if (ReferenceEquals(running, this)) running = null;
        }
        public void ExposeData()
        {
            Scribe_Collections.Look(ref snapshots, "snapshots", LookMode.Deep);
            Scribe_Collections.Look(ref observed, "observedPeriods", LookMode.Value, LookMode.Value, ref mapKeys, ref mapValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                snapshots ??= new List<SettlementSnapshot>(); observed ??= new Dictionary<int, int>();
                snapshots.RemoveAll(s => s == null || s.id.NullOrEmpty()); pollTick = -1; ClearRun();
                pendingSnapshots.Clear();
                foreach (var snapshot in snapshots)
                    if (!snapshot.complete && !snapshot.failed) pendingSnapshots.Add(snapshot);
            }
        }
    }
}
