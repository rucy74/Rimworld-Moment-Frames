using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace MomentFrames
{
    /// <summary>RimTalk (cj.rimtalk, AI-written colonist dialogue) — when it is active, each colonist's context for a conversation gets a line
    /// about the photos they are in ("Photos Victoria is in: Wedding: Victoria ♥ Karl (3 days ago, with Karl) · ..."), so they can bring those
    /// days up. Through RimTalk's public prompt API (RimTalkPromptAPI.InjectPawnSection, after the Thoughts section), by reflection: no hard
    /// dependency, nothing happens without RimTalk, and no extra AI calls — the line rides the conversations RimTalk makes anyway.
    /// RimTalk builds the context on the main thread before sending the request, so the provider may read the photo store.</summary>
    [StaticConstructorOnStartup]
    public static class RimTalkBridge
    {
        public const string PackageId = "cj.rimtalk", ModId = "MomentFrames", SectionName = "MomentFramesPhotos";
        public const int MaxPhotos = 3, MaxOthers = 3;

        /// <summary>True once the section is registered with RimTalk.</summary>
        public static bool Registered { get; private set; }

        /// <summary>Why it is not registered (RimTalk inactive, or its API changed); null when registered.</summary>
        public static string Why { get; private set; }

        static RimTalkBridge()
        {
            try { Register(); }
            catch (Exception e)
            {
                Why = "failed: " + e.GetType().Name + ": " + e.Message;
                Log.Warning("[MomentFrames] RimTalk bridge not registered (" + Why + ")");
            }
        }

        private static void Register()
        {
            if (!ModsConfig.IsActive(PackageId)) { Why = "RimTalk not active"; return; }
            Type api = AccessTools.TypeByName("RimTalk.API.RimTalkPromptAPI");
            Type pawnCats = AccessTools.TypeByName("RimTalk.API.ContextCategories+Pawn");
            Type positions = AccessTools.TypeByName("RimTalk.API.ContextHookRegistry+InjectPosition");
            var inject = api == null ? null : AccessTools.Method(api, "InjectPawnSection");
            var thoughts = pawnCats == null ? null : AccessTools.Field(pawnCats, "Thoughts");
            if (inject == null || thoughts == null || positions == null) { Why = "RimTalk's prompt API not found (api " + (api != null) + ", thoughts " + (thoughts != null) + ", positions " + (positions != null) + ")"; return; }
            object after = Enum.Parse(positions, "After");
            Func<Pawn, string> provider = Section;
            inject.Invoke(null, new object[] { ModId, SectionName, thoughts.GetValue(null), after, provider, 100 });
            Registered = true;
            Why = null;
        }

        /// <summary>The line for one colonist: their latest photos (not test shots), with how long ago and who else is in them. Null when none.</summary>
        public static string Section(Pawn pawn)
        {
            try
            {
                var store = PhotoStore.Instance;
                if (pawn == null || store == null || Current.ProgramState != ProgramState.Playing) return null;
                // The latest by when they happened, not by when they were added: a relic or a back-dated photo is added late (r110).
                var recs = new List<PhotoRecord>();
                var all = store.Records;
                for (int i = 0; i < all.Count; i++)
                {
                    var r = all[i];
                    if (!r.HasFile || r.kind == Kinds.Test || !r.Has(pawn)) continue;
                    int at = recs.FindIndex(x => x.ticksAbs < r.ticksAbs);
                    if (at < 0) recs.Add(r); else recs.Insert(at, r);
                    if (recs.Count > MaxPhotos) recs.RemoveAt(recs.Count - 1);
                }
                if (recs.Count == 0) return null;
                int now = Find.TickManager.TicksAbs;
                string me = pawn.LabelShort;
                var items = recs.Select(r =>
                {
                    string ago = "MF_TalkAgo".Translate(Math.Max(0, now - r.ticksAbs).ToStringTicksToPeriod());
                    var others = r.people.Where(n => n != me).Take(MaxOthers).ToList();
                    return others.Count == 0 ? "MF_TalkPhoto".Translate(r.title, ago).ToString() : "MF_TalkPhotoWith".Translate(r.title, ago, string.Join(", ", others)).ToString();
                });
                return "MF_TalkPhotos".Translate(me, string.Join(" · ", items));
            }
            catch (Exception e)
            {
                Log.WarningOnce("[MomentFrames] RimTalk photo line: " + e.Message, 0x4d465254);
                return null;
            }
        }
    }
}
