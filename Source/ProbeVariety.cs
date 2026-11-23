using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool varietyMode;
        private static string varietyPawnId;
        private static string[] varietyKeys, varietyNext;
        private static Dictionary<string, string> varietyPhotos;
        private static string[] varietyLives;
        private const string VarietyPrefix = "native-variety-history";

        private static void VarietyCheck(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException("caption variety: " + name);
            checks.Add("PASS: caption variety — " + name);
        }

        private static void VarietyStep()
        {
            switch (phase)
            {
                case 0:
                    FxSet(false); Setup(); Wait(1f); phase = 1; return;
                case 1:
                    testRec = PhotoStore.Instance.Request(map, center, "group", "probe-variety-photo", "caption variety fixture", "", new[] { p1, p2 });
                    VarietyCheck(testRec != null && !testRec.desc.NullOrEmpty(), "actual new photo registration chooses and stores a complete caption");
                    phase = 2; return;
                case 2:
                    if (!testRec.HasFile)
                    {
                        if (!PhotoStore.Instance.Records.Contains(testRec)) throw new InvalidOperationException("variety capture was dropped");
                        return;
                    }
                    PrepareVariety();
                    MainTabWindow_Album.Open(testRec); Wait(.7f); phase = 3; return;
                case 3:
                    CloseLog(); Shot("variety-gallery.png"); Wait(.5f); phase = 4; return;
                case 4:
                    Require("variety-gallery.png");
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    Find.Selector.Select(p1, false); InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Chronicle));
                    Wait(.7f); phase = 5; return;
                case 5:
                    CloseLog(); Shot("variety-chronicle.png"); Wait(.5f); phase = 6; return;
                case 6:
                    Require("variety-chronicle.png");
                    Find.MainTabsRoot.EscapeCurrentTab(false);
                    SaveVariety(); Wait(.8f); phase = 7; return;
                case 7:
                    GameDataSaveLoader.LoadGame("mfvariety"); Wait(1.5f); phase = 8; return;
                case 8:
                    VerifyVarietyRecords();
                    var next = Enumerable.Range(0, 8).Select(i => PhotoStore.Instance.Descriptions.Pick(VarietyPrefix, varietyKeys, n => 0)).ToArray();
                    VarietyCheck(next.SequenceEqual(varietyNext), "real game save/load continues exactly from the saved unused/recent candidates");
                    GameDataSaveLoader.LoadGame("mfvariety-legacy"); Wait(1.5f); phase = 9; return;
                case 9:
                    VerifyVarietyRecords();
                    VarietyCheck(PhotoStore.Instance.Descriptions.Pick(VarietyPrefix, varietyKeys, n => 0) == varietyKeys[0],
                        "legacy game XML without history loads an empty usable per-game bag");
                    File.WriteAllText(Path.Combine(dir, "variety-result.json"), "{\"ok\":true,\"checks\":[" + string.Join(",", checks.Select(Json))
                        + "],\"limits\":\"Isolated Core/Harmony fixtures. Full player save/mod compatibility and natural play are untested.\"}");
                    active = false; Application.Quit(); return;
            }
        }

        private static void PrepareVariety()
        {
            var store = PhotoStore.Instance;
            string[] full = { p1.LabelShort + ", " + p2.LabelShort, p1.LabelShort, "season", "place", "detail" };
            var pool = DescriptionCandidates.EligibleKeys("MF_Caption_group", 2, full, DescriptionCandidates.Lookup);
            VarietyCheck(pool.Count >= 12, "native translation supplies at least twelve distinct eligible group captions");
            var exercised = new HashSet<string>();
            int failures = 0;
            for (int seed = 0; seed < 128; seed++)
            {
                var history = new DescriptionHistory();
                Rand.PushState(seed + 4000);
                try
                {
                    var recent = new Queue<string>();
                    for (int cycle = 0; cycle < 4; cycle++)
                    {
                        var seen = new HashSet<string>();
                        for (int i = 0; i < pool.Count; i++)
                        {
                            string key = history.Pick("group", pool, n => Rand.Range(0, n));
                            if (!seen.Add(key) || recent.Contains(key)) failures++;
                            exercised.Add(key); recent.Enqueue(key); if (recent.Count > 5) recent.Dequeue();
                        }
                    }
                }
                finally { Rand.PopState(); }
            }
            VarietyCheck(failures == 0 && exercised.SetEquals(pool), "128 native Rand seeds, four full bags each: every candidate reached, no within-cycle or recent-five repeats");

            var names = new[] { p1, p2 };
            LifeEvents.Record(LifeEvents.Joined, new[] { p1 }, null, null, map);
            LifeEvents.Record(LifeEvents.Lover, names, null, null, map);
            var lover = store.LifeEvents.Last(e => e.kind == LifeEvents.Lover);
            File.WriteAllText(Path.Combine(dir, "variety-lover-context.json"), "{\"key\":" + Json(lover.noteKey)
                + ",\"first\":" + Json(lover.NoteFor(lover.pawnIds[0])) + ",\"second\":" + Json(lover.NoteFor(lover.pawnIds[1])) + "}");
            VarietyCheck(lover.LineFor(lover.pawnIds[0]).Contains(lover.names[1]) && lover.LineFor(lover.pawnIds[1]).Contains(lover.names[0])
                && !lover.NoteFor(lover.pawnIds[0]).NullOrEmpty() && !lover.NoteFor(lover.pawnIds[1]).NullOrEmpty(),
                "saved relation event lines name the other person from both viewpoints, including generic reflection candidates");
            var namedRelation = new LifeEvent { kind = lover.kind, pawnIds = lover.pawnIds.ToList(), names = lover.names.ToList(),
                dateLabel = lover.dateLabel, noteKey = "MF_LifeNote_lover_1" };
            VarietyCheck(namedRelation.NoteFor(namedRelation.pawnIds[0]).Contains(namedRelation.names[1])
                && namedRelation.NoteFor(namedRelation.pawnIds[1]).Contains(namedRelation.names[0]),
                "named reflection fixture resolves the correct counterpart for each real recorded viewer");
            var genericRelation = new LifeEvent { kind = lover.kind, pawnIds = lover.pawnIds.ToList(), names = lover.names.ToList(),
                dateLabel = lover.dateLabel, noteKey = "MF_LifeNote_lover_18" };
            VarietyCheck(!genericRelation.NoteFor(genericRelation.pawnIds[0]).NullOrEmpty()
                && !genericRelation.NoteFor(genericRelation.pawnIds[0]).Contains(genericRelation.names[1])
                && genericRelation.LineFor(genericRelation.pawnIds[0]).Contains(genericRelation.names[1]),
                "generic reflection positive control disproves the old all-candidates-contain-a-name assumption without losing event identity");
            var named = new LifeEvent { kind = LifeEvents.Joined, pawnIds = new List<string> { p1.GetUniqueLoadID() }, names = new List<string> { p1.LabelShort },
                ticksAbs = Find.TickManager.TicksAbs, year = PhotoStore.Year(map, Find.TickManager.TicksAbs),
                dateLabel = PhotoStore.DateLabel(map, Find.TickManager.TicksAbs), noteKey = "MF_LifeNote_joined_24" };
            VarietyCheck(!named.dateLabel.NullOrEmpty() && named.NoteFor(p1.GetUniqueLoadID()).Contains(p1.LabelShort)
                && named.NoteFor(p1.GetUniqueLoadID()).Contains(named.dateLabel), "new named/date-capable life templates format with the record's own facts");
            store.AddLife(named);
            store.AddLife(new LifeEvent { kind = LifeEvents.Joined, pawnIds = new List<string> { p1.GetUniqueLoadID() }, names = new List<string> { p1.LabelShort },
                ticksAbs = named.ticksAbs, year = named.year, dateLabel = named.dateLabel, noteKey = "MF_LifeNote_joined_2" });
            store.AddLife(new LifeEvent { kind = LifeEvents.Joined, pawnIds = new List<string> { p1.GetUniqueLoadID() }, names = new List<string> { p1.LabelShort },
                ticksAbs = named.ticksAbs, year = named.year, dateLabel = named.dateLabel });
            var create = AccessTools.Method(typeof(PhotoStore), "Create");
            create.Invoke(store, new object[] { map, center.ToVector3Shifted(), "group", "probe-variety-explicit", "explicit fixture", "player supplied description", new[] { p1 }, null, PhotoCapture.DefaultAspect, true });
            var edit = store.SaveEdit(testRec, new Rect(0f, 0f, 1f, 1f), PhotoFilters.None);
            VarietyCheck(edit != null, "an actual photo edit supplies a nonempty preservation fixture");
            edit.note = "player edit note";
            store.SetShown(testRec, null);
            VarietyCheck(store.Records.Any(r => r.desc == "player supplied description"), "explicit photo descriptions stay verbatim");
            varietyPawnId = p1.GetUniqueLoadID();
            varietyPhotos = store.Records.ToDictionary(r => r.id, PhotoDescriptionFact);
            varietyLives = store.LifeEvents.Select(LifeDescriptionFact).ToArray();
            varietyKeys = pool.ToArray();
            for (int i = 0; i < 3; i++) store.Descriptions.Pick(VarietyPrefix, varietyKeys, n => 0);
            var samples = new List<string>();
            for (int i = 0; i < 12; i++) samples.Add(PhotoDesc.Pick("group", names, map));
            File.WriteAllText(Path.Combine(dir, "variety-examples.json"), "{\"photo\":" + Json(testRec.desc) + ",\"group\":[" + string.Join(",", samples.Select(Json))
                + "],\"join\":" + Json(named.NoteFor(varietyPawnId)) + ",\"loverFirst\":" + Json(lover.NoteFor(lover.pawnIds[0]))
                + ",\"loverSecond\":" + Json(lover.NoteFor(lover.pawnIds[1])) + "}");
        }

        private static void SaveVariety()
        {
            GameDataSaveLoader.SaveGame("mfvariety");
            string path = GenFilePaths.FilePathForSavedGame("mfvariety");
            VarietyCheck(File.Exists(path), "actual game save was written inside the disposable profile");
            var xml = new XmlDocument(); xml.Load(path);
            var nodes = xml.SelectNodes("//descriptionHistory");
            VarietyCheck(nodes.Count == 1 && nodes[0].SelectNodes("bags/li").Count > 0, "the game save contains nonempty per-colony sentence history");
            nodes[0].ParentNode.RemoveChild(nodes[0]);
            xml.Save(GenFilePaths.FilePathForSavedGame("mfvariety-legacy"));
            varietyNext = Enumerable.Range(0, 8).Select(i => PhotoStore.Instance.Descriptions.Pick(VarietyPrefix, varietyKeys, n => 0)).ToArray();
            var fresh = new DescriptionHistory();
            VarietyCheck(!varietyNext.SequenceEqual(Enumerable.Range(0, 8).Select(i => fresh.Pick(VarietyPrefix, varietyKeys, n => 0))),
                "positive control: reset history would change the asserted next sequence");
        }

        private static void VerifyVarietyRecords()
        {
            map = Find.CurrentMap;
            Find.TickManager.Pause();
            Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear();
            var store = PhotoStore.Instance;
            var changed = varietyPhotos.Where(kv => !store.Records.Any(r => r.id == kv.Key && PhotoDescriptionFact(r) == kv.Value)).ToList();
            File.WriteAllText(Path.Combine(dir, "variety-preservation-diff.json"), "[" + string.Join(",", changed.Select(kv =>
                "{\"id\":" + Json(kv.Key) + ",\"expected\":" + Json(kv.Value) + ",\"actual\":" + Json(store.Get(kv.Key) != null ? PhotoDescriptionFact(store.Get(kv.Key)) : "MISSING") + "}")) + "]");
            VarietyCheck(varietyPhotos.Count > 0 && varietyPhotos.All(kv => store.Records.Any(r => r.id == kv.Key && PhotoDescriptionFact(r) == kv.Value)),
                "loaded original/generated/player/edit photo descriptions are unchanged");
            VarietyCheck(varietyLives.Length > 0 && varietyLives.SequenceEqual(store.LifeEvents.Select(LifeDescriptionFact)),
                "loaded chosen life keys, old candidate text, both viewpoints and old keyless notes are unchanged");
        }
    }
}
