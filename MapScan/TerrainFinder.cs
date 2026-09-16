using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Zemin yapısını iç tutarlılığından bulur.
///
///     Yapı kendi kendini doğruluyor: döşeme sayısı, satır uzunluğu ve veri vektörünün
///     boyutu birbirine bağlı. Dördü aynı anda tutuyorsa bu yapı gerçektir - rastgele
///     bellekte bu birleşim çıkmaz. Bu yüzden hiçbir sabit adrese, hiçbir dış offset
///     listesine ihtiyaç yok; yapıyı şeklinden değil, kendi iç mantığından tanıyoruz.
///
///     Alan yerleşimi (yapı başına göre):
///         +0x18  long  toplam dosem X
///         +0x20  long  toplam dosem Y
///         +0xD0  ptr   veri vektoru: baslangic
///         +0xD8  ptr   veri vektoru: bitis
///         +0x130 int   satir basina bayt
///
///     Tutarlılık kuralları:
///         izgara_X      = dosemX * 23
///         izgara_Y      = dosemY * 23
///         satirBayt     = ceil(izgara_X / 2)        (2 hucre 1 bayt)
///         bitis - bas   = satirBayt * izgara_Y
///
///     Dünya -> hücre dönüşümü de buradan: 250 / 23 = 10.8696 birim bir hücre.
/// </summary>
internal static class TerrainFinder
{
    internal const int TileToGrid = 23;
    internal const float TileToWorld = 250f;
    internal const float WorldPerCell = TileToWorld / TileToGrid;

    private const int StructSpan = 0x138;
    private const int ReadBlock = 1 << 20;

    internal readonly record struct Terrain(
        ulong StructAddress,
        long TilesX,
        long TilesY,
        int GridX,
        int GridY,
        int BytesPerRow,
        ulong DataStart,
        long DataLength)
    {
        public override string ToString() =>
            $"yapi {this.StructAddress:X}   dosem {this.TilesX}x{this.TilesY}   " +
            $"izgara {this.GridX}x{this.GridY}   satirBayt {this.BytesPerRow}   " +
            $"veri {this.DataStart:X} ({this.DataLength / 1024} KB)";
    }

    /// <summary>
    ///     Tek bir adreste zemin yapisi var mi - Find'in kullandigi sinavlarin aynisi.
    ///     Bellegi taramadan, bilinen bir adresi dogrulamak icin (alan degisiminde yeni
    ///     alanin yeri bir isaretciden okunabiliyorsa).
    /// </summary>
    internal static Terrain? TryReadAt(IntPtr handle, ulong address)
    {
        var buf = new byte[StructSpan];
        if (address < 0x10000 ||
            !Native.ReadProcessMemory(handle, (IntPtr)address, buf, (IntPtr)StructSpan, out var got) ||
            (long)got != StructSpan)
        {
            return null;
        }

        return Validate(handle, buf, 0, address);
    }

    private static Terrain? Validate(IntPtr handle, byte[] buf, int i, ulong address)
    {
        var tx = BitConverter.ToInt64(buf, i + 0x18);
        if (tx is < 1 or > 4000) { return null; }

        var ty = BitConverter.ToInt64(buf, i + 0x20);
        if (ty is < 1 or > 4000) { return null; }

        var gridX = (int)(tx * TileToGrid);
        var gridY = (int)(ty * TileToGrid);
        var bytesPerRow = BitConverter.ToInt32(buf, i + 0x130);
        if (bytesPerRow != (gridX + 1) / 2) { return null; }

        var begin = BitConverter.ToUInt64(buf, i + 0xD0);
        var end = BitConverter.ToUInt64(buf, i + 0xD8);
        if (begin < 0x10000 || end <= begin || (begin & 7) != 0) { return null; }

        var length = (long)(end - begin);
        if (length != (long)bytesPerRow * gridY) { return null; }

        // Veri gercekten okunabiliyor mu
        var probe = new byte[8];
        if (!Native.ReadProcessMemory(handle, (IntPtr)begin, probe, (IntPtr)8, out var g1) || (long)g1 != 8)
        {
            return null;
        }

        return new Terrain(address, tx, ty, gridX, gridY, bytesPerRow, begin, length);
    }

    /// <summary>
    ///     Butun bellegi tarayip zemin yapilarini bulur. Bolgeler cekirdeklere bolunuyor:
    ///     7-13 GB tek is parcaciginda 4-7 saniye suruyordu. Alan degisiminde bu tarama artik
    ///     gerekmiyor (LocalPlayer.FromEntity yeni alani dogrudan okuyor); sadece ilk acilista
    ///     ve o yol basarisiz olursa yedek olarak calisiyor.
    /// </summary>
    internal static List<Terrain> Find(IntPtr handle, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();
        var regions = new List<(ulong Base, long Size)>();
        ulong address = 0x10000;

        while (address < 0x7FFFFFFF0000UL)
        {
            if (Native.VirtualQueryEx(handle, (IntPtr)address, out var mbi, mbiSize) == IntPtr.Zero) { break; }
            var size = (long)mbi.RegionSize;
            if (size <= 0) { break; }

            if (mbi.State == Native.MemCommit &&
                mbi.Type == Native.MemPrivate &&
                (mbi.Protect & Native.PageGuard) == 0 &&
                mbi.Protect == Native.PageReadWrite)
            {
                regions.Add(((ulong)mbi.BaseAddress.ToInt64(), size));
            }

            address += (ulong)size;
        }

        var found = new System.Collections.Concurrent.ConcurrentBag<Terrain>();
        long scanned = 0;

        Parallel.ForEach(
            regions,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            () => new byte[ReadBlock],
            (region, _, buf) =>
            {
                for (long off = 0; off < region.Size; off += ReadBlock - StructSpan)
                {
                    var want = (int)Math.Min(ReadBlock, region.Size - off);
                    if (want < StructSpan) { break; }
                    if (!Native.ReadProcessMemory(handle, (IntPtr)(region.Base + (ulong)off), buf,
                            (IntPtr)want, out var got) || (long)got < StructSpan)
                    {
                        continue;
                    }

                    var n = (long)got;
                    Interlocked.Add(ref scanned, n);

                    for (long i = 0; i + StructSpan <= n; i += 8)
                    {
                        // Ucuz on eleme; tam sinav Validate'te.
                        var tx = BitConverter.ToInt64(buf, (int)i + 0x18);
                        if (tx is < 1 or > 4000) { continue; }

                        if (Validate(handle, buf, (int)i, region.Base + (ulong)off + (ulong)i) is { } t) { found.Add(t); }
                    }
                }

                return buf;
            },
            _ => { });

        sw.Stop();
        log?.Invoke($"{scanned / 1024 / 1024} MB tarandi, {found.Count} zemin yapisi, {sw.Elapsed.TotalSeconds:F1} sn");
        return found.ToList();
    }

    internal static int Run()
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            var list = Find(handle, Console.WriteLine);
            Console.WriteLine();
            foreach (var t in list.OrderByDescending(t => t.DataLength))
            {
                Console.WriteLine("  " + t);
            }

            Console.WriteLine();
            Console.WriteLine($"dunya -> hucre bolen: {WorldPerCell:F4}");
            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}
