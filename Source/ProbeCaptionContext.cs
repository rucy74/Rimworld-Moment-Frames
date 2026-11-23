using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    // Reached only through Probe's marked disposable-profile guard. Never loads a player save.
    public static partial class Probe
    {
        private static bool captionContextMode;
        private static Pawn captionHuman, captionAnimal, captionBaby, captionDeadAnimal;
        private static string captionHumanId, captionAnimalId, captionBabyId, captionPetPhotoId, captionBabyPhotoId;
        private static Building_PhotoSpot captionSpot;
        private static LordJob_MFGroupPhoto captionPetJob;
        private static PhotoRecord captionCurrent;
        private static int captionCase, captionTicks;
        private static IntVec3 captionHumanStart, captionAnimalStart;
        private static bool captionHumanWalked, captionAnimalWalked, captionCallback, captionPriorPause;
        private static string captionInitial, captionExpected, captionPrefix, captionLabel;
        private static readonly Dictionary<string, string> captionStoredFacts = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> captionStoredHashes = new Dictionary<string, string>();
        private static readonly List<string> captionGeneratedIds = new List<string>();
        private static readonly List<string> captionExamples = new List<string>();
        private static readonly Dictionary<string, string[]> captionContinuation = new Dictionary<string, string[]>();

        private static void CaptionCheck(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("captured caption context: " + label);
            checks.Add("PASS: captured caption context — " + label);
        }
        private static string CaptionHash(string path)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
        }
        private static string CaptionLast(string text) => (text ?? "").Replace("\r\n", "\n").Split('\n').Last();
        private static string CaptionFact(PhotoRecord rec) => (PhotoDescriptionFact(rec) + "|" + rec.title + "|" + rec.kind + "|"
            + rec.file + "|" + string.Join(",", rec.peopleIds)).Replace("\r\n", "\n");
        private static List<Pawn> CaptionActors(string prefix) => prefix == PhotoCaptionContext.CarriedBabyPrefix
            ? new List<Pawn> { captionHuman, captionBaby } : new List<Pawn> { captionHuman, captionAnimal };
        private static string[] CaptionValues(string prefix)
        {
            var actors = CaptionActors(prefix);
            return PhotoCaptionContext.Measure(CaptionFixture(prefix, "context-values:" + prefix), actors, map).Values.ToArray();
        }
        private static HashSet<string> CaptionBank(string prefix) => new HashSet<string>(Enumerable.Range(1, 12).Select(n =>
            CaptionText.Clean(DescriptionCandidates.Format(DescriptionCandidates.Lookup(prefix + "_" + n), CaptionValues(prefix)))));
        private static PhotoRecord CaptionFixture(string prefix, string id)
        {
            var rec = new PhotoRecord { id = id, kind = Kinds.Manual, title = "Native caption fixture", mapId = map.uniqueID,
                center = center.ToVector3Shifted(), spanCells = 9f, aspect = 1.5f, generatedDesc = true };
            rec.SetPeople(CaptionActors(prefix)); return rec;
        }
        private static IEnumerable<string> CaptionPrefixes()
        {
            yield return PhotoCaptionContext.AnimalPairPrefix;
            if (captionBaby != null) yield return PhotoCaptionContext.CarriedBabyPrefix;
        }
        private static void CaptionPlace(Pawn pawn, IntVec3 offset)
        {
            if (pawn == null || !pawn.Spawned) return;
            FixReady(pawn); pawn.Position = center + offset; pawn.Notify_Teleported(true, true);
            // A paused native fixture still needs a stable job for the single initialization tick after loading.
            var wait = JobMaker.MakeJob(JobDefOf.Wait); wait.expiryInterval = 100000;
            pawn.jobs.StartJob(wait, JobCondition.InterruptForced); pawn.Drawer.tweener.ResetTweenedPosToRoot();
        }
        private static void CaptionDropBaby()
        {
            if (captionBaby == null) return;
            var holder = BirthdayCarry.Holder(captionBaby);
            if (holder == null) return;
            CaptionCheck(holder.carryTracker.TryDropCarriedThing(center + new IntVec3(19, 0, 0), ThingPlaceMode.Direct, out _)
                && captionBaby.Spawned, "fixture releases the actually held baby through native carryTracker");
        }
        private static void CaptionHoldBaby()
        {
            if (captionBaby == null) return;
            if (BirthdayCarry.Holder(captionBaby) == captionHuman) return;
            CaptionDropBaby(); CaptionPlace(captionBaby, new IntVec3(0, 0, 1));
            CaptionCheck(captionHuman.carryTracker.TryStartCarry(captionBaby, 1) > 0 && !captionBaby.Spawned
                && captionHuman.carryTracker.CarriedThing == captionBaby && BirthdayCarry.Holder(captionBaby) == captionHuman,
                "native pickup fixture really transfers baby into the selected person's carryTracker");
        }
        private static void CaptionSetActors(bool held = false)
        {
            CaptionDropBaby(); CaptionPlace(captionHuman, new IntVec3(-1, 0, 0));
            CaptionPlace(captionAnimal, new IntVec3(1, 0, 0)); CaptionPlace(captionBaby, new IntVec3(19, 0, 0));
            if (captionDeadAnimal?.Corpse?.Spawned == true) captionDeadAnimal.Corpse.Position = center + new IntVec3(20, 0, 2);
            if (held) CaptionHoldBaby(); SettleDraw();
        }
        private static void CaptionSetup()
        {
            FxSet(false); FixSetup(); Find.MainTabsRoot.EscapeCurrentTab(false);
            var settings = MomentFramesMod.Settings;
            settings.msColonyStart = false; settings.capturePetArrivals = false; settings.birthdayMode = Birthday.ModeOff;
            settings.battleMode = Battles.ModeOff; settings.msDeath = false; settings.captureReactionMode = PhotoReactions.ModeOff;
            settings.albumTheme = "graphite"; settings.albumFont = "pencil"; settings.albumTextSize = 130;
            map.Biome.constantOutdoorTemperature = 22f;
            captionHuman = map.mapPawns.FreeColonistsSpawned.First(p => p.DevelopmentalStage.Adult());
            captionHuman.Name = new NameSingle("Caption caregiver"); captionHumanId = captionHuman.GetUniqueLoadID();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.Where(p => p != captionHuman).ToList())
                CaptionPlace(pawn, new IntVec3(20, 0, 8));
            captionAnimal = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDef.Named("Husky"), Faction.OfPlayer,
                forceGenerateNewPawn: true, fixedBiologicalAge: 3f, fixedChronologicalAge: 3f));
            captionAnimal.Name = new NameSingle("Caption animal"); GenSpawn.Spawn(captionAnimal, center + new IntVec3(1, 0, 0), map);
            captionAnimalId = captionAnimal.GetUniqueLoadID();
            if (ModsConfig.BiotechActive)
            {
                captionBaby = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    forceGenerateNewPawn: true, fixedBiologicalAge: 1f, fixedChronologicalAge: 1f,
                    allowDowned: true, developmentalStages: DevelopmentalStage.Baby));
                captionBaby.Name = new NameSingle("Caption child"); GenSpawn.Spawn(captionBaby, center + new IntVec3(19, 0, 0), map);
                captionBabyId = captionBaby.GetUniqueLoadID();
                // This person is deliberately not the baby's parent. A visible holder is the only supported fact.
                foreach (var relation in captionBaby.relations.DirectRelations.Where(r => r.otherPawn == captionHuman).ToList())
                    captionBaby.relations.RemoveDirectRelation(relation.def, captionHuman);
                CaptionCheck(!captionBaby.relations.FamilyByBlood.Contains(captionHuman),
                    "carrying fixture uses a real non-parent caregiver and cannot justify an invented parent");
            }
            else checks.Add("INFO: carried-baby native checks skipped because Biotech is inactive; run -Dlc for both banks.");
            CaptionSetActors();
            captionSpot = (Building_PhotoSpot)ThingMaker.MakeThing(ThingDef.Named("MF_PhotoSpot"));
            captionSpot.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(captionSpot, center, map);
            captionStoredFacts.Clear(); captionStoredHashes.Clear(); captionGeneratedIds.Clear(); captionExamples.Clear();
            captionContinuation.Clear(); captionCase = captionTicks = 0;
            checks.Add("INFO: native caption language " + LanguageDatabase.activeLanguage.folderName
                + "; instant carryTracker pickup is a caption fixture, not a baby walk/return test.");
        }
        private static bool CaptionAdvancePet()
        {
            for (int i = 0; i < 30 && !captionPetJob.Shooting && !captionPetJob.Finished; i++)
            {
                Find.TickManager.DoSingleTick(); captionTicks++;
                captionHumanWalked |= captionHuman.Position != captionHumanStart;
                captionAnimalWalked |= captionAnimal.Position != captionAnimalStart;
            }
            SettleDraw();
            if (captionTicks > 6000) throw new TimeoutException("native caption pet gathering did not expose; result="
                + captionPetJob.Result + " ready=" + captionPetJob.InPlace);
            return captionPetJob.Shooting || captionPetJob.Finished;
        }
        private static void CaptionPicture(PhotoRecord rec, string label)
        {
            var store = PhotoStore.Instance;
            CaptionCheck(rec?.HasFile == true && File.Exists(store.OriginalPath(rec)) && File.Exists(store.OriginalThumbPath(rec)),
                label + " stores the actual PNG and thumbnail");
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                CaptionCheck(texture.LoadImage(File.ReadAllBytes(store.OriginalPath(rec)), false) && texture.width > 100
                    && texture.height > 100 && texture.GetPixels32().Any(p => p.r > 60 || p.g > 60 || p.b > 60),
                    label + " native PNG decodes and contains rendered scene pixels");
            }
            finally { UnityEngine.Object.Destroy(texture); }
            File.Copy(store.OriginalPath(rec), Path.Combine(dir, label + ".png"), true);
            captionExamples.Add(label + " [" + string.Join(", ", rec.people) + "]\n" + rec.desc);
            checks.Add(CaptureDiagLine(label + " actual native render", PhotoCapture.VisiblePawns(map).Where(rec.Has)));
        }
        private static void CaptionSpecial(PhotoRecord rec, string prefix, string label)
        {
            var actors = CaptionActors(prefix); var bank = CaptionBank(prefix);
            CaptionCheck(rec.generatedDesc && actors.All(p => rec.Has(p))
                && bank.Contains(rec.desc) && rec.captionScene == prefix
                && !rec.captionTemplate.NullOrEmpty() && rec.captionValues.Count > 7
                && actors.Any(p => CaptionLast(rec.desc).Contains(p.LabelShort)),
                label + " stored scene caption and provenance name a recorded actor without a duplicate metadata line [generated " + rec.generatedDesc + " has " + string.Join("/", actors.Select(p => p.LabelShort + "=" + rec.Has(p))) + " bank " + bank.Contains(rec.desc) + " scene " + rec.captionScene + " template " + rec.captionTemplate + " values " + rec.captionValues.Count + " desc " + rec.desc + " last " + CaptionLast(rec.desc) + "]");
            if (prefix == PhotoCaptionContext.CarriedBabyPrefix)
                CaptionCheck(BirthdayCarry.Holder(captionBaby) == captionHuman && !captionBaby.Spawned
                    && !captionBaby.relations.FamilyByBlood.Contains(captionHuman),
                    "stored baby caption follows an actually held child and a non-parent caregiver");
        }
        private static void CaptionStartCase()
        {
            // All edits occur before Request's deferred queue is processed by PhotoStore.GameComponentUpdate.
            CaptionSetActors(); captionCallback = false; captionExpected = captionPrefix = null;
            var actors = new List<Pawn> { captionHuman, captionAnimal }; string desc = null, kind = Kinds.Manual;
            switch (captionCase)
            {
                case 0: captionLabel = "caption-live-animal"; captionPrefix = PhotoCaptionContext.AnimalPairPrefix; break;
                case 1:
                    captionLabel = "caption-ground-baby"; CaptionPlace(captionAnimal, new IntVec3(19, 0, 3));
                    CaptionPlace(captionBaby, new IntVec3(1, 0, 0)); actors = new List<Pawn> { captionHuman, captionBaby }; break;
                case 2:
                    captionLabel = "caption-held-baby"; CaptionPlace(captionAnimal, new IntVec3(19, 0, 3));
                    CaptionHoldBaby(); actors = new List<Pawn> { captionHuman, captionBaby };
                    captionPrefix = PhotoCaptionContext.CarriedBabyPrefix; break;
                case 3:
                    captionLabel = "caption-missing-carrier"; CaptionPlace(captionAnimal, new IntVec3(19, 0, 3));
                    CaptionHoldBaby(); actors = new List<Pawn> { captionBaby }; captionPrefix = PhotoCaptionContext.CarriedBabyPrefix; break;
                case 4: captionLabel = "caption-unrecorded-human"; actors = new List<Pawn> { captionAnimal }; captionPrefix = PhotoCaptionContext.AnimalPairPrefix; break;
                case 5: captionLabel = "caption-outside-human"; break;
                case 6:
                    captionLabel = "caption-dead-animal"; CaptionPlace(captionAnimal, new IntVec3(19, 0, 3));
                    captionDeadAnimal = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDef.Named("Husky"), Faction.OfPlayer,
                        forceGenerateNewPawn: true, fixedBiologicalAge: 3f, fixedChronologicalAge: 3f));
                    captionDeadAnimal.Name = new NameSingle("Caption deceased animal");
                    GenSpawn.Spawn(captionDeadAnimal, center + new IntVec3(1, 0, 0), map); captionDeadAnimal.Kill(null);
                    actors = new List<Pawn> { captionHuman, captionDeadAnimal }; break;
                case 7:
                    captionLabel = "caption-explicit-event"; kind = "party";
                    desc = captionExpected = "Verified event description. Do not replace this event's text."; break;
                case 8: captionLabel = "caption-late-user-text"; break;
                case 9: captionLabel = "caption-late-retitle"; break;
                case 10: captionLabel = "caption-late-empty-text"; break;
                default: throw new InvalidOperationException("unknown caption case " + captionCase);
            }
            SettleDraw();
            CaptionCheck(!PhotoCapture.Busy, captionLabel + " starts with no pending exposure");
            if (captionCase >= 8)
                captionCurrent = PhotoStore.Instance.Request(map, center, kind, captionLabel, captionLabel, desc, actors, 9f, 1.5f,
                    () => captionCallback = true);
            else captionCurrent = PhotoStore.Instance.RequestNow(map, center.ToVector3Shifted(), kind, captionLabel,
                captionLabel, desc, actors, 9f, 1.5f, () => captionCallback = true);
            CaptionCheck(captionCurrent != null && !captionCurrent.HasFile, captionLabel + " is a newly registered uncaptured record");
            captionInitial = captionCurrent.desc;
            if (captionCase == 5)
            {
                CaptionCheck(captionCurrent.Has(captionHuman) && captionCurrent.Has(captionAnimal),
                    "out-of-frame guard begins with both real actors registered");
                CaptionPlace(captionHuman, new IntVec3(19, 0, 0)); SettleDraw();
            }
            else if (captionCase == 8)
            {
                CaptionCheck(captionCurrent.generatedDesc && !PhotoCapture.Busy, "manual description edit occurs while capture remains queued");
                captionExpected = "User text entered after registration, before the queued photo.";
                captionCurrent.SetDescription(captionExpected);
                CaptionCheck(!captionCurrent.generatedDesc, "SetDescription marks the late user text as explicit");
            }
            else if (captionCase == 9)
            {
                captionExpected = "Retitled event text entered before the queued exposure.";
                CaptionCheck(!PhotoCapture.Busy && PhotoStore.Instance.Retitle(captionLabel, "Retitled native event", "party", actors, captionExpected)
                    && !captionCurrent.generatedDesc, "public Retitle marks queued explicit event text before capture");
            }
            else if (captionCase == 10)
            {
                captionExpected = ""; captionCurrent.SetDescription(null);
                CaptionCheck(captionCurrent.desc == "" && !captionCurrent.generatedDesc,
                    "clearing a queued description is an explicit user choice");
            }
        }
        private static void CaptionCheckCase()
        {
            CaptionPicture(captionCurrent, captionLabel);
            if (captionPrefix != null)
            {
                CaptionSpecial(captionCurrent, captionPrefix, captionLabel);
                if (captionCase == 2) captionBabyPhotoId = captionCurrent.id;
            }
            else if (captionExpected != null)
                CaptionCheck(!captionCurrent.generatedDesc && captionCurrent.desc == captionExpected,
                    captionLabel + " preserves its exact explicit text through the real capture callback");
            else
            {
                var photographed = PhotoCapture.VisiblePawns(map).Where(captionCurrent.Has).ToList();
                CaptionCheck(PhotoDesc.ForCapturedContext(captionCurrent, photographed, map).NullOrEmpty(),
                    captionLabel + " production context dispatcher rejects unsupported visible facts");
                if (captionCase == 5)
                {
                    string fact = CaptionText.Clean("MF_PhotoFactPeople".Translate(captionCurrent.title, 1).ToString());
                    CaptionCheck(captionCurrent.peopleIds.Count == 1 && captionCurrent.Has(captionAnimal) && !captionCurrent.Has(captionHuman)
                        && captionCurrent.desc.Replace("\r\n", "\n").Split('\n')[0] == fact,
                        "actual capture removes the person who left its frame and regenerates the correct one-actor fact");
                }
                else CaptionCheck(captionCurrent.desc == captionInitial,
                    captionLabel + " retains its original generated text without inventing a special pair [" + captionInitial + "] -> [" + captionCurrent.desc + "]");
                if (captionCase == 1) CaptionCheck(captionBaby.Spawned && BirthdayCarry.Holder(captionBaby) == null,
                    "ground-baby exposure really shows a spawned child rather than a held child");
                if (captionCase == 3) CaptionCheck(captionCurrent.Has(captionBaby) && !captionCurrent.Has(captionHuman)
                    && BirthdayCarry.Holder(captionBaby) == captionHuman,
                    "a real but unrecorded carrier cannot authorize the carried-baby sentence bank");
                if (captionCase == 4) CaptionCheck(captionCurrent.peopleIds.Count == 1 && captionCurrent.Has(captionAnimal)
                    && PhotoCapture.InFrame(captionHuman, map, captionCurrent.center, 9f, 1.5f),
                    "a nearby but unrecorded person cannot authorize an animal-and-person caption");
                if (captionCase == 6) CaptionCheck(captionDeadAnimal.Dead && captionDeadAnimal.Corpse?.Spawned == true
                    && captionCurrent.Has(captionDeadAnimal), "dead-animal guard photographs an actual recorded native corpse");
            }
        }
        private static void CaptionBanks()
        {
            var store = PhotoStore.Instance; var previous = store.Descriptions;
            CaptionSetActors(captionBaby != null);
            try
            {
                foreach (string prefix in CaptionPrefixes())
                {
                    Traverse.Create(store).Field("descriptionHistory").SetValue(new DescriptionHistory());
                    var values = CaptionValues(prefix); var keys = DescriptionCandidates.EligibleKeys(prefix, 2, values, DescriptionCandidates.Lookup);
                    var bank = CaptionBank(prefix);
                    CaptionCheck(keys.Count == 12 && bank.Count == 12 && !bank.Contains("")
                        && keys.All(k => DescriptionCandidates.Lookup(k).Contains("{6}") || DescriptionCandidates.Lookup(k).Contains("{7}")),
                        "active native language supplies 12 distinct eligible " + prefix + " sentences naming a verified actor (keys " + keys.Count + ", bank " + bank.Count + ", without a name: " + string.Join(",", keys.Where(k => !DescriptionCandidates.Lookup(k).Contains("{6}") && !DescriptionCandidates.Lookup(k).Contains("{7}"))) + ")");
                    var rec = CaptionFixture(prefix, "context-pool:" + prefix);
                    var drawn = new List<string>();
                    for (int i = 0; i < 36; i++) drawn.Add(CaptionLast(PhotoDesc.ForCapturedContext(rec, CaptionActors(prefix), map)));
                    CaptionCheck(Enumerable.Range(0, 3).All(cycle => new HashSet<string>(drawn.Skip(cycle * 12).Take(12)).SetEquals(bank))
                        && drawn.Select((text, i) => i == 0 || !drawn.Skip(Math.Max(0, i - 5)).Take(Math.Min(5, i)).Contains(text)).All(ok => ok),
                        "production dispatcher exhausts all 12 " + prefix + " candidates before refilling and avoids the recent five across three cycles");
                    CaptionCheck(drawn.All(s => (s.Contains(values[6]) || s.Contains(values[7])) && !s.Contains("{6}") && !s.Contains("{7}")),
                        "every actual " + prefix + " draw names a real actor and leaves no slot unresolved");
                    File.WriteAllLines(Path.Combine(dir, prefix + "-examples.txt"), drawn.Take(12));
                    float expected, after;
                    Rand.PushState(79231); try { expected = Rand.Value; } finally { Rand.PopState(); }
                    Rand.PushState(79231);
                    try { PhotoDesc.ForCapturedContext(rec, CaptionActors(prefix), map); after = Rand.Value; }
                    finally { Rand.PopState(); }
                    CaptionCheck(expected == after, "production " + prefix + " selection preserves the surrounding native game RNG stream");
                    var missing = CaptionValues(prefix); missing[7] = "";
                    CaptionCheck(DescriptionCandidates.EligibleKeys(prefix, 2, missing, DescriptionCandidates.Lookup).All(k => !DescriptionCandidates.Lookup(k).Contains("{7}")),
                        "no candidate in " + prefix + " that names the companion can format without that name");
                }
                var animalRecord = CaptionFixture(PhotoCaptionContext.AnimalPairPrefix, "context-faction-guard");
                captionAnimal.SetFaction(null);
                try { CaptionCheck(PhotoDesc.ForCapturedContext(animalRecord, CaptionActors(PhotoCaptionContext.AnimalPairPrefix), map).NullOrEmpty(),
                    "a real wild animal cannot authorize the player-animal caption bank"); }
                finally { captionAnimal.SetFaction(Faction.OfPlayer); }
                var wrongMap = CaptionFixture(PhotoCaptionContext.AnimalPairPrefix, "context-map-guard"); wrongMap.mapId = map.uniqueID + 1;
                CaptionCheck(PhotoDesc.ForCapturedContext(wrongMap, CaptionActors(PhotoCaptionContext.AnimalPairPrefix), map).NullOrEmpty(),
                    "a record from another map cannot authorize a pair found on the current map");
            }
            finally { Traverse.Create(store).Field("descriptionHistory").SetValue(previous); }
        }
        private static string[] CaptionNext(string prefix) => Enumerable.Range(0, 18).Select(i => CaptionLast(PhotoDesc.ForCapturedContext(
            CaptionFixture(prefix, "context-continuation:" + prefix + ":" + i), CaptionActors(prefix), map))).ToArray();
        private static void CaptionSave()
        {
            var store = PhotoStore.Instance; CaptionSetActors(captionBaby != null);
            Traverse.Create(store).Field("descriptionHistory").SetValue(new DescriptionHistory());
            foreach (string prefix in CaptionPrefixes()) for (int i = 0; i < 3; i++)
                PhotoDesc.ForCapturedContext(CaptionFixture(prefix, "context-saved:" + prefix + ":" + i), CaptionActors(prefix), map);
            foreach (var rec in store.Records)
            {
                captionStoredFacts[rec.id] = CaptionFact(rec);
                if (rec.HasFile) captionStoredHashes[rec.id] = CaptionHash(store.OriginalPath(rec));
                if (rec.generatedDesc) captionGeneratedIds.Add(rec.id);
            }
            GameDataSaveLoader.SaveGame("mf-caption-context");
            var xml = new XmlDocument(); xml.Load(GenFilePaths.FilePathForSavedGame("mf-caption-context"));
            foreach (string prefix in CaptionPrefixes())
            {
                var bag = xml.SelectSingleNode("//descriptionHistory/bags/li[prefix='" + prefix + "']");
                CaptionCheck(bag != null && bag.SelectNodes("used/li").Count == 3 && bag.SelectNodes("recent/li").Count == 3,
                    "actual game XML stores the three used and recent " + prefix + " candidates");
                captionContinuation[prefix] = CaptionNext(prefix);
            }
            var existing = store.Descriptions; Traverse.Create(store).Field("descriptionHistory").SetValue(new DescriptionHistory());
            try
            {
                foreach (string prefix in CaptionPrefixes()) CaptionCheck(!CaptionNext(prefix).SequenceEqual(captionContinuation[prefix]),
                    "positive control: reset " + prefix + " history changes the next actual dispatcher sequence");
            }
            finally { Traverse.Create(store).Field("descriptionHistory").SetValue(existing); }
            var oldMarkers = xml.SelectNodes("//records/li/generatedDesc").Cast<XmlNode>().ToList();
            CaptionCheck(oldMarkers.Count > 0, "legacy fixture removes real new-description markers from the actual saved photo records");
            foreach (var node in oldMarkers) node.ParentNode.RemoveChild(node);
            xml.Save(GenFilePaths.FilePathForSavedGame("mf-caption-context-legacy"));
            File.WriteAllText(Path.Combine(dir, "caption-context-examples.json"), "{\"language\":" + Json(LanguageDatabase.activeLanguage.folderName)
                + ",\"captured\":[" + string.Join(",", captionExamples.Select(Json)) + "]}");
        }
        private static void CaptionLoad(string name)
        {
            captionPriorPause = Prefs.PauseOnLoad; Prefs.PauseOnLoad = true;
            try { GameDataSaveLoader.LoadGame(name); }
            catch { Prefs.PauseOnLoad = captionPriorPause; throw; }
        }
        private static void CaptionReadActors()
        {
            Prefs.PauseOnLoad = captionPriorPause; map = Find.CurrentMap; Find.TickManager.Pause();
            Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear(); CloseLog();
            var available = PhotoCapture.VisiblePawns(map).ToList();
            captionHuman = available.First(p => p.GetUniqueLoadID() == captionHumanId);
            captionAnimal = available.First(p => p.GetUniqueLoadID() == captionAnimalId);
            if (captionBabyId != null) captionBaby = available.First(p => p.GetUniqueLoadID() == captionBabyId);
        }
        private static void CaptionCheckLoaded(bool legacy)
        {
            var store = PhotoStore.Instance;
            CaptionCheck(captionStoredFacts.Count > 0 && captionStoredFacts.All(kv => store.Get(kv.Key) != null
                && CaptionFact(store.Get(kv.Key)) == kv.Value),
                (legacy ? "legacy" : "current") + " actual save/load preserves every old, generated, manual, empty and event description verbatim");
            CaptionCheck(captionStoredHashes.All(kv => store.Get(kv.Key)?.HasFile == true
                && CaptionHash(store.OriginalPath(store.Get(kv.Key))) == kv.Value),
                (legacy ? "legacy" : "current") + " actual load leaves every stored native photo PNG unchanged");
            if (legacy) CaptionCheck(captionGeneratedIds.Count > 0 && captionGeneratedIds.All(id => !store.Get(id).generatedDesc),
                "actual old game XML without generatedDesc loads the safe false default and keeps its previously chosen text");
        }
        private static void CaptionContextStep()
        {
            switch (phase)
            {
                case 0: CaptionSetup(); Wait(.5f); phase = 1; return;
                case 1:
                    CaptionPlace(captionHuman, new IntVec3(-13, 0, -5)); CaptionPlace(captionAnimal, new IntVec3(-11, 0, -3));
                    captionHumanStart = captionHuman.Position; captionAnimalStart = captionAnimal.Position;
                    captionPetJob = PetPhotos.Start(captionSpot, captionHuman, captionAnimal);
                    CaptionCheck(captionPetJob != null && captionPetJob.IsPetPhoto && captionPetJob.Members.Count == 2,
                        "actual PetPhotos.Start creates the chosen person-and-animal gathering"); phase = 2; return;
                case 2:
                    if (!CaptionAdvancePet()) return;
                    CaptionCheck(captionHumanWalked && captionAnimalWalked && captionPetJob.InPlace == 2 && captionPetJob.Shooting,
                        "actual native pet duties walk both actors into the photo before exposure"); phase = 3; return;
                case 3:
                    if (PhotoCapture.Busy) return;
                    for (int i = 0; i < 30 && (captionHuman.GetLord() != null || captionAnimal.GetLord() != null); i++) Find.TickManager.DoSingleTick();
                    captionCurrent = PhotoStore.Instance.Get(captionPetJob.PhotoId); captionPetPhotoId = captionCurrent?.id;
                    CaptionCheck(captionPetJob.Finished && captionPetJob.Takes == 1 && captionPetJob.Result == LordJob_MFGroupPhoto.Outcome.Shot
                        && captionCurrent?.kind == PetPhotos.Kind && captionHuman.GetLord() == null && captionAnimal.GetLord() == null,
                        "actual pet photograph saves once and releases both actors");
                    CaptionPicture(captionCurrent, "caption-real-pet-gathering");
                    CaptionSpecial(captionCurrent, PhotoCaptionContext.AnimalPairPrefix, "actual PetPhotos.Start path"); phase = 10; return;
                case 10:
                    if (captionBaby == null && captionCase >= 1 && captionCase <= 3) captionCase = 4;
                    if (captionCase > 10) { CaptionBanks(); phase = 20; return; }
                    CaptionStartCase(); phase = 11; return;
                case 11:
                    if (!captionCallback || PhotoCapture.Busy)
                    {
                        if (!PhotoStore.Instance.Records.Contains(captionCurrent)) throw new InvalidOperationException("caption capture was dropped: " + captionLabel);
                        return;
                    }
                    CaptionCheckCase(); captionCase++; phase = 10; return;
                case 20:
                    MainTabWindow_Album.Open(PhotoStore.Instance.Get(captionPetPhotoId)); Wait(.5f); phase = 21; return;
                case 21: CloseLog(); Shot("caption-context-animal-album.png"); Wait(.6f); phase = 22; return;
                case 22:
                    Require("caption-context-animal-album.png"); Find.MainTabsRoot.EscapeCurrentTab(false);
                    if (captionBabyPhotoId == null) { phase = 25; return; }
                    MainTabWindow_Album.Open(PhotoStore.Instance.Get(captionBabyPhotoId)); Wait(.5f); phase = 23; return;
                case 23: CloseLog(); Shot("caption-context-baby-album.png"); Wait(.6f); phase = 24; return;
                case 24: Require("caption-context-baby-album.png"); Find.MainTabsRoot.EscapeCurrentTab(false); phase = 25; return;
                case 25: CaptionSave(); Wait(.5f); phase = 26; return;
                case 26: CaptionLoad("mf-caption-context"); Wait(1f); phase = 27; return;
                case 27:
                    CaptionReadActors(); CaptionCheckLoaded(false); CaptionSetActors(captionBaby != null);
                    foreach (string prefix in CaptionPrefixes()) CaptionCheck(CaptionNext(prefix).SequenceEqual(captionContinuation[prefix]),
                        "actual game save/load continues the exact next 18 " + prefix + " dispatcher choices, including refill/recent history");
                    CaptionCheckLoaded(false); CaptionLoad("mf-caption-context-legacy"); Wait(1f); phase = 28; return;
                case 28:
                    CaptionReadActors(); CaptionCheckLoaded(true); Wait(.7f); phase = 29; return;
                case 29:
                    CaptionCheckLoaded(true);
                    File.WriteAllText(Path.Combine(dir, "caption-context-result.json"), "{\"ok\":true,\"checks\":[" + string.Join(",", checks.Select(Json))
                        + "],\"limits\":\"Isolated native caption/capture/save fixtures. Baby pickup is instantaneous native carryTracker state, not a walk/return test. Full user saves, full modpack compatibility and natural gameplay remain untested.\"}");
                    active = false; Application.Quit(); return;
            }
        }
    }
}
