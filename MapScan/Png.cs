using System.IO.Compression;

namespace Poe2Map;

/// <summary>
///     Bağımlılıksız gri tonlamalı PNG yazıcı. Dış kütüphane yok: zlib akışını
///     DeflateStream + adler32 ile, sağlama toplamlarını kendi CRC tablomuzla üretiyoruz.
/// </summary>
internal static class Png
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc(byte[] data, int offset, int length)
    {
        var c = 0xFFFFFFFFu;
        for (var i = 0; i < length; i++)
        {
            c = CrcTable[(c ^ data[offset + i]) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static void WriteBig(Stream s, uint value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    private static void Chunk(Stream s, string type, byte[] payload)
    {
        WriteBig(s, (uint)payload.Length);
        var full = new byte[4 + payload.Length];
        for (var i = 0; i < 4; i++) { full[i] = (byte)type[i]; }
        Buffer.BlockCopy(payload, 0, full, 4, payload.Length);
        s.Write(full, 0, full.Length);
        WriteBig(s, Crc(full, 0, full.Length));
    }

    /// <summary>Gri tonlamalı 8-bit PNG yazar. pixels uzunluğu width*height olmalı.</summary>
    internal static void WriteGray(string path, byte[] pixels, int width, int height)
    {
        // Her satırın başına filtre baytı (0 = filtresiz) konur.
        var raw = new byte[(width + 1) * height];
        for (var y = 0; y < height; y++)
        {
            raw[y * (width + 1)] = 0;
            Buffer.BlockCopy(pixels, y * width, raw, y * (width + 1) + 1, width);
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var def = new DeflateStream(ms, CompressionLevel.Optimal, true))
            {
                def.Write(raw, 0, raw.Length);
            }

            compressed = ms.ToArray();
        }

        var zlib = new byte[2 + compressed.Length + 4];
        zlib[0] = 0x78;
        zlib[1] = 0x01;
        Buffer.BlockCopy(compressed, 0, zlib, 2, compressed.Length);
        var adler = Adler32(raw);
        zlib[^4] = (byte)(adler >> 24);
        zlib[^3] = (byte)(adler >> 16);
        zlib[^2] = (byte)(adler >> 8);
        zlib[^1] = (byte)adler;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
        ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
        ihdr[8] = 8;  // bit derinligi
        ihdr[9] = 0;  // gri tonlama
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(fs, "IHDR", ihdr);
        Chunk(fs, "IDAT", zlib);
        Chunk(fs, "IEND", Array.Empty<byte>());
    }
}
