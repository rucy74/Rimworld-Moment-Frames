using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace MomentFrames
{
    /// <summary>Draft-only choices: filtering never edits the selected roster, and placement only reorders valid cells.</summary>
    internal static class PhotoPreparationChoices
    {
        internal static bool MatchesName(string name, string query) => string.IsNullOrWhiteSpace(query)
            || (name ?? "").IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        internal static int ResolveReactionMode(int recorded, int? pending, int defaultMode)
            => Math.Max(0,Math.Min(2,recorded>=0?recorded:pending??defaultMode));

        internal static List<Pawn> Filter(IEnumerable<Pawn> roster, string query, Func<Pawn,bool> required = null)
            => roster.Where(p => required?.Invoke(p) == true || MatchesName(p.LabelShort,query)
                || MatchesName(p.Label,query)).ToList();

        /// <summary>Anyone who has held the start button back stays at the top until the window closes, so rows do not move under the mouse when one is unticked.</summary>
        internal static void Remember(HashSet<Pawn> flagged, IEnumerable<Pawn> problems)
        {
            foreach (var pawn in problems) flagged.Add(pawn);
        }

        /// <summary>Added to the window's line only: one unreachable person who may be left out, and nothing else in the way.</summary>
        internal static string RemoveHint(string reason, IDictionary<Pawn, string> problems, IDictionary<Pawn, string> brief, Func<Pawn, bool> removable)
        {
            if (problems.Count != 1) return "";
            var problem = problems.First();
            if (problem.Value != reason || !removable(problem.Key) || !brief.TryGetValue(problem.Key, out var why)
                || why != "MF_PreparationReachShort".Translate().ToString()) return "";
            return " " + "MF_PreparationRemoveHint".Translate();
        }

        internal static void PutSubjectAtFront(List<IntVec3> cells, int subject, IntVec3 spot, Rot4 direction)
        {
            if(subject<0 || subject>=cells.Count) return;
            int Depth(IntVec3 c) => direction == Rot4.East ? c.x-spot.x : direction == Rot4.South ? spot.z-c.z
                : direction == Rot4.West ? spot.x-c.x : c.z-spot.z;
            int Side(IntVec3 c) => direction == Rot4.East || direction == Rot4.West ? c.z-spot.z : c.x-spot.x;
            int best = Enumerable.Range(0,cells.Count).OrderBy(i=>Depth(cells[i])).ThenBy(i=>Math.Abs(Side(cells[i])))
                .ThenBy(i=>i).First();
            var prior = cells[subject]; cells[subject] = cells[best]; cells[best] = prior;
        }
    }
}
