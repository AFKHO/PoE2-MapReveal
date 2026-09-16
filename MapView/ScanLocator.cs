using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     IzgarayÄ± bellekte tarayarak bulur. Offset kullanmadÄ±ÄŸÄ± iÃ§in oyun patch'lerinden
///     etkilenmez - veriyi adresinden deÄŸil ÅŸeklinden tanÄ±r.
///
///     ÃœÃ§ aÅŸama, Ã§Ã¼nkÃ¼ 5 GB'Ä± baÅŸtan sona okumak 20 saniye sÃ¼rÃ¼yor:
///
///       1. YOKLAMA  - her bÃ¶lgeden birkaÃ§ 4 KB pencere okunur. BÃ¶lgenin tamamÄ± deÄŸil.
///                     BÃ¶ylece okunan veri 5 GB'tan ~100 MB'a iner.
///       2. ONAYLAMA - yoklamayÄ± geÃ§en bÃ¶lgeler tam okunur, Ä±zgara gibi duran en uzun
///                     kesintisiz parÃ§a Ã§Ä±karÄ±lÄ±r.
///       3. SECIM    - adaylar 2 boyutluluÄŸuna gÃ¶re sÄ±ralanÄ±r. GerÃ§ek bir harita, satÄ±r
///                     uzunluÄŸu kadar kaydÄ±rÄ±ldÄ±ÄŸÄ±nda kendine Ã§ok benzer; gÃ¼rÃ¼ltÃ¼ benzemez.
///                     Bu "dÃ¼zenlilik" oranÄ± en yÃ¼ksek olan aday Ã¶ne Ã§Ä±kar.
/// </summary>
internal sealed class ScanLocator : IGridLocator
{
    public string Name => "Tarama";

    private const int Window = 4096;
    private const int ProbeCount = 8;
    private const long MinRegionSize = 64 * 1024;
    private const long MaxRegionSize = 128L * 1024 * 1024;
    private const long MinRunBytes = 48 * 1024;
    private const int MaxDetailed = 150;

    public IReadOnlyList<GridLocation> Locate(IntPtr handle, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var regions = EnumerateRegions(handle);
        log?.Invoke($"{regions.Count} bolge aday boyutta");

        var probe = new byte[Window];
        var passed = new List<(ulong Base, long Size, double Ratio)>();
        var probedBytes = 0L;

        // 1. YOKLAMA - hicbir bolge tam okunmuyor
        foreach (var (regionBase, regionSize) in regions)
        {
            var hits = 0;
            var tried = 0;
            var step = Math.Max(Window, regionSize / ProbeCount);
            for (long off = 0; off + Window <= regionSize; off += step)
            {
                if (!Native.ReadProcessMemory(handle, (IntPtr)(regionBase + (ulong)off), probe,
                        (IntPtr)Window, out var got) || (long)got < Window)
                {
                    continue;
                }

                probedBytes += Window;
                tried++;
                if (LooksLikeGrid(probe, 0, Window)) { hits++; }
            }

            if (hits >= 2) { passed.Add((regionBase, regionSize, (double)hits / Math.Max(1, tried))); }
        }

        // Boyuta gÃ¶re sÄ±ralamak yanlÄ±ÅŸtÄ±: bÃ¼yÃ¼k ama haritayla ilgisi olmayan bloklar
        // listeyi doldurup gerÃ§ek haritayÄ± dÄ±ÅŸarÄ±da bÄ±rakÄ±yordu. AsÄ±l belirleyici,
        // bÃ¶lgenin ne kadarÄ±nÄ±n Ä±zgaraya benzediÄŸi.
        var shortlist = passed
            .OrderByDescending(p => p.Ratio)
            .ThenByDescending(p => p.Size)
            .Take(MaxDetailed)
            .Select(p => (p.Base, p.Size))
            .ToList();
        log?.Invoke($"yoklama {probedBytes / 1024 / 1024} MB -> {passed.Count} bolge gecti, " +
                    $"en buyuk {shortlist.Count} tanesi inceleniyor");

        // 2. ONAYLAMA
        var confirmed = new List<GridLocation>();
        var fullReadBytes = 0L;

        foreach (var (regionBase, regionSize) in shortlist)
        {
            var buf = new byte[regionSize];
            if (!Native.ReadProcessMemory(handle, (IntPtr)regionBase, buf, (IntPtr)regionSize, out var readGot) ||
                (long)readGot < MinRunBytes)
            {
                continue;
            }

            fullReadBytes += (long)readGot;
            var run = LongestRun(buf, (long)readGot);
            if (run.Length < MinRunBytes) { continue; }

            var cells = Unpack(buf, run.Start, run.Length);

            // Tekduze tamponlari ele: tek bir degerden ibaret olanlar her kaydirmada
            // kendine benzer ve duzenlilik olcusunu kandirir.
            if (StrideDetector.DominantValueFraction(cells) > 0.95) { continue; }

            var (stride, strength) = StrideDetector.Detect(cells);
            if (stride <= 0) { continue; }

            var zeros = 0L;
            var band = 0L;
            foreach (var c in cells)
            {
                if (c == 0) { zeros++; }
                else if (c is >= 1 and <= 4) { band++; }
            }

            confirmed.Add(new GridLocation(
                regionBase + (ulong)run.Start, run.Length, stride, strength,
                (double)zeros / cells.Length, (double)band / cells.Length));
        }

        sw.Stop();
        log?.Invoke($"tam okuma {fullReadBytes / 1024 / 1024} MB, {confirmed.Count} aday, " +
                    $"{sw.Elapsed.TotalSeconds:F1} sn");

        // 3. SECIM - once gercek harita gibi duranlar (kenar bandi olanlar)
        return confirmed
            .OrderByDescending(c => c.LooksLikeRealMap)
            .ThenByDescending(c => c.ByteLength)
            .ToList();
    }

    /// <summary>
    ///     Kalibrasyon için: hiçbir ön eleme yapmadan, yoklamayı geçen BÜTÜN bölgeleri
    ///     ve ölçülen satır uzunluklarını döndürür.
    ///
    ///     Normal aramada "hangisi gerçek harita" sorusunu heuristiklerle tahmin ediyoruz
    ///     ve bu tahmin doğru haritayı birkaç kez dışarıda bıraktı. Kalibrasyonda ise
    ///     elimizde gerçek bir hakem var - oyuncunun gezdiği noktalar - o yüzden tahmine
    ///     hiç gerek yok, eleme işini örnekler yapsın.
    /// </summary>
    internal IReadOnlyList<(ulong Base, long Size, int Stride)> Survey(IntPtr handle, Action<string>? log = null)
    {
        return this.SurveyFull(handle, log)
            .Select(g => (g.Address, g.ByteLength, g.Stride))
            .ToList();
    }

    /// <summary>
    ///     Ön eleme yapmadan bulunan bütün ızgara adaylarını, istatistikleriyle döndürür.
    ///     Uygulamanın listesi bunu kullanıyor: otomatik seçim güvenilir olmadığı sürece
    ///     en azından doğru harita listede bulunsun.
    /// </summary>
    internal List<GridLocation> SurveyFull(IntPtr handle, Action<string>? log = null)
    {
        var regions = EnumerateRegions(handle);
        var probe = new byte[Window];
        var result = new List<GridLocation>();
        var read = 0L;

        foreach (var (regionBase, regionSize) in regions)
        {
            var hits = 0;
            var step = Math.Max(Window, regionSize / ProbeCount);
            for (long off = 0; off + Window <= regionSize; off += step)
            {
                if (Native.ReadProcessMemory(handle, (IntPtr)(regionBase + (ulong)off), probe,
                        (IntPtr)Window, out var got) && (long)got == Window &&
                    LooksLikeGrid(probe, 0, Window))
                {
                    hits++;
                }
            }

            if (hits < 2) { continue; }

            var buf = new byte[regionSize];
            if (!Native.ReadProcessMemory(handle, (IntPtr)regionBase, buf, (IntPtr)regionSize, out var g) ||
                (long)g < MinRunBytes)
            {
                continue;
            }

            read += (long)g;
            var run = LongestRun(buf, (long)g);
            if (run.Length < MinRunBytes) { continue; }

            var cells = Unpack(buf, run.Start, run.Length);
            var (stride, strength) = StrideDetector.Detect(cells);
            if (stride <= 0) { continue; }

            var zeros = 0L;
            var band = 0L;
            foreach (var c in cells)
            {
                if (c == 0) { zeros++; }
                else if (c is >= 1 and <= 4) { band++; }
            }

            result.Add(new GridLocation(
                regionBase + (ulong)run.Start, run.Length, stride, strength,
                (double)zeros / cells.Length, (double)band / cells.Length));
        }

        log?.Invoke($"{regions.Count} bolge -> {result.Count} tanesi izgara benzeri, {read / 1024 / 1024} MB okundu");
        return result;
    }

    private static List<(ulong Base, long Size)> EnumerateRegions(IntPtr handle)
    {
        var result = new List<(ulong, long)>();
        ulong address = 0x10000;
        var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();

        while (address < 0x7FFFFFFF0000UL)
        {
            if (Native.VirtualQueryEx(handle, (IntPtr)address, out var mbi, mbiSize) == IntPtr.Zero) { break; }
            var size = (long)mbi.RegionSize;
            if (size <= 0) { break; }

            if (mbi.State == Native.MemCommit &&
                mbi.Type == Native.MemPrivate &&
                (mbi.Protect & Native.PageGuard) == 0 &&
                mbi.Protect == Native.PageReadWrite &&
                size >= MinRegionSize && size <= MaxRegionSize)
            {
                result.Add(((ulong)mbi.BaseAddress.ToInt64(), size));
            }

            address += (ulong)size;
        }

        return result;
    }

    /// <summary>
    ///     HÃ¼cre deÄŸerleri kÃ¼Ã§Ã¼k tamsayÄ± olduÄŸu iÃ§in baytlarÄ±n Ã§eÅŸitliliÄŸi dÃ¼ÅŸÃ¼k ve
    ///     yarÄ±m baytlarÄ± kÃ¼Ã§Ã¼k olur. Tamamen tekdÃ¼ze bloklar (hepsi sÄ±fÄ±r ya da hepsi
    ///     aynÄ± deÄŸer) elenmez - onlar haritanÄ±n bÃ¼yÃ¼k boÅŸ/dolu kÄ±sÄ±mlarÄ± olabiliyor.
    /// </summary>
    private static bool LooksLikeGrid(byte[] buf, long off, int len)
    {
        Span<bool> seen = stackalloc bool[256];
        var distinct = 0;
        var nibbleOk = 0;

        for (var i = 0; i < len; i++)
        {
            var b = buf[off + i];
            if (!seen[b]) { seen[b] = true; distinct++; }
            if ((b & 0x0F) <= 7 && (b >> 4) <= 7) { nibbleOk++; }
        }

        return distinct <= 48 && (double)nibbleOk / len >= 0.98;
    }

    private static (long Start, long Length) LongestRun(byte[] buf, long length)
    {
        long bestStart = 0, bestLen = 0, curStart = -1;

        for (long off = 0; off + Window <= length; off += Window)
        {
            if (LooksLikeGrid(buf, off, Window))
            {
                if (curStart < 0) { curStart = off; }
            }
            else if (curStart >= 0)
            {
                if (off - curStart > bestLen) { bestLen = off - curStart; bestStart = curStart; }
                curStart = -1;
            }
        }

        if (curStart >= 0)
        {
            var end = length - (length % Window);
            if (end - curStart > bestLen) { bestLen = end - curStart; bestStart = curStart; }
        }

        return (bestStart, bestLen);
    }

    internal static byte[] Unpack(byte[] buf, long start, long length)
    {
        var cells = new byte[length * 2];
        for (long i = 0; i < length; i++)
        {
            var b = buf[start + i];
            cells[i * 2] = (byte)(b & 0x0F);
            cells[i * 2 + 1] = (byte)(b >> 4);
        }

        return cells;
    }
}

