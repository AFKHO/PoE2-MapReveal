using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Patch sonrasi oyuncu zincirinin yeni baslangicini bulur.
///
///     Yontem: modulun veri bolumundeki her isaretciyi sirayla deniyoruz. Her aday
///     icin bilinen zincir sekli uygulaniyor (+0x1E0 -> +0x3F0), cikan nesnenin
///     +0x88'indeki uc float konum olarak okunuyor ve ZEMINE karsi dogrulaniyor:
///     konumun denk geldigi hucre, bulunan izgaralardan birinin icinde ve yurunebilir
///     olmak zorunda.
///
///     Bu dogrulama tesadufe kapali. Rastgele uc float hem sonlu olup hem makul
///     araliga dusup hem de dar bir izgaranin yurunebilir bir hucresine denk gelmiyor.
///     Ustune bir de "yurudugunde degisiyor mu" testi var.
/// </summary>
internal static class PlayerHunt
{
    private const int Block = 1 << 16;

    private readonly record struct Candidate(ulong Rva, ulong ObjectBase, float X, float Y, float Z);

    internal static int Run(string[] args)
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        ulong moduleBase;
        long moduleSize;
        try
        {
            moduleBase = (ulong)game.MainModule!.BaseAddress.ToInt64();
            moduleSize = game.MainModule.ModuleMemorySize;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("modul alinamadi: " + ex.Message);
            return 1;
        }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            Console.WriteLine($"modul {moduleBase:X}  boyut {moduleSize / 1024 / 1024} MB");
            Console.WriteLine($"denenen zincir sekli: modul + RVA -> +0x{PlayerChain.Hops[0]:X} -> +0x{PlayerChain.Hops[1]:X}");
            Console.WriteLine();

            var terrains = TerrainFinder.Find(handle, Console.WriteLine);
            if (terrains.Count == 0)
            {
                Console.Error.WriteLine("Zemin yapisi bulunamadi - bir alanda misin?");
                return 1;
            }

            Console.WriteLine($"{terrains.Count} izgaraya karsi dogrulanacak:");
            foreach (var t in terrains)
            {
                Console.WriteLine($"    {t.GridX}x{t.GridY}  (dunya {t.GridX * PlayerChain.WorldPerCell:F0} x " +
                                  $"{t.GridY * PlayerChain.WorldPerCell:F0})");
            }

            Console.WriteLine();
            var found = Scan(handle, moduleBase, moduleSize, terrains);

            if (found.Count == 0)
            {
                Console.WriteLine("Aday bulunamadi.");
                Console.WriteLine("Zincir SEKLI de degismis olabilir (+0x1E0 / +0x3F0 artik tutmuyor).");
                Console.WriteLine("O durumda oyuncu nesnesini konumdan aramak gerekiyor: MapScan valscan");
                return 1;
            }

            // Ayni nesneye giden birden fazla RVA olabilir; nesneye gore grupluyoruz.
            var byObject = found.GroupBy(c => c.ObjectBase).OrderByDescending(g => g.Count()).ToList();
            Console.WriteLine($"{found.Count} aday RVA, {byObject.Count} farkli nesne:");
            Console.WriteLine();

            foreach (var g in byObject)
            {
                var f = g.First();
                Console.WriteLine($"  nesne {f.ObjectBase:X}   konum ({f.X:F0}, {f.Y:F0}, {f.Z:F0})  " +
                                  $"hucre ({(int)(f.X / PlayerChain.WorldPerCell)}, " +
                                  $"{(int)(f.Y / PlayerChain.WorldPerCell)})");
                foreach (var c in g.Take(8))
                {
                    Console.WriteLine($"      RVA 0x{c.Rva:X}");
                }

                if (g.Count() > 8) { Console.WriteLine($"      ... {g.Count() - 8} tane daha"); }
            }

            if (args.Contains("walk"))
            {
                Console.WriteLine();
                Verify(handle, moduleBase, byObject.Select(g => g.First()).ToList());
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Hangisinin gercek oyuncu oldugunu kesinlestirmek icin:");
                Console.WriteLine("  MapScan playerhunt walk     (yurumen istenecek)");
            }

            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private static List<Candidate> Scan(
        IntPtr handle, ulong moduleBase, long moduleSize, List<TerrainFinder.Terrain> terrains)
    {
        var sw = Stopwatch.StartNew();
        var found = new List<Candidate>();
        var buf = new byte[Block];

        // Ayni isaretci degeri modulde yuzlerce kez geciyor; her biri icin zincir
        // cozmek bosa is. Deger bazinda hatirliyoruz.
        var tried = new Dictionary<ulong, ulong>();
        long pointers = 0;
        long resolved = 0;

        for (long off = 0; off < moduleSize; off += Block)
        {
            var want = (int)Math.Min(Block, moduleSize - off);
            if (!Native.ReadProcessMemory(handle, (IntPtr)(moduleBase + (ulong)off), buf,
                    (IntPtr)want, out var got) || (long)got < 8)
            {
                continue;
            }

            var n = (long)got;
            for (long i = 0; i + 8 <= n; i += 8)
            {
                var value = BitConverter.ToUInt64(buf, (int)i);
                if (!PlayerChain.LooksLikePointer(value)) { continue; }
                pointers++;

                if (!tried.TryGetValue(value, out var objectBase))
                {
                    var cursor = value;
                    foreach (var hop in PlayerChain.Hops)
                    {
                        cursor = PlayerChain.ReadPointer(handle, cursor + hop);
                        if (cursor == 0) { break; }
                    }

                    objectBase = cursor;
                    tried[value] = objectBase;
                    if (objectBase != 0) { resolved++; }
                }

                if (objectBase == 0) { continue; }

                var pos = PlayerChain.ReadPosition(handle, objectBase);
                if (pos is not { } p) { continue; }

                // Kose civari elenmeli: hucre (0,0) butun izgaralarin icinde kaliyor ve
                // 0.0001 gibi kirinti degerler testi geciyor. Oyuncu haritanin sol ust
                // kosesinde olmaz; en az birkac dosem iceride olmali.
                if (p.X < 400f || p.Y < 400f) { continue; }
                if (!OnWalkableGround(handle, terrains, p.X, p.Y)) { continue; }

                found.Add(new Candidate((ulong)off + (ulong)i, objectBase, p.X, p.Y, p.Z));
            }
        }

        sw.Stop();
        Console.WriteLine($"{pointers} isaretci, {resolved} zincir cozuldu, {found.Count} zemine oturan, " +
                          $"{sw.Elapsed.TotalSeconds:F0} sn");
        return found;
    }

    /// <summary>
    ///     Konum, izgaralardan birinin icinde ve yurunebilir bir hucreye mi dusuyor.
    ///     Tek sayi genislikli izgaralarda satir basina bir dolgu yarim bayti oldugu
    ///     icin hucre adresi y * satirBayt + x / 2 olarak hesaplaniyor.
    /// </summary>
    private static bool OnWalkableGround(
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

    /// <summary>
    ///     Gercek oyuncuyu ayirmak icin: yuruyunce konumu degisen nesne oyuncudur.
    /// </summary>
    private static void Verify(IntPtr handle, ulong moduleBase, List<Candidate> candidates)
    {
        Console.WriteLine("OYUNA GEC ve YURU. Bekliyorum (en fazla 90 sn)...");

        var before = candidates
            .Select(c => PlayerChain.ReadPosition(handle, c.ObjectBase))
            .ToList();

        var wait = Stopwatch.StartNew();
        var moved = new bool[candidates.Count];
        var seen = 0;

        while (wait.Elapsed.TotalSeconds < 90 && seen < candidates.Count)
        {
            Thread.Sleep(200);
            seen = 0;
            for (var k = 0; k < candidates.Count; k++)
            {
                var now = PlayerChain.ReadPosition(handle, candidates[k].ObjectBase);
                if (before[k] is { } b && now is { } nw &&
                    (Math.Abs(b.X - nw.X) > 5f || Math.Abs(b.Y - nw.Y) > 5f))
                {
                    moved[k] = true;
                }

                if (moved[k]) { seen++; }
            }

            if (seen > 0 && wait.Elapsed.TotalSeconds > 6) { break; }
        }

        Console.WriteLine();
        for (var k = 0; k < candidates.Count; k++)
        {
            var c = candidates[k];
            var now = PlayerChain.ReadPosition(handle, c.ObjectBase);
            Console.WriteLine($"  nesne {c.ObjectBase:X}  {(moved[k] ? "YURUDU  <-- oyuncu bu" : "kipirdamadi")}" +
                              (now is { } p ? $"   simdi ({p.X:F0}, {p.Y:F0})" : ""));
        }

        var winner = Enumerable.Range(0, candidates.Count).Where(k => moved[k]).ToList();
        Console.WriteLine();
        if (winner.Count == 1)
        {
            var c = candidates[winner[0]];
            Console.WriteLine($"Tek aday kaldi. PlayerChain.StaticRva = 0x{c.Rva:X} yapilmali.");
        }
        else if (winner.Count == 0)
        {
            Console.WriteLine("Hicbiri kipirdamadi - yurumedin ya da hicbiri oyuncu degil.");
        }
        else
        {
            Console.WriteLine($"{winner.Count} nesne birden yurudu; hepsi ayni oyuncunun farkli kopyasi olabilir.");
        }
    }
}
