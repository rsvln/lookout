using System.IO.Compression;
using System.Text;

namespace Lookout
{
    // Tiny PNG writer (no image library): a dark square with a red circle, for PWA / apple-touch icons.
    public static class PngIcon
    {
        const byte DarkR = 0x0d, DarkG = 0x11, DarkB = 0x17;
        const byte RedR = 0xe5, RedG = 0x48, RedB = 0x4d;

        public static byte[] AppIcon(int size)
        {
            size = Math.Clamp(size, 16, 1024);
            var raw = new byte[size * (1 + size * 3)];
            double cx = (size - 1) / 2.0, r = size * 0.36, r2 = r * r;
            int p = 0;
            for (int y = 0; y < size; y++)
            {
                raw[p++] = 0;
                for (int x = 0; x < size; x++)
                {
                    double dx = x - cx, dy = y - cx;
                    bool on = dx * dx + dy * dy <= r2;
                    raw[p++] = on ? RedR : DarkR;
                    raw[p++] = on ? RedG : DarkG;
                    raw[p++] = on ? RedB : DarkB;
                }
            }
            return Wrap(size, size, raw);
        }

        static byte[] Wrap(int w, int h, byte[] raw)
        {
            using var png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            Chunk(png, "IHDR", Ihdr(w, h));
            Chunk(png, "IDAT", Zlib(raw));
            Chunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        static byte[] Ihdr(int w, int h)
        {
            var b = new byte[13];
            WriteBe(b, 0, w); WriteBe(b, 4, h);
            b[8] = 8; b[9] = 2;
            return b;
        }

        static byte[] Zlib(byte[] raw)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0x01);
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                ds.Write(raw, 0, raw.Length);
            uint a1 = 1, a2 = 0;
            foreach (byte x in raw) { a1 = (a1 + x) % 65521; a2 = (a2 + a1) % 65521; }
            ms.WriteByte((byte)(a2 >> 8)); ms.WriteByte((byte)a2);
            ms.WriteByte((byte)(a1 >> 8)); ms.WriteByte((byte)a1);
            return ms.ToArray();
        }

        static void Chunk(MemoryStream png, string type, byte[] data)
        {
            var name = Encoding.ASCII.GetBytes(type);
            WriteBeStream(png, data.Length);
            png.Write(name);
            png.Write(data);
            WriteBeStream(png, Crc(name, data));
        }

        static uint Crc(byte[] type, byte[] data)
        {
            uint c = 0xffffffff;
            foreach (byte x in type) c = CrcByte(c, x);
            foreach (byte x in data) c = CrcByte(c, x);
            return c ^ 0xffffffff;
        }

        static uint CrcByte(uint c, byte x)
        {
            c ^= x;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
            return c;
        }

        static void WriteBe(byte[] b, int i, int v)
        {
            b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v;
        }

        static void WriteBeStream(MemoryStream s, int v) => s.Write(new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        static void WriteBeStream(MemoryStream s, uint v) => WriteBeStream(s, (int)v);
    }
}
