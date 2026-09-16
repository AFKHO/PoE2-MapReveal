using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Bulunan zemin yapılarından hangisinin GÜNCEL alana ait olduğunu belirler.
///
///     Oyun eski alanların örneklerini bellekte tutuyor, bu yüzden aynı anda birkaç
///     geçerli zemin yapısı bulunuyor. Ayırt edici şu: güncel alan nesnesi, oyuncuya
///     ulaşabilen tek nesnedir. Eski örneklerin oyuncuyla bağı kopmuştur.
///
///     Sabit offset varsaymıyoruz: alan nesnesinden başlayıp sınırlı derinlikte işaretçi
///     takip ediyoruz ve bilinen oyuncu nesnesinin adresini arıyoruz. Bulunursa yol da
///     yazdırılıyor - o yol, ileride doğrudan kullanılabilecek bir zincir demek.
/// </summary>
internal static class LiveArea
{
    /// <summary>
    ///     En son bulunan canli alan nesnesi. Oyuncuyu her karede yeniden
    ///     taramadan cozebilmek icin: alan tabanli yol buradan uc isaretci
    ///     okumasiyla isliyor, oysa alani bulmak 5 saniyelik bellek taramasi.
    /// </summary>
    internal static ulong LastAreaBase { get; private set; }

    /// <summary>
    ///     En son bulunan konum adresi. Arama alan basina bir kez yapiliyor,
    ///     sonra bu adres dogrudan okunuyor - 60 Hz'de arama yapilamaz.
    /// </summary>
    internal static ulong LastPositionAddress { get; set; }

    private const int NodeRead = 0x800;
    private const int MaxDepth = 4;
    private const int MaxNodes = 40000;

    /// <summary>
    ///     Güncel alanın zemin yapısını döndürür. Bulamazsa null.
    /// </summary>
    internal static TerrainFinder.Terrain? FindLive(IntPtr handle, ulong moduleBase, Action<string>? log = null)
    {
        var terrains = TerrainFinder.Find(handle, log);
        if (terrains.Count == 0) { return null; }

        // Her alanin yapisindan oyuncunun konumunu ariyoruz ve bu ayni zamanda canli
        // alani da belirliyor: konum, arandigi alanin KENDI izgarasinda yurunebilir
        // olmak zorunda. Tutan alan hem canli alandir hem oyuncuyu vermistir.
        // Iki soruyu tek sinavla cozuyor ve hicbir offset varsaymiyor.
        foreach (var t in terrains.OrderByDescending(t => t.DataLength))
        {
            var areaBase = t.StructAddress - 0x8D0;
            var at = PlayerFinder.Find(handle, areaBase, t);
            if (at is not { } address) { continue; }

            LastAreaBase = areaBase;
            LastPositionAddress = address;
            var p = PlayerFinder.ReadAt(handle, address);
            log?.Invoke($"guncel alan {areaBase:X}, konum adresi {address:X}" +
                        (p is { } q ? $"  ({q.X:F0}, {q.Y:F0})" : ""));
            return t;
        }

        var player = PlayerChain.Resolve(handle, moduleBase);
        if (!PlayerChain.LooksLikePointer(player))
        {
            log?.Invoke("oyuncu bulunamadi - MapScan poshunt");
            return null;
        }

        // Birincil ayrac: oyuncunun hucresi. Konumu artik guvenilir okudugumuz icin
        // guncel alan, o hucreyi ICEREN ve orada YURUNEBILIR olan izgaradir. Bu ayrac
        // patch'ten etkilenmiyor, cunku hicbir offset varsaymiyor.
        //
        // Eskiden "oyuncuya ulasan tek alan" diye BFS yapiyorduk. O ayrac artik tek
        // basina yetmiyor: 11.09.2026 patch'inden sonra bulunan nesne oyuncu varliginin
        // kendisi degil, konumu tutan baska bir nesne - ona her alandan ulasiliyor.
        var fit = terrains.Where(t => PlayerInside(handle, t, player)).ToList();

        if (fit.Count == 1)
        {
            LastAreaBase = fit[0].StructAddress - 0x8D0;
            log?.Invoke($"guncel alan {LastAreaBase:X} (oyuncu hucresi bu izgarada)");
            return fit[0];
        }

        // Birden fazla izgara oyuncunun hucresini iceriyorsa BFS ile ayiriyoruz.
        foreach (var t in (fit.Count > 0 ? fit : terrains).OrderByDescending(t => t.DataLength))
        {
            var areaBase = t.StructAddress - 0x8D0;
            var path = FindPath(handle, areaBase, player, out _);
            if (path != null)
            {
                LastAreaBase = areaBase;
                log?.Invoke($"guncel alan {areaBase:X}, yol: alan + {path}");
                return t;
            }
        }

        log?.Invoke($"{terrains.Count} zemin yapisi var ama hicbiri oyuncunun hucresini tutmuyor");
        return null;
    }

    internal static int Run()
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        ulong moduleBase;
        try { moduleBase = (ulong)game.MainModule!.BaseAddress.ToInt64(); }
        catch (Exception ex) { Console.Error.WriteLine("modul alinamadi: " + ex.Message); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            var terrains = TerrainFinder.Find(handle, Console.WriteLine);
            Console.WriteLine();

            var refs = ModuleReferences(handle, game, terrains.Select(t => t.StructAddress - 0x8D0));

            var found = false;
            foreach (var t in terrains.OrderByDescending(t => t.DataLength))
            {
                var areaBase = t.StructAddress - 0x8D0;
                var head = $"alan {areaBase:X}  izgara {t.GridX}x{t.GridY}  " +
                           $"modulden {refs.GetValueOrDefault(areaBase)} referans";
                var at = PlayerFinder.Find(handle, areaBase, t);

                if (at is not { } address)
                {
                    Console.WriteLine($"  eski   : {head}");
                    continue;
                }

                found = true;
                var p = PlayerFinder.ReadAt(handle, address);
                Console.WriteLine($"  *** GUNCEL: {head}");
                Console.WriteLine($"      konum adresi {address:X}  " +
                                  (p is { } q
                                      ? $"({q.X:F0}, {q.Y:F0}) -> hucre " +
                                        $"({(int)(q.X / PlayerChain.WorldPerCell)}, " +
                                        $"{(int)(q.Y / PlayerChain.WorldPerCell)})"
                                      : "okunamadi"));
            }

            if (!found)
            {
                Console.WriteLine();
                Console.WriteLine("Hicbir alanda oyuncu bulunamadi. Kayitli yolun izi:");
                var biggest = terrains.OrderByDescending(t => t.DataLength).First();
                PlayerChain.TraceFromArea(handle, biggest.StructAddress - 0x8D0, Console.WriteLine);
                return 1;
            }

            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private static ulong Resolve(IntPtr handle, ulong start, ulong[] hops)
    {
        var eight = new byte[8];
        var cursor = start;
        foreach (var hop in hops)
        {
            if (!Native.ReadProcessMemory(handle, (IntPtr)cursor, eight, (IntPtr)8, out var got) || (long)got != 8)
            {
                return 0;
            }

            var value = BitConverter.ToUInt64(eight, 0);
            if (value == 0) { return 0; }
            cursor = value + hop;
        }

        if (!Native.ReadProcessMemory(handle, (IntPtr)cursor, eight, (IntPtr)8, out var g) || (long)g != 8)
        {
            return 0;
        }

        return BitConverter.ToUInt64(eight, 0);
    }

    /// <summary>
    ///     Alan nesnesinden başlayarak hedefe giden offset yolunu arar (genişlik öncelikli).
    /// </summary>
    /// <summary>
    ///     Verilen alan adreslerinin her birine MODULUN icinden kac isaretci
    ///     baktigini sayar.
    ///
    ///     Ayrac olarak bunu kullaniyoruz cunku konuma dayali sinavlar ayirt
    ///     etmedi: oyuncu izgaralarin ortasinda oldugunda hucresi alti izgaranin
    ///     hepsinde yurunebilir cikiyor. Oyunun kendisi ise canli alani bir global
    ///     degiskende tutmak zorunda; eski alan ornekleri yigindaki bir onbellekte
    ///     duruyor ve modulden gosterilmiyorlar.
    /// </summary>
    internal static Dictionary<ulong, int> ModuleReferences(
        IntPtr handle, System.Diagnostics.Process game, IEnumerable<ulong> targets)
    {
        var counts = targets.Distinct().ToDictionary(t => t, _ => 0);
        var wanted = new HashSet<ulong>(counts.Keys);

        ulong moduleBase;
        long moduleSize;
        try
        {
            moduleBase = (ulong)game.MainModule!.BaseAddress.ToInt64();
            moduleSize = game.MainModule.ModuleMemorySize;
        }
        catch
        {
            return counts;
        }

        const int Block = 1 << 20;
        var buf = new byte[Block];

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
                if (wanted.Contains(value)) { counts[value]++; }
            }
        }

        return counts;
    }

    /// <summary>
    ///     Oyuncunun konumu bu izgaranin icinde ve yurunebilir bir hucrede mi.
    ///     Tek sayi genislikli izgaralarda satir basina bir dolgu yarim bayti var,
    ///     o yuzden hucre adresi y * satirBayt + x / 2.
    /// </summary>
    internal static bool PlayerInside(IntPtr handle, TerrainFinder.Terrain t, ulong playerObject)
    {
        var pos = PlayerChain.ReadPosition(handle, playerObject);
        if (pos is not { } p) { return false; }

        var cx = (int)(p.X / PlayerChain.WorldPerCell);
        var cy = (int)(p.Y / PlayerChain.WorldPerCell);
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

    /// <summary>Oyuncunun hucresindeki zemin degeri; izgara disindaysa null.</summary>
    private static int? CellValue(IntPtr handle, TerrainFinder.Terrain t, ulong playerObject)
    {
        var pos = PlayerChain.ReadPosition(handle, playerObject);
        if (pos is not { } p) { return null; }

        var cx = (int)(p.X / PlayerChain.WorldPerCell);
        var cy = (int)(p.Y / PlayerChain.WorldPerCell);
        if (cx < 0 || cy < 0 || cx >= t.GridX || cy >= t.GridY) { return null; }

        var one = new byte[1];
        var at = t.DataStart + (ulong)((long)cy * t.BytesPerRow + (cx / 2));
        if (!Native.ReadProcessMemory(handle, (IntPtr)at, one, (IntPtr)1, out var got) || (long)got != 1)
        {
            return null;
        }

        return (cx & 1) == 0 ? one[0] & 0x0F : (one[0] >> 4) & 0x0F;
    }

    /// <summary>
    ///     Alan nesnesinden hedefe giden ofset yolunu SAYI listesi olarak dondurur.
    ///     Kaydedilebilir olmasi icin: modul tabanli zincir oyun yeniden baslatilinca
    ///     kirildi, alan tabanli yol ise her acilista yapisal olarak bulunabiliyor.
    /// </summary>
    internal static List<ulong>? FindOffsetPath(
        IntPtr handle, ulong areaBase, ulong target, int maxDepth = 4, int maxNodes = MaxNodes)
    {
        var seen = new HashSet<ulong> { areaBase };
        var queue = new Queue<(ulong Address, List<ulong> Path)>();
        queue.Enqueue((areaBase, new List<ulong>()));
        var visited = 0;
        var buf = new byte[NodeRead];

        while (queue.Count > 0 && visited < maxNodes)
        {
            var (address, path) = queue.Dequeue();
            visited++;

            if (!Native.ReadProcessMemory(handle, (IntPtr)address, buf, (IntPtr)NodeRead, out var got) ||
                (long)got < 8)
            {
                continue;
            }

            var n = (long)got;
            for (long i = 0; i + 8 <= n; i += 8)
            {
                var value = BitConverter.ToUInt64(buf, (int)i);
                if (value == target) { return new List<ulong>(path) { (ulong)i }; }

                if (path.Count + 1 >= maxDepth) { continue; }
                if (!PlayerChain.LooksLikePointer(value)) { continue; }
                if (!seen.Add(value)) { continue; }

                queue.Enqueue((value, new List<ulong>(path) { (ulong)i }));
            }
        }

        return null;
    }

    private static string? FindPath(IntPtr handle, ulong areaBase, ulong target, out int visited)
    {
        var seen = new HashSet<ulong> { areaBase };
        var queue = new Queue<(ulong Address, string Path, int Depth)>();
        queue.Enqueue((areaBase, "", 0));
        visited = 0;

        var buf = new byte[NodeRead];

        while (queue.Count > 0 && visited < MaxNodes)
        {
            var (address, path, depth) = queue.Dequeue();
            visited++;

            if (!Native.ReadProcessMemory(handle, (IntPtr)address, buf, (IntPtr)NodeRead, out var got) ||
                (long)got < 8)
            {
                continue;
            }

            var n = (long)got;
            for (long i = 0; i + 8 <= n; i += 8)
            {
                var value = BitConverter.ToUInt64(buf, (int)i);
                var step = path.Length == 0 ? $"0x{i:X}" : $"{path} -> 0x{i:X}";

                if (value == target) { return step; }

                if (depth + 1 >= MaxDepth) { continue; }
                if (value < 0x10000 || value > 0x7FFFFFFFFFFF || (value & 7) != 0) { continue; }
                if (!seen.Add(value)) { continue; }

                queue.Enqueue((value, step, depth + 1));
            }
        }

        return null;
    }
}
