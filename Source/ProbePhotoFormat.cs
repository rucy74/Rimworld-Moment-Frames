using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>The photo file format and deleting for good (2026-11-23, -momentFramesPhotoFormat). New photos are JPG by default and PNG when
    /// chosen; every reader takes either (worker decoder, frame textures, previews, the full-size viewer, the file check, the folder scan,
    /// another colony's import); an edit follows the setting while its original keeps its format; deleting archived photos for good removes
    /// every file only they use, keeps a protected copy another photo still uses, and leaves a chronicle line for moments that keep one.</summary>
    public static partial class Probe
    {
        private static bool photoFormatMode;
        private static IEnumerator pfScript;
        private static PhotoRecord pfTaken;

        private static void PfCheck(bool ok, string label)
        { if (!ok) throw new InvalidOperationException("photo format: " + label); checks.Add("PASS: " + label); }

        private static void PhotoFormatStep()
        {
            CloseLog();
            if (pfScript == null)
            {
                deadline = Time.realtimeSinceStartup + 420f;
                map = Find.CurrentMap; center = map.Center;
                pfScript = PfRun();
            }
            if (pfScript.MoveNext()) return;
            MomentFramesMod.Settings.photoFormat = PhotoImageFormat.Jpg;
            File.WriteAllText(Path.Combine(dir, "photo-format-result.json"), "{\"ok\":true,\"runtime\":" + Json(VersionControl.CurrentVersionStringWithRev)
                + ",\"checks\":[" + string.Join(",", checks.Select(Json)) + "]}");
            active = false; Application.Quit();
        }

        private static IEnumerable PfFor(float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until) yield return null;
        }

        private static IEnumerable PfTake(PhotoStore store, string kind, string key, List<Pawn> people)
        {
            while (PhotoCapture.Busy) yield return null;
            pfTaken = store.RequestNow(map, center.ToVector3Shifted(), kind, key, key, "photo format test", people, 14f, 1.5f, null);
            PfCheck(pfTaken != null, kind + " photo requested");
            float until = Time.realtimeSinceStartup + 90f;
            while (!pfTaken.HasFile && Time.realtimeSinceStartup < until) yield return null;
            PfCheck(pfTaken.HasFile, kind + " photo written: " + pfTaken.file);
            while (PhotoCapture.Busy) yield return null;
        }

        private static IEnumerator PfRun()
        {
            var store = PhotoStore.Instance;
            var settings = MomentFramesMod.Settings;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            ClearHostiles(map); Find.Storyteller.storytellerComps.Clear(); Find.Storyteller.incidentQueue.Clear();
            settings.memoryNight = settings.yearLetter = settings.buildingPhotos = false; settings.settlementPeriod = 0;
            settings.fxFreeze = settings.fxCamera = false;
            Prefs.DevMode = false; Find.PlaySettings.showLearningHelper = false;
            var people = map.mapPawns.FreeColonistsSpawned.Take(3).ToList();
            PfCheck(people.Count >= 2, "fresh minimal game has colonists");
            foreach (var cell in CellRect.CenteredOn(center, 16)) { map.fogGrid.Unfog(cell); map.roofGrid.SetRoof(cell, null); }
            foreach (var cell in CellRect.CenteredOn(center, 12)) foreach (var thing in cell.GetThingList(map).Where(t => t.def.passability == Traversability.Impassable).ToList()) thing.Destroy();
            foreach (var pawn in people) { FixReady(pawn); pawn.Position = center + IntVec3.West * (2 - people.IndexOf(pawn) * 2); pawn.Notify_Teleported(); }
            Find.CameraDriver.JumpToCurrentMapLoc(center);
            foreach (var s in PfFor(1f)) yield return s;

            // 1. The default: a new photo is a JPG, everything derived from it stays PNG, and every reader takes it.
            PfCheck(settings.photoFormat == PhotoImageFormat.Jpg && PhotoImageFormat.SaveJpeg, "JPG is the default");
            foreach (var s in PfTake(store, "wedding", "pf-wedding", people)) yield return s;
            var a = pfTaken; string folder = store.FolderPath;
            string aPath = Path.Combine(folder, a.file); byte[] aBytes = File.ReadAllBytes(aPath);
            PfCheck(a.file.EndsWith(".jpg", StringComparison.Ordinal) && PhotoJpeg.IsJpeg(aBytes), "the new photo is saved as a JPG: " + a.file);
            PfCheck(PhotoJpeg.Size(aBytes, out int aw, out int ah) && aw >= 1000 && Mathf.Abs(aw / (float)ah - 1.5f) < .02f, "the JPG has the photo's size " + aw + "x" + ah);
            PfCheck(ExternalPhotoFiles.Png(aBytes, out float aspect) && Mathf.Abs(aspect - aw / (float)ah) < .001f, "the image check accepts it and reads its proportions");
            string aBaseline = a.sourceBaselineFile == null ? null : Path.Combine(folder, a.sourceBaselineFile);
            PfCheck(a.sourceBaselineFile == Path.Combine(ExternalPhotoFiles.BaselineFolder, a.sourceSha256 + ".jpg") && File.ReadAllBytes(aBaseline).SequenceEqual(aBytes),
                "its protected copy is the same JPG: " + a.sourceBaselineFile);
            string aThumb = Path.Combine(folder, Path.GetFileNameWithoutExtension(a.file) + ".thumb.png");
            PfCheck(File.Exists(aThumb) && PhotoImageFormat.IsPng(File.ReadAllBytes(aThumb)), "its thumbnail stays a PNG");
            string aPreview = PhotoPreviews.PathFor(store.OriginalPath(a));
            PfCheck(File.Exists(aPreview) && PhotoImageFormat.IsPng(File.ReadAllBytes(aPreview)), "its preview stays a PNG");
            var decoded = PhotoImageFormat.DecodeOnWorker(aBytes);
            PfCheck(decoded != null && decoded.Width == aw && decoded.Height == ah, "the worker decoder reads it");
            checks.Add("INFO: this photo is " + (aBytes.Length / 1e6).ToString("0.00") + " MB as JPG and would be " + (decoded.Encode().Length / 1e6).ToString("0.00") + " MB as PNG; JPG encode "
                + PhotoCapture.LastEncodeMilliseconds.ToString("0") + " ms on the worker");
            PfCheck(PhotoStorage.References(store, a.id).Select(r => PhotoStorageFiles.Inspect(r, CancellationToken.None)).All(x => x.State == PhotoFileState.Ready), "the photo file check finds it ready");
            PfCheck(ExternalPhotoImport.Scan(store).All(c => c.file != a.file), "the folder scan does not take it for a changed image");
            PfCheck(Traverse.Create(typeof(RelicImport)).Method("FromName", new[] { typeof(string) }).GetValue<RelicPhoto>(a.file) != null, "another colony would read it back by its name");
            var frameRead = Traverse.Create(typeof(FramePhotoTextures)).Method("Read", new[] { typeof(string), typeof(int) }).GetValue(store.FilePath(a), 512);
            PfCheck(Traverse.Create(frameRead).Field("Pixels").GetValue() != null && Traverse.Create(frameRead).Field("Error").GetValue<string>() == null, "a frame's texture worker decodes it");
            File.Delete(aPreview); PhotoTextureCache.Forget(aPreview); PhotoPreviews.Reset();
            float until = Time.realtimeSinceStartup + 30f;
            while (!File.Exists(aPreview) && Time.realtimeSinceStartup < until) { PhotoPreviews.DisplayPath(store.OriginalPath(a), null); yield return null; }
            PfCheck(File.Exists(aPreview) && PhotoImageFormat.IsPng(File.ReadAllBytes(aPreview)), "a missing preview is made again from the JPG");
            var zoom = new Dialog_MFPhotoZoom(a); Find.WindowStack.Add(zoom);
            var zoomField = Traverse.Create(zoom);
            until = Time.realtimeSinceStartup + 30f;
            while (zoomField.Field("texture").GetValue<Texture2D>() == null && !zoomField.Field("loadFailed").GetValue<bool>() && Time.realtimeSinceStartup < until) yield return null;
            var full = zoomField.Field("texture").GetValue<Texture2D>();
            PfCheck(full != null && full.width == aw && full.height == ah, "the full-size viewer shows it at full resolution");
            foreach (var s in PfFor(.8f)) yield return s;
            Shot("pf-viewer.png");
            foreach (var s in PfFor(.5f)) yield return s;
            zoom.Close(false);

            // 2. An edit follows the setting.
            var edit = store.SaveEdit(a, new Rect(.1f, .1f, .6f, .6f), PhotoFilters.None);
            string editPath = edit == null ? null : store.EditPath(edit);
            PfCheck(edit != null && edit.file.EndsWith(".jpg", StringComparison.Ordinal) && PhotoJpeg.IsJpeg(File.ReadAllBytes(editPath)), "an edit is saved as a JPG too: " + edit?.file);
            PfCheck(File.Exists(Path.Combine(folder, Path.GetFileNameWithoutExtension(edit.file) + ".thumb.png")) && store.FilePath(a) == editPath, "the edit has its thumbnail and is the version shown");

            // 3. PNG when chosen; an edit made after switching back is JPG while its original stays PNG.
            settings.photoFormat = PhotoImageFormat.Png;
            foreach (var s in PfTake(store, Kinds.Manual, "pf-manual-png", people)) yield return s;
            var b = pfTaken;
            PfCheck(b.file.EndsWith(".png", StringComparison.Ordinal) && PhotoImageFormat.IsPng(File.ReadAllBytes(Path.Combine(folder, b.file)))
                && b.sourceBaselineFile != null && b.sourceBaselineFile.EndsWith(".png", StringComparison.Ordinal), "with PNG chosen the photo and its protected copy are PNG: " + b.file);
            settings.photoFormat = PhotoImageFormat.Jpg;
            var bEdit = store.SaveEdit(b, new Rect(0f, 0f, .5f, .5f), PhotoFilters.None);
            PfCheck(bEdit != null && bEdit.file.EndsWith(".jpg", StringComparison.Ordinal) && Path.GetFileNameWithoutExtension(bEdit.file) == Path.GetFileNameWithoutExtension(b.file) + "_e1",
                "after switching back an edit of the PNG photo is a JPG: " + bEdit?.file);

            // 4. A second photo that shares the first one's protected copy (the same picture), so deleting must keep that copy.
            string cFile = "pf-shared-copy.jpg";
            File.Copy(aPath, Path.Combine(folder, cFile)); File.Copy(aThumb, Path.Combine(folder, "pf-shared-copy.thumb.png"));
            var c = UiRecord(store, "pf-shared", Kinds.Manual, cFile, 5);
            c.sourceSha256 = a.sourceSha256; c.sourceBaselineFile = a.sourceBaselineFile; store.NotifyChanged();
            PfCheck(store.OriginalPath(c) == aBaseline, "control: the second photo reads the shared protected copy");

            // 5. Deleting the wedding for good from the archive tab.
            var aOnly = PhotoArchive.FilesOf(store, a).Where(File.Exists).Where(f => !PhotoArchive.FilesOf(store, c).Contains(f)).ToList();
            var person = people.FirstOrDefault(p => a.Has(p));
            PfCheck(person != null && aOnly.Contains(aPath) && aOnly.Contains(editPath) && !aOnly.Contains(aBaseline), "control: the wedding has people and files of its own (" + aOnly.Count + ")");
            PfCheck(store.Archive(a), "the wedding goes to the archive");
            AlbumOpen(false, false); UiAlbum.ShowArchive();
            foreach (var s in PfFor(1f)) yield return s;
            Shot("pf-archive-tab.png");
            foreach (var s in PfFor(.5f)) yield return s;
            MainTabWindow_Album.AskDeleteArchived(new List<PhotoRecord> { a });
            var box = Find.WindowStack.WindowOfType<Dialog_MessageBox>();
            string question = box?.text.ToString() ?? "";
            PfCheck(question.Contains("MF_Archive_DeleteOne".Translate().ToString()) && question.Contains("MF_Archive_DeleteWarning".Translate().ToString())
                && question.Contains("MF_Archive_ConfirmLine".Translate().ToString()), "the question warns about older saves and says the chronicle line stays");
            foreach (var s in PfFor(1f)) yield return s;
            Shot("pf-delete-question.png");
            foreach (var s in PfFor(.5f)) yield return s;
            box.buttonAAction(); box.Close(false);
            PfCheck(aOnly.All(f => !File.Exists(f)), "deleting removes every file only the wedding used: photo, thumbnail, edit and the edit's thumbnail (" + aOnly.Count + ")");
            PfCheck(File.Exists(aBaseline) && File.Exists(store.OriginalPath(c)), "a protected copy another photo still uses stays");
            PfCheck(!a.HasFile && store.ArchivedRecords.Contains(a) && store.ChronicleFor(person).Contains(a), "the wedding stays as a chronicle line without a picture");
            PfCheck(UiAlbum.ArchiveTabWidth == 0f, "with no pictures left in the archive the tab goes away");
            foreach (var s in PfFor(1f)) yield return s;

            // 6. Emptying the archive: the rest go, and the shared copy too once nothing uses it.
            var bFiles = PhotoArchive.FilesOf(store, b).Where(File.Exists).ToList();
            PfCheck(store.Archive(b) && store.Archive(c), "two more photos go to the archive");
            long freed = store.DeleteArchived(store.ArchivedRecords.Where(r => r.HasFile).ToList());
            PfCheck(bFiles.Count >= 4 && bFiles.All(f => !File.Exists(f)), "emptying the archive removes the PNG photo with its JPG edit (" + bFiles.Count + " files)");
            PfCheck(!File.Exists(aBaseline) && !File.Exists(Path.Combine(folder, cFile)), "the shared protected copy goes once nothing uses it");
            PfCheck(freed > 0 && !store.AllRecords.Contains(b) && !store.AllRecords.Contains(c), "photos somebody took leave no record behind; freed " + StorageUsage.Bytes(freed));
            string aId = a.id, bId = b.id;

            // 7. Saving and loading keeps it that way.
            GameDataSaveLoader.SaveGame("mfphotoformat");
            while (PhotoCapture.Busy) yield return null;
            GameDataSaveLoader.LoadGame("mfphotoformat");
            foreach (var s in PfFor(2f)) yield return s;
            store = PhotoStore.Instance; map = Find.CurrentMap;
            var loaded = store.GetAny(aId);
            PfCheck(loaded != null && !loaded.HasFile && loaded.Archived && store.GetAny(bId) == null, "after loading the wedding is still a line without a picture and the other photo is still gone");
            PfCheck(PhotoStorage.References(store).All(r => r.RecordId != aId && r.RecordId != bId), "the photo file check no longer looks for deleted files");
        }
    }
}
