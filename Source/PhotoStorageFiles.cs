using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace MomentFrames
{
    internal enum PhotoFileState { Ready, ProtectedCopy, Missing, Unreadable }
    // Immutable snapshots cross the worker boundary; no game objects, Unity calls or settings on that thread.
    internal sealed class PhotoFileReference
    {
        internal string Folder, RecordId, EditId, File, Baseline, Hash, Title, Date;
        internal string Key => RecordId + ":" + (EditId ?? "");
    }
    internal sealed class PhotoFileStatus
    {
        internal PhotoFileReference Reference;
        internal PhotoFileState State;
        internal string AvailablePath, VerifiedHash, Detail;
    }
    internal sealed class PhotoFileMatch
    {
        internal PhotoFileReference Reference;
        internal string Path;
    }
    internal static class PhotoStorageFiles
    {
        internal static bool HashKnown(string hash) => hash != null && hash.Length == 64 && hash.All(Uri.IsHexDigit);
        internal static string Local(string folder, string file)
        {
            if (string.IsNullOrWhiteSpace(file) || file == "." || file == ".." || file != Path.GetFileName(file)
                || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("Invalid photo filename");
            return Path.Combine(Path.GetFullPath(folder), file);
        }
        internal static string ProtectedPath(PhotoFileReference r)
        {
            if (string.IsNullOrEmpty(r.Baseline)) return null;
            string file = Path.GetFileName(r.Baseline);
            if (Path.GetDirectoryName(r.Baseline) != ".mf-originals" || file.Length != 68
                || !(file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) || !HashKnown(file.Substring(0, 64))
                || HashKnown(r.Hash) && !file.StartsWith(r.Hash, StringComparison.OrdinalIgnoreCase)) return null;
            return Path.Combine(Path.GetFullPath(r.Folder), ".mf-originals", file);
        }
        internal static string HashFile(string path, CancellationToken cancel)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                byte[] buffer = new byte[65536]; int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                { cancel.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, read, buffer, 0); }
                sha.TransformFinalBlock(buffer, 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        private static bool PngHeader(string path) => PhotoImageFormat.ImageHeader(path);   // PNG or JPEG
        private static bool Check(string path, string expected, CancellationToken cancel, out string hash)
        {
            hash = null; cancel.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || !PngHeader(path)) return false;
            hash = HashFile(path, cancel);
            return string.IsNullOrEmpty(expected) || HashKnown(expected) && hash.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        internal static PhotoFileStatus Inspect(PhotoFileReference r, CancellationToken cancel)
        {
            var result = new PhotoFileStatus { Reference = r, State = PhotoFileState.Missing };
            string raw = null, copy = null, rawHash = null, copyHash = null;
            bool rawOK = false, copyOK = false; string error = null;
            try { raw = Local(r.Folder, r.File); rawOK = Check(raw, r.Hash, cancel, out rawHash); }
            catch (IOException e) { error = e.Message; } catch (UnauthorizedAccessException e) { error = e.Message; }
            catch (ArgumentException e) { error = e.Message; }
            try { copy = ProtectedPath(r); copyOK = Check(copy, string.IsNullOrEmpty(r.Hash) && copy != null ? Path.GetFileNameWithoutExtension(copy) : r.Hash, cancel, out copyHash); }
            catch (IOException e) { error = e.Message; } catch (UnauthorizedAccessException e) { error = e.Message; }
            catch (ArgumentException e) { error = e.Message; }
            // A bad referenced baseline would win the normal display resolver, so explicitly offer reconnecting the valid raw image.
            bool badCopy = !string.IsNullOrEmpty(r.Baseline) && copy != null && File.Exists(copy) && !copyOK;
            if (rawOK && !badCopy) { result.State = PhotoFileState.Ready; result.AvailablePath = raw; result.VerifiedHash = rawHash; }
            else if (copyOK || rawOK)
            { result.State = PhotoFileState.ProtectedCopy; result.AvailablePath = copyOK ? copy : raw; result.VerifiedHash = copyOK ? copyHash : rawHash; }
            else if (error != null) result.State = PhotoFileState.Unreadable;
            result.Detail = error;
            return result;
        }
        internal static List<PhotoFileStatus> Scan(IEnumerable<PhotoFileReference> references, CancellationToken cancel, Action<int> progress = null)
        {
            var results = new List<PhotoFileStatus>();
            foreach (var reference in references) { cancel.ThrowIfCancellationRequested(); results.Add(Inspect(reference, cancel)); progress?.Invoke(results.Count); }
            return results;
        }
        internal static List<PhotoFileMatch> Find(string folder, IList<PhotoFileReference> wanted, CancellationToken cancel)
        {
            folder = Path.GetFullPath(folder);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
            var byHash = wanted.Where(r => HashKnown(r.Hash)).GroupBy(r => r.Hash, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var matches = new List<PhotoFileMatch>(); var found = new HashSet<string>();
            if (byHash.Count == 0) return matches;
            foreach (var directory in new[] { folder, Path.Combine(folder, ".mf-originals") })
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var path in PhotoImageFormat.ImageFiles(directory))
                {
                    cancel.ThrowIfCancellationRequested();
                    string hash;
                    try { if (!PngHeader(path)) continue; hash = HashFile(path, cancel); }
                    catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                    if (byHash.TryGetValue(hash, out var refs))
                        foreach (var r in refs) if (found.Add(r.Key)) matches.Add(new PhotoFileMatch { Reference = r, Path = path });
                    if (found.Count == byHash.Values.Sum(v => v.Count)) return matches;
                }
            }
            return matches;
        }
        // Only a new owned file is written; even an externally changed same-name image remains untouched.
        internal static string Restore(PhotoFileReference r, string source, string verifiedHash, CancellationToken cancel)
        {
            if (!HashKnown(verifiedHash) || HashKnown(r.Hash) && !r.Hash.Equals(verifiedHash, StringComparison.OrdinalIgnoreCase)
                || !Check(source, verifiedHash, cancel, out _)) throw new IOException("Photo changed; check again");
            Directory.CreateDirectory(r.Folder);
            string extension = string.Equals(Path.GetExtension(source), ".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            string file = "recovered_" + Guid.NewGuid().ToString("N") + extension;
            string target = Local(r.Folder, file), temp = target + ".tmp";
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[65536]; int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0) { cancel.ThrowIfCancellationRequested(); output.Write(buffer, 0, n); }
                }
                if (!Check(temp, verifiedHash, cancel, out _)) throw new IOException("Recovered copy did not match");
                cancel.ThrowIfCancellationRequested(); File.Move(temp, target); return file;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        internal static void DiscardOwnedCopy(PhotoFileReference r, string file)
        {
            if (file == null || file.Length != 46 || !file.StartsWith("recovered_", StringComparison.Ordinal) || !(file.EndsWith(".png", StringComparison.Ordinal) || file.EndsWith(".jpg", StringComparison.Ordinal))
                || !Guid.TryParseExact(file.Substring(10, file.Length - 14), "N", out _)) return;
            string path = Local(r.Folder, file); if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }
}
