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
    // Entered only through Probe's existing marked disposable-profile guard, never a normal player session.
    public static partial class Probe
    {
        private static bool petPhotosMode;
        private static Pawn petsOwner, petsOtherOwner, petsAnimal, petsResident, petsWild;
        private static string petsOwnerId, petsAnimalId, petsResidentId, petsWildId, petsFamilyId, petsPairId;
        private static string petsFamilyHash, petsPairHash;
        private static Building_PhotoSpot petsSpot;
        private static LordJob_MFGroupPhoto petsJob;
        private static Dialog_PetPhotoSetup petsDialog;
        private static GameComponent_PetMilestones petsMilestones;
        private static readonly Dictionary<int, HashSet<string>> petsRendered = new Dictionary<int, HashSet<string>>();
        private static int petsTicks, petsCaptureFrame, petsBeforeCount, petsJoinedAbs;
        private static IntVec3 petsFamilyHome = IntVec3.Invalid;
        private static bool petsOwnerWalked, petsAnimalWalked, petsGuardDone;
        private static IntVec3 petsOwnerStart, petsAnimalStart;
        private static float petsShotWait = -1f;
        private static bool petsShotRetried;
        private static Pawn petsGuardAnimal;
        private static string petsGuardKey;
        private static bool petsLoadPending, petsPriorPauseOnLoad;
        private static int petsLoadGameTick, petsLoadAbsTick;
        private static string petsLoadName;
        private static int petsObservedPhase = -1;
        private static float petsPhaseStarted, petsProgressAt;

        private static void PetsCheck(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("pet photos: " + label);
            checks.Add("PASS: " + label);
        }
        private static string PetsHash(string path)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
        }
        private static void PetsNoteRender(Pawn ___pawn)
        {
            // MapUpdate submits these draw calls before the end-of-frame Camera.Render consumes them.
            // The camera target is assigned later, so testing targetTexture here would discard every valid submission.
            if (!petPhotosMode || !active || ___pawn == null || !PhotoCapture.Busy || ___pawn.Map != Find.CurrentMap) return;
            int frameNumber = Time.frameCount;
            if (!petsRendered.TryGetValue(frameNumber, out var set)) petsRendered[frameNumber] = set = new HashSet<string>();
            set.Add(___pawn.GetUniqueLoadID());
            if (petsRendered.Count > 20000) petsRendered.Remove(petsRendered.Keys.Min());
        }
        private static void PetsCaptureDone() { if (petPhotosMode && active) petsCaptureFrame = PhotoCapture.LastRenderFrame; }
        private static void PetsNoteDrop(PhotoRecord rec)
        {
            if (!petPhotosMode || !active || rec == null) return;
            File.AppendAllText(Path.Combine(dir, "pet-progress.log"), "DROP kind=" + rec.kind + " key=" + rec.eventKey + " tick=" + Find.TickManager.TicksGame
                + " frame=" + Time.frameCount + " renderFrame=" + PhotoCapture.LastRenderFrame + " encodeMs=" + PhotoCapture.LastEncodeMilliseconds
                + " center=" + rec.center + " span=" + rec.spanCells + " animal=" + petsAnimal?.Position + " inFrame=" + (petsAnimal != null && PhotoCapture.InFrame(petsAnimal, map, rec.center, rec.spanCells, rec.Aspect))
                + Environment.NewLine + new System.Diagnostics.StackTrace(1, false) + Environment.NewLine);
        }
        private static bool PetsRequire(string name)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            { petsShotWait = -1f; petsShotRetried = false; Require(name); return true; }
            if (petsShotWait < 0f) petsShotWait = Time.realtimeSinceStartup;
            float elapsed = Time.realtimeSinceStartup - petsShotWait;
            if (elapsed > 15f) throw new TimeoutException("native pet screenshot did not finish: " + name);
            if (elapsed > 1f && !petsShotRetried) { Shot(name); petsShotRetried = true; }
            return false;
        }
        private static bool PetsAdvance(Func<bool> until, int budget = 30)
        {
            for (int i = 0; i < budget && !PhotoCapture.Busy && !until(); i++)
            {
                Find.TickManager.DoSingleTick(); petsTicks++;
                petsOwnerWalked |= petsOwner != null && petsOwner.Position != petsOwnerStart;
                petsAnimalWalked |= petsAnimal != null && petsAnimal.Position != petsAnimalStart;
            }
            if (petsTicks > 30000) throw new TimeoutException("pet flow phase " + phase + " ready=" + petsMilestones?.Ready
                + " pending=" + petsMilestones?.PendingCount + " pair=" + petsJob?.Result + " takes=" + petsJob?.Takes
                + " owner=" + petsOwner?.Position + "/" + petsOwner?.CurJobDef + " pet=" + petsAnimal?.Position + "/" + petsAnimal?.CurJobDef);
            bool done = until();
            if (done && petsJob?.Shooting == true)
                File.AppendAllText(Path.Combine(dir, "pet-progress.log"), "EXPOSURE BEFORE TWEEN RESET: " + PetsStateDiagnostic() + Environment.NewLine);
            // A fast tick batch has no intervening native render frames. Keep DrawPos near the actual cells,
            // as the existing group-photo fixtures do, before the next batch can choose a photo's centre.
            SettleDraw();
            if (done && petsJob?.Shooting == true)
                File.AppendAllText(Path.Combine(dir, "pet-progress.log"), "EXPOSURE AFTER TWEEN RESET: " + PetsStateDiagnostic() + Environment.NewLine);
            return done;
        }
        private static string PetsPawnDiagnostic(Pawn pawn, PhotoRecord record)
        {
            if (pawn == null) return "null";
            return pawn.GetUniqueLoadID() + " cell=" + pawn.Position + " draw=" + (pawn.Spawned ? pawn.DrawPos.ToString() : "unspawned")
                + " job=" + pawn.CurJobDef?.defName + " duty=" + pawn.mindState?.duty?.def?.defName
                + " lord=" + pawn.GetLord()?.GetUniqueLoadID() + " inFrame="
                + (record != null && PhotoCapture.InFrame(pawn, Find.CurrentMap, record.center, record.spanCells, record.Aspect));
        }
        private static string PetsStateDiagnostic()
        {
            var store = PhotoStore.Instance; var record = store?.Get(petsJob?.PhotoId);
            return "phase=" + phase + " phaseSeconds=" + (Time.realtimeSinceStartup - petsPhaseStarted).ToString("0.00")
                + " frame=" + Time.frameCount + " gameTick=" + Find.TickManager.TicksGame + " absTick=" + Find.TickManager.TicksAbs
                + " paused=" + Find.TickManager.Paused + " manualTicks=" + petsTicks + " captureBusy=" + PhotoCapture.Busy
                + " state=" + (petsJob == null ? "null" : Traverse.Create(petsJob).Field("state").GetValue<int>().ToString())
                + " shooting=" + petsJob?.Shooting + " finished=" + petsJob?.Finished + " holding=" + petsJob?.Holding
                + " result=" + petsJob?.Result + " takes=" + petsJob?.Takes + " inPlace=" + petsJob?.InPlace
                + " photoId=" + petsJob?.PhotoId + " record=" + record?.id + "/file=" + record?.file + "/hasFile=" + record?.HasFile
                + " center=" + record?.center + " span=" + record?.spanCells + " aspect=" + record?.Aspect
                + " records=" + store?.Records.Count + " family=" + (store?.Records.Count(r => r.kind == PetMilestones.Kind && r.HasFile) ?? -1)
                + " pairs=" + (store?.Records.Count(r => r.kind == PetPhotos.Kind && r.HasFile) ?? -1)
                + " pending=" + petsMilestones?.PendingCount + " cameraEnabled=" + Find.Camera?.enabled
                + " cameraDriverEnabled=" + Find.CameraDriver?.enabled + " expandFrame=" + PhotoCapture.LastExpandFrame
                + " renderFrame=" + PhotoCapture.LastRenderFrame + " owner=[" + PetsPawnDiagnostic(petsOwner, record)
                + "] animal=[" + PetsPawnDiagnostic(petsAnimal, record) + "]";
        }
        private static void PetsProgress()
        {
            float now = Time.realtimeSinceStartup;
            bool changed = petsObservedPhase != phase;
            if (changed) { petsObservedPhase = phase; petsPhaseStarted = now; }
            if (changed || now >= petsProgressAt)
            {
                string line = PetsStateDiagnostic();
                File.WriteAllText(Path.Combine(dir, "pet-progress.txt"), line + Environment.NewLine);
                if (changed) File.AppendAllText(Path.Combine(dir, "pet-progress.log"), line + Environment.NewLine);
                petsProgressAt = now + 1f;
            }
            if (now - petsPhaseStarted > (phase == 11 ? 20f : 90f))
                throw new TimeoutException("pet fixture phase did not progress: " + PetsStateDiagnostic());
        }
        private static Pawn PetsGenerate(string name, Faction faction, IntVec3 position)
        {
            var animal = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDef.Named("Husky"), faction,
                forceGenerateNewPawn: true, fixedBiologicalAge: 3f, fixedChronologicalAge: 3f));
            animal.Name = new NameSingle(name); GenSpawn.Spawn(animal, position, map); FixReady(animal); return animal;
        }
        private static int PetsCount(string kind) => PhotoStore.Instance.Records.Count(r => r.kind == kind && r.HasFile);
        private static PhotoRecord PetsFamily() => PhotoStore.Instance.Records.FirstOrDefault(r => r.eventKey == PetMilestones.Key(petsAnimal));
        private static LordJob_MFGroupPhoto PetsActive() => map.lordManager.lords.Select(l => l.LordJob)
            .OfType<LordJob_MFGroupPhoto>().FirstOrDefault(j => j.IsPetPhoto && !j.Finished && j.PetPawn?.GetUniqueLoadID() == petsAnimalId);
        private static void PetsCloseDialog() => Find.WindowStack.TryRemove(typeof(Dialog_PetPhotoSetup), false);
        private static void PetsShowDialog(bool dark = false, bool narrow = false)
        {
            MomentFramesMod.Settings.albumTheme = dark ? "darkroom" : "graphite";
            MomentFramesMod.Settings.albumFont = dark ? "system" : "pencil";
            MomentFramesMod.Settings.albumTextSize = 130;
            petsDialog = new Dialog_PetPhotoSetup(petsSpot, petsAnimal); Find.WindowStack.Add(petsDialog);
            if (narrow) petsDialog.windowRect = new Rect(360f, 15f, 560f, 750f);
        }
        private static void PetsAway()
        {
            FixReady(petsOwner); FixReady(petsAnimal);
            petsOwner.Position = center + new IntVec3(-19, 0, -4); petsOwner.Notify_Teleported(true, true);
            petsAnimal.Position = center + new IntVec3(-17, 0, 3); petsAnimal.Notify_Teleported(true, true); SettleDraw();
            petsOwnerStart = petsOwner.Position; petsAnimalStart = petsAnimal.Position;
            petsOwnerWalked = petsAnimalWalked = false; petsTicks = 0;
        }
        private static void PetsLoadPaused(string name)
        {
            // Native Game.LoadGame schedules one initialization tick, then pauses when this preference is true.
            // Keep it scoped to this disposable fixture so wall-clock render waits cannot advance simulation.
            petsPriorPauseOnLoad = Prefs.PauseOnLoad;
            petsLoadGameTick = Find.TickManager.TicksGame; petsLoadAbsTick = Find.TickManager.TicksAbs;
            petsLoadName = name; petsLoadPending = true; Prefs.PauseOnLoad = true;
            try { GameDataSaveLoader.LoadGame(name); }
            catch { Prefs.PauseOnLoad = petsPriorPauseOnLoad; petsLoadPending = false; throw; }
        }
        private static string PetsLoadDiagnostic()
        {
            var family = PetsFamily();
            return "save=" + petsLoadName + " pending=" + petsMilestones?.PendingCount
                + " family=" + (family?.id ?? "null") + "/" + family?.HasFile
                + " gameTick=" + Find.TickManager.TicksGame + "/saved=" + petsLoadGameTick
                + " absTick=" + Find.TickManager.TicksAbs + "/saved=" + petsLoadAbsTick
                + " paused=" + Find.TickManager.Paused;
        }
        private static void PetsReadActors()
        {
            if (petsLoadPending)
            {
                petsLoadPending = false; Prefs.PauseOnLoad = petsPriorPauseOnLoad;
                PetsCheck(Find.TickManager.Paused && Find.TickManager.TicksGame == petsLoadGameTick + 1
                    && Find.TickManager.TicksAbs == petsLoadAbsTick + 1,
                    "native load stays paused after its single initialization tick: save=" + petsLoadName
                    + " gameTick=" + Find.TickManager.TicksGame + "/saved=" + petsLoadGameTick
                    + " absTick=" + Find.TickManager.TicksAbs + "/saved=" + petsLoadAbsTick
                    + " paused=" + Find.TickManager.Paused);
            }
            map = Find.CurrentMap; petsMilestones = Current.Game.GetComponent<GameComponent_PetMilestones>();
            petsOwner = map.mapPawns.FreeColonistsSpawned.First(p => p.GetUniqueLoadID() == petsOwnerId);
            petsOtherOwner = map.mapPawns.FreeColonistsSpawned.First(p => p != petsOwner);
            petsAnimal = map.mapPawns.AllPawnsSpawned.First(p => p.GetUniqueLoadID() == petsAnimalId);
            petsResident = map.mapPawns.AllPawnsSpawned.First(p => p.GetUniqueLoadID() == petsResidentId);
            petsWild = map.mapPawns.AllPawnsSpawned.First(p => p.GetUniqueLoadID() == petsWildId);
            petsSpot = map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSpot>().First();
            petsJob = PetsActive(); Find.TickManager.Pause();
        }
        private static void PetsCheckPicture(PhotoRecord record, string label, params Pawn[] expected)
        {
            var store = PhotoStore.Instance;
            PetsCheck(record?.HasFile == true && File.Exists(store.OriginalPath(record)) && File.Exists(store.OriginalThumbPath(record)),
                label + " writes both native PNG and thumbnail files");
            PetsCheck(expected.All(p => record.Has(p) && record.peopleIds.Contains(p.GetUniqueLoadID())
                && record.requiredSubjects.Contains(p.GetUniqueLoadID())), label + " stores the exact required actor IDs and photo roster");
            PetsCheck(record.kind == PetMilestones.Kind || expected.All(p => PhotoCapture.InFrame(p, map, record.center, record.spanCells, record.Aspect)),
                label + " required living actors remain inside its actual saved framing");
            File.Copy(store.OriginalPath(record), Path.Combine(dir, label + "-photo.png"), true);
            File.Copy(store.OriginalThumbPath(record), Path.Combine(dir, label + "-thumb.png"), true);
            checks.Add(CaptureDiagLine(label + " native actor draw submission", expected));
            PetsCheck(petsRendered.TryGetValue(petsCaptureFrame, out var drawn) && expected.All(p => drawn.Contains(p.GetUniqueLoadID())),
                label + " submits every required actor through native PawnRenderer in the photo's actual render frame (capture frame " + petsCaptureFrame + ", expand " + PhotoCapture.LastExpandFrame + ", recorded " + string.Join(" ", petsRendered.Keys.OrderBy(k => k).Select(k => k + ":" + petsRendered[k].Count)) + ", expected " + string.Join(",", expected.Select(p => p.GetUniqueLoadID())) + ")");
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                PetsCheck(tex.LoadImage(File.ReadAllBytes(store.OriginalPath(record)), false) && tex.width > 100 && tex.height > 100,
                    label + " saved PNG decodes at the actual photo resolution");
                var px = tex.GetPixels32();
                PetsCheck(px.Any(p => p.r > 70 || p.g > 70 || p.b > 70), label + " image contains visible rendered scene pixels");
            }
            finally { UnityEngine.Object.Destroy(tex); }
        }
        private static void PetsSettingsAndRecordScribe()
        {
            string path = Path.Combine(dir, "pet-record.xml"); PhotoRecord fixture = PhotoStore.Instance.Get(petsPairId);
            var expected = fixture.requiredSubjects.ToList();
            Scribe.saver.InitSaving(path, "PetRecord"); Scribe_Deep.Look(ref fixture, "record"); Scribe.saver.FinalizeSaving();
            fixture = null; Scribe.loader.InitLoading(path); Scribe_Deep.Look(ref fixture, "record"); Scribe.loader.FinalizeLoading();
            PetsCheck(fixture.requiredSubjects.SequenceEqual(expected) && fixture.peopleIds.Contains(petsAnimalId),
                "actual Scribe round-trip preserves required owner and animal actor IDs");
            var xml = new XmlDocument(); xml.Load(path); var subjects = xml.SelectSingleNode("/PetRecord/record/requiredSubjects");
            PetsCheck(subjects != null, "required pet subjects are present in saved photo XML"); subjects.ParentNode.RemoveChild(subjects);
            string legacy = Path.Combine(dir, "pet-record-legacy.xml"); xml.Save(legacy);
            fixture = null; Scribe.loader.InitLoading(legacy); Scribe_Deep.Look(ref fixture, "record"); Scribe.loader.FinalizeLoading();
            PetsCheck(fixture.requiredSubjects != null && fixture.requiredSubjects.Count == 0 && fixture.id == petsPairId,
                "old photo record without requiredSubjects loads with an empty safe default");
            path = Path.Combine(dir, "pet-settings-off.xml"); MomentFramesSettings settings = new MomentFramesSettings { capturePetArrivals = false };
            Scribe.saver.InitSaving(path, "PetSettings"); Scribe_Deep.Look(ref settings, "settings"); Scribe.saver.FinalizeSaving();
            settings = null; Scribe.loader.InitLoading(path); Scribe_Deep.Look(ref settings, "settings"); Scribe.loader.FinalizeLoading();
            PetsCheck(!settings.capturePetArrivals, "automatic pet arrival photo preference survives actual Scribe round-trip");
        }
        private static void PetsCaptionBanks()
        {
            var store = PhotoStore.Instance; var previous = store.Descriptions;
            try
            {
                // Isolated bag fixture uses the production dispatcher; existing photo text and saved usage stay untouched.
                foreach (var kind in new[] { PetMilestones.Kind, PetPhotos.Kind })
                {
                    Traverse.Create(store).Field("descriptionHistory").SetValue(new DescriptionHistory());
                    var actors = new List<Pawn> { petsOwner, petsAnimal };
                    string[] values = { string.Join(", ", actors.Select(p => p.LabelShort)), petsOwner.LabelShort, "", "", petsAnimal.LabelShort, "" };
                    var expected = new HashSet<string>(Enumerable.Range(1, 8).Select(n => CaptionText.Clean(DescriptionCandidates.Format(
                        DescriptionCandidates.Lookup("MF_Caption_" + kind + "_" + n), values))));
                    var drawn = new List<string>();
                    for (int i = 0; i < 8; i++) drawn.Add(CaptionText.Clean(PetPhotos.Caption(kind, actors, map, petsAnimal, "pet-caption-fixture:" + kind + ":" + i)));
                    PetsCheck(expected.Count == 8 && !expected.Contains("") && new HashSet<string>(drawn).SetEquals(expected) && drawn.Distinct().Count() == 8,
                        "actual caption dispatcher reaches all eight " + kind + " sentences through one no-repeat bag");
                    PetsCheck(drawn.All(s => s.Contains(petsAnimal.LabelShort) && !s.Contains("{4}")
                        && (kind != PetPhotos.Kind || s.Contains(petsOwner.LabelShort))), "caption dispatcher resolves the animal and selected owner names for " + kind);
                    var captured = store.Get(kind == PetMilestones.Kind ? petsFamilyId : petsPairId);
                    string actual = CaptionText.Clean(captured.desc);
                    if (kind == PetPhotos.Kind)
                    {
                        var observed = (captured.captionValues ?? new List<string>()).ToArray();
                        PetsCheck(observed.Length > 7 && observed[6] == petsAnimal.LabelShort && observed[7] == petsOwner.LabelShort, "stored pair caption freezes the real animal and owner names");
                        bool bondedPair = captured.captionScene == "MF_Caption_scene_animal_bond";
                        int pairBankSize = bondedPair ? 6 : 12; string pairBank = bondedPair ? captured.captionScene : PhotoCaptionContext.AnimalPairPrefix;
                        var contextExpected = new HashSet<string>(Enumerable.Range(1, pairBankSize).Select(n => CaptionText.Clean(
                            DescriptionCandidates.Format(DescriptionCandidates.Lookup(pairBank + "_" + n), observed))));
                        File.WriteAllLines(Path.Combine(dir, "pair-caption-actual.txt"), new[] { "actual: " + actual, "scene: " + captured.captionScene, "template: " + captured.captionTemplate,
                            "values: " + string.Join(" | ", captured.captionValues ?? new List<string>()), "people: " + string.Join(", ", captured.people), "title: " + captured.title, "expected:" }.Concat(contextExpected));
                        PetsCheck(contextExpected.Count == pairBankSize && contextExpected.Contains(actual),
                            "actual native stored pair caption uses the verified co-photographed animal/person bank");
                    }
                    else PetsCheck(expected.Contains(actual), "actual native stored first-family caption keeps the matching eight-sentence bank");
                    File.WriteAllLines(Path.Combine(dir, kind + "-caption-examples.txt"), drawn);
                    float expectedRandom, afterCaption;
                    Rand.PushState(79391); try { expectedRandom = Rand.Value; } finally { Rand.PopState(); }
                    Rand.PushState(79391);
                    try { PetPhotos.Caption(kind, actors, map, petsAnimal, "random-preservation"); afterCaption = Rand.Value; }
                    finally { Rand.PopState(); }
                    PetsCheck(expectedRandom == afterCaption, "production caption selection preserves the surrounding game random stream for " + kind);
                }
            }
            finally { Traverse.Create(store).Field("descriptionHistory").SetValue(previous); }
        }
        private static void PetsCheckLists(bool stacked)
        {
            var a = petsDialog.AnimalListRect; var b = petsDialog.OwnerListRect;
            PetsCheck(a.width > 40f && b.width > 40f && a.height > 80f && b.height > 80f && !a.Overlaps(b)
                && (stacked ? a.yMax <= b.yMin : a.xMax <= b.xMin), "native setup keeps separate readable " + (stacked ? "stacked" : "side-by-side") + " animal and owner viewports at 130 percent");
        }
        private static void PetsGuardCapture(bool dead)
        {
            petsGuardDone = false;
            petsGuardAnimal = PetsGenerate(dead ? "Guard corpse" : "Guard moved", Faction.OfPlayer, center + new IntVec3(0, 0, -2));
            petsOwner.Position = center + new IntVec3(-2, 0, -2); petsOwner.Notify_Teleported(true, true); FixReady(petsOwner); SettleDraw();
            string key = petsGuardKey = PetMilestones.Key(petsGuardAnimal);
            var rec = PhotoStore.Instance.RequestNow(map, center.ToVector3Shifted(), PetMilestones.Kind, key,
                "Disposable required animal guard", "Native guard fixture", new[] { petsOwner, petsGuardAnimal }, 12f, 1.5f, () => petsGuardDone = true);
            PetsCheck(rec != null, "deferred native required-animal guard exposure starts: " + key);
            rec.requiredSubjects.Add(petsGuardAnimal.GetUniqueLoadID());
            if (dead) petsGuardAnimal.Kill(null);
            else { petsGuardAnimal.Position = center + new IntVec3(23, 0, 0); petsGuardAnimal.Notify_Teleported(true, true); SettleDraw(); }
        }
        private static void PetsBorrowedCheck()
        {
            // A precise temporary-host metadata fixture. A natural quest is not invoked by this probe.
            var animal = petsResident; var originalGuest = animal.guest;
            try
            {
                animal.guest = new Pawn_GuestTracker(animal);
                var host = Find.FactionManager.AllFactions.First(f => f != Faction.OfPlayer && !f.def.hidden);
                Traverse.Create(animal.guest).Field("hostFactionInt").SetValue(host);
                PetsCheck(animal.HostFaction == host && !PetMilestones.OwnedAnimal(animal)
                    && !PetPhotos.EligibleAnimal(animal) && !PetPhotos.Animals(map).Contains(animal),
                    "temporary-host animal is excluded from automatic and manual ownership lists");
            }
            finally { animal.guest = originalGuest; }
        }
        private static void PetPhotosStep()
        {
            PetsProgress();
            switch (phase)
            {
                case 0:
                    FxSet(false); FixSetup(); Find.MainTabsRoot.EscapeCurrentTab(false); map.Biome.constantOutdoorTemperature = 22f;
                    MomentFramesMod.Settings.msColonyStart = false; MomentFramesMod.Settings.msRareAnimal = false;
                    MomentFramesMod.Settings.lifeEvents = false; MomentFramesMod.Settings.birthdayMode = Birthday.ModeOff;
                    MomentFramesMod.Settings.captureReactionMode = PhotoReactions.ModeOff; MomentFramesMod.Settings.capturePetArrivals = true;
                    petsOwner = map.mapPawns.FreeColonistsSpawned.First(p => p.DevelopmentalStage.Adult());
                    petsOtherOwner = map.mapPawns.FreeColonistsSpawned.First(p => p != petsOwner);
                    foreach (var person in map.mapPawns.FreeColonistsSpawned) FixReady(person);
                    petsOwnerId = petsOwner.GetUniqueLoadID();
                    petsMilestones = Current.Game.GetComponent<GameComponent_PetMilestones>();
                    PetsCheck(petsMilestones?.Ready == true, "native GameComponent completes existing-resident baseline before observing new joins");
                    petsResident = PetsGenerate("Nabi", Faction.OfPlayer, center + new IntVec3(-5, 0, -3)); petsResidentId = petsResident.GetUniqueLoadID();
                    petsAnimal = PetsGenerate("Bori", null, center + new IntVec3(1, 0, -2)); petsAnimalId = petsAnimal.GetUniqueLoadID();
                    petsWild = PetsGenerate("Wild observer", null, center + new IntVec3(4, 0, -5)); petsWildId = petsWild.GetUniqueLoadID();
                    petsSpot = (Building_PhotoSpot)ThingMaker.MakeThing(ThingDef.Named("MF_PhotoSpot")); petsSpot.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(petsSpot, center + new IntVec3(8, 0, 0), map);
                    var harmony = new Harmony("momentframes.probe.pets");
                    harmony.Patch(AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt)), prefix: new HarmonyMethod(typeof(Probe), nameof(PetsNoteRender)));
                    harmony.Patch(AccessTools.Method(typeof(PhotoCapture), "Render"), postfix: new HarmonyMethod(typeof(Probe), nameof(PetsCaptureDone)));
                    harmony.Patch(AccessTools.Method(typeof(PhotoStore), "Drop"), prefix: new HarmonyMethod(typeof(Probe), nameof(PetsNoteDrop)));
                    PetsCheck(!PetMilestones.OwnedAnimal(petsWild) && !PetPhotos.EligibleAnimal(petsWild) && !PetPhotos.Animals(map).Contains(petsWild),
                        "wild animal is excluded from automatic family photos and manual companion selection");
                    PetsCheck(!petsMilestones.ObserveJoin(petsResident, petsOwner) && petsMilestones.PendingCount == 0,
                        "spawning or re-observing an existing player animal does not backfill a first-family photograph");
                    PetsBorrowedCheck(); Wait(3f); phase = 1; return;
                case 1:
                    petsOwner.Position = petsAnimal.Position + new IntVec3(-2, 0, 0); petsOwner.Notify_Teleported(true, true); SettleDraw();
                    petsJoinedAbs = Find.TickManager.TicksAbs; InteractionWorker_RecruitAttempt.DoRecruit(petsOwner, petsAnimal, false);
                    PetsCheck(petsAnimal.Faction == Faction.OfPlayer && petsMilestones.PendingCount == 1 && PhotoStore.Instance.HasMark(PetMilestones.Key(petsAnimal)),
                        "actual vanilla successful tame path observes the faction gain and queues one first-family candidate");
                    GameDataSaveLoader.SaveGame("mf-pet-arrival-pending"); PetsLoadPaused("mf-pet-arrival-pending");
                    Wait(3f); phase = 20; return;
                case 20:
                    PetsReadActors(); PetsCheck(petsMilestones.PendingCount == 1 && PetsFamily() == null,
                        "actual save/load preserves one unphotographed arrival candidate instead of backfilling or losing it: " + PetsLoadDiagnostic());
                    petsTicks = 0; phase = 2; return;
                case 2:
                    if (petsFamilyHome == IntVec3.Invalid && petsCaptureFrame > 0 && PhotoCapture.Busy)
                    {
                        // The shutter has fired and the file is still being written: walking off now must not cost the photo.
                        petsFamilyHome = petsAnimal.Position; petsAnimal.Position = petsAnimal.Position + new IntVec3(30, 0, 30); petsAnimal.Notify_Teleported(true, true); SettleDraw();
                    }
                    if (!PetsAdvance(() => PetsFamily()?.HasFile == true)) return;
                    var family = PetsFamily(); petsFamilyId = family.id;
                    PetsCheck(petsFamilyHome != IntVec3.Invalid && !PhotoCapture.InFrame(petsAnimal, map, family.center, family.spanCells, family.Aspect) && family.peopleIds.Contains(petsAnimalId),
                        "first-family photo survives its animal walking out of frame while the file is written");
                    petsAnimal.Position = petsFamilyHome; petsAnimal.Notify_Teleported(true, true); SettleDraw();
                    PetsCheckPicture(family, "pet-family", petsAnimal);
                    PetsCheck(family.kind == PetMilestones.Kind && family.eventSubjectIds.Contains(petsAnimalId)
                        && PetsCount(PetMilestones.Kind) == 1 && family.ticksAbs == petsJoinedAbs,
                        "real tame records exactly one first-family photo with the specific animal identity and original arrival time");
                    petsFamilyHash = PetsHash(PhotoStore.Instance.OriginalPath(family));
                    petsAnimal.training.Train(TrainableDefOf.Obedience, petsOwner, true); petsAnimal.playerSettings.Master = petsOwner;
                    PetsCheck(PetPhotos.DefaultOwner(petsAnimal) == petsOwner, "manual setup recommends the actual trained master");
                    PetsShowDialog(); Wait(.6f); phase = 3; return;
                case 3:
                    PetsCheck(petsDialog.SelectedPet == petsAnimal && petsDialog.SelectedOwner == petsOwner, "native setup initializes the selected animal and actual master");
                    PetsCheck(!petsDialog.SelectAnimal(petsWild), "native setup refuses selecting a wild animal");
                    PetsCheck(petsDialog.SelectOwner(petsOtherOwner) && petsDialog.SelectAnimal(petsResident)
                        && petsDialog.SelectedOwner == petsOtherOwner, "explicit owner choice survives changing to another eligible animal");
                    petsDialog.SelectAnimal(petsAnimal); petsDialog.SelectOwner(petsOwner);
                    PetsCheckLists(false);
                    CloseLog(); Shot("pet-setup-graphite-130.png"); phase = 4; return;
                case 4:
                    if (!PetsRequire("pet-setup-graphite-130.png")) return; PetsCloseDialog(); PetsShowDialog(false, true); Wait(.5f); phase = 5; return;
                case 5:
                    PetsCheckLists(true); CloseLog(); Shot("pet-setup-narrow-130.png"); phase = 6; return;
                case 6:
                    if (!PetsRequire("pet-setup-narrow-130.png")) return; PetsCloseDialog(); PetsShowDialog(true); Wait(.5f); phase = 7; return;
                case 7:
                    CloseLog(); Shot("pet-setup-darkroom-130.png"); phase = 8; return;
                case 8:
                    if (!PetsRequire("pet-setup-darkroom-130.png")) return; PetsCloseDialog(); PetsAway(); PetsShowDialog();
                    petsDialog.SelectOwner(petsOwner); petsDialog.SelectAnimal(petsAnimal);
                    PetsCheck(petsDialog.StartSelected(), "native preparation action starts an owner-and-animal gathering"); petsJob = PetsActive();
                    petsDialog.Close();
                    PetsCheck(petsJob != null && petsJob.PetOwner == petsOwner && petsJob.PetPawn == petsAnimal
                        && petsJob.Members.Count == 2 && petsJob.lord.ownedPawns.Count == 2 && petsJob.InPlace < 2,
                        "manual pet gathering enlists exactly the chosen two actors and waits for both to arrive");
                    for (int i = 0; i < 20; i++) Find.TickManager.DoSingleTick();
                    PetsCheck(!petsJob.Shooting && petsJob.Takes == 0, "manual pet exposure does not fire while owner and pet are still walking");
                    GameDataSaveLoader.SaveGame("mf-pet-waiting"); PetsLoadPaused("mf-pet-waiting"); Wait(1f); phase = 9; return;
                case 9:
                    PetsReadActors(); PetsCheck(petsJob != null && petsJob.Members.Count == 2 && petsJob.PetOwner == petsOwner && petsJob.PetPawn == petsAnimal,
                        "actual save/load restores the same waiting pet gathering and both actor references");
                    PetsCheck(PetsCount(PetMilestones.Kind) == 1 && petsMilestones.PendingCount == 0 && !petsMilestones.ObserveJoin(petsAnimal, petsOwner),
                        "actual load and first re-observation do not replay an already recorded first-family arrival");
                    petsTicks = 0; phase = 10; return;
                case 10:
                    if (!PetsAdvance(() => petsJob.Shooting || petsJob.Finished, 30)) return;
                    PetsCheck(petsJob.Shooting && petsJob.InPlace == 2 && petsOwnerWalked && petsAnimalWalked,
                        "both actors really walk under native duties before the two-person exposure starts"); phase = 11; return;
                case 11:
                    if (PhotoCapture.Busy) return;
                    if (!petsJob.Finished)
                        throw new InvalidOperationException("pet exposure callback did not finish the gathering: " + PetsStateDiagnostic());
                    if (!PetsAdvance(() => petsOwner.GetLord() == null && petsAnimal.GetLord() == null, 30)) return;
                    var pair = PhotoStore.Instance.Get(petsJob.PhotoId); petsPairId = pair?.id;
                    PetsCheck(petsJob.Takes == 1 && petsJob.Result == LordJob_MFGroupPhoto.Outcome.Shot && pair?.kind == PetPhotos.Kind,
                        "successful pet photo takes once and finishes automatically");
                    PetsCheckPicture(pair, "pet-owner-pair", petsOwner, petsAnimal); petsPairHash = PetsHash(PhotoStore.Instance.OriginalPath(pair));
                    PetsCheck(!Reunion.Possible(pair) && !Reunion.Possible(PhotoStore.Instance.Get(petsFamilyId)),
                        "new animal photos hide the human-only reunion action rather than omit the animal or mislabel it as not ours");
                    PetsCheck("MF_PetSubjects".CanTranslate() && "MF_PetSubjects".Translate("actor").ToString() != "MF_People".Translate("actor").ToString(),
                        "animal photo subjects have a separate neutral album label in the active language");
                    PetsCheck(petsOwner.GetLord() == null && petsAnimal.GetLord() == null, "both owner and animal are released automatically after the photograph");
                    PetsSettingsAndRecordScribe(); PetsCaptionBanks(); MainTabWindow_Album.Open(pair); Wait(.5f); phase = 12; return;
                case 12:
                    CloseLog(); Shot("pet-pair-album-130.png"); phase = 13; return;
                case 13:
                    if (!PetsRequire("pet-pair-album-130.png")) return; Find.MainTabsRoot.EscapeCurrentTab(false);
                    petsAnimal.SetFaction(null); petsAnimal.SetFaction(Faction.OfPlayer, petsOwner);
                    PetsCheck(petsMilestones.PendingCount == 0 && !petsMilestones.ObserveJoin(petsAnimal, petsOwner), "re-taming an already recorded animal never relabels it as its first family day");
                    petsBeforeCount = PetsCount(PetPhotos.Kind); PetsAway(); petsJob = PetPhotos.Start(petsSpot, petsOwner, petsAnimal);
                    PetsCheck(petsJob != null, "second companion gathering may start as an explicit new photo"); petsJob.Cancel(); petsTicks = 0; phase = 14; return;
                case 14:
                    if (!PetsAdvance(() => petsOwner.GetLord() == null && petsAnimal.GetLord() == null, 30)) return;
                    PetsCheck(petsJob.Finished && petsJob.Takes == 0 && PetsCount(PetPhotos.Kind) == petsBeforeCount, "cancel releases both actors and leaves no additional photograph");
                    PetsAway(); petsJob = PetPhotos.Start(petsSpot, petsOwner, petsAnimal); PetsCheck(petsJob != null, "start native companion dropout fixture");
                    petsAnimal.needs.food.CurLevel = .01f; petsTicks = 0; phase = 15; return;
                case 15:
                    if (!PetsAdvance(() => petsJob.Finished && petsOwner.GetLord() == null && petsAnimal.GetLord() == null, 30)) return;
                    PetsCheck(petsJob.Takes == 0 && PetsCount(PetPhotos.Kind) == petsBeforeCount
                        && petsOwner.GetLord() == null && petsAnimal.GetLord() == null, "animal becoming too hungry cancels the session and releases its owner without a partial photo");
                    FixReady(petsAnimal); FixReady(petsOwner);
                    var sleeping = (Building_Bed)ThingMaker.MakeThing(ThingDef.Named("AnimalSleepingSpot")); sleeping.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(sleeping, petsAnimal.Position, map); sleeping.CompAssignableToPawn.TryAssignPawn(petsAnimal);
                    petsAnimal.needs.rest.CurLevel = .05f; petsAnimal.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, sleeping), JobCondition.InterruptForced);
                    for (int i = 0; i < 90; i++) Find.TickManager.DoSingleTick(); Wait(.2f); phase = 16; return;
                case 16:
                    PetsCheck(!petsAnimal.Awake() && !PetPhotos.CanStart(petsSpot, petsOwner, petsAnimal, out var asleepWhy)
                        && asleepWhy == "MF_Why_Asleep".Translate().ToString(), "sleeping pet is identified as asleep and cannot be summoned for a manual companion photo");
                    FixReady(petsAnimal); FixReady(petsOwner); petsOwner.drafter.Drafted = true;
                    PetsCheck(!PetPhotos.CanStart(petsSpot, petsOwner, petsAnimal, out _), "unavailable drafted owner cannot start a companion photo");
                    petsOwner.drafter.Drafted = false; petsAnimal.health.AddHediff(HediffDefOf.Anesthetic);
                    PetsCheck(petsAnimal.Downed && !PetPhotos.CanStart(petsSpot, petsOwner, petsAnimal, out _), "actually downed animal cannot start a companion photo");
                    FixReady(petsAnimal); PetsGuardCapture(false); phase = 17; return;
                case 17:
                    if (!petsGuardDone || PhotoCapture.Busy) return;
                    PetsCheck(!PhotoStore.Instance.HasKey(petsGuardKey), "animal moving out before deferred rendering rejects the record instead of storing empty scenery");
                    PetsCheck(PhotoStore.Instance.HasMark(petsGuardKey), "a failed first-family exposure retains its first-ownership mark");
                    petsGuardAnimal.SetFaction(null); petsGuardAnimal.SetFaction(Faction.OfPlayer, petsOwner);
                    PetsCheck(petsMilestones.PendingCount == 0, "re-taming after a failed first-family exposure does not invent another first day");
                    petsGuardAnimal.Destroy(DestroyMode.Vanish); PetsGuardCapture(true); phase = 18; return;
                case 18:
                    if (!petsGuardDone || PhotoCapture.Busy) return;
                    PetsCheck(!PhotoStore.Instance.HasKey(petsGuardKey), "corpse cannot substitute for the required living first-family photo subject");
                    PetsCheck(PhotoStore.Instance.HasMark(petsGuardKey), "rejected corpse exposure preserves the original first-ownership mark");
                    MomentFramesMod.Settings.capturePetArrivals = false;
                    var muted = PetsGenerate("Arrived while off", null, center + new IntVec3(-4, 0, -1)); muted.SetFaction(Faction.OfPlayer, petsOwner);
                    PetsCheck(PhotoStore.Instance.HasMark(PetMilestones.Key(muted)) && petsMilestones.PendingCount == 0, "first ownership is remembered while automatic pet photos are disabled");
                    MomentFramesMod.Settings.capturePetArrivals = true;
                    PetsCheck(!petsMilestones.ObserveJoin(muted, petsOwner) && petsMilestones.PendingCount == 0, "reenabling automatic pet photos does not backfill an earlier arrival");
                    GameDataSaveLoader.SaveGame("mf-pet-final"); PetsLoadPaused("mf-pet-final"); Wait(1f); phase = 19; return;
                case 19:
                    PetsReadActors();
                    PetsCheck(PetsCount(PetMilestones.Kind) == 1 && PetsCount(PetPhotos.Kind) == 1 && petsMilestones.PendingCount == 0,
                        "final actual game save/load retains one arrival and one companion photo without replay");
                    PetsCheck(PetsHash(PhotoStore.Instance.OriginalPath(PhotoStore.Instance.Get(petsFamilyId))) == petsFamilyHash
                        && PetsHash(PhotoStore.Instance.OriginalPath(PhotoStore.Instance.Get(petsPairId))) == petsPairHash,
                        "save/load and rejected captures leave both successful original photographs unchanged");
                    File.WriteAllText(Path.Combine(dir, "pet-photos-result.json"), "{\"ok\":true,\"checks\":[" + string.Join(",", checks.Select(Json))
                        + "],\"limits\":\"Marked disposable native fixtures. Vanilla successful DoRecruit/SetFaction path, native actor walking, waiting save/load and render-to-texture are exercised. Natural taming chance, quest generation, births/deaths beyond explicit guard fixtures, original user saves and full modpack compatibility remain untested. Temporary-host exclusion uses an explicit guest metadata fixture.\"}");
                    active = false; Application.Quit(); return;
            }
        }
    }
}
