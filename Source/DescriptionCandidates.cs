using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Verse;

namespace MomentFrames
{
    /// <summary>Prewritten local sentences. Only templates whose recorded facts exist are eligible; no model or network call.</summary>
    public static class DescriptionCandidates
    {
        public const int MaxVariants = 24;
        private static readonly Regex Slots = new Regex(@"\{([0-9]+)\}");

        public static bool Fits(string template, string[] values)
        {
            if (string.IsNullOrWhiteSpace(template)) return false;
            foreach (Match slot in Slots.Matches(template))
                if (!int.TryParse(slot.Groups[1].Value, out int index) || values == null || index >= values.Length || string.IsNullOrWhiteSpace(values[index])) return false;
            return true;
        }

        public static List<string> EligibleKeys(string prefix, int peopleCount, string[] values, Func<string, string> lookup)
        {
            List<string> PreferNamed(List<string> keys)
            {
                // Preserve the identity in portraits, the couple and the party host when it is known.
                if (prefix != "MF_Caption_role" && prefix != "MF_Caption_wedding" && prefix != "MF_Caption_party") return keys;
                var named = keys.Where(key => Slots.Matches(lookup(key)).Cast<Match>().Any(slot =>
                    (slot.Groups[1].Value == "0" || slot.Groups[1].Value == "1") && values != null
                    && int.Parse(slot.Groups[1].Value) < values.Length && !string.IsNullOrWhiteSpace(values[int.Parse(slot.Groups[1].Value)]))).ToList();
                return named.Count > 0 ? named : keys;
            }
            List<string> Read(string start)
            {
                var keys = new List<string>();
                for (int i = 1; i <= MaxVariants; i++)
                {
                    string key = start + "_" + i;
                    if (Fits(lookup(key), values)) keys.Add(key);
                }
                return keys;
            }
            if (string.IsNullOrEmpty(prefix)) return new List<string>();
            if (values != null && values.Length > 4 && !string.IsNullOrWhiteSpace(values[4]))
            {
                var detailed = Read(prefix + "_detail");
                if (detailed.Count > 0) return PreferNamed(detailed);
            }
            if (peopleCount >= 0)
            {
                string context = peopleCount == 0 ? "scene" : peopleCount == 1 ? "solo" : "group";
                var specific = Read(prefix + "_" + context);
                if (specific.Count > 0) return PreferNamed(specific);
            }
            return PreferNamed(Read(prefix));
        }

        public static List<string> WithoutRepeat(IList<string> keys, string last)
        {
            return keys.Count > 1 ? keys.Where(key => key != last).ToList() : new List<string>(keys);
        }

        /// <summary>Replace slots in the template once; braces inside a recorded name are ordinary text.</summary>
        public static string Format(string template, string[] values)
        {
            if (!Fits(template, values)) return "";
            return Slots.Replace(template, slot => values[int.Parse(slot.Groups[1].Value)]);
        }

        /// <summary>CanTranslate asks only the game's current language, while Translate itself falls back to English: a language without this text must still get it.</summary>
        public static bool HasText(string key) => key.CanTranslate() || LanguageDatabase.defaultLanguage.HaveTextForKey(key);

        public static string Lookup(string key) => HasText(key) ? key.Translate().ToString() : null;

        public static string PickKey(string prefix, int peopleCount, string[] values)
        {
            var keys = EligibleKeys(prefix, peopleCount, values, Lookup);
            if (keys.Count == 0) return null;
            // The game's registry owns the bag; a new colony never inherits another save's choices.
            var history = PhotoStore.Instance?.Descriptions;
            return history == null ? keys[Rand.Range(0, keys.Count)]
                : history.Pick(prefix, keys, n => Rand.Range(0, n));
        }
    }
}
