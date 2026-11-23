using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>Album misses read/decode on one worker; at most one small texture is uploaded per Update.</summary>
    internal static class PhotoThumbnailLoader
    {
        private sealed class Request
        {
            internal string Thumb, Original;
            internal PhotoPixels Pixels;
            internal byte[] Encoded;
            internal string Error;
            internal bool Derived;
        }
        private static readonly Queue<Request> queue = new Queue<Request>();
        private static readonly Dictionary<string, Request> pending = new Dictionary<string, Request>(StringComparer.OrdinalIgnoreCase);
        private static Task<Request> worker;
        private static int uploadFrame = -1;
        internal static bool Pending(string path) => !string.IsNullOrEmpty(path) && pending.ContainsKey(path);
        internal static void Enqueue(string thumb, string original)
        {
            if (pending.ContainsKey(thumb) || pending.Count >= 32) return;
            var request = new Request { Thumb = thumb, Original = original };
            pending.Add(thumb, request); queue.Enqueue(request);
        }
        private static bool Current(Request request) => pending.TryGetValue(request.Thumb, out var value) && ReferenceEquals(value, request);
        internal static void Forget(string path) => pending.Remove(path);
        internal static void Reset() { pending.Clear(); queue.Clear(); }
        internal static void Update()
        {
            if (worker == null && queue.Count == 0) return;
            if (worker != null)
            {
                if (!worker.IsCompleted || uploadFrame == Time.frameCount) return;
                var request = worker.GetAwaiter().GetResult(); worker = null;
                if (Current(request))
                {
                    pending.Remove(request.Thumb);
                    if (PhotoTextureCache.ThumbRecentlyUsed(request.Thumb))
                    {
                        uploadFrame = Time.frameCount;
                        PhotoTextureCache.CompleteThumb(request.Thumb, request.Pixels, request.Encoded, request.Derived, request.Error);
                    }
                }
            }
            while (queue.Count > 0)
            {
                var request = queue.Dequeue();
                if (!Current(request)) continue;
                if (!PhotoTextureCache.ThumbRecentlyUsed(request.Thumb)) { pending.Remove(request.Thumb); continue; }
                worker = Task.Run(() => Read(request));
                return;
            }
        }
        private static Request Read(Request request)
        {
            try
            {
                string source = File.Exists(request.Thumb) ? request.Thumb : request.Original;
                if (string.IsNullOrEmpty(source) || !File.Exists(source)) return request;
                if (new FileInfo(source).Length > ExternalPhotoFiles.MaximumBytes) throw new IOException("Photo exceeds thumbnail input limit");
                byte[] bytes = File.ReadAllBytes(source);
                if (!ExternalPhotoFiles.Png(bytes, out _)) throw new InvalidDataException("Invalid thumbnail image or image size");
                request.Derived = source != request.Thumb;
                var pixels = PhotoImageFormat.DecodeOnWorker(bytes);
                if (pixels != null)
                {
                    int side = Math.Min(1024, Math.Max(256, (int)Math.Ceiling(256d * pixels.Width / pixels.Height)));
                    request.Pixels = pixels.Resize(side);
                    return request;
                }
                // Imported palette/RGBA PNGs keep Unity's decoder, with upload limited to one image per frame.
                request.Encoded = bytes;
            }
            catch (Exception e) { request.Error = e.Message; }
            return request;
        }
    }
}
