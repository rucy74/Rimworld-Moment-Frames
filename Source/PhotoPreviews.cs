using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Verse;

namespace MomentFrames
{
    internal static class PhotoPreviews
    {
        internal const int Side = 1600;
        private static readonly Queue<string> queue = new Queue<string>();
        private static readonly HashSet<string> scheduled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private sealed class Result { internal byte[] Fallback; internal string Error; }
        private sealed class Retry { internal int Failures; internal DateTime After; }
        private static readonly Dictionary<string, Retry> retries = new Dictionary<string, Retry>(StringComparer.OrdinalIgnoreCase);
        private static Task<Result> worker;
        private static string workingPath;
        private static int workingGeneration;
        private static volatile int generation;
        internal static string PathFor(string original) => Path.Combine(Path.GetDirectoryName(original), Path.GetFileNameWithoutExtension(original) + ".preview.png");
        internal static string DisplayPath(string original, string thumb)
        {
            if (string.IsNullOrEmpty(original)) return null;
            string preview = PathFor(original);
            if (File.Exists(preview)) return preview;
            if (retries.TryGetValue(original, out var retry) && DateTime.UtcNow < retry.After) return thumb;
            if (queue.Count < 16 && !scheduled.Contains(original)) { scheduled.Add(original); queue.Enqueue(original); }
            return thumb; // Immediate existing print while the large PNG is decoded off the game thread.
        }
        internal static void Update()
        {
            if (worker != null)
            {
                if (!worker.IsCompleted) return;
                Result result = worker.GetAwaiter().GetResult();
                worker = null;
                if (workingGeneration == generation)
                {
                    if (result.Fallback != null)
                    {
                        try
                        {
                            byte[] bytes = PhotoPreviewFallback.Make(result.Fallback);
                            string path = workingPath; int lease = workingGeneration;
                            worker = Task.Run(() => Save(path, bytes, lease));
                            return;
                        }
                        catch (Exception e) { result.Error = e.Message; }
                    }
                    scheduled.Remove(workingPath);
                    if (result.Error == null) retries.Remove(workingPath);
                    else
                    {
                        if (!retries.TryGetValue(workingPath, out var retry)) retries[workingPath] = retry = new Retry();
                        retry.Failures = Math.Min(6, retry.Failures + 1);
                        retry.After = DateTime.UtcNow.AddSeconds(Math.Min(30, 1 << (retry.Failures - 1)));
                        if (retry.Failures == 1) Log.Warning("[MomentFrames] preview unavailable; will retry, original retained: " + result.Error);
                    }
                }
            }
            if (queue.Count == 0) return;
            workingPath = queue.Dequeue(); workingGeneration = generation;
            string next = workingPath; int nextLease = workingGeneration;
            worker = Task.Run(() => Read(next, nextLease));
        }
        private static Result Read(string path, int lease)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) throw new FileNotFoundException("Photo is temporarily unavailable", path);
                if (info.Length > ExternalPhotoFiles.MaximumBytes) throw new InvalidDataException("Photo exceeds preview input limit");
                byte[] png = File.ReadAllBytes(path);
                if (!ExternalPhotoFiles.Png(png, out _)) throw new InvalidDataException("Invalid preview source image or image size");
                // Opaque RGB PNGs and sequential JPEGs stay on the worker; the native fallback also preserves imported alpha.
                var pixels = PhotoImageFormat.DecodeOnWorker(png);
                if (pixels == null) return new Result { Fallback = png };
                return Save(path, pixels.Resize(Side).Encode(), lease);
            }
            catch (Exception e) { return new Result { Error = e.Message }; }
        }
        private static Result Save(string path, byte[] bytes, int lease)
        {
            try
            {
                string preview = PathFor(path);
                if (lease == generation && !File.Exists(preview)) PhotoFiles.WriteOriginal(preview, bytes);
                return new Result();
            }
            catch (Exception e) { return new Result { Error = e.Message }; }
        }
        internal static void Remember(string original, byte[] preview)
        {
            if (original.NullOrEmpty() || preview == null || File.Exists(PathFor(original))) return;
            PhotoFiles.TryDerived(PathFor(original), () => preview);
        }
        internal static void Reset() { generation++; queue.Clear(); scheduled.Clear(); retries.Clear(); }
    }
}
