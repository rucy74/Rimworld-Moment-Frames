using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MomentFrames
{
    /// <summary>The file format of new photo originals, and the checks that let every reader take either kind. Originals taken before this
    /// setting existed stay PNG and keep their names; nothing here rewrites a file. Thumbnails and previews are always PNG (small, and the
    /// workers decode them without the engine). Only SaveJpeg reads the settings, so read it on the game thread and hand the bool to workers.</summary>
    internal static class PhotoImageFormat
    {
        internal const int Jpg = 0, Png = 1;
        internal const string JpgExtension = ".jpg", PngExtension = ".png";

        internal static bool SaveJpeg => (MomentFramesMod.Settings?.photoFormat ?? Jpg) != Png;
        internal static string Extension(bool jpeg) => jpeg ? JpgExtension : PngExtension;

        /// <summary>An original in the chosen format. Pure computation; safe on any thread.</summary>
        internal static byte[] Encode(PhotoPixels pixels, bool jpeg) => jpeg ? PhotoJpeg.Encode(pixels, PhotoJpeg.DefaultQuality) : pixels.Encode();

        internal static bool IsPng(byte[] bytes) => bytes != null && bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71
            && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10;
        internal static bool IsJpeg(byte[] bytes) => PhotoJpeg.IsJpeg(bytes);

        /// <summary>The extension that matches the bytes: ".jpg" for a JPEG, ".png" otherwise.</summary>
        internal static string ExtensionOf(byte[] bytes) => IsJpeg(bytes) ? JpgExtension : PngExtension;

        /// <summary>A photo image file name (original or edit), not a thumbnail or a preview.</summary>
        internal static bool IsImageName(string file)
        {
            if (string.IsNullOrEmpty(file)) return false;
            if (file.EndsWith(".thumb.png", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".preview.png", StringComparison.OrdinalIgnoreCase)) return false;
            string extension = Path.GetExtension(file);
            return extension.Equals(PngExtension, StringComparison.OrdinalIgnoreCase) || extension.Equals(JpgExtension, StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Photo image files directly in a folder (no thumbnails or previews), in name order.</summary>
        internal static IEnumerable<string> ImageFiles(string folder) => Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(p => IsImageName(Path.GetFileName(p))).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether the first bytes of a file are a PNG or JPEG header with a size (without reading the whole file).</summary>
        internal static bool ImageHeader(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var head = new byte[Math.Min(stream.Length, 65536)];
                int read = 0, n;
                while (read < head.Length && (n = stream.Read(head, read, head.Length - read)) > 0) read += n;
                if (read < head.Length) Array.Resize(ref head, read);
                if (IsPng(head))
                    return head.Length >= 24 && head[12] == 73 && head[13] == 72 && head[14] == 68 && head[15] == 82
                        && (head[16] | head[17] | head[18] | head[19]) != 0 && (head[20] | head[21] | head[22] | head[23]) != 0;
                return PhotoJpeg.Size(head, out _, out _);
            }
        }

        /// <summary>Pixels decoded on the calling (worker) thread for an 8-bit RGB PNG or a sequential JPEG; null for anything else (palette,
        /// alpha, interlaced PNGs, progressive JPEGs), which the engine's decoder then takes on the game thread.</summary>
        internal static PhotoPixels DecodeOnWorker(byte[] bytes)
        {
            try
            {
                if (IsPng(bytes)) return bytes.Length >= 33 && bytes[24] == 8 && bytes[25] == 2 && bytes[28] == 0 ? PhotoPixels.Decode(bytes) : null;
                if (IsJpeg(bytes)) return PhotoJpeg.Decode(bytes);
            }
            catch (InvalidDataException) { }
            return null;
        }
    }
}
