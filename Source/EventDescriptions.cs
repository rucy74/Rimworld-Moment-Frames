using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace MomentFrames
{
    /// <summary>More specific wording for an already captured event. These prefixes never change its photo kind, deduplication key,
    /// capture settings or timing. Unknown ritual patterns retain their existing generic wording.</summary>
    public static class EventDescriptions
    {
        private sealed class RitualWording
        {
            internal readonly string behavior, prefix;
            internal RitualWording(string behavior, string prefix) { this.behavior = behavior; this.prefix = prefix; }
        }

        // Exact pattern/behavior pairs in the installed vanilla Ideology Defs. A mod using a different pattern or behavior is not
        // inferred to perform these actions merely because a type, translated label or defName contains a familiar word.
        private static readonly Dictionary<string, RitualWording> Rituals = new Dictionary<string, RitualWording>(StringComparer.Ordinal)
        {
            { "CelebrationSkyLanterns", new RitualWording("CelebrationSkyLanterns", "sky_lanterns") },
            { "CelebrationTree", new RitualWording("CelebrationTree", "tree_celebration") },
            { "CelebrationPartyDanceDrum", new RitualWording("CelebrationPartyDanceDrum", "drum_dance") },
            { "CelebrationPartyDanceTech", new RitualWording("DancePartyTech", "tech_dance") },
            { "FeastCannibal", new RitualWording("FeastCannibal", "cannibal_feast") },
            { "CelebrationPartyChat", new RitualWording("DateRitual", "date_festival") },
            { "Execution", new RitualWording("Execution", "execution") },
            { "SacrificePrisoner", new RitualWording("SacrificePrisoner", "prisoner_sacrifice") },
            { "SacrificeAnimal", new RitualWording("SacrificeAnimal", "animal_sacrifice") },
            { "ScarificationCeremony", new RitualWording("ScarificationCeremony", "scarification") },
            { "BlindingCeremony", new RitualWording("BlindingCeremony", "blinding") },
            { "GladiatorDuel", new RitualWording("GladiatorDuel", "duel") }
        };

        /// <summary>Null means use the existing celebration/ritual candidates. Both Def names must match a known vanilla pair.</summary>
        public static string RitualPrefix(string pattern, string behavior) =>
            pattern != null && behavior != null && Rituals.TryGetValue(pattern, out var wording) && wording.behavior == behavior
                ? wording.prefix : null;

        public static string RitualPrefix(Precept_Ritual ritual) =>
            RitualPrefix(ritual?.sourcePattern?.defName, ritual?.behavior?.def?.defName);

        /// <summary>End-of-event hooks queue a later scene, sometimes on a map viewed much later. Keep the observed event facts,
        /// without claiming that its performance or ceremony is still visible in the photograph.</summary>
        public static string Afterward(string title, string instrument, string fallback)
        {
            if (!instrument.NullOrEmpty() && DescriptionCandidates.HasText("MF_ConcertAfterwardInstrument"))
                return "MF_ConcertAfterwardInstrument".Translate(title, instrument);
            return DescriptionCandidates.HasText("MF_EventAfterward") ? "MF_EventAfterward".Translate(title).ToString() : fallback;
        }

        /// <summary>A concert uses the party capture route in vanilla. Record only an instrument currently targeted by its organizer's
        /// real instrument-playing job. The gathering spot alone cannot prove which instrument was played: vanilla may choose another nearby.
        /// A completed/cancelled event without that observed job gets concert wording without an instrument claim.</summary>
        public static void Party(LordJob_Joinable_Party job, out string title, out string prefix, out string instrument)
        {
            Pawn organizer = job?.Organizer;
            string host = organizer?.LabelShort;
            title = "MF_Party".Translate(host ?? "?");
            prefix = "party";
            instrument = null;
            if (!(job is LordJob_Joinable_Concert)) return;

            prefix = "concert";
            if (!host.NullOrEmpty() && DescriptionCandidates.HasText("MF_ConcertHost")) title = "MF_ConcertHost".Translate(host);
            else if (host.NullOrEmpty() && DescriptionCandidates.HasText("MF_ConcertPlain")) title = "MF_ConcertPlain".Translate();

            var current = organizer?.CurJob;
            if (current?.def != JobDefOf.Play_MusicalInstrument) return;
            if (!(current.targetA.Thing is Building_MusicalInstrument actual) || !actual.Spawned || actual.Map != job.Map || !actual.IsBeingPlayed) return;
            string label = actual.def.LabelCap;
            if (!label.NullOrEmpty()) instrument = label;
        }
    }
}
