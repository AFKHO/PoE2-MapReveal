using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Oyun acilisinda bir kez, uc saniyelik tek bir yuruyusle hem oyuncunun konum
///     adresini hem canli alani sabitler.
///
///     Neden harekete basvuruyoruz: konuma dayali butun ayraclar denendi ve
///     alanlari ayirt etmedi.
///
///       - "hucre bu izgarada ve yurunebilir": oyuncu izgaralarin ortasindayken
///         alti izgaranin hepsinde geciyor
///       - "modulden referans var mi": alti alan icin de sifir, cunku bu oyun kendi
///         verilerine duz 8 baytlik isaretci tutmuyor (dort ayri referans taramasi
///         sifir dondu)
///       - "oyuncuya ulasan tek alan": patch sonrasi bulunan nesne oyuncu varliginin
///         kendisi olmadigi icin her alandan ulasiliyor
///       - kayitli sabit yol: varliga kadar kararli, sonrasi degil - bilesen sirasi
///         her oturumda degisiyor
///
///     Hareket ise tesadufe kapali: oyuncunun konumu yuruyunce degisir, yurume hizi
///     kadar degisir, ve her an TEK bir izgarada yurunebilir kalir. O izgara canli
///     alandir. Aday sayisi once alanlarin oyuncu isaretcisinden toplanip birkac yuze
///     indirildigi icin tek kisa yuruyus yetiyor.
/// </summary>
internal static class Pin
{
    private const int Samples = 14;
    private const int IntervalMs = 220;

    internal readonly record struct Pinned(ulong PositionAddress, TerrainFinder.Terrain Area);

    internal static Pinned? Run(IntPtr handle, Action<string> log, Func<bool>? waitForWalk = null)
    {
        var terrains = TerrainFinder.Find(handle, log);
        if (terrains.Count == 0) { log("zemin yapisi bulunamadi"); return null; }

        // Kisa yol: onceki kopyalar hala canli mi? Canlilik HAREKETLE sinaniyor.
        //
        // Onceden burada "adres makul bir konum okuyor ve tek bir izgarada yurunebilir"
        // demek yetiyordu. Yetmiyormus: alan degisince oyun oyuncu nesnesini yeniden
        // olusturuyor, eski adres eski konumu tutmaya devam ediyor ve o konum yeni
        // alanda da yurunebilir cikabiliyor. Sinav gecti, harita dondu, takip etmedi.
        var known = LivePosition.Snapshot().ToList();
        if (known.Count == 0 && LiveArea.LastPositionAddress != 0) { known.Add(LiveArea.LastPositionAddress); }
        if (known.Count > 0 && ConfirmMoving(handle, terrains, known, log) is { } reused)
        {
            return reused;
        }

        // Aday adresleri butun alanlarin oyuncu isaretcisinden topla.
        var candidates = new List<ulong>();
        foreach (var t in terrains)
        {
            candidates.AddRange(PlayerFinder.Candidates(handle, t.StructAddress - 0x8D0));
        }

        candidates = candidates.Distinct().ToList();
        log($"{candidates.Count} konum adayi");
        if (candidates.Count == 0) { return null; }

        log("OYUNA GEC ve 3-4 saniye YURU...");
        if (waitForWalk is not null && !waitForWalk())
        {
            log("hareket gorulmedi");
            return null;
        }
        else if (waitForWalk is null && !WaitForMotion(handle, candidates))
        {
            log("hareket gorulmedi");
            return null;
        }

        // Yorunge topla.
        var tracks = candidates.Select(_ => new List<(float X, float Y, float Z)>()).ToList();
        for (var s = 0; s < Samples; s++)
        {
            for (var k = 0; k < candidates.Count; k++)
            {
                if (PlayerFinder.ReadAt(handle, candidates[k]) is { } p) { tracks[k].Add(p); }
            }

            Thread.Sleep(IntervalMs);
        }

        // Yorunge tamami TEK bir izgarada yurunebilir olmali. Ilk gecenle durmuyoruz:
        // gecen BUTUN kopyalari tutuyoruz ki biri olurse digerine gecilebilsin.
        var passed = new List<(ulong Address, TerrainFinder.Terrain Area, double Total)>();
        for (var k = 0; k < candidates.Count; k++)
        {
            var track = tracks[k];
            if (track.Count < Samples) { continue; }

            var total = 0.0;
            var jumped = false;
            for (var i = 1; i < track.Count; i++)
            {
                var step = Math.Sqrt(Math.Pow(track[i].X - track[i - 1].X, 2) +
                                     Math.Pow(track[i].Y - track[i - 1].Y, 2));
                if (step > 600) { jumped = true; break; }
                total += step;
            }

            // Esik yuksek olmali: uc saniyede 119 birim yol alan bir aday sinavi
            // gecmisti, ama o hizda yuruyen bir oyuncu yok - yavas suruklenen bir
            // canavar ya da baska bir nesneydi. Gercek yuruyus bunun katlari kadar.
            if (jumped || total < 600) { continue; }

            foreach (var t in terrains)
            {
                if (!track.All(p => PlayerFinder.Walkable(handle, t, p.X, p.Y))) { continue; }
                passed.Add((candidates[k], t, total));
                break;
            }
        }

        if (passed.Count == 0)
        {
            log("hicbir aday yorungeyi gecmedi - yurudugunden emin ol");
            return null;
        }

        // Kopyalar ayni alanda olmali; en kalabalik grubu aliyoruz, en uzun yol alani tercih.
        var group = passed
            .GroupBy(p => p.Area.StructAddress)
            .OrderByDescending(g => g.Count())
            .First()
            .ToList();
        var best = group.OrderByDescending(p => p.Total).First();

        LivePosition.Set(group.Select(p => p.Address), best.Address);
        LiveArea.LastPositionAddress = best.Address;
        PlayerChain.PinnedAddress = best.Address;
        PlayerChain.PinnedPid = GetProcessId(handle);
        try { PlayerChain.Save(PlayerChain.RecordedExeSize, "pin"); } catch { }

        log($"oyuncu {best.Address:X} (+{group.Count - 1} kopya), canli alan " +
            $"{best.Area.StructAddress - 0x8D0:X} ({best.Area.GridX}x{best.Area.GridY}), yol {best.Total:F0} birim");
        return new Pinned(best.Address, best.Area);
    }

    /// <summary>
    ///     Kayitli kopyalardan biri gercekten hareket ediyor mu. Once uc saniye sessizce
    ///     bekliyor - alan degisiminden sonra oyuncu genelde zaten yuruyor, yuru demeye
    ///     gerek kalmiyor. Hareket yoksa "yuru" diyor ve toplam on saniyeye kadar bekliyor.
    ///     Hareket eden kopyanin kisa yorungesi bir izgarada yurunebilir kalmali.
    ///     Hicbiri kipirdamazsa null: kopyalar olmus, bastan aranmasi gerekiyor.
    /// </summary>
    private static Pinned? ConfirmMoving(
        IntPtr handle, List<TerrainFinder.Terrain> terrains, List<ulong> known, Action<string> log)
    {
        var start = known.Select(a => PlayerFinder.ReadAt(handle, a)).ToList();
        var clock = Stopwatch.StartNew();
        var prompted = false;

        while (clock.Elapsed.TotalSeconds < 10)
        {
            Thread.Sleep(200);
            if (!prompted && clock.Elapsed.TotalSeconds > 3)
            {
                log("OYUNA GEC ve 3-4 saniye YURU...");
                prompted = true;
            }

            for (var k = 0; k < known.Count; k++)
            {
                if (start[k] is not { } before || PlayerFinder.ReadAt(handle, known[k]) is not { } now) { continue; }
                if (Math.Abs(before.X - now.X) <= 30f && Math.Abs(before.Y - now.Y) <= 30f) { continue; }

                var track = new List<(float X, float Y, float Z)> { now };
                for (var s = 0; s < 10; s++)
                {
                    Thread.Sleep(150);
                    if (PlayerFinder.ReadAt(handle, known[k]) is { } r) { track.Add(r); }
                }

                var area = terrains.FirstOrDefault(t => track.All(p => PlayerFinder.Walkable(handle, t, p.X, p.Y)));
                if (area.GridX == 0)
                {
                    start[k] = track[^1];
                    continue;
                }

                LivePosition.Set(known, known[k]);
                LiveArea.LastPositionAddress = known[k];
                log($"onceki kopya canli, canli alan {area.GridX}x{area.GridY} - yeniden aramak gerekmedi");
                return new Pinned(known[k], area);
            }
        }

        log("onceki kopyalar hareket etmiyor - bastan araniyor");
        LivePosition.Clear();
        return null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetProcessId(IntPtr process);

    private static bool WaitForMotion(IntPtr handle, List<ulong> candidates)
    {
        var before = candidates.Select(a => PlayerFinder.ReadAt(handle, a)).ToList();
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed.TotalSeconds < 90)
        {
            Thread.Sleep(200);
            for (var k = 0; k < candidates.Count; k++)
            {
                var now = PlayerFinder.ReadAt(handle, candidates[k]);
                if (before[k] is { } b && now is { } q &&
                    (Math.Abs(b.X - q.X) > 30f || Math.Abs(b.Y - q.Y) > 30f))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static int Cli()
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            var pinned = Run(handle, Console.WriteLine);
            if (pinned is not { } p) { return 1; }

            var pos = PlayerFinder.ReadAt(handle, p.PositionAddress);
            Console.WriteLine();
            Console.WriteLine($"SABITLENDI: konum {p.PositionAddress:X}  " +
                              (pos is { } q ? $"({q.X:F0}, {q.Y:F0})" : "okunamadi"));
            Console.WriteLine($"            alan {p.Area.StructAddress - 0x8D0:X}  " +
                              $"izgara {p.Area.GridX}x{p.Area.GridY}");
            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}
