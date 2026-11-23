using System;

namespace MomentFrames
{
    /// <summary>Only game ticks advance this clock. Gathering never consumes the watching budget.</summary>
    internal sealed class MemoryNightTiming
    {
        internal const int DefaultWatchTicks = 10800, SlideTicks = 900, ArrivalStableTicks = 120, GatherWaitTicks = 5000;
        internal int Duration = DefaultWatchTicks, GatherElapsed, ArrivalStable, WatchElapsed;
        internal bool Watching, WaitingForDecision;
        internal bool Finished => Watching && WatchElapsed >= Duration;
        internal int TicksLeft => Watching ? Math.Max(0, Duration - WatchElapsed) : int.MaxValue;
        internal static int FitDuration(int value) => Math.Max(3600, Math.Min(21600, value));
        internal bool Tick(bool everyoneArrived)
        {
            if (Watching)
            {
                WatchElapsed = Math.Min(Duration, WatchElapsed + 1);
                ArrivalStable = everyoneArrived ? Math.Min(ArrivalStableTicks, ArrivalStable + 1) : 0;
                return false;
            }
            GatherElapsed++;
            ArrivalStable = everyoneArrived ? ArrivalStable + 1 : 0;
            if (ArrivalStable >= ArrivalStableTicks) { BeginWatching(); return true; }
            if (GatherElapsed >= GatherWaitTicks) WaitingForDecision = true;
            return false;
        }
        internal void BeginWatching()
        { Watching = true; WaitingForDecision = false; WatchElapsed = ArrivalStable = 0; }
        internal bool StartWithPresent(int present)
        {
            if (Watching || present <= 0) return false;
            BeginWatching(); return true;
        }
        internal void ContinueWaiting() { WaitingForDecision = false; GatherElapsed = 0; }
        internal int SlideIndex(int count, int skip = 0) => count <= 0 ? -1
            : (((Watching ? Math.Min(WatchElapsed, Math.Max(0, Duration - 1)) / SlideTicks : 0) + skip) % count + count) % count;
        internal bool CanRequestPhoto(bool pending, bool succeeded, int retryAt, bool automaticEnabled, bool everyoneArrived)
            => Watching && !Finished && !pending && !succeeded && ArrivalStable >= ArrivalStableTicks && WatchElapsed >= retryAt
                && automaticEnabled && everyoneArrived;
    }
}
