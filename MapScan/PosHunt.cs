using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Oyuncunun konum alanini hicbir offset varsaymadan bulur.
///
///     Patch zincirin hem baslangicini hem seklini degistirdiginde tek saglam dayanak
///     kaliyor: zemin izgarasi. Onu yapisal imzayla buluyoruz, yani patch'ten
///     etkilenmiyor. Oyuncunun konumu da su uc sarti birden tutmak zorunda:
///
///       1. Uc ardisik float, makul dunya araliginda
///       2. Denk geldigi hucre bir izgaranin icinde ve YURUNEBILIR
///       3. Yuruyunce degisiyor, ve degistikten SONRA da yurunebilir hucrede
///
///     Ucuncusu belirleyici. Rastgele bir float cifti degisebilir, ama degisip
///     yine ayni izgaranin yurunebilir bir hucresine dusmesi tesadufe kapali.
///     Bulunan adresten nesneye, oradan sabit zincire ptrscan ile cikiliyor.
/// </summary>
internal static class PosHunt
{
    private const int ReadBlock = 1 << 20;
    private const int MaxCandidates = 4_000_000;
    private const int Rounds = 3;

    internal static int Run(string[] args)
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            var terrains = TerrainFinder.Find(handle, Console.WriteLine);
            if (terrains.Count == 0)
            {
                Console.Error.WriteLine("Zemin yapisi bulunamadi - bir alanda misin?");
                return 1;
            }

            // "poshunt areapath [derinlik]": kayitli zincirden oyuncu nesnesini cozup
            // ALAN yapisindan ona giden yolu arar. Yurume gerektirmiyor - zincir zaten
            // kayitli. Alan tabanli yol, modul zincirinin oyun yeniden baslatilinca
            // kirilmasina karsi tek kalici cozum.
            if (args.Length >= 1 && args[0].Equals("areapath", StringComparison.OrdinalIgnoreCase))
            {
                var depth = args.Length > 1 ? int.Parse(args[1]) : 6;
                ulong modBase = 0;
                try { modBase = (ulong)game.MainModule!.BaseAddress.ToInt64(); } catch { }

                var obj = PlayerChain.Resolve(handle, modBase);
                if (!PlayerChain.LooksLikePointer(obj))
                {
                    Console.Error.WriteLine("Kayitli zincir cozulemedi - once 'MapScan poshunt'.");
                    return 1;
                }

                var pos = PlayerChain.ReadPosition(handle, obj);
                Console.WriteLine($"oyuncu nesnesi {obj:X}  " +
                                  (pos is { } q ? $"konum ({q.X:F0}, {q.Y:F0})" : "konum okunamadi"));

                foreach (var t in terrains)
                {
                    if (pos is { } pp && !Walkable(handle, t, pp.X, pp.Y)) { continue; }

                    var areaBase = t.StructAddress - 0x8D0;
                    Console.WriteLine($"alan {areaBase:X} icinde araniyor (derinlik {depth}, " +
                                      "600 bin dugum, bir kac dakika surebilir)...");
                    var found = LiveArea.FindOffsetPath(handle, areaBase, obj, depth, 600_000);
                    if (found is null) { Console.WriteLine("  bulunamadi"); continue; }

                    PlayerChain.AreaHops = found.ToArray();
                    long size = 0;
                    try { size = new FileInfo(game.MainModule!.FileName!).Length; } catch { }
                    PlayerChain.Save(size, "poshunt areapath");

                    Console.WriteLine();
                    Console.WriteLine("ALAN TABANLI YOL BULUNDU ve kaydedildi:");
                    Console.WriteLine("  alan + " + string.Join(" -> +", found.Select(h => $"0x{h:X}")));
                    Console.WriteLine("Bu yol oyun yeniden baslatilsa da gecerli kalir.");
                    return 0;
                }

                Console.WriteLine("Alan tabanli yol bulunamadi - derinligi artirmayi dene:");
                Console.WriteLine("  MapScan poshunt areapath 8");
                return 1;
            }

            // "poshunt chain <adres>": taramayi ve yurume turlarini atla, dogrudan
            // bilinen bir konum adresine sabit zincir ara. Aramayi derinlestirip
            // tekrar denemek gerektiginde bastan yurumek zorunda kalmamak icin.
            if (args.Length >= 2 && args[0].Equals("chain", StringComparison.OrdinalIgnoreCase))
            {
                var given = ulong.Parse(
                    args[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                    System.Globalization.NumberStyles.HexNumber);
                return Finish(handle, game, terrains, new List<ulong> { given });
            }

            // En genis izgara, aritmetik on filtre icin ust sinir veriyor.
            var maxWorldX = terrains.Max(t => t.GridX) * PlayerChain.WorldPerCell;
            var maxWorldY = terrains.Max(t => t.GridY) * PlayerChain.WorldPerCell;
            Console.WriteLine($"{terrains.Count} izgara, en genis dunya olcusu {maxWorldX:F0} x {maxWorldY:F0}");
            Console.WriteLine();

            var candidates = Scan(handle, terrains, maxWorldX, maxWorldY);
            if (candidates.Count == 0)
            {
                Console.Error.WriteLine("Hicbir aday yok - zemin izgaralari guncel alana ait olmayabilir.");
                return 1;
            }

            for (var round = 1; round <= Rounds && candidates.Count > 1; round++)
            {
                Console.WriteLine();
                Console.WriteLine($"--- {round}. tur: OYUNA GEC ve YURU ---");
                candidates = TrajectoryFilter(handle, terrains, candidates, round);
                Console.WriteLine($"{candidates.Count} aday kaldi");
                if (candidates.Count == 0) { break; }
            }

            Console.WriteLine();
            if (candidates.Count == 0)
            {
                Console.WriteLine("Butun adaylar elendi. Yurumedin ya da alan degisti; tekrar dene.");
                return 1;
            }

            // Turlar bitince bir kez daha suzuyoruz. Tarama anindaki deger gecerliydi
            // diye adres gecerli sayilmiyor: yigin adresleri degerini surekli degistiriyor
            // ve tur sonunda (0, 0, 1) gibi seylere donuyorlar. Ilk denemede zincir tam
            // boyle bir adrese baglandi ve sinavi "ikisi de sifir" oldugu icin gecti.
            candidates = candidates.Where(a =>
            {
                var p = ReadTriple(handle, a);
                return p is { } q && q.X > 400f && q.Y > 400f && Math.Abs(q.Z) < 100000f &&
                       !(Math.Abs(q.X - q.Y) < 0.01f && Math.Abs(q.Y - q.Z) < 0.01f) &&
                       OnWalkable(handle, terrains, q.X, q.Y);
            }).ToList();

            if (candidates.Count == 0)
            {
                Console.WriteLine("Hayatta kalanlarin hicbiri son kontrolu gecmedi. Tekrar dene.");
                return 1;
            }

            // Ayni konumu gosteren birden fazla adres var (oyun kopya tutuyor). En kalabalik
            // kume gercek oyuncudur; tek basina duran bir adres daha supheli.
            var cluster = candidates
                .Select(a => (Addr: a, Pos: ReadTriple(handle, a)!.Value))
                .GroupBy(t => ((int)(t.Pos.X / 50), (int)(t.Pos.Y / 50)))
                .OrderByDescending(g => g.Count())
                .First()
                .ToList();

            Console.WriteLine($"en kalabalik konum kumesi: {cluster.Count} adres");
            candidates = cluster.Select(t => t.Addr).Concat(candidates).Distinct().ToList();

            Console.WriteLine($"=== {candidates.Count} hayatta kalan ===");
            foreach (var a in candidates.Take(20))
            {
                var p = ReadTriple(handle, a);
                Console.WriteLine($"  {a:X}   konum " +
                                  (p is { } q ? $"({q.X:F1}, {q.Y:F1}, {q.Z:F1})  hucre " +
                                                $"({(int)(q.X / PlayerChain.WorldPerCell)}, " +
                                                $"{(int)(q.Y / PlayerChain.WorldPerCell)})" : "okunamadi"));
            }

            if (candidates.Count > 20) { Console.WriteLine($"  ... {candidates.Count - 20} tane daha"); }

            return Finish(handle, game, terrains, candidates);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    ///     Aday konum adreslerinden sabit zincire cikar, sinar ve kaydeder.
    ///     Taramadan ayri duruyor ki derinlestirip tekrar denerken bastan
    ///     yurumek gerekmesin ("poshunt chain &lt;adres&gt;").
    /// </summary>
    private static int Finish(
        IntPtr handle, Process game, List<TerrainFinder.Terrain> terrains, List<ulong> candidates)
    {
        {
            ulong moduleBase = 0;
            try { moduleBase = (ulong)game.MainModule!.BaseAddress.ToInt64(); } catch { }

            // Her adaya sabit zincir cikmiyor; birkacini deniyoruz ve SINAVI GECEN
            // ilkini aliyoruz. Sinav "ikisi ayni mi" degil - ikisi de GECERLI olmali,
            // yoksa iki tarafin sifir okumasi sinavi geciriyor (ilk denemede oldu).
            StaticChain.Result? passed = null;
            ulong passedAt = 0;

            foreach (var target in candidates.Take(6))
            {
                Console.WriteLine();
                Console.WriteLine($"=== sabit zincir araniyor: {target:X} ===");
                if (StaticChain.Find(handle, game, target, Console.WriteLine) is not { } c) { continue; }

                var objectBase = PlayerChain.Resolve(handle, moduleBase, c.Rva, c.Hops);
                var viaChain = PlayerChain.LooksLikePointer(objectBase)
                    ? ReadTriple(handle, objectBase + c.PositionOffset)
                    : null;
                var direct = ReadTriple(handle, target);

                var ok = viaChain is { } v && direct is { } d &&
                         v.X > 400f && v.Y > 400f && Math.Abs(v.Z) < 100000f &&
                         OnWalkable(handle, terrains, v.X, v.Y) &&
                         Math.Abs(v.X - d.X) < 2f && Math.Abs(v.Y - d.Y) < 2f;

                Console.WriteLine($"  zincir: modul + 0x{c.Rva:X}" +
                                  string.Concat(c.Hops.Select(h => $" -> +0x{h:X}")) +
                                  $" -> +0x{c.PositionOffset:X}");
                Console.WriteLine($"  okudu : nesne {objectBase:X}  " +
                                  (viaChain is { } vv ? $"konum ({vv.X:F1}, {vv.Y:F1})" : "konum okunamadi"));
                Console.WriteLine($"  sinav : {(ok ? "GECTI" : "gecemedi")}");

                if (!ok) { continue; }

                passed = c;
                passedAt = target;
                break;
            }

            if (passed is not { } win)
            {
                Console.WriteLine();
                Console.WriteLine("Sinavi gecen zincir yok - kaydetmiyorum.");
                Console.WriteLine($"Elle bakmak icin:  MapScan ptrscan {candidates[0]:X} 4 0x400");
                return 1;
            }

            // SON KAPI: canlilik. Zincir cozulmesi ve gecerli bir konum okumasi yeterli
            // degil - okudugu deger YASAMALI. 12.09.2026'da tam buradan yanildik: secilen
            // nesne eski alandaki konumunda donmustu, oyuncu baska alandayken bile ayni
            // degeri veriyordu. Zincir dogru gorunuyordu ama olu bir kopyayi okuyordu.
            var objBase = PlayerChain.Resolve(handle, moduleBase, win.Rva, win.Hops);
            var start = ReadTriple(handle, objBase + win.PositionOffset);

            Console.WriteLine();
            Console.WriteLine("=== canlilik sinavi: OYUNA GEC ve YURU (en fazla 60 sn) ===");

            var alive = false;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 60)
            {
                Thread.Sleep(250);
                var now = ReadTriple(handle, objBase + win.PositionOffset);
                if (start is { } s && now is { } n &&
                    (Math.Abs(s.X - n.X) > 50f || Math.Abs(s.Y - n.Y) > 50f))
                {
                    alive = true;
                    Console.WriteLine($"  kipirdadi: ({s.X:F0}, {s.Y:F0}) -> ({n.X:F0}, {n.Y:F0})  CANLI");
                    break;
                }
            }

            if (!alive)
            {
                Console.WriteLine("  konum hic kipirdamadi - bu OLU bir kopya, kaydetmiyorum.");
                Console.WriteLine("  Yurudugunden emin ol ve tekrar dene.");
                return 1;
            }

            var c2 = win;
            PlayerChain.StaticRva = c2.Rva;
            PlayerChain.Hops = c2.Hops;
            PlayerChain.PositionOffset = c2.PositionOffset;

            // ALAN TABANLI yolu da cikar: modul tabanli zincir oyun yeniden
            // baslatilinca kirildi (gecici bir global uzerinden geciyordu), oysa alan
            // yapisi her acilista yapisal imzayla bulunuyor. Oyuncunun bulundugu
            // alandan nesneye giden ofset yolunu kaydediyoruz.
            PlayerChain.AreaHops = Array.Empty<ulong>();
            var here = ReadTriple(handle, objBase + win.PositionOffset);
            if (here is { } hp)
            {
                foreach (var t in terrains)
                {
                    if (!Walkable(handle, t, hp.X, hp.Y)) { continue; }

                    var areaBase = t.StructAddress - 0x8D0;
                    var path = LiveArea.FindOffsetPath(handle, areaBase, objBase);
                    if (path is null) { continue; }

                    PlayerChain.AreaHops = path.ToArray();
                    Console.WriteLine($"  alan tabanli yol: alan + " +
                                      string.Join(" -> +", path.Select(h => $"0x{h:X}")));
                    break;
                }
            }

            if (PlayerChain.AreaHops.Length == 0)
            {
                Console.WriteLine("  UYARI: alan tabanli yol bulunamadi - zincir oyun");
                Console.WriteLine("         yeniden baslatilinca tekrar aranmasi gerekebilir.");
            }

            long exeSize = 0;
            try { exeSize = new FileInfo(game.MainModule!.FileName!).Length; } catch { }
            PlayerChain.Save(exeSize, $"poshunt, konum adresi {passedAt:X}");

            Console.WriteLine();
            Console.WriteLine("ZINCIR BULUNDU, SINANDI ve kaydedildi:");
            Console.WriteLine($"  modul + 0x{c2.Rva:X}" +
                              string.Concat(c2.Hops.Select(h => $" -> +0x{h:X}")) +
                              $" -> +0x{c2.PositionOffset:X}");
            Console.WriteLine($"  {PlayerChain.ConfigPath}");
            Console.WriteLine();
            Console.WriteLine("MapView'i yeniden baslatmak yeterli - derlemeye gerek yok.");
            return 0;
        }
    }

    /// <summary>
    ///     Birinci gecis: ucuz aritmetik filtreler, sonra yurunebilirlik sinavi.
    ///     Bellek okumasi pahali oldugu icin once float araliklari eliyor.
    /// </summary>
    private static List<ulong> Scan(
        IntPtr handle, List<TerrainFinder.Terrain> terrains, float maxWorldX, float maxWorldY)
    {
        var sw = Stopwatch.StartNew();
        var rough = new List<ulong>();
        var buf = new byte[ReadBlock];
        var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();
        ulong address = 0x10000;
        long bytes = 0;

        while (address < 0x7FFFFFFF0000UL && rough.Count < MaxCandidates)
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
                for (long off = 0; off < size && rough.Count < MaxCandidates; off += ReadBlock - 16)
                {
                    var want = (int)Math.Min(ReadBlock, size - off);
                    if (want < 12) { break; }
                    if (!Native.ReadProcessMemory(handle, (IntPtr)(regionBase + (ulong)off), buf,
                            (IntPtr)want, out var got) || (long)got < 12)
                    {
                        continue;
                    }

                    var n = (long)got;
                    bytes += n;
                    for (long i = 0; i + 12 <= n; i += 4)
                    {
                        var x = BitConverter.ToSingle(buf, (int)i);
                        if (!(x > 400f) || !(x < maxWorldX)) { continue; }

                        var y = BitConverter.ToSingle(buf, (int)i + 4);
                        if (!(y > 400f) || !(y < maxWorldY)) { continue; }

                        var z = BitConverter.ToSingle(buf, (int)i + 8);
                        if (!float.IsFinite(z) || Math.Abs(z) > 100000f) { continue; }

                        rough.Add(regionBase + (ulong)off + (ulong)i);
                    }
                }
            }

            address += (ulong)size;
        }

        sw.Stop();
        Console.WriteLine($"{bytes / 1024 / 1024} MB tarandi, aritmetik filtreyi gecen {rough.Count}, " +
                          $"{sw.Elapsed.TotalSeconds:F0} sn");

        var walkable = rough.Where(a =>
        {
            var p = ReadTriple(handle, a);
            return p is { } q && OnWalkable(handle, terrains, q.X, q.Y);
        }).ToList();

        Console.WriteLine($"yurunebilir hucreye oturan {walkable.Count}");
        return walkable;
    }

    /// <summary>
    ///     Yurumeyi bekler, sonra YORUNGE alir: bir kac saniye boyunca ornekleyip
    ///     oyuncu konumunun tutmak zorunda oldugu her sarti ayni anda uygular.
    ///
    ///     Iki uc nokta karsilastirmak yetmiyordu. (8365.3, 8365.3, 8365.3) gibi uc
    ///     bileseni esit adaylar ve duvarin icinden gecen nesneler o testi geciyordu.
    ///     Yorunge bunlari eliyor: oyuncu hep AYNI izgarada kalir, HER an yurunebilir
    ///     bir hucrede olur ve iki ornek arasinda yurume hizi kadar yol alir.
    /// </summary>
    private static List<ulong> TrajectoryFilter(
        IntPtr handle, List<TerrainFinder.Terrain> terrains, List<ulong> candidates, int round)
    {
        const int Samples = 16;
        const int IntervalMs = 220;

        Console.WriteLine("hareket bekleniyor (en fazla 60 sn)...");
        if (!WaitForMotion(handle, candidates))
        {
            Console.WriteLine("hareket gorulmedi - bu turu atliyorum");
            return candidates;
        }

        // Yorunge topluyoruz: her aday icin ornek dizisi.
        var tracks = candidates.Select(_ => new List<(float X, float Y, float Z)>()).ToList();
        for (var s = 0; s < Samples; s++)
        {
            for (var k = 0; k < candidates.Count; k++)
            {
                var p = ReadTriple(handle, candidates[k]);
                if (p is { } q) { tracks[k].Add(q); }
            }

            Thread.Sleep(IntervalMs);
        }

        var kept = new List<ulong>();
        for (var k = 0; k < candidates.Count; k++)
        {
            if (tracks[k].Count < Samples) { continue; }
            if (Plausible(handle, terrains, tracks[k])) { kept.Add(candidates[k]); }
        }

        Console.WriteLine($"{round}. tur: {kept.Count} aday yorungeyi gecti");
        return kept.Count > 0 ? kept : candidates;
    }

    /// <summary>Bir yorunge oyuncuya ait olabilir mi.</summary>
    private static bool Plausible(
        IntPtr handle, List<TerrainFinder.Terrain> terrains, List<(float X, float Y, float Z)> track)
    {
        // Uc bileseni esit degerler konum degil, dolgu.
        foreach (var p in track)
        {
            if (Math.Abs(p.X - p.Y) < 0.01f && Math.Abs(p.Y - p.Z) < 0.01f) { return false; }
            if (p.X < 400f || p.Y < 400f || Math.Abs(p.Z) > 100000f) { return false; }
        }

        // Hicbir sey degismediyse bu alan konum degil (ya da oyuncu durmus - o zaman
        // eleme yapmiyoruz, cagiran taraf hareketi bekledi).
        var total = 0.0;
        for (var i = 1; i < track.Count; i++)
        {
            var step = Math.Sqrt(Math.Pow(track[i].X - track[i - 1].X, 2) +
                                 Math.Pow(track[i].Y - track[i - 1].Y, 2));

            // Yurume hizi sinirli: bir kac yuz birimden fazla ziplama konum degil,
            // isinlanma ya da alakasiz bir sayaçtir.
            if (step > 600) { return false; }
            total += step;
        }

        if (total < 100) { return false; }

        // En onemlisi: butun yorunge TEK bir izgarada ve her an yurunebilir olmali.
        foreach (var t in terrains)
        {
            if (track.All(p => Walkable(handle, t, p.X, p.Y))) { return true; }
        }

        return false;
    }

    private static bool Walkable(IntPtr handle, TerrainFinder.Terrain t, float worldX, float worldY)
    {
        var cx = (int)(worldX / PlayerChain.WorldPerCell);
        var cy = (int)(worldY / PlayerChain.WorldPerCell);
        if (cx < 0 || cy < 0 || cx >= t.GridX || cy >= t.GridY) { return false; }

        var one = new byte[1];
        var at = t.DataStart + (ulong)((long)cy * t.BytesPerRow + (cx / 2));
        if (!Native.ReadProcessMemory(handle, (IntPtr)at, one, (IntPtr)1, out var got) || (long)got != 1)
        {
            return false;
        }

        var nibble = (cx & 1) == 0 ? one[0] & 0x0F : (one[0] >> 4) & 0x0F;
        return nibble > 0;
    }

    private static bool WaitForMotion(IntPtr handle, List<ulong> candidates)
    {
        var before = candidates.Select(a => ReadTriple(handle, a)).ToList();
        var wait = Stopwatch.StartNew();

        while (wait.Elapsed.TotalSeconds < 60)
        {
            Thread.Sleep(250);
            for (var k = 0; k < candidates.Count; k++)
            {
                var now = ReadTriple(handle, candidates[k]);
                if (before[k] is { } b && now is { } q &&
                    (Math.Abs(b.X - q.X) > 30f || Math.Abs(b.Y - q.Y) > 30f))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<ulong> MotionFilterUnused(
        IntPtr handle, List<TerrainFinder.Terrain> terrains, List<ulong> candidates, int round)
    {
        var before = candidates.Select(a => ReadTriple(handle, a)).ToList();

        Console.WriteLine("hareket bekleniyor (en fazla 60 sn)...");
        var wait = Stopwatch.StartNew();
        var started = false;
        while (wait.Elapsed.TotalSeconds < 60)
        {
            Thread.Sleep(250);
            var movers = 0;
            for (var k = 0; k < candidates.Count; k++)
            {
                var now = ReadTriple(handle, candidates[k]);
                if (before[k] is { } b && now is { } q &&
                    (Math.Abs(b.X - q.X) > 30f || Math.Abs(b.Y - q.Y) > 30f))
                {
                    movers++;
                }
            }

            // Adaylarin bir kismi kipirdadiysa yurumeye baslamis demektir.
            if (movers > 0) { started = true; break; }
        }

        if (!started)
        {
            Console.WriteLine("hareket gorulmedi - bu turu atliyorum");
            return candidates;
        }

        Thread.Sleep(1200);

        var kept = new List<ulong>();
        for (var k = 0; k < candidates.Count; k++)
        {
            var now = ReadTriple(handle, candidates[k]);
            if (before[k] is not { } b || now is not { } q) { continue; }

            var dx = Math.Abs(b.X - q.X);
            var dy = Math.Abs(b.Y - q.Y);
            if (dx < 30f && dy < 30f) { continue; }              // kipirdamadi
            if (dx > 4000f || dy > 4000f) { continue; }          // isinlandi, konum degil
            if (!OnWalkable(handle, terrains, q.X, q.Y)) { continue; }

            kept.Add(candidates[k]);
        }

        Console.WriteLine($"{round}. turda hareket eden ve zeminde kalan: {kept.Count}");
        return kept.Count > 0 ? kept : candidates;
    }

    private static (float X, float Y, float Z)? ReadTriple(IntPtr handle, ulong address)
    {
        var twelve = new byte[12];
        if (!Native.ReadProcessMemory(handle, (IntPtr)address, twelve, (IntPtr)12, out var got) ||
            (long)got != 12)
        {
            return null;
        }

        var x = BitConverter.ToSingle(twelve, 0);
        var y = BitConverter.ToSingle(twelve, 4);
        var z = BitConverter.ToSingle(twelve, 8);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) { return null; }
        return (x, y, z);
    }

    /// <summary>
    ///     Tek sayi genislikli izgaralarda satir basina bir dolgu yarim bayti var,
    ///     o yuzden hucre adresi y * satirBayt + x / 2.
    /// </summary>
    private static bool OnWalkable(
        IntPtr handle, List<TerrainFinder.Terrain> terrains, float worldX, float worldY)
    {
        var cx = (int)(worldX / PlayerChain.WorldPerCell);
        var cy = (int)(worldY / PlayerChain.WorldPerCell);
        var one = new byte[1];

        foreach (var t in terrains)
        {
            if (cx < 0 || cy < 0 || cx >= t.GridX || cy >= t.GridY) { continue; }

            var at = t.DataStart + (ulong)((long)cy * t.BytesPerRow + (cx / 2));
            if (!Native.ReadProcessMemory(handle, (IntPtr)at, one, (IntPtr)1, out var got) || (long)got != 1)
            {
                continue;
            }

            var nibble = (cx & 1) == 0 ? one[0] & 0x0F : (one[0] >> 4) & 0x0F;
            if (nibble > 0) { return true; }
        }

        return false;
    }
}
