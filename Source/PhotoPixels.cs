using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MomentFrames
{
    // Plain RGB buffers, bottom row first (Unity ReadPixels orientation). No Unity/Verse calls on workers.
    internal sealed class PhotoPixels
    {
        internal readonly int Width, Height;
        internal readonly byte[] Rgb;
        internal PhotoPixels(int width, int height, byte[] rgb)
        {
            if (width < 1 || height < 1 || width > 8192 || height > 8192 || rgb?.Length != checked(width * height * 3))
                throw new InvalidDataException("Invalid photo pixel buffer");
            Width = width; Height = height; Rgb = rgb;
        }
        internal PhotoPixels Resize(int side)
        {
            double scale = Math.Min(1d, side / (double)Math.Max(Width, Height));
            int w = Math.Max(1, (int)Math.Round(Width * scale)), h = Math.Max(1, (int)Math.Round(Height * scale));
            if (w == Width && h == Height) return this;
            byte[] output = new byte[w * h * 3];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                double sx = (x + .5) * Width / w - .5, sy = (y + .5) * Height / h - .5;
                int x0 = Math.Max(0, (int)Math.Floor(sx)), y0 = Math.Max(0, (int)Math.Floor(sy));
                int x1 = Math.Min(Width - 1, x0 + 1), y1 = Math.Min(Height - 1, y0 + 1);
                double fx = Math.Max(0, sx - x0), fy = Math.Max(0, sy - y0);
                for (int c = 0; c < 3; c++)
                {
                    double lo = Rgb[(y0 * Width + x0) * 3 + c] * (1 - fx) + Rgb[(y0 * Width + x1) * 3 + c] * fx;
                    double hi = Rgb[(y1 * Width + x0) * 3 + c] * (1 - fx) + Rgb[(y1 * Width + x1) * 3 + c] * fx;
                    output[(y * w + x) * 3 + c] = (byte)Math.Round(lo * (1 - fy) + hi * fy);
                }
            }
            return new PhotoPixels(w, h, output);
        }
        private static readonly byte[] Signature = {137,80,78,71,13,10,26,10};
        private static readonly uint[] CrcTable = MakeCrc();
        private static uint[] MakeCrc()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++) { uint n = i; for (int b = 0; b < 8; b++) n = (n & 1) != 0 ? 0xedb88320 ^ (n >> 1) : n >> 1; table[i] = n; }
            return table;
        }
        private static void U32(Stream s, uint n) { s.WriteByte((byte)(n >> 24)); s.WriteByte((byte)(n >> 16)); s.WriteByte((byte)(n >> 8)); s.WriteByte((byte)n); }
        private static uint U32(byte[] b, int i) => (uint)b[i] << 24 | (uint)b[i+1] << 16 | (uint)b[i+2] << 8 | b[i+3];
        private static void Chunk(Stream stream, string name, byte[] bytes)
        {
            U32(stream, (uint)bytes.Length); byte[] type = Encoding.ASCII.GetBytes(name); stream.Write(type, 0, 4); stream.Write(bytes, 0, bytes.Length);
            uint crc = 0xffffffff; foreach (byte b in type) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
            foreach (byte b in bytes) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8); U32(stream, crc ^ 0xffffffff);
        }
        internal byte[] Encode()
        {
            using (var output = new MemoryStream()) using (var data = new MemoryStream())
            {
                output.Write(Signature, 0, 8);
                using (var header = new MemoryStream()) { U32(header, (uint)Width); U32(header, (uint)Height); header.Write(new byte[]{8,2,0,0,0},0,5); Chunk(output,"IHDR",header.ToArray()); }
                data.WriteByte(0x78); data.WriteByte(0x01); uint a = 1, b = 0;
                using (var zip = new DeflateStream(data, CompressionLevel.Fastest, true))
                {
                    var row = new byte[Width * 3 + 1]; row[0] = 1; // Sub filter preserves pixels while compressing flat terrain well.
                    for (int y = Height - 1; y >= 0; y--)
                    {
                        int start = y * Width * 3;
                        for (int x = 0; x < Width * 3; x++) row[x+1] = unchecked((byte)(Rgb[start+x] - (x >= 3 ? Rgb[start+x-3] : 0)));
                        // Adler-32 can defer its divisions for at most 5552 bytes without overflowing uint.
                        for (int at = 0; at < row.Length;)
                        {
                            int end = Math.Min(row.Length, at + 5552);
                            while (at < end) { a += row[at++]; b += a; }
                            a %= 65521; b %= 65521;
                        }
                        zip.Write(row, 0, row.Length);
                    }
                }
                U32(data, b << 16 | a); Chunk(output,"IDAT",data.ToArray()); Chunk(output,"IEND",Array.Empty<byte>()); return output.ToArray();
            }
        }
        internal static PhotoPixels Decode(byte[] png)
        {
            if (png == null || png.Length < 33) throw new InvalidDataException("PNG missing");
            for (int i = 0; i < 8; i++) if (png[i] != Signature[i]) throw new InvalidDataException("PNG signature");
            int w = checked((int)U32(png,16)), h = checked((int)U32(png,20)), channels = png[25] == 2 ? 3 : png[25] == 6 ? 4 : 0;
            if (w < 1 || h < 1 || w > 8192 || h > 8192 || png[24] != 8 || channels == 0 || png[28] != 0)
                throw new InvalidDataException("Preview supports non-interlaced 8-bit RGB/RGBA PNGs");
            using (var packed = new MemoryStream())
            {
                for (int at = 8; at + 12 <= png.Length;)
                {
                    int n = checked((int)U32(png,at)); if (n < 0 || n > png.Length-at-12) throw new InvalidDataException("PNG chunk length");
                    if (Encoding.ASCII.GetString(png,at+4,4) == "IDAT") packed.Write(png,at+8,n);
                    at += n + 12;
                }
                byte[] compressed = packed.ToArray(); if (compressed.Length < 6) throw new InvalidDataException("PNG data");
                using (var input = new MemoryStream(compressed,2,compressed.Length-6)) using (var zip = new DeflateStream(input,CompressionMode.Decompress))
                {
                    int stride = checked(w*channels); byte[] prior = new byte[stride], row = new byte[stride], rgb = new byte[checked(w*h*3)];
                    for (int y = 0; y < h; y++)
                    {
                        int filter = zip.ReadByte(); if (filter < 0 || filter > 4) throw new InvalidDataException("PNG row filter");
                        for (int at = 0; at < stride;) { int n = zip.Read(row,at,stride-at); if (n == 0) throw new EndOfStreamException(); at += n; }
                        for (int x = 0; x < stride; x++)
                        {
                            int left = x >= channels ? row[x-channels] : 0, up = prior[x], corner = x >= channels ? prior[x-channels] : 0;
                            int predictor = filter == 1 ? left : filter == 2 ? up : filter == 3 ? (left+up)/2 : filter == 4 ? Paeth(left,up,corner) : 0;
                            row[x] = unchecked((byte)(row[x]+predictor));
                        }
                        for (int x = 0; x < w; x++) for (int c = 0; c < 3; c++) rgb[((h-1-y)*w+x)*3+c] = row[x*channels+c];
                        var swap = prior; prior = row; row = swap;
                    }
                    return new PhotoPixels(w,h,rgb);
                }
            }
        }
        private static int Paeth(int a, int b, int c)
        { int p=a+b-c, pa=Math.Abs(p-a), pb=Math.Abs(p-b), pc=Math.Abs(p-c); return pa<=pb&&pa<=pc?a:pb<=pc?b:c; }
    }
}
