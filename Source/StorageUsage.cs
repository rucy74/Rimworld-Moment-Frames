using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace MomentFrames
{
    /// <summary>How much disk the photographs take, for the settings window (user 2026-11-01: keep the 192 px default but show the space it
    /// uses). Counted on a worker only while the window asks, at most every 30 s or when the folder changes; never per tick.</summary>
    internal static class StorageUsage
    {
        internal sealed class Result
        {
            internal bool HasColony;
            internal int ColonyPhotos, Colonies;
            internal long ColonyBytes, ColonyProtected, RootBytes;
            internal double RecentAverage;   // bytes of the latest ordinary (non-survey) originals in this colony
        }

        private static Task<Result> work;
        private static Result last;
        private static float startedAt = -999f;
        private static string countedRoot, countedColony;

        internal static Result Last => last;

        /// <summary>Picks up a finished count and starts a new one when the last is older than 30 s or counted another folder.</summary>
        internal static void Poll()
        {
            if (work != null && work.IsCompleted) { if (!work.IsFaulted) last = work.Result; work = null; }
            var store = PhotoStore.Instance;
            string colony = store != null && Current.Game != null ? store.FolderPath : null;
            string root = colony != null ? Path.GetDirectoryName(colony) : DefaultRoot();
            if (work != null || (Time.realtimeSinceStartup - startedAt < 30f && root == countedRoot && colony == countedColony)) return;
            countedRoot = root; countedColony = colony; startedAt = Time.realtimeSinceStartup;
            int photos = 0;
            var recent = new List<string>();
            if (colony != null)
            {
                var records = store.AllRecords.Where(r => r.HasFile).ToList();
                photos = records.Count;
                var ordinary = records.Where(r => r.kind != "survey").ToList();
                recent = ordinary.Skip(Math.Max(0, ordinary.Count - 20)).Select(r => Path.Combine(colony, r.file)).ToList();
            }
            work = Task.Run(() => Count(root, colony, photos, recent));
        }

        private static Result Count(string root, string colony, int photos, List<string> recent)
        {
            var result = new Result { HasColony = colony != null, ColonyPhotos = photos };
            if (!root.NullOrEmpty() && Directory.Exists(root))
            {
                foreach (string dir in SafeDirectories(root))
                {
                    long size = Size(dir);
                    if (size <= 0) continue;
                    result.RootBytes += size;
                    result.Colonies++;
                }
            }
            if (colony != null && Directory.Exists(colony))
            {
                result.ColonyBytes = Size(colony);
                string protectedCopies = Path.Combine(colony, ExternalPhotoFiles.BaselineFolder);
                if (Directory.Exists(protectedCopies)) result.ColonyProtected = Size(protectedCopies);
            }
            var lengths = recent.Select(Length).Where(n => n > 0).ToList();
            result.RecentAverage = lengths.Count > 0 ? lengths.Average() : 0;
            return result;
        }

        private static IEnumerable<string> SafeDirectories(string root)
        {
            try { return Directory.GetDirectories(root); }
            catch (Exception) { return Enumerable.Empty<string>(); }
        }

        private static long Size(string dir)
        {
            long total = 0;
            try { foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) total += Length(file); }
            catch (Exception) { }
            return total;
        }

        private static long Length(string file)
        {
            try { return new FileInfo(file).Length; }
            catch (Exception) { return 0; }
        }

        /// <summary>The photo folder root when no game is loaded (main menu): the same rule as PhotoStore.FolderPath without the game id.</summary>
        private static string DefaultRoot()
        {
            string fallback = Path.Combine(GenFilePaths.SaveDataFolderPath, "MomentFrames");
            string setting = MomentFramesMod.Settings?.folderOverride;
            if (setting.NullOrEmpty()) return fallback;
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(setting.Trim())); }
            catch (Exception) { return fallback; }
        }

        internal static string Bytes(double bytes)
        {
            const double MB = 1024d * 1024d, GB = MB * 1024d;
            if (bytes >= GB) return (bytes / GB).ToString("0.00") + "GB";
            return (bytes / MB).ToString(bytes < 10 * MB ? "0.0" : "0") + "MB";
        }
    }
}
