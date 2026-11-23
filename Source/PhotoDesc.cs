using System.Collections.Generic;
using System;
using System.Linq;
using RimWorld;
using Verse;

namespace MomentFrames
{
    /// <summary>The sentence under a photo: picked once from the language file's templates for that kind when the record is made
    /// (MF_Caption_{kind}_1..n and eligible conditional groups; {0} = recorded people, {1} = the first recorded person,
    /// {2} = season, {3} = photographed map/place, {4} = explicit event detail, {5} = verified instrument),
    /// then kept as written — the player may edit it. Kinds without templates get no sentence.</summary>
    public static class PhotoDesc
    {
        internal static string ForRecord(PhotoRecord rec, IList<Pawn> people, Map map)
        {
            Rand.PushState(GenText.StableStringHash(rec.id ?? ""));
            try { return ForRecordCore(rec, people, map); }
            finally { Rand.PopState(); }
        }

        private static string ForRecordCore(PhotoRecord rec, IList<Pawn> people, Map map)
        {
            string title = rec.title.NullOrEmpty() ? MainTabWindow_Album.KindLabel(rec.kind) : rec.title;
            string fact = (people.Count > 0 ? "MF_PhotoFactPeople" : "MF_PhotoFactScene").Translate(title, people.Count).ToString();
            string reflection = Pick(rec.kind, people, map);
            // A fact-only entry between fuller entries gives the page a different rhythm.
            return CaptionText.Clean(fact + (reflection.NullOrEmpty() || Rand.Chance(.25f) ? "" : "\n" + reflection));
        }

        /// <summary>A replacement for a new automatically generated description, measured at successful capture.
        /// Explicit event/player text and completed records are protected. Observation and selection are separate so only a
        /// successful shutter consumes a sentence. Slots 6..10 contain the frozen subject/companion/occasion/relation/detail.</summary>
        internal static string ForCapturedContext(PhotoRecord rec, IList<Pawn> people, Map map)
        {
            if (!CanApplyCaptured(rec)) return null;
            return ComposeCaptured(rec, PhotoCaptionContext.Measure(rec, people, map), out _);
        }

        internal static bool CanApplyCaptured(PhotoRecord rec) => rec != null && rec.generatedDesc && !rec.HasFile
            && !rec.eventOnly && rec.captionTemplate.NullOrEmpty();

        internal static PhotoCaptionContext ObserveCapture(PhotoRecord rec, Map map)
        {
            if (!CanApplyCaptured(rec)) return null;
            try { return PhotoCaptionContext.Measure(rec, PhotoCapture.VisiblePawns(map).ToList(), map); }
            catch (Exception e) { Log.Warning("[MomentFrames] could not observe the caption scene: " + e.Message); return null; }
        }

        internal static bool TryApplyCaptured(PhotoRecord rec, PhotoCaptionContext context)
        {
            if (!CanApplyCaptured(rec)) return false;
            string sentence = ComposeCaptured(rec, context, out string key);
            if (sentence.NullOrEmpty()) return false;
            rec.desc = sentence;
            rec.captionScene = context.Prefix;
            rec.captionTemplate = key;
            rec.captionValues = new List<string>(context.Values);
            return true;
        }

        private static string ComposeCaptured(PhotoRecord rec, PhotoCaptionContext context, out string selectedKey)
        {
            selectedKey = null;
            if (context?.Values == null || context.People == null) return null;
            int seed = 29;
            unchecked { foreach (char c in rec.id ?? "") seed = seed * 31 + c; }
            Rand.PushState(seed);
            try
            {
                var values = context.Values;
                string key = DescriptionCandidates.PickKey(context.Prefix, context.People.Count, values);
                if (key == null) return null;
                string sentence = DescriptionCandidates.Format(DescriptionCandidates.Lookup(key), values);
                if (sentence.NullOrEmpty()) return null;
                selectedKey = key;
                return CaptionText.Clean(sentence);
            }
            finally { Rand.PopState(); }
        }

        internal static bool TryApplyFrozen(PhotoRecord rec)
        {
            if (!CanApplyCaptured(rec) || rec.captionScene.NullOrEmpty() || rec.captionValues == null) return false;
            var values = rec.captionValues.ToArray();
            Rand.PushState(GenText.StableStringHash(rec.id ?? ""));
            try
            {
                var key = DescriptionCandidates.PickKey(rec.captionScene, rec.people.Count, values);
                if (key == null) return false;
                rec.desc = CaptionText.Clean(DescriptionCandidates.Format(DescriptionCandidates.Lookup(key), values));
                rec.captionTemplate = key;
                return true;
            }
            finally { Rand.PopState(); }
        }
        public const int MaxVariants = 4;

        public static string Pick(string kind, IList<Pawn> people, Map map, string detail = null) => Pick(kind, people, map, detail, null);

        public static string Pick(string kind, IList<Pawn> people, Map map, string detail, string instrument)
        {
            // Provisional/event text has no record ID yet. A local seed must not consume gameplay Rand.
            Rand.PushState(GenText.StableStringHash(Guid.NewGuid().ToString("N")));
            try { return PickCore(kind, people, map, detail, instrument); }
            finally { Rand.PopState(); }
        }

        private static string PickCore(string kind, IList<Pawn> people, Map map, string detail, string instrument)
        {
            if (kind.NullOrEmpty()) return "";
            var recorded = people == null ? new List<Pawn>() : people.Where(p => p != null).Distinct().ToList();
            string names = string.Join(", ", recorded.Select(p => p.LabelShort));
            string first = recorded.FirstOrDefault()?.LabelShort ?? "";
            string season = "";
            try { if (map != null) season = GenLocalDate.Season(map).LabelCap(); } catch { }
            string place = "";
            try { if (map?.Parent != null) place = map.Parent.LabelCap; } catch { }
            var values = new[] { names, first, season, place, detail ?? "", instrument ?? "" };
            string prefix=kind=="party" || kind==Journey.Kind ? "MF_Caption_fact_"+kind : "MF_Caption_"+kind;
            string key = DescriptionCandidates.PickKey(prefix, recorded.Count, values);
            if(key==null && prefix!="MF_Caption_"+kind) key=DescriptionCandidates.PickKey("MF_Caption_"+kind,recorded.Count,values);
            if (key != null) return DescriptionCandidates.Format(DescriptionCandidates.Lookup(key), values);
            // Other languages/mod-provided kinds can still supply the earlier translation keys.
            int n = 0;
            while (n < MaxVariants && DescriptionCandidates.HasText("MF_Desc_" + kind + "_" + (n + 1))) n++;
            if (n == 0) return "";
            key = "MF_Desc_" + kind + "_" + Rand.RangeInclusive(1, n);
            return key.Translate(names.NullOrEmpty() ? "MF_Desc_Someone".Translate().ToString() : names,
                first.NullOrEmpty() ? "MF_Desc_Someone".Translate().ToString() : first, season,
                place.NullOrEmpty() ? Faction.OfPlayer?.Name ?? "" : place, detail ?? "").ToString();
        }

        /// <summary>Specific gathering captions fall back to the existing group in languages without the new bank.</summary>
        public static string PickSpecific(string specific, string fallback, IList<Pawn> people, Map map, string detail = null, string instrument = null)
        {
            if (!specific.NullOrEmpty())
            {
                string sentence = Pick(specific, people, map, detail, instrument);
                if (!sentence.NullOrEmpty()) return sentence;
            }
            return Pick(fallback, people, map, detail);
        }
    }
}
