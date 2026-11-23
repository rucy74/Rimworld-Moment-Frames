using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MomentFrames
{
    internal enum ExternalPhotoKind { OtherDay, NewImage, ChangedImage, MissingImage }
    internal sealed class ExternalPhotoEntry
    {
        internal string file, hash, recordId, editId, kind, title, note, date, dateSlug;
        internal int year, ticks = -1;
        internal float aspect;
        internal bool Exact;
    }
    internal sealed class ExternalPhotoCandidate
    {
        internal string path, file, hash, recordId, editId;
        internal ExternalPhotoKind kind;
        internal ExternalPhotoEntry history;
        internal bool duplicate;
        internal string Key => file + ":" + hash;
    }

    /// <summary>A just-taken photo's files, written off the game thread before anyone has decided to keep it. The picture and its thumbnail wait under
    /// temporary names and take their final names on the game thread; the protected copy is already in place. A photo that is turned away gives everything
    /// back with Release, which removes only what this request added: a copy an earlier photo of the same picture owns stays.</summary>
    internal sealed class PreparedCapture
    {
        internal string folder, hash, relative, image, thumb, copy;
        internal bool madeCopy, madeCopyThumb, madePreview;
        internal bool In(string current) => string.Equals(folder, current, StringComparison.OrdinalIgnoreCase);
        private static void Remove(string path)
        { if (path != null) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        internal void Release(bool keepCopy = false)
        {
            Remove(image); Remove(thumb);
            image = thumb = null;
            if (keepCopy || copy == null) return;
            if (madeCopy) Remove(copy);
            if (madeCopyThumb) Remove(Path.ChangeExtension(copy, ".thumb.png"));
            if (madePreview) Remove(PhotoPreviews.PathFor(copy));
            madeCopy = madeCopyThumb = madePreview = false;
        }
    }

    /// <summary>Folder comparison and immutable baseline storage. Scanning reads only; commit creates new files and removes only its own incomplete files.</summary>
    internal static class ExternalPhotoFiles
    {
        internal const string LedgerFile = ".mf-photo-history.tsv", BaselineFolder = ".mf-originals";
        internal const int MaximumSide = 8192;
        internal const long MaximumBytes = 67108864;
        internal static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal static string HashFile(string path)
        { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static string HashFile(string path, CancellationToken cancellation)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
            {
                var buffer = new byte[65536];
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = stream.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        internal static bool SafeName(string file) => !string.IsNullOrWhiteSpace(file) && file == Path.GetFileName(file) && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        internal static string RelativeImage(string folder,string path)
        {
            string root=Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(path);
            if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Album image is outside the colony folder");
            return string.Join("/",full.Substring(root.Length).Split(new[]{Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar},StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        }
        internal static bool Png(byte[] bytes, out float aspect)
            => Png(bytes, out aspect, out _);
        /// <summary>A PNG or JPEG with a sane size (the name is from when every original was a PNG). Aspect is width / height.</summary>
        internal static bool Png(byte[] bytes, out float aspect, out bool tooLarge)
        {
            aspect = 0f;
            tooLarge = false;
            if (PhotoJpeg.Size(bytes, out int jpegWidth, out int jpegHeight))
            {
                tooLarge = bytes.LongLength > MaximumBytes || jpegWidth > MaximumSide || jpegHeight > MaximumSide;
                if (tooLarge) return false;
                aspect = jpegWidth / (float)jpegHeight; return true;
            }
            byte[] signature = {137,80,78,71,13,10,26,10};
            if (bytes == null || bytes.Length < 33 || !signature.SequenceEqual(bytes.Take(8))
                || bytes[8]!=0||bytes[9]!=0||bytes[10]!=0||bytes[11]!=13
                || bytes[12]!=73||bytes[13]!=72||bytes[14]!=68||bytes[15]!=82) return false;
            long width = 0, height = 0;
            for (int i = 16; i < 20; i++) width = (width << 8) | bytes[i];
            for (int i = 20; i < 24; i++) height = (height << 8) | bytes[i];
            if (width <= 0 || height <= 0) return false;
            tooLarge = bytes.LongLength > MaximumBytes || width > MaximumSide || height > MaximumSide;
            if (tooLarge) return false;
            aspect = width / (float)height; return true;
        }
        internal static string Preserve(string folder, string expectedHash, byte[] bytes, byte[] thumb = null)
        {
            if (Hash(bytes) != expectedHash) throw new InvalidDataException("Image changed while preserving its baseline");
            string relative = Path.Combine(BaselineFolder, expectedHash + PhotoImageFormat.ExtensionOf(bytes)), path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path))
            {
                bool created = false;
                try { using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { created = true; stream.Write(bytes, 0, bytes.Length); } }
                catch { if (created) try { File.Delete(path); } catch { } throw; }
            }
            if (HashFile(path) != expectedHash) throw new InvalidDataException("Preserved baseline checksum mismatch");
            // A baseline thumbnail belongs to these exact source bytes, rather than an externally replaced visible filename.
            string thumbPath=Path.ChangeExtension(path,".thumb.png");
            if(thumb!=null&&!File.Exists(thumbPath))
            {
                bool created=false;
                try { using(var stream=new FileStream(thumbPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){created=true;stream.Write(thumb,0,thumb.Length);} }
                catch { if(created)try{File.Delete(thumbPath);}catch{} throw; }
            }
            return relative;
        }
        /// <summary>Everything a new photo writes, on the worker that encoded it. All or nothing: on a failure it gives back what it made and throws,
        /// and the game thread writes the photo the ordinary way, which also reports what went wrong.</summary>
        internal static PreparedCapture Prepare(string folder, byte[] png, byte[] thumb, byte[] preview)
        {
            var prepared = new PreparedCapture { folder = folder, hash = Hash(png) };
            try
            {
                Directory.CreateDirectory(folder);
                string stamp = Path.Combine(folder, ".mf-capture-" + Guid.NewGuid().ToString("N"));
                prepared.image = stamp + ".png.tmp";
                using (var stream = new FileStream(prepared.image, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(png, 0, png.Length);
                if (thumb != null)
                {
                    prepared.thumb = stamp + ".thumb.png.tmp";
                    using (var stream = new FileStream(prepared.thumb, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(thumb, 0, thumb.Length);
                }
                prepared.copy = Path.Combine(folder, BaselineFolder, prepared.hash + PhotoImageFormat.ExtensionOf(png));
                prepared.madeCopy = !File.Exists(prepared.copy);
                prepared.madeCopyThumb = thumb != null && !File.Exists(Path.ChangeExtension(prepared.copy, ".thumb.png"));
                prepared.relative = Preserve(folder, prepared.hash, png, thumb);
                string previewPath = PhotoPreviews.PathFor(prepared.copy);
                if (preview != null && !File.Exists(previewPath))
                {
                    PhotoFiles.WriteOriginal(previewPath, preview);
                    prepared.madePreview = true;
                }
                return prepared;
            }
            catch { prepared.Release(); throw; }
        }
        private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
        internal static List<ExternalPhotoEntry> History(string folder)
        {
            var entries = new List<ExternalPhotoEntry>(); string path = Path.Combine(folder, LedgerFile);
            if (!File.Exists(path)) return entries;
            foreach (string line in File.ReadLines(path))
            {
                try
                {
                    var f = line.Split('\t'); if (f.Length != 13 || f[0] != "1") continue;
                    var entry = new ExternalPhotoEntry { file = Decode(f[1]), hash = f[2], recordId = Decode(f[3]), editId = Decode(f[4]), kind = Decode(f[5]), title = Decode(f[6]), note = Decode(f[7]), date = Decode(f[8]), dateSlug = Decode(f[9]), year = int.Parse(f[10], CultureInfo.InvariantCulture), ticks = int.Parse(f[11], CultureInfo.InvariantCulture), aspect = float.Parse(f[12], CultureInfo.InvariantCulture), Exact = true };
                    if (SafeName(entry.file) && entry.hash.Length == 64 && entry.hash.All(Uri.IsHexDigit)
                        && !float.IsNaN(entry.aspect) && !float.IsInfinity(entry.aspect) && entry.aspect > 0f) entries.Add(entry);
                }
                catch (FormatException) { } // An incomplete final ledger line never makes an image authoritative.
                catch (OverflowException) { }
            }
            return entries;
        }
        internal static void Append(string folder, ExternalPhotoEntry e)
        {
            if (!SafeName(e.file)) throw new InvalidDataException("Photo ledger requires a local filename");
            string line = string.Join("\t", new[] {"1",Encode(e.file),e.hash,Encode(e.recordId),Encode(e.editId),Encode(e.kind),Encode(e.title),Encode(e.note),Encode(e.date),Encode(e.dateSlug),e.year.ToString(CultureInfo.InvariantCulture),e.ticks.ToString(CultureInfo.InvariantCulture),e.aspect.ToString("R",CultureInfo.InvariantCulture)}) + "\n";
            Directory.CreateDirectory(folder); File.AppendAllText(Path.Combine(folder, LedgerFile), line, new UTF8Encoding(false));
        }
        internal static List<ExternalPhotoCandidate> Scan(string folder, IEnumerable<ExternalPhotoEntry> current, IEnumerable<ExternalPhotoEntry> history,
            CancellationToken cancellation = default, Action<int> progress = null)
        {
            cancellation.ThrowIfCancellationRequested();
            var refs = (current ?? Enumerable.Empty<ExternalPhotoEntry>()).Where(e => SafeName(e.file)).ToList();
            var byFile = refs.GroupBy(e => e.file, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var knownHashes = new HashSet<string>(refs.Where(e => !string.IsNullOrEmpty(e.hash)).Select(e => e.hash), StringComparer.OrdinalIgnoreCase);
            var old = (history ?? Enumerable.Empty<ExternalPhotoEntry>()).ToList();
            var result = new List<ExternalPhotoCandidate>();
            int checkedFiles = 0;
            if (!Directory.Exists(folder)) return result;
            foreach (string path in PhotoImageFormat.ImageFiles(folder).ToList())
            {
                cancellation.ThrowIfCancellationRequested();
                string file = Path.GetFileName(path);
                string hash = cancellation.CanBeCanceled ? HashFile(path, cancellation) : HashFile(path);
                progress?.Invoke(++checkedFiles);
                if (byFile.TryGetValue(file, out var reference))
                {
                    // Legacy images without a known baseline cannot be claimed to have been edited.
                    if (string.IsNullOrEmpty(reference.hash) || reference.hash == hash) continue;
                    result.Add(new ExternalPhotoCandidate { path=path,file=file,hash=hash,recordId=reference.recordId,editId=reference.editId,kind=ExternalPhotoKind.ChangedImage,history=reference,duplicate=knownHashes.Contains(hash) });
                    continue;
                }
                var prior = old.LastOrDefault(e => string.Equals(e.file,file,StringComparison.OrdinalIgnoreCase) && e.hash == hash);
                result.Add(new ExternalPhotoCandidate { path=path,file=file,hash=hash,kind=prior != null ? ExternalPhotoKind.OtherDay : ExternalPhotoKind.NewImage,history=prior,duplicate=knownHashes.Contains(hash) });
            }
            foreach (var reference in refs.Where(e => !File.Exists(Path.Combine(folder,e.file))))
            {
                cancellation.ThrowIfCancellationRequested();
                result.Add(new ExternalPhotoCandidate { file=reference.file,recordId=reference.recordId,editId=reference.editId,kind=ExternalPhotoKind.MissingImage,history=reference });
            }
            return result;
        }
        internal static string WriteNew(string folder, string stem, byte[] png, byte[] thumb, Action<string> register)
        {
            var created = new List<string>();
            try
            {
                Directory.CreateDirectory(folder); string file = stem + "_" + Guid.NewGuid().ToString("N") + PhotoImageFormat.ExtensionOf(png);
                string path=Path.Combine(folder,file); Write(path,png,created);
                if (thumb != null) Write(Path.Combine(folder,Path.GetFileNameWithoutExtension(file)+".thumb.png"),thumb,created);
                register(file); return file;
            }
            catch
            {
                foreach (string path in created) try { File.Delete(path); } catch { }
                throw;
            }
        }
        private static void Write(string path, byte[] data, List<string> created)
        {
            using (var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
            { created.Add(path); stream.Write(data,0,data.Length); }
        }
    }
}
