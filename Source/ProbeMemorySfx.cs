using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;
using Verse.Sound;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool memorySfxMode;
        private static Building_PhotoProjector sfxOther;
        private static Lord sfxLord;
        private static LordJob_MFMemoryNight sfxJob;
        private static Game sfxSavedGame;
        private static string sfxProjectorId;
        private static float sfxVolumeBefore;
        private static int sfxSecondsBefore, sfxNativeBoundary, sfxStartBoundary, sfxSlideBoundary, sfxFrames;
        private static int sfxBasePositiveFrames, sfxEventPositiveFrames;
        private static SampleOneShot sfxReplacedSample;
        private static AudioSource sfxReplacedSource;
        private static StreamWriter sfxTrace;
        private static readonly List<SfxNativeShot> sfxShots = new List<SfxNativeShot>();
        private sealed class SfxNativeShot
        {
            internal SampleOneShot Sample;
            internal string Def, Clip, Maker;
            internal Map Map;
            internal IntVec3 Cell;
            internal int Frames, PositiveFrames, MinSamples = int.MaxValue, MaxSamples;
            internal float Slider, PeakVolume;
            internal bool Spatial = true, Pitch = true, ClipMatches = true;
        }
        private static string SfxF(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static void SfxCheck(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException("memory SFX: " + text + " [" + MemoryNightSfx.LastReport + "]");
            checks.Add("PASS: memory SFX — " + text);
        }
        private static bool IsProjectorShot(SampleOneShot sample) => sample?.subDef?.parentDef != null
            && (sample.subDef.parentDef.defName == "MF_Projector_Start" || sample.subDef.parentDef.defName == "MF_Projector_Slide");
        private static float SfxDistance(IntVec3 cell) => Mathf.Sqrt((cell - Find.CameraDriver.MapPosition).LengthHorizontalSquared);
        private static void SampleMemorySfx()
        {
            if (sfxTrace == null) return;
            sfxFrames++;
            // World SFX are excluded from this separate music-only overlap audit.
            var eventSource = MemoryNightMusic.EventSource;
            float eventVolume = eventSource != null && eventSource.isPlaying ? eventSource.volume : 0f;
            float baseVolume = MemoryNightMusicAdapter.BaseSources.Where(s => s != null && s.isPlaying)
                .Select(s => s.volume).DefaultIfEmpty(0f).Max();
            if (baseVolume > .00001f) sfxBasePositiveFrames++;
            if (eventVolume > .00001f) sfxEventPositiveFrames++;
            if (eventVolume > .00001f && baseVolume > .00001f)
                throw new InvalidOperationException("memory SFX run exposed music overlap: " + baseVolume + "/" + eventVolume);
            string musicColumns = "," + SfxF(baseVolume) + "," + SfxF(eventVolume) + "," + MemoryNightMusic.State;
            int rows = 0;
            var playing = Find.SoundRoot?.oneShotManager?.PlayingOneShots;
            if (playing != null)
                foreach (var sample in playing.Where(IsProjectorShot).ToArray())
                {
                    var source = sample.source;
                    if (source == null || source.clip == null || !source.isPlaying) continue;
                    var record = sfxShots.FirstOrDefault(r => ReferenceEquals(r.Sample, sample));
                    if (record == null)
                    {
                        record = new SfxNativeShot
                        {
                            Sample = sample, Def = sample.subDef.parentDef.defName, Clip = source.clip.name,
                            Maker = sample.info.Maker.Thing?.GetUniqueLoadID() ?? "", Map = sample.info.Maker.Map,
                            Cell = sample.info.Maker.Cell, Slider = Prefs.VolumeGame
                        };
                        sfxShots.Add(record);
                    }
                    record.Frames++;
                    if (source.volume > .00001f && !RuntimeAudio.Read<bool>(source, "mute")) record.PositiveFrames++;
                    record.MinSamples = Math.Min(record.MinSamples, RuntimeAudio.Samples(source));
                    record.MaxSamples = Math.Max(record.MaxSamples, RuntimeAudio.Samples(source));
                    record.PeakVolume = Mathf.Max(record.PeakVolume, source.volume);
                    record.Spatial &= source.spatialBlend == 1f && RuntimeAudio.Read<float>(source, "minDistance") == 3f && RuntimeAudio.Read<float>(source, "maxDistance") == 16f
                        && source.transform.position.ToIntVec3() == record.Cell && !sample.subDef.onCamera;
                    record.Pitch &= Mathf.Abs(source.pitch - 1f) < .00001f;
                    record.ClipMatches &= source.clip.name == record.Clip && !RuntimeAudio.Read<bool>(source, "loop");
                    sfxTrace.WriteLine(Time.frameCount + "," + SfxF(Time.realtimeSinceStartup) + "," + phase + "," + record.Def
                        + "," + record.Clip + "," + source.GetInstanceID() + "," + (sfxShots.IndexOf(record)+1) + ",true," + RuntimeAudio.Samples(source)
                        + "," + SfxF(source.volume) + "," + RuntimeAudio.Read<bool>(source, "mute") + "," + SfxF(Prefs.VolumeGame) + "," + record.Maker
                        + "," + record.Cell.x + "," + record.Cell.z + "," + SfxF(SfxDistance(record.Cell))
                        + "," + MemoryNightSfx.StartCount + "," + MemoryNightSfx.SlideCount + "," + MemoryNightSfx.ObservedPhoto
                        + "," + MemoryNightSfx.LastReport.Replace(",", ";") + musicColumns);
                    rows++;
                }
            if (rows == 0)
                sfxTrace.WriteLine(Time.frameCount + "," + SfxF(Time.realtimeSinceStartup) + "," + phase
                    + ",none,none,0,0,false,0,0,false," + SfxF(Prefs.VolumeGame) + ",,0,0,"
                    + (projector != null && projector.Spawned ? SfxF(SfxDistance(projector.Position)) : "0")
                    + "," + MemoryNightSfx.StartCount + "," + MemoryNightSfx.SlideCount + "," + MemoryNightSfx.ObservedPhoto
                    + "," + MemoryNightSfx.LastReport.Replace(",", ";") + musicColumns);
            if (sfxFrames % 60 == 0) sfxTrace.Flush();
        }
        private static void SfxMark()
        {
            sfxNativeBoundary = sfxShots.Count;
            sfxStartBoundary = MemoryNightSfx.StartCount;
            sfxSlideBoundary = MemoryNightSfx.SlideCount;
        }
        private static void SfxNoNew(string text)
        {
            SfxCheck(sfxShots.Count == sfxNativeBoundary && MemoryNightSfx.StartCount == sfxStartBoundary
                && MemoryNightSfx.SlideCount == sfxSlideBoundary, text);
        }
        // The clip starts when the changed photo is first drawn, which can be a few frames after the change.
        private static bool SfxSettled(string def)
        {
            if (sfxSettleSince == 0f) sfxSettleSince = Time.realtimeSinceStartup;
            var shot = sfxShots.Skip(sfxNativeBoundary).FirstOrDefault(r => r.Def == def);
            bool done = (shot != null && shot.Frames >= 2 && shot.MaxSamples > shot.MinSamples) || Time.realtimeSinceStartup - sfxSettleSince > 3f;
            if (done) sfxSettleSince = 0f;
            return done;
        }
        private static float sfxSettleSince;
        private static void SfxShot(string def, float slider, string text)
        {
            var fresh = sfxShots.Skip(sfxNativeBoundary).Where(r => r.Def == def).ToArray();
            SfxCheck(fresh.Length == 1, text + ": exactly one independent native SampleOneShot");
            var r = fresh[0];
            SfxCheck(Find.TickManager.Paused,
                text + ": native paused manual control (paused=" + Find.TickManager.Paused
                + ", samples=" + r.MinSamples + ".." + r.MaxSamples + ")");
            float expected = (def == "MF_Projector_Start" ? .35f : .22f) * slider;
            SfxCheck(r.Frames >= 2 && r.MaxSamples > r.MinSamples && r.Clip == def && r.ClipMatches,
                text + ": selected WAV plays and actual timeSamples advance without looping (frames=" + r.Frames + ", samples=" + r.MinSamples + ".." + r.MaxSamples + ", clip=" + r.Clip + ", same=" + r.ClipMatches + ")");
            SfxCheck(r.Spatial && r.Pitch && r.Map == map && r.Cell == projector.Position && r.Maker == projector.GetUniqueLoadID(),
                text + ": world source uses the correct projector, map, cell, 3..16 range and original pitch");
            SfxCheck(Mathf.Abs(r.Slider - slider) < .00001f && Mathf.Abs(r.PeakVolume - expected) < .0001f
                && (slider == 0f ? r.PositiveFrames == 0 : r.PositiveFrames >= 2),
                text + ": actual game-volume gain is " + (def == "MF_Projector_Start" ? "0.35" : "0.22") + " x " + SfxF(slider));
            if (def == "MF_Projector_Start")
                SfxCheck(MemoryNightSfx.StartCount == sfxStartBoundary + 1 && MemoryNightSfx.LastStartProjector == projector.GetUniqueLoadID(), text + ": one start dispatch");
            else
                SfxCheck(MemoryNightSfx.SlideCount == sfxSlideBoundary + 1 && MemoryNightSfx.LastSlideProjector == projector.GetUniqueLoadID()
                    && MemoryNightSfx.LastSlidePhoto == projector.Current.id, text + ": one changed-photo dispatch");
        }
        private static void SfxNear() => Find.CameraDriver.SetRootPosAndSize(projector.DrawPos, 10f);
        private static void SfxFar() => Find.CameraDriver.SetRootPosAndSize((center + new IntVec3(35, 0, 35)).ToVector3Shifted(), 10f);
        private static void SfxBegin()
        {
            foreach (var lord in map.lordManager.lords.ToList()) map.lordManager.RemoveLord(lord);
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.needs?.rest != null) pawn.needs.rest.CurLevel = 1f;
                if (pawn.needs?.food != null) pawn.needs.food.CurLevel = 1f;
                if (pawn.Drafted) pawn.drafter.Drafted = false;
                if (pawn.InMentalState) pawn.MentalState.RecoverFromState();
                if (!pawn.Awake()) RestUtility.WakeUp(pawn);
            }
            SfxCheck(MemoryNight.Start(projector), "real projector gathering starts");
            Find.TickManager.Pause();
            sfxLord = map.lordManager.lords.Single(l => l.LordJob is LordJob_MFMemoryNight);
            sfxJob = (LordJob_MFMemoryNight)sfxLord.LordJob;
            SfxCheck(sfxLord.CurLordToil is LordToil_MFMemoryNight && sfxJob.SfxStartConsumed, "actual new gathering Init consumes its start marker");
        }
        private static void SfxRemove()
        {
            if (sfxLord != null && map.lordManager.lords.Contains(sfxLord)) map.lordManager.RemoveLord(sfxLord);
        }
        private static void SfxNext()
        {
            string before = projector.Current.id;
            projector.Next();
            SfxCheck(projector.Current.id != before, "fixture changes the real projected photograph ID");
        }
        private static bool SfxBaseline => MemoryNightSfx.Owner == sfxJob && MemoryNightSfx.ObservedProjector == projector
            && MemoryNightSfx.ObservedPhoto == projector.Current?.id;
        private static void SfxReloadRefs()
        {
            SfxCheck(!ReferenceEquals(Current.Game, sfxSavedGame), "native loader creates a different Game instance");
            map = Find.CurrentMap;
            Find.TickManager.Pause();
            Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear();
            projector = map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoProjector>().Single(p => p.GetUniqueLoadID() == sfxProjectorId);
            sfxLord = map.lordManager.lords.Single(l => l.LordJob is LordJob_MFMemoryNight);
            sfxJob = (LordJob_MFMemoryNight)sfxLord.LordJob;
            SfxNear();
            SfxCheck(sfxJob.SfxStartConsumed && sfxLord.CurLordToil is LordToil_MFMemoryNight, "loaded gathering keeps its consumed start marker");
        }
        private static void MemorySfxStep()
        {
            switch (phase)
            {
                case 0:
                    FxSet(false); Setup();
                    sfxVolumeBefore = Prefs.VolumeGame; sfxSecondsBefore = MomentFramesMod.Settings.projectorSeconds;
                    Prefs.VolumeGame = 1f; MomentFramesMod.Settings.projectorSeconds = 120;
                    sfxShots.Clear(); sfxFrames = sfxBasePositiveFrames = sfxEventPositiveFrames = 0;
                    SfxCheck(AudioListener.volume == 0f, "disposable master mute is separate from native source gain/time evidence");
                    sfxTrace = new StreamWriter(Path.Combine(dir, "sfx-frames.csv"));
                    sfxTrace.WriteLine("frame,real_seconds,phase,def,clip,source,native_sample,is_playing,samples,volume,mute,game_volume,maker,cell_x,cell_z,camera_distance,start_dispatches,slide_dispatches,observed_photo,report,base_volume,event_volume,music_state");
                    projector = (Building_PhotoProjector)ThingMaker.MakeThing(ThingDef.Named("MF_Projector"), ThingDefOf.Steel);
                    projector.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(projector, center + new IntVec3(0,0,-5), map, Rot4.North);
                    sfxProjectorId = projector.GetUniqueLoadID();
                    sfxOther = (Building_PhotoProjector)ThingMaker.MakeThing(ThingDef.Named("MF_Projector"), ThingDefOf.Steel);
                    sfxOther.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(sfxOther, center + new IntVec3(4,0,-5), map, Rot4.North);
                    SfxNear(); MemoryNightSfx.ProbeResetCounters();
                    testRec = PhotoStore.Instance.Request(map, center, "group", "sfx-fixture-photo", "Projector SFX fixture", "", new[] { p1,p2 });
                    Wait(.8f); phase = 1; return;
                case 1:
                    if (!testRec.HasFile || PhotoCapture.Busy) return;
                    var store = PhotoStore.Instance;
                    for (int i = 1; i <= 2; i++)
                    {
                        var rec = new PhotoRecord { id="sfx-film-"+i, kind="group",file="sfx-film-"+i+".png",title="SFX film fixture",ticksAbs=testRec.ticksAbs+i,year=testRec.year,aspect=testRec.aspect };
                        File.Copy(store.FilePath(testRec), store.FilePath(rec), true);
                        HarmonyLib.Traverse.Create(store).Field("records").GetValue<List<PhotoRecord>>().Add(rec);
                    }
                    store.NotifyChanged(); SfxCheck(projector.ReelCount >= 3, "fixture film uses three real PNG files");
                    phase = 2; return;
                case 2:
                    // RimTunes creates its fallback source lazily through its provider, before its first song.
                    var rimType = HarmonyLib.AccessTools.TypeByName("RimTunes.RimTunesMod");
                    if (rimType != null)
                    {
                        var runtime = HarmonyLib.AccessTools.Property(rimType, "Runtime").GetValue(null, null);
                        if (runtime == null) return;
                        var provider = HarmonyLib.AccessTools.Property(runtime.GetType(), "AudioSource").GetValue(runtime, null);
                        if (provider == null) return;
                        HarmonyLib.AccessTools.Method(provider.GetType(), "GetAudioSource").Invoke(provider, Array.Empty<object>());
                    }
                    if (!MemoryNightMusicAdapter.TryAcquire(out _)) return;
                    MemoryNightMusicAdapter.Release();
                    Find.MusicManagerPlay.ForcePlaySong(DefDatabase<SongDef>.GetNamed("A_New_Day"), false);
                    foreach (string name in new[] { "MF_Projector_Start", "MF_Projector_Slide" })
                    {
                        var clip = ContentFinder<AudioClip>.Get("MomentFrames/Projector/"+name, false);
                        if (!RuntimeAudio.Ready(clip)) return;
                        var def = DefDatabase<SoundDef>.GetNamed(name);
                        SfxCheck(clip.channels == 2 && clip.frequency == 48000 && Mathf.Abs(clip.length - (name == "MF_Projector_Start" ? .8f : .4f)) < .002f,
                            "native decoded selected " + name + " WAV at its fixed duration, stereo 48 kHz");
                        SfxCheck(def.context == SoundContext.MapOnly && !def.sustain && def.subSounds.Count == 1 && !def.subSounds[0].onCamera
                            && !def.subSounds[0].tempoAffectedByGameSpeed && !def.subSounds[0].muteWhenPaused, "native SoundDef uses game SFX volume and world position at real-time pitch");
                    }
                    SfxMark(); SfxNext(); sfxOther.Next(); Wait(.5f); phase=3; return;
                case 3:
                    SfxNoNew("ordinary projector photo changes outside a memory night produce no projector SFX");
                    SfxMark(); SfxBegin(); Wait(.25f); phase=4; return;
                case 4:
                    SfxShot("MF_Projector_Start",1f,"new nearby night");
                    SfxCheck(MemoryNightSfx.SlideCount == sfxSlideBoundary, "first actual rendered photo is a silent baseline");
                    Wait(1f); phase=5; return;
                case 5:
                    if (!SfxBaseline) return;
                    SfxCheck(MemoryNightSfx.StartCount == sfxStartBoundary + 1, "continued paused gathering never repeats its startup dispatch");
                    SfxMark(); SfxNext(); sfxOther.Next(); Wait(.22f); phase=6; return;
                case 6:
                    if (!SfxSettled("MF_Projector_Slide")) return;
                    SfxShot("MF_Projector_Slide",1f,"first changed photo");
                    SfxCheck(sfxShots.Skip(sfxNativeBoundary).All(r=>r.Maker==projector.GetUniqueLoadID()), "other visible projector changing its photo does not dispatch this event's sound");
                    Wait(.6f); phase=7; return;
                case 7:
                    Prefs.VolumeGame=.5f; SfxMark(); SfxNext(); Wait(.22f); phase=8; return;
                case 8:
                    if (!SfxSettled("MF_Projector_Slide")) return;
                    SfxShot("MF_Projector_Slide",.5f,"second changed photo at half game volume"); Wait(.6f); phase=9; return;
                case 9:
                    Prefs.VolumeGame=0f; SfxMark(); SfxNext(); Wait(.22f); phase=10; return;
                case 10:
                    if (!SfxSettled("MF_Projector_Slide")) return;
                    SfxShot("MF_Projector_Slide",0f,"third changed photo with game SFX muted"); Wait(.6f); phase=11; return;
                case 11:
                    SfxRemove(); Prefs.VolumeGame=.5f; SfxMark(); SfxBegin(); Wait(.25f); phase=12; return;
                case 12:
                    SfxShot("MF_Projector_Start",.5f,"new night at half game volume"); Wait(1f); phase=13; return;
                case 13:
                    SfxRemove(); Prefs.VolumeGame=0f; SfxMark(); SfxBegin(); Wait(.25f); phase=14; return;
                case 14:
                    SfxShot("MF_Projector_Start",0f,"new night with game SFX muted"); Wait(1f); phase=15; return;
                case 15:
                    Prefs.VolumeGame=1f; SfxMark(); Find.World.renderer.wantedMode=WorldRenderMode.Planet; SfxNext(); Wait(.6f); phase=16; return;
                case 16:
                    SfxNoNew("hidden world-map photograph changes dispatch no SFX"); Find.World.renderer.wantedMode=WorldRenderMode.None; SfxNear(); Wait(.5f); phase=17; return;
                case 17:
                    if (!SfxBaseline) return;
                    SfxNoNew("map return observes the photo silently and never replays the consumed start");
                    SfxMark(); SfxNext(); Wait(.22f); phase=18; return;
                case 18:
                    SfxShot("MF_Projector_Slide",1f,"photo change after map-return baseline"); Wait(.6f); phase=40; return;
                case 40:
                    Find.CameraDriver.SetRootPosAndSize(projector.DrawPos + new Vector3(15f,0f,0f),20f); Wait(.4f); phase=41; return;
                case 41:
                    SfxCheck(SfxDistance(projector.Position)>=14f && SfxDistance(projector.Position)<=16f, "actual camera positive control lies near the hearing boundary");
                    if (!SfxBaseline) return;
                    SfxMark(); SfxNext(); Wait(.22f); phase=42; return;
                case 42:
                    SfxShot("MF_Projector_Slide",1f,"changed photo near 16-cell hearing boundary"); Wait(.6f); phase=19; return;
                case 19:
                    SfxMark(); SfxFar(); Wait(.4f); phase=20; return;
                case 20:
                    SfxCheck(!projector.Position.InHorDistOf(Find.CameraDriver.MapPosition,16f), "actual camera lies outside the 16-cell hearing gate");
                    SfxNext(); Wait(.5f); phase=21; return;
                case 21:
                    SfxNoNew("distant camera suppresses changed-photo dispatch and native source creation");
                    SfxRemove(); SfxBegin(); Wait(.5f); phase=22; return;
                case 22:
                    SfxNoNew("new distant night consumes its start without dispatch or native source");
                    SfxNear(); Wait(.5f); phase=23; return;
                case 23:
                    if (!SfxBaseline) return;
                    SfxNoNew("nearby reentry establishes a silent photo baseline and does not replay distant start");
                    SfxMark(); SfxNext(); Wait(.22f); phase=24; return;
                case 24:
                    SfxShot("MF_Projector_Slide",1f,"photo change after nearby reentry"); Wait(.7f); phase=25; return;
                case 25:
                    SfxMark(); SfxRemove(); Wait(.4f); phase=26; return;
                case 26:
                    SfxNoNew("cancelling a night schedules no extra startup or slide sound");
                    SfxMark(); SfxBegin(); Wait(.2f); phase=27; return;
                case 27:
                    SfxShot("MF_Projector_Start",1f,"first rapid replacement night");
                    sfxReplacedSample=sfxShots.Skip(sfxNativeBoundary).Single(r=>r.Def=="MF_Projector_Start").Sample;
                    sfxReplacedSource=sfxReplacedSample.source;
                    SfxCheck(sfxReplacedSource.isPlaying, "rapid replacement occurs while the earlier 0.8-second start source is still playing");
                    SfxMark(); SfxRemove(); SfxBegin(); Wait(.25f); phase=28; return;
                case 28:
                    SfxCheck(sfxReplacedSource != null && !sfxReplacedSource.isPlaying
                        && !Find.SoundRoot.oneShotManager.PlayingOneShots.Contains(sfxReplacedSample),
                        "native maxVoices replaces and stops the previous startup sample during rapid new-night transfer");
                    SfxShot("MF_Projector_Start",1f,"second rapid replacement night"); Wait(1f); phase=29; return;
                case 29:
                    if (!SfxBaseline) return;
                    SfxMark(); sfxSavedGame=Current.Game; GameDataSaveLoader.SaveGame("mfmemorysfx"); Wait(.3f); phase=30; return;
                case 30:
                    var savePath=GenFilePaths.FilePathForSavedGame("mfmemorysfx");
                    SfxCheck(File.Exists(savePath),"native save exists inside this new disposable profile");
                    var xml=new XmlDocument(); xml.Load(savePath);
                    var marker=xml.SelectSingleNode("//mfSfxStartConsumed");
                    SfxCheck(marker!=null && marker.InnerText.Equals("true",StringComparison.OrdinalIgnoreCase),"actual save XML persists consumed startup marker");
                    GameDataSaveLoader.LoadGame("mfmemorysfx"); Wait(1f); phase=31; return;
                case 31:
                    SfxReloadRefs(); Wait(.5f); phase=32; return;
                case 32:
                    if (!SfxBaseline) return;
                    SfxCheck(sfxShots.Count==sfxNativeBoundary && MemoryNightSfx.StartCount==0 && MemoryNightSfx.SlideCount==0,
                        "actual save/load creates no duplicate native start or first-photo sample");
                    Find.TickManager.Pause();
                    SfxMark(); SfxNext(); Wait(.22f); phase=33; return;
                case 33:
                    SfxShot("MF_Projector_Slide",1f,"changed photo after actual load"); Wait(.8f); phase=34; return;
                case 34:
                    SfxMark(); sfxSavedGame=Current.Game; GameDataSaveLoader.SaveGame("mfmemorysfx"); Wait(.3f); phase=35; return;
                case 35:
                    var legacy=new XmlDocument(); legacy.Load(GenFilePaths.FilePathForSavedGame("mfmemorysfx"));
                    var markers=legacy.SelectNodes("//mfSfxStartConsumed"); SfxCheck(markers.Count==1,"legacy fixture removes the one gathering's actual startup marker");
                    foreach(XmlNode node in markers) node.ParentNode.RemoveChild(node);
                    legacy.Save(GenFilePaths.FilePathForSavedGame("mfmemorysfx_legacy"));
                    GameDataSaveLoader.LoadGame("mfmemorysfx_legacy"); Wait(1f); phase=36; return;
                case 36:
                    SfxReloadRefs(); Wait(.5f); phase=37; return;
                case 37:
                    if (!SfxBaseline) return;
                    SfxCheck(sfxShots.Count==sfxNativeBoundary && MemoryNightSfx.StartCount==0 && MemoryNightSfx.SlideCount==0,
                        "actual legacy-save load without marker is consumed silently and establishes first-photo baseline");
                    Find.TickManager.Pause();
                    SfxMark(); SfxNext(); Wait(.22f); phase=38; return;
                case 38:
                    SfxShot("MF_Projector_Slide",1f,"changed photo after legacy-save load"); Wait(.8f); phase=39; return;
                case 39:
                    SfxCheck(sfxFrames>100 && sfxShots.Any(r=>r.Def=="MF_Projector_Start" && r.PositiveFrames>=2 && r.MaxSamples>r.MinSamples)
                        && sfxShots.Any(r=>r.Def=="MF_Projector_Slide" && r.PositiveFrames>=2 && r.MaxSamples>r.MinSamples),
                        "audit contains positive native start and slide samples with advancing timelines");
                    Prefs.VolumeGame=sfxVolumeBefore; MomentFramesMod.Settings.projectorSeconds=sfxSecondsBefore;
                    sfxTrace.Dispose(); sfxTrace=null;
                    File.WriteAllText(Path.Combine(dir,"memory-sfx-result.json"),"{\"ok\":true,\"frames\":"+sfxFrames
                        +",\"nativeStartShots\":"+sfxShots.Count(r=>r.Def=="MF_Projector_Start")+",\"nativeSlideShots\":"+sfxShots.Count(r=>r.Def=="MF_Projector_Slide")
                        +",\"positiveBaseFrames\":"+sfxBasePositiveFrames+",\"positiveEventFrames\":"+sfxEventPositiveFrames+",\"musicOverlapFrames\":0"
                        +",\"checks\":["+string.Join(",",checks.Select(Json))+"]"
                        +",\"fixture\":\"Real gathering Init, actual rendered projector Next IDs and Verse PlayingOneShots. Paused disposable map; copied fixture PNGs; temporary game-volume 0/0.5/1 and camera positions; actual save/load plus a disposable XML copy with the start marker removed. No production SFX method or AudioSource.Play is directly invoked by this probe.\""
                        +",\"limits\":\"Master muted: clip identity, source gain, spatial metadata and advancing samples only; no listening approval. Distance verifies production camera gating, not acoustic loudness. Music regression is a separate native run; this fixture checks music source overlap every observed frame.\"}");
                    CloseLog(); active=false; Application.Quit(); return;
            }
        }
    }
}
