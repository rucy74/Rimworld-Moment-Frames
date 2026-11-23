using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool importedPhotosMode;
        private static MainTabWindow_Album importAlbum;
        private static Dialog_ChoosePhoto importPicker;
        private static string computerId, relicId, colonyId, legacyId, importedSourceHash;
        private static int importOriginalTextSize;
        private static void ImportCheck(bool ok, string label)
        { if (!ok) throw new InvalidOperationException("imported photos: " + label); checks.Add("PASS: " + label); }
        private static List<PhotoRecord> AlbumList() => (List<PhotoRecord>)AccessTools.Method(typeof(MainTabWindow_Album), "Filtered").Invoke(importAlbum, null);
        private static ImportedPhotosTab Archive() => (ImportedPhotosTab)AccessTools.Field(typeof(MainTabWindow_Album), "imports").GetValue(importAlbum);
        // The guide photos are JPEGs; a saved photo is always a PNG.
        private static void CopyAsPng(string image, string dest)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            tex.LoadImage(File.ReadAllBytes(PhotoGuide.PathFor(image)));
            File.WriteAllBytes(dest, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }

        private static PhotoRecord ImportFixture(PhotoStore store, string id, string kind, string image)
        {
            var rec = new PhotoRecord { id = id, kind = kind, title = id, desc = "분류 확인용 사진", year = 5500, ticksAbs = 500,
                dateLabel = "5500년 1분기 1일", file = id + ".png", aspect = 1.5f };
            CopyAsPng(image, store.FilePath(rec));
            ImportCheck(store.CommitRecord(rec), id + " fixture registered"); return rec;
        }
        private static void ImportedPhotosStep()
        {
            CloseLog(); // Remove only the probe's debug UI; Player.log remains intact for verification.
            var store = PhotoStore.Instance;
            switch (phase)
            {
                case 0:
                    map = Find.CurrentMap; Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    deadline = Time.realtimeSinceStartup + 180;
                    MomentFramesMod.Settings.autoCaptureEnabled = false;
                    importOriginalTextSize = MomentFramesMod.Settings.albumTextSize;
                    MomentFramesMod.Settings.albumTextSize = 100;
                    Prefs.DevMode = false;
                    foreach (var win in Find.WindowStack.Windows.Where(w => w.GetType().Name == "EditWindow_Log").ToList()) win.Close();
                    Directory.CreateDirectory(store.FolderPath);
                    var normal = ImportFixture(store, "current-colony", Kinds.Manual, "birthday"); colonyId = normal.id;
                    File.Copy(store.FilePath(normal), Path.Combine(store.FolderPath, "fixture-edit.png"));
                    normal.edits.Add(new PhotoEdit { id = "external-edit", file = "fixture-edit.png", externalImage = true });
                    ImportCheck(PhotoSources.Of(normal) == PhotoSource.Colony, "external edit does not move an original colony event");
                    var otherDay = ImportFixture(store, "same-colony-otherday", Kinds.OtherDay, "family");
                    ImportCheck(PhotoSources.Of(otherDay) == PhotoSource.Colony, "recovered same-colony day stays with this colony");
                    string input = Path.Combine(dir, "family-input.png"); CopyAsPng("family", input);
                    importedSourceHash = ExternalPhotoFiles.Hash(File.ReadAllBytes(input));
                    ImportCheck(ExternalPhotoImport.ImportFile(store, input, out var computer, out var problem), "file outside colony folder imports: " + problem);
                    computerId = computer.id; computer.title = "컴퓨터에서 가져온 가족사진"; computer.favorite = true; store.NotifyChanged();
                    ImportCheck(PhotoSources.Of(computer) == PhotoSource.Computer && computer.mapId == -1 && computer.peopleIds.Count == 0, "external provenance and unknown game context retained");
                    ImportCheck(ExternalPhotoFiles.Hash(File.ReadAllBytes(input)) == importedSourceHash && store.FilePath(computer) != input, "input is unchanged; registered copy has a separate path");
                    int count = store.Records.Count; int files = Directory.GetFiles(store.FolderPath).Length;
                    ImportCheck(!ExternalPhotoImport.ImportFile(store, input, out _, out problem) && store.Records.Count == count && Directory.GetFiles(store.FolderPath).Length == files, "duplicate import creates neither record nor extra file");
                    string broken = Path.Combine(dir, "broken.png"); File.WriteAllText(broken, "not a PNG");
                    ImportCheck(!ExternalPhotoImport.ImportFile(store, broken, out _, out problem) && store.Records.Count == count, "invalid PNG fails without adding a record");
                    ImportCheck(!ExternalPhotoImport.ImportFile(null, input, out _, out problem), "stale or absent store cannot import");
                    var oldSource = new RelicSource { gameId = "old-colony-a", name = "산들 마을", folder = Path.Combine(Path.GetDirectoryName(store.FolderPath), "old-colony-a") };
                    Directory.CreateDirectory(oldSource.folder); CopyAsPng("birthday", Path.Combine(oldSource.folder, "birthday.png"));
                    File.WriteAllText(Path.Combine(oldSource.folder, RelicImport.IndexFile), "#MomentFrames\t산들 마을\told-colony-a\n");
                    var relic = store.ImportRelic(oldSource, new RelicPhoto { file = "birthday.png", title = "산들 마을의 생일", dateLabel = "5499년 2분기 4일" });
                    ImportCheck(relic != null && relic.importedColonyName == "산들 마을" && relic.importedColonyId == "old-colony-a", "new relic freezes colony name and identity"); relicId = relic.id;
                    var legacy = ImportFixture(store, "older-import", Kinds.Relic, "family"); legacyId = legacy.id; legacy.eventKey = "relic:old-colony-a:older.png";
                    var legacyB = ImportFixture(store, "another-colony", Kinds.Relic, "birthday"); legacyB.eventKey = "relic:old-colony-b:older.png";
                    ImportCheck(PhotoSources.ColonyKey(legacy) == "old-colony-a" && PhotoSources.ColonyName(store, legacy) == "산들 마을", "old record resolves identity and index header without migration");
                    ImportCheck(PhotoSources.ColonyName(store, legacyB).Contains("old-colo"), "missing source uses an explicit colony ID fallback");
                    var unsafeKey = new PhotoRecord { kind = Kinds.Relic, eventKey = "relic:../outside:file.png" };
                    ImportCheck(PhotoSources.ColonyName(store, unsafeKey).Contains("../outsi"), "unsafe old ID falls back without resolving a directory");
                    store.NotifyChanged();
                    MainTabWindow_Album.Open(normal);
                    importAlbum = (MainTabWindow_Album)DefDatabase<MainButtonDef>.GetNamed("MF_Album").TabWindow;
                    importAlbum.ShowChronicle(false); importAlbum.ClearFilters();
                    ImportCheck(AlbumList().Select(r => r.id).OrderBy(s => s).SequenceEqual(new[] { normal.id, otherDay.id }.OrderBy(s => s)), "normal photos exclude computer and past-colony imports");
                    importAlbum.ShowChronicle(true, true);
                    ImportCheck(AlbumList().All(r => !PhotoSources.Imported(r)), "chronicle also excludes imports");
                    importAlbum.OpenImported(PhotoSource.Computer);
                    ImportCheck(AlbumList().Count == 1 && AlbumList()[0].id == computer.id, "computer tab has imported photo only");
                    ImportCheck(ReferenceEquals(AlbumList(), AlbumList()), "unchanged imported list reuses the cached result");
                    importAlbum.OpenImported(PhotoSource.Legacy);
                    ImportCheck(AlbumList().Count == 3, "legacy tab includes existing and new imports");
                    Archive().Colony = "old-colony-a";
                    ImportCheck(AlbumList().Count == 2 && AlbumList().All(r => PhotoSources.ColonyKey(r) == "old-colony-a"), "colony selector includes old and new records from the same source");
                    MainTabWindow_Album.Open(relic);
                    ImportCheck(importAlbum.GalleryCurrent?.id == relic.id && Archive().Detail && Archive().Colony == "old-colony-a", "open relic reuses gallery while retaining source filter");
                    MainTabWindow_Album.ShowVersion(relic, "");
                    ImportCheck(importAlbum.GalleryCurrent?.id == relic.id && importAlbum.GalleryVersion == "", "editor return routes to the imported photo and original version");
                    importAlbum.OpenImported();
                    ImportCheck(!Archive().Detail && Archive().Colony == "old-colony-a" && AlbumList().Count == 2, "back to list preserves colony filter");
                    Archive().Filters.Search = "no matching photograph";
                    ImportCheck(AlbumList().Count == 0, "import search produces a genuine empty result");
                    Archive().Filters.Search = "";
                    importAlbum.OpenImported(PhotoSource.Computer); Archive().Filters.FavoritesOnly = true;
                    ImportCheck(AlbumList().Count == 1, "import favorites includes selected heart");
                    AlbumFavorites.Toggle(store, computer);
                    ImportCheck(AlbumList().Count == 0, "heart change invalidates imported list cache");
                    AlbumFavorites.Toggle(store, computer); Archive().Filters.FavoritesOnly = false;
                    int callbacks = 0; var picker = new Dialog_ChoosePhoto("probe", computer.id, id => callbacks++);
                    ImportCheck(picker.Source == PhotoSource.Computer && picker.VisiblePhotos.Count == 1, "picker opens on current image source");
                    ImportCheck(picker.PreviewPhoto(computer.id) && callbacks == 0, "preview selection does not apply immediately");
                    picker.Source = PhotoSource.Legacy;
                    ImportCheck(picker.VisiblePhotos.Count == 3 && picker.PendingPhotoId == computer.id, "switch source retains selection without mixing lists");
                    ImportCheck(picker.SelectPhoto(picker.PendingPhotoId) && callbacks == 1, "confirm can apply retained selection from another source");
                    var limited = new Dialog_ChoosePhoto("limited", normal.id, id => callbacks++, new[] { normal.id }, true);
                    limited.Source = PhotoSource.Computer;
                    ImportCheck(limited.VisiblePhotos.Count == 0 && !limited.SelectPhoto(computer.id) && callbacks == 1, "allowed IDs remain enforced across sources");
                    var cancel = new Dialog_ChoosePhoto("cancel", normal.id, id => callbacks++); cancel.PreviewPhoto(normal.id); cancel.Close();
                    ImportCheck(callbacks == 1, "closing picker leaves target unchanged");
                    importAlbum.ShowChronicle(false); importAlbum.ClearFilters(); phase = 1; Wait(.5f); return;
                case 1: Shot("01-colony.png"); phase = 2; Wait(.4f); return;
                case 2: importAlbum.OpenImported(PhotoSource.Computer); phase = 3; Wait(.5f); return;
                case 3: Shot("02-computer.png"); phase = 4; Wait(.4f); return;
                case 4: importAlbum.OpenImported(PhotoSource.Legacy); Archive().Colony = null; phase = 5; Wait(.5f); return;
                case 5: Shot("03-past-colonies.png"); phase = 6; Wait(.4f); return;
                case 6: MainTabWindow_Album.Open(store.Get(computerId)); phase = 7; Wait(.5f); return;
                case 7: Shot("04-imported-gallery.png"); phase = 8; Wait(.4f); return;
                case 8: importPicker = new Dialog_ChoosePhoto("환등기", computerId, id => { }); Find.WindowStack.Add(importPicker); phase = 9; Wait(.5f); return;
                case 9: Shot("05-picker.png"); phase = 10; Wait(.4f); return;
                case 10:
                    ImportCheck(importPicker.DrawnGridArea.height > 100 && importPicker.DrawnColumns >= 1 && importPicker.DrawnThumbnailCount == 1, "native picker has a usable grid and one source thumbnail");
                    importPicker.Close(); importAlbum.OpenImported(PhotoSource.Legacy); Archive().Colony = null;
                    importAlbum.windowRect = new Rect(10, 50, 1000, 600); MomentFramesMod.Settings.albumTextSize = 150;
                    phase = 11; Wait(.5f); return;
                case 11: Shot("06-small-large-text.png"); phase = 12; Wait(.4f); return;
                case 12:
                    MomentFramesMod.Settings.albumTextSize = importOriginalTextSize;
                    Find.MainTabsRoot.SetCurrentTab(null);
                    GameDataSaveLoader.SaveGame("mf-imported-photos"); phase = 13;
                    GameDataSaveLoader.LoadGame("mf-imported-photos"); Wait(.5f); return;
                case 13:
                    ImportCheck(store.Get(relicId)?.importedColonyName == "산들 마을" && store.Get(relicId)?.importedColonyId == "old-colony-a", "relic provenance survives actual save/load");
                    ImportCheck(store.Get(computerId)?.favorite == true && store.Get(computerId)?.externalSourceHash == importedSourceHash, "imported photo and favorite survive save/load");
                    ImportCheck(PhotoSources.ColonyKey(store.Get(legacyId)) == "old-colony-a", "legacy identity still works without new fields after load");
                    ImportCheck(PhotoSources.Of(store.Get(colonyId)) == PhotoSource.Colony, "external edit leaves original source unchanged after load");
                    importAlbum.OpenImported(PhotoSource.Computer);
                    ImportCheck(AlbumList().Count == 1 && AlbumList()[0] == store.Get(computerId), "new store invalidates album cache after load");
                    Find.MainTabsRoot.SetCurrentTab(null);
                    File.WriteAllText(Path.Combine(dir, "imported-photos.txt"), string.Join("\n", checks)); Finish(); return;
            }
        }
    }
}
