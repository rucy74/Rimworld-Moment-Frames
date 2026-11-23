using System;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    public static partial class Probe
    {
        private static bool storageReviewMode;
        private static readonly string[] storageFormats = { "rgb", "palette", "gray", "gray16", "rgba", "interlaced" };
        private static FileStream storageLock;
        private static string storageLockedPath, storageBattleId, storageImportId;
        private static float storageStarted;

        private static void StorageCheck(bool ok, string label)
        { if (!ok) throw new InvalidOperationException("storage review: " + label); checks.Add("PASS: " + label); }

        private static void StorageReviewStep()
        {
            if (phase == 0)
            {
                map = Find.CurrentMap; center = map.Center; Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                deadline = Time.realtimeSinceStartup + 120f;
                var store = PhotoStore.Instance;
                Directory.CreateDirectory(store.FolderPath);
                PhotoPreviews.Reset();
                foreach (string kind in storageFormats)
                {
                    string path = Path.Combine(dir, kind + ".png");
                    StorageCheck(ExternalPhotoFiles.Png(File.ReadAllBytes(path), out _), kind + " accepted by importer header validation");
                    PhotoPreviews.DisplayPath(path, null);
                }
                foreach (string kind in new[] { "palette", "rgba" })
                {
                    string path = Path.Combine(dir, kind + ".thumb.png");
                    PhotoTextureCache.CompleteThumb(path, null, File.ReadAllBytes(Path.Combine(dir, kind + ".png")), true, null);
                    var texture = PhotoTextureCache.Thumb(path, null);
                    StorageCheck(texture != null && texture.height <= PhotoCapture.ThumbHeight && texture.width <= 1024,
                        kind + " derived thumbnail has bounded native texture dimensions");
                    PhotoTextureCache.Forget(path);
                }
                string largePath = Path.Combine(store.FolderPath, "review-survey.png");
                File.Copy(Path.Combine(dir, "survey5200.png"), largePath);
                byte[] png = File.ReadAllBytes(largePath); string hash = ExternalPhotoFiles.Hash(png);
                StorageCheck(ExternalPhotoFiles.Png(png, out _, out bool tooLarge) && !tooLarge, "5200 square survey is allowed");
                var candidate = new ExternalPhotoCandidate { path = largePath, file = Path.GetFileName(largePath), hash = hash, kind = ExternalPhotoKind.NewImage };
                StorageCheck(ExternalPhotoImport.Import(store, candidate, out var failure), "actual large survey import succeeds: " + failure);
                var imported = store.Records.Last(r => r.externalSourceHash == hash); storageImportId = imported.id;
                StorageCheck(ExternalPhotoFiles.HashFile(largePath) == hash, "large import preserves source bytes");
                byte[] oversize = (byte[])png.Clone(); oversize[16] = 0; oversize[17] = 0; oversize[18] = 32; oversize[19] = 1;
                StorageCheck(!ExternalPhotoFiles.Png(oversize, out _, out tooLarge) && tooLarge, "8193 side is classified as too large before decoding");
                StorageCheck(!ExternalPhotoFiles.Png(new byte[34], out _, out tooLarge) && !tooLarge, "corrupt header is distinguished from excessive size");

                var rec = store.CreateBattleCandidate(map, center.ToVector3Shifted(), 12f, 1.5f, "review-battle:" + Guid.NewGuid(), Enumerable.Empty<Pawn>());
                rec.file = Path.Combine(".battle-candidates", rec.id + ".png");
                string negative = Path.Combine(store.FolderPath, rec.file); Directory.CreateDirectory(Path.GetDirectoryName(negative));
                byte[] rgb = File.ReadAllBytes(Path.Combine(dir, "rgb.png"));
                File.WriteAllBytes(negative, rgb); File.WriteAllBytes(Path.ChangeExtension(negative, ".thumb.png"), rgb);
                string target = Path.Combine(store.FolderPath, rec.dateSlug + "_battle_" + rec.id + ".png");
                Directory.CreateDirectory(Path.ChangeExtension(target, ".thumb.png"));
                var fight = new Battles.Fight { map = map, candidate = rec, candidateSaved = true };
                var published = BattlePhoto.Publish(store, fight, "Review battle"); storageBattleId = rec.id;
                StorageCheck(published != null && store.Records.Count(r => r.id == rec.id) == 1, "thumbnail write failure still registers one battle photo");
                StorageCheck(File.ReadAllBytes(target).SequenceEqual(rgb) && File.Exists(negative), "published original and saved negative retain identical bytes");
                StorageCheck(BattlePhoto.Publish(store, fight, "Duplicate") == null && store.Records.Count(r => r.id == rec.id) == 1, "repeated publication does not duplicate the record");
                storageLockedPath = Path.Combine(dir, "locked.png"); File.WriteAllBytes(storageLockedPath, rgb);
                storageLock = new FileStream(storageLockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                PhotoPreviews.DisplayPath(storageLockedPath, null);
                storageStarted = Time.realtimeSinceStartup; phase = 1; Wait(.1f); return;
            }
            if (phase == 1)
            {
                foreach (string kind in storageFormats) PhotoPreviews.DisplayPath(Path.Combine(dir, kind + ".png"), null);
                if (storageFormats.Any(k => !File.Exists(PhotoPreviews.PathFor(Path.Combine(dir, k + ".png"))))) { Wait(.1f); return; }
                foreach (string kind in storageFormats)
                {
                    Texture2D source = null, preview = null;
                    try
                    {
                        source = new Texture2D(2, 2, TextureFormat.RGBA32, false); source.LoadImage(File.ReadAllBytes(Path.Combine(dir, kind + ".png")), false);
                        preview = new Texture2D(2, 2, TextureFormat.RGBA32, false); preview.LoadImage(File.ReadAllBytes(PhotoPreviews.PathFor(Path.Combine(dir, kind + ".png"))), false);
                        StorageCheck(preview.width <= PhotoPreviews.Side && preview.height <= PhotoPreviews.Side, kind + " native preview generated within size limit");
                        Color a = source.GetPixelBilinear(.5f, .5f), b = preview.GetPixelBilinear(.5f, .5f);
                        StorageCheck(Mathf.Abs(a.r-b.r)<.03f && Mathf.Abs(a.g-b.g)<.03f && Mathf.Abs(a.b-b.b)<.03f && Mathf.Abs(a.a-b.a)<.03f, kind + " preview retains colour and alpha");
                    }
                    finally { if(source != null) UnityEngine.Object.Destroy(source); if(preview != null) UnityEngine.Object.Destroy(preview); }
                }
                phase = 2; storageStarted = Time.realtimeSinceStartup; Wait(2f); return;
            }
            if (phase == 2)
            {
                StorageCheck(!File.Exists(PhotoPreviews.PathFor(storageLockedPath)), "locked source cannot create a preview");
                storageLock.Dispose(); storageLock = null; phase = 3; storageStarted = Time.realtimeSinceStartup; Wait(.1f); return;
            }
            if (phase == 3)
            {
                PhotoPreviews.DisplayPath(storageLockedPath, null);
                if (!File.Exists(PhotoPreviews.PathFor(storageLockedPath))) { Wait(.1f); return; }
                StorageCheck(true, "preview recovers after unlock without resetting the session");
                GameDataSaveLoader.SaveGame("mf-review-storage"); phase = 4;
                GameDataSaveLoader.LoadGame("mf-review-storage"); Wait(.5f); return;
            }
            if (phase == 4)
            {
                map = Find.CurrentMap;
                var store = PhotoStore.Instance;
                StorageCheck(store.Records.Count(r=>r.id==storageBattleId && r.HasFile)==1, "battle photograph survives actual save and load");
                StorageCheck(store.Records.Count(r=>r.id==storageImportId && r.HasFile)==1, "large imported photo survives actual save and load");
                File.WriteAllText(Path.Combine(dir, "storage-review.txt"), string.Join("\n", checks));
                Finish();
            }
        }
    }
}
