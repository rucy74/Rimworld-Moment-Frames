using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool memoryMusicMode;
        private static Lord musicLord;
        private static LordJob_MFMemoryNight musicJob;
        private static int musicSamples, musicEventPositive, musicBasePositive, musicBeforeSamples, musicPausedTick;
        private static float musicSavedPosition, musicVolumeBefore;
        private static string musicSavedKey, musicSavedLast, musicProjectorId;
        private static string[] musicSavedBag;
        private static Game musicSavedGame;
        private static AudioClip[] musicRealClips;
        private static StreamWriter musicTrace;
        private static bool musicLoadDiagnosticWritten;
        private static readonly HashSet<string> musicPlayedKeys = new HashSet<string>();

        private static void MusicCheck(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("memory music: " + message + " (" + MemoryNightMusic.State + ": " + MemoryNightMusic.LastReason + ")");
            checks.Add("PASS: memory music — " + message);
        }
        private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static void SampleMemoryMusic()
        {
            if (musicTrace == null) return;
            var source = MemoryNightMusic.EventSource;
            float eventVolume = source != null && source.isPlaying ? source.volume : 0f;
            float baseVolume = MemoryNightMusicAdapter.BaseSources.Where(s => s != null && s.isPlaying).Select(s => s.volume).DefaultIfEmpty(0f).Max();
            musicTrace.WriteLine(Time.frameCount + "," + F(Time.realtimeSinceStartup) + "," + phase + "," + MemoryNightMusic.State
                + "," + F(baseVolume) + "," + F(eventVolume) + "," + F(MemoryNightMusic.BaseGain)
                + "," + RuntimeAudio.Samples(source) + "," + (MemoryNightMusic.Owner?.MusicTrackKey ?? "")
                + "," + MemoryNightMusic.LastReason.Replace(",",";") + "," + (musicJob?.MusicCompleted ?? false));
            musicSamples++;
            if (eventVolume > 0.00001f)
            {
                musicEventPositive++;
                if (MemoryNightMusic.Owner != null) musicPlayedKeys.Add(MemoryNightMusic.Owner.MusicTrackKey);
            }
            if (baseVolume > 0.00001f) musicBasePositive++;
            if (baseVolume > 0.00001f && eventVolume > 0.00001f)
            {
                musicTrace.Flush();
                throw new InvalidOperationException("simultaneous pre-master source volumes: " + baseVolume + " / " + eventVolume);
            }
            if (musicSamples % 60 == 0) musicTrace.Flush();
        }
        private static bool MusicPlaying => MemoryNightMusic.State == "Playing" && MemoryNightMusic.EventSource != null && MemoryNightMusic.EventSource.isPlaying;
        private static bool MusicIdle => MemoryNightMusic.State == "Idle" && MemoryNightMusic.BaseGain == 1f;
        private static void MusicSourceReplacementCheck()
        {
            var manager=Find.MusicManagerPlay;
            var field=AccessTools.Field(typeof(MusicManagerPlay),"audioSource");
            var original=(AudioSource)field.GetValue(manager);
            var oldSources=MemoryNightMusicAdapter.BaseSources;
            var go=new GameObject("Memory music probe replacement");
            var replacement=go.AddComponent<AudioSource>(); replacement.playOnAwake=false; replacement.volume=.9f;
            try
            {
                field.SetValue(manager,replacement);
                var type=AccessTools.TypeByName("RimTunes.RimTunesMod");
                if (type!=null)
                {
                    var runtime=AccessTools.Property(type,"Runtime").GetValue(null,null);
                    var provider=AccessTools.Property(runtime.GetType(),"AudioSource").GetValue(runtime,null);
                    MusicCheck(ReferenceEquals(AccessTools.Method(provider.GetType(),"GetAudioSource").Invoke(provider,Array.Empty<object>()),replacement), "actual RimTunes provider resolves replacement source");
                    MusicCheck(replacement.volume==0f, "RimTunes source-return hook suppresses replacement before Play");
                }
                Find.MusicManagerPlay.ForcePlaySong(DefDatabase<SongDef>.GetNamed("A_New_Day"),false);
                MusicCheck(replacement.isPlaying && replacement.clip!=null && replacement.volume==0f
                    && oldSources.All(s=>s==null || s.volume==0f), "actual engine Play writes leave old and replacement base sources silent");
            }
            finally
            {
                replacement.Stop(); replacement.volume=0f; field.SetValue(manager,original); UnityEngine.Object.Destroy(go);
                MemoryNightMusicAdapter.ApplyNow();
            }
        }
        private static void MusicBegin()
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
            MusicCheck(MemoryNight.Start(projector), "real projector start creates a gathering");
            musicLord = map.lordManager.lords.Last(l => l.LordJob is LordJob_MFMemoryNight);
            musicJob = (LordJob_MFMemoryNight)musicLord.LordJob;
            MusicCheck(musicLord.CurLordToil is LordToil_MFMemoryNight, "live memory-night toil is the music owner candidate");
            // The music waits for people to arrive; these checks are about the music, so they skip the walk.
            Traverse.Create(musicJob).Field("clock").Method("BeginWatching").GetValue();
            musicJob.EnsureAudience();
        }
        private static void MusicRemove()
        {
            if (musicLord != null && map.lordManager.lords.Contains(musicLord)) map.lordManager.RemoveLord(musicLord);
        }
        private static void MusicShuffleChecks()
        {
            string[] original = MemoryNightMusic.ProbeBag(); string previous = MemoryNightMusic.LastTrack;
            for (int seed = 0; seed < 128; seed++)
            {
                Rand.PushState(seed);
                try
                {
                    MemoryNightMusic.ProbeRestoreHistory(Array.Empty<string>(), "");
                    string last = "";
                    for (int cycle = 0; cycle < 4; cycle++)
                    {
                        var seen = new HashSet<string>();
                        for (int n = 0; n < 3; n++)
                        {
                            string key = MemoryNightMusic.ProbePickTrack();
                            if (key == last || !seen.Add(key)) throw new InvalidOperationException("shuffle repeat seed " + seed);
                            last = key;
                        }
                        if (seen.Count != 3) throw new InvalidOperationException("shuffle coverage seed " + seed);
                    }
                }
                finally { Rand.PopState(); }
            }
            MemoryNightMusic.ProbeRestoreHistory(original, previous);
            MusicCheck(true, "128 seeds x four bags = 1536 selections cover all three without immediate repeats");
        }
        private static void MemoryMusicStep()
        {
            switch (phase)
            {
                case 0:
                    FxSet(false); Setup(); musicVolumeBefore = Prefs.VolumeMusic;
                    MusicCheck(musicVolumeBefore > 0f && AudioListener.volume == 0f, "disposable music setting is positive; master mute is excluded from source-gain evidence");
                    musicTrace = new StreamWriter(Path.Combine(dir, "music-frames.csv"));
                    musicTrace.WriteLine("frame,real_seconds,probe_phase,state,base_volume,event_volume,base_gain,event_samples,track,reason,job_completed");
                    projector = (Building_PhotoProjector)ThingMaker.MakeThing(ThingDef.Named("MF_Projector"), ThingDefOf.Steel);
                    projector.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(projector, center + new IntVec3(0,0,-5), map, Rot4.North);
                    musicProjectorId = projector.GetUniqueLoadID();
                    testRec = PhotoStore.Instance.Request(map, center, "group", "music-fixture-photo", "Memory night music fixture", "", new[] { p1,p2 });
                    Find.MusicManagerPlay.MusicUpdate();
                    Find.MusicManagerPlay.ForcePlaySong(DefDatabase<SongDef>.GetNamed("A_New_Day"), false);
                    Wait(.7f); phase = 1; return;
                case 1:
                    if (!testRec.HasFile) return;
                    var store = PhotoStore.Instance;
                    for (int i = 1; i <= 2; i++)
                    {
                        var record = new PhotoRecord { id="music-film-"+i,kind="group",file="music-film-"+i+".png",title="Music film fixture",ticksAbs=testRec.ticksAbs+i,year=testRec.year,aspect=testRec.aspect };
                        File.Copy(store.FilePath(testRec),store.FilePath(record),true);
                        Traverse.Create(store).Field("records").GetValue<List<PhotoRecord>>().Add(record);
                    }
                    store.NotifyChanged(); MusicCheck(projector.ReelCount >= 3, "projector has three real PNG files");
                    phase=100; return;
                case 100:
                    if (!MemoryNightMusic.ProbeLoadClips()) return;
                    foreach (string key in MemoryNightMusic.TrackKeys)
                    {
                        var clip = MemoryNightMusic.ClipFor(key);
                        MusicCheck(clip != null && clip.channels == 2 && clip.frequency == 48000 && Mathf.Abs(clip.length-179.9f)<.25f,
                            "native decoded selected clip " + key + " ("+F(clip.length)+" seconds)");
                    }
                    MusicShuffleChecks();
                    MusicCheck(MemoryNightMusicAdapter.TryAcquire(out string adapterReason), "installed music adapter acquires sources: " + adapterReason);
                    MusicCheck(MemoryNightMusicAdapter.MaxBaseVolume > .0001f, "actual base music source starts with positive volume before ducking");
                    MemoryNightMusicAdapter.Release();
                    musicRealClips=MemoryNightMusic.TrackKeys.Select(MemoryNightMusic.ClipFor).ToArray();
                    MemoryNightMusic.ProbeUseClips(new AudioClip[] { null,musicRealClips[1],musicRealClips[2] });
                    MusicBegin(); Wait(.4f); phase=110; return;
                case 110:
                    MusicCheck(MusicIdle && !musicJob.MusicStarted && MemoryNightMusicAdapter.MaxBaseVolume>.0001f,
                        "missing clip fixture leaves original BGM playing and gathering intact");
                    MusicRemove(); MemoryNightMusic.ProbeUseClips(musicRealClips);
                    MusicBegin(); Wait(.2f); phase=2; return;
                case 2:
                    MusicCheck(MemoryNightMusic.State == "BaseOut" && !musicJob.MusicStarted, "base fade begins while event has never started");
                    MusicCheck(MemoryNightMusic.EventSource == null || !MemoryNightMusic.EventSource.isPlaying, "event source remains stopped throughout base fade");
                    phase=3; return;
                case 3:
                    if (!MusicPlaying) return;
                    MusicCheck(MemoryNightMusicAdapter.IsBaseSilent && MemoryNightMusic.BaseGain==0f, "event starts only after actual base silence");
                    MusicCheck(!RuntimeAudio.Read<bool>(MemoryNightMusic.EventSource, "loop") && MemoryNightMusic.EventSource.pitch==1f, "event uses original speed and does not loop");
                    MusicCheck(Mathf.Abs(MemoryNightMusic.EventSource.volume-Prefs.VolumeMusic*.8f)<.0001f, "event follows music slider at default 80 percent");
                    Find.TickManager.Pause();
                    musicBeforeSamples=RuntimeAudio.Samples(MemoryNightMusic.EventSource); musicPausedTick=Find.TickManager.TicksGame;
                    Wait(.8f); phase=4; return;
                case 4:
                    MusicCheck(Find.TickManager.Paused && Find.TickManager.TicksGame==musicPausedTick && RuntimeAudio.Samples(MemoryNightMusic.EventSource)>musicBeforeSamples,
                        "actual audio advances while gameplay remains paused: paused="+Find.TickManager.Paused+", ticks="+musicPausedTick+"/"+Find.TickManager.TicksGame+", samples="+musicBeforeSamples+"/"+RuntimeAudio.Samples(MemoryNightMusic.EventSource));
                    Prefs.VolumeMusic=.23f; Find.MusicManagerPlay.MusicUpdate(); Wait(.2f); phase=5; return;
                case 5:
                    MusicCheck(Mathf.Abs(MemoryNightMusic.EventSource.volume-.23f*.8f)<.0001f && MemoryNightMusicAdapter.IsBaseSilent, "music slider changes event volume without leaking base music");
                    Prefs.VolumeMusic=musicVolumeBefore;
                    Find.MusicManagerPlay.ForcePlaySong(DefDatabase<SongDef>.GetNamed("Moving_On"),false);
                    MusicCheck(MemoryNightMusicAdapter.IsBaseSilent, "base ForcePlaySong during event cannot leak volume");
                    var runtimeType=AccessTools.TypeByName("RimTunes.RimTunesMod");
                    if (runtimeType!=null)
                    {
                        var runtime=AccessTools.Property(runtimeType,"Runtime").GetValue(null,null);
                        var fade=AccessTools.Property(runtime.GetType(),"Fade").GetValue(runtime,null);
                        AccessTools.Method(fade.GetType(),"CancelAndPlay").Invoke(fade,new object[] { DefDatabase<SongDef>.GetNamed("A_New_Day"),null });
                        AccessTools.Method(fade.GetType(),"FadeOutAndPlay").Invoke(fade,new object[] { DefDatabase<SongDef>.GetNamed("Moving_On"),.25f,.15f,null });
                        Wait(1.2f); phase=105; return;
                    }
                    phase=105; return;
                case 105:
                    MusicCheck(MusicPlaying && MemoryNightMusicAdapter.IsBaseSilent, "base song change and any installed RimTunes fade remain silent underneath event");
                    MusicSourceReplacementCheck();
                    var type=AccessTools.TypeByName("RimTunes.RimTunesMod");
                    if (type!=null)
                    {
                        var runtime=AccessTools.Property(type,"Runtime").GetValue(null,null);
                        var volume=AccessTools.Property(runtime.GetType(),"Volume").GetValue(runtime,null);
                        var field=AccessTools.Field(volume.GetType(),"_normalizationGain");
                        var apply=AccessTools.Method(volume.GetType(),"ApplyVolume");
                        object original=field.GetValue(volume);
                        try
                        {
                            field.SetValue(volume,.63f); apply.Invoke(volume,Array.Empty<object>());
                            MusicCheck(MemoryNightMusicAdapter.IsBaseSilent, "RimTunes normalization final write cannot unmute base");
                        }
                        finally { field.SetValue(volume,original); apply.Invoke(volume,Array.Empty<object>()); }
                    }
                    // A night ends on its own clock now; run it out so the real transition ends the gathering.
                    Traverse.Create(musicJob).Field("clock").Field("WatchElapsed").SetValue(musicJob.DurationTicks);
                    MusicCheck(Traverse.Create(musicJob).Field("clock").Property("Finished").GetValue<bool>(), "actual night clock has run out");
                    Find.TickManager.DoSingleTick();
                    phase=6; return;
                case 6:
                    if (!MusicIdle) return;
                    MusicCheck(!MemoryNightMusic.EventSource.isPlaying && MemoryNightMusicAdapter.MaxBaseVolume>.0001f, "natural gathering timeout stops event before restoring positive base volume");
                    MusicBegin(); Wait(.3f); phase=7; return;
                case 7:
                    MusicCheck(!musicJob.MusicStarted && MemoryNightMusic.State=="BaseOut", "cancel fixture reaches base fade before event start");
                    MusicRemove(); phase=8; return;
                case 8:
                    if (!MusicIdle) return;
                    MusicCheck(!musicJob.MusicStarted && musicJob.MusicCompleted && !MemoryNightMusic.EventSource.isPlaying, "cancel during base fade never plays an event clip");
                    MusicBegin(); phase=9; return;
                case 9:
                    if (!MusicPlaying) return;
                    MusicRemove(); phase=10; return;
                case 10:
                    if (!MusicIdle) return;
                    MusicCheck(musicJob.MusicCompleted && !MemoryNightMusic.EventSource.isPlaying && MemoryNightMusicAdapter.MaxBaseVolume>.0001f, "cancel during event fades to stop then restores base");
                    MusicBegin(); phase=11; return;
                case 11:
                    if (!MusicPlaying) return;
                    Find.World.renderer.wantedMode=WorldRenderMode.Planet;
                    phase=12; return;
                case 12:
                    if (!MusicIdle) return;
                    MusicCheck(!musicJob.MusicCompleted && musicJob.MusicPosition>0f && !MemoryNightMusic.EventSource.isPlaying, "world map hides event music and preserves nonzero playback position");
                    musicSavedKey=musicJob.MusicTrackKey; musicSavedPosition=musicJob.MusicPosition;
                    Find.World.renderer.wantedMode=WorldRenderMode.None; phase=13; return;
                case 13:
                    if (!MusicPlaying) return;
                    MusicCheck(musicJob.MusicTrackKey==musicSavedKey && MemoryNightMusic.EventSource.time>=musicSavedPosition, "map return resumes same event song after a new sequential base fade");
                    MusicRemove(); MusicBegin(); phase=14; return;
                case 14:
                    if (!MusicPlaying || MemoryNightMusic.Owner!=musicJob) return;
                    MusicCheck(MemoryNightMusic.EventSource.isPlaying, "rapid owner replacement waits for old event stop and base handover");
                    MemoryNightMusic.ProbeSeek(MemoryNightMusic.EventSource.clip.length-.5f); Wait(.9f); phase=15; return;
                case 15:
                    if (!MusicIdle) return;
                    MusicCheck(musicJob.MusicCompleted && !MemoryNightMusic.EventSource.isPlaying, "natural selected-track EOF returns base and never loops");
                    Wait(.3f); phase=16; return;
                case 16:
                    MusicCheck(MusicIdle && !MemoryNightMusic.EventSource.isPlaying, "completed song does not restart in the surviving gathering");
                    MusicRemove(); MusicBegin(); phase=17; return;
                case 17:
                    if (!MusicPlaying) return;
                    projector.DeSpawn(); phase=18; return;
                case 18:
                    if (!MusicIdle) return;
                    MusicCheck(!MemoryNightMusic.EventSource.isPlaying && musicJob.MusicCompleted, "projector despawn while paused releases event and base");
                    MusicRemove(); GenSpawn.Spawn(projector,center+new IntVec3(0,0,-5),map,Rot4.North); MusicBegin(); phase=19; return;
                case 19:
                    if (!MusicPlaying) return;
                    MemoryNightMusic.ProbeSeek(8f); Wait(.4f); phase=20; return;
                case 20:
                    musicSavedKey=musicJob.MusicTrackKey; musicSavedPosition=MemoryNightMusic.EventSource.time;
                    musicSavedBag=MemoryNightMusic.ProbeBag(); musicSavedLast=MemoryNightMusic.LastTrack;
                    musicSavedGame=Current.Game;
                    MusicCheck(musicSavedPosition>7f && musicSavedBag.Length>0, "save fixture has nonzero position and nonempty remaining shuffle bag");
                    GameDataSaveLoader.SaveGame("mfmemorymusic");
                    Wait(.5f); phase=21; return;
                case 21:
                    var savePath=GenFilePaths.FilePathForSavedGame("mfmemorymusic");
                    MusicCheck(File.Exists(savePath), "real save file exists in this new disposable profile");
                    var xml=new XmlDocument(); xml.Load(savePath);
                    var positionNode=xml.SelectSingleNode("//mfMusicPosition");
                    MusicCheck(positionNode!=null && float.TryParse(positionNode.InnerText,NumberStyles.Float,CultureInfo.InvariantCulture,out musicSavedPosition) && musicSavedPosition>7f,
                        "real save XML stores a nonzero live event position");
                    GameDataSaveLoader.LoadGame("mfmemorymusic"); Wait(1.5f); phase=22; return;
                case 22:
                    MusicCheck(!ReferenceEquals(Current.Game,musicSavedGame), "load replaces the previous Game instance");
                    map=Find.CurrentMap; Find.TickManager.Pause(); Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear();
                    projector=map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoProjector>().Single(p=>p.GetUniqueLoadID()==musicProjectorId);
                    musicLord=map.lordManager.lords.Single(l=>l.LordJob is LordJob_MFMemoryNight);
                    musicJob=(LordJob_MFMemoryNight)musicLord.LordJob;
                    MusicCheck(musicJob.MusicTrackKey==musicSavedKey && musicJob.MusicStarted && !musicJob.MusicCompleted && musicJob.MusicPosition>=musicSavedPosition-.3f, "real load preserves song identity, started state and nonzero playback position");
                    MusicCheck(MemoryNightMusic.LastTrack==musicSavedLast && MemoryNightMusic.ProbeBag().SequenceEqual(musicSavedBag), "real load preserves remaining shuffle bag and previous track");
                    phase=23; return;
                case 23:
                    if (!MusicPlaying && !musicLoadDiagnosticWritten)
                    {
                        musicLoadDiagnosticWritten=true;
                        File.WriteAllText(Path.Combine(dir,"load-music-diagnostic.txt"),MemoryNightMusic.State+" / "+MemoryNightMusic.LastReason
                            +"\njob completed="+musicJob.MusicCompleted+" toil="+musicLord.CurLordToil?.GetType().Name+" projector="+projector.Spawned+" reel="+projector.ReelCount
                            +"\nDrawingMap="+WorldRendererUtility.DrawingMap+" source="+MemoryNightMusicAdapter.DebugSnapshot());
                    }
                    if (!MusicPlaying) return;
                    MusicCheck(MemoryNightMusic.EventSource.time>=musicSavedPosition-.3f && MemoryNightMusicAdapter.IsBaseSilent, "loaded gathering resumes its song through verified base silence");
                    MusicCheck(musicPlayedKeys.Count>=3, "native gathering playback reached all three selected songs");
                    CloseLog(); Shot("memory-night-music.png"); Wait(.5f); phase=24; return;
                case 24:
                    Require("memory-night-music.png");
                    AccessTools.Method(typeof(Game),nameof(Game.Dispose)).Invoke(Current.Game,Array.Empty<object>());
                    MusicCheck(MemoryNightMusic.EventSource==null && MemoryNightMusic.BaseGain==1f, "real Game.Dispose hook stops event source before releasing base gate");
                    Prefs.VolumeMusic=musicVolumeBefore;
                    MusicCheck(musicSamples>100 && musicEventPositive>10 && musicBasePositive>10, "frame audit has positive event and base samples, not an empty or wholly muted input");
                    musicTrace.Dispose(); musicTrace=null;
                    File.WriteAllText(Path.Combine(dir,"memory-music-result.json"),"{\"ok\":true,\"adapter\":"+Json(MemoryNightMusicAdapter.AdapterName)
                        +",\"frames\":"+musicSamples+",\"eventPositiveFrames\":"+musicEventPositive+",\"basePositiveFrames\":"+musicBasePositive
                        +",\"checks\":["+string.Join(",",checks.Select(Json))+"],\"limits\":\"Disposable profile; actual selected OGG clips and AudioSources. Master muted: source gain/time evidence, not listening approval or full mod-pack compatibility.\"}");
                    active=false; Application.Quit(); return;
            }
        }
    }
}
