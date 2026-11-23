using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace MomentFrames
{
    /// <summary>The archive tab: a photo leaves every place at once, nothing is deleted, nothing is photographed again, and restoring puts it back where it was.</summary>
    public static partial class Probe
    {
        private static bool archiveMode;
        private static PhotoRecord archWed, archMan, archBurstA, archBurstB, archBurstC, archSurvey, archEventOnly, archSpare;
        private static Building_PhotoFrame archFrame, archFrameRehung;
        private static PhotoRecord archUndated;
        private static string archRehungId;
        private static Building_PhotoCollage archCollage;
        private static Building_PhotoProjector archProjector;
        private static Building_PhotoTree archTree;
        private static List<Pawn> archPeople;
        private static List<string> archOrder;
        private static int archScanBefore, archReelBefore, archCountBefore;
        private const string ArchWedKey = "wedding:archive-test";

        private static void ArchCheck(bool ok, string label)
        { if (!ok) throw new InvalidOperationException("archive: " + label); checks.Add("PASS: " + label); }

        private static List<string> ArchLabels(PhotoRecord rec) => UiAlbum.ArchiveOptions(rec).Select(o => o.Label).ToList();

        private static string ArchPngName(int n, string kind, string id) => (900 + n).ToString("0000") + "_5500-1-02_" + kind + "_" + id + "_aa" + n.ToString("000000") + ".png";

        private static PhotoRecord ArchRecord(PhotoStore store, byte[] png, int n, string id, string kind)
        {
            string file = ArchPngName(n, kind, id);
            File.WriteAllBytes(Path.Combine(store.FolderPath, file), png);
            File.WriteAllBytes(Path.Combine(store.FolderPath, Path.GetFileNameWithoutExtension(file) + ".thumb.png"), png);
            var rec = UiRecord(store, id, kind, file, n);
            rec.eventKey = "archive-test:" + id; rec.dateSlug = "5500-1-02"; rec.title = id;
            return rec;
        }

        private static T ArchSpawn<T>(string def, IntVec3 at) where T : Thing
        {
            var thingDef = ThingDef.Named(def);
            var thing = (T)ThingMaker.MakeThing(thingDef, thingDef.MadeFromStuff ? ThingDefOf.WoodLog : null);
            thing.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(thing, at, map, Rot4.North);
            return thing;
        }

        private static int ArchScan(PhotoStore store, string file) => ExternalPhotoImport.Scan(store).Count(c => c.file == file);

        private static bool ArchRelicSees(PhotoStore store, string file)
        {
            RelicImport.WriteIndex(store);
            return RelicImport.Read(store.FolderPath, "archive-test").photos.Any(p => p.file == file);
        }

        private static void ArchWhileAway(PhotoStore store)
        {
            var person = archPeople[0];
            ArchCheck(store.Records.All(r => r != archWed) && store.Get(archWed.id) == null && store.GetAny(archWed.id) == archWed && archWed.Archived, "the photo leaves the record list, an ordinary lookup, and can still be found as archived");
            ArchCheck(File.Exists(store.OriginalPath(archWed)) && File.Exists(store.OriginalThumbPath(archWed)), "nothing is deleted: the picture and its thumbnail stay");
            ArchCheck(store.HasKey(ArchWedKey), "the event still counts as photographed");
            ArchCheck(store.Request(map, center.ToVector3Shifted(), "wedding", ArchWedKey, "again", null, archPeople, 12f, 1.5f, null) == null, "a request for the same moment is refused, so it is not taken again");
            ArchCheck(store.Retitle(ArchWedKey, "arch-wed-retitled", null, null) && archWed.title == "arch-wed-retitled", "a late retitle of the event still reaches it");
            archWed.title = "arch-wed";
            var lateEdit = new PhotoEdit { id = "arch-late-edit", file = archWed.file + ".edit.png" };
            ArchCheck(store.CommitEdit(archWed, lateEdit) && archWed.edits.Remove(lateEdit), "a changed file found for it can still be registered");
            long raw = new FileInfo(Path.Combine(store.FolderPath, archWed.file)).Length;
            ArchCheck(PhotoArchive.Bytes(store) >= raw * 2, "the size counts the picture and its thumbnail, not just one");
            ArchCheck(UiAlbum.ArchiveTabWidth > 0f, "the tab exists while something is in it");
            var chosen = AlbumFavorites.FilmIds(store, store.Records.Where(r => r.id == archSpare.id), new[] { archSpare.id, archWed.id, "gone" });
            ArchCheck(chosen.Count == 2 && chosen.Contains(archWed.id), "choosing a film again keeps the archived photo on it, and drops what no longer exists");
            UiAlbum.ShowChronicle(false, false); UiAlbum.ClearFilters();
            ArchCheck(UiList().All(r => r != archWed), "photo tab no longer lists it");
            UiAlbum.ShowChronicle(true, false);
            ArchCheck(UiList().All(r => r != archWed), "colony chronicle no longer lists it");
            UiAlbum.ShowChronicle(false, false);
            ArchCheck(store.CountFor(person) == archCountBefore - 1 && store.For(person).All(r => r != archWed), "the person's photo count drops by one");
            ArchCheck(store.ChronicleFor(person).Contains(archWed), "an event keeps its line in the person's chronicle");
            ArchCheck(archFrame.Record == null && archFrame.photoId == archWed.id, "the frame shows nothing but remembers the photo");
            archFrameRehung.SetPhoto(archSpare.id);
            ArchCheck(archCollage.SlotId(0) == archWed.id && archCollage.Displayed.All(r => r != archWed), "the board keeps its slot and does not show it");
            ArchCheck(archProjector.ReelCount == archReelBefore - 1 && archProjector.SelectedPhotoIds.Contains(archWed.id), "the chosen film skips it and remembers it");
            archTree.RefreshSelection();
            ArchCheck(archTree.SlotId(0) == archWed.id && archTree.Selection.slots[0].pinned && archTree.BoundTextureAt(0) == null && archTree.Displayed.All(r => r != archWed), "the tree keeps a pinned place empty");
            ArchCheck(ArchScan(store, archWed.file) == archScanBefore, "the external photo scan does not take the kept file for a new photo");
            ArchCheck(!ArchRelicSees(store, archWed.file) && ArchRelicSees(store, archSpare.file), "another game reading this folder does not see it, and still sees the rest");
            ArchCheck(PhotoStorage.References(store).Any(r => r.RecordId == archWed.id), "the storage window still looks after its file");
        }

        private static void ArchBursts(PhotoStore store)
        {
            var before = new PhotoBursts(store.Records.ToList());
            ArchCheck(before.Prints.Contains(archBurstB) && !before.Prints.Contains(archBurstA), "control: the burst shows its chosen representative");
            ArchCheck(store.Archive(archBurstB), "one shot of a burst can leave");
            var one = new PhotoBursts(store.Records.ToList());
            ArchCheck(one.Prints.Contains(archBurstA) && one.Shots(archBurstA).Count == 2, "the remaining shots pick another representative");
            UiGallery(UiAlbum, archBurstA);
            UiAlbum.ArchivePhotos(new List<PhotoRecord> { archBurstA, archBurstC });
            ArchCheck(new PhotoBursts(store.Records.ToList()).Prints.All(r => r != archBurstA && r != archBurstB && r != archBurstC), "the whole burst can leave");
            ArchCheck(!store.ChronicleFor(archPeople[0]).Contains(archBurstA), "a group photo somebody took leaves the person's chronicle whole");
            foreach (var shot in new[] { archBurstA, archBurstB, archBurstC }) ArchCheck(store.Restore(shot), "shot restored: " + shot.id);
            ArchCheck(new PhotoBursts(store.Records.ToList()).Prints.Contains(archBurstB), "restoring brings the original representative back");
            ArchCheck(store.Archive(archBurstB), "the representative can leave again");
            PhotoBursts.SetRepresentative(store, archBurstA);
            ArchCheck(store.Restore(archBurstB) && new PhotoBursts(store.Records.ToList()).Prints.Contains(archBurstA) && !archBurstB.groupRepresentative, "a new representative chosen meanwhile stays after restoring");
            PhotoBursts.SetRepresentative(store, archBurstB);
            var filter = Traverse.Create(UiAlbum);
            filter.Field("search").SetValue("arch-burst-b"); filter.Field("cacheValid").SetValue(false); UiList();
            ArchCheck(ArchLabels(archBurstB).Contains("MF_Archive_MoveBurst".Translate(3).ToString()), "the whole burst means all of it, whatever the filter shows");
            UiAlbum.ClearFilters();
        }

        private static void ArchNightGuard(PhotoStore store)
        {
            string label = "MF_Archive_Move".Translate().ToString();
            var free = UiAlbum.ArchiveOptions(archSpare).First(o => o.Label == label);
            ArchCheck(UiAlbum.ArchiveBlocked(archSpare) == null, "control: the button is open for an ordinary photo");
            ArchCheck(!free.Disabled, "control: an ordinary photo can be moved");
            var gather = MemoryNight.GatherCell(archProjector);
            var job = new LordJob_MFMemoryNight(gather, archPeople[0], MemoryNight.Def, archProjector, archPeople, true, MemoryNight.DefaultWatchTicks);
            var lord = LordMaker.MakeNewLord(Faction.OfPlayer, job, map, archPeople);
            Traverse.Create(job).Field("filmIds").SetValue(new List<string> { archSpare.id });
            ArchCheck(PhotoArchive.InNightFilm(archSpare) && !PhotoArchive.InNightFilm(archMan), "the night's film is recognised");
            ArchCheck(UiAlbum.ArchiveBlocked(archSpare) == "MF_Archive_InNight".Translate().ToString(), "the button is greyed out while the night shows it, and says why");
            var blocked = UiAlbum.ArchiveOptions(archSpare).First(o => o.Label.StartsWith(label, StringComparison.Ordinal));
            ArchCheck(blocked.Disabled && blocked.Label.Contains("MF_Archive_InNight".Translate().ToString()), "a photo in the running film cannot be moved, and says why");
            map.lordManager.RemoveLord(lord);
            foreach (var pawn in archPeople) { pawn.jobs?.EndCurrentJob(Verse.AI.JobCondition.InterruptForced, false); }
        }

        private static void ArchiveStep()
        {
            CloseLog();
            var store = PhotoStore.Instance;
            var album = UiAlbum;
            var field = Traverse.Create(album);
            switch (phase)
            {
                case 0:
                    deadline = Time.realtimeSinceStartup + 240f; map = Find.CurrentMap; center = map.Center;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    ClearHostiles(map); Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear();
                    var settings = MomentFramesMod.Settings;
                    settings.autoCaptureEnabled = settings.memoryNight = settings.yearLetter = settings.buildingPhotos = false;
                    settings.settlementPeriod = 0; settings.albumTextSize = 100;
                    Prefs.DevMode = false; Find.PlaySettings.showLearningHelper = false;
                    archPeople = map.mapPawns.FreeColonistsSpawned.Take(3).ToList();
                    ArchCheck(archPeople.Count >= 2, "fresh minimal game has colonists");
                    foreach (var cell in CellRect.CenteredOn(center, 16)) { map.fogGrid.Unfog(cell); map.roofGrid.SetRoof(cell, null); }
                    foreach (var cell in CellRect.CenteredOn(center, 12)) foreach (var thing in cell.GetThingList(map).Where(t => t.def.passability == Traversability.Impassable).ToList()) thing.Destroy();
                    foreach (var pawn in archPeople) { FixReady(pawn); pawn.Position = center + IntVec3.West * (6 + archPeople.IndexOf(pawn)); pawn.Notify_Teleported(); }
                    Directory.CreateDirectory(store.FolderPath);
                    var tex = new Texture2D(600, 400, TextureFormat.RGB24, false);
                    tex.SetPixels32(Enumerable.Repeat(new Color32(70, 120, 90, 255), 600 * 400).ToArray()); tex.Apply(false);
                    byte[] png = tex.EncodeToPNG(); UnityEngine.Object.Destroy(tex);
                    archWed = ArchRecord(store, png, 1, "arch-wed", "wedding"); archWed.eventKey = ArchWedKey; archWed.SetPeople(archPeople);
                    archMan = ArchRecord(store, png, 2, "arch-man", Kinds.Manual); archMan.SetPeople(archPeople);
                    archBurstA = ArchRecord(store, png, 3, "arch-burst-a", GroupPhoto.Kind);
                    archBurstB = ArchRecord(store, png, 4, "arch-burst-b", GroupPhoto.Kind);
                    archBurstC = ArchRecord(store, png, 5, "arch-burst-c", GroupPhoto.Kind);
                    foreach (var shot in new[] { archBurstA, archBurstB, archBurstC }) { shot.groupSession = "arch-burst"; shot.SetPeople(archPeople); }
                    archBurstB.groupRepresentative = true;
                    archSurvey = ArchRecord(store, png, 6, "arch-survey", Kinds.SettlementOverview);
                    archSpare = ArchRecord(store, png, 7, "arch-spare", Kinds.Manual);
                    for (int i = 8; i < 11; i++) ArchRecord(store, png, i, "arch-film-" + i, Kinds.Manual);
                    archEventOnly = UiRecord(store, "arch-event-only", "wedding", null, 12); archEventOnly.eventOnly = true;
                    archUndated = ArchRecord(store, png, 13, "arch-undated", Kinds.External); archUndated.ticksAbs = -1;
                    foreach (var pair in new Dictionary<string, bool> { { "wedding", true }, { "death", true }, { PetMilestones.Kind, true }, { Kinds.Manual, false }, { GroupPhoto.Kind, false }, { PetPhotos.Kind, false }, { Kinds.Birthday, false }, { Kinds.Reshoot, false }, { Kinds.SettlementOverview, false } })
                        ArchCheck(Kinds.KeepsLineWhenArchived(pair.Key) == pair.Value, "chronicle line when archived, " + pair.Key + ": " + pair.Value);
                    store.NotifyChanged();
                    archFrame = ArchSpawn<Building_PhotoFrame>("MF_PhotoFrame", center + new IntVec3(-3, 0, 3)); archFrame.SetPhoto(archWed.id);
                    archFrameRehung = ArchSpawn<Building_PhotoFrame>("MF_PhotoFrame", center + new IntVec3(-6, 0, 3)); archFrameRehung.SetPhoto(archWed.id);
                    archRehungId = archFrameRehung.ThingID;
                    archCollage = ArchSpawn<Building_PhotoCollage>("MF_PhotoCollage", center + new IntVec3(1, 0, 3)); archCollage.SetSlot(0, archWed.id);
                    archProjector = ArchSpawn<Building_PhotoProjector>("MF_Projector", center + new IntVec3(0, 0, -4));
                    archProjector.SetSelectedFilm(new[] { archWed.id, archSpare.id, "arch-film-8", "arch-film-9" });
                    archTree = ArchSpawn<Building_PhotoTree>("MF_MemoryTree", center + new IntVec3(7, 0, 3));
                    archTree.SetPhoto(0, archWed.id); archTree.SetPhoto(1, archMan.id); archTree.SetPinned(1, false);
                    ArchCheck(archTree.SlotId(1) == archMan.id, "control: an unpinned tree place holds a photo");
                    archTree.SetPhoto(2, archSpare.id); archTree.ClearSlot(2);
                    ArchCheck(store.Archive(archSpare) && store.Restore(archSpare), "a photo not on the tree can go and come back");
                    archTree.RefreshSelection();
                    ArchCheck(archTree.Selection.slots.All(s => s.photoId != archSpare.id), "a photo the player took off the tree stays off after a round trip");
                    ArchCheck(album.ArchiveTabWidth == 0f, "with nothing archived the tab does not exist");
                    ArchCheck(store.Archive(archUndated) && store.Restore(archUndated) && store.Records.Last() == archUndated, "an undated photo goes back to the end");
                    archOrder = store.Records.Select(r => r.id).ToList();
                    archReelBefore = archProjector.ReelCount; archCountBefore = store.CountFor(archPeople[0]);
                    ArchCheck(archFrame.Record == archWed && archCollage.Displayed.Contains(archWed) && archReelBefore == 4, "control: frame, board and film show the photo");
                    ArchCheck(store.ChronicleFor(archPeople[0]).Contains(archWed) && store.ChronicleFor(archPeople[0]).Contains(archMan), "control: both photos are in the person's chronicle");
                    ArchCheck(Traverse.Create(typeof(RelicImport)).Method("FromName", new[] { typeof(string) }).GetValue<RelicPhoto>(archWed.file) != null, "control: a kept file would be read back by its name");
                    ArchCheck(ArchRelicSees(store, archWed.file), "control: another game sees an ordinary photo");
                    string stray = ArchPngName(99, Kinds.Manual, "arch-stray");
                    File.WriteAllBytes(Path.Combine(store.FolderPath, stray), png);
                    ArchCheck(ArchScan(store, stray) == 1, "control: the scan does notice a file it does not know");
                    File.Delete(Path.Combine(store.FolderPath, stray));
                    archScanBefore = ArchScan(store, archWed.file);
                    ArchCheck(!store.Archive(archSurvey) && !store.Archive(archEventOnly) && !store.Archive(null), "settlement surveys and event records cannot be moved");
                    AlbumOpen(false, false);
                    phase = 1; Wait(1f); return;
                case 1:
                    Shot("album-none-archived.png"); phase = 101; Wait(.5f); return;
                case 101:
                    UiGallery(album, archWed);
                    var labels = ArchLabels(archWed);
                    ArchCheck(labels.Contains("MF_Archive_Move".Translate().ToString()) && !labels.Contains("MF_Archive_MoveBurst".Translate(3).ToString()), "the button offers one choice for a single photo");
                    ArchCheck(album.GalleryMoreOptions(archWed).All(o => o.Label != "MF_Archive_Move".Translate().ToString()), "it is not buried in the photo tools menu any more");
                    ArchCheck(album.ArchiveBlocked(archSurvey) == "MF_Archive_SurveyHint".Translate().ToString(), "the button is greyed out for a settlement survey, and says why");
                    var burstLabels = ArchLabels(archBurstB);
                    ArchCheck(burstLabels.Contains("MF_Archive_MoveBurst".Translate(3).ToString()), "a burst also offers all of its shots");
                    phase = 2; Wait(1f); return;
                case 2:
                    Shot("archive-gallery-buttons.png"); phase = 201; Wait(.5f); return;
                case 201:
                    UiGallery(album, archBurstB);
                    album.ArchiveButton(archBurstB);
                    ArchCheck(Find.WindowStack.WindowOfType<FloatMenu>() != null && Find.WindowStack.WindowOfType<Dialog_MessageBox>() == null, "the button asks how much of a burst first");
                    phase = 202; Wait(1f); return;
                case 202:
                    Shot("archive-menu.png"); phase = 102; Wait(.5f); return;
                case 102:
                    Find.WindowStack.TryRemove(typeof(FloatMenu), false);
                    UiGallery(album, archWed);
                    album.ArchiveButton(archWed);
                    var question = Find.WindowStack.WindowOfType<Dialog_MessageBox>();
                    string text = question?.text.ToString() ?? "";
                    bool Names(string part) => text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;   // the game capitalizes the first item in English
                    ArchCheck(Names("MF_Archive_UsesFrames".Translate(3)) && Names("MF_Archive_UsesProjectors".Translate(1)) && Names("MF_Archive_UsesTrees".Translate(1)),
                        "the question names where it is in use: " + text);
                    ArchCheck(text.Contains("MF_Archive_ConfirmLine".Translate().ToString()), "the question says an event stays in the chronicle");
                    ArchCheck(!PhotoArchive.Question(new[] { archSpare }).Contains("MF_Archive_ConfirmLine".Translate().ToString()), "a photo somebody took has no such line");
                    phase = 3; Wait(1f); return;
                case 3:
                    Shot("archive-question.png"); phase = 103; Wait(.5f); return;
                case 103:
                    var box = Find.WindowStack.WindowOfType<Dialog_MessageBox>();
                    var shown = UiList(); int place = shown.IndexOf(archWed);
                    var neighbour = place + 1 < shown.Count ? shown[place + 1] : shown[place - 1];
                    box.buttonAAction(); box.Close(false);
                    ArchCheck(store.ArchivedRecords.Contains(archWed), "answering yes moves it");
                    ArchCheck(album.GalleryCurrent == neighbour, "the gallery moves on to the next photo");
                    ArchWhileAway(store);
                    ArchCheck(store.Archive(archMan) && archTree.SlotId(1) == archMan.id, "control: a place only changes when the tree next looks");
                    archTree.RefreshSelection();
                    ArchCheck(archTree.SlotId(1) != archMan.id, "an unpinned tree place gives it up");
                    ArchCheck(!store.ChronicleFor(archPeople[0]).Contains(archMan), "a photo somebody took leaves the person's chronicle whole");
                    ArchCheck(!store.Archive(archWed), "an archived photo cannot be moved twice");
                    album.ShowArchive();
                    phase = 4; Wait(1f); return;
                case 4:
                    Shot("archive-tab.png"); phase = 104; Wait(.5f); return;
                case 104:
                    ArchCheck(field.Field("tab").GetValue().ToString() == "Archive", "the tab opens");
                    field.Field("search").SetValue("keep-this"); field.Field("cacheValid").SetValue(false);
                    UiGallery(album, archWed);
                    ArchCheck(field.Field("tab").GetValue().ToString() == "Archive" && field.Field("search").GetValue<string>() == "keep-this", "opening an archived photo stays on the tab and leaves the filters alone");
                    var zoom = Find.WindowStack.WindowOfType<Dialog_MFPhotoZoom>();
                    ArchCheck(zoom != null, "opening an archived photo shows it");
                    zoom.Close(false); album.ClearFilters();
                    var treeWindow = new Dialog_PhotoTree(archTree);
                    treeWindow.OpenSelected();
                    ArchCheck(Find.WindowStack.WindowOfType<Dialog_MFPhotoZoom>() == null && field.Field("tab").GetValue().ToString() == "Archive", "the tree window does not open an archived photo");
                    Find.WindowStack.Add(treeWindow);
                    phase = 1040; Wait(1f); return;
                case 1040:
                    Shot("archive-tree-window.png"); phase = 1041; Wait(.5f); return;
                case 1041:
                    Find.WindowStack.TryRemove(typeof(Dialog_PhotoTree), false);
                    Find.MainTabsRoot.SetCurrentTab(null, false);
                    Find.Selector.ClearSelection(); Find.Selector.Select(archPeople[0]);
                    Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, false);
                    ArchCheck(InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Chronicle)) is ITab_Pawn_Chronicle, "the person's chronicle opens");
                    Find.CameraDriver.JumpToCurrentMapLoc(center);
                    phase = 5; Wait(1f); return;
                case 5:
                    Shot("archive-chronicle-and-frames.png"); phase = 105; Wait(.5f); return;
                case 105:
                    Find.MainTabsRoot.SetCurrentTab(null, false); Find.Selector.ClearSelection();
                    ArchCheck(store.Restore(archMan), "a photo can be restored");
                    ArchNightGuard(store);
                    ArchBursts(store);
                    GameDataSaveLoader.SaveGame("mfarchive");
                    phase = 7; return;
                case 7:
                    if (PhotoCapture.Busy) return;
                    GameDataSaveLoader.LoadGame("mfarchive"); Wait(1f); phase = 8; return;
                case 8:
                    map = Find.CurrentMap;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    archWed = store.GetAny("arch-wed");
                    ArchCheck(archWed != null && archWed.Archived && store.ArchivedRecords.Contains(archWed) && store.Get("arch-wed") == null, "saving and loading keeps it archived");
                    ArchCheck(store.HasKey(ArchWedKey), "after loading the event still counts as photographed");
                    archFrameRehung = map.listerThings.AllThings.OfType<Building_PhotoFrame>().Single(f => f.ThingID == archRehungId);
                    archFrame = map.listerThings.AllThings.OfType<Building_PhotoFrame>().Single(f => f.ThingID != archRehungId);
                    archCollage = map.listerThings.AllThings.OfType<Building_PhotoCollage>().Single();
                    archProjector = map.listerThings.AllThings.OfType<Building_PhotoProjector>().Single();
                    archTree = map.listerThings.AllThings.OfType<Building_PhotoTree>().Single();
                    archPeople = archPeople.Select(p => map.mapPawns.FreeColonistsSpawned.First(x => x.GetUniqueLoadID() == p.GetUniqueLoadID())).ToList();
                    ArchCheck(archFrame.photoId == archWed.id && archFrame.Record == null && archCollage.SlotId(0) == archWed.id, "after loading the frame and board still remember it");
                    archTree.RefreshSelection();
                    ArchCheck(archTree.SlotId(0) == archWed.id && archTree.Selection.slots[0].pinned, "after loading the tree still keeps its place");
                    ArchCheck(store.ChronicleFor(archPeople[0]).Contains(archWed), "after loading the chronicle line is still there");
                    AlbumOpen(false, false); album.ShowArchive();
                    phase = 9; Wait(.8f); return;
                case 9:
                    ArchCheck(store.Restore(archWed) && !archWed.Archived && store.Get(archWed.id) == archWed && !store.Restore(archWed), "restoring brings it back, once");
                    ArchCheck(store.Records.Select(r => r.id).SequenceEqual(archOrder), "every photo is back in its original order");
                    ArchCheck(archFrame.Record == archWed, "the frame shows it again");
                    ArchCheck(archFrameRehung.photoId == "arch-spare", "a frame given another photo meanwhile keeps that photo");
                    ArchCheck(archCollage.Displayed.Contains(archWed), "the board shows it again");
                    ArchCheck(archProjector.ReelCount == archReelBefore, "the film has it again");
                    archTree.RefreshSelection();
                    ArchCheck(archTree.SlotId(0) == archWed.id, "the pinned tree place has it again");
                    ArchCheck(ArchRelicSees(store, archWed.file), "another game can read it again");
                    ArchCheck(store.ChronicleFor(archPeople[0]).Contains(archWed) && store.CountFor(archPeople[0]) == archCountBefore, "the person's chronicle and count are as before");
                    phase = 10; Wait(.8f); return;
                case 10:
                    ArchCheck(field.Field("tab").GetValue().ToString() == "Photos", "with nothing left the tab closes and the album returns to photos");
                    ArchCheck(archTree.BoundTextureAt(0) != null, "the pinned tree place draws it again");
                    archCountBefore = store.Records.Count;
                    GameDataSaveLoader.SaveGame("mfarchive-old");
                    phase = 11; return;
                case 11:
                    if (PhotoCapture.Busy) return;
                    // A save from before this feature has no such list at all.
                    string savePath = GenFilePaths.FilePathForSavedGame("mfarchive-old");
                    string saved = File.ReadAllText(savePath);
                    string older = System.Text.RegularExpressions.Regex.Replace(saved, @"<archivedRecords\s*/>|<archivedRecords>.*?</archivedRecords>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
                    ArchCheck(older.Length < saved.Length && !older.Contains("archivedRecords"), "control: the list is gone from the older save");
                    File.WriteAllText(savePath, older);
                    GameDataSaveLoader.LoadGame("mfarchive-old"); Wait(1f); phase = 12; return;
                case 12:
                    ArchCheck(store.ArchivedRecords.Count == 0 && store.Records.Count == archCountBefore && store.HasKey(ArchWedKey), "a save from before this feature loads with every photo in the album");
                    File.WriteAllText(Path.Combine(dir, "archive-result.json"), "{\"ok\":true,\"runtime\":" + Json(VersionControl.CurrentVersionStringWithRev) + ",\"checks\":[" + string.Join(",", checks.Select(Json)) + "]}");
                    active = false; Application.Quit(); return;
            }
        }
    }
}
