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
    /// <summary>Native, disposable-profile coverage for shared gathering/reunion/picker UI.</summary>
    public static partial class Probe
    {
        private static bool sharedPhotoUiMode;
        private static readonly List<Pawn> sharedPeople = new List<Pawn>();
        private static Dialog_GroupPhotoSetup sharedGroupDialog;
        private static Dialog_Reunion sharedReunionDialog;
        private static Dialog_ChoosePhoto sharedPickerDialog;
        private static LordJob_MFGroupPhoto sharedJob;
        private static PhotoRecord sharedOriginal, sharedReunionOriginal;
        private static readonly List<PhotoRecord> sharedPickerPhotos = new List<PhotoRecord>();
        private static List<Thing> sharedWalls;
        private static Pawn sharedBlocked, sharedBaby, sharedReunionUnavailable;
        private static IntVec3 sharedOldPosition;
        private static Building_Bed sharedBabyBed;
        private static int sharedTicks, sharedCallbackCount;
        private static string sharedPickedId, sharedOriginalSnapshot;
        private static string sharedOriginalId;
        private static List<string> sharedPersonIds;
        private static bool sharedUiMeasured;
        private static float sharedMeasureUntil;
        private static float sharedShotWait = -1f;
        private static bool sharedShotRetried;

        private static bool SharedRequire(string name)
        {
            var file = Path.Combine(dir, name);
            if (File.Exists(file) && new FileInfo(file).Length > 0)
            {
                sharedShotWait = -1f; sharedShotRetried = false; Require(name); return true;
            }
            if (sharedShotWait < 0f) sharedShotWait = Time.realtimeSinceStartup;
            float elapsed = Time.realtimeSinceStartup - sharedShotWait;
            if (elapsed > 15f) throw new TimeoutException("native screenshot did not finish: " + name);
            if (elapsed > 1f && !sharedShotRetried)
            {
                checks.Add("INFO: waiting for asynchronous native screenshot " + name);
                Shot(name); sharedShotRetried = true;
            }
            return false;
        }

        private static void SharedCheck(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("shared photo UI: " + label);
            checks.Add("PASS: " + label);
        }
        private static bool SharedSame(IEnumerable<Pawn> a, IEnumerable<Pawn> b) => new HashSet<Pawn>(a).SetEquals(b);
        private static void SharedAppearance(string theme = "graphite", string font = "pencil", int size = 130)
        {
            MomentFramesMod.Settings.albumTheme = theme;
            MomentFramesMod.Settings.albumFont = font;
            MomentFramesMod.Settings.albumTextSize = size;
        }
        private static void SharedCloseWindows()
        {
            Find.WindowStack.TryRemove(typeof(Dialog_GroupPhotoSetup), false);
            Find.WindowStack.TryRemove(typeof(Dialog_Reunion), false);
            Find.WindowStack.TryRemove(typeof(Dialog_ChoosePhoto), false);
        }
        private static void SharedShowGroup(bool narrow = false, bool dark = false)
        {
            SharedAppearance(dark ? "darkroom" : "graphite", dark ? "system" : "pencil", dark ? 100 : 130);
            sharedGroupDialog = new Dialog_GroupPhotoSetup(fixesSpot);
            Find.WindowStack.Add(sharedGroupDialog);
            if (narrow) sharedGroupDialog.windowRect = new Rect(380f, 20f, 520f, 750f);
        }
        private static void SharedShowReunion(bool narrow = false, bool dark = false)
        {
            SharedAppearance(dark ? "darkroom" : "graphite", dark ? "system" : "pencil", dark ? 100 : 130);
            sharedReunionDialog = new Dialog_Reunion(sharedReunionOriginal);
            Find.WindowStack.Add(sharedReunionDialog);
            if (narrow) sharedReunionDialog.windowRect = new Rect(380f, 20f, 520f, 750f);
        }
        private static void SharedShowPicker(bool narrow = false, bool dark = false)
        {
            SharedAppearance(dark ? "darkroom" : "graphite", dark ? "system" : "pencil", dark ? 100 : 130);
            sharedPickerDialog = new Dialog_ChoosePhoto("MF_AlbumTitle".Translate(), sharedPickerPhotos[0].id,
                id => { sharedPickedId = id; sharedCallbackCount++; }, sharedPickerPhotos.Select(r => r.id), false);
            Find.WindowStack.Add(sharedPickerDialog);
            if (narrow) sharedPickerDialog.windowRect = new Rect(380f, 20f, 520f, 750f);
        }
        private static LordJob_MFGroupPhoto SharedActiveGroup() => map.lordManager.lords.Select(l => l.LordJob)
            .OfType<LordJob_MFGroupPhoto>().FirstOrDefault(j => !j.Finished && !j.IsBirthday);
        private static bool SharedAdvance(Func<bool> until, int budget = 1)
        {
            for (int i = 0; i < budget && !until(); i++) { Find.TickManager.DoSingleTick(); sharedTicks++; }
            if (sharedTicks > 6000) throw new TimeoutException("shared gathering phase " + phase + " progress="
                + PhotoGatheringProgress.Summary(sharedJob) + " result=" + sharedJob?.Result);
            bool arrived = until();
            if (arrived) SettleDraw(); // Disposable paused fixture uses manual ticks; sync only its render interpolation.
            return arrived;
        }
        private static void SharedDisband()
        {
            sharedJob?.Cancel();
            for (int i = 0; i < 130; i++) Find.TickManager.DoSingleTick();
            foreach (var p in sharedPeople) FixReady(p);
        }
        private static void SharedSelectSpot()
        {
            Find.Selector.ClearSelection(); Find.Selector.Select(fixesSpot);
            Find.CameraDriver.SetRootPosAndSize(fixesSpot.DrawPos + new Vector3(0f, 0f, 1f), 11f);
        }
        private static void SharedCheckSelections()
        {
            SharedCheck(sharedGroupDialog.Chosen.Count == sharedPeople.Count, "ordinary gathering initially selects all available people");
            sharedGroupDialog.SelectGuests(2);
            SharedCheck(sharedGroupDialog.Chosen.Count == 0 && !sharedGroupDialog.CanStart, "empty ordinary draft cannot start a gathering");
            sharedGroupDialog.SelectGuests(0);
            SharedCheck(sharedGroupDialog.CanUndoSelection && sharedGroupDialog.UndoSelection() && sharedGroupDialog.Chosen.Count == 0,
                "ordinary select all has a one-step undo to an empty draft");
            SharedCheck(!sharedGroupDialog.CanUndoSelection && !sharedGroupDialog.UndoSelection(), "ordinary undo is consumed once");
            sharedGroupDialog.SelectGuests(0); sharedGroupDialog.SelectGuests(0);
            SharedCheck(sharedGroupDialog.UndoSelection() && sharedGroupDialog.Chosen.Count == 0, "no-op select all retains the previous ordinary undo point");
            sharedGroupDialog.SelectRandomGuests(6, new System.Random(9));
            SharedCheck(sharedGroupDialog.UndoSelection() && sharedGroupDialog.Chosen.Count == 0, "ordinary random selection is undoable");
            var draws = new HashSet<string>();
            for (int seed = 0; seed < 100; seed++)
            {
                sharedGroupDialog.SelectRandomGuests(6, new System.Random(seed));
                var chosen = sharedGroupDialog.Chosen;
                if (chosen.Count != 6 || chosen.Distinct().Count() != 6 || chosen.Any(p => !sharedPeople.Contains(p)))
                    throw new InvalidOperationException("ordinary random seed " + seed);
                draws.Add(string.Join(",", chosen.Select(p => p.thingIDNumber).OrderBy(id => id)));
            }
            SharedCheck(draws.Count > 1, "100 ordinary random draws preserve unique available people, exact total and varied subsets");
            sharedGroupDialog.SelectGuests(2); sharedGroupDialog.SelectGuests(0);
            SharedCheck(sharedGroupDialog.ToggleGuest(sharedPeople[0]) && sharedGroupDialog.UndoSelection() && sharedGroupDialog.Chosen.Count == 0,
                "ordinary row toggle does not overwrite the bulk-selection undo point");
            sharedPeople[0].drafter.Drafted = true;
            sharedGroupDialog.SelectGuests(0);
            SharedCheck(!sharedGroupDialog.Chosen.Contains(sharedPeople[0]) && !sharedGroupDialog.ToggleGuest(sharedPeople[0]),
                "ordinary select all and row toggle both exclude a drafted pawn");
            sharedGroupDialog.SelectRandomGuests(999, new System.Random(2));
            SharedCheck(sharedGroupDialog.Chosen.Count == sharedPeople.Count - 1 && !sharedGroupDialog.Chosen.Contains(sharedPeople[0]),
                "ordinary random total clamps to the currently available roster");
            sharedPeople[0].drafter.Drafted = false; FixReady(sharedPeople[0]);
            sharedGroupDialog.SelectGuests(0); var draft = sharedGroupDialog.Chosen.ToList();
            sharedGroupDialog.ExpandedSettings = true;
            SharedCheck(SharedSame(draft, sharedGroupDialog.Chosen), "expanding ordinary settings preserves the roster");
            sharedGroupDialog.ExpandedSettings = false;
            foreach (var facing in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            {
                sharedGroupDialog.SetDirection(facing);
                var plan = PhotoGatheringPlan.Create(map, fixesSpot.Position, sharedGroupDialog.Chosen, facing);
                SharedCheck(plan.CanStart && plan.Cells.SequenceEqual(GroupFormation.Cells(fixesSpot.Position, draft.Count, map, facing)),
                    "ordinary preflight uses the requested formation direction " + facing);
            }
            sharedGroupDialog.SetDirection(Rot4.North);
            sharedPeople[0].drafter.Drafted = true;
            var stale = PhotoGatheringPlan.Create(map, fixesSpot.Position, draft, Rot4.North);
            int lordCount = map.lordManager.lords.Count;
            SharedCheck(!stale.CanStart && stale.Problems.ContainsKey(sharedPeople[0]) && stale.Reason.Contains(sharedPeople[0].LabelShort)
                && GroupPhoto.StartPrepared(map, fixesSpot.Position, draft, Rot4.North) == null && map.lordManager.lords.Count == lordCount,
                "ordinary final start rechecks a selected pawn who became unavailable and creates no lord");
            sharedPeople[0].drafter.Drafted = false; FixReady(sharedPeople[0]);
        }
        private static void SharedCheckCapacity()
        {
            var walls = new List<IntVec3>();
            for (int z = 1; z <= GroupFormation.MaxRows; z++) for (int x = -1; x <= 1; x++)
                if (z != 1 || x == 0) walls.Add(fixesSpot.Position + new IntVec3(x, 0, z));
            sharedWalls = PreparationBuildWalls(walls);
            var guests = sharedPeople.Take(3).ToList();
            var plan = PhotoGatheringPlan.Create(map, fixesSpot.Position, guests, Rot4.North);
            SharedCheck(!plan.CanStart && plan.Moving.Count == 3 && plan.Cells.Count == 2
                && plan.Reason == "MF_PreparationSpaceProblem".Translate(3, 2, 3).ToString(),
                "ordinary native obstacles report only two usable standing places for three selected people");
            int lords = map.lordManager.lords.Count;
            SharedCheck(GroupPhoto.StartPrepared(map, fixesSpot.Position, guests, Rot4.North) == null && map.lordManager.lords.Count == lords,
                "ordinary final click rejects insufficient native formation cells without creating a lord");
            PreparationRemoveWalls(sharedWalls);
            SharedCheck(PhotoGatheringPlan.Create(map, fixesSpot.Position, guests, Rot4.North).CanStart,
                "ordinary formation preflight becomes ready after obstacles are removed");
        }
        private static void SharedBeginReach()
        {
            sharedBlocked = sharedPeople[0]; sharedOldPosition = sharedBlocked.Position;
            sharedBlocked.Position = center + new IntVec3(-18, 0, -14); sharedBlocked.Notify_Teleported(true, true); FixReady(sharedBlocked);
            var cells = new List<IntVec3>();
            for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
                if (x != 0 || z != 0) cells.Add(sharedBlocked.Position + new IntVec3(x, 0, z));
            sharedWalls = PreparationBuildWalls(cells);
            var plan = PhotoGatheringPlan.Create(map, fixesSpot.Position, sharedPeople, Rot4.North);
            SharedCheck(!plan.CanStart && plan.Cells.Count == sharedPeople.Count
                && plan.ShortProblems.ContainsKey(sharedBlocked) && plan.Reason.Contains(sharedBlocked.LabelShort),
                "ordinary native reachability catches a walled-in selected pawn despite sufficient formation space");
            int lords = map.lordManager.lords.Count;
            SharedCheck(GroupPhoto.StartPrepared(map, fixesSpot.Position, sharedPeople, Rot4.North) == null && map.lordManager.lords.Count == lords,
                "ordinary final start refuses a native unreachable pawn without creating a lord");
        }
        private static void SharedEndReach()
        {
            PreparationRemoveWalls(sharedWalls);
            sharedBlocked.Position = sharedOldPosition; sharedBlocked.Notify_Teleported(true, true); FixReady(sharedBlocked);
            SharedCheck(PhotoGatheringPlan.Create(map, fixesSpot.Position, sharedPeople, Rot4.North).CanStart,
                "ordinary preflight clears the named route problem when the route opens");
        }
        private static string SharedSnapshot(PhotoRecord rec) => rec.id + "|" + rec.title + "|" + rec.desc + "|" + rec.file + "|"
            + rec.mapId + "|" + rec.center + "|" + rec.spanCells + "|" + rec.Aspect + "|" + string.Join(",", rec.peopleIds);
        private static PhotoRecord SharedClone(PhotoRecord source, string id, string title)
        {
            return new PhotoRecord { id = id, file = source.file, title = title, desc = source.desc, kind = source.kind,
                mapId = source.mapId, center = source.center, spanCells = source.spanCells, aspect = source.Aspect,
                ticksAbs = source.ticksAbs, dateLabel = source.dateLabel, dateSlug = source.dateSlug, year = source.year,
                people = source.people.ToList(), peopleIds = source.peopleIds.ToList() };
        }
        private static void SharedPrepareReunion()
        {
            sharedReunionOriginal = SharedClone(sharedOriginal, "shared-reunion-fixture", sharedOriginal.title);
            sharedReunionOriginal.people.Add("Former colony member"); sharedReunionOriginal.peopleIds.Add("Pawn_shared_missing_fixture");
            sharedOriginalSnapshot = SharedSnapshot(sharedReunionOriginal);
            sharedReunionUnavailable = sharedPeople.First(p => sharedOriginal.peopleIds.Contains(p.GetUniqueLoadID()));
            sharedReunionUnavailable.drafter.Drafted = true;
            checks.Add("INFO: reunion UI adds one missing historical person to a cloned record; original native photograph and metadata stay unchanged.");
        }
        private static void SharedCheckOrdinaryEditing()
        {
            var lord = GroupPhoto.StartPrepared(map, fixesSpot.Position, sharedPeople.Take(3).ToList(), Rot4.North);
            sharedJob = lord?.LordJob as LordJob_MFGroupPhoto;
            SharedCheck(sharedJob != null, "start ordinary three-person editable gathering");
            var editor = new Dialog_GroupPhotoSetup(fixesSpot, sharedJob); Find.WindowStack.Add(editor);
            SharedCheck(editor.Chosen.Count == 3 && editor.CanStart, "ordinary roster editor accepts its current lord members without busy false positives");
            var hungry = sharedJob.Members[0];
            var originalMembers = sharedJob.Members.ToList(); var originalCells = sharedJob.Cells.ToList();
            hungry.needs.food.CurLevel = 0f;
            SharedCheck(hungry.GetLord() == lord && (!GatheringsUtility.PawnCanStartOrContinueGathering(hungry)
                || !GatheringsUtility.ShouldGuestKeepAttendingGathering(hungry)),
                "zero food triggers a native gathering fitness rejection while the pawn still belongs to the edited lord");
            editor.SetDirection(Rot4.North); // Same direction invalidates the frame-cached UI plan for this same-frame fixture.
            var hungryPlan = PhotoGatheringPlan.Create(map, fixesSpot.Position, editor.Chosen, Rot4.North, sharedJob);
            SharedCheck(!hungryPlan.CanStart && hungryPlan.Problems.ContainsKey(hungry) && !editor.CanStart,
                "ordinary same-lord editing preflight rejects a starving selected member");
            int originalLordCount = map.lordManager.lords.Count;
            SharedCheck(!editor.StartSelected() && sharedJob.Members.SequenceEqual(originalMembers)
                && sharedJob.Cells.SequenceEqual(originalCells) && map.lordManager.lords.Count == originalLordCount,
                "ordinary same-lord final apply refuses a starving member without modifying the roster or creating a lord");
            FixReady(hungry); editor.SetDirection(Rot4.North);
            SharedCheck(editor.CanStart && PhotoGatheringPlan.Create(map, fixesSpot.Position, editor.Chosen, Rot4.North, sharedJob).CanStart,
                "restoring native food fitness clears the ordinary same-lord editing rejection");
            editor.SelectGuests(0); editor.SetDirection(Rot4.South);
            SharedCheck(editor.StartSelected() && SharedActiveGroup() == sharedJob && sharedJob.Members.Count == sharedPeople.Count
                && sharedJob.Cells.Distinct().Count() == sharedPeople.Count,
                "ordinary roster editing adds all available people and updates assigned cells in the same gathering");
            SharedCheck(editor.ToggleGuest(sharedPeople[0]) && editor.StartSelected() && sharedJob.Members.Count == sharedPeople.Count - 1
                && !sharedJob.Members.Contains(sharedPeople[0]), "ordinary roster editing removes a person without spawning a second lord");
            var leaving = sharedJob.Members.Skip(1).Take(2).ToList();
            var formerCells = leaving.ToDictionary(p => p, p => sharedJob.CellFor(p));
            var formerJobs = new Dictionary<Pawn, Job>();
            var formerDrivers = new Dictionary<Pawn, JobDriver>();
            foreach (var pawn in leaving)
            {
                var nativeGoto = JobMaker.MakeJob(JobDefOf.Goto, formerCells[pawn]);
                pawn.jobs.StartJob(nativeGoto, JobCondition.InterruptForced);
                formerJobs[pawn] = nativeGoto;
                formerDrivers[pawn] = pawn.jobs.curDriver;
            }
            SharedCheck(leaving.Count == 2 && leaving.All(p => p.GetLord() == lord && p.CurJob == formerJobs[p]
                && p.CurJobDef == JobDefOf.Goto && p.CurJob.targetA.Cell == formerCells[p]),
                "two edited participants have real native goto jobs targeting their respective original formation cells");
            foreach (var pawn in leaving) if (!editor.ToggleGuest(pawn)) throw new InvalidOperationException("ordinary removal fixture lost selected person");
            bool appliedRemoval = editor.StartSelected();
            bool removedAndStopped = appliedRemoval && sharedJob.Members.Count == sharedPeople.Count - 3
                && leaving.All(p => !lord.ownedPawns.Contains(p) && !sharedJob.Members.Contains(p) && p.jobs.curDriver != formerDrivers[p]
                    && !(p.CurJobDef == JobDefOf.Goto && p.CurJob.targetA.Cell == formerCells[p]));
            if (!removedAndStopped)
            {
                var removalPlan = PhotoGatheringPlan.Create(map, fixesSpot.Position, editor.Chosen, Rot4.South, sharedJob);
                checks.Add("INFO: removal diagnostic applied=" + appliedRemoval + " selected=" + editor.Chosen.Count
                    + " expected=" + (sharedPeople.Count - 3) + " members=" + sharedJob.Members.Count + " owned=" + lord.ownedPawns.Count
                    + " planCanStart=" + removalPlan.CanStart + " planReason=" + removalPlan.Reason
                    + " direction=" + sharedJob.FormationDirection + " activeSameJob=" + (SharedActiveGroup() == sharedJob));
                foreach (var pawn in leaving)
                    checks.Add("INFO: removal person " + pawn.LabelShort + " oldCell=" + formerCells[pawn] + " currentCellFor=" + sharedJob.CellFor(pawn)
                        + " selected=" + editor.Chosen.Contains(pawn) + " member=" + sharedJob.Members.Contains(pawn) + " owned=" + lord.ownedPawns.Contains(pawn)
                        + " currentLord=" + pawn.GetLord()?.LordJob?.GetType().Name + " sameLord=" + (pawn.GetLord() == lord)
                        + " job=" + pawn.CurJobDef?.defName + " jobTarget=" + pawn.CurJob?.targetA + " jobSameReference=" + ReferenceEquals(pawn.CurJob, formerJobs[pawn])
                        + " formerJobNowDef=" + formerJobs[pawn]?.def?.defName + " formerJobNowTarget=" + formerJobs[pawn]?.targetA
                        + " driverSameReference=" + ReferenceEquals(pawn.jobs.curDriver, formerDrivers[pawn])
                        + " duty=" + pawn.mindState.duty + " moving=" + pawn.pather.Moving + " drafted=" + pawn.Drafted);
            }
            SharedCheck(removedAndStopped,
                "removing two ordinary guests stops each old-cell goto job and releases both from the same gathering");
            SharedCloseWindows(); SharedDisband();
        }
        private static void SharedRefreshAfterLoad()
        {
            map = Find.CurrentMap;
            sharedPeople.Clear();
            foreach (var id in sharedPersonIds) sharedPeople.Add(map.mapPawns.FreeColonistsSpawned.First(p => p.GetUniqueLoadID() == id));
            fixesSpot = map.listerBuildings.AllBuildingsColonistOfClass<Building_PhotoSpot>().First();
            sharedJob = SharedActiveGroup(); sharedOriginal = PhotoStore.Instance.Get(sharedOriginalId);
            SharedCheck(sharedJob != null && sharedOriginal?.HasFile == true, "real save/load restores the ordinary gathering and its original photograph");
            SharedCheck(PhotoGatheringProgress.Total(sharedJob) == 6 && !PhotoGatheringProgress.Ready(sharedJob)
                && PhotoGatheringProgress.Details(sharedJob).Contains(sharedPeople[0].LabelShort),
                "real save/load preserves the full progress denominator and identifies a drafted original person before re-observation");
        }
        private static void SharedCheckReunion()
        {
            var candidates = sharedReunionDialog.CandidatesForDisplay.ToList();
            SharedCheck(candidates.Count == sharedReunionOriginal.peopleIds.Count
                && candidates.Any(c => !c.CanCome) && sharedReunionDialog.SelectedPawns.All(p => sharedReunionOriginal.peopleIds.Contains(p.GetUniqueLoadID())),
                "reunion lists original people including unavailable history and selects only original ready people");
            int ready = candidates.Count(c => c.CanCome);
            SharedCheck(sharedReunionDialog.SelectedCount == ready && ready == sharedOriginal.peopleIds.Count - 1,
                "reunion initial selection excludes a drafted original person and a missing historical person");
            int firstReady = candidates.FindIndex(c => c.CanCome), unavailable = candidates.FindIndex(c => !c.CanCome);
            sharedReunionDialog.ToggleCandidate(firstReady);
            int afterReadyToggle = sharedReunionDialog.SelectedCount;
            sharedReunionDialog.ToggleCandidate(unavailable);
            SharedCheck(firstReady >= 0 && afterReadyToggle == ready - 1 && sharedReunionDialog.SelectedCount == afterReadyToggle,
                "reunion checkbox toggles a ready original person and cannot add an unavailable person");
            var before = sharedReunionDialog.SelectedPawns.ToList();
            sharedReunionDialog.SelectAll(); sharedReunionDialog.SelectAll();
            SharedCheck(sharedReunionDialog.CanUndoSelection && sharedReunionDialog.UndoSelection()
                && SharedSame(before, sharedReunionDialog.SelectedPawns), "reunion select all undo restores the subset and survives a no-op repeat");
            SharedCheck(!sharedReunionDialog.UndoSelection(), "reunion undo is consumed once");
            sharedReunionDialog.SelectAll();
            var newlyBusy = sharedReunionDialog.SelectedPawns[0]; newlyBusy.drafter.Drafted = true;
            int lords = map.lordManager.lords.Count;
            SharedCheck(!sharedReunionDialog.CanStart && !sharedReunionDialog.StartSelected() && map.lordManager.lords.Count == lords,
                "reunion final start rechecks a selected person who became drafted and keeps the selection window open");
            newlyBusy.drafter.Drafted = false; FixReady(newlyBusy);
            var outsider = sharedPeople.First(p => !sharedReunionOriginal.peopleIds.Contains(p.GetUniqueLoadID()));
            SharedCheck(!Reunion.Start(sharedReunionOriginal, new List<Pawn> { outsider }) && map.lordManager.lords.Count == lords,
                "reunion final API refuses a person outside the original photograph");
            SharedCheck(SharedSnapshot(sharedReunionOriginal) == sharedOriginalSnapshot, "reunion selection and failed starts leave original metadata unchanged");
            sharedReunionUnavailable.drafter.Drafted = false; FixReady(sharedReunionUnavailable); sharedReunionDialog.SelectAll();
        }
        private static void SharedPreparePicker()
        {
            var store = PhotoStore.Instance;
            var fixtureRecords = store.Records as List<PhotoRecord>;
            if (fixtureRecords == null) throw new InvalidOperationException("probe registry fixture list unavailable");
            for (int i = 0; i < 42; i++)
            {
                var rec = SharedClone(sharedOriginal, "shared-picker-" + i.ToString("00"), "Shared photo " + i.ToString("00"));
                rec.desc = "Caption proof " + i.ToString("00"); rec.ticksAbs = sharedOriginal.ticksAbs + i;
                fixtureRecords.Add(rec); sharedPickerPhotos.Add(rec);
            }
            var edited = sharedPickerPhotos[2];
            edited.edits.Add(new PhotoEdit { id = "fixture-edit", file = edited.file, title = "Edited title proof", note = "Edited note proof" });
            edited.shownEdit = "fixture-edit";
            sharedPickerPhotos[0].ticksAbs = sharedOriginal.ticksAbs + 100;
            sharedPickerPhotos[1].title = "Shared photo 01 / the first winter gathering beside the old colony gardens remembered together";
            store.NotifyChanged();
            checks.Add("INFO: picker uses 42 cloned registry fixtures sharing one real native photograph, to exercise scroll virtualization and file-backed thumbnails.");
        }
        private static void SharedCheckPickerLayout(string label)
        {
            SharedCheck(sharedPickerDialog.DrawnColumns >= 1 && sharedPickerDialog.DrawnCardSize.x > 0f
                && sharedPickerDialog.DrawnColumns * (sharedPickerDialog.DrawnCardSize.x + 10f) - 10f <= sharedPickerDialog.DrawnGridArea.width,
                label + " cards stay within the native scroll viewport");
            SharedCheck(sharedPickerDialog.DrawnThumbnailCount > 0 && sharedPickerDialog.DrawnThumbnailCount < sharedPickerPhotos.Count,
                label + " loads only visible thumbnail rows from the long fixture album");
        }
        private static void SharedCheckPickerSearch()
        {
            SharedCheck(sharedPickerDialog.VisiblePhotos.Count == 42 && sharedPickerDialog.VisiblePhotos.All(r => sharedPickerPhotos.Contains(r)),
                "picker allowed IDs preserve the requested subset and exclude other real photographs");
            sharedPickerDialog.SetSearch("  SHARED PHOTO 07  ");
            SharedCheck(sharedPickerDialog.VisiblePhotos.Count == 1 && sharedPickerDialog.VisiblePhotos[0] == sharedPickerPhotos[7],
                "picker title search remains case-insensitive under the themed UI");
            var beforeAccept = sharedPickerDialog.VisiblePhotos.ToList();
            sharedPickerDialog.OnAcceptKeyPressed();
            SharedCheck(Find.WindowStack.Windows.Contains(sharedPickerDialog) && sharedPickerDialog.VisiblePhotos.SequenceEqual(beforeAccept)
                && sharedCallbackCount == 0 && sharedPickedId == null,
                "native picker accept key keeps the search window and results open without selecting a photograph");
            sharedPickerDialog.SetSearch("Caption proof 08");
            SharedCheck(sharedPickerDialog.VisiblePhotos.Count == 1 && sharedPickerDialog.VisiblePhotos[0] == sharedPickerPhotos[8], "picker searches original descriptions");
            sharedPickerDialog.SetSearch("Edited title proof");
            SharedCheck(sharedPickerDialog.VisiblePhotos.Count == 1 && sharedPickerDialog.VisiblePhotos[0] == sharedPickerPhotos[2], "picker searches the shown edited title");
            sharedPickerDialog.SetSearch("Edited note proof");
            SharedCheck(sharedPickerDialog.VisiblePhotos.Count == 1 && sharedPickerDialog.VisiblePhotos[0] == sharedPickerPhotos[2], "picker searches the shown edited description");
            sharedPickerDialog.SetSearch(sharedOriginal.people[0]);
            SharedCheck(sharedPickerDialog.VisiblePhotos.Count == 42, "picker searches remembered person names");
            sharedPickerDialog.SetSearch("not-a-real-picker-query-649");
            SharedCheck(sharedPickerDialog.VisiblePhotos.Count == 0, "picker reports no matches without selecting a photograph");
            sharedPickerDialog.SetSearch("Shared photo 07");
            SharedCheck(!sharedPickerDialog.SelectPhoto(sharedOriginal.id)
                && sharedCallbackCount == 0 && sharedPickedId == null, "picker refuses disallowed IDs without invoking the callback");
            SharedCheck(sharedPickerDialog.SelectPhoto(sharedPickerPhotos[7].id) && sharedPickedId == sharedPickerPhotos[7].id && sharedCallbackCount == 1,
                "picker invokes its selection callback once for a visible allowed photograph");
        }
        private static void SharedFinish()
        {
            File.WriteAllText(Path.Combine(dir, "shared-photo-ui-result.json"), "{\"ok\":true,\"checks\":[" + string.Join(",", checks.Select(Json))
                + "],\"limits\":\"Disposable native fixtures with 22C climate. Original user saves, natural gameplay and full modpack compatibility are untested. Picker pagination uses explicitly identified cloned registry fixtures. Baby progress screenshot injects the normal editing flag to hold the shutter until the cancellation check.\"}");
            active = false; Application.Quit();
        }
        private static void SharedPhotoUiStep()
        {
            switch (phase)
            {
                case 0:
                    FxSet(false); FixSetup(); Find.MainTabsRoot.EscapeCurrentTab(false); map.Biome.constantOutdoorTemperature = 22f;
                    MomentFramesMod.Settings.msColonyStart = false; MomentFramesMod.Settings.birthdayMode = Birthday.ModeOff;
                    sharedPeople.AddRange(map.mapPawns.FreeColonistsSpawned);
                    while (sharedPeople.Count < 9)
                    {
                        var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                        GenSpawn.Spawn(pawn, center + new IntVec3(-12, 0, sharedPeople.Count - 6), map); sharedPeople.Add(pawn);
                    }
                    foreach (var pawn in sharedPeople) FixReady(pawn);
                    fixesSpot = (Building_PhotoSpot)ThingMaker.MakeThing(ThingDef.Named("MF_PhotoSpot")); fixesSpot.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(fixesSpot, center + new IntVec3(8, 0, 0), map);
                    SharedShowGroup(); Find.WindowStack.Add(new SharedPhotoUiMeasure()); sharedMeasureUntil = Time.realtimeSinceStartup + 15f; Wait(.6f); phase = 1; return;
                case 1:
                    if (!sharedUiMeasured)
                    {
                        if (Time.realtimeSinceStartup > sharedMeasureUntil) throw new TimeoutException("native shared OnGUI measurement did not render");
                        Wait(.1f); return;
                    }
                    SharedCheck(sharedUiMeasured, "native shared OnGUI measurement completed"); Find.WindowStack.TryRemove(typeof(SharedPhotoUiMeasure), false);
                    CloseLog(); Shot("shared-group-graphite-130.png"); Wait(.4f); phase = 2; return;
                case 2:
                    if (!SharedRequire("shared-group-graphite-130.png")) return; SharedCheckSelections();
                    sharedGroupDialog.SelectGuests(2); sharedGroupDialog.SelectGuests(0);
                    CloseLog(); Shot("shared-group-undo.png"); Wait(.4f); phase = 3; return;
                case 3:
                    if (!SharedRequire("shared-group-undo.png")) return; SharedCloseWindows(); SharedCheckCapacity(); SharedBeginReach(); SharedShowGroup(); Wait(.5f); phase = 4; return;
                case 4:
                    CloseLog(); Shot("shared-group-blocked.png"); Wait(.4f); phase = 5; return;
                case 5:
                    if (!SharedRequire("shared-group-blocked.png")) return; SharedCloseWindows(); SharedEndReach(); SharedShowGroup(true); Wait(.5f); phase = 6; return;
                case 6:
                    CloseLog(); Shot("shared-group-narrow.png"); Wait(.4f); phase = 7; return;
                case 7:
                    if (!SharedRequire("shared-group-narrow.png")) return; SharedCloseWindows(); SharedShowGroup(false, true); Wait(.5f); phase = 8; return;
                case 8:
                    CloseLog(); Shot("shared-group-darkroom.png"); Wait(.4f); phase = 9; return;
                case 9:
                    if (!SharedRequire("shared-group-darkroom.png")) return; SharedCloseWindows(); SharedCheckOrdinaryEditing(); SharedShowGroup();
                    sharedGroupDialog.SelectRandomGuests(6, new System.Random(12)); sharedGroupDialog.SetDirection(Rot4.East);
                    SharedCheck(sharedGroupDialog.StartSelected(), "ordinary preparation starts the selected six-person gathering"); SharedCloseWindows(); sharedJob = SharedActiveGroup();
                    SharedCheck(sharedJob != null && !sharedJob.IsBirthday && sharedJob.Members.Count == 6 && sharedJob.FormationDirection == Rot4.East,
                        "ordinary gathering preserves selected roster and explicit direction without birthday behavior");
                    SharedCheck(PhotoGatheringProgress.Total(sharedJob) == 6 && PhotoGatheringProgress.Arrived(sharedJob) < 6 && !PhotoGatheringProgress.Ready(sharedJob),
                        "progress counts the full intended ordinary roster before arrivals");
                    SharedCheck(!PhotoGatheringProgress.PawnSummary(sharedJob.Members[0]).NullOrEmpty()
                        && PhotoGatheringProgress.PawnSummary(sharedPeople.First(p => !sharedJob.Members.Contains(p))).NullOrEmpty(),
                        "selected-participant progress appears only for active gathering members");
                    SharedSelectSpot(); Wait(.5f); phase = 10; return;
                case 10:
                    CloseLog(); Shot("shared-progress-arriving.png"); Wait(.4f); phase = 11; return;
                case 11:
                    if (!SharedRequire("shared-progress-arriving.png")) return; sharedTicks = 0; phase = 12; return;
                case 12:
                    if (!SharedAdvance(() => sharedJob.InPlace == sharedJob.Members.Count)) return;
                    SharedCheck(PhotoGatheringProgress.Total(sharedJob) == 6 && PhotoGatheringProgress.Arrived(sharedJob) == 6
                        && PhotoGatheringProgress.Ready(sharedJob), "progress reports all six native arrivals as ready");
                    SharedCheck(sharedJob.Members.All(p => p.Rotation == Rot4.West), "native ordinary pose faces opposite the selected gathering direction");
                    CloseLog(); Shot("shared-progress-ready.png"); Wait(.4f); phase = 13; return;
                case 13:
                    if (!SharedRequire("shared-progress-ready.png")) return; phase = 14; return;
                case 14:
                    if (!SharedAdvance(() => sharedJob.Shooting || sharedJob.Takes > 0, 20)) return;
                    phase = 15; return;
                case 15:
                    if (PhotoCapture.Busy || sharedJob.Shooting) return;
                    SharedCheck(sharedJob.Takes == 1 && sharedJob.Holding && !sharedJob.Finished,
                        "ordinary gathering still photographs automatically and holds the line after its first successful take");
                    sharedOriginal = PhotoStore.Instance.Get(sharedJob.PhotoId);
                    SharedCheck(sharedOriginal?.HasFile == true && File.Exists(PhotoStore.Instance.FilePath(sharedOriginal))
                        && sharedOriginal.kind == GroupPhoto.Kind && sharedOriginal.peopleIds.Count == 6,
                        "ordinary automatic capture stores the six selected people in a real PNG");
                    File.Copy(PhotoStore.Instance.FilePath(sharedOriginal), Path.Combine(dir, "shared-group-capture.png"), true);
                    sharedOriginalId = sharedOriginal.id; sharedPersonIds = sharedPeople.Select(p => p.GetUniqueLoadID()).ToList();
                    SharedDisband();
                    sharedJob = GroupPhoto.StartPrepared(map, fixesSpot.Position, sharedPeople.Take(6).ToList(), Rot4.North)?.LordJob as LordJob_MFGroupPhoto;
                    SharedCheck(sharedJob != null, "start ordinary dropout progress fixture");
                    sharedPeople[0].drafter.Drafted = true; sharedTicks = 0; phase = 16; return;
                case 16:
                    if (!SharedAdvance(() => sharedJob.Dropped.Contains(sharedPeople[0]), 1)) return;
                    SharedCheck(PhotoGatheringProgress.Total(sharedJob) == 6 && !PhotoGatheringProgress.Ready(sharedJob)
                        && PhotoGatheringProgress.Details(sharedJob).Contains(sharedPeople[0].LabelShort),
                        "native drafted dropout retains the original six-person denominator and names the missing person");
                    SharedSelectSpot(); CloseLog(); Shot("shared-progress-dropout.png"); Wait(.4f); phase = 17; return;
                case 17:
                    if (!SharedRequire("shared-progress-dropout.png")) return; GameDataSaveLoader.SaveGame("mf-shared-progress-dropout");
                    GameDataSaveLoader.LoadGame("mf-shared-progress-dropout"); Wait(1f); phase = 170; return;
                case 170:
                    SharedRefreshAfterLoad(); sharedPeople[0].drafter.Drafted = false; SharedDisband(); SharedPrepareReunion(); SharedShowReunion(); Wait(.6f); phase = 18; return;
                case 18:
                    CloseLog(); Shot("shared-reunion-graphite-130.png"); Wait(.4f); phase = 19; return;
                case 19:
                    if (!SharedRequire("shared-reunion-graphite-130.png")) return; SharedCheckReunion(); SharedCloseWindows(); SharedShowReunion(true); Wait(.5f); phase = 20; return;
                case 20:
                    CloseLog(); Shot("shared-reunion-narrow.png"); Wait(.4f); phase = 21; return;
                case 21:
                    if (!SharedRequire("shared-reunion-narrow.png")) return; SharedCloseWindows(); SharedShowReunion(false, true); Wait(.5f); phase = 22; return;
                case 22:
                    CloseLog(); Shot("shared-reunion-darkroom.png"); Wait(.4f); phase = 23; return;
                case 23:
                    if (!SharedRequire("shared-reunion-darkroom.png")) return;
                    SharedCheck(sharedReunionDialog.StartSelected(), "reunion preparation starts all currently ready original people"); sharedJob = SharedActiveGroup();
                    SharedCheck(sharedJob?.ReunionOf == sharedReunionOriginal.id && sharedJob.Members.Count == sharedOriginal.peopleIds.Count,
                        "reunion gathering preserves the original photo link and original available roster");
                    SharedCheck(SharedSnapshot(sharedReunionOriginal) == sharedOriginalSnapshot, "successful reunion start preserves original framing and metadata");
                    SharedCloseWindows();
                    var selectedParticipant = sharedJob.Members[0];
                    SharedCheck(sharedJob.Spotless && selectedParticipant.GetInspectString().StartsWith(PhotoGatheringProgress.Summary(sharedJob)),
                        "native pawn inspect string starts with gathering progress when the reunion has no photo spot");
                    Find.Selector.ClearSelection(); Find.Selector.Select(selectedParticipant);
                    Find.CameraDriver.SetRootPosAndSize(sharedJob.spot.ToVector3Shifted(), 11f);
                    CloseLog(); Shot("shared-progress-participant.png"); Wait(.4f); phase = 231; return;
                case 231:
                    if (!SharedRequire("shared-progress-participant.png")) return; SharedDisband(); SharedPreparePicker(); SharedShowPicker(); Wait(.6f); phase = 24; return;
                case 24:
                    SharedCheckPickerLayout("130% picker"); CloseLog(); Shot("shared-picker-graphite-130.png"); Wait(.4f); phase = 25; return;
                case 25:
                    if (!SharedRequire("shared-picker-graphite-130.png")) return; SharedCloseWindows(); SharedShowPicker(true); Wait(.6f); phase = 26; return;
                case 26:
                    SharedCheckPickerLayout("narrow 130% picker"); CloseLog(); Shot("shared-picker-narrow.png"); Wait(.4f); phase = 27; return;
                case 27:
                    if (!SharedRequire("shared-picker-narrow.png")) return; SharedCloseWindows(); SharedShowPicker(false, true); Wait(.6f); phase = 28; return;
                case 28:
                    SharedCheckPickerLayout("darkroom picker"); CloseLog(); Shot("shared-picker-darkroom.png"); Wait(.4f); phase = 29; return;
                case 29:
                    if (!SharedRequire("shared-picker-darkroom.png")) return; SharedCheckPickerSearch();
                    if (!ModsConfig.BiotechActive) { SharedFinish(); return; }
                    SharedBeginBabyProgress(); sharedTicks = 0; phase = 30; return;
                case 30:
                    if (!SharedAdvance(() => sharedJob.InPlace == sharedJob.Members.Count
                        && sharedJob.BirthdayGuardian.carryTracker.CarriedThing == sharedBaby, 30)) return;
                    SharedCheck(PhotoGatheringProgress.Total(sharedJob) == 4 && PhotoGatheringProgress.Arrived(sharedJob) == 4 && PhotoGatheringProgress.Ready(sharedJob),
                        "carried baby is counted as the fourth ready attendee without needing a standing cell");
                    SharedCheck(sharedJob.EditingBirthday && sharedJob.Takes == 0 && !sharedJob.Shooting,
                        "probe editing flag preserves the baby-ready progress screen before its cancellation check");
                    CloseLog(); Shot("shared-progress-baby-ready.png"); Wait(.4f); phase = 31; return;
                case 31:
                    if (!SharedRequire("shared-progress-baby-ready.png")) return;
                    sharedJob.EditingBirthday = false; sharedJob.Cancel(); sharedTicks = 0; phase = 32; return;
                case 32:
                    if (!SharedAdvance(() => sharedBaby.Spawned && sharedJob.BirthdayGuardian.carryTracker.CarriedThing == null, 30)) return;
                    SharedCheck(sharedBaby.Position == sharedBabyBed.Position, "baby progress fixture cancels and returns the baby to the assigned bed");
                    SharedFinish(); return;
            }
        }
        private static void SharedBeginBabyProgress()
        {
            foreach (var pawn in sharedPeople) FixReady(pawn);
            var guardian = sharedPeople[0];
            sharedBaby = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                forceGenerateNewPawn: true, fixedBiologicalAge: 1f, fixedChronologicalAge: 1f, allowDowned: true, developmentalStages: DevelopmentalStage.Baby));
            sharedBaby.relations.AddDirectRelation(PawnRelationDefOf.Parent, guardian);
            sharedBabyBed = (Building_Bed)ThingMaker.MakeThing(ThingDef.Named("BabySleepingSpot")); sharedBabyBed.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(sharedBabyBed, center + new IntVec3(-17, 0, -2), map); GenSpawn.Spawn(sharedBaby, sharedBabyBed.Position, map);
            sharedBabyBed.CompAssignableToPawn.TryAssignPawn(sharedBaby);
            sharedBaby.needs.food.CurLevel = .95f; sharedBaby.needs.rest.CurLevel = .9f;
            sharedBaby.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, sharedBabyBed), JobCondition.InterruptForced);
            var guests = new[] { sharedBaby }.Concat(sharedPeople.Take(3)).ToList();
            SharedCheck(Birthday.StartPrepared(sharedBaby, 1, fixesSpot.Position, guests, Rot4.North, null, guardian, true), "start baby progress fixture with three standing guests and baby");
            sharedJob = Birthday.Active(sharedBaby);
            // Fixture-only shutter hold for the ready-state screenshot; production auto-shoot is tested by the birthday/feedback suites.
            sharedJob.EditingBirthday = true;
            SharedCheck(PhotoGatheringProgress.Total(sharedJob) == 4 && PhotoGatheringProgress.Arrived(sharedJob) < 4 && !PhotoGatheringProgress.Ready(sharedJob),
                "baby progress includes the subject and caregiver in the full four-person intended roster before pickup");
            SharedCheck(PhotoGatheringProgress.Details(sharedJob).Contains(sharedBaby.LabelShort),
                "baby progress names the fixed subject who has not yet been picked up despite exclusion from standing members");
            SharedSelectSpot();
        }
        private sealed class SharedPhotoUiMeasure : Window
        {
            public SharedPhotoUiMeasure() { doCloseX = false; }
            public override void DoWindowContents(Rect rect)
            {
                if (sharedUiMeasured) return;
                var settings = MomentFramesMod.Settings; int previous = settings.albumTextSize;
                var oldStyle = Text.fontStyles[(int)GameFont.Small]; var oldFont = Text.Font; var oldColor = GUI.color;
                SharedCheck(sharedPeople.All(p => Dialog_BirthdaySetup.PortraitFor(p) != null), "native portrait textures exist for the shared ordinary roster");
                foreach (int size in AlbumStyle.Sizes)
                {
                    settings.albumTextSize = size;
                    using (AlbumStyle.Begin())
                    {
                        Text.Font = GameFont.Small;
                        SharedCheck(Dialog_BirthdaySetup.ControlHeight >= AlbumStyle.StyleFor("Ag", GameFont.Small).fontSize * 1.7f,
                            "shared control dimensions retain the selected text size " + size);
                    }
                    SharedCheck(Text.fontStyles[(int)GameFont.Small] == oldStyle && Text.Font == oldFont && GUI.color == oldColor,
                        "shared appearance scope restores native styles after size " + size);
                }
                settings.albumTextSize = previous; sharedUiMeasured = true;
            }
        }
    }
}
