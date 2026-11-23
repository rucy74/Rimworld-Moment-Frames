using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>A scene for the user to look at with their own photos (2026-11-23, -momentFramesUserTest=&lt;folder&gt;, started by
    /// user-test-jpg.cmd). Each photo hangs twice side by side: left the original PNG, right the same picture saved the way new photos
    /// are now (JPG 98, PhotoJpeg). Then the probe steps aside for good: no deadline, no quitting, the game is the user's. Everything
    /// happens in the disposable profile's own photo folder; the source folder (a copy of a few of the user's photos) is only read.</summary>
    public static partial class Probe
    {
        private static string userTestSource;

        private static void UserTestStep()
        {
            active = false;   // from here on the game belongs to the user, whatever happens below
            try
            {
                var store = PhotoStore.Instance;
                var settings = MomentFramesMod.Settings;
                map = Find.CurrentMap; center = map.Center;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                settings.photoFormat = PhotoImageFormat.Jpg;
                settings.framePhotoHeight = 1024;   // the sharpest frames, so any difference would show
                Directory.CreateDirectory(store.FolderPath);
                var files = Directory.Exists(userTestSource) ? PhotoImageFormat.ImageFiles(userTestSource).Take(8).ToList() : new List<string>();
                var pairs = new List<PhotoRecord[]>();
                foreach (string path in files)
                {
                    byte[] original = File.ReadAllBytes(path);
                    var pixels = PhotoImageFormat.DecodeOnWorker(original);
                    if (pixels == null) continue;
                    string title = Path.GetFileNameWithoutExtension(path);
                    int cut = title.IndexOf('_', title.IndexOf('_', title.IndexOf('_') + 1) + 1);   // 0012_5502-1-03_wedding_<title>_<id>
                    if (cut > 0 && title.LastIndexOf('_') > cut) title = title.Substring(cut + 1, title.LastIndexOf('_') - cut - 1).Replace('-', ' ');
                    int i = pairs.Count;
                    pairs.Add(new[] { UserTestRecord(store, original, title + " · PNG", i * 2), UserTestRecord(store, PhotoJpeg.Encode(pixels), title + " · JPG", i * 2 + 1) });
                }
                if (pairs.Count == 0) throw new InvalidOperationException("no readable PNG photos in " + userTestSource);

                // Frames in rows of four pairs: PNG on the left, JPG two cells to its right.
                int rows = (pairs.Count + 3) / 4;
                var area = CellRect.CenteredOn(center, 14);
                foreach (var cell in area)
                {
                    map.fogGrid.Unfog(cell); map.roofGrid.SetRoof(cell, null);
                    foreach (var thing in cell.GetThingList(map).Where(t => !(t is Pawn) && t.def.destroyable && t.def.category != ThingCategory.Filth).ToList()) thing.Destroy();
                }
                var report = new StringBuilder();
                for (int i = 0; i < pairs.Count; i++)
                {
                    var origin = center + new IntVec3(-9 + (i % 4) * 5, 0, (rows - 1) * 3 / 2 - (i / 4) * 3);
                    ArchSpawn<Building_PhotoFrame>("MF_PhotoFrame", origin).SetPhoto(pairs[i][0].id);
                    ArchSpawn<Building_PhotoFrame>("MF_PhotoFrame", origin + IntVec3.East * 2).SetPhoto(pairs[i][1].id);
                    report.AppendLine(pairs[i][0].file + "  |  " + pairs[i][1].file);
                }
                File.WriteAllText(Path.Combine(dir, "user-test-pairs.txt"), report.ToString());
                Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 18f);
                Messages.Message("시험 장면 준비: 액자마다 왼쪽이 원래 PNG, 오른쪽이 새 JPG입니다. 액자를 누르면 사진 이름 끝에 PNG·JPG가 보입니다.", MessageTypeDefOf.PositiveEvent, false);
                Log.Message("[MomentFrames user test] " + pairs.Count + " pairs hung; photos in " + store.FolderPath);
            }
            catch (Exception e)
            {
                Log.Error("[MomentFrames user test] scene not set up: " + e);
                Messages.Message("시험 장면을 못 만들었습니다: " + e.Message, MessageTypeDefOf.RejectInput, false);
            }
        }

        /// <summary>A manual photo of this test colony holding these bytes, with its thumbnail and protected copy, as a real photo has.</summary>
        private static PhotoRecord UserTestRecord(PhotoStore store, byte[] image, string title, int index)
        {
            byte[] thumb = null;
            float aspect = PhotoCapture.MakeThumb(image, ref thumb);
            string id = Guid.NewGuid().ToString("N");
            string file = "usertest_" + index.ToString("00") + "_" + id.Substring(0, 8) + PhotoImageFormat.ExtensionOf(image);
            File.WriteAllBytes(Path.Combine(store.FolderPath, file), image);
            if (thumb != null) File.WriteAllBytes(Path.Combine(store.FolderPath, Path.GetFileNameWithoutExtension(file) + ".thumb.png"), thumb);
            int now = Find.TickManager.TicksAbs;
            var rec = new PhotoRecord { id = id, kind = Kinds.Manual, eventKey = "usertest:" + id, title = title, desc = "",
                ticksAbs = now + index, capturedTicksAbs = now + index, year = 5500, dateLabel = "5500", dateSlug = "5500-1-01",
                mapId = map.uniqueID, aspect = PhotoCapture.ClampAspect(aspect), center = center.ToVector3Shifted(), spanCells = 19, file = file };
            if (!store.CommitRecord(rec)) throw new InvalidOperationException("could not add " + file);
            ExternalPhotoImport.RememberRecord(store, rec);
            return rec;
        }
    }
}
