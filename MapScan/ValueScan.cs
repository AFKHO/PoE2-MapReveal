using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     DeÄŸiÅŸen deÄŸerleri eleyerek bir sayÄ±yÄ± bulur - Cheat Engine'in yaptÄ±ÄŸÄ± iÅŸin aynÄ±sÄ±,
///     ama kendi kodumuz ve oyuna hiÃ§bir ÅŸey yazmÄ±yor.
///
///     MantÄ±k: aradÄ±ÄŸÄ±mÄ±z ÅŸeyin (oyuncunun konumu, zoom oranÄ±) nasÄ±l davrandÄ±ÄŸÄ±nÄ± biliyoruz.
///     YÃ¼rÃ¼yÃ¼nce deÄŸiÅŸir, dururken deÄŸiÅŸmez. Bellekteki milyonlarca sayÄ±dan bu davranÄ±ÅŸÄ±
///     gÃ¶sterenler her turda elenerek birkaÃ§ taneye iner.
///
///     Adaylar diske yazÄ±lÄ±yor Ã§Ã¼nkÃ¼ turlar arasÄ±nda sen oyunda bir ÅŸey yapÄ±yorsun ve
///     araÃ§ bu sÄ±rada kapanÄ±yor.
///
///     Kullanim:
///       valscan new    &lt;dosya&gt;            ilk tarama - butun makul float'lari topla
///       valscan filter &lt;dosya&gt; &lt;kip&gt;      degisti / degismedi / artti / azaldi
///       valscan show   &lt;dosya&gt; [adet]     kalan adaylari listele
/// </summary>
internal static class ValueScan
{
    private const int ReadBlock = 256 * 1024;
    private const int MaxCandidates = 60_000_000;

    private static bool Plausible(float v) =>
        float.IsFinite(v) && Math.Abs(v) >= 0.5f && Math.Abs(v) <= 200000f;

    internal static int Run(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("valscan new|filter|show <dosya> [kip|adet]"); return 2; }

        return args[0].ToLowerInvariant() switch
        {
            "new" => NewScan(args[1]),
            "filter" => Filter(args[1], args.Length > 2 ? args[2].ToLowerInvariant() : "degisti"),
            "show" => Show(args[1], args.Length > 2 ? int.Parse(args[2]) : 40),
            "pairs" => Pairs(args[1], args.Length > 2 ? int.Parse(args[2]) : 25),
            "track" => Track(args[1], args.Length > 2 ? int.Parse(args[2]) : 12, args.Length > 3 ? int.Parse(args[3]) : 700),
            _ => 2
        };
    }

    private static (IntPtr Handle, Process Game) Open()
    {
        var game = Native.FindGame() ?? throw new InvalidOperationException("Oyun sureci bulunamadi.");
        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { throw new InvalidOperationException("OpenProcess basarisiz."); }
        return (handle, game);
    }

    private static int NewScan(string file)
    {
        var (handle, game) = Open();
        Console.WriteLine($"surec: {game.ProcessName} (pid {game.Id})");

        var addrs = new List<ulong>(1 << 22);
        var vals = new List<float>(1 << 22);
        var sw = Stopwatch.StartNew();
        long scanned = 0;

        try
        {
            ulong address = 0x10000;
            var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();
            var buf = new byte[ReadBlock];

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
                    for (long off = 0; off < size; off += ReadBlock)
                    {
                        var want = (int)Math.Min(ReadBlock, size - off);
                        if (!Native.ReadProcessMemory(handle, (IntPtr)(regionBase + (ulong)off), buf,
                                (IntPtr)want, out var got) || (long)got < 4)
                        {
                            continue;
                        }

                        var n = (long)got;
                        scanned += n;
                        for (long i = 0; i + 4 <= n; i += 4)
                        {
                            var v = BitConverter.ToSingle(buf, (int)i);
                            if (!Plausible(v)) { continue; }
                            addrs.Add(regionBase + (ulong)off + (ulong)i);
                            vals.Add(v);
                            if (addrs.Count >= MaxCandidates) { break; }
                        }

                        if (addrs.Count >= MaxCandidates) { break; }
                    }
                }

                if (addrs.Count >= MaxCandidates) { break; }
                address += (ulong)size;
            }
        }
        finally
        {
            Native.CloseHandle(handle);
        }

        sw.Stop();
        Save(file, addrs, vals);
        Console.WriteLine($"{scanned / 1024 / 1024} MB tarandi, {addrs.Count:N0} aday ({sw.Elapsed.TotalSeconds:F0} sn)");
        Console.WriteLine($"kaydedildi: {Path.GetFullPath(file)}");
        return 0;
    }

    /// <summary>
    ///     Konum neredeyse her zaman yan yana iki (ya da Ã¼Ã§) float olarak tutulur: X, Y.
    ///     Bu yÃ¼zden "hemen 4 bayt sonrasÄ± da aday olan" girdileri tutmak, tek baÅŸÄ±na
    ///     duran gÃ¼rÃ¼ltÃ¼ deÄŸerlerini bedavaya eler.
    /// </summary>
    private static int PairFilter(string file)
    {
        var (addrs, vals) = Load(file);
        var set = new HashSet<ulong>(addrs);
        var keptA = new List<ulong>();
        var keptV = new List<float>();

        for (var i = 0; i < addrs.Length; i++)
        {
            if (set.Contains(addrs[i] + 4)) { keptA.Add(addrs[i]); keptV.Add(vals[i]); }
        }

        Save(file, keptA, keptV);
        Console.WriteLine($"onceki: {addrs.Length:N0}   ciftin ilk yarisi olanlar: {keptA.Count:N0}");

        if (keptA.Count is > 0 and <= 40)
        {
            Console.WriteLine();
            for (var i = 0; i < keptA.Count; i++) { Console.WriteLine($"  {keptA[i]:X}   {keptV[i]}"); }
        }

        return 0;
    }

    private static int Filter(string file, string mode)
    {
        if (mode == "cift") { return PairFilter(file); }

        var (addrs, vals) = Load(file);
        Console.WriteLine($"onceki aday sayisi: {addrs.Length:N0}   kip: {mode}");

        var (handle, _) = Open();
        var keptA = new List<ulong>();
        var keptV = new List<float>();
        var sw = Stopwatch.StartNew();

        try
        {
            var buf = new byte[ReadBlock];
            ulong blockBase = 0;
            long blockLen = 0;

            for (var i = 0; i < addrs.Length; i++)
            {
                var a = addrs[i];
                if (a < blockBase || a + 4 > blockBase + (ulong)blockLen)
                {
                    blockBase = a & ~0xFFFUL;
                    if (!Native.ReadProcessMemory(handle, (IntPtr)blockBase, buf, (IntPtr)ReadBlock, out var got) ||
                        (long)got < 4)
                    {
                        blockLen = 0;
                        continue;
                    }

                    blockLen = (long)got;
                }

                var offset = (int)(a - blockBase);
                if (offset + 4 > blockLen) { continue; }

                var now = BitConverter.ToSingle(buf, offset);
                var before = vals[i];
                if (!float.IsFinite(now)) { continue; }

                var keep = mode switch
                {
                    "degisti" => Math.Abs(now - before) > 0.0001f,
                    "degismedi" => Math.Abs(now - before) <= 0.0001f,
                    "artti" => now > before + 0.0001f,
                    "azaldi" => now < before - 0.0001f,
                    _ => false
                };

                if (keep) { keptA.Add(a); keptV.Add(now); }
            }
        }
        finally
        {
            Native.CloseHandle(handle);
        }

        sw.Stop();
        Save(file, keptA, keptV);
        Console.WriteLine($"kalan aday: {keptA.Count:N0}   ({sw.Elapsed.TotalSeconds:F0} sn)");

        if (keptA.Count is > 0 and <= 40)
        {
            Console.WriteLine();
            for (var i = 0; i < keptA.Count; i++)
            {
                Console.WriteLine($"  {keptA[i]:X}   {keptV[i]}");
            }
        }

        return 0;
    }

    /// <summary>
    ///     Kalan adaylarÄ± (X, Y) Ã§ifti olarak okur ve aynÄ± Ã§iftin kaÃ§ ayrÄ± adreste
    ///     gÃ¶rÃ¼ndÃ¼ÄŸÃ¼nÃ¼ sayar. Oyuncunun konumu bellekte birden fazla kopyada tutulur,
    ///     bu yÃ¼zden en Ã§ok tekrar eden Ã§ift gÃ¼Ã§lÃ¼ adaydÄ±r.
    /// </summary>
    internal static int Pairs(string file, int top)
    {
        var (addrs, _) = Load(file);
        var (handle, _) = Open();
        var groups = new Dictionary<(int X, int Y), List<ulong>>();

        try
        {
            var eight = new byte[8];
            foreach (var a in addrs)
            {
                if (!Native.ReadProcessMemory(handle, (IntPtr)a, eight, (IntPtr)8, out var got) || (long)got != 8)
                {
                    continue;
                }

                var x = BitConverter.ToSingle(eight, 0);
                var y = BitConverter.ToSingle(eight, 4);
                if (!float.IsFinite(x) || !float.IsFinite(y)) { continue; }

                var key = ((int)Math.Round(x), (int)Math.Round(y));
                if (!groups.TryGetValue(key, out var list)) { groups[key] = list = new List<ulong>(); }
                list.Add(a);
            }
        }
        finally
        {
            Native.CloseHandle(handle);
        }

        Console.WriteLine($"{addrs.Length:N0} aday -> {groups.Count:N0} farkli (X,Y) cifti");
        Console.WriteLine();
        Console.WriteLine("EN COK TEKRAR EDEN CIFTLER");
        foreach (var g in groups.OrderByDescending(g => g.Value.Count).Take(top))
        {
            var where = string.Join(", ", g.Value.Take(6).Select(a => a.ToString("X")));
            Console.WriteLine($"  ({g.Key.X,7}, {g.Key.Y,7})  x{g.Value.Count,3}   {where}");
        }

        return 0;
    }

    /// <summary>
    ///     Adayları sen yürürken arka arkaya örnekler ve hareketin düzgünlüğüne bakar.
    ///
    ///     Gerçek bir konum, kare kare küçük ve birbirine yakın adımlarla ilerler. Rastgele
    ///     bir dizi ya da sayaç ise ya hiç kıpırdamaz ya da zıplar. "En büyük adım / ortalama
    ///     adım" oranı bu ikisini net ayırıyor: konumda bu oran 1'e yakın, gürültüde büyük.
    /// </summary>
    internal static int Track(string file, int samples, int intervalMs)
    {
        var (addrs, _) = Load(file);
        var (handle, _) = Open();

        var xs = new float[addrs.Length, 2];
        var series = new List<(float X, float Y)[]>();

        try
        {
            for (var s = 0; s < samples; s++)
            {
                var snap = new (float, float)[addrs.Length];
                var eight = new byte[8];
                for (var i = 0; i < addrs.Length; i++)
                {
                    if (Native.ReadProcessMemory(handle, (IntPtr)addrs[i], eight, (IntPtr)8, out var got) &&
                        (long)got == 8)
                    {
                        snap[i] = (BitConverter.ToSingle(eight, 0), BitConverter.ToSingle(eight, 4));
                    }
                    else
                    {
                        snap[i] = (float.NaN, float.NaN);
                    }
                }

                series.Add(snap);
                Console.WriteLine($"  ornek {s + 1}/{samples}");
                if (s < samples - 1) { Thread.Sleep(intervalMs); }
            }
        }
        finally
        {
            Native.CloseHandle(handle);
        }

        var scored = new List<(ulong Addr, double Mean, double Max, double Total, float X, float Y)>();

        for (var i = 0; i < addrs.Length; i++)
        {
            double total = 0, max = 0;
            var moves = 0;
            var ok = true;

            for (var s = 1; s < series.Count; s++)
            {
                var (px, py) = series[s - 1][i];
                var (cx, cy) = series[s][i];
                if (!float.IsFinite(px) || !float.IsFinite(cx)) { ok = false; break; }

                var d = Math.Sqrt((cx - px) * (double)(cx - px) + (cy - py) * (double)(cy - py));
                total += d;
                if (d > max) { max = d; }
                if (d > 0.001) { moves++; }
            }

            if (!ok || moves < series.Count / 2) { continue; }

            var mean = total / (series.Count - 1);
            if (mean <= 0) { continue; }

            var last = series[^1][i];
            scored.Add((addrs[i], mean, max, total, last.X, last.Y));
        }

        Console.WriteLine();
        Console.WriteLine($"surekli hareket eden aday: {scored.Count}");
        Console.WriteLine();
        Console.WriteLine("EN DUZGUN HAREKET EDENLER  (max/ort orani 1'e yakin olan gercek konumdur)");
        Console.WriteLine("adres            ort adim   max adim   oran    son (X, Y)");

        foreach (var c in scored.OrderBy(c => c.Max / c.Mean).Take(25))
        {
            Console.WriteLine($"{c.Addr:X}   {c.Mean,8:F2}   {c.Max,8:F2}   {c.Max / c.Mean,5:F2}   " +
                              $"({c.X,10:F1}, {c.Y,10:F1})");
        }

        return 0;
    }

    private static int Show(string file, int count)
    {
        var (addrs, vals) = Load(file);
        Console.WriteLine($"aday sayisi: {addrs.Length:N0}");

        var (handle, _) = Open();
        try
        {
            var four = new byte[4];
            for (var i = 0; i < Math.Min(count, addrs.Length); i++)
            {
                var now = float.NaN;
                if (Native.ReadProcessMemory(handle, (IntPtr)addrs[i], four, (IntPtr)4, out var got) && (long)got == 4)
                {
                    now = BitConverter.ToSingle(four, 0);
                }

                Console.WriteLine($"  {addrs[i]:X}   kayitli {vals[i],14}   simdi {now,14}");
            }
        }
        finally
        {
            Native.CloseHandle(handle);
        }

        return 0;
    }

    private static void Save(string file, List<ulong> addrs, List<float> vals)
    {
        using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new BinaryWriter(fs);
        w.Write(addrs.Count);
        for (var i = 0; i < addrs.Count; i++) { w.Write(addrs[i]); w.Write(vals[i]); }
    }

    private static (ulong[] Addrs, float[] Vals) Load(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var r = new BinaryReader(fs);
        var n = r.ReadInt32();
        var addrs = new ulong[n];
        var vals = new float[n];
        for (var i = 0; i < n; i++) { addrs[i] = r.ReadUInt64(); vals[i] = r.ReadSingle(); }
        return (addrs, vals);
    }
}


