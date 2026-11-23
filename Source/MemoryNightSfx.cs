using System;
using System.Linq;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MomentFrames
{
    /// <summary>Short map sounds from the real gathering and actually drawn photographs. Verse owns their sources and game-volume gain.</summary>
    internal static class MemoryNightSfx
    {
        internal const string StartDefName = "MF_Projector_Start", SlideDefName = "MF_Projector_Slide";
        internal const float HearingDistance = 16f;

        private static Game game;
        private static LordJob_MFMemoryNight owner, renderedJob;
        private static Building_PhotoProjector observedProjector, renderedProjector;
        private static string observedPhoto = "", renderedPhoto = "";
        private static int renderedFrame = -1;
        private static SampleOneShot lastSample;

        // Dispatch counters: actual clip/source playback is separately observable in Verse's PlayingOneShots.
        internal static int StartCount { get; private set; }
        internal static int SlideCount { get; private set; }
        internal static string LastStartProjector { get; private set; } = "";
        internal static string LastSlideProjector { get; private set; } = "";
        internal static string LastSlidePhoto { get; private set; } = "";
        internal static string LastReason { get; private set; } = "";
        internal static string LastReport => LastReason;
        internal static LordJob_MFMemoryNight Owner => owner;
        internal static Building_PhotoProjector ObservedProjector => observedProjector;
        internal static string ObservedPhoto => observedPhoto;
        internal static SampleOneShot LastSample => lastSample != null
            && Find.SoundRoot?.oneShotManager.PlayingOneShots.Contains(lastSample) == true ? lastSample : null;

        internal static void Reset()
        {
            game = null;
            ClearObservation();
            ProbeResetCounters();
        }

        internal static void ExposeData()
        {
            // No runtime handle or photograph baseline is restored. The job saves its consumed start marker.
            if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit) ClearObservation();
        }

        internal static void BeginNight(LordJob_MFMemoryNight job)
        {
            try { BeginNightCore(job); }
            catch (Exception e) { Fail(e); }
        }

        private static void BeginNightCore(LordJob_MFMemoryNight job)
        {
            if (job == null || job.SfxStartConsumed) return;
            // Consume even when hidden, distant or unavailable: returning later must never replay the start.
            job.SfxStartConsumed = true;
            EnsureGame();
            if (Scribe.mode != LoadSaveMode.Inactive || !Alive(job) || !NearbyView(job.Projector))
            {
                LastReason = "new night start consumed outside the nearby map view";
                return;
            }
            if (Dispatch(StartDefName, job.Projector))
            {
                StartCount++;
                LastStartProjector = job.Projector.GetUniqueLoadID();
            }
        }

        /// <summary>Called after the projector successfully binds its current texture. Records only; drawing never plays audio.</summary>
        internal static void ObserveProjectedPhoto(Building_PhotoProjector projector, PhotoRecord rec)
        {
            try { ObserveProjectedPhotoCore(projector, rec); }
            catch (Exception e) { Fail(e); }
        }

        private static void ObserveProjectedPhotoCore(Building_PhotoProjector projector, PhotoRecord rec)
        {
            if (rec == null || rec.id.NullOrEmpty() || !rec.HasFile || !NearbyView(projector)) return;
            var job = Candidate();
            if (job == null || job.Projector != projector) return;
            renderedJob = job;
            renderedProjector = projector;
            renderedPhoto = rec.id;
            renderedFrame = Time.frameCount;
        }

        internal static void Update()
        {
            try
            {
                EnsureGame();
                var job = Candidate();
                if (job != owner)
                {
                    owner = job;
                    ClearBaseline();
                }
                if (job == null)
                {
                    ClearObservation();
                    return;
                }
                var p = job.Projector;
                if (!NearbyView(p) || renderedJob != job || renderedProjector != p
                    || renderedFrame < Time.frameCount - 1)
                {
                    ClearBaseline();
                    return;
                }
                // A button can change Current after this frame's draw. Keep the previous visible identity until the new one is drawn.
                if (renderedPhoto != p.Current?.id) return;
                if (observedProjector != p || observedPhoto.NullOrEmpty())
                {
                    observedProjector = p;
                    observedPhoto = renderedPhoto;
                    LastReason = "first rendered photograph observed silently";
                    return;
                }
                if (observedPhoto == renderedPhoto) return;
                observedPhoto = renderedPhoto; // Consume before dispatch, including unavailable/muted sounds.
                if (Dispatch(SlideDefName, p))
                {
                    SlideCount++;
                    LastSlideProjector = p.GetUniqueLoadID();
                    LastSlidePhoto = renderedPhoto;
                }
            }
            catch (Exception e) { Fail(e); }
        }

        private static void Fail(Exception e)
        {
            ClearObservation();
            LastReason = "projector sound error: " + e.GetType().Name;
            Log.WarningOnce("[MomentFrames] projector sound unavailable: " + e, 0x4D465346);
        }

        private static void EnsureGame()
        {
            if (ReferenceEquals(game, Current.Game)) return;
            Reset();
            game = Current.Game;
        }

        private static void ClearBaseline()
        {
            observedProjector = null;
            observedPhoto = "";
        }

        private static void ClearObservation()
        {
            owner = renderedJob = null;
            renderedProjector = null;
            renderedPhoto = "";
            renderedFrame = -1;
            ClearBaseline();
        }

        private static bool Alive(LordJob_MFMemoryNight job)
        {
            var lord = job?.lord;
            var p = job?.Projector;
            return Current.ProgramState == ProgramState.Playing && Current.Game != null
                && lord != null && lord.LordJob == job && lord.CurLordToil is LordToil_MFMemoryNight
                && lord.Map != null && Find.Maps.Contains(lord.Map) && lord.Map.lordManager.lords.Contains(lord)
                && p != null && p.Spawned && p.Map == lord.Map && p.ReelCount > 0;
        }

        private static LordJob_MFMemoryNight Candidate()
        {
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null
                || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap) return null;
            foreach (var lord in MemoryNightRoster.OnMap(Find.CurrentMap))
                if (lord.LordJob is LordJob_MFMemoryNight job && Alive(job)) return job;
            return null;
        }

        private static bool NearbyView(Building_PhotoProjector p)
        {
            var camera = Find.CameraDriver;
            return Current.ProgramState == ProgramState.Playing && Current.Game != null && UnityData.IsInMainThread
                && p != null && p.Spawned && p.Map == Find.CurrentMap && WorldRendererUtility.DrawingMap
                && camera != null && !PhotoCapture.Busy && p.Position.InHorDistOf(camera.MapPosition, HearingDistance)
                && (camera.CurrentViewRect.Contains(p.Position) || camera.CurrentViewRect.Contains(MemoryNight.ScreenCenter(p).ToIntVec3()));
        }

        private static bool Dispatch(string name, Building_PhotoProjector p)
        {
            var def = DefDatabase<SoundDef>.GetNamedSilentFail(name);
            var manager = Find.SoundRoot?.oneShotManager;
            if (def == null || def.isUndefined || def.Duration.min <= 0f || manager == null)
            {
                lastSample = null;
                LastReason = name + " unavailable; event consumed without replay";
                return false;
            }
            var previous = manager.PlayingOneShots.LastOrDefault(s => s.subDef.parentDef == def && s.info.Maker.Thing == p);
            def.PlayOneShot(SoundInfo.InMap(p));
            var current = manager.PlayingOneShots.LastOrDefault(s => s.subDef.parentDef == def && s.info.Maker.Thing == p);
            lastSample = ReferenceEquals(previous, current) ? null : current;
            LastReason = name + (lastSample != null ? " dispatched to Verse map source" : " dispatched; no new Verse sample");
            return true;
        }

        internal static void ProbeResetCounters()
        {
            StartCount = SlideCount = 0;
            LastStartProjector = LastSlideProjector = LastSlidePhoto = LastReason = "";
            lastSample = null;
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.Dispose))]
    internal static class Patch_MemoryNightSfx_GameDispose
    {
        private static void Prefix(Game __instance)
        {
            if (Current.Game == __instance) MemoryNightSfx.Reset();
        }
    }
}
