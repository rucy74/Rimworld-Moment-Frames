using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace MomentFrames
{
    /// <summary>A moment of a colonist's life that has no photo: joining (or being born), lovers, engagement, marriage, a break-up or divorce,
    /// a bond with an animal, being carried off by raiders, losing a limb (user, 2026-10-01: prosthetics and conversions left out). Shown as a
    /// line between the photos in that colonist's own chronicle tab only. The names and the detail are kept as text as they were then, so a
    /// line still reads after the other person is gone.</summary>
    public class LifeEvent : IExposable
    {
        public string kind;
        public List<string> pawnIds = new List<string>();
        public List<string> names = new List<string>();   // index-aligned with pawnIds
        public string other;     // an animal or a faction (null when the other one is a person in pawnIds)
        public string detail;    // the cause of a lost limb
        public int ticksAbs;
        public string dateLabel;
        public int year;
        public string noteKey;   // chosen once; the other person's name is resolved for the viewer

        public void ExposeData()
        {
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Collections.Look(ref pawnIds, "pawnIds", LookMode.Value);
            Scribe_Collections.Look(ref names, "names", LookMode.Value);
            Scribe_Values.Look(ref other, "other");
            Scribe_Values.Look(ref detail, "detail");
            Scribe_Values.Look(ref ticksAbs, "ticksAbs");
            Scribe_Values.Look(ref dateLabel, "dateLabel");
            Scribe_Values.Look(ref year, "year");
            Scribe_Values.Look(ref noteKey, "noteKey");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (pawnIds == null) pawnIds = new List<string>();
                if (names == null) names = new List<string>();
            }
        }

        /// <summary>The line as the given colonist's chronicle shows it: about the other one in the event.</summary>
        private string OtherFor(string pawnId)
        {
            string who = other;
            if (who.NullOrEmpty())
                for (int i = 0; i < pawnIds.Count && i < names.Count; i++)
                    if (pawnIds[i] != pawnId) { who = names[i]; break; }
            return who;
        }

        public string LineFor(string pawnId)
        {
            string key = "MF_Life_" + kind + (kind == LifeEvents.Limb && detail.NullOrEmpty() ? "Plain" : "");
            return CaptionText.Clean(key.Translate(OtherFor(pawnId) ?? "?", detail ?? "").ToString());
        }

        private string[] NoteValues(string pawnId)
        {
            int index = pawnIds.IndexOf(pawnId);
            string self = index >= 0 && index < names.Count ? names[index] : "";
            return new[] { OtherFor(pawnId) ?? "", detail ?? "", self ?? "", dateLabel ?? "" };
        }

        public string NoteFor(string pawnId) => noteKey.NullOrEmpty() ? "" :
            DescriptionCandidates.Format(DescriptionCandidates.Lookup(noteKey), NoteValues(pawnId));

        internal void ChooseNote()
        {
            string category = kind + (kind == LifeEvents.Limb && detail.NullOrEmpty() ? "Plain" : "");
            noteKey = DescriptionCandidates.PickKey("MF_LifeNote_" + category, -1,
                NoteValues(pawnIds.FirstOrDefault()));
        }
    }

    public static class LifeEvents
    {
        public const string Joined = "joined", Born = "born", Lover = "lover", Fiance = "fiance", Spouse = "spouse", BrokeUp = "brokeup",
            Divorced = "divorced", Bond = "bond", Captured = "captured", Limb = "limb";

        private static readonly HashSet<string> Limbs = new HashSet<string> { "Arm", "Leg", "Hand", "Foot" };

        private static bool On => Hooks.Playing && MomentFramesMod.Settings != null && MomentFramesMod.Settings.lifeEvents;

        /// <summary>Ours and really here: a pawn being generated (a newcomer made as someone's ex) is neither on a map nor in a caravan yet.</summary>
        private static bool Present(Pawn p) => p != null && !p.Dead && p.Faction == Faction.OfPlayer && (p.SpawnedOrAnyParentSpawned || p.IsCaravanMember());

        internal static void Record(string kind, IEnumerable<Pawn> pawns, string other, string detail, Map map)
        {
            var store = PhotoStore.Instance;
            if (store == null) return;
            var actors = pawns?.Where(p => p != null).ToList() ?? new List<Pawn>();
            // A caravan has no MapHeld. Its own tile must win over an unrelated home map's calendar.
            PlanetTile tile = map?.Tile ?? PlanetTile.Invalid;
            if (!tile.Valid) tile = actors.Select(p => p.Tile).Where(t => t.Valid).DefaultIfEmpty(PlanetTile.Invalid).First();
            if (!tile.Valid) tile = (Find.AnyPlayerHomeMap ?? Find.CurrentMap)?.Tile ?? PlanetTile.Invalid;
            int now = Find.TickManager.TicksAbs;
            var e = new LifeEvent { kind = kind, other = other, detail = detail, ticksAbs = now,
                dateLabel = PhotoStore.DateLabel(tile, now), year = PhotoStore.Year(tile, now) };
            foreach (var p in actors)
            {
                e.pawnIds.Add(p.GetUniqueLoadID());
                e.names.Add(p.LabelShort);
            }
            if (e.pawnIds.Count > 0) { e.ChooseNote(); store.AddLife(e); }
        }

        /// <summary>A free colonist not seen before has joined (a baby: was born). The first poll of a game, or of a save from before this
        /// existed, only learns who is already there. Every way in is covered this way (recruits, wanderers, quest joiners, births), and a
        /// slave or a lodger is not a free colonist. Runs on the store's frequent poll.</summary>
        internal static void Poll(PhotoStore store)
        {
            if (store == null || !On) return;
            var now = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoLodgers;
            if (store.KnownColonists == null)
            {
                store.KnownColonists = new HashSet<string>(now.Where(p => p != null).Select(p => p.GetUniqueLoadID()));
                return;
            }
            foreach (var p in now)
            {
                if (p?.RaceProps == null || !p.RaceProps.Humanlike || !store.KnownColonists.Add(p.GetUniqueLoadID())) continue;
                if (!store.HasMark("life-origin:" + p.GetUniqueLoadID())) Record(Joined, new[] { p }, null, null, p.MapHeld);
            }
        }

        internal static void OnRelation(Pawn a, Pawn b, PawnRelationDef def)
        {
            if (!On || a?.RaceProps == null || b?.RaceProps == null) return;
            string kind = def == PawnRelationDefOf.Lover ? Lover : def == PawnRelationDefOf.Fiance ? Fiance : def == PawnRelationDefOf.Spouse ? Spouse
                : def == PawnRelationDefOf.ExLover ? BrokeUp : def == PawnRelationDefOf.ExSpouse ? Divorced : def == PawnRelationDefOf.Bond ? Bond : null;
            if (kind == null) return;
            if (kind == Bond)
            {
                Pawn human = a.RaceProps.Humanlike ? a : b.RaceProps.Humanlike ? b : null;
                Pawn animal = human == a ? b : a;
                if (human == null || animal.RaceProps.Humanlike || !Present(human)) return;
                Record(Bond, new[] { human }, animal.LabelShort, null, human.MapHeld);
                return;
            }
            if (!a.RaceProps.Humanlike || !b.RaceProps.Humanlike || !Present(a) || !Present(b)) return;
            Record(kind, new[] { a, b }, null, null, a.MapHeld ?? b.MapHeld);
        }

        internal static void OnLostPart(Pawn pawn, Hediff_MissingPart lost, BodyPartRecord part)
        {
            if (!On || pawn?.RaceProps == null || !pawn.RaceProps.Humanlike || part?.def == null || !Limbs.Contains(part.def.defName)) return;
            if (pawn.Faction != Faction.OfPlayer || lost.lastInjury == HediffDefOf.SurgicalCut) return;   // an amputation is treatment, not an injury
            if (pawn.health?.hediffSet?.hediffs == null || !pawn.health.hediffSet.hediffs.Contains(lost)) return;
            Record(Limb, new[] { pawn }, part.Label, lost.lastInjury?.label, pawn.MapHeld);
        }
    }

    [HarmonyPatch(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.AddDirectRelation))]
    internal static class Patch_LifeRelation
    {
        private static void Prefix(Pawn_RelationsTracker __instance, PawnRelationDef def, Pawn otherPawn, out bool __state)
        {
            __state = true;
            try { __state = def == null || otherPawn == null || __instance.DirectRelationExists(def, otherPawn); }
            catch (Exception e) { Hooks.Fail("relation", e); }
        }

        private static void Postfix(Pawn_RelationsTracker __instance, PawnRelationDef def, Pawn otherPawn, bool __state, Pawn ___pawn)
        {
            try
            {
                if (__state || !__instance.DirectRelationExists(def, otherPawn)) return;   // existed already, or the game refused it
                LifeEvents.OnRelation(___pawn, otherPawn, def);
            }
            catch (Exception e) { Hooks.Fail("relation", e); }
        }
    }

    /// <summary>A colonist carried off by raiders (the kidnapping tracker of the faction that took them).</summary>
    [HarmonyPatch(typeof(KidnappedPawnsTracker), nameof(KidnappedPawnsTracker.Kidnap))]
    internal static class Patch_LifeCaptured
    {
        private static void Prefix(Pawn pawn, Faction ___faction)
        {
            try
            {
                if (!Hooks.Playing || MomentFramesMod.Settings == null || !MomentFramesMod.Settings.lifeEvents) return;
                if (pawn?.RaceProps == null || !pawn.RaceProps.Humanlike || pawn.Faction != Faction.OfPlayer || ___faction == null || ___faction == Faction.OfPlayer) return;
                LifeEvents.Record(LifeEvents.Captured, new[] { pawn }, ___faction.Name, null, pawn.MapHeld);
            }
            catch (Exception e) { Hooks.Fail("kidnap", e); }
        }
    }

    /// <summary>A limb lost. Every hediff goes through here, so the first thing done is one type check.</summary>
    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff), new[] { typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) })]
    internal static class Patch_LifeLimb
    {
        private static void Postfix(Hediff hediff, BodyPartRecord part, Pawn ___pawn)
        {
            if (!(hediff is Hediff_MissingPart lost)) return;
            try { LifeEvents.OnLostPart(___pawn, lost, part ?? hediff.Part); }
            catch (Exception e) { Hooks.Fail("lost limb", e); }
        }
    }
}
