using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
#if !MF_WAYLITH
using Verse;
#endif

namespace MomentFrames
{
    /// <summary>Keep Unity-only audio APIs out of methods JIT-compiled by the 1.7 compatibility engine.</summary>
    internal static class RuntimeAudio
    {
        internal static readonly bool Waylith = typeof(AudioClip).Assembly.GetName().Name == "UnityStubs";
        private static readonly PropertyInfo samplePosition = typeof(AudioSource).GetProperty("timeSamples");
        private static readonly Dictionary<string, MemberInfo> sourceReads = new Dictionary<string, MemberInfo>();

        internal static bool Ready(AudioClip clip, bool generated = false)
        {
            if (clip == null) return false;
#if MF_WAYLITH
            return clip.samples > 0 && clip.frequency > 0;
#else
            return Waylith ? clip.samples > 0 && clip.frequency > 0 : UnityReady(clip, generated);
#endif
        }

#if !MF_WAYLITH
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool UnityReady(AudioClip clip, bool generated)
            => (generated ? clip.loadState : RuntimeAudioClipLoader.Manager.GetAudioClipLoadState(clip)) == AudioDataLoadState.Loaded;
#endif

        // Diagnostics only: 1.7 exposes time in seconds, while Unity also exposes sample position.
        internal static int Samples(AudioSource source)
        {
            if (source == null) return 0;
            if (samplePosition?.GetGetMethod(true) != null) return (int)samplePosition.GetValue(source, null);
            return source.clip != null ? (int)Math.Round((double)source.time * source.clip.frequency) : 0;
        }

        internal static T Read<T>(AudioSource source, string name)
        {
            if (!sourceReads.TryGetValue(name, out MemberInfo member))
            {
                PropertyInfo property = typeof(AudioSource).GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                member = property?.GetGetMethod(true) != null ? (MemberInfo)property
                    : typeof(AudioSource).GetField("_" + name, BindingFlags.NonPublic | BindingFlags.Instance);
                if (member == null) throw new MissingMemberException(typeof(AudioSource).FullName, name);
                sourceReads.Add(name, member);
            }
            return (T)(member is PropertyInfo p ? p.GetValue(source, null) : ((FieldInfo)member).GetValue(source));
        }
    }
}
