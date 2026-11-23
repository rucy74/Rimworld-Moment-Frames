using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Temporary gain on the installed music engines' final source writes. The engines keep their timelines,
    /// settings and fades. In particular, retaining the unducked writes lets a RimTunes fade keep advancing at gain zero.</summary>
    internal static class MemoryNightMusicAdapter
    {
        private const string HarmonyId = "rucy74.momentframes.memorynightmusic";
        private const float SilenceLimit = 0.000001f;
        private static readonly Dictionary<AudioSource, SourceVolume> sources = new Dictionary<AudioSource, SourceVolume>();
        private static readonly Dictionary<MethodBase, int> expectedWrites = new Dictionary<MethodBase, int>();
        private static readonly FieldInfo vanillaSource = AccessTools.Field(typeof(MusicManagerPlay), "audioSource");
        private static readonly MethodInfo volumeSetter = AccessTools.PropertySetter(typeof(AudioSource), "volume");
        private static Harmony harmony;
        private static bool acquired;
        private static float baseGain = 1f;
        private static string lastError;
        private static PropertyInfo rimRuntime, rimAudio, rimVolume, rimEffective;
        private static FieldInfo rimBackup;
        private static bool rimTunes;

        private sealed class SourceVolume
        {
            public AudioSource Source;
            public MusicManagerPlay Manager;
            public bool RimTunes;
            public float RawVolume;
            public float RimFadeFactor = 1f;
        }

        internal static float BaseGain => baseGain;
        internal static string AdapterName => rimTunes ? "vanilla + RimTunes final volume writes" : "vanilla final volume writes";
        internal static string LastError => lastError;

        /// <summary>Only initialized music sources, including a still-alive previous source. Never the event source or SFX.</summary>
        internal static AudioSource[] BaseSources
        {
            get
            {
                ObserveSources();
                return sources.Keys.Where(s => s != null).ToArray();
            }
        }

        internal static float MaxBaseVolume
        {
            get
            {
                var current = BaseSources;
                if (lastError != null) return float.PositiveInfinity;
                float maximum = 0f;
                foreach (var source in current)
                {
                    float value = source.volume;
                    if (float.IsNaN(value) || float.IsInfinity(value)) return float.PositiveInfinity;
                    maximum = Mathf.Max(maximum, value);
                }
                return maximum;
            }
        }

        internal static bool IsBaseSilent => acquired && baseGain == 0f && BaseSources.Length > 0
            && lastError == null && MaxBaseVolume <= SilenceLimit;

        internal static bool TryAcquire(out string reason)
        {
            reason = null;
            if (acquired)
            {
                reason = lastError;
                return lastError == null;
            }
            lastError = null;
            try
            {
                EnsurePatches();
                if (Find.MusicManagerPlay == null) throw new InvalidOperationException("The game music manager is unavailable.");
                sources.Clear();
                baseGain = 1f;
                acquired = true;
                ObserveSources();
                if (lastError != null) throw new InvalidOperationException(lastError);
                if (sources.Count == 0) throw new InvalidOperationException("No initialized base music source was found.");
                return true;
            }
            catch (Exception e)
            {
                reason = "Base music adapter unavailable: " + e.GetBaseException().Message;
                Release();
                lastError = reason;
                return false;
            }
        }

        internal static void SetBaseGain(float gain, AudioSource nativeWriteSource = null)
        {
            if (!acquired) return;
            // A broken fade value must not make the base engine audible underneath the event.
            baseGain = float.IsNaN(gain) || float.IsInfinity(gain) ? 0f : Mathf.Clamp01(gain);
            ApplyNow(nativeWriteSource);
        }

        internal static void ApplyNow(AudioSource nativeWriteSource = null)
        {
            if (!acquired) return;
            ObserveSources(nativeWriteSource);
            foreach (var state in sources.Values.ToArray())
                if (state.Source != null && !ReferenceEquals(state.Source, nativeWriteSource))
                    state.Source.volume = Sanitize(state.RawVolume) * baseGain;
        }

        /// <summary>Call only after the event source has stopped. Read current settings, rather than restoring a saved slider.</summary>
        internal static void Release(AudioSource nativeWriteSource = null)
        {
            baseGain = 1f;
            try
            {
                if (acquired)
                {
                    ObserveSources(nativeWriteSource);
                    foreach (var state in sources.Values.ToArray())
                    {
                        if (state.Source == null || ReferenceEquals(state.Source, nativeWriteSource)) continue;
                        float raw = state.RawVolume;
                        try
                        {
                            if (state.RimTunes) raw = RimRawVolume() * state.RimFadeFactor;
                            else if (state.Manager != null) raw = state.Manager.CurSanitizedVolume;
                        }
                        catch { /* A disposed engine still gets its last actual unducked request. */ }
                        // This is MF's restoration write, not a replaced native setter.
                        try { state.Source.volume = Sanitize(raw); }
                        catch (Exception e) { RememberError(e); }
                    }
                }
            }
            catch (Exception e) { RememberError(e); }
            finally { acquired = false; }
        }

        internal static string DebugSnapshot()
        {
            var sb = new StringBuilder();
            sb.Append(AdapterName).Append(" acquired=").Append(acquired).Append(" gain=").Append(baseGain)
                .Append(" error=").Append(lastError ?? "none");
            foreach (var source in BaseSources)
            {
                var state = sources[source];
                sb.Append(" | ").Append(source.GetInstanceID()).Append(':').Append(source.name)
                    .Append(" actual=").Append(source.volume).Append(" raw=").Append(state.RawVolume)
                    .Append(" playing=").Append(source.isPlaying).Append(" samples=").Append(RuntimeAudio.Samples(source))
                    .Append(" clip=").Append(source.clip == null ? "none" : source.clip.name);
            }
            return sb.ToString();
        }

        private static float Sanitize(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp(value, 0f, 1000f);

        private static void ObserveSources(AudioSource nativeWriteSource = null)
        {
            try
            {
                foreach (var dead in sources.Keys.Where(s => s == null).ToArray()) sources.Remove(dead);
                var manager = Find.MusicManagerPlay;
                if (manager != null)
                {
                    var source = (AudioSource)vanillaSource.GetValue(manager);
                    Track(source, manager, rimTunes, !ReferenceEquals(source, nativeWriteSource));
                }
                if (rimTunes)
                {
                    object runtime = rimRuntime.GetValue(null, null);
                    if (runtime == null) throw new InvalidOperationException("RimTunes runtime was removed.");
                    object provider = rimAudio.GetValue(runtime, null);
                    if (provider == null) throw new InvalidOperationException("RimTunes source provider is unavailable.");
                    var source = (AudioSource)rimBackup.GetValue(provider);
                    Track(source, null, true, !ReferenceEquals(source, nativeWriteSource));
                    RimRawVolume();
                }
            }
            catch (Exception e) { RememberError(e); }
        }

        private static SourceVolume Track(AudioSource source, MusicManagerPlay manager, bool isRimTunes, bool applySuppression = true)
        {
            if (source == null) return null;
            if (!sources.TryGetValue(source, out var state))
            {
                state = new SourceVolume { Source = source, Manager = manager, RimTunes = isRimTunes, RawVolume = Sanitize(source.volume) };
                sources.Add(source, state);
                // Apply suppression before any optional engine metadata read can fail.
                if (acquired && applySuppression) source.volume = state.RawVolume * baseGain;
                if (isRimTunes)
                {
                    try
                    {
                        float nominal = RimRawVolume();
                        if (nominal > 0f) state.RimFadeFactor = state.RawVolume / nominal;
                    }
                    catch (Exception e) { RememberError(e); }
                }
            }
            else if (manager != null) state.Manager = manager;
            return state;
        }

        private static float RimRawVolume()
        {
            object runtime = rimRuntime.GetValue(null, null);
            object volume = runtime == null ? null : rimVolume.GetValue(runtime, null);
            if (volume == null) throw new InvalidOperationException("RimTunes volume manager is unavailable.");
            return Sanitize((float)rimEffective.GetValue(volume, null));
        }

        private static void WriteVanillaVolume(AudioSource source, float raw)
        {
            float output = raw;
            if (acquired)
            {
                try
                {
                    var manager = Find.MusicManagerPlay;
                    if (manager != null && (AudioSource)vanillaSource.GetValue(manager) != source) manager = null;
                    var state = Track(source, manager, false, false);
                    state.RawVolume = Sanitize(raw);
                    state.RimTunes = false;
                    output = state.RawVolume * baseGain;
                }
                catch (Exception e) { output = FallbackAfterError(raw, e, source); }
            }
            // The original native operation must execute once, including its null/destroyed-source errors.
            source.volume = output;
        }

        private static void WriteRimTunesVolume(AudioSource source, float raw)
        {
            float output = raw;
            if (acquired)
            {
                try
                {
                    var state = Track(source, null, true, false);
                    state.RawVolume = Sanitize(raw);
                    state.RimTunes = true;
                    float nominal = RimRawVolume();
                    if (nominal > 0f) state.RimFadeFactor = state.RawVolume / nominal;
                    output = state.RawVolume * baseGain;
                }
                catch (Exception e) { output = FallbackAfterError(raw, e, source); }
            }
            source.volume = output;
        }

        private static void VanillaInitialized(MusicManagerPlay __instance)
        {
            if (!acquired) return;
            try { Track((AudioSource)vanillaSource.GetValue(__instance), __instance, rimTunes); }
            catch (Exception e) { FallbackAfterError(0f, e); }
        }

        private static void RimSourceReturned(AudioSource __result)
        {
            if (!acquired) return;
            try { Track(__result, null, true); }
            catch (Exception e) { FallbackAfterError(0f, e); }
        }

        private static void RememberError(Exception error)
        {
            // Keep the first failure through later successful observations until a new acquisition.
            if (lastError == null) lastError = error.GetBaseException().Message;
        }

        private static float FallbackAfterError(float raw, Exception error, AudioSource nativeWriteSource = null)
        {
            RememberError(error);
            baseGain = 0f;
            // Restoring raw volume is safe only after our own event source has actually stopped.
            return MemoryNightMusic.AbortForAdapterError(lastError, nativeWriteSource) ? raw : 0f;
        }

        private static void EnsurePatches()
        {
            if (vanillaSource == null || vanillaSource.FieldType != typeof(AudioSource) || volumeSetter == null)
                throw new InvalidOperationException("The installed vanilla music source layout is unsupported.");
            if (harmony == null) harmony = new Harmony(HarmonyId);
            PatchWriter(RequiredMethod(typeof(MusicManagerPlay), "MusicUpdate"), 1);
            PatchWriter(RequiredMethod(typeof(MusicManagerPlay), "PlaySong"), 1);
            PatchPostfix(RequiredMethod(typeof(MusicManagerPlay), "InitializeMusicManager"), nameof(VanillaInitialized));

            Type mod = AccessTools.TypeByName("RimTunes.RimTunesMod");
            rimTunes = mod != null;
            if (!rimTunes) return;
            rimRuntime = RequiredProperty(mod, "Runtime");
            Type runtime = rimRuntime.PropertyType;
            rimAudio = RequiredProperty(runtime, "AudioSource");
            rimVolume = RequiredProperty(runtime, "Volume");
            rimEffective = RequiredProperty(rimVolume.PropertyType, "EffectiveVolume");
            if (rimEffective.PropertyType != typeof(float)) throw new InvalidOperationException("RimTunes EffectiveVolume changed type.");
            rimBackup = AccessTools.Field(rimAudio.PropertyType, "_backupAudioSource");
            if (rimBackup == null || rimBackup.FieldType != typeof(AudioSource)) throw new InvalidOperationException("RimTunes backup source layout changed.");
            PatchPostfix(RequiredMethod(rimAudio.PropertyType, "GetAudioSource"), nameof(RimSourceReturned));
            PatchWriter(RequiredMethod(rimVolume.PropertyType, "ApplyVolume"), 1);
            Type fade = AccessTools.TypeByName("RimTunes.Playback.FadeController");
            if (fade == null) throw new InvalidOperationException("RimTunes fade controller is unavailable.");
            PatchIterator(fade, "FadeInRoutine", 2);
            PatchIterator(fade, "FadeCoroutine", 3);
        }

        private static PropertyInfo RequiredProperty(Type type, string name)
        {
            var property = AccessTools.Property(type, name);
            if (property == null || property.GetGetMethod(true) == null) throw new MissingMemberException(type.FullName, name);
            return property;
        }

        private static MethodInfo RequiredMethod(Type type, string name)
        {
            var method = AccessTools.Method(type, name);
            if (method == null || method.GetMethodBody() == null) throw new MissingMethodException(type.FullName, name);
            return method;
        }

        private static void PatchIterator(Type parent, string name, int count)
        {
            var iterators = parent.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(t => t.Name.StartsWith("<" + name + ">", StringComparison.Ordinal)).ToArray();
            if (iterators.Length != 1) throw new InvalidOperationException("Unsupported RimTunes iterator: " + name);
            PatchWriter(RequiredMethod(iterators[0], "MoveNext"), count);
        }

        private static void PatchWriter(MethodBase method, int count)
        {
            expectedWrites[method] = count;
            if (!HasPatch(method, nameof(FinalVolumeWrites), true))
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(MemoryNightMusicAdapter), nameof(FinalVolumeWrites)) { priority = Priority.Last });
            if (!HasPatch(method, nameof(FinalVolumeWrites), true)) throw new InvalidOperationException("Music volume transpiler was not installed: " + method.Name);
        }

        private static void PatchPostfix(MethodBase method, string name)
        {
            if (!HasPatch(method, name, false))
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(MemoryNightMusicAdapter), name) { priority = Priority.Last });
            if (!HasPatch(method, name, false)) throw new InvalidOperationException("Music source observer was not installed: " + method.Name);
        }

        private static bool HasPatch(MethodBase method, string name, bool transpiler)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return false;
            var patches = transpiler ? info.Transpilers : info.Postfixes;
            return patches.Any(p => p.owner == HarmonyId && p.PatchMethod.Name == name);
        }

        private static IEnumerable<CodeInstruction> FinalVolumeWrites(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            int writes = code.Count(i => i.Calls(volumeSetter));
            if (!expectedWrites.TryGetValue(__originalMethod, out int expected) || writes != expected)
                throw new InvalidOperationException("Installed music volume writes changed: " + __originalMethod.DeclaringType.FullName
                    + "." + __originalMethod.Name + " expected " + expected + ", found " + writes);
            string sink = __originalMethod.DeclaringType == typeof(MusicManagerPlay) ? nameof(WriteVanillaVolume) : nameof(WriteRimTunesVolume);
            var replacement = AccessTools.Method(typeof(MemoryNightMusicAdapter), sink);
            foreach (var instruction in code)
            {
                if (instruction.Calls(volumeSetter))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
                yield return instruction;
            }
        }
    }
}
