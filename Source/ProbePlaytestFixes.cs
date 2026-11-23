using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool playtestFixesMode, fixesFailRender;
        private static LordJob_MFGroupPhoto fixesJob;
        private static readonly List<Pawn> fixesPeople = new List<Pawn>();
        private static Building_PhotoSpot fixesSpot;
        private static Pawn fixesChild;
        private static string fixesSubjectId, fixesSavedCaption, fixesEventId;
        private static int fixesAge = 31;
        private static PhotoRecord fixesPhoto;
        private static bool fixesUiMeasured;
        private static int fixesFailures, fixesGuideBefore;
        private static bool fixesDrawPreview;
        private static int fixesNormalPreviewCalls;
        private static int fixesGatherTicks;
        private static bool FixGatherUntil(Func<bool> until, int budget = 30)
        {
            for (int i = 0; i < budget && !PhotoCapture.Busy && !until(); i++) { Find.TickManager.DoSingleTick(); fixesGatherTicks++; }   // a picture being written holds the shot back in real time, not game time
            if (fixesGatherTicks > 6000) throw new TimeoutException("playtest gathering phase " + phase + ", result=" + fixesJob.Result
                + ", ready=" + fixesJob.InPlace + "/" + fixesJob.Members.Count + ", photo busy=" + PhotoCapture.Busy + ", editing=" + fixesJob.EditingBirthday + ", takes=" + fixesJob.Takes + ", ticks=" + fixesGatherTicks + ", frame=" + Time.frameCount);
            return until();
        }
        private static void FixesPreview(Map __instance)
        {
            if (!fixesDrawPreview || __instance != Find.CurrentMap) return;
            if (!PhotoCapture.HideGuides) fixesNormalPreviewCalls++;
            PlaceWorker_SpectatorPreview.DrawSpectatorPreview(ThingDef.Named("ChristmasTree"), fixesSpot.Position, Rot4.North, Color.yellow, false, out _);
        }

        private static void FixCheck(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("playtest fixes: " + label);
            checks.Add("PASS: " + label);
        }
        private static bool FixesRender(ref PhotoCapture.Result __result)
        {
            if (!fixesFailRender) return true;
            fixesFailRender = false; fixesFailures++; __result = null; return false;
        }
        private static void FixReady(Pawn p)
        {
            foreach (var h in p.health.hediffSet.hediffs.ToList()) p.health.RemoveHediff(h);
            if (p.Drafted) p.drafter.Drafted = false;
            if (p.needs.food != null) p.needs.food.CurLevel = .95f;
            if (p.needs.rest != null) p.needs.rest.CurLevel = .95f;
            if (p.needs.joy != null) p.needs.joy.CurLevel = .8f;
            p.jobs?.EndCurrentJob(JobCondition.InterruptForced, false);
            p.pather.StopDead();
            p.Drawer.tweener.ResetTweenedPosToRoot();
        }
        private static void FixPose(LordJob_MFGroupPhoto job)
        {
            foreach (var p in job.Members)
            {
                p.Position = job.CellFor(p); p.Notify_Teleported(true, true); p.pather.StopDead(); p.Drawer.tweener.ResetTweenedPosToRoot();
            }
            for (int i = 0; i < 130; i++) Find.TickManager.DoSingleTick();
        }
        private static void FixSetup()
        {
            map = Find.CurrentMap; center = map.Center; Find.TickManager.Pause(); CloseLog(); LanguageFilesParse();
            Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear(); ClearHostiles(map); map.fogGrid.ClearAllFog();
            for (int x = center.x - 30; x <= center.x + 30; x++)
                for (int z = center.z - 30; z <= center.z + 30; z++)
                {
                    var cell = new IntVec3(x, 0, z);
                    foreach (var thing in cell.GetThingList(map).ToList()) if (!(thing is Pawn) && thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil); map.roofGrid.SetRoof(cell, null);
                }
            int n = 0;
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                pawn.Position = center + new IntVec3(n++ * 2 - 2, 0, -2); pawn.Notify_Teleported(true, true);
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms(); map.mapDrawer.RegenerateEverythingNow();
            Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 16f);
        }
        private static void PlaytestFixesStep()
        {
            var store = PhotoStore.Instance;
            switch (phase)
            {
                case 0:
                    FxSet(false); FixSetup();
                    MomentFramesMod.Settings.msColonyStart = false;
                    MomentFramesMod.Settings.msMechlink = false;
                    MomentFramesMod.Settings.msHistoric = false;
                    MomentFramesMod.Settings.msPersonal = false;
                    MomentFramesMod.Settings.msRareAnimal = false;
                    MomentFramesMod.Settings.msAnomaly = false;
                    MomentFramesMod.Settings.msEnding = false;
                    MomentFramesMod.Settings.yearLetter = false;
                    MomentFramesMod.Settings.battleMode = Battles.ModeEvery;
                    fixesPeople.AddRange(map.mapPawns.FreeColonistsSpawned);
                    while (fixesPeople.Count < 13)
                    {
                        var p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                        GenSpawn.Spawn(p, center + new IntVec3(fixesPeople.Count - 6, 0, -7), map); fixesPeople.Add(p);
                    }
                    foreach (var p in fixesPeople) FixReady(p);
                    p1 = fixesPeople[0]; p2 = fixesPeople[1]; fixesSubjectId = p1.GetUniqueLoadID();
                    fixesSpot = (Building_PhotoSpot)ThingMaker.MakeThing(ThingDef.Named("MF_PhotoSpot"));
                    fixesSpot.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(fixesSpot, center + new IntVec3(0, 0, -5), map);
                    new Harmony("momentframes.probe.playtest-failure").Patch(AccessTools.Method(typeof(PhotoCapture), "Render"),
                        prefix: new HarmonyMethod(typeof(Probe), nameof(FixesRender)));
                    new Harmony("momentframes.probe.preview").Patch(AccessTools.Method(typeof(Map), nameof(Map.MapUpdate)), postfix: new HarmonyMethod(typeof(Probe), nameof(FixesPreview)));
                    FixTextAndBounds();
                    FixCheck(Birthday.StartPrepared(p1, fixesAge, fixesSpot.Position, fixesPeople.Take(5).ToList(), Rot4.North, null), "five selected birthday attendees start: " + string.Join(";", fixesPeople.Take(5).Select(p => p.LabelShort + " " + GroupPhoto.WhyNot(p))) + " danger=" + map.dangerWatcher.DangerRating);
                    fixesJob = Birthday.Active(p1);
                    FixCheck(fixesJob != null && fixesJob.Members.Count == 5, "birthday roster matches selection");
                    Birthday.OpenSetup(p1, fixesAge, fixesJob);
                    FixPose(fixesJob);
                    FixCheck(!fixesJob.Shooting && fixesJob.Takes == 0 && !store.HasKey(Birthday.Key(p1, fixesAge))
                        && fixesJob.EditingBirthday && Find.WindowStack.IsOpen<Dialog_BirthdaySetup>(), "ready birthday waits while the real roster editor is open");
                    FixCheck(fixesJob.ReplaceBirthdayRoster(fixesSpot.Position, fixesPeople.ToList(), GroupFormation.Cells(fixesSpot.Position, 13, map, Rot4.North), Rot4.North), "add guests during gathering");
                    FixCheck(fixesJob.Members.Count == 13, "thirteen guests selected without four-person or six-friend cap");
                    Find.WindowStack.TryRemove(typeof(Dialog_BirthdaySetup), true);
                    fixesJob.ShootNow(); for (int i = 0; i < 30; i++) Find.TickManager.DoSingleTick();
                    FixCheck(!fixesJob.Shooting && !store.HasKey(Birthday.Key(p1, fixesAge)), "new arrivals hold the shutter");
                    fixesJob.Leave(p1); fixesJob.ShootNow(); for (int i = 0; i < 30; i++) Find.TickManager.DoSingleTick();
                    FixCheck(!store.HasKey(Birthday.Key(p1, fixesAge)), "birthday subject absent cannot consume birthday or capture friends alone");
                    FixReady(p1);
                    FixCheck(fixesJob.ReplaceBirthdayRoster(fixesSpot.Position, fixesPeople.ToList(), GroupFormation.Cells(fixesSpot.Position, 13, map), Rot4.North), "missing subject can be re-added");
                    Birthday.OpenSetup(p1, fixesAge, fixesJob);
                    FixPose(fixesJob); Birthday.SendLetter(p1, fixesAge);
                    Wait(.7f); phase = 1; return;
                case 1:
                    CloseLog(); Shot("birthday-guests-13.png"); Wait(.4f); phase = 2; return;
                case 2:
                    Require("birthday-guests-13.png"); Find.WindowStack.TryRemove(typeof(Dialog_BirthdaySetup), true);
                    foreach (var p in fixesPeople) FixReady(p);
                    fixesFailRender = true;
                    FixPose(fixesJob);
                    for (int i = 0; i < 30; i++) { Find.TickManager.DoSingleTick(); if (fixesJob.Shooting) break; }
                    FixCheck(fixesJob.Shooting, "ready birthday automatically starts a real capture after the editor closes: complete=" + fixesJob.Complete + " inPlace=" + fixesJob.InPlace + " roster=" + fixesJob.Roster + " editing=" + fixesJob.EditingBirthday); phase = 3; return;
                case 3:
                    if (PhotoCapture.Busy || fixesJob.Shooting) return;
                    FixCheck(fixesFailures == 1 && fixesJob.Takes == 0 && !fixesJob.Holding, "injected camera failure does not count as a successful take");
                    FixCheck(Birthday.Possible(p1, fixesAge, out _) && Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_MFBirthday>().Any(l => l.pawn == p1), "capture failure preserves birthday opportunity and letter");
                    phase = 31; return;
                case 31:
                    if (PhotoCapture.Busy) return;
                    FixPose(fixesJob);
                    for (int i = 0; i < 30; i++) { Find.TickManager.DoSingleTick(); if (fixesJob.Shooting) break; }
                    if (fixesJob.Shooting) phase = 4;
                    return;
                case 4:
                    if (PhotoCapture.Busy || fixesJob.Shooting) return;
                    fixesPhoto = store.Get(fixesJob.PhotoId);
                    FixCheck(fixesPhoto?.HasFile == true && fixesPhoto.people.Count == 13 && fixesPhoto.Has(p1), "retry saves all thirteen people and the subject: file=" + fixesPhoto?.file + " people=" + fixesPhoto?.people.Count + " state=" + fixesJob.Result);
                    FixCheck(fixesJob.Finished && fixesJob.Result == LordJob_MFGroupPhoto.Outcome.Shot && fixesJob.Takes == 1, "birthday ends automatically after successful file save");
                    for (int i = 0; i < 30; i++) Find.TickManager.DoSingleTick();
                    FixCheck(fixesPeople.All(p => p.GetLord()?.LordJob != fixesJob && p.mindState.duty?.def != MFDefs.PoseDuty), "birthday participants return to ordinary jobs");
                    FixCheck(!Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_MFBirthday>().Any(l => l.pawn == p1 && l.age == fixesAge), "successful birthday resolves the live letter");
                    File.Copy(store.FilePath(fixesPhoto), Path.Combine(dir, "birthday-13.png"), true);
                    foreach (var p in fixesPeople) FixReady(p);
                    FixCheck(Birthday.StartPrepared(p1, fixesAge, fixesSpot.Position, fixesPeople.Take(5).ToList(), Rot4.East, null), "completed birthday allows an explicit new retake");
                    fixesJob = Birthday.Active(p1); FixPose(fixesJob);
                    for (int i = 0; i < 30; i++) { Find.TickManager.DoSingleTick(); if (fixesJob.Shooting) break; }
                    phase = 5; return;
                case 5:
                    if (PhotoCapture.Busy || fixesJob.Shooting) return;
                    var retake = store.Get(fixesJob.PhotoId);
                    FixCheck(retake?.HasFile == true && retake.reshootOf == fixesPhoto.id && store.Get(fixesPhoto.id)?.HasFile == true, "retake links to the original and preserves it");
                    for (int i = 0; i < 30; i++) Find.TickManager.DoSingleTick();
                    foreach (var p in fixesPeople) FixReady(p);
                    FixCheck(Birthday.StartPrepared(p1, fixesAge + 1, fixesSpot.Position, fixesPeople.Take(5).ToList(), Rot4.North, null), "new birthday may be cancelled before shooting");
                    fixesJob = Birthday.Active(p1); Birthday.SendLetter(p1, fixesAge + 1); fixesJob.Cancel();
                    for (int i = 0; i < 30; i++) Find.TickManager.DoSingleTick();
                    FixCheck(Birthday.Possible(p1, fixesAge + 1, out _) && Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_MFBirthday>().Any(l => l.age == fixesAge + 1), "cancel keeps the birthday letter reusable");
                    FixChildAndAdoption();
                    phase = 6; Wait(.5f); return;
                case 6:
                    FixCheck(!fixesChild.Awake() && !fixesChild.Downed && GroupPhoto.CanJoin(fixesChild)
                        && GroupPhoto.WhyNot(fixesChild).NullOrEmpty(), "healthy sleeping child is callable without a false downed or unavailable warning");
                    foreach (var p in fixesPeople.Take(5)) FixReady(p);
                    FixCheck(Birthday.StartPrepared(fixesChild, 4, fixesSpot.Position, new[] { fixesChild }.Concat(fixesPeople.Take(5)).ToList(), Rot4.South, null), "five guests and the selected sleeping child are summoned to the photo spot");
                    fixesJob = Birthday.Active(fixesChild);
                    FixCheck(!fixesJob.FixedBirthdaySubject && fixesJob.Members.Count == 6 && fixesJob.Members.Contains(fixesChild),
                        "sleeping child receives a real walking duty as the sixth selected person");
                    Birthday.OpenSetup(fixesChild, 4, fixesJob); fixesGatherTicks = 0; phase = 60; return;
                case 60:
                    if (!FixGatherUntil(() => fixesJob.InPlace == 6 && fixesChild.Awake())) return;
                    FixCheck(fixesChild.Position == fixesJob.CellFor(fixesChild) && fixesJob.Takes == 0 && fixesJob.EditingBirthday,
                        "sleeping child wakes and reaches the assigned photo cell naturally while editing holds the shutter");
                    foreach (var p in fixesJob.Members) p.Drawer.tweener.ResetTweenedPosToRoot();
                    Birthday.SendLetter(fixesChild, 4); Find.WindowStack.TryRemove(typeof(Dialog_BirthdaySetup), true); phase = 61; return;
                case 61:
                    if (!FixGatherUntil(() => fixesJob.Shooting)) return;
                    FixCheck(fixesJob.Shooting, "ready child birthday starts automatically when the editor closes");
                    GameDataSaveLoader.SaveGame("mfplaytest-pending"); phase = 7; return;
                case 7:
                    if (PhotoCapture.Busy) return;
                    GameDataSaveLoader.LoadGame("mfplaytest-pending"); Wait(1f); phase = 8; return;
                case 8:
                    map = Find.CurrentMap; store = PhotoStore.Instance;
                    fixesChild = map.mapPawns.FreeColonistsSpawned.First(p => p.GetUniqueLoadID() == fixesChild.GetUniqueLoadID());
                    fixesJob = Birthday.Active(fixesChild);
                    FixCheck(fixesJob != null && !fixesJob.Shooting && !fixesJob.Holding && fixesJob.Takes == 0, "load during capture does not invent a successful birthday take");
                    FixCheck(!store.HasKey(Birthday.Key(fixesChild, 4)), "interrupted birthday request released after load for automatic retry");
                    fixesGatherTicks = 0; phase = 81; return;
                case 81:
                    if (!FixGatherUntil(() => fixesJob.Shooting)) return;
                    phase = 9; return;
                case 9:
                    if (PhotoCapture.Busy || fixesJob.Shooting) return;
                    var childPhoto = store.Get(fixesJob.PhotoId);
                    FixCheck(childPhoto?.HasFile == true && childPhoto.Has(fixesChild) && childPhoto.people.Count >= 6 && fixesJob.Finished,
                        "loaded birthday of the woken child automatically saves subject plus five guests and ends");
                    File.Copy(store.FilePath(childPhoto), Path.Combine(dir, "birthday-sleeping-child.png"), true);
                    for (int i = 0; i < 30; i++) Find.TickManager.DoSingleTick();
                    FixBattles();
                    phase = 10; return;
                case 10:
                    if (PhotoCapture.Busy) return;
                    if (store.Records.Any(r => !r.HasFile && !r.eventOnly)) return;
                    FixCheck(store.Get(fixesEventId)?.eventOnly == true, "battle without remains stays a text record");
                    fixesSpot = map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSpot>().First();
                    p1 = map.mapPawns.FreeColonistsSpawned.First(p => p.GetUniqueLoadID() == fixesSubjectId);
                    Find.Selector.ClearSelection(); Find.Selector.Select(fixesSpot, false);
                    fixesGuideBefore = Building_PhotoSpot.HiddenInCaptures;
                    fixesDrawPreview = true;
                    fixesPhoto = store.Request(map, fixesSpot.Position, "manual", "probe-clean-guides", "guide suppression", "", new[] { p1 }, 13f);
                    phase = 11; return;
                case 11:
                    if (!fixesPhoto.HasFile) return;
                    FixCheck(Building_PhotoSpot.HiddenInCaptures > fixesGuideBefore && CaptureSelectionGuides.Suppressed > 0 && !PhotoCapture.HideGuides,
                        "real capture skips spot graphic and selection overlays, then restores normal guide rendering: spot=" + Building_PhotoSpot.HiddenInCaptures + ", before=" + fixesGuideBefore + ", selection=" + CaptureSelectionGuides.Suppressed + ", hide=" + PhotoCapture.HideGuides);
                    FixCheck(CaptureSpectatorGuides.Suppressed > 0 && fixesNormalPreviewCalls > 0, "real spectator preview renders normally and is suppressed during photo frame");
                    fixesDrawPreview = false;
                    File.Copy(store.FilePath(fixesPhoto), Path.Combine(dir, "clean-guides.png"), true);
                    Find.Selector.ClearSelection();
                    MomentFramesMod.Settings.albumFont = "pencil"; MomentFramesMod.Settings.albumTextSize = 130;
                    MainTabWindow_Album.Open(fixesPhoto);
                    Find.WindowStack.Add(new FixesUiMeasure()); phase = 12; return;
                case 12:
                    if (!fixesUiMeasured) return;
                    if (fixesUiError != null) throw new InvalidOperationException("playtest fixes: " + fixesUiError.Message);
                    Find.WindowStack.TryRemove(typeof(FixesUiMeasure), false); Wait(.5f); phase = 13; return;
                case 13:
                    CloseLog(); Shot("album-large-tabs.png"); Wait(.4f); phase = 14; return;
                case 14:
                    Require("album-large-tabs.png"); Find.MainTabsRoot.EscapeCurrentTab(false);
                    var albumDef = DefDatabase<MainButtonDef>.GetNamed("MF_Album");
                    Find.MainTabsRoot.SetCurrentTab(albumDef); ((MainTabWindow_Album)albumDef.TabWindow).ShowChronicle(true);
                    Wait(.5f); phase = 15; return;
                case 15:
                    CloseLog(); Shot("chronicle-event-only.png"); Wait(.4f); phase = 16; return;
                case 16:
                    Require("chronicle-event-only.png"); Find.MainTabsRoot.EscapeCurrentTab(false);
                    fixesSavedCaption = store.Records.First(r => r.HasFile).desc;
                    GameDataSaveLoader.SaveGame("mfplaytest-final"); GameDataSaveLoader.LoadGame("mfplaytest-final"); Wait(1f); phase = 17; return;
                case 17:
                    FixCheck(PhotoStore.Instance.Records.First(r => r.HasFile).desc == fixesSavedCaption, "real save/load preserves chosen description text");
                    FixCheck(PhotoStore.Instance.Get(fixesEventId)?.eventOnly == true && !PhotoStore.Instance.Get(fixesEventId).HasFile, "event-only entry survives load without a fabricated photo");
                    File.WriteAllText(Path.Combine(dir, "playtest-fixes-result.json"), "{\"ok\":true,\"checks\":[" + string.Join(",", checks.Select(Json))
                        + "],\"limits\":\"Disposable Core, Harmony and official DLC fixtures; natural player session and full modpack are not tested.\"}");
                    active = false; Application.Quit(); return;
            }
        }

        private static void FixTextAndBounds()
        {
            FixCheck(CaptionText.Clean("(*Name)셀리안(/Name) 입양함") == "셀리안 입양함", "game Name markers resolved for display");
            FixCheck(CaptionText.Clean("적을(를) 모르나이(가) 셀레스테은(는)") == "적을 모르나가 셀레스테는", "Korean final-consonant particles resolved");
            var old = new PhotoRecord { desc = "(*Name)셀리안(/Name) 입양함", title = "적을(를) 막아낸 날" };
            FixCheck(old.DisplayDesc == "셀리안 입양함" && old.DisplayTitle == "적을 막아낸 날" && old.desc.Contains("(*Name)"), "legacy display is cleaned while original text is preserved");
            foreach (var c in new[] { new Vector3(.5f, 0f, .5f), new Vector3(map.Size.x - .5f, 0f, map.Size.z - .5f) })
            {
                var center = c; float span = 19f, aspect = 1.5f; PhotoCapture.KeepInside(map, ref center, ref span, ref aspect);
                FixCheck(center.x - span * aspect / 2f >= 0f && center.z - span / 2f >= 0f && center.x + span * aspect / 2f <= map.Size.x && center.z + span / 2f <= map.Size.z, "corner framing stays wholly inside map");
            }
            foreach (var kind in new[] { "colonystart", "mechlink", "ending", "adoption", "babyarrival" })
                FixCheck(!PhotoDesc.Pick(kind, new[] { p1 }, map).NullOrEmpty(), kind + " has a local sentence bank");
            var bag = new DescriptionHistory(); var keys = Enumerable.Range(1, 24).Select(i => "MF_Caption_fixture_" + i).ToList();
            var chosen = Enumerable.Range(0, 48).Select(i => bag.Pick("MF_Caption_fixture", keys, n => 0)).ToList();
            FixCheck(chosen.Take(24).Distinct().Count() == 24 && chosen.Skip(24).Distinct().Count() == 24, "caption bag exhausts candidates before refilling");
            var first = bag.Pick("MF_Caption_one", keys, n => 0); var second = bag.Pick("MF_Caption_two", keys, n => 0);
            FixCheck(first != second, "different event kinds avoid the recently used sentence form");
            var north = GroupFormation.Cells(fixesSpot.Position, 5, map, Rot4.North);
            var south = GroupFormation.Cells(fixesSpot.Position, 5, map, Rot4.South);
            FixCheck(north.Count == 5 && south.Count == 5 && north.All(c => c.z > fixesSpot.Position.z) && south.All(c => c.z < fixesSpot.Position.z), "formation direction changes actual standing cells");
            FixCheck(!Historic.ArrivalPending(map), "completed quick-test landing has no held arrival pawn");
            FixCheck(PhotoStore.Instance.InitialColonists.Count >= 2 && PhotoStore.Instance.InitialColonists.All(p => p.Spawned && p.Map == map), "scenario records selected initial colonists and their completed landing");
            var info = new ActiveTransporterInfo();
            var newcomer = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer); info.innerContainer.TryAdd(newcomer);
            var landingCell = center + new IntVec3(16, 0, -8);
            DropPodUtility.MakeDropPodAt(landingCell, map, info, Faction.OfPlayer);
            var incoming = landingCell.GetFirstThing<DropPodIncoming>(map);
            FixCheck(Historic.ArrivalPending(map), "colony-start guard detects a colonist still in an incoming pod");
            incoming.Destroy(DestroyMode.Vanish);
        }

        private static void FixChildAndAdoption()
        {
            fixesChild = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                forceGenerateNewPawn: true, fixedBiologicalAge: 4f, fixedChronologicalAge: 4f, developmentalStages: DevelopmentalStage.Child));
            var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
            bed.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bed, center + new IntVec3(-12, 0, -5), map);
            GenSpawn.Spawn(fixesChild, bed.Position, map); fixesChild.needs.rest.CurLevel = .6f;
            bed.CompAssignableToPawn.TryAssignPawn(fixesChild);
            fixesChild.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, bed), JobCondition.InterruptForced);
            for (int i = 0; i < 100; i++) Find.TickManager.DoSingleTick();
            var infant = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, null,
                forceGenerateNewPawn: true, fixedBiologicalAge: .2f, fixedChronologicalAge: .2f, allowDowned: true, developmentalStages: DevelopmentalStage.Baby));
            GenSpawn.Spawn(infant, center + new IntVec3(-10, 0, -8), map);
            new Designator_Adopt().DesignateThing(infant);
            var store = PhotoStore.Instance;
            var photo = store.Records.FirstOrDefault(r => r.eventKey == "baby:" + infant.GetUniqueLoadID());
            FixCheck(photo != null && photo.kind == "adoption" && photo.title == "MF_Adoption".Translate(infant.LabelShort).ToString(), "real Adopt action uses adoption title and kind rather than birth");
            FixCheck(store.LifeEvents.Any(e => e.kind == "adopted" && e.pawnIds.Contains(infant.GetUniqueLoadID())) && !store.LifeEvents.Any(e => e.kind == LifeEvents.Born && e.pawnIds.Contains(infant.GetUniqueLoadID())), "adopted infant has adoption life entry and no false born entry");
            FixCheck(BirthSources.Adopting == null && BirthSources.BirthDepth == 0, "event-source scope restored after action");
            var vat = (Building_GrowthVat)ThingMaker.MakeThing(ThingDefOf.GrowthVat);
            vat.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(vat, center + new IntVec3(18, 0, -16), map);
            var mother = fixesPeople.First(p => p.gender == Gender.Female);
            var born = PregnancyUtility.ApplyBirthOutcome(new RitualOutcomePossibility { positivityIndex = 2 }, 1f, null,
                new List<GeneDef>(), mother, vat, preventLetter: true) as Pawn;
            FixCheck(born != null && !born.Dead && store.LifeEvents.Any(e => e.kind == LifeEvents.Born && e.pawnIds.Contains(born.GetUniqueLoadID())), "actual birth without a letter records born from the source event");
            FixCheck(store.Records.Any(r => r.kind == "baby" && r.eventKey == "baby:" + born.GetUniqueLoadID()) && BirthSources.BirthDepth == 0,
                "actual birth photo and source scope survive a suppressed birth letter");
        }

        private static void FixBattles()
        {
            map = Find.CurrentMap; center = map.Center;
            var store = PhotoStore.Instance;
            var f = new Battles.Fight { map = map, type = "raid", faction = "enemy", startTick = Find.TickManager.TicksGame - 30 };
            var empty = Battles.Finish(store, f, Find.TickManager.TicksGame);
            fixesEventId = empty.id;
            FixCheck(empty.eventOnly && empty.people.Count == 0 && !empty.HasFile, "no remains means an event record, not an empty landscape with nearest names");
            var enemyFaction = Find.FactionManager.AllFactions.First(x => x.HostileTo(Faction.OfPlayer));
            var cluster = new Battles.Fight { map = map, type = "raid", faction = "enemy", startTick = Find.TickManager.TicksGame - 20 };
            foreach (var offset in new[] { new IntVec3(-16, 0, -10), new IntVec3(-15, 0, -10), new IntVec3(40, 0, 35) })
            {
                var enemy = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, enemyFaction);
                GenSpawn.Spawn(enemy, center + offset, map); cluster.enemies.Add(enemy); enemy.Kill(null);
            }
            var rec = Battles.Finish(store, cluster, Find.TickManager.TicksGame);
            FixCheck(rec.peopleIds.All(id => map.mapPawns.FreeColonistsSpawned.Any(p => p.GetUniqueLoadID() == id && PhotoCapture.InFrame(p, map, rec.center, rec.spanCells, rec.Aspect))), "battle names include only people in the actual frame");
            var disappearing = new Battles.Fight { map = map, type = "raid", startTick = Find.TickManager.TicksGame - 1 };
            var vanish = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, enemyFaction); GenSpawn.Spawn(vanish, center + new IntVec3(13, 0, -10), map); disappearing.enemies.Add(vanish); vanish.Kill(null);
            var delayed = Battles.Finish(store, disappearing, Find.TickManager.TicksGame); vanish.Corpse.Destroy(DestroyMode.Vanish);
            FixCheck(delayed != null, "pending aftermath fixture exists before remnants removed");
        }

        private static Exception fixesUiError;
        private sealed class FixesUiMeasure : Window
        {
            public FixesUiMeasure() { doCloseX = false; }
            public override void DoWindowContents(Rect r)
            {
                if (fixesUiMeasured) return;
                try
                {
                using (AlbumStyle.Begin())
                {
                    int font = AlbumStyle.StyleFor("연대기", GameFont.Small).fontSize;
                    FixCheck(MainTabWindow_Album.HeaderRowHeight >= font * 1.6f && font >= 25, "large pencil font has real header height instead of shrinking to eleven");
                    var album = new MainTabWindow_Album();
                    Traverse.Create(album).Field("filtersExpanded").SetValue(true);
                    FixCheck(album.HeaderHeight(600f) > album.HeaderHeight(1240f), "narrow album wraps filters onto another row");
                }
                }
                catch (Exception e) { fixesUiError = e; }
                fixesUiMeasured = true;
            }
        }
    }
}
