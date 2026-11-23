using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Shared, fixed-resolution map prints. RGB decoding/resizing runs on one worker, with at most one upload per frame.
    /// The 64 MiB budget evicts unused prints; live holders are protected. No files are written and no work runs per simulation tick.</summary>
    internal static class FramePhotoTextures
    {
        internal sealed class Entry
        {
            internal string Key, Path;
            internal int Height, Refs, LastUse;
            internal bool Retired, Finished;
            internal Material Material;
            internal double DecodeMs, UploadMs;
            internal long Bytes;
        }
        private sealed class Result { internal PhotoPixels Pixels; internal byte[] Encoded; internal string Error; internal double Milliseconds; }
        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<Entry> queue = new Queue<Entry>();
        private static Task<Result> worker;
        private static Entry working;
        private static int uploadFrame = -1;
        private static int currentHeight;
        internal const long Budget = 64L * 1024 * 1024;
        internal static int Uploads { get; private set; }
        internal static int Count => entries.Count;
        internal static long Bytes => entries.Values.Sum(e => e.Bytes);
        internal static int Normalize(int height) => height == 256 || height == 1024 ? height : 512;
        internal static void EnsureQuality(int height)
        {
            if (currentHeight == height) return;
            Clear(); currentHeight = height; // Release old-resolution copies even when switching down to thumbnail mode.
        }
        internal static int SideFor(int width, int height, int targetHeight) =>
            Math.Min(targetHeight * 3, Math.Max(targetHeight, (int)Math.Round(targetHeight * (double)width / height)));

        internal static Entry Acquire(string path, int height)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = path + "|" + height;
            if (entries.TryGetValue(key, out var found)) { found.Refs++; found.LastUse = Time.frameCount; return found; }
            if (queue.Count >= 32) return null;
            // A quality change must not keep three unused copies of every image.
            foreach (var old in entries.Values.Where(e => e.Refs == 0 && string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray()) Remove(old);
            var entry = new Entry { Key = key, Path = path, Height = height, Refs = 1, LastUse = Time.frameCount };
            entries.Add(key, entry); queue.Enqueue(entry); Evict(); return entry;
        }
        internal static void Release(Entry entry)
        {
            if (entry == null) return;
            entry.Refs = Math.Max(0, entry.Refs - 1); entry.LastUse = Time.frameCount;
            if (entry.Refs == 0 && (entry.Retired || !entry.Finished || entry.Material == null)) Remove(entry);
            Evict();
        }
        private static void Remove(Entry entry)
        {
            if (entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(entry, current)) entries.Remove(entry.Key);
            entry.Retired = true;
            if (entry.Refs == 0) Destroy(entry);
        }
        private static void Destroy(Entry entry)
        {
            if (entry.Material != null)
            {
                UnityEngine.Object.Destroy(entry.Material.mainTexture);
                UnityEngine.Object.Destroy(entry.Material);
            }
            entry.Material = null; entry.Bytes = 0;
        }
        private static void Evict()
        {
            long bytes = Bytes; int count = entries.Count;
            if (bytes <= Budget && count <= 64) return;
            foreach (var e in entries.Values.Where(e => e.Refs == 0).OrderBy(e => e.LastUse).ToArray())
            {
                bytes -= e.Bytes; count--; Remove(e);
                if (bytes <= Budget && count <= 64) break;
            }
        }
        internal static void Forget(string path)
        {
            foreach (var e in entries.Values.Where(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray()) Remove(e);
        }
        internal static void Clear()
        {
            foreach (var entry in entries.Values) { entry.Retired = true; Destroy(entry); }
            entries.Clear(); queue.Clear();
            // The running worker only owns immutable bytes. Its retired result is discarded on completion.
        }
        internal static void Update()
        {
            if (worker == null && queue.Count == 0) return;
            if (worker != null)
            {
                if (!worker.IsCompleted || uploadFrame == Time.frameCount) return;
                Result result = worker.GetAwaiter().GetResult(); worker = null;
                if (!working.Retired && working.Refs > 0)
                {
                    uploadFrame = Time.frameCount;
                    Texture2D texture = null;
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        if (result.Error != null) throw new InvalidDataException(result.Error);
                        texture = result.Pixels != null ? PhotoTextureCache.Upload(result.Pixels) : UploadOtherPng(result.Encoded, working.Height);
                        working.Material = new Material(ShaderDatabase.Cutout) { mainTexture = texture };
                        working.Bytes = (long)texture.width * texture.height * 6; // Conservative RGB/RGBA + mipmap budget.
                        texture = null; Uploads++;
                    }
                    catch (Exception e) { Log.WarningOnce("[MomentFrames] frame quality unavailable; using thumbnail: " + e.Message, 0x4D464651); }
                    finally
                    {
                        if (texture != null) UnityEngine.Object.Destroy(texture);
                        working.Finished = true; working.DecodeMs = result.Milliseconds; working.UploadMs = clock.Elapsed.TotalMilliseconds;
                    }
                    Evict();
                }
                working = null;
            }
            while (queue.Count > 0)
            {
                var entry = queue.Dequeue();
                if (entry.Retired || entry.Refs == 0) continue;
                working = entry; worker = Task.Run(() => Read(entry.Path, entry.Height)); return;
            }
        }
        private static Result Read(string path, int height)
        {
            var clock = Stopwatch.StartNew(); var result = new Result();
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > ExternalPhotoFiles.MaximumBytes) throw new IOException("Missing or oversized frame photo");
                var bytes = File.ReadAllBytes(path);
                if (!ExternalPhotoFiles.Png(bytes, out _)) throw new InvalidDataException("Invalid frame image");
                var pixels = PhotoImageFormat.DecodeOnWorker(bytes);
                if (pixels != null) result.Pixels = pixels.Resize(SideFor(pixels.Width, pixels.Height, height));
                else result.Encoded = bytes; // Native decoder preserves alpha and supports imported palette/interlaced PNGs and progressive JPEGs.
            }
            catch (Exception e) { result.Error = e.Message; }
            result.Milliseconds = clock.Elapsed.TotalMilliseconds; return result;
        }
        private static Texture2D UploadOtherPng(byte[] bytes, int height)
        {
            Texture2D source = null, bounded = null;
            try
            {
                source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!source.LoadImage(bytes, false)) throw new InvalidDataException("Frame PNG could not be decoded");
                float scale = Mathf.Min(1f, (float)height / source.height, height * 3f / source.width);
                int w = Math.Max(1, Mathf.RoundToInt(source.width * scale)), h = Math.Max(1, Mathf.RoundToInt(source.height * scale));
                var pixels = new Color32[w * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) pixels[y * w + x] = source.GetPixelBilinear((x + .5f) / w, (y + .5f) / h);
                bounded = new Texture2D(w, h, TextureFormat.RGBA32, true);
                bounded.SetPixels32(pixels); bounded.Apply(true, true);
                bounded.filterMode = FilterMode.Trilinear; bounded.wrapMode = TextureWrapMode.Clamp;
                var result = bounded; bounded = null; return result;
            }
            finally
            {
                if (source != null) UnityEngine.Object.Destroy(source);
                if (bounded != null) UnityEngine.Object.Destroy(bounded);
            }
        }
    }

    internal sealed class FramePhotoBinding
    {
        private readonly PhotoBinding thumbnail = new PhotoBinding();
        private FramePhotoTextures.Entry entry;
        private string id, version, folder, path;
        private int height, retryAt;
        internal static int Lookups;
        internal Material Mat => entry != null && !entry.Retired && entry.Material != null ? entry.Material : thumbnail.Mat;
        internal string BoundPath => entry != null && !entry.Retired && entry.Material != null ? path : thumbnail.BoundPath;
        internal void Bind(PhotoRecord record)
        {
            var store = PhotoStore.Instance;
            if (record == null || !record.HasFile || store == null) { Unbind(); return; }
            int desired = FramePhotoTextures.Normalize(MomentFramesMod.Settings.framePhotoHeight);
            FramePhotoTextures.EnsureQuality(desired);
            string shown = record.Shown?.file ?? record.file;
            if (id != record.id || version != shown || folder != store.FolderPath || height != desired || entry?.Retired == true)
            {
                Unbind(); id = record.id; version = shown; folder = store.FolderPath; height = desired;
                path = store.FilePath(record);
                Lookups++;
            }
            if (height > 256)
            {
                if (entry == null && Time.frameCount >= retryAt)
                { entry = FramePhotoTextures.Acquire(path, height); retryAt = Time.frameCount + 120; }
                if (entry?.Material != null) { thumbnail.Unbind(); return; }
                if (entry?.Finished == true && Time.frameCount >= retryAt)
                { FramePhotoTextures.Release(entry); entry = null; retryAt = Time.frameCount + 1800; }
            }
            thumbnail.Bind(record, true);
        }
        internal void Unbind()
        {
            FramePhotoTextures.Release(entry); entry = null; thumbnail.Unbind();
            id = version = folder = path = null; height = retryAt = 0;
        }
    }
}
