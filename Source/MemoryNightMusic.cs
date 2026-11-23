using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>One real-time music source, with sequential handovers: base silent, then event; event stopped, then base.
    /// Playback/history is saved separately from Unity handles. A paused game keeps music and fades running, like vanilla music.</summary>
    internal static class MemoryNightMusic
    {
        internal static readonly string[] TrackKeys =
        {
            "MF_MemoryNight_DreamMoment_B", "MF_MemoryNight_DreamPath_A", "MF_MemoryNight_DreamGarden_A"
        };
        private enum Phase { Idle, BaseOut, WaitForSilence, EventIn, Playing, EventOut, BaseIn, Cleanup }
        private static Phase phase;
        private static List<string> bag = new List<string>();
        private static string lastTrack = "";
        private static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private static readonly HashSet<LordJob_MFMemoryNight> failed = new HashSet<LordJob_MFMemoryNight>();
        private static LordJob_MFMemoryNight owner;
        private static AudioSource source;
        private static float elapsed, duration, fromGain, eventGain;
        private static bool preservePosition, clipsChecked, probeClips;
        private static string lastReason = "";
        private static float baseOutSeconds = 2f, eventInSeconds = 2f, endOutSeconds = 2f,
            cancelOutSeconds = 1f, hiddenOutSeconds = 1f, baseInSeconds = 2f;
        private const float EventVolumeFactor = 0.8f;

        internal static string State => phase.ToString();
        internal static LordJob_MFMemoryNight Owner => owner;
        internal static AudioSource EventSource => source;
        internal static float EventGain => eventGain;
        internal static float BaseGain => MemoryNightMusicAdapter.BaseGain;
        internal static string LastReason => lastReason;
        internal static string LastTrack => lastTrack;
        internal static int BagCount => bag.Count;
        internal static int ReadyClips => clips.Values.Count(c => ClipReady(c));
        internal static AudioClip ClipFor(string key) => key != null && clips.TryGetValue(key, out var c) ? c : null;

        internal static void Update()
        {
            try { UpdateCore(Mathf.Max(0f, Time.unscaledDeltaTime)); }
            catch (Exception e)
            {
                lastReason = "music error: " + e.GetType().Name;
                if (owner != null) failed.Add(owner);
                StopRuntime();
                WarnCleanup(e);
            }
        }

        private static void UpdateCore(float delta)
        {
            if (phase == Phase.Cleanup)
            {
                StopRuntime();
                return;
            }
            // Called before PhotoStore's Playing guard, so leaving the play scene releases audio too.
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null)
            {
                if (phase != Phase.Idle || source != null) StopRuntime();
                return;
            }
            if (owner != null && source != null && source.isPlaying) owner.MusicPosition = source.time;
            MemoryNightMusicAdapter.ApplyNow();
            if (phase != Phase.Idle && MemoryNightMusicAdapter.LastError != null)
            {
                AbortForAdapterError(MemoryNightMusicAdapter.LastError);
                return;
            }
            if (source != null) source.volume = Mathf.Clamp01(Prefs.VolumeMusic) * EventVolumeFactor * eventGain;
            if ((phase == Phase.EventIn || phase == Phase.Playing || phase == Phase.EventOut)
                && source != null && source.isPlaying && !MemoryNightMusicAdapter.IsBaseSilent)
                FailOwner("base silence gate lost during event playback");

            if (owner != null && phase != Phase.EventOut && phase != Phase.BaseIn)
            {
                if (!Alive(owner)) End(owner);
                else if (!Visible(owner)) BeginEventOut(hiddenOutSeconds, true, "map hidden");
            }

            elapsed += delta;
            switch (phase)
            {
                case Phase.Idle:
                    var next = Candidate();
                    if (next != null) Begin(next);
                    break;
                case Phase.BaseOut:
                    MemoryNightMusicAdapter.SetBaseGain(Mathf.Lerp(fromGain, 0f, Progress));
                    if (Progress >= 1f)
                    {
                        MemoryNightMusicAdapter.SetBaseGain(0f);
                        SetPhase(Phase.WaitForSilence, 2f);
                    }
                    break;
                case Phase.WaitForSilence:
                    MemoryNightMusicAdapter.SetBaseGain(0f);
                    if (MemoryNightMusicAdapter.IsBaseSilent) StartEvent();
                    else if (elapsed >= duration) FailOwner("base music did not become silent");
                    break;
                case Phase.EventIn:
                    if (EventFinished()) break;
                    eventGain = Mathf.Lerp(fromGain, 1f, Progress);
                    SetEventVolume();
                    if (Progress >= 1f) SetPhase(Phase.Playing, 0f);
                    break;
                case Phase.Playing:
                    EventFinished();
                    break;
                case Phase.EventOut:
                    eventGain = Mathf.Lerp(fromGain, 0f, Progress);
                    SetEventVolume();
                    if (Progress >= 1f)
                    {
                        if (source != null)
                        {
                            if (owner != null && source.isPlaying) owner.MusicPosition = source.time;
                            if (owner != null && !source.isPlaying && owner.MusicStarted && source.clip != null)
                            {
                                owner.MusicPosition = source.clip.length;
                                owner.MusicCompleted = true;
                            }
                            source.volume = 0f;
                            source.Stop();
                            if (source.isPlaying) return; // Never release the base gate before the event source actually stops.
                            source.clip = null;
                        }
                        if (owner != null && !preservePosition) owner.MusicCompleted = true;
                        eventGain = 0f;
                        BeginBaseIn();
                    }
                    break;
                case Phase.BaseIn:
                    // A rapid new gathering waits for this entire handover, avoiding overlapping sources/fades.
                    MemoryNightMusicAdapter.SetBaseGain(Mathf.Lerp(fromGain, 1f, Progress));
                    if (Progress >= 1f)
                    {
                        MemoryNightMusicAdapter.SetBaseGain(1f);
                        MemoryNightMusicAdapter.Release();
                        SetPhase(Phase.Idle, 0f);
                    }
                    break;
            }
        }

        private static float Progress => duration <= 0f ? 1f : Mathf.Clamp01(elapsed / duration);
        private static void SetPhase(Phase value, float seconds)
        {
            phase = value;
            elapsed = 0f;
            duration = Mathf.Max(0f, seconds);
        }
        private static bool Alive(LordJob_MFMemoryNight job)
        {
            var lord = job?.lord;
            var p = job?.Projector;
            return job?.Watching == true && !job.Cancelled && lord != null && lord.LordJob == job && lord.CurLordToil is LordToil_MFMemoryNight
                && Find.Maps.Contains(lord.Map) && lord.Map.lordManager.lords.Contains(lord)
                && p != null && p.Spawned && p.Map == lord.Map && job.HasFilmPhotos;
        }
        private static bool Visible(LordJob_MFMemoryNight job) => job?.lord?.Map == Find.CurrentMap && WorldRendererUtility.DrawingMap;
        private static LordJob_MFMemoryNight Candidate()
        {
            if (Find.CurrentMap == null || !WorldRendererUtility.DrawingMap) return null;
            // Keep a valid incumbent; use stable lord order only when acquiring a new owner.
            if (owner != null && Alive(owner) && Visible(owner) && !owner.MusicCompleted && !failed.Contains(owner)) return owner;
            LordJob_MFMemoryNight best = null;
            foreach (var lord in MemoryNightRoster.OnMap(Find.CurrentMap))
            {
                var job = lord.LordJob as LordJob_MFMemoryNight;
                if (job == null || job.MusicCompleted || failed.Contains(job) || !Alive(job)) continue;
                if (best == null || lord.loadID < best.lord.loadID) best = job;
            }
            return best;
        }

        private static bool LoadClips()
        {
            if (!clipsChecked)
            {
                clipsChecked = true;
                foreach (string key in TrackKeys)
                {
                    var clip = ContentFinder<AudioClip>.Get("MomentFrames/MemoryNight/" + key, false);
                    if (clip != null) clips[key] = clip;
                }
            }
            if (clips.Count != TrackKeys.Length)
            {
                lastReason = "one or more selected music clips are missing";
                return false;
            }
            foreach (var clip in clips.Values)
            {
                if (clip == null)
                {
                    lastReason = "one or more selected music clips are missing";
                    return false;
                }
                if (float.IsNaN(clip.length) || float.IsInfinity(clip.length) || clip.length <= 0f)
                {
                    lastReason = "selected music clip has no valid duration: " + clip.name;
                    return false;
                }
                if (!ClipReady(clip)) { lastReason = "selected music clip is not loaded: " + clip.name; return false; }
            }
            return true;
        }
        private static bool ClipReady(AudioClip clip) => RuntimeAudio.Ready(clip, probeClips);

        private static void Begin(LordJob_MFMemoryNight job)
        {
            // Missing/pending content never suppresses vanilla music; loading may settle on a later frame.
            if (!LoadClips()) return;
            if (!MemoryNightMusicAdapter.TryAcquire(out string reason))
            {
                lastReason = reason;
                // LoadedGame can expose a valid restored lord before the play root creates its audio source.
                // Only a recognized readiness failure retries; unsupported layouts/patches still leave vanilla alone.
                if (!IsRetryableAcquireFailure(reason)) failed.Add(job);
                return;
            }
            if (source == null)
            {
                var go = new GameObject("MomentFrames Memory Night Music") { hideFlags = HideFlags.HideAndDontSave };
                source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.pitch = 1f;
                source.spatialBlend = 0f;
                source.ignoreListenerPause = true;
                source.volume = 0f;
            }
            owner = job;
            if (job.MusicTrackKey.NullOrEmpty()) job.MusicTrackKey = PickTrack();
            if (!clips.ContainsKey(job.MusicTrackKey)) { FailOwner("saved music key is unavailable"); return; }
            source.Stop();
            source.clip = null;
            eventGain = 0f;
            fromGain = MemoryNightMusicAdapter.BaseGain;
            lastReason = "base fades before event playback";
            SetPhase(Phase.BaseOut, baseOutSeconds);
        }

        internal static bool IsRetryableAcquireFailure(string reason)
        {
            if (reason == null) return false;
            const string prefix = "Base music adapter unavailable: ";
            if (!reason.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string detail = reason.Substring(prefix.Length);
            return detail == "The game music manager is unavailable."
                || detail == "No initialized base music source was found."
                || detail == "RimTunes runtime was removed."
                || detail == "RimTunes source provider is unavailable."
                || detail == "RimTunes volume manager is unavailable.";
        }

        private static void StartEvent()
        {
            if (owner == null || !Alive(owner) || !Visible(owner)) { BeginBaseIn(); return; }
            var clip = ClipFor(owner.MusicTrackKey);
            if (!ClipReady(clip)) { FailOwner("selected music became unavailable"); return; }
            if (owner.MusicPosition >= clip.length - 0.02f)
            {
                owner.MusicCompleted = true;
                BeginBaseIn();
                return;
            }
            // This is deliberately separate from the frame that completed the base fade.
            MemoryNightMusicAdapter.SetBaseGain(0f);
            MemoryNightMusicAdapter.ApplyNow();
            if (!MemoryNightMusicAdapter.IsBaseSilent) return;
            source.clip = clip;
            source.loop = false;
            source.pitch = 1f;
            source.volume = 0f;
            source.time = Mathf.Clamp(owner.MusicPosition, 0f, clip.length - 0.02f);
            source.Play();
            owner.MusicStarted = true;
            fromGain = 0f;
            lastReason = "event playing after verified base silence";
            SetPhase(Phase.EventIn, eventInSeconds);
        }

        private static bool EventFinished()
        {
            if (source != null && source.isPlaying) return false;
            if (owner != null)
            {
                if (source?.clip != null) owner.MusicPosition = source.clip.length;
                owner.MusicCompleted = true;
            }
            eventGain = 0f;
            if (source != null) { source.Stop(); source.volume = 0f; source.clip = null; }
            lastReason = "event song finished; no restart this night";
            BeginBaseIn();
            return true;
        }
        private static void SetEventVolume()
        {
            if (source != null) source.volume = Mathf.Clamp01(Prefs.VolumeMusic) * EventVolumeFactor * eventGain;
        }
        private static void BeginEventOut(float seconds, bool hold, string reason)
        {
            if (phase == Phase.EventOut || phase == Phase.BaseIn || phase == Phase.Idle || phase == Phase.Cleanup) return;
            preservePosition = hold;
            lastReason = reason;
            if (source == null || !source.isPlaying)
            {
                if (owner != null && !hold) owner.MusicCompleted = true;
                eventGain = 0f;
                BeginBaseIn();
                return;
            }
            fromGain = eventGain;
            SetPhase(Phase.EventOut, seconds);
        }
        private static void BeginBaseIn()
        {
            owner = null;
            fromGain = MemoryNightMusicAdapter.BaseGain;
            SetPhase(Phase.BaseIn, baseInSeconds);
        }
        internal static void End(LordJob_MFMemoryNight job, bool normal = false)
        {
            // Lord cleanup and map removal also call End outside our Update boundary.
            // Only this MF observer belongs in the catch, never the caller's native cleanup.
            try
            {
                if (job == null) return;
                job.MusicCompleted = true;
                if (owner != job) return; // A different lord's cleanup cannot stop the current owner.
                BeginEventOut(normal ? endOutSeconds : cancelOutSeconds, false, normal ? "night finished" : "night cancelled");
            }
            catch (Exception e)
            {
                lastReason = "music end error: " + e.GetType().Name;
                if (owner != null) failed.Add(owner);
                StopRuntime();
                WarnCleanup(e);
            }
        }
        private static void FailOwner(string reason)
        {
            lastReason = reason;
            if (owner != null) failed.Add(owner);
            StopRuntime();
        }

        internal static bool AbortForAdapterError(string reason, AudioSource nativeWriteSource = null)
        {
            lastReason = "base music adapter failed: " + reason;
            if (owner != null) failed.Add(owner);
            return StopRuntime(nativeWriteSource);
        }

        private static string PickTrack()
        {
            if (bag.Count == 0) bag.AddRange(TrackKeys);
            var choices = bag.Where(k => k != lastTrack).ToList();
            if (choices.Count == 0) choices.AddRange(bag);
            string key = choices[Rand.Range(0, choices.Count)];
            bag.Remove(key);
            lastTrack = key;
            return key;
        }
        internal static void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving && owner != null && source != null && source.isPlaying) owner.MusicPosition = source.time;
            Scribe_Collections.Look(ref bag, "mfMemoryNightMusicBag", LookMode.Value);
            Scribe_Values.Look(ref lastTrack, "mfMemoryNightMusicLastTrack", "");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (bag == null) bag = new List<string>();
                bag = bag.Where(TrackKeys.Contains).Distinct().ToList();
                if (lastTrack == null || !TrackKeys.Contains(lastTrack)) lastTrack = "";
            }
        }
        private static bool StopRuntime(AudioSource nativeWriteSource = null)
        {
            eventGain = 0f;
            if (!StopEventSource())
            {
                // A failed Stop or unreadable playback state cannot safely release the base music gate.
                SetPhase(Phase.Cleanup, 0f);
                try { MemoryNightMusicAdapter.SetBaseGain(0f, nativeWriteSource); }
                catch (Exception e) { WarnCleanup(e); }
                return false;
            }
            owner = null;
            try { MemoryNightMusicAdapter.Release(nativeWriteSource); }
            catch (Exception e) { WarnCleanup(e); }
            SetPhase(Phase.Idle, 0f);
            return true;
        }

        private static bool StopEventSource()
        {
            if (source == null) { source = null; return true; }
            var stopping = source;
            try { if (owner != null && stopping.isPlaying) owner.MusicPosition = stopping.time; }
            catch (Exception e) { WarnCleanup(e); }
            // Try each independent way to stop our own audio, even if a previous cleanup operation fails.
            try { stopping.volume = 0f; }
            catch (Exception e) { WarnCleanup(e); }
            try { stopping.Stop(); }
            catch (Exception e) { WarnCleanup(e); }
            try { stopping.clip = null; }
            catch (Exception e) { WarnCleanup(e); }
            try { if (stopping.isPlaying) return false; }
            catch (Exception e) { WarnCleanup(e); return false; }
            try { UnityEngine.Object.Destroy(stopping.gameObject); }
            catch (Exception e) { WarnCleanup(e); }
            source = null;
            return true;
        }

        private static void WarnCleanup(Exception error)
        {
            // Logging is also MF's work and must not turn a cleanup failure into an engine failure.
            try { Log.WarningOnce("[MomentFrames] memory night music unavailable: " + error, 0x4D464D55); }
            catch { }
        }
        internal static void Reset()
        {
            MemoryNightRoster.Reset();
            // Reset history now, once. A cleanup retried after a load must not
            // erase the new save's bag/lastTrack restored by ExposeData.
            StopRuntime();
            bag = new List<string>();
            lastTrack = "";
            clips.Clear();
            clipsChecked = probeClips = false;
            failed.Clear();
            lastReason = "reset";
            ProbeResetTimings();
        }

        // Narrow native probe controls. Production never calls these; probe changes do not alter Prefs or gameplay timing.
        internal static void ProbeSetFadeSeconds(float baseOut, float eventIn, float endOut, float cancelOut, float hiddenOut, float baseIn)
        {
            baseOutSeconds = Mathf.Max(0f, baseOut); eventInSeconds = Mathf.Max(0f, eventIn);
            endOutSeconds = Mathf.Max(0f, endOut); cancelOutSeconds = Mathf.Max(0f, cancelOut);
            hiddenOutSeconds = Mathf.Max(0f, hiddenOut); baseInSeconds = Mathf.Max(0f, baseIn);
        }
        internal static void ProbeResetTimings() => ProbeSetFadeSeconds(2f, 2f, 2f, 1f, 1f, 2f);
        internal static bool ProbeLoadClips() => LoadClips();
        internal static void ProbeUseClips(AudioClip[] values)
        {
            if (phase != Phase.Idle || values == null || values.Length != TrackKeys.Length) throw new InvalidOperationException("Probe clips require an idle controller and three clips.");
            clips.Clear();
            for (int i = 0; i < values.Length; i++) clips[TrackKeys[i]] = values[i];
            probeClips = clipsChecked = true;
        }
        internal static void ProbeSeek(float seconds)
        {
            if (owner == null || source?.clip == null || !source.isPlaying) throw new InvalidOperationException("No event is playing.");
            source.time = Mathf.Clamp(seconds, 0f, source.clip.length - 0.02f);
            owner.MusicPosition = source.time;
        }
        internal static string ProbePickTrack() => PickTrack();
        internal static string[] ProbeBag() => bag.ToArray();
        internal static void ProbeRestoreHistory(string[] values, string previous)
        {
            bag = values == null ? new List<string>() : values.Where(TrackKeys.Contains).Distinct().ToList();
            lastTrack = TrackKeys.Contains(previous) ? previous : "";
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.Dispose))]
    internal static class Patch_MemoryNightMusic_GameDispose
    {
        private static void Prefix(Game __instance)
        {
            // Observe cleanup before Dispose; never catch or alter the game's Dispose exception.
            try { if (Current.Game == __instance) MemoryNightMusic.Reset(); }
            catch (Exception e)
            {
                try { Log.WarningOnce("[MomentFrames] memory night music cleanup failed: " + e, 0x4D464D56); }
                catch { }
            }
        }
    }
}
