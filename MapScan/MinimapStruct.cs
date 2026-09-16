using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Minimap görünürlük yapısını bellekte arar.
///
///     Yapının şekli Ghidra'dan çıktı - FUN_14120d8d0 bu alanları okuyor:
///
///         +0x00  int    genislik  (hucre)
///         +0x04  int    yukseklik (hucre)
///         +0x08  float  dunya genisligi
///         +0x0C  float  dunya yuksekligi
///         +0x10  ptr    float dizisi (gorunurluk, uzunluk = genislik*yukseklik)
///
///     Bunu bulmak iki şeyi birden çözer: ızgaranın boyutları tahmin edilmez, doğrudan
///     okunur; ve dünya→hücre dönüşümü de veriden gelir (çağıran kod
///     "(dunyaX / dunyaGenisligi) * genislik" hesabını yapıyor).
///
///     Arama çok seçici: iki makul tamsayı, iki pozitif float, ve bunlarla tutarlı
///     boyutta okunabilir bir tampon. Rastgele veride bu birleşim neredeyse hiç çıkmaz.
/// </summary>
internal static class MinimapStruct
{
    private const int ReadBlock = 1 << 20;

    internal static int Run()
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        var sw = Stopwatch.StartNew();
        var found = 0;

        try
        {
            var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();
            var buf = new byte[ReadBlock];
            var probe = new byte[4];
            ulong address = 0x10000;

            Console.WriteLine("adres            genislik x yukseklik   dunya olcusu        birim/hucre   veri");

            while (address < 0x7FFFFFFF0000UL)
            {
                if (Native.VirtualQueryEx(handle, (IntPtr)address, out var mbi, mbiSize) == IntPtr.Zero) { break; }
                var size = (long)mbi.RegionSize;
                if (size <= 0) { break; }

                var usable = mbi.State == Native.MemCommit &&
                             mbi.Type == Native.MemPrivate &&
                             (mbi.Protect & Native.PageGuard) == 0 &&
                             mbi.Protect == Native.PageReadWrite;

                if (usable)
                {
                    var regionBase = (ulong)mbi.BaseAddress.ToInt64();
                    for (long off = 0; off < size; off += ReadBlock - 32)
                    {
                        var want = (int)Math.Min(ReadBlock, size - off);
                        if (want < 24) { break; }
                        if (!Native.ReadProcessMemory(handle, (IntPtr)(regionBase + (ulong)off), buf,
                                (IntPtr)want, out var got) || (long)got < 24)
                        {
                            continue;
                        }

                        var n = (long)got;
                        for (long i = 0; i + 24 <= n; i += 4)
                        {
                            var w = BitConverter.ToInt32(buf, (int)i);
                            if (w is < 32 or > 16384) { continue; }

                            var h = BitConverter.ToInt32(buf, (int)i + 4);
                            if (h is < 32 or > 16384) { continue; }

                            var wx = BitConverter.ToSingle(buf, (int)i + 8);
                            var wy = BitConverter.ToSingle(buf, (int)i + 12);
                            if (!float.IsFinite(wx) || !float.IsFinite(wy) || wx <= 0 || wy <= 0) { continue; }

                            var sx = wx / w;
                            var sy = wy / h;
                            if (sx is < 1f or > 200f || sy is < 1f or > 200f) { continue; }
                            if (Math.Abs(sx - sy) > 0.5f) { continue; }

                            var ptr = BitConverter.ToUInt64(buf, (int)i + 16);
                            if (ptr < 0x10000 || ptr > 0x7FFFFFFFFFFF || (ptr & 3) != 0) { continue; }

                            // Veri tamponu gercekten okunabiliyor mu, hem basi hem sonu
                            long cells = (long)w * h;
                            if (!Native.ReadProcessMemory(handle, (IntPtr)ptr, probe, (IntPtr)4, out var g1) ||
                                (long)g1 != 4)
                            {
                                continue;
                            }

                            var lastByte = ptr + (ulong)((cells - 1) * 4);
                            if (!Native.ReadProcessMemory(handle, (IntPtr)lastByte, probe, (IntPtr)4, out var g2) ||
                                (long)g2 != 4)
                            {
                                continue;
                            }

                            found++;
                            Console.WriteLine(
                                $"{regionBase + (ulong)off + (ulong)i:X}   {w,5} x {h,-5}   " +
                                $"{wx,9:F0} x {wy,-9:F0}   {sx,6:F2}      {ptr:X}");
                        }
                    }
                }

                address += (ulong)size;
            }
        }
        finally
        {
            Native.CloseHandle(handle);
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"{found} eslesme, {sw.Elapsed.TotalSeconds:F0} sn");
        return 0;
    }
}
