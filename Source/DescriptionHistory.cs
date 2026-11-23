using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace MomentFrames
{
    /// <summary>Per-save usage of complete sentences. Changing eligible facts keeps the other candidates' history.</summary>
    public sealed class DescriptionHistory : IExposable
    {
        public const int RecentLimit = 5;
        private List<Bag> bags = new List<Bag>();
        private List<string> recentForms = new List<string>();
        private static string Form(string key) => key.Substring(key.LastIndexOf('_') + 1);

        public sealed class Bag : IExposable
        {
            public string prefix;
            public List<string> used = new List<string>();
            public List<string> recent = new List<string>();

            public void ExposeData()
            {
                Scribe_Values.Look(ref prefix, "prefix");
                Scribe_Collections.Look(ref used, "used", LookMode.Value);
                Scribe_Collections.Look(ref recent, "recent", LookMode.Value);
                if (Scribe.mode == LoadSaveMode.PostLoadInit)
                {
                    if (used == null) used = new List<string>();
                    if (recent == null) recent = new List<string>();
                }
            }
        }

        public string Pick(string prefix, IList<string> eligible, Func<int, int> randomIndex)
        {
            if (string.IsNullOrEmpty(prefix) || eligible == null) return null;
            var keys = eligible.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
            if (keys.Count == 0) return null;
            var bag = bags.FirstOrDefault(b => b.prefix == prefix);
            if (bag == null) { bag = new Bag { prefix = prefix }; bags.Add(bag); }
            var unused = keys.Where(k => !bag.used.Contains(k)).ToList();
            if (unused.Count == 0)
            {
                // Refill this eligible set only. E.g. another group-size/detail pool is still remembered.
                bag.used.RemoveAll(keys.Contains);
                unused = keys;
            }
            var pool = unused.Where(k => !bag.recent.Contains(k)).ToList();
            if (pool.Count == 0)
            {
                // Small/changed pools cannot always avoid all five. Keep unused priority and relax oldest first.
                pool.Add(bag.recent.First(unused.Contains));
            }
            string picked = pool[randomIndex(pool.Count)];
            if (prefix.StartsWith("MF_Caption_", StringComparison.Ordinal))
            {
                var varied = pool.Where(k => !recentForms.Contains(Form(k))).ToList();
                if (varied.Count > 0) picked = varied[randomIndex(varied.Count)];
                recentForms.Add(Form(picked));
                if (recentForms.Count > 3) recentForms.RemoveAt(0);
            }
            bag.used.Add(picked);
            bag.recent.Remove(picked);
            bag.recent.Add(picked);
            if (bag.recent.Count > RecentLimit) bag.recent.RemoveAt(0);
            return picked;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref bags, "bags", LookMode.Deep);
            Scribe_Collections.Look(ref recentForms, "recentForms", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (bags == null) bags = new List<Bag>();
                if (recentForms == null) recentForms = new List<string>();
                bags.RemoveAll(b => b == null || string.IsNullOrEmpty(b.prefix));
            }
        }
    }
}
