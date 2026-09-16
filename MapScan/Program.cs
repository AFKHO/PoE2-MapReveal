using System.Globalization;
using System.Text;

namespace Poe2Map;

/// <summary>
///     PoE2 sÃ¼recinin belleÄŸinde harita (yÃ¼rÃ¼nebilirlik) verisi arayan tarayÄ±cÄ±.
///
///     BÃ¶lge baÅŸÄ±na istatistik iÅŸe yaramÄ±yor: bÃ¼yÃ¼k heap arenalarÄ± 50-65 MB ve iÃ§lerinde
///     her tÃ¼r veri karÄ±ÅŸÄ±k duruyor. Izgara, bÃ¶yle bir arenanÄ±n *iÃ§inde* bir tampon. O
///     yÃ¼zden bÃ¶lgeleri pencerelere bÃ¶lÃ¼p her pencereyi ayrÄ± puanlÄ±yoruz, sonra ardÄ±ÅŸÄ±k
///     "Ä±zgara gibi" pencereleri birleÅŸtirip aday tamponlarÄ± Ã§Ä±karÄ±yoruz.
///
///     Izgara nasÄ±l gÃ¶rÃ¼nÃ¼r: hÃ¼cre deÄŸerleri kÃ¼Ã§Ã¼k tamsayÄ± olduÄŸu iÃ§in baytlarÄ±n Ã§eÅŸitliliÄŸi
///     dÃ¼ÅŸÃ¼k ve yarÄ±m baytlarÄ± (nibble) kÃ¼Ã§Ã¼k. Ama asÄ±l kanÄ±t karÅŸÄ±laÅŸtÄ±rmadan gelir:
///     alan deÄŸiÅŸince iÃ§eriÄŸi tamamen deÄŸiÅŸen, dururken deÄŸiÅŸmeyen tampon.
///
///     Komutlar:
///       scan   [etiket]                     anlÄ±k gÃ¶rÃ¼ntÃ¼ al, aday tamponlarÄ± bas
///       diff   [snapA] [snapB]              iki anlÄ±k gÃ¶rÃ¼ntÃ¼yÃ¼ karÅŸÄ±laÅŸtÄ±r
///       dump   [hexadres] [boyut] [dosya]   bir bÃ¶lgeyi diske yaz
///       render [dosya] [geniÅŸlik?]          dÃ¶kÃ¼len tamponu BMP olarak Ã§iz (geniÅŸlik yoksa otomatik bul)
///
///     Sadece okuma yapar. SÃ¼rece hiÃ§bir ÅŸey yazÄ±lmaz.
/// </summary>
internal static class Program
{
    private const int Window = 4096;
    private const long MinRegionSize = 64 * 1024;
    private const long MaxRegionSize = 256L * 1024 * 1024;
    private const long MinRunBytes = 32 * 1024;

    private static readonly string OutDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "snapshots"));

    private static int Main(string[] args)
    {
        try
        {
            var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "scan";
            return cmd switch
            {
                "scan" => Scan(args.Length > 1 ? args[1] : "snap"),
                "diff" => Diff(args[1], args[2]),
                "dump" => Dump(args[1], args[2], args[3]),
                "render" => Render(args[1], args.Length > 2 ? int.Parse(args[2]) : 0,
                                             args.Length > 3 ? int.Parse(args[3]) : 0),
                "strides" => Strides(args[1]),
                "region" => RegionInfo(args[1]),
                "resolve" => Resolve(args.Skip(1).ToArray()),
                "valscan" => ValueScan.Run(args.Skip(1).ToArray()),
                "minimap" => MinimapStruct.Run(),
                "terrain" => TerrainFinder.Run(),
                "livearea" => LiveArea.Run(),
                "ui" => UiFinder.Run(),
                "uichain" => UiFinder.RunChain(args[1]),
                "mapwatch" => UiFinder.Watch(),
                "mapdiff" => UiFinder.Diff(args.Skip(1).ToArray()),
                "uisnap" => UiFinder.Snapshot(false),
                "uidiff" => UiFinder.Snapshot(true),
                "playerhunt" => PlayerHunt.Run(args.Skip(1).ToArray()),
                "poshunt" => PosHunt.Run(args.Skip(1).ToArray()),
                "camhunt" => CamHunt.Run(args.Skip(1).ToArray()),
                "pin" => Pin.Cli(),
                "monsters" => MonstersCommand.Run(),
                "playerentity" => PlayerEntityCommand.Run(),
                "exits" => ExitsCommand.Run(),
                "tiles" => TilesCommand.Run(),
                "arealink" => AreaLinkCommand.Run(),
                "camera" => args.Length > 1 && args[1].Equals("track", StringComparison.OrdinalIgnoreCase)
                    ? CameraFinder.Track()
                    : CameraFinder.Run(args.Length > 1 && args[1].Equals("all", StringComparison.OrdinalIgnoreCase)),
                "refscan" => PointerScan.RefScan(args[1], args[2],
                    args.Length > 3
                        ? ulong.Parse(args[3].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber)
                        : 0),
                "ptrscan" => PointerScan.Run(args[1],
                    args.Length > 2 ? int.Parse(args[2]) : 4,
                    args.Length > 3 ? int.Parse(args[3]) : 0x1000),
                _ => Usage()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("HATA: " + ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("kullanim:");
        Console.WriteLine("  MapScan scan [etiket]");
        Console.WriteLine("  MapScan diff <snapA.tsv> <snapB.tsv>");
        Console.WriteLine("  MapScan dump <hexadres> <boyut> <dosya>");
        Console.WriteLine("  MapScan render <dosya> [genislik]");
        Console.WriteLine("  MapScan strides <dosya>");
        return 2;
    }

    /// <summary>
    ///     SatÄ±r uzunluÄŸu adaylarÄ±nÄ± puanlarÄ±yla listeler. Bir Ä±zgarada satÄ±r uzunluÄŸu kadar
    ///     kaydÄ±rÄ±lmÄ±ÅŸ hÃ¢l kendine Ã§ok benzer, o yÃ¼zden farkÄ±n en kÃ¼Ã§Ã¼k olduÄŸu kaydÄ±rma
    ///     satÄ±r uzunluÄŸudur. Tek bir tahmin yerine ilk 12 adayÄ± basÄ±yoruz ki katlarÄ±
    ///     (2w, 3w...) ve yakÄ±n komÅŸularÄ± da gÃ¶rÃ¼p doÄŸrusunu seÃ§ebilelim.
    /// </summary>
    private static int Strides(string file)
    {
        var raw = File.ReadAllBytes(file);
        var cells = new byte[raw.Length * 2];
        for (var i = 0; i < raw.Length; i++)
        {
            cells[i * 2] = (byte)(raw[i] & 0x0F);
            cells[i * 2 + 1] = (byte)(raw[i] >> 4);
        }

        Console.WriteLine($"dosya {raw.Length} bayt -> {cells.Length} hucre (nibble acilmis)");
        Console.WriteLine();

        var scored = new List<(int Width, double Score)>();
        var sample = Math.Min(cells.Length, 1 << 21);

        for (var w = 64; w <= 4096 && w * 4 < sample; w++)
        {
            long diff = 0;
            long counted = 0;
            var n = sample - w;
            var step = Math.Max(1, n / 300000);
            for (var i = 0; i < n; i += step)
            {
                diff += Math.Abs(cells[i] - cells[i + w]);
                counted++;
            }

            scored.Add((w, (double)diff / counted));
        }

        Console.WriteLine("EN IYI 12 SATIR UZUNLUGU (dusuk puan = iyi)");
        Console.WriteLine("genislik   puan     yukseklik(bu dosyada)");
        foreach (var (w, s) in scored.OrderBy(x => x.Score).Take(12))
        {
            Console.WriteLine($"{w,8}   {s,6:F4}   {cells.Length / w,8}");
        }

        return 0;
    }

    private sealed record Run(ulong Base, long Length, int Distinct, double Nibble, double Zero, ulong Hash, string Sample);

    /// <summary>
    ///     Bir iÅŸaretÃ§i zincirini takip eder ve her adÄ±mÄ± basar.
    ///     Kullanim: resolve &lt;statikRVA&gt; &lt;ofset1&gt; &lt;ofset2&gt; ...
    ///     Hepsi onaltilik. Son adimda varilan adresin izgara gibi durup durmadigini da soyler.
    /// </summary>
    private static int Resolve(string[] args)
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        ulong modBase;
        try { modBase = (ulong)game.MainModule!.BaseAddress.ToInt64(); }
        catch (Exception ex) { Console.Error.WriteLine("ana modul alinamadi: " + ex.Message); return 1; }

        var rva = ulong.Parse(args[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber);
        var offsets = args.Skip(1)
            .Select(a => ulong.Parse(a.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber))
            .ToArray();

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            Console.WriteLine($"modul basi : {modBase:X}");
            var cursor = modBase + rva;
            Console.WriteLine($"statik     : modul+0x{rva:X} = {cursor:X}");

            var eight = new byte[8];
            for (var i = 0; i <= offsets.Length; i++)
            {
                if (!Native.ReadProcessMemory(handle, (IntPtr)cursor, eight, (IntPtr)8, out var got) || (long)got != 8)
                {
                    Console.Error.WriteLine($"  adim {i}: {cursor:X} okunamadi - zincir kirik");
                    return 1;
                }

                var value = BitConverter.ToUInt64(eight, 0);
                if (i == offsets.Length)
                {
                    Console.WriteLine($"  son deger  : {value:X}");
                    cursor = value;
                    break;
                }

                Console.WriteLine($"  [{cursor:X}] = {value:X}   +0x{offsets[i]:X}  ->  {value + offsets[i]:X}");
                cursor = value + offsets[i];
            }

            Console.WriteLine();
            Console.WriteLine($"VARILAN ADRES: {cursor:X}");

            var probe = new byte[4096];
            if (Native.ReadProcessMemory(handle, (IntPtr)cursor, probe, (IntPtr)probe.Length, out var pgot) &&
                (long)pgot == probe.Length)
            {
                var seen = new bool[256];
                var distinct = 0;
                var nibbleOk = 0;
                foreach (var b in probe)
                {
                    if (!seen[b]) { seen[b] = true; distinct++; }
                    if ((b & 0x0F) <= 7 && (b >> 4) <= 7) { nibbleOk++; }
                }

                var frac = (double)nibbleOk / probe.Length;
                Console.WriteLine($"ilk 4KB: distinct={distinct}  nibble={frac:F3}  ->  " +
                                  (distinct <= 48 && frac >= 0.98 ? "IZGARA GIBI DURUYOR" : "izgara gibi DURMUYOR"));
                var sb = new StringBuilder();
                for (var i = 0; i < 24; i++) { sb.Append(probe[i].ToString("X2")).Append(' '); }
                Console.WriteLine("ornek  : " + sb.ToString().TrimEnd());
            }
            else
            {
                Console.WriteLine("varilan adres okunamadi.");
            }

            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    ///     Bir adresin hangi tahsisin (allocation) iÃ§inde olduÄŸunu sÃ¶yler. Ä°ÅŸaretÃ§iler
    ///     tamponun ortasÄ±nÄ± deÄŸil tahsis baÅŸÄ±nÄ± gÃ¶sterdiÄŸi iÃ§in bunu bilmemiz gerekiyor.
    /// </summary>
    private static int RegionInfo(string hexAddress)
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var address = ulong.Parse(hexAddress.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber);
        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();
            if (Native.VirtualQueryEx(handle, (IntPtr)address, out var mbi, mbiSize) == IntPtr.Zero)
            {
                Console.Error.WriteLine("VirtualQueryEx basarisiz.");
                return 1;
            }

            Console.WriteLine($"sorulan adres  : {address:X}");
            Console.WriteLine($"bolge basi     : {(ulong)mbi.BaseAddress.ToInt64():X}");
            Console.WriteLine($"TAHSIS BASI    : {(ulong)mbi.AllocationBase.ToInt64():X}");
            Console.WriteLine($"bolge boyutu   : {(long)mbi.RegionSize} (0x{(long)mbi.RegionSize:X})");
            Console.WriteLine($"durum/koruma/tip: {mbi.State:X} / {mbi.Protect:X} / {mbi.Type:X}");
            Console.WriteLine($"adres - tahsis  : 0x{address - (ulong)mbi.AllocationBase.ToInt64():X}");

            // VirtualQueryEx sorulan adresi taban kabul edip oradan itibaren kalan boyutu
            // dÃ¶ndÃ¼rÃ¼yor. GerÃ§ek bÃ¶lge sÄ±nÄ±rlarÄ±nÄ± gÃ¶rmek iÃ§in tahsis baÅŸÄ±ndan ileri yÃ¼rÃ¼yoruz.
            var allocBase = (ulong)mbi.AllocationBase.ToInt64();
            ulong cursor = allocBase;
            long total = 0;
            while (Native.VirtualQueryEx(handle, (IntPtr)cursor, out var m2, mbiSize) != IntPtr.Zero)
            {
                if ((ulong)m2.AllocationBase.ToInt64() != allocBase) { break; }
                var rs = (long)m2.RegionSize;
                if (rs <= 0) { break; }

                var regEnd = cursor + (ulong)rs;
                if (m2.State == Native.MemCommit && address >= cursor && address < regEnd)
                {
                    Console.WriteLine();
                    Console.WriteLine("*** ADRESI ICEREN GERCEK BOLGE ***");
                    Console.WriteLine($"    bas    : {cursor:X}");
                    Console.WriteLine($"    boyut  : {rs} (0x{rs:X})");
                    Console.WriteLine($"    son    : {regEnd:X}");
                    Console.WriteLine($"    adres bu bolgede +0x{address - cursor:X}");
                }

                total += rs;
                cursor = regEnd;
            }

            Console.WriteLine();
            Console.WriteLine($"tahsis toplami : {total} (0x{total:X})  ->  {allocBase:X} - {allocBase + (ulong)total:X}");
            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    // ---------------------------------------------------------------- scan

    private static int Scan(string label)
    {
        var game = Native.FindGame();
        if (game == null)
        {
            Console.Error.WriteLine("Oyun sureci bulunamadi. PoE2 acik mi?");
            return 1;
        }

        Console.WriteLine($"surec : {game.ProcessName} (pid {game.Id})  bellek {game.WorkingSet64 / 1024 / 1024} MB");

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero)
        {
            Console.Error.WriteLine("OpenProcess basarisiz. Yonetici olarak calistirmayi dene.");
            return 1;
        }

        var runs = new List<Run>();
        long bytesRead = 0;
        var regionCount = 0;

        try
        {
            ulong address = 0x10000;
            var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();

            while (address < 0x7FFFFFFF0000UL)
            {
                if (Native.VirtualQueryEx(handle, (IntPtr)address, out var mbi, mbiSize) == IntPtr.Zero) { break; }

                var size = (long)mbi.RegionSize;
                if (size <= 0) { break; }

                var usable =
                    mbi.State == Native.MemCommit &&
                    mbi.Type == Native.MemPrivate &&
                    (mbi.Protect & Native.PageGuard) == 0 &&
                    (mbi.Protect == Native.PageReadWrite || mbi.Protect == Native.PageReadOnly) &&
                    size >= MinRegionSize && size <= MaxRegionSize;

                if (usable)
                {
                    var buf = new byte[size];
                    if (Native.ReadProcessMemory(handle, mbi.BaseAddress, buf, (IntPtr)size, out var got) &&
                        (long)got >= Window)
                    {
                        regionCount++;
                        bytesRead += (long)got;
                        FindRuns((ulong)mbi.BaseAddress.ToInt64(), buf, (long)got, runs);
                    }
                }

                address += (ulong)size;
            }
        }
        finally
        {
            Native.CloseHandle(handle);
        }

        Console.WriteLine($"taranan bolge: {regionCount}   okunan: {bytesRead / 1024 / 1024} MB   aday tampon: {runs.Count}");
        Console.WriteLine();

        Directory.CreateDirectory(OutDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var snapPath = Path.Combine(OutDir, $"{label}_{stamp}.tsv");

        using (var w = new StreamWriter(snapPath, false, Encoding.UTF8))
        {
            w.WriteLine("base\tlength\tdistinct\tnibble\tzero\thash\tsample");
            foreach (var r in runs.OrderBy(r => r.Base))
            {
                w.WriteLine(string.Join('\t',
                    r.Base.ToString("X"), r.Length, r.Distinct,
                    r.Nibble.ToString("F3", CultureInfo.InvariantCulture),
                    r.Zero.ToString("F3", CultureInfo.InvariantCulture),
                    r.Hash.ToString("X16"), r.Sample));
            }
        }

        Console.WriteLine("EN BUYUK 30 ADAY TAMPON");
        Console.WriteLine("base            uzunluk    distinct  nibble  zero   ornek");
        foreach (var r in runs.OrderByDescending(r => r.Length).Take(30))
        {
            Console.WriteLine($"{r.Base:X12}  {r.Length,9}  {r.Distinct,7}  {r.Nibble,6:F3}  {r.Zero,5:F3}  {r.Sample}");
        }

        Console.WriteLine();
        Console.WriteLine("anlik goruntu: " + snapPath);
        return 0;
    }

    /// <summary>
    ///     BÃ¶lgeyi pencerelere bÃ¶ler, "Ä±zgara gibi" ardÄ±ÅŸÄ±k pencereleri tek tampon olarak birleÅŸtirir.
    /// </summary>
    private static void FindRuns(ulong regionBase, byte[] buf, long length, List<Run> into)
    {
        long runStart = -1;

        for (long off = 0; off + Window <= length; off += Window)
        {
            if (LooksLikeGrid(buf, off, Window))
            {
                if (runStart < 0) { runStart = off; }
            }
            else if (runStart >= 0)
            {
                Emit(regionBase, buf, runStart, off - runStart, into);
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            var end = length - (length % Window);
            Emit(regionBase, buf, runStart, end - runStart, into);
        }
    }

    private static bool LooksLikeGrid(byte[] buf, long off, int len)
    {
        Span<bool> seen = stackalloc bool[256];
        var distinct = 0;
        var nibbleOk = 0;
        var zero = 0;

        for (var i = 0; i < len; i++)
        {
            var b = buf[off + i];
            if (!seen[b]) { seen[b] = true; distinct++; }
            if ((b & 0x0F) <= 7 && (b >> 4) <= 7) { nibbleOk++; }
            if (b == 0) { zero++; }
        }

        // KÃ¼Ã§Ã¼k deÄŸerli, az Ã§eÅŸitli, ama tamamen boÅŸ olmayan.
        return distinct is >= 3 and <= 48
               && (double)nibbleOk / len >= 0.98
               && (double)zero / len <= 0.92;
    }

    private static void Emit(ulong regionBase, byte[] buf, long off, long len, List<Run> into)
    {
        if (len < MinRunBytes) { return; }

        Span<bool> seen = stackalloc bool[256];
        var distinct = 0;
        long nibbleOk = 0;
        long zero = 0;
        var hash = 1469598103934665603UL;

        for (long i = 0; i < len; i++)
        {
            var b = buf[off + i];
            if (!seen[b]) { seen[b] = true; distinct++; }
            if ((b & 0x0F) <= 7 && (b >> 4) <= 7) { nibbleOk++; }
            if (b == 0) { zero++; }
            hash = (hash ^ b) * 1099511628211UL;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < 20 && i < len; i++) { sb.Append(buf[off + i].ToString("X2")).Append(' '); }

        into.Add(new Run(regionBase + (ulong)off, len, distinct,
            (double)nibbleOk / len, (double)zero / len, hash, sb.ToString().TrimEnd()));
    }

    // ---------------------------------------------------------------- diff

    private static int Diff(string a, string b)
    {
        var mapA = LoadSnap(a);
        var mapB = LoadSnap(b);

        var changed = new List<string[]>();
        var onlyB = new List<string[]>();

        foreach (var (key, partsB) in mapB)
        {
            if (mapA.TryGetValue(key, out var partsA))
            {
                if (partsA[5] != partsB[5]) { changed.Add(partsB); }
            }
            else
            {
                onlyB.Add(partsB);
            }
        }

        Console.WriteLine($"A: {mapA.Count} tampon   B: {mapB.Count} tampon");
        Console.WriteLine($"ayni yerde ama ICERIGI DEGISEN: {changed.Count}   sadece B'de olan: {onlyB.Count}");
        Console.WriteLine();
        Console.WriteLine("=== ICERIGI DEGISENLER (en buyuk 25) - EN GUCLU ADAYLAR ===");
        Console.WriteLine("base            uzunluk    distinct  nibble  zero   ornek");
        foreach (var p in changed.OrderByDescending(p => long.Parse(p[1])).Take(25))
        {
            Console.WriteLine($"{p[0],14}  {p[1],9}  {p[2],7}  {p[3],6}  {p[4],5}  {p[6]}");
        }

        Console.WriteLine();
        Console.WriteLine("=== SADECE IKINCI TARAMADA OLANLAR (en buyuk 15) ===");
        Console.WriteLine("base            uzunluk    distinct  nibble  zero   ornek");
        foreach (var p in onlyB.OrderByDescending(p => long.Parse(p[1])).Take(15))
        {
            Console.WriteLine($"{p[0],14}  {p[1],9}  {p[2],7}  {p[3],6}  {p[4],5}  {p[6]}");
        }

        return 0;
    }

    private static Dictionary<string, string[]> LoadSnap(string path)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length < 7) { continue; }
            result[parts[0] + ":" + parts[1]] = parts;
        }

        return result;
    }

    // ---------------------------------------------------------------- dump

    private static int Dump(string hexAddress, string sizeText, string file)
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var address = ulong.Parse(hexAddress.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber);
        var size = int.Parse(sizeText);

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            var buf = new byte[size];
            if (!Native.ReadProcessMemory(handle, (IntPtr)address, buf, (IntPtr)size, out var got))
            {
                Console.Error.WriteLine("ReadProcessMemory basarisiz.");
                return 1;
            }

            File.WriteAllBytes(file, buf.AsSpan(0, (int)got).ToArray());
            Console.WriteLine($"{(long)got} bayt yazildi: {Path.GetFullPath(file)}");
            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    // ---------------------------------------------------------------- render

    /// <summary>
    ///     DÃ¶kÃ¼len tamponu gÃ¶rÃ¼ntÃ¼ olarak Ã§izer. GeniÅŸlik verilmezse otomatik bulur:
    ///     bir Ä±zgaranÄ±n satÄ±r uzunluÄŸu kadar kaydÄ±rÄ±lmÄ±ÅŸ hÃ¢li kendine Ã§ok benzer,
    ///     bu yÃ¼zden farkÄ±n en kÃ¼Ã§Ã¼k olduÄŸu kaydÄ±rma satÄ±r geniÅŸliÄŸidir.
    /// </summary>
    /// <param name="width">Bayt baÅŸÄ±na bir hÃ¼cre varsayÄ±mÄ± iÃ§in satÄ±r uzunluÄŸu (0 = otomatik).</param>
    /// <param name="nibbleWidth">
    ///     Nibble aÃ§Ä±lmÄ±ÅŸ hÃ¢l iÃ§in satÄ±r uzunluÄŸu. 0 ise <paramref name="width" />'in iki katÄ±,
    ///     o da 0 ise otomatik. AyrÄ± verilebiliyor Ã§Ã¼nkÃ¼ satÄ±r uzunluÄŸu tek sayÄ± olabiliyor -
    ///     bu Ä±zgarada 621 - ve o durumda bayt cinsinden tam sayÄ± karÅŸÄ±lÄ±ÄŸÄ± yok.
    /// </param>
    private static int Render(string file, int width, int nibbleWidth = 0)
    {
        var data = File.ReadAllBytes(file);
        if (data.Length < 4096) { Console.Error.WriteLine("dosya cok kucuk"); return 1; }

        // 1) Bayt baÅŸÄ±na bir hÃ¼cre varsayÄ±mÄ±
        RenderOne(file, "_bayt", data, width);

        // 2) Bayt baÅŸÄ±na iki hÃ¼cre (nibble paketli) varsayÄ±mÄ±
        var unpacked = new byte[data.Length * 2];
        for (var i = 0; i < data.Length; i++)
        {
            unpacked[i * 2] = (byte)(data[i] & 0x0F);
            unpacked[i * 2 + 1] = (byte)(data[i] >> 4);
        }

        var nw = nibbleWidth > 0 ? nibbleWidth : (width > 0 ? width * 2 : 0);
        RenderOne(file, "_nibble", unpacked, nw);
        return 0;
    }

    private static void RenderOne(string file, string suffix, byte[] cells, int width)
    {
        if (width <= 0)
        {
            width = DetectWidth(cells);
        }

        var height = cells.Length / width;
        if (height < 4)
        {
            Console.WriteLine($"{suffix}: genislik {width} makul degil, atlandi");
            return;
        }

        var maxValue = 1;
        foreach (var b in cells) { if (b > maxValue && b <= 32) { maxValue = b; } }

        var pixels = new byte[(long)width * height <= int.MaxValue ? width * height : 0];
        for (var i = 0; i < pixels.Length; i++)
        {
            var v = cells[i];
            pixels[i] = v == 0 ? (byte)0 : (byte)Math.Min(255, 50 + 205 * v / maxValue);
        }

        var outPath = Path.ChangeExtension(file, null) + suffix + ".png";
        Png.WriteGray(outPath, pixels, width, height);
        Console.WriteLine($"{suffix}: {width} x {height}  (maks deger {maxValue})  ->  {Path.GetFullPath(outPath)}");
    }

    private static int DetectWidth(byte[] data)
    {
        var sample = Math.Min(data.Length, 1 << 20);
        var bestWidth = 64;
        var bestScore = double.MaxValue;

        for (var w = 16; w <= 8192 && w * 4 < sample; w++)
        {
            long diff = 0;
            var n = sample - w;
            var step = Math.Max(1, n / 200000);
            long counted = 0;
            for (var i = 0; i < n; i += step)
            {
                diff += Math.Abs(data[i] - data[i + w]);
                counted++;
            }

            var score = (double)diff / counted;
            if (score < bestScore) { bestScore = score; bestWidth = w; }
        }

        return bestWidth;
    }

}




