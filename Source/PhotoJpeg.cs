using System;
using System.IO;

namespace MomentFrames
{
    /// <summary>Baseline JPEG for photo originals, written and read on worker threads with no Unity or Verse calls, so it behaves the same
    /// in 1.6 (Unity) and 1.7 (whose engine replaced Unity's image code). Writes 4:4:4 (no colour subsampling) with the standard Huffman
    /// tables. Reads sequential Huffman JPEGs (baseline and extended, 8-bit) with 1 or 3 components and sampling up to 2x2; anything else
    /// (progressive, arithmetic coding, 12-bit, CMYK, several scans) throws InvalidDataException, and callers then use the engine's decoder
    /// on the game thread, as they already do for imported PNGs the plain decoder does not cover.</summary>
    internal static class PhotoJpeg
    {
        internal const int DefaultQuality = 98;

        // Natural (row-major) index -> position in zigzag order, and the reverse.
        private static readonly byte[] ZigZag = {
            0,1,5,6,14,15,27,28, 2,4,7,13,16,26,29,42, 3,8,12,17,25,30,41,43, 9,11,18,24,31,40,44,53,
            10,19,23,32,39,45,52,54, 20,22,33,38,46,51,55,60, 21,34,37,47,50,56,59,61, 35,36,48,49,57,58,62,63 };
        private static readonly byte[] Natural = Invert(ZigZag);
        // ITU T.81 Annex K quantization tables, natural order.
        private static readonly byte[] LumaQuant = {
            16,11,10,16,24,40,51,61, 12,12,14,19,26,58,60,55, 14,13,16,24,40,57,69,56, 14,17,22,29,51,87,80,62,
            18,22,37,56,68,109,103,77, 24,35,55,64,81,104,113,92, 49,64,78,87,103,121,120,101, 72,92,95,98,112,100,103,99 };
        private static readonly byte[] ChromaQuant = {
            17,18,24,47,99,99,99,99, 18,21,26,66,99,99,99,99, 24,26,56,99,99,99,99,99, 47,66,99,99,99,99,99,99,
            99,99,99,99,99,99,99,99, 99,99,99,99,99,99,99,99, 99,99,99,99,99,99,99,99, 99,99,99,99,99,99,99,99 };
        // Annex K.3 Huffman tables: code counts per length 1..16, then the symbols.
        private static readonly byte[] DcLumaCounts = { 0,1,5,1,1,1,1,1,1,0,0,0,0,0,0,0 };
        private static readonly byte[] DcChromaCounts = { 0,3,1,1,1,1,1,1,1,1,1,0,0,0,0,0 };
        private static readonly byte[] DcSymbols = { 0,1,2,3,4,5,6,7,8,9,10,11 };
        private static readonly byte[] AcLumaCounts = { 0,2,1,3,3,2,4,3,5,5,4,4,0,0,1,0x7d };
        private static readonly byte[] AcLumaSymbols = {
            0x01,0x02,0x03,0x00,0x04,0x11,0x05,0x12,0x21,0x31,0x41,0x06,0x13,0x51,0x61,0x07,0x22,0x71,0x14,0x32,0x81,0x91,0xa1,0x08,
            0x23,0x42,0xb1,0xc1,0x15,0x52,0xd1,0xf0,0x24,0x33,0x62,0x72,0x82,0x09,0x0a,0x16,0x17,0x18,0x19,0x1a,0x25,0x26,0x27,0x28,
            0x29,0x2a,0x34,0x35,0x36,0x37,0x38,0x39,0x3a,0x43,0x44,0x45,0x46,0x47,0x48,0x49,0x4a,0x53,0x54,0x55,0x56,0x57,0x58,0x59,
            0x5a,0x63,0x64,0x65,0x66,0x67,0x68,0x69,0x6a,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7a,0x83,0x84,0x85,0x86,0x87,0x88,0x89,
            0x8a,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9a,0xa2,0xa3,0xa4,0xa5,0xa6,0xa7,0xa8,0xa9,0xaa,0xb2,0xb3,0xb4,0xb5,0xb6,
            0xb7,0xb8,0xb9,0xba,0xc2,0xc3,0xc4,0xc5,0xc6,0xc7,0xc8,0xc9,0xca,0xd2,0xd3,0xd4,0xd5,0xd6,0xd7,0xd8,0xd9,0xda,0xe1,0xe2,
            0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea,0xf1,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,0xf9,0xfa };
        private static readonly byte[] AcChromaCounts = { 0,2,1,2,4,4,3,4,7,5,4,4,0,1,2,0x77 };
        private static readonly byte[] AcChromaSymbols = {
            0x00,0x01,0x02,0x03,0x11,0x04,0x05,0x21,0x31,0x06,0x12,0x41,0x51,0x07,0x61,0x71,0x13,0x22,0x32,0x81,0x08,0x14,0x42,0x91,
            0xa1,0xb1,0xc1,0x09,0x23,0x33,0x52,0xf0,0x15,0x62,0x72,0xd1,0x0a,0x16,0x24,0x34,0xe1,0x25,0xf1,0x17,0x18,0x19,0x1a,0x26,
            0x27,0x28,0x29,0x2a,0x35,0x36,0x37,0x38,0x39,0x3a,0x43,0x44,0x45,0x46,0x47,0x48,0x49,0x4a,0x53,0x54,0x55,0x56,0x57,0x58,
            0x59,0x5a,0x63,0x64,0x65,0x66,0x67,0x68,0x69,0x6a,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7a,0x82,0x83,0x84,0x85,0x86,0x87,
            0x88,0x89,0x8a,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9a,0xa2,0xa3,0xa4,0xa5,0xa6,0xa7,0xa8,0xa9,0xaa,0xb2,0xb3,0xb4,
            0xb5,0xb6,0xb7,0xb8,0xb9,0xba,0xc2,0xc3,0xc4,0xc5,0xc6,0xc7,0xc8,0xc9,0xca,0xd2,0xd3,0xd4,0xd5,0xd6,0xd7,0xd8,0xd9,0xda,
            0xe2,0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,0xf9,0xfa };
        // AAN scale factors: cos(k*pi/16)*sqrt(2), k = 1..7, 1 for k = 0.
        private static readonly float[] Aan = { 1f, 1.387039845f, 1.306562965f, 1.175875602f, 1f, 0.785694958f, 0.541196100f, 0.275899379f };

        private static byte[] Invert(byte[] map)
        {
            var inverse = new byte[map.Length];
            for (int i = 0; i < map.Length; i++) inverse[map[i]] = (byte)i;
            return inverse;
        }

        internal static bool IsJpeg(byte[] bytes) => bytes != null && bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

        // ---------------------------------------------------------------- writing

        private sealed class Writer
        {
            private readonly MemoryStream stream;
            private uint bits; private int count;
            internal Writer(MemoryStream stream) { this.stream = stream; }
            internal void Put(int code, int size)
            {
                bits = (bits << size) | ((uint)code & ((1u << size) - 1u)); count += size;
                while (count >= 8)
                {
                    byte b = (byte)(bits >> (count - 8)); stream.WriteByte(b);
                    if (b == 0xFF) stream.WriteByte(0);
                    count -= 8;
                }
                bits &= (1u << count) - 1u;
            }
            internal void Flush() { if (count > 0) Put((1 << (8 - count)) - 1, 8 - count); }
        }

        private static void Codes(byte[] counts, byte[] symbols, out ushort[] code, out byte[] size)
        {
            code = new ushort[256]; size = new byte[256];
            int c = 0, k = 0;
            for (int length = 1; length <= 16; length++)
            {
                for (int i = 0; i < counts[length - 1]; i++, k++) { code[symbols[k]] = (ushort)c; size[symbols[k]] = (byte)length; c++; }
                c <<= 1;
            }
        }

        private static byte Scaled(byte value, int scale) => (byte)Math.Max(1, Math.Min(255, (value * scale + 50) / 100));

        /// <summary>The photo as a baseline JPEG. Pure computation; safe on any thread.</summary>
        internal static byte[] Encode(PhotoPixels image, int quality = DefaultQuality)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            quality = Math.Max(1, Math.Min(100, quality));
            int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
            var lumaQ = new byte[64]; var chromaQ = new byte[64];
            for (int i = 0; i < 64; i++) { lumaQ[i] = Scaled(LumaQuant[i], scale); chromaQ[i] = Scaled(ChromaQuant[i], scale); }
            var lumaF = new float[64]; var chromaF = new float[64];
            for (int row = 0, k = 0; row < 8; row++)
                for (int col = 0; col < 8; col++, k++)
                {
                    lumaF[k] = 1f / (lumaQ[k] * Aan[row] * Aan[col] * 8f);
                    chromaF[k] = 1f / (chromaQ[k] * Aan[row] * Aan[col] * 8f);
                }
            Codes(DcLumaCounts, DcSymbols, out var dcLumaCode, out var dcLumaSize);
            Codes(DcChromaCounts, DcSymbols, out var dcChromaCode, out var dcChromaSize);
            Codes(AcLumaCounts, AcLumaSymbols, out var acLumaCode, out var acLumaSize);
            Codes(AcChromaCounts, AcChromaSymbols, out var acChromaCode, out var acChromaSize);

            int width = image.Width, height = image.Height;
            using (var stream = new MemoryStream(Math.Max(1024, width * height / 2)))
            {
                stream.WriteByte(0xFF); stream.WriteByte(0xD8);
                // JFIF, version 1.01, no density units, no thumbnail.
                Segment(stream, 0xE0, new byte[] { (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 });
                var dqt = new byte[130];
                dqt[0] = 0; for (int i = 0; i < 64; i++) dqt[1 + i] = lumaQ[Natural[i]];
                dqt[65] = 1; for (int i = 0; i < 64; i++) dqt[66 + i] = chromaQ[Natural[i]];
                Segment(stream, 0xDB, dqt);
                Segment(stream, 0xC0, new byte[] { 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1 });
                using (var dht = new MemoryStream())
                {
                    Table(dht, 0x00, DcLumaCounts, DcSymbols); Table(dht, 0x10, AcLumaCounts, AcLumaSymbols);
                    Table(dht, 0x01, DcChromaCounts, DcSymbols); Table(dht, 0x11, AcChromaCounts, AcChromaSymbols);
                    Segment(stream, 0xC4, dht.ToArray());
                }
                Segment(stream, 0xDA, new byte[] { 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0 });

                var writer = new Writer(stream);
                var y = new float[64]; var cb = new float[64]; var cr = new float[64]; var du = new int[64];
                int dcY = 0, dcCb = 0, dcCr = 0;
                byte[] rgb = image.Rgb;
                for (int by = 0; by < height; by += 8)
                    for (int bx = 0; bx < width; bx += 8)
                    {
                        for (int row = 0, k = 0; row < 8; row++)
                        {
                            // PhotoPixels keeps the bottom row first; JPEG rows run top down. Edge blocks repeat the last row and column.
                            int sourceRow = height - 1 - Math.Min(by + row, height - 1);
                            int line = sourceRow * width;
                            for (int col = 0; col < 8; col++, k++)
                            {
                                int p = (line + Math.Min(bx + col, width - 1)) * 3;
                                float r = rgb[p], g = rgb[p + 1], b = rgb[p + 2];
                                y[k] = 0.299f * r + 0.587f * g + 0.114f * b - 128f;
                                cb[k] = -0.168736f * r - 0.331264f * g + 0.5f * b;
                                cr[k] = 0.5f * r - 0.418688f * g - 0.081312f * b;
                            }
                        }
                        dcY = Block(writer, y, lumaF, du, dcY, dcLumaCode, dcLumaSize, acLumaCode, acLumaSize);
                        dcCb = Block(writer, cb, chromaF, du, dcCb, dcChromaCode, dcChromaSize, acChromaCode, acChromaSize);
                        dcCr = Block(writer, cr, chromaF, du, dcCr, dcChromaCode, dcChromaSize, acChromaCode, acChromaSize);
                    }
                writer.Flush();
                stream.WriteByte(0xFF); stream.WriteByte(0xD9);
                return stream.ToArray();
            }
        }

        private static void Segment(Stream stream, int marker, byte[] body)
        {
            stream.WriteByte(0xFF); stream.WriteByte((byte)marker);
            int length = body.Length + 2; stream.WriteByte((byte)(length >> 8)); stream.WriteByte((byte)length);
            stream.Write(body, 0, body.Length);
        }

        private static void Table(Stream stream, int id, byte[] counts, byte[] symbols)
        {
            stream.WriteByte((byte)id); stream.Write(counts, 0, 16); stream.Write(symbols, 0, symbols.Length);
        }

        private static void ForwardDct(float[] d, int o, int s)
        {
            float d0 = d[o], d1 = d[o + s], d2 = d[o + 2 * s], d3 = d[o + 3 * s], d4 = d[o + 4 * s], d5 = d[o + 5 * s], d6 = d[o + 6 * s], d7 = d[o + 7 * s];
            float tmp0 = d0 + d7, tmp7 = d0 - d7, tmp1 = d1 + d6, tmp6 = d1 - d6, tmp2 = d2 + d5, tmp5 = d2 - d5, tmp3 = d3 + d4, tmp4 = d3 - d4;
            float tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
            d[o] = tmp10 + tmp11; d[o + 4 * s] = tmp10 - tmp11;
            float z1 = (tmp12 + tmp13) * 0.707106781f;
            d[o + 2 * s] = tmp13 + z1; d[o + 6 * s] = tmp13 - z1;
            tmp10 = tmp4 + tmp5; tmp11 = tmp5 + tmp6; tmp12 = tmp6 + tmp7;
            float z5 = (tmp10 - tmp12) * 0.382683433f, z2 = tmp10 * 0.541196100f + z5, z4 = tmp12 * 1.306562965f + z5, z3 = tmp11 * 0.707106781f;
            float z11 = tmp7 + z3, z13 = tmp7 - z3;
            d[o + 5 * s] = z13 + z2; d[o + 3 * s] = z13 - z2; d[o + s] = z11 + z4; d[o + 7 * s] = z11 - z4;
        }

        private static int Block(Writer w, float[] data, float[] scale, int[] du, int previousDc,
            ushort[] dcCode, byte[] dcSize, ushort[] acCode, byte[] acSize)
        {
            for (int row = 0; row < 8; row++) ForwardDct(data, row * 8, 1);
            for (int col = 0; col < 8; col++) ForwardDct(data, col, 8);
            for (int i = 0; i < 64; i++)
            {
                float v = data[i] * scale[i];
                int q = (int)(v < 0 ? v - 0.5f : v + 0.5f);
                // Baseline Huffman covers DC differences to 11 bits and AC values to 10; only a contrived pattern reaches the limit.
                du[ZigZag[i]] = i == 0 ? Math.Max(-2047, Math.Min(2047, q)) : Math.Max(-1023, Math.Min(1023, q));
            }
            int dc = du[0], diff = dc - previousDc;
            if (diff == 0) w.Put(dcCode[0], dcSize[0]);
            else { int n = Category(diff); w.Put(dcCode[n], dcSize[n]); w.Put(diff < 0 ? diff - 1 : diff, n); }
            int last = 63;
            while (last > 0 && du[last] == 0) last--;
            for (int i = 1; i <= last; i++)
            {
                int run = 0;
                while (du[i] == 0) { run++; i++; }
                while (run >= 16) { w.Put(acCode[0xF0], acSize[0xF0]); run -= 16; }
                int v = du[i], n = Category(v), symbol = (run << 4) | n;
                w.Put(acCode[symbol], acSize[symbol]); w.Put(v < 0 ? v - 1 : v, n);
            }
            if (last != 63) w.Put(acCode[0], acSize[0]);
            return dc;
        }

        private static int Category(int value)
        {
            int magnitude = value < 0 ? -value : value, n = 0;
            while (magnitude > 0) { n++; magnitude >>= 1; }
            return n;
        }

        // ---------------------------------------------------------------- reading

        private sealed class Huffman
        {
            internal readonly int[] MaxCode = new int[18], ValueOffset = new int[18];
            internal readonly byte[] Values = new byte[256];
            internal readonly byte[] FastLength = new byte[1 << FastBits], FastValue = new byte[1 << FastBits];
            internal const int FastBits = 9;
            internal Huffman(byte[] counts, byte[] values)
            {
                Array.Copy(values, Values, values.Length);
                int code = 0, k = 0;
                for (int length = 1; length <= 16; length++)
                {
                    ValueOffset[length] = k - code;
                    for (int i = 0; i < counts[length - 1]; i++, k++)
                    {
                        if (length <= FastBits)
                        {
                            int shift = FastBits - length, first = code << shift;
                            for (int j = 0; j < 1 << shift; j++) { FastLength[first + j] = (byte)length; FastValue[first + j] = values[k]; }
                        }
                        code++;
                    }
                    MaxCode[length] = counts[length - 1] > 0 ? code - 1 : -1;
                    if (code > 1 << length) throw new InvalidDataException("Bad JPEG Huffman table");
                    code <<= 1;
                }
                MaxCode[17] = int.MaxValue;
            }
        }

        private sealed class Component
        {
            internal int Id, H, V, Quant, Dc, Ac, Predictor;
            internal int BlocksWide, BlocksHigh, Stride;
            internal byte[] Plane;
        }

        private sealed class Reader
        {
            private readonly byte[] data; private int pos; private uint bits; private int count;
            internal Reader(byte[] data, int pos) { this.data = data; this.pos = pos; }
            private void Fill()
            {
                while (count <= 24)
                {
                    int b = 0;
                    if (pos < data.Length)
                    {
                        b = data[pos];
                        if (b == 0xFF)
                        {
                            int next = pos + 1 < data.Length ? data[pos + 1] : 0xD9;
                            if (next == 0x00) pos += 2;
                            else b = 0;   // a marker: feed zeros and leave it for Restart
                        }
                        else pos++;
                    }
                    bits |= (uint)b << (24 - count); count += 8;
                }
            }
            internal int Bits(int n)
            {
                if (n == 0) return 0;
                Fill();
                int v = (int)(bits >> (32 - n)); bits <<= n; count -= n; return v;
            }
            internal int Decode(Huffman h)
            {
                Fill();
                int peek = (int)(bits >> (32 - Huffman.FastBits)), length = h.FastLength[peek];
                if (length > 0) { bits <<= length; count -= length; return h.FastValue[peek]; }
                int code = (int)(bits >> (32 - Huffman.FastBits));
                length = Huffman.FastBits;
                bits <<= Huffman.FastBits; count -= Huffman.FastBits;
                while (code > h.MaxCode[length])
                {
                    if (count <= 0) Fill();
                    code = (code << 1) | (int)(bits >> 31); bits <<= 1; count--; length++;
                    if (length > 16) throw new InvalidDataException("Bad JPEG Huffman code");
                }
                return h.Values[h.ValueOffset[length] + code];
            }
            internal static int Extend(int v, int n) => n == 0 ? 0 : v < 1 << (n - 1) ? v - (1 << n) + 1 : v;
            internal void Restart()
            {
                bits = 0; count = 0;
                while (pos + 1 < data.Length && !(data[pos] == 0xFF && data[pos + 1] >= 0xD0 && data[pos + 1] <= 0xD7)) pos++;
                if (pos + 1 >= data.Length) throw new InvalidDataException("Missing JPEG restart marker");
                pos += 2;
            }
        }

        private static int U16(byte[] d, int i) => d[i] << 8 | d[i + 1];

        /// <summary>Width and height from the frame header, or false when this is not a JPEG the photo code can name.</summary>
        internal static bool Size(byte[] data, out int width, out int height)
        {
            width = height = 0;
            if (!IsJpeg(data)) return false;
            int pos = 2;
            while (pos + 4 <= data.Length)
            {
                if (data[pos] != 0xFF) return false;
                int marker = data[pos + 1];
                if (marker == 0xFF) { pos++; continue; }
                if (marker == 0xD8 || marker >= 0xD0 && marker <= 0xD7 || marker == 0x01) { pos += 2; continue; }
                int length = U16(data, pos + 2);
                if (length < 2 || pos + 2 + length > data.Length) return false;
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    if (length < 7) return false;
                    height = U16(data, pos + 5); width = U16(data, pos + 7);
                    return width > 0 && height > 0;
                }
                if (marker == 0xDA || marker == 0xD9) return false;
                pos += 2 + length;
            }
            return false;
        }

        /// <summary>Decodes a sequential Huffman JPEG into bottom-row-first RGB. Pure computation; safe on any thread.</summary>
        internal static PhotoPixels Decode(byte[] data)
        {
            if (!IsJpeg(data)) throw new InvalidDataException("Not a JPEG");
            var quant = new float[4][]; var dcTables = new Huffman[4]; var acTables = new Huffman[4];
            Component[] components = null; int width = 0, height = 0, restartInterval = 0; bool adobeRgb = false, frame = false;
            int pos = 2;
            while (true)
            {
                if (pos + 4 > data.Length) throw new InvalidDataException("Truncated JPEG");
                if (data[pos] != 0xFF) throw new InvalidDataException("JPEG marker expected");
                int marker = data[pos + 1];
                if (marker == 0xFF) { pos++; continue; }
                if (marker == 0xD8 || marker == 0x01 || marker >= 0xD0 && marker <= 0xD7) { pos += 2; continue; }
                if (marker == 0xD9) throw new InvalidDataException("JPEG without image data");
                int length = U16(data, pos + 2), body = pos + 4, end = pos + 2 + length;
                if (length < 2 || end > data.Length) throw new InvalidDataException("Bad JPEG segment length");
                switch (marker)
                {
                    case 0xDB:
                        for (int at = body; at < end;)
                        {
                            int precision = data[at] >> 4, id = data[at] & 15; at++;
                            if (id > 3 || precision > 1) throw new InvalidDataException("Bad JPEG quantization table");
                            var table = new float[64];
                            for (int k = 0; k < 64; k++)
                            {
                                int v = precision == 0 ? data[at++] : U16(data, (at += 2) - 2);
                                int natural = Natural[k], row = natural >> 3, col = natural & 7;
                                // AAN scaling and the final divide by 8 folded into dequantization.
                                table[natural] = v * Aan[row] * Aan[col] / 8f;
                            }
                            quant[id] = table;
                        }
                        break;
                    case 0xC0: case 0xC1:
                        if (data[body] != 8) throw new InvalidDataException("Only 8-bit JPEG");
                        height = U16(data, body + 1); width = U16(data, body + 3);
                        int count = data[body + 5];
                        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) throw new InvalidDataException("JPEG size out of range");
                        if (count != 1 && count != 3) throw new InvalidDataException("Only grey or three-component JPEG");
                        components = new Component[count];
                        for (int i = 0; i < count; i++)
                        {
                            int at = body + 6 + i * 3;
                            var c = new Component { Id = data[at], H = data[at + 1] >> 4, V = data[at + 1] & 15, Quant = data[at + 2] };
                            if (c.H < 1 || c.H > 2 || c.V < 1 || c.V > 2 || c.Quant > 3) throw new InvalidDataException("Unsupported JPEG sampling");
                            components[i] = c;
                        }
                        frame = true;
                        break;
                    case 0xC4:
                        for (int at = body; at < end;)
                        {
                            int cls = data[at] >> 4, id = data[at] & 15; at++;
                            if (cls > 1 || id > 3) throw new InvalidDataException("Bad JPEG Huffman table id");
                            var counts = new byte[16]; Array.Copy(data, at, counts, 0, 16); at += 16;
                            int total = 0; foreach (byte n in counts) total += n;
                            if (total > 256 || at + total > end) throw new InvalidDataException("Bad JPEG Huffman table");
                            var values = new byte[total]; Array.Copy(data, at, values, 0, total); at += total;
                            (cls == 0 ? dcTables : acTables)[id] = new Huffman(counts, values);
                        }
                        break;
                    case 0xDD:
                        restartInterval = U16(data, body);
                        break;
                    case 0xEE:
                        // Adobe: transform 0 on three components means the samples are RGB, not YCbCr.
                        if (length >= 14 && data[body] == 'A' && data[body + 1] == 'd' && data[body + 2] == 'o' && data[body + 3] == 'b' && data[body + 4] == 'e')
                            adobeRgb = data[body + 11] == 0;
                        break;
                    case 0xDA:
                        if (!frame) throw new InvalidDataException("JPEG scan before frame");
                        int inScan = data[body];
                        if (inScan != components.Length) throw new InvalidDataException("Only single-scan JPEG");
                        for (int i = 0; i < inScan; i++)
                        {
                            int id = data[body + 1 + i * 2], tables = data[body + 2 + i * 2];
                            var c = Array.Find(components, x => x.Id == id) ?? throw new InvalidDataException("Unknown JPEG component");
                            c.Dc = tables >> 4; c.Ac = tables & 15;
                            if (c.Dc > 3 || c.Ac > 3 || dcTables[c.Dc] == null || acTables[c.Ac] == null || quant[c.Quant] == null)
                                throw new InvalidDataException("Missing JPEG table");
                        }
                        int spectral = body + 1 + inScan * 2;
                        if (data[spectral] != 0 || data[spectral + 1] != 63 || data[spectral + 2] != 0) throw new InvalidDataException("Only sequential JPEG");
                        return Scan(data, end, components, width, height, restartInterval, quant, dcTables, acTables,
                            adobeRgb || components.Length == 3 && components[0].Id == 'R' && components[1].Id == 'G' && components[2].Id == 'B');
                    default:
                        // SOF2 and the other frame types (progressive, lossless, arithmetic) are left to the engine's decoder.
                        if (marker >= 0xC2 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                            throw new InvalidDataException("Unsupported JPEG type");
                        break;
                }
                pos = end;
            }
        }

        private static PhotoPixels Scan(byte[] data, int start, Component[] components, int width, int height, int restartInterval,
            float[][] quant, Huffman[] dcTables, Huffman[] acTables, bool rgbSamples)
        {
            int hMax = 1, vMax = 1;
            foreach (var c in components) { hMax = Math.Max(hMax, c.H); vMax = Math.Max(vMax, c.V); }
            bool single = components.Length == 1;
            int mcuX = single ? (width + 7) / 8 : (width + 8 * hMax - 1) / (8 * hMax);
            int mcuY = single ? (height + 7) / 8 : (height + 8 * vMax - 1) / (8 * vMax);
            foreach (var c in components)
            {
                c.BlocksWide = single ? mcuX : mcuX * c.H; c.BlocksHigh = single ? mcuY : mcuY * c.V;
                c.Stride = c.BlocksWide * 8; c.Plane = new byte[c.Stride * c.BlocksHigh * 8];
            }
            var reader = new Reader(data, start);
            var coef = new float[64]; var work = new float[64];
            int done = 0, total = mcuX * mcuY;
            for (int my = 0; my < mcuY; my++)
                for (int mx = 0; mx < mcuX; mx++)
                {
                    if (restartInterval > 0 && done > 0 && done % restartInterval == 0)
                    {
                        reader.Restart();
                        foreach (var c in components) c.Predictor = 0;
                    }
                    foreach (var c in components)
                    {
                        int h = single ? 1 : c.H, v = single ? 1 : c.V;
                        for (int by = 0; by < v; by++)
                            for (int bx = 0; bx < h; bx++)
                            {
                                Array.Clear(coef, 0, 64);
                                var q = quant[c.Quant];
                                int t = reader.Decode(dcTables[c.Dc]);
                                c.Predictor += t == 0 ? 0 : Reader.Extend(reader.Bits(t), t);
                                coef[0] = c.Predictor * q[0];
                                var ac = acTables[c.Ac];
                                for (int k = 1; k < 64;)
                                {
                                    int rs = reader.Decode(ac), run = rs >> 4, size = rs & 15;
                                    if (size == 0) { if (run != 15) break; k += 16; continue; }
                                    k += run;
                                    if (k > 63) throw new InvalidDataException("Bad JPEG coefficient run");
                                    int natural = Natural[k];
                                    coef[natural] = Reader.Extend(reader.Bits(size), size) * q[natural];
                                    k++;
                                }
                                InverseDct(coef, work, c.Plane, ((my * v + by) * 8) * c.Stride + (mx * h + bx) * 8, c.Stride);
                            }
                    }
                    done++;
                }
            var rgb = new byte[checked(width * height * 3)];
            var y = components[0];
            for (int row = 0; row < height; row++)
            {
                int target = (height - 1 - row) * width * 3;
                if (single)
                {
                    int line = row * y.Stride;
                    for (int col = 0; col < width; col++, target += 3) rgb[target] = rgb[target + 1] = rgb[target + 2] = y.Plane[line + col];
                    continue;
                }
                var cb = components[1]; var cr = components[2];
                int yLine = row * y.V / vMax * y.Stride, bLine = row * cb.V / vMax * cb.Stride, rLine = row * cr.V / vMax * cr.Stride;
                for (int col = 0; col < width; col++, target += 3)
                {
                    float l = y.Plane[yLine + col * y.H / hMax], u = cb.Plane[bLine + col * cb.H / hMax], s = cr.Plane[rLine + col * cr.H / hMax];
                    if (rgbSamples) { rgb[target] = (byte)l; rgb[target + 1] = (byte)u; rgb[target + 2] = (byte)s; continue; }
                    u -= 128f; s -= 128f;
                    rgb[target] = Clamp(l + 1.402f * s);
                    rgb[target + 1] = Clamp(l - 0.344136f * u - 0.714136f * s);
                    rgb[target + 2] = Clamp(l + 1.772f * u);
                }
            }
            return new PhotoPixels(width, height, rgb);
        }

        private static byte Clamp(float v) => v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)(v + 0.5f);

        private static void InverseDct(float[] c, float[] w, byte[] plane, int origin, int stride)
        {
            for (int col = 0; col < 8; col++)
            {
                float tmp0 = c[col], tmp1 = c[16 + col], tmp2 = c[32 + col], tmp3 = c[48 + col];
                float tmp10 = tmp0 + tmp2, tmp11 = tmp0 - tmp2, tmp13 = tmp1 + tmp3, tmp12 = (tmp1 - tmp3) * 1.414213562f - tmp13;
                tmp0 = tmp10 + tmp13; tmp3 = tmp10 - tmp13; tmp1 = tmp11 + tmp12; tmp2 = tmp11 - tmp12;
                float tmp4 = c[8 + col], tmp5 = c[24 + col], tmp6 = c[40 + col], tmp7 = c[56 + col];
                float z13 = tmp6 + tmp5, z10 = tmp6 - tmp5, z11 = tmp4 + tmp7, z12 = tmp4 - tmp7;
                tmp7 = z11 + z13; tmp11 = (z11 - z13) * 1.414213562f;
                float z5 = (z10 + z12) * 1.847759065f;
                tmp10 = 1.082392200f * z12 - z5; tmp12 = -2.613125930f * z10 + z5;
                tmp6 = tmp12 - tmp7; tmp5 = tmp11 - tmp6; tmp4 = tmp10 + tmp5;
                w[col] = tmp0 + tmp7; w[56 + col] = tmp0 - tmp7; w[8 + col] = tmp1 + tmp6; w[48 + col] = tmp1 - tmp6;
                w[16 + col] = tmp2 + tmp5; w[40 + col] = tmp2 - tmp5; w[32 + col] = tmp3 + tmp4; w[24 + col] = tmp3 - tmp4;
            }
            for (int row = 0; row < 8; row++)
            {
                int r = row * 8, o = origin + row * stride;
                float tmp10 = w[r] + w[r + 4], tmp11 = w[r] - w[r + 4], tmp13 = w[r + 2] + w[r + 6], tmp12 = (w[r + 2] - w[r + 6]) * 1.414213562f - tmp13;
                float tmp0 = tmp10 + tmp13, tmp3 = tmp10 - tmp13, tmp1 = tmp11 + tmp12, tmp2 = tmp11 - tmp12;
                float z13 = w[r + 5] + w[r + 3], z10 = w[r + 5] - w[r + 3], z11 = w[r + 1] + w[r + 7], z12 = w[r + 1] - w[r + 7];
                float tmp7 = z11 + z13; tmp11 = (z11 - z13) * 1.414213562f;
                float z5 = (z10 + z12) * 1.847759065f;
                tmp10 = 1.082392200f * z12 - z5; tmp12 = -2.613125930f * z10 + z5;
                float tmp6 = tmp12 - tmp7, tmp5 = tmp11 - tmp6, tmp4 = tmp10 + tmp5;
                plane[o] = Clamp(tmp0 + tmp7 + 128f); plane[o + 7] = Clamp(tmp0 - tmp7 + 128f);
                plane[o + 1] = Clamp(tmp1 + tmp6 + 128f); plane[o + 6] = Clamp(tmp1 - tmp6 + 128f);
                plane[o + 2] = Clamp(tmp2 + tmp5 + 128f); plane[o + 5] = Clamp(tmp2 - tmp5 + 128f);
                plane[o + 4] = Clamp(tmp3 + tmp4 + 128f); plane[o + 3] = Clamp(tmp3 - tmp4 + 128f);
            }
        }
    }
}
