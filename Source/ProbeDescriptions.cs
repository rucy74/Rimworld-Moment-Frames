using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    // Runs only inside Probe's marked disposable game/profile, never against a player save.
    public static partial class Probe
    {
        private static List<string> savedLifeDescriptions;
        private static Dictionary<string, string> savedPhotoDescriptions;
        private static LifeEvent legacyDescriptionFixture;

        private static void DescriptionCandidatesCheck()
        {
            void C(bool ok, string message) => checks.Add((ok ? "PASS" : "FAIL") + ": caption candidates: " + message);
            string[] full = { "one, two", "one", "season", "place", "detail" };
            var generic = DescriptionCandidates.EligibleKeys("MF_Caption_role", 1, new[] { "one", "one", "season", "place", "" }, DescriptionCandidates.Lookup);
            var detailed = DescriptionCandidates.EligibleKeys("MF_Caption_role", 1, full, DescriptionCandidates.Lookup);
            C(generic.Count >= 2 && generic.All(k => !k.Contains("_detail_")) && detailed.Count >= 3 && detailed.All(k => k.Contains("_detail_"))
                && generic.Concat(detailed).All(k => DescriptionCandidates.Lookup(k).Contains("{1}")), "game language lookup preserves placeholders; known role actor stays in the sentence, and detail is used only when recorded");
            C(DescriptionCandidates.PickKey("MF_Caption_unknown_probe_kind", 0, full) == null, "unknown kind has no invented caption");
            var noPeople = PhotoDesc.Pick("group", new List<Pawn>(), null);
            var solo = PhotoDesc.Pick("group", new[] { p1 }, map);
            var together = PhotoDesc.Pick("group", new[] { p1, p2 }, map);
            C(!string.IsNullOrWhiteSpace(noPeople) && !string.IsNullOrWhiteSpace(solo) && !string.IsNullOrWhiteSpace(together)
                && !(noPeople + solo + together).Contains("{0}") && !(noPeople + solo + together).Contains("{1}"), "scene, solo and group render through PhotoDesc with no unresolved slots");
            var previousKey = "";
            bool noRepeat = true;
            for (int i = 0; i < 30; i++)
            {
                string key = DescriptionCandidates.PickKey("MF_Caption_role", 1, full);
                noRepeat &= key != previousKey && detailed.Contains(key);
                previousKey = key;
            }
            C(noRepeat, "30 game-Rand choices stay eligible and avoid the immediately previous sentence");

            var store = PhotoStore.Instance;
            var relation = store.LifeEvents.FirstOrDefault(e => e.kind == LifeEvents.Lover && e.pawnIds.Count == 2);
            if (relation == null)
            {
                MomentFramesMod.Settings.lifeEvents = true;
                var pair = map.mapPawns.FreeColonistsSpawned.Take(2).ToList();
                LifeEvents.OnRelation(pair[0], pair[1], RimWorld.PawnRelationDefOf.Lover);
                relation = store.LifeEvents.FirstOrDefault(e => e.kind == LifeEvents.Lover && e.pawnIds.Count == 2);
            }
            if (relation == null) throw new InvalidOperationException("caption fixture needs the native lover relation event");
            string first = relation.NoteFor(relation.pawnIds[0]), second = relation.NoteFor(relation.pawnIds[1]);
            C(!relation.noteKey.NullOrEmpty() && first.Contains(relation.names[1]) && second.Contains(relation.names[0]), "the same saved lover candidate names the other person from each viewer's chronicle");
            C(store.LifeEvents.All(e => !e.noteKey.NullOrEmpty() && !e.NoteFor(e.pawnIds[0]).NullOrEmpty()), "native relation, join, injury, bond and captured hooks choose readable life notes when recorded");

            // Mimic an old serialized entry with no noteKey; display and a new save must not backfill it.
            legacyDescriptionFixture = new LifeEvent { kind = LifeEvents.Joined, pawnIds = new List<string> { p1.GetUniqueLoadID() },
                names = new List<string> { p1.LabelShort }, ticksAbs = Find.TickManager.TicksAbs, year = PhotoStore.Year(map, Find.TickManager.TicksAbs), dateLabel = PhotoStore.DateLabel(map, Find.TickManager.TicksAbs) };
            store.AddLife(legacyDescriptionFixture);
            C(legacyDescriptionFixture.NoteFor(p1.GetUniqueLoadID()) == "" && !legacyDescriptionFixture.LineFor(p1.GetUniqueLoadID()).NullOrEmpty(), "an old keyless entry retains its original title without a new note");

            // Exercise the actual registration path without scheduling an image capture.
            var create = AccessTools.Method(typeof(PhotoStore), "Create");
            PhotoRecord Make(string eventKey, string caption) => (PhotoRecord)create.Invoke(store, new object[] { map, center.ToVector3Shifted(), "group", eventKey, "caption fixture", caption, new[] { p1 }, null, PhotoCapture.DefaultAspect, true });
            var generated = Make("probe-caption-generated", "");
            var explicitRec = Make("probe-caption-explicit", "player supplied description");
            try
            {
                C(generated != null && !generated.desc.NullOrEmpty() && explicitRec?.desc == "player supplied description", "new empty photo descriptions use candidates; explicit descriptions stay verbatim");
            }
            finally { if (generated != null) store.Remove(generated); if (explicitRec != null) store.Remove(explicitRec); }
            File.WriteAllText(Path.Combine(dir, "caption-examples.json"), "{\"scene\":" + Json(noPeople) + ",\"solo\":" + Json(solo) + ",\"group\":" + Json(together)
                + ",\"loverFirst\":" + Json(first) + ",\"loverSecond\":" + Json(second) + ",\"noteKey\":" + Json(relation.noteKey) + "}");
        }

        private static string LifeDescriptionFact(LifeEvent e) => e.kind + "|" + e.ticksAbs + "|" + e.noteKey + "|"
            + string.Join(";", e.pawnIds.Select(id => id + ":" + e.NoteFor(id)));
        // RimWorld's XML writer uses the OS newline convention. Compare text exactly apart
        // from CRLF/LF, without trimming whitespace or hiding a changed sentence/edit.
        private static string PhotoDescriptionFact(PhotoRecord r) => (r.desc + "|" + r.DisplayDesc + "|"
            + string.Join(";", r.edits.Select(e => e.id + ":" + e.note))).Replace("\r\n", "\n");

        private static void SaveDescriptionFacts()
        {
            savedLifeDescriptions = PhotoStore.Instance.LifeEvents.Select(LifeDescriptionFact).ToList();
            savedPhotoDescriptions = PhotoStore.Instance.Records.ToDictionary(r => r.id, PhotoDescriptionFact);
        }

        private static void CheckDescriptionFacts()
        {
            var store = PhotoStore.Instance;
            bool lifeSame = savedLifeDescriptions.SequenceEqual(store.LifeEvents.Select(LifeDescriptionFact));
            bool photoSame = store.Records.All(r => savedPhotoDescriptions.TryGetValue(r.id, out string original) && original == PhotoDescriptionFact(r));
            var legacy = store.LifeEvents.FirstOrDefault(e => e.kind == legacyDescriptionFixture.kind && e.ticksAbs == legacyDescriptionFixture.ticksAbs && e.noteKey == null && e.pawnIds.SequenceEqual(legacyDescriptionFixture.pawnIds));
            checks.Add((lifeSame && photoSame && legacy != null && legacy.NoteFor(legacy.pawnIds[0]) == "" ? "PASS" : "FAIL")
                + ": caption candidates: real Scribe save/load retains life-note keys, both viewpoints, original/edit captions and the old keyless entry (life=" + lifeSame + ", photo=" + photoSame + ", legacy=" + (legacy != null) + ")");
        }
    }
}
