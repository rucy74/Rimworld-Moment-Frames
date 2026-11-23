using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool birthdayPreparationMode, birthdayUiMeasured;
        private static Pawn preparationBaby, preparationGuardian;
        private static string preparationBabyId, preparationGuardianId;
        private static List<Pawn> preparationGuests;
        private static LordJob_MFGroupPhoto preparationJob;
        private static Building_Bed preparationBed;
        private static int preparationTicks;
        private static Dialog_BirthdaySetup preparationDialog;
        private static Pawn preparationBlockedGuest;
        private static IntVec3 preparationBlockedPosition;
        private static List<Thing> preparationWalls;
        private static void PreparationCheck(bool ok, string label)
        { if (!ok) throw new InvalidOperationException("birthday preparation: " + label); checks.Add("PASS: " + label); }
        private static bool AdvancePreparation(Func<bool> until, int budget = 90)
        {
            for (int i = 0; i < budget && !until(); i++) { Find.TickManager.DoSingleTick(); preparationTicks++; }
            if (preparationTicks > 6000) throw new TimeoutException("baby flow: " + phase + " job=" + preparationGuardian.CurJobDef
                + " pos=" + preparationGuardian.Position + " carried=" + preparationGuardian.carryTracker.CarriedThing
                + " result=" + preparationJob.Result + " inPlace=" + preparationJob.InPlace + " roster=" + preparationJob.Roster);
            return until();
        }
        private static void RefreshPreparation()
        {
            map = Find.CurrentMap;
            preparationBaby = PhotoCapture.VisiblePawns(map).First(p => p.GetUniqueLoadID() == preparationBabyId);
            preparationGuardian = map.mapPawns.FreeColonistsSpawned.First(p => p.GetUniqueLoadID() == preparationGuardianId);
            preparationGuests = map.mapPawns.FreeColonistsSpawned.Where(p => p != preparationBaby).Take(4).ToList();
            preparationGuests.Remove(preparationGuardian); preparationGuests.Insert(0, preparationGuardian);
            preparationJob = Birthday.Active(preparationBaby) ?? preparationJob;
            preparationBed = map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>().First(b => b.OwnersForReading.Contains(preparationBaby));
        }
        private static void ShowPreparation(Pawn subject, int age, string theme, string font, int size)
        {
            MomentFramesMod.Settings.albumTheme = theme; MomentFramesMod.Settings.albumFont = font; MomentFramesMod.Settings.albumTextSize = size;
            preparationDialog = new Dialog_BirthdaySetup(subject, age, null, fixesSpot.Position); Find.WindowStack.Add(preparationDialog);
        }
        private static bool SamePreparationGuests(IEnumerable<Pawn> a, IEnumerable<Pawn> b) => new HashSet<Pawn>(a).SetEquals(b);
        private static void CheckPreparationUndo()
        {
            preparationDialog.SelectGuests(2);
            var before = preparationDialog.Chosen.ToList();
            preparationDialog.SelectGuests(1);
            PreparationCheck(preparationDialog.CanUndoSelection && preparationDialog.UndoSelection()
                && SamePreparationGuests(before, preparationDialog.Chosen), "select all undo restores the previous guest draft");
            PreparationCheck(!preparationDialog.CanUndoSelection && !preparationDialog.UndoSelection(), "selection undo is consumed rather than toggling drafts");
            preparationDialog.SelectGuests(1); preparationDialog.SelectGuests(1);
            PreparationCheck(preparationDialog.UndoSelection() && SamePreparationGuests(before, preparationDialog.Chosen),
                "no-op select all preserves the earlier undo point");
            preparationDialog.SelectRandomGuests(4, new System.Random(9));
            PreparationCheck(preparationDialog.UndoSelection() && SamePreparationGuests(before, preparationDialog.Chosen),
                "random selection undo restores the previous guest draft");
            preparationDialog.SelectGuests(1);
            var optional = preparationGuests.First(p => p != preparationGuardian);
            PreparationCheck(preparationDialog.ToggleGuest(optional) && preparationDialog.UndoSelection()
                && SamePreparationGuests(before, preparationDialog.Chosen), "individual guest toggle does not replace the bulk-selection undo point");
            preparationDialog.SelectGuests(1);
        }
        private static List<Thing> PreparationBuildWalls(IEnumerable<IntVec3> cells)
        {
            var walls = new List<Thing>();
            foreach (var cell in cells)
            {
                var wall = ThingMaker.MakeThing(ThingDef.Named("Wall"), ThingDefOf.Steel);
                wall.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(wall, cell, map); walls.Add(wall);
            }
            PreparationRefreshRegions(); return walls;
        }
        private static void PreparationRefreshRegions()
        {
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms(); map.reachability.ClearCache(); map.mapDrawer.RegenerateEverythingNow();
        }
        private static void PreparationRemoveWalls(List<Thing> walls)
        {
            foreach (var wall in walls) if (!wall.Destroyed) wall.Destroy(DestroyMode.Vanish);
            PreparationRefreshRegions();
        }
        private static void CheckPreparationCapacity()
        {
            var guests = new[] { preparationGuardian }.Concat(preparationGuests.Where(p => p != preparationGuardian).Take(2)).ToList();
            var blocked = new List<IntVec3>();
            for (int z = 1; z <= GroupFormation.MaxRows; z++)
                for (int x = -1; x <= 1; x++)
                    if (z != 1 || x == 0) blocked.Add(fixesSpot.Position + new IntVec3(x, 0, z));
            var walls = PreparationBuildWalls(blocked);
            var plan = BirthdayPreparationPlan.Create(preparationGuardian, fixesSpot.Position, guests, Rot4.North, null);
            PreparationCheck(!plan.CanStart && plan.Moving.Count == 3 && plan.Cells.Count == 2
                && plan.Reason == "MF_PreparationSpaceProblem".Translate(3, 2, 3).ToString(),
                "native obstructed formation reports three selected people and only two standing places before gathering");
            int lords = map.lordManager.lords.Count;
            PreparationCheck(!Birthday.StartPrepared(preparationGuardian, 31, fixesSpot.Position, guests, Rot4.North, null)
                && map.lordManager.lords.Count == lords && Birthday.Active(preparationGuardian) == null,
                "final gather click rechecks capacity and creates no lord when standing places are insufficient");
            PreparationRemoveWalls(walls);
            PreparationCheck(BirthdayPreparationPlan.Create(preparationGuardian, fixesSpot.Position, guests, Rot4.North, null).CanStart,
                "removing formation obstacles restores a valid preparation plan");
        }
        private static void BeginPreparationReachFixture()
        {
            preparationBlockedGuest = preparationGuests.First(p => p != preparationGuardian);
            preparationBlockedPosition = preparationBlockedGuest.Position;
            FixReady(preparationBlockedGuest);
            preparationBlockedGuest.Position = center + new IntVec3(-18, 0, -14);
            preparationBlockedGuest.Notify_Teleported(true, true);
            var surround = new List<IntVec3>();
            for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
                if (x != 0 || z != 0) surround.Add(preparationBlockedGuest.Position + new IntVec3(x, 0, z));
            preparationWalls = PreparationBuildWalls(surround);
            var plan = BirthdayPreparationPlan.Create(preparationGuardian, fixesSpot.Position, preparationGuests, Rot4.North, null);
            PreparationCheck(plan.Cells.Count == preparationGuests.Count && !plan.CanStart
                && plan.Problems.TryGetValue(preparationBlockedGuest, out var why)
                && why == "MF_PreparationCannotReach".Translate(preparationBlockedGuest.LabelShort).ToString(),
                "native unreachable guest is identified before gathering even with enough standing places");
            int lords = map.lordManager.lords.Count;
            PreparationCheck(!Birthday.StartPrepared(preparationGuardian, 31, fixesSpot.Position, preparationGuests, Rot4.North, null)
                && map.lordManager.lords.Count == lords && Birthday.Active(preparationGuardian) == null,
                "final gather click rechecks native reachability and creates no lord for an unreachable guest");
        }
        private static void EndPreparationReachFixture()
        {
            PreparationRemoveWalls(preparationWalls);
            PreparationCheck(BirthdayPreparationPlan.Create(preparationGuardian, fixesSpot.Position, preparationGuests, Rot4.North, null).CanStart,
                "opening the blocked guest's route clears the preparation problem");
            preparationBlockedGuest.Position = preparationBlockedPosition; preparationBlockedGuest.Notify_Teleported(true, true);
            FixReady(preparationBlockedGuest);
        }
        private static void EndPreparation()
        {
            File.WriteAllText(Path.Combine(dir, "birthday-preparation-result.json"), "{\"ok\":true,\"checks\":[" + string.Join(",", checks.Select(Json))
                + "],\"limits\":\"Disposable fixtures with 22C climate; natural player save, full modpack and aesthetic acceptance are untested.\"}");
            active = false; Application.Quit();
        }
        private static void BirthdayPreparationStep()
        {
            switch (phase)
            {
                case 0:
                    FxSet(false); FixSetup(); Find.MainTabsRoot.EscapeCurrentTab(false);
                    map.Biome.constantOutdoorTemperature = 22f; // Probe fixture only, never the user's profile.
                    MomentFramesMod.Settings.msColonyStart = false; MomentFramesMod.Settings.birthdayMode = Birthday.ModeOff;
                    preparationGuests = map.mapPawns.FreeColonistsSpawned.ToList();
                    while (preparationGuests.Count < 7)
                    { var p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer); GenSpawn.Spawn(p, center + new IntVec3(-10, 0, preparationGuests.Count), map); preparationGuests.Add(p); }
                    foreach (var p in preparationGuests) FixReady(p);
                    preparationGuardian = preparationGuests.First(p => p.DevelopmentalStage.Adult()); preparationGuardianId = preparationGuardian.GetUniqueLoadID();
                    fixesSpot = (Building_PhotoSpot)ThingMaker.MakeThing(ThingDef.Named("MF_PhotoSpot"));
                    fixesSpot.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(fixesSpot, center + new IntVec3(8, 0, 0), map);
                    ShowPreparation(preparationGuardian, 31, "graphite", "pencil", 130);
                    Find.WindowStack.Add(new BirthdayPreparationMeasure()); Wait(.8f); phase = 1; return;
                case 1:
                    PreparationCheck(birthdayUiMeasured, "native OnGUI measured all supported font sizes and narrow layout");
                    Find.WindowStack.TryRemove(typeof(BirthdayPreparationMeasure), false); CloseLog(); Shot("birthday-preparation-graphite-130.png"); Wait(.4f); phase = 2; return;
                case 2:
                    Require("birthday-preparation-graphite-130.png"); preparationDialog.Close();
                    ShowPreparation(preparationGuardian, 31, "graphite", "pencil", 130);
                    PreparationCheck(!preparationDialog.ExpandedSettings, "photo settings begin collapsed with guests visible");
                    PreparationCheck(!preparationDialog.ToggleGuest(preparationGuardian), "guest row cannot remove the birthday subject");
                    CheckPreparationUndo();
                    var optionalGuest = preparationGuests.First(p => p != preparationGuardian);
                    bool wasSelected = preparationDialog.Chosen.Contains(optionalGuest);
                    PreparationCheck(preparationDialog.ToggleGuest(optionalGuest) && preparationDialog.Chosen.Contains(optionalGuest) != wasSelected
                        && preparationDialog.ToggleGuest(optionalGuest) && preparationDialog.Chosen.Contains(optionalGuest) == wasSelected, "guest row toggles an optional guest both ways");
                    preparationDialog.SelectGuests(1);
                    PreparationCheck(preparationGuests.All(p => preparationDialog.Chosen.Contains(p)), "quick selection includes every available guest");
                    var samples = new HashSet<string>();
                    for (int seed = 0; seed < 100; seed++)
                    {
                        preparationDialog.SelectRandomGuests(4, new System.Random(seed));
                        var randomDraw = preparationDialog.Chosen;
                        if (randomDraw.Count != 4 || randomDraw.Distinct().Count() != 4 || !randomDraw.Contains(preparationGuardian)
                            || randomDraw.Any(p => !preparationGuests.Contains(p))) throw new InvalidOperationException("random guest seed " + seed);
                        samples.Add(string.Join(",", randomDraw.Select(p => p.thingIDNumber).OrderBy(id => id)));
                    }
                    PreparationCheck(samples.Count > 1, "100 seeded random draws preserve subject, exact count, unique available guests and varying subsets");
                    optionalGuest.drafter.Drafted = true;
                    preparationDialog.SelectRandomGuests(99, new System.Random(7));
                    PreparationCheck(!preparationDialog.Chosen.Contains(optionalGuest) && preparationDialog.Chosen.Count == preparationGuests.Count - 1,
                        "random request beyond available count clamps and excludes drafted guest");
                    optionalGuest.drafter.Drafted = false; preparationDialog.SelectGuests(1);
                    if (!preparationDialog.Chosen.Contains(optionalGuest)) throw new InvalidOperationException("stale selection fixture missing guest");
                    optionalGuest.drafter.Drafted = true;
                    preparationDialog.SelectGuests(1);
                    PreparationCheck(!preparationDialog.Chosen.Contains(optionalGuest), "select all replaces stale selection and excludes unavailable guest");
                    optionalGuest.drafter.Drafted = false; FixReady(optionalGuest);
                    preparationDialog.SelectGuests(2);
                    PreparationCheck(preparationDialog.Chosen.Count == 1 && preparationDialog.Chosen.Contains(preparationGuardian), "subject-only preset keeps the required birthday subject");
                    preparationDialog.SelectGuests(0);
                    var draft = preparationDialog.Chosen.ToList();
                    preparationDialog.ExpandedSettings = true;
                    PreparationCheck(draft.SequenceEqual(preparationDialog.Chosen), "expanding photo settings retains the selected guests");
                    preparationDialog.ExpandedSettings = false;
                    preparationDialog.SelectGuests(2); preparationDialog.SelectGuests(1);
                    PreparationCheck(preparationDialog.CanUndoSelection, "undo control is available after changing the complete guest list");
                    Wait(.5f); phase = 208; return;
                case 208:
                    CloseLog(); Shot("birthday-preparation-undo.png"); Wait(.4f); phase = 209; return;
                case 209:
                    Require("birthday-preparation-undo.png"); preparationDialog.Close();
                    CheckPreparationCapacity(); BeginPreparationReachFixture();
                    ShowPreparation(preparationGuardian, 31, "graphite", "pencil", 130);
                    preparationDialog.SelectGuests(1);
                    Wait(.5f); phase = 214; return;
                case 214:
                    CloseLog(); Shot("birthday-preparation-blocked-route.png"); Wait(.4f); phase = 215; return;
                case 215:
                    Require("birthday-preparation-blocked-route.png"); preparationDialog.Close(); EndPreparationReachFixture();
                    ShowPreparation(preparationGuardian, 31, "graphite", "pencil", 130);
                    preparationDialog.ExpandedSettings = true;
                    Wait(.5f); phase = 210; return;
                case 210:
                    CloseLog(); Shot("birthday-preparation-expanded.png"); Wait(.4f); phase = 211; return;
                case 211:
                    Require("birthday-preparation-expanded.png"); preparationDialog.Close();
                    ShowPreparation(preparationGuardian, 31, "graphite", "pencil", 130);
                    preparationDialog.windowRect = new Rect(380f, 20f, 520f, 750f);
                    preparationDialog.SelectGuests(1);
                    Wait(.5f); phase = 212; return;
                case 212:
                    CloseLog(); Shot("birthday-preparation-narrow.png"); Wait(.4f); phase = 213; return;
                case 213:
                    Require("birthday-preparation-narrow.png"); preparationDialog.Close();
                    ShowPreparation(preparationGuardian, 31, "darkroom", "system", 100); Wait(.6f); phase = 3; return;
                case 3:
                    CloseLog(); Shot("birthday-preparation-darkroom.png"); Wait(.4f); phase = 4; return;
                case 4:
                    Require("birthday-preparation-darkroom.png"); preparationDialog.Close();
                    if (!ModsConfig.BiotechActive) { PreparationCheck(!BirthdayCarry.CanCarry(preparationGuardian, preparationGuardian), "Core rejects a non-baby transport without accessing childcare"); EndPreparation(); return; }
                    preparationBaby = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                        forceGenerateNewPawn: true, fixedBiologicalAge: 1f, fixedChronologicalAge: 1f, allowDowned: true, developmentalStages: DevelopmentalStage.Baby));
                    preparationBaby.relations.AddDirectRelation(PawnRelationDefOf.Parent, preparationGuardian);
                    preparationBabyId = preparationBaby.GetUniqueLoadID();
                    preparationBed = (Building_Bed)ThingMaker.MakeThing(ThingDef.Named("BabySleepingSpot"));
                    preparationBed.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(preparationBed, center + new IntVec3(-17, 0, -2), map);
                    GenSpawn.Spawn(preparationBaby, preparationBed.Position, map); preparationBed.CompAssignableToPawn.TryAssignPawn(preparationBaby);
                    preparationBaby.needs.food.CurLevel = .95f; preparationBaby.needs.rest.CurLevel = .9f;
                    preparationBaby.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, preparationBed), JobCondition.InterruptForced);
                    preparationGuardian.Position = center + new IntVec3(-20, 0, -2); preparationGuardian.Notify_Teleported(true, true);
                    ShowPreparation(preparationBaby, 1, "graphite", "pen", 115);
                    PreparationCheck(preparationDialog.Guardian == preparationGuardian && preparationDialog.Chosen.Contains(preparationGuardian), "photo-spot baby setup recommends parent and includes required caregiver");
                    PreparationCheck(!preparationDialog.ToggleGuest(preparationGuardian) && preparationDialog.Chosen.Contains(preparationGuardian), "guest row cannot remove the baby's caregiver");
                    preparationDialog.SelectGuests(2);
                    var babyDraft = preparationDialog.Chosen.ToList();
                    preparationDialog.SelectGuests(1);
                    PreparationCheck(preparationDialog.UndoSelection() && SamePreparationGuests(babyDraft, preparationDialog.Chosen)
                        && preparationDialog.Chosen.Contains(preparationBaby) && preparationDialog.Chosen.Contains(preparationGuardian),
                        "baby selection undo preserves the birthday subject and required caregiver");
                    PreparationCheck(!preparationDialog.UndoSelection(), "baby selection undo also consumes its one saved draft");
                    for (int seed = 0; seed < 100; seed++)
                    {
                        preparationDialog.SelectRandomGuests(4, new System.Random(seed));
                        var randomDraw = preparationDialog.Chosen;
                        if (randomDraw.Count != 4 || randomDraw.Distinct().Count() != 4 || !randomDraw.Contains(preparationBaby) || !randomDraw.Contains(preparationGuardian))
                            throw new InvalidOperationException("baby random guest seed " + seed);
                    }
                    PreparationCheck(true, "100 baby random draws keep the baby and required caregiver within requested count");
                    preparationDialog.SelectRandomGuests(1, new System.Random(1));
                    PreparationCheck(preparationDialog.Chosen.Count == 2 && preparationDialog.Chosen.Contains(preparationBaby) && preparationDialog.Chosen.Contains(preparationGuardian),
                        "tiny random count cannot remove baby or caregiver");
                    preparationDialog.SelectGuests(2);
                    PreparationCheck(preparationDialog.Chosen.Count == 2 && preparationDialog.Chosen.Contains(preparationGuardian), "subject-only baby preset keeps the required caregiver");
                    preparationDialog.SelectGuests(0);
                    Wait(.7f); phase = 5; return;
                case 5:
                    CloseLog(); Shot("birthday-preparation-baby.png"); Wait(.4f); phase = 6; return;
                case 6:
                    Require("birthday-preparation-baby.png"); preparationDialog.Close();
                    PreparationCheck(!BirthdayCarry.CanCarry(preparationBaby, preparationBaby), "baby cannot be their own caregiver");
                    preparationGuardian.drafter.Drafted = true;
                    PreparationCheck(!BirthdayCarry.CanCarry(preparationGuardian, preparationBaby), "drafted caregiver cannot start baby transport");
                    preparationGuardian.drafter.Drafted = false; FixReady(preparationGuardian);
                    preparationGuests.Remove(preparationGuardian); preparationGuests.Insert(0, preparationGuardian);
                    var missingGuardian = BirthdayPreparationPlan.Create(preparationBaby, fixesSpot.Position,
                        new[] { preparationBaby, preparationGuardian }, Rot4.North, null, null, true);
                    PreparationCheck(!missingGuardian.CanStart && missingGuardian.Reason == "MF_BabyNoGuardian".Translate().ToString(),
                        "carry-mode preparation blocks a missing caregiver before gathering");
                    int beforeMissingCaregiver = map.lordManager.lords.Count;
                    PreparationCheck(!Birthday.StartPrepared(preparationBaby, 1, fixesSpot.Position,
                        new[] { preparationBaby, preparationGuardian }, Rot4.North, null, null, true)
                        && map.lordManager.lords.Count == beforeMissingCaregiver && Birthday.Active(preparationBaby) == null,
                        "final gather click keeps carry-mode requirement and creates no lord without a caregiver");
                    map.Biome.constantOutdoorTemperature = -80f;
                    fixesSpot.Position.GetRoom(map).Temperature = -80f;
                    var cold = BirthdayPreparationPlan.Create(preparationBaby, fixesSpot.Position,
                        new[] { preparationBaby, preparationGuardian }, Rot4.North, null, preparationGuardian, true);
                    PreparationCheck(!cold.CanStart && cold.Problems.TryGetValue(preparationGuardian, out var coldReason)
                        && coldReason == "MF_BabyUnsafePlace".Translate().ToString(), "unsafe cold baby destination is visible in preparation before pickup");
                    PreparationCheck(!Birthday.StartPrepared(preparationBaby, 1, fixesSpot.Position, new[] { preparationBaby, preparationGuardian }, Rot4.North, null, preparationGuardian), "unsafe cold photo spot rejected before baby pickup");
                    map.Biome.constantOutdoorTemperature = 22f;
                    fixesSpot.Position.GetRoom(map).Temperature = 22f;
                    PreparationCheck(Birthday.StartPrepared(preparationBaby, 1, fixesSpot.Position, new[] { preparationBaby }.Concat(preparationGuests.Take(3)).ToList(), Rot4.North, null, preparationGuardian), "baby birthday starts at photo spot rather than at bed");
                    preparationJob = Birthday.Active(preparationBaby); preparationTicks = 0;
                    PreparationCheck(preparationJob.spot == fixesSpot.Position && preparationJob.InPlace == 0, "caregiver must fetch baby before ready"); phase = 7; return;
                case 7:
                    if (!AdvancePreparation(() => preparationGuardian.carryTracker.CarriedThing == preparationBaby, 1)) return;
                    PreparationCheck(!preparationBaby.Spawned && preparationGuardian.Position != preparationJob.CellFor(preparationGuardian), "native pickup holds baby before walking to photo spot; no teleport");
                    GameDataSaveLoader.SaveGame("mf-baby-in-transit"); GameDataSaveLoader.LoadGame("mf-baby-in-transit"); Wait(1f); phase = 8; return;
                case 8:
                    RefreshPreparation();
                    PreparationCheck(preparationJob?.BirthdayGuardian == preparationGuardian && preparationGuardian.carryTracker.CarriedThing == preparationBaby
                        && preparationGuardian.jobs.curDriver is JobDriver_MFCarryBaby, "real load in transit preserves baby, caregiver, lord and driver");
                    preparationDialog = new Dialog_BirthdaySetup(preparationBaby, 1, preparationJob, preparationJob.spot);
                    Find.WindowStack.Add(preparationDialog); phase = 9; return;
                case 9:
                    if (!AdvancePreparation(() => preparationJob.InPlace == preparationJob.Members.Count)) return;
                    PreparationCheck(!preparationJob.Finished && preparationJob.Takes == 0 && !preparationJob.Shooting
                        && preparationJob.EditingBirthday && Find.WindowStack.IsOpen<Dialog_BirthdaySetup>(),
                        "carried baby and guests wait while the real roster editor is open");
                    PreparationCheck(PhotoCapture.VisualPosition(preparationBaby, map, out var draw) && (draw - preparationGuardian.DrawPos).sqrMagnitude < 3f, "baby metadata follows rendered carried position");
                    GameDataSaveLoader.SaveGame("mf-baby-holding"); GameDataSaveLoader.LoadGame("mf-baby-holding"); Wait(1f); phase = 10; return;
                case 10:
                    RefreshPreparation();
                    PreparationCheck(preparationGuardian.carryTracker.CarriedThing == preparationBaby && preparationJob.Takes == 0,
                        "real load while posing keeps baby held without inventing a successful photograph");
                    preparationDialog = new Dialog_BirthdaySetup(preparationBaby, 1, preparationJob, preparationJob.spot);
                    Find.WindowStack.Add(preparationDialog);
                    var editingPlan = BirthdayPreparationPlan.Create(preparationBaby, preparationJob.spot,
                        new[] { preparationBaby }.Concat(preparationJob.Members).ToList(), preparationJob.FormationDirection,
                        preparationJob, preparationGuardian, true);
                    PreparationCheck(editingPlan.CanStart && editingPlan.Problems.Count == 0,
                        "editing an existing birthday keeps its own lord members eligible instead of marking them busy");
                    var duplicatePlan = BirthdayPreparationPlan.Create(preparationBaby, preparationJob.spot,
                        new[] { preparationBaby }.Concat(preparationJob.Members).ToList(), preparationJob.FormationDirection,
                        null, preparationGuardian, true);
                    PreparationCheck(!duplicatePlan.CanStart, "new gathering is blocked while the same birthday gathering is active");
                    var replacementGuardian = preparationGuests.First(p => p != preparationGuardian);
                    PreparationCheck(!BirthdayPreparationPlan.Create(preparationBaby, preparationJob.spot,
                        new[] { preparationBaby }.Concat(preparationJob.Members).ToList(), preparationJob.FormationDirection,
                        preparationJob, replacementGuardian, true).CanStart
                        && preparationGuardian.carryTracker.CarriedThing == preparationBaby,
                        "caregiver replacement while baby is held is rejected without changing the held baby");
                    var roster = preparationJob.Members.Concat(preparationGuests).Distinct().Take(4).ToList();
                    PreparationCheck(preparationJob.ReplaceBirthdayRoster(preparationJob.spot, roster, GroupFormation.Cells(preparationJob.spot, roster.Count, map, Rot4.South), Rot4.South, preparationGuardian), "edit guests and direction while holding baby");
                    PreparationCheck(preparationGuardian.carryTracker.CarriedThing == preparationBaby, "roster edit preserves the held baby"); phase = 11; return;
                case 11:
                    if (!AdvancePreparation(() => preparationJob.InPlace == preparationJob.Members.Count)) return;
                    for (int i = 0; i < 130; i++) Find.TickManager.DoSingleTick();
                    PreparationCheck(preparationJob.Takes == 0 && preparationGuardian.Rotation == Rot4.North, "changed formation walks naturally and holds new facing");
                    PreparationCheck(preparationJob.ReplaceBirthdayRoster(preparationJob.spot, preparationJob.Members.ToList(),
                        GroupFormation.Cells(preparationJob.spot, preparationJob.Members.Count, map, Rot4.North), Rot4.North, preparationGuardian), "return to front-facing formation for sample photo");
                    phase = 110; return;
                case 110:
                    if (!AdvancePreparation(() => preparationJob.InPlace == preparationJob.Members.Count)) return;
                    // The probe manually ticks a paused world. Refresh render interpolation after actual pathing, without moving anyone.
                    foreach (var attendee in preparationJob.Members) attendee.Drawer.tweener.ResetTweenedPosToRoot();
                    Wait(.5f); phase = 111; return;
                case 111:
                    preparationDialog.Close();
                    if (!AdvancePreparation(() => preparationJob.Shooting || preparationJob.Takes > 0)) return;
                    PreparationCheck(preparationJob.Shooting, "closing the roster editor allows the ready baby birthday to photograph automatically");
                    PreparationCheck(!BirthdayPreparationPlan.Create(preparationBaby, preparationJob.spot,
                        new[] { preparationBaby }.Concat(preparationJob.Members).ToList(), preparationJob.FormationDirection,
                        preparationJob, preparationGuardian, true).CanStart, "birthday roster cannot be applied while photography is in progress");
                    phase = 12; return;
                case 12:
                    if (PhotoCapture.Busy || preparationJob.Shooting) return;
                    var rec = PhotoStore.Instance.Get(preparationJob.PhotoId);
                    PreparationCheck(rec?.HasFile == true && rec.Has(preparationBaby) && rec.Has(preparationGuardian) && rec.people.Count == 5,
                        "saved PNG includes carried baby and all four attendees in metadata: actual=" + (rec == null ? "null" : string.Join(",", rec.people)));
                    PreparationCheck(rec.HasPeopleBox && preparationJob.Finished && preparationJob.Takes == 1, "baby photo has measured crop bounds and completes gathering");
                    PreparationCheck(!BirthdayPreparationPlan.Create(preparationBaby, preparationJob.spot,
                        new[] { preparationBaby }.Concat(preparationJob.Members).ToList(), preparationJob.FormationDirection,
                        preparationJob, preparationGuardian, true).CanStart, "finished birthday gathering cannot accept a late roster edit");
                    File.Copy(PhotoStore.Instance.FilePath(rec), Path.Combine(dir, "birthday-carried-baby.png"), true); preparationTicks = 0; phase = 121; return;
                case 121:
                    if (!AdvancePreparation(() => preparationGuardian.GetLord() == null && preparationGuardian.pather.Moving
                        && preparationGuardian.carryTracker.CarriedThing == preparationBaby, 1)) return;
                    PreparationCheck(preparationGuardian.CurJob.lord == null, "return job releases removed gathering reference before save");
                    GameDataSaveLoader.SaveGame("mf-baby-returning"); GameDataSaveLoader.LoadGame("mf-baby-returning"); Wait(1f); phase = 122; return;
                case 122:
                    RefreshPreparation();
                    PreparationCheck(preparationGuardian.GetLord() == null && preparationGuardian.carryTracker.CarriedThing == preparationBaby,
                        "real load on return trip preserves caregiver and baby after guests disband"); phase = 13; return;
                case 13:
                    if (!AdvancePreparation(() => preparationBaby.Spawned && preparationGuardian.carryTracker.CarriedThing == null)) return;
                    PreparationCheck(preparationBaby.Position == preparationBed.Position, "after photograph caregiver walks baby back to assigned bed");
                    PreparationCheck(preparationGuests.All(p => p.GetLord() == null), "all birthday guests released after photo");
                    foreach (var p in preparationGuests) FixReady(p);
                    PreparationCheck(Birthday.StartPrepared(preparationBaby, 2, fixesSpot.Position, new[] { preparationBaby, preparationGuardian }, Rot4.North, null, preparationGuardian), "second baby gathering starts for cancellation check");
                    preparationJob = Birthday.Active(preparationBaby); preparationTicks = 0; phase = 14; return;
                case 14:
                    if (!AdvancePreparation(() => preparationJob.InPlace == preparationJob.Members.Count)) return;
                    preparationJob.Cancel(); preparationTicks = 0; phase = 15; return;
                case 15:
                    if (!AdvancePreparation(() => preparationBaby.Spawned && preparationGuardian.carryTracker.CarriedThing == null)) return;
                    PreparationCheck(preparationBaby.Position == preparationBed.Position && !PhotoStore.Instance.HasKey(Birthday.Key(preparationBaby, 2)), "cancellation returns baby to bed without recording a photograph");
                    FixReady(preparationGuardian);
                    PreparationCheck(Birthday.StartPrepared(preparationBaby, 3, fixesSpot.Position, new[] { preparationBaby, preparationGuardian }, Rot4.North, null, preparationGuardian), "start caregiver departure fixture");
                    preparationJob = Birthday.Active(preparationBaby); preparationTicks = 0; phase = 16; return;
                case 16:
                    if (!AdvancePreparation(() => preparationJob.InPlace == preparationJob.Members.Count)) return;
                    preparationJob.Leave(preparationGuardian); preparationTicks = 0; phase = 17; return;
                case 17:
                    if (!AdvancePreparation(() => preparationBaby.Spawned && preparationGuardian.carryTracker.CarriedThing == null)) return;
                    PreparationCheck(preparationBaby.Position == preparationBed.Position && preparationJob.Finished
                        && !PhotoStore.Instance.HasKey(Birthday.Key(preparationBaby, 3)), "caregiver leaving cancels gathering and returns baby without recording");
                    FixReady(preparationGuardian);
                    PreparationCheck(Birthday.StartPrepared(preparationBaby, 4, fixesSpot.Position, new[] { preparationBaby, preparationGuardian }, Rot4.North, null, preparationGuardian), "start cancellation before pickup fixture");
                    preparationJob = Birthday.Active(preparationBaby); preparationJob.Cancel();
                    for (int i = 0; i < 130; i++) Find.TickManager.DoSingleTick();
                    PreparationCheck(preparationBaby.Spawned && preparationBaby.Position == preparationBed.Position
                        && preparationGuardian.carryTracker.CarriedThing == null, "cancellation before pickup leaves baby in bed");
                    PreparationCheck(!BirthdayCarry.CanCarry(preparationGuardian, preparationGuests.First(p => p != preparationGuardian)), "walking colonist is not treated as a carryable baby");
                    PreparationCheck(GroupPhoto.Start(map, fixesSpot.Position, GroupPhoto.Eligible(map, fixesSpot.Position)) != null, "ordinary photo spot still starts a direct group gathering");
                    PreparationCheck(!Find.WindowStack.IsOpen<Dialog_BirthdaySetup>(), "ordinary group photo does not introduce birthday preparation UI");
                    var ordinary = map.lordManager.lords.Select(l => l.LordJob).OfType<LordJob_MFGroupPhoto>().First(j => !j.IsBirthday && !j.Finished);
                    ordinary.Cancel(); EndPreparation(); return;
            }
        }
        private sealed class BirthdayPreparationMeasure : Window
        {
            public BirthdayPreparationMeasure() { doCloseX = false; }
            public override void DoWindowContents(Rect r)
            {
                if (birthdayUiMeasured) return;
                var settings = MomentFramesMod.Settings; int previousSize = settings.albumTextSize;
                var originalStyle = Text.fontStyles[(int)GameFont.Small]; var originalFont = Text.Font; var originalColor = GUI.color;
                PreparationCheck(preparationGuests.All(p => Dialog_BirthdaySetup.PortraitFor(p) != null), "native current pawn portrait textures available for every guest");
                foreach (int size in AlbumStyle.Sizes)
                {
                    settings.albumTextSize = size;
                    using (AlbumStyle.Begin())
                    {
                        Text.Font = GameFont.Small; Text.WordWrap = true;
                        float height = Dialog_BirthdaySetup.ControlHeight;
                        PreparationCheck(height >= AlbumStyle.StyleFor("Ag", GameFont.Small).fontSize * 1.7f, "controls keep chosen font size " + size);
                        Rect area = Dialog_BirthdaySetup.SettingsArea(new Rect(24f, 24f, 472f, 560f), 65f, height + 110f);
                        PreparationCheck(area.height > 160f && area.yMax < 584f - height, "narrow settings scroll above fixed confirmation at size " + size);
                        float measured = Dialog_BirthdaySetup.ControlHeightFor(452f);
                        PreparationCheck(new[] { "MF_BirthdayStartGather", "MF_BirthdayApplyGuests", "MF_BirthdayPreview" }
                            .All(key => AlbumStyle.TextHeight(key.Translate(), (452f - 10f) / 2f - 12f, GameFont.Small) <= measured - 12f),
                            "narrow buttons wrap at chosen font size instead of shrinking " + size);
                    }
                    PreparationCheck(Text.fontStyles[(int)GameFont.Small] == originalStyle && Text.Font == originalFont && GUI.color == originalColor, "appearance scope restores native UI at size " + size);
                }
                settings.albumTextSize = previousSize; birthdayUiMeasured = true;
            }
        }
    }
}
