using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Reference-protected originals with a bounded idle LRU, and a least-recently-drawn thumbnail cache.</summary>
    public static class PhotoTextureCache
    {
        private class Entry
        {
            public Texture2D tex;
            public Material mat;
            public int refs;
            public int lastUse;
            public long bytes;
            public long fileLength, fileTicks;
            public float checkAt;
            public bool retire;
        }

        private static readonly Dictionary<string, Entry> full = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Texture2D> thumbs = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> thumbLastUse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // frame of last use
        private static readonly Dictionary<string, float> missing = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private const float MissingRetrySeconds = 2f;
        private static int lastEvictFrame = -1;
        // Retain recently viewed originals across tab changes, with active display references protected.
        // Conservative RGB+mipmap/driver accounting; this is a cache budget, not a total game-memory limit.
        public static long FullBudgetBytes { get; set; } = 192L * 1024 * 1024;
        public static int FullCap { get; set; } = 8;
        public static int FullLoads, FullHits;
        public static long FullBytes => full.Values.Sum(e => e.bytes);
        public static int FullCount => full.Count;
        /// <summary>About 0.5 MB of video memory each (256 px high with mipmaps), so about 120 MB at the cap. A thumbnail is never bigger than
        /// that: a missing one is made from the full image (<see cref="ThumbFromFull"/>) instead of the full image standing in. Beyond the cap
        /// the least recently drawn thumbnails are dropped (reloaded from disk when scrolled back into view). Settable so the probe can exercise
        /// eviction.</summary>
        public static int ThumbCap { get; set; } = 240;

        /// <summary>Probe diagnostics: thumbnails made from the full image because the thumbnail file was missing.</summary>
        public static int ThumbsMade;

        /// <summary>Returns a material whose main texture is the photo, or null if the file is missing. Call Release with the same path.</summary>
        public static Material Acquire(string path)
        {
            if (path.NullOrEmpty()) return null;
            if (full.TryGetValue(path, out var e))
            {
                if (e.refs == 0 && Time.realtimeSinceStartup >= e.checkAt)
                {
                    e.checkAt = Time.realtimeSinceStartup + MissingRetrySeconds;
                    if (!FileCurrent(path, e)) { DestroyFull(path, e); e = null; }
                }
                if (e != null)
                {
                    e.refs++; e.lastUse = Time.frameCount; FullHits++; return e.mat;
                }
            }
            Texture2D tex = Load(path);
            if (tex == null) return null;
            var mat = new Material(ShaderDatabase.Cutout) { mainTexture = tex };
            var loaded = new Entry { tex = tex, mat = mat, refs = 1, lastUse = Time.frameCount, bytes = (long)tex.width * tex.height * 6L,
                checkAt = Time.realtimeSinceStartup + MissingRetrySeconds };
            try { var file = new FileInfo(path); if (file.Exists) { loaded.fileLength = file.Length; loaded.fileTicks = file.LastWriteTimeUtc.Ticks; } } catch { }
            full[path] = loaded;
            FullLoads++;
            EvictFull();
            return mat;
        }

        public static bool IsCached(string path) => !path.NullOrEmpty() && full.ContainsKey(path);

        internal static bool Owns(string path, Material material) => !path.NullOrEmpty() && full.TryGetValue(path, out var entry) && ReferenceEquals(entry.mat, material);

        /// <summary>The cached full texture without touching its reference count (for a holder that already acquired it).</summary>
        public static Texture2D Peek(string path) => !path.NullOrEmpty() && full.TryGetValue(path, out var e) ? e.tex : null;

        public static Texture2D AcquireTexture(string path)
        {
            var mat = Acquire(path);
            return mat == null ? null : (Texture2D)mat.mainTexture;
        }

        /// <summary>The viewer decodes large RGB photographs off-thread, then uploads only on the game thread.</summary>
        internal static Material AcquireDecoded(string path, PhotoPixels pixels, byte[] encoded)
        {
            if (IsCached(path)) return Acquire(path);
            Texture2D tex = null;
            try
            {
                tex = pixels == null ? Decode(encoded, path) : Upload(pixels);
                if (tex == null) return null;
                var file = new FileInfo(path);
                if (!file.Exists) { UnityEngine.Object.Destroy(tex); return null; }
                var mat = new Material(ShaderDatabase.Cutout) { mainTexture = tex };
                full[path] = new Entry { tex = tex, mat = mat, refs = 1, lastUse = Time.frameCount,
                    bytes = (long)tex.width * tex.height * 6L, fileLength = file.Length, fileTicks = file.LastWriteTimeUtc.Ticks,
                    checkAt = Time.realtimeSinceStartup + MissingRetrySeconds };
                FullLoads++; EvictFull(); return mat;
            }
            catch
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                throw;
            }
        }

        public static void Release(string path)
        {
            if (path.NullOrEmpty() || !full.TryGetValue(path, out var e)) return;
            e.refs = Math.Max(0, e.refs - 1);
            e.lastUse = Time.frameCount;
            if (e.refs == 0 && e.retire) { DestroyFull(path, e); return; }
            EvictFull();
        }

        private static void EvictFull()
        {
            long bytes = FullBytes;
            if (full.Count <= Math.Max(0, FullCap) && bytes <= Math.Max(0L, FullBudgetBytes)) return;
            foreach (var victim in full.Where(pair => pair.Value.refs == 0).OrderBy(pair => pair.Value.lastUse).ToList())
            {
                if (full.Count <= Math.Max(0, FullCap) && bytes <= Math.Max(0L, FullBudgetBytes)) break;
                bytes -= victim.Value.bytes;
                DestroyFull(victim.Key, victim.Value);
            }
        }

        private static void DestroyFull(string path, Entry entry)
        {
            full.Remove(path);
            if (entry.mat != null) UnityEngine.Object.Destroy(entry.mat);
            if (entry.tex != null) UnityEngine.Object.Destroy(entry.tex);
        }

        private static bool FileCurrent(string path, Entry entry)
        {
            try { var file = new FileInfo(path); return file.Exists && file.Length == entry.fileLength && file.LastWriteTimeUtc.Ticks == entry.fileTicks; }
            catch { return false; }
        }

        public static Texture2D Thumb(string thumbPath, string fullPath)
        {
            if (thumbPath.NullOrEmpty()) return null;
            int now = Time.frameCount;
            thumbLastUse[thumbPath] = now;
            if (thumbs.TryGetValue(thumbPath, out var t) && t != null)
            {
                // An int compare on every hit; the sweep runs at most once a frame while over the cap (with every thumbnail on screen drawn in the
                // last two frames there is nothing to drop, and sweeping again on every hit found nothing each time).
                if (thumbs.Count > ThumbCap && now != lastEvictFrame) EvictThumbs();
                return t;
            }
            Texture2D tex = Load(thumbPath) ?? Decode(ThumbFromFull(thumbPath, fullPath), thumbPath);
            if (tex == null) return null;
            thumbs[thumbPath] = tex;
            if (thumbs.Count > ThumbCap) EvictThumbs();
            return tex;
        }

        public static int ThumbCount => thumbs.Count;

        internal static Texture2D ThumbDeferred(string thumbPath, string fullPath)
        {
            if (thumbPath.NullOrEmpty()) return null;
            thumbLastUse[thumbPath] = Time.frameCount;
            if (thumbs.TryGetValue(thumbPath, out var texture) && texture != null) return texture;
            if (!IsMissing(thumbPath)) PhotoThumbnailLoader.Enqueue(thumbPath, fullPath);
            return null;
        }

        internal static bool ThumbRecentlyUsed(string path) => thumbLastUse.TryGetValue(path, out int frame) && Time.frameCount - frame <= 2;

        internal static void CompleteThumb(string path, PhotoPixels pixels, byte[] encoded, bool derived, string error)
        {
            if (thumbs.TryGetValue(path, out var existing) && existing != null) return;
            Texture2D texture = null;
            try
            {
                if (pixels != null) texture = Upload(pixels);
                else if (encoded != null)
                {
                    if (derived)
                    {
                        byte[] thumbnail = null;
                        PhotoCapture.MakeThumb(encoded, ref thumbnail);
                        encoded = thumbnail;
                    }
                    texture = Decode(encoded, path);
                }
                if (texture == null)
                {
                    RememberMissing(path);
                    if (!error.NullOrEmpty()) Log.Warning("[MomentFrames] thumbnail unavailable: " + error);
                    return;
                }
                thumbs[path] = texture;
                if (derived) ThumbsMade++;
                missing.Remove(path);
                if (thumbs.Count > ThumbCap) EvictThumbs();
            }
            catch (Exception e)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                RememberMissing(path);
                Log.Warning("[MomentFrames] thumbnail unavailable: " + e.Message);
            }
            if (!error.NullOrEmpty()) Log.Warning("[MomentFrames] thumbnail unavailable: " + error);
        }

        private static void EvictThumbs()
        {
            int now = Time.frameCount;
            lastEvictFrame = now;
            var victims = thumbs.Keys
                .Where(k => !thumbLastUse.TryGetValue(k, out int f) || now - f > 2)   // never the ones drawn in the last two frames
                .OrderBy(k => thumbLastUse.TryGetValue(k, out int f) ? f : -1)
                .Take(thumbs.Count - ThumbCap + ThumbCap / 8)
                .ToList();
            foreach (var k in victims)
            {
                if (thumbs[k] != null) UnityEngine.Object.Destroy(thumbs[k]);
                thumbs.Remove(k);
                thumbLastUse.Remove(k);
            }
        }

        public static void ForgetMissing() => missing.Clear();

        private static bool IsMissing(string path)
        {
            if (!missing.TryGetValue(path, out float retryAt)) return false;
            if (Time.realtimeSinceStartup < retryAt) return true;
            missing.Remove(path);
            return false;
        }

        private static void RememberMissing(string path) => missing[path] = Time.realtimeSinceStartup + MissingRetrySeconds;

        /// <summary>Forgets one path: its "missing" mark and its cached thumbnail (an edited version was just written or deleted there). Full
        /// textures still referenced by a holder stay until that holder releases them.</summary>
        public static void Forget(string path)
        {
            if (path.NullOrEmpty()) return;
            FramePhotoTextures.Forget(path);
            PhotoThumbnailLoader.Forget(path);
            missing.Remove(path);
            if (full.TryGetValue(path, out var existing))
            {
                if (existing.refs == 0) DestroyFull(path, existing);
                else existing.retire = true;
            }
            if (thumbs.TryGetValue(path, out var t))
            {
                if (t != null) UnityEngine.Object.Destroy(t);
                thumbs.Remove(path);
                thumbLastUse.Remove(path);
            }
        }

        /// <summary>Destroys every cached texture/material. Called when a game is created or loaded.</summary>
        public static void Clear()
        {
            FramePhotoTextures.Clear();
            PhotoThumbnailLoader.Reset();
            PhotoPreviews.Reset();
            foreach (var e in full.Values)
            {
                if (e.mat != null) UnityEngine.Object.Destroy(e.mat);
                if (e.tex != null) UnityEngine.Object.Destroy(e.tex);
            }
            full.Clear();
            foreach (var t in thumbs.Values)
                if (t != null) UnityEngine.Object.Destroy(t);
            thumbs.Clear();
            thumbLastUse.Clear();
            missing.Clear();
        }

        /// <summary>Makes sure the thumbnail file exists, writing one from the full image when it is missing (photos from before thumbnails
        /// existed, or a thumbnail file that was deleted). True when there is a thumbnail file to use.</summary>
        public static bool EnsureThumb(string thumbPath, string fullPath)
        {
            if (thumbPath.NullOrEmpty()) return false;
            if (File.Exists(thumbPath)) return true;
            return ThumbFromFull(thumbPath, fullPath) != null && File.Exists(thumbPath);
        }

        /// <summary>A thumbnail made from the full image (PNG bytes), also written next to it so this happens once. Null when the full image is
        /// missing or unreadable; the bytes are returned even when the folder cannot be written to.</summary>
        private static byte[] ThumbFromFull(string thumbPath, string fullPath)
        {
            if (fullPath.NullOrEmpty() || IsMissing(fullPath)) return null;
            try
            {
                if (!File.Exists(fullPath))
                {
                    RememberMissing(fullPath);
                    return null;
                }
                byte[] thumb = null;
                PhotoCapture.MakeThumb(File.ReadAllBytes(fullPath), ref thumb);
                if (thumb == null)
                {
                    RememberMissing(fullPath);
                    return null;
                }
                ThumbsMade++;
                try
                {
                    File.WriteAllBytes(thumbPath, thumb);
                    missing.Remove(thumbPath);
                }
                catch (Exception e) { Log.Warning("[MomentFrames] could not save a thumbnail at " + thumbPath + ": " + e.Message); }
                return thumb;
            }
            catch (Exception e)
            {
                Log.Warning("[MomentFrames] could not make a thumbnail from " + fullPath + ": " + e.Message);
                RememberMissing(fullPath);
                return null;
            }
        }

        private static Texture2D Load(string path)
        {
            if (path.NullOrEmpty() || IsMissing(path)) return null;
            try
            {
                if (!File.Exists(path))
                {
                    RememberMissing(path);
                    return null;
                }
                return Decode(File.ReadAllBytes(path), path);
            }
            catch (Exception e)
            {
                Log.Warning("[MomentFrames] could not load " + path + ": " + e.Message);
                RememberMissing(path);
                return null;
            }
        }

        /// <summary>A display texture from pixels decoded on a worker (album thumbnails, the zoom view). On 1.7's engine raw RGB24 SetPixelData
        /// uploads black (every thumbnail in the album, chronicle and map pins, 2026-10-18), so there the pixels go through SetPixels32, which
        /// the editor preview already uses on both engines. 1.6 keeps the direct copy.</summary>
        internal static Texture2D Upload(PhotoPixels pixels)
        {
            var tex = new Texture2D(pixels.Width, pixels.Height, TextureFormat.RGB24, true);
            if (RuntimeAudio.Waylith)
            {
                byte[] rgb = pixels.Rgb;
                var px = new Color32[pixels.Width * pixels.Height];
                for (int i = 0, j = 0; i < px.Length; i++, j += 3) px[i] = new Color32(rgb[j], rgb[j + 1], rgb[j + 2], 255);
                tex.SetPixels32(px);
            }
            else tex.SetPixelData(pixels.Rgb, 0);
            tex.Apply(true, true);
            tex.filterMode = FilterMode.Trilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }

        private static Texture2D Decode(byte[] bytes, string path)
        {
            if (bytes == null) return null;
            // Display only: mipmaps so a large photo shrinks smoothly in the gallery/viewer, and no CPU copy kept (markNonReadable).
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, true);
            if (!tex.LoadImage(bytes, true))
            {
                UnityEngine.Object.Destroy(tex);
                RememberMissing(path);
                return null;
            }
            tex.filterMode = FilterMode.Trilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }
    }

    /// <summary>The photo one map display shows (a frame, one collage window, the projector's slide) and its material. Displays are drawn every
    /// frame, so the image file is looked up on disk only when the photo, its shown version or the photo folder changes (a failed lookup is
    /// retried after two seconds). Before, every frame on screen checked the disk and rebuilt its paths on every frame.</summary>
    public sealed class PhotoBinding
    {
        private string boundId, boundVersion, boundFolder, boundPath;
        private bool boundThumbnailOnly;
        private int retryFrame = -1;

        public Material Mat { get; private set; }

        /// <summary>The image file drawn (the thumbnail when there is one), or null.</summary>
        public string BoundPath => boundPath;

        /// <summary>Probe diagnostics: how many times any display went to the disk for its image.</summary>
        public static int Lookups;

        public void Bind(PhotoRecord rec, bool thumbnailOnly = false)
        {
            if (boundPath != null && !PhotoTextureCache.Owns(boundPath, Mat)) Reset();   // a cache clear can already have rebound this path for another holder
            var store = PhotoStore.Instance;
            if (rec == null || !rec.HasFile || store == null)
            {
                if (boundId != null || boundPath != null) Unbind();
                return;
            }
            string version = rec.Shown?.file ?? rec.file, folder = store.FolderPath;
            if (rec.id == boundId && version == boundVersion && folder == boundFolder && thumbnailOnly == boundThumbnailOnly && (Mat != null || Time.frameCount < retryFrame)) return;
            Unbind();
            boundId = rec.id;
            boundVersion = version;
            boundFolder = folder;
            boundThumbnailOnly = thumbnailOnly;
            Lookups++;
            string full = store.FilePath(rec), thumb = store.ThumbPath(rec);
            string path = PhotoTextureCache.EnsureThumb(thumb, full) ? thumb : thumbnailOnly ? null : full;
            Mat = PhotoTextureCache.Acquire(path);
            if (Mat != null) boundPath = path;
            else retryFrame = Time.frameCount + 120;
        }

        public void Unbind()
        {
            if (boundPath != null) PhotoTextureCache.Release(boundPath);
            Reset();
        }

        private void Reset()
        {
            boundId = boundVersion = boundFolder = boundPath = null;
            Mat = null;
            retryFrame = -1;
        }
    }
}
