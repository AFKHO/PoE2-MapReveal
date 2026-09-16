using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Hedef adrese giden pointer zincirini arar.
///
///     Mantık: ızgaranın adresini biliyoruz ama o adres her alanda değişiyor. Kalıcı olan şey,
///     onu tutan yapıya giden yol. Bu yüzden geriye doğru yürüyoruz: önce ızgarayı işaret eden
///     8 baytlık değerleri buluyoruz, sonra onları tutan yapıları işaret edenleri, ve böyle
///     devam ederek exe'nin kendi veri bölümüne (sabit adres) varmaya çalışıyoruz.
///
///     Bir seviye = belleğin tam bir taraması. Bu yüzden seviye sayısını küçük tutuyoruz.
/// </summary>
internal sealed class PointerScan
{
    private sealed record Node(ulong Address, int ParentIndex, int Offset, int Level);

    private readonly IntPtr handle;
    private readonly ulong moduleBase;
    private readonly ulong moduleEnd;
    private readonly List<(ulong Base, long Size)> regions = new();
    private readonly List<Node> nodes = new();

    private const int MaxCandidatesPerLevel = 3000;
    private const int ChunkSize = 8 * 1024 * 1024;

    private PointerScan(IntPtr handle, ulong moduleBase, ulong moduleEnd)
    {
        this.handle = handle;
        this.moduleBase = moduleBase;
        this.moduleEnd = moduleEnd;
    }

    internal static int Run(string hexTarget, int maxLevels, int maxOffset)
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        ulong modBase = 0, modEnd = 0;
        try
        {
            var m = game.MainModule;
            if (m != null)
            {
                modBase = (ulong)m.BaseAddress.ToInt64();
                modEnd = modBase + (ulong)m.ModuleMemorySize;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("ana modul bilgisi alinamadi: " + ex.Message);
        }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            var scan = new PointerScan(handle, modBase, modEnd);
            scan.CollectRegions();
            return scan.Search(
                ulong.Parse(hexTarget.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                    System.Globalization.NumberStyles.HexNumber),
                maxLevels, maxOffset);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    ///     Verilen aralığın İÇİNE işaret eden her şeyi arar. Normal zincir taraması tam
    ///     adres eşleşmesi arıyor; bu ise "tamponun herhangi bir yerini gösteren" referansları
    ///     buluyor. Ayrıca 32-bit değerleri de kontrol ediyor, çünkü oyun kendi arenasında
    ///     mutlak adres yerine arena başına göre offset saklıyor olabilir.
    /// </summary>
    internal static int RefScan(string hexBase, string hexSize, ulong arenaBase)
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var bas = ulong.Parse(hexBase.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.HexNumber);
        var size = ulong.Parse(hexSize.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.HexNumber);
        var end = bas + size;

        uint offLo = 0, offHi = 0;
        if (arenaBase != 0)
        {
            offLo = (uint)(bas - arenaBase);
            offHi = (uint)(end - arenaBase);
        }

        ulong modBase = 0, modEnd = 0;
        try
        {
            var m = game.MainModule;
            if (m != null) { modBase = (ulong)m.BaseAddress.ToInt64(); modEnd = modBase + (ulong)m.ModuleMemorySize; }
        }
        catch { }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        var scan = new PointerScan(handle, modBase, modEnd);
        try
        {
            scan.CollectRegions();
            Console.WriteLine($"aranan aralik: {bas:X} - {end:X}");
            if (arenaBase != 0) { Console.WriteLine($"arena offseti olarak: 0x{offLo:X} - 0x{offHi:X}"); }
            Console.WriteLine();

            var hits64 = new List<(ulong At, ulong Value)>();
            var hits32 = new List<(ulong At, uint Value)>();
            var buffer = new byte[ChunkSize];
            var sw = Stopwatch.StartNew();

            foreach (var (regionBase, regionSize) in scan.regions)
            {
                // Tamponun kendi içindeki değerleri saymayalım.
                if (regionBase >= bas && regionBase < end) { continue; }

                for (long off = 0; off < regionSize; off += ChunkSize)
                {
                    var want = (int)Math.Min(ChunkSize, regionSize - off);
                    if (!Native.ReadProcessMemory(handle, (IntPtr)(regionBase + (ulong)off), buffer,
                            (IntPtr)want, out var got) || (long)got < 8)
                    {
                        continue;
                    }

                    var n = (long)got;
                    for (long i = 0; i + 8 <= n; i += 4)
                    {
                        var v64 = BitConverter.ToUInt64(buffer, (int)i);
                        if (v64 >= bas && v64 < end)
                        {
                            hits64.Add((regionBase + (ulong)off + (ulong)i, v64));
                            if (hits64.Count > 5000) { break; }
                        }
                        else if (offHi != 0)
                        {
                            var v32 = BitConverter.ToUInt32(buffer, (int)i);
                            if (v32 >= offLo && v32 < offHi && hits32.Count <= 5000)
                            {
                                hits32.Add((regionBase + (ulong)off + (ulong)i, v32));
                            }
                        }
                    }
                }
            }

            sw.Stop();
            Console.WriteLine($"64-bit isaretci: {hits64.Count}   32-bit arena offseti: {hits32.Count}   ({sw.Elapsed.TotalSeconds:F0} sn)");
            Console.WriteLine();

            Console.WriteLine("=== TAMPONA ISARET EDEN 64-BIT DEGERLER (ilk 25, tampon basina en yakin) ===");
            Console.WriteLine("nerede            gosterdigi        tampon basindan");
            foreach (var (at, v) in hits64.OrderBy(h => h.Value).ThenBy(h => h.At).Take(25))
            {
                var inModule = modBase != 0 && at >= modBase && at < modEnd ? "  <-- SABIT (modul)" : "";
                Console.WriteLine($"{at:X}   {v:X}   +0x{v - bas:X}{inModule}");
            }

            if (hits32.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("=== ARENA OFFSETI GIBI DURAN 32-BIT DEGERLER (ilk 15) ===");
                foreach (var (at, v) in hits32.OrderBy(h => h.Value).Take(15))
                {
                    Console.WriteLine($"{at:X}   0x{v:X}   tampon basindan +0x{v - offLo:X}");
                }
            }

            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private void CollectRegions()
    {
        ulong address = 0x10000;
        var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();
        long total = 0;

        while (address < 0x7FFFFFFF0000UL)
        {
            if (Native.VirtualQueryEx(this.handle, (IntPtr)address, out var mbi, mbiSize) == IntPtr.Zero) { break; }
            var size = (long)mbi.RegionSize;
            if (size <= 0) { break; }

            // Sabit adresi bulabilmek icin exe'nin kendi bolgelerini de tariyoruz,
            // o yuzden MEM_IMAGE de dahil.
            var readable =
                mbi.State == Native.MemCommit &&
                (mbi.Protect & Native.PageGuard) == 0 &&
                mbi.Protect is Native.PageReadWrite or Native.PageReadOnly;

            if (readable)
            {
                this.regions.Add(((ulong)mbi.BaseAddress.ToInt64(), size));
                total += size;
            }

            address += (ulong)size;
        }

        Console.WriteLine($"taranacak bolge: {this.regions.Count}   toplam {total / 1024 / 1024} MB");
        if (this.moduleBase != 0)
        {
            Console.WriteLine($"ana modul: {this.moduleBase:X} - {this.moduleEnd:X}");
        }
    }

    private int Search(ulong target, int maxLevels, int maxOffset)
    {
        Console.WriteLine($"hedef: {target:X}   en fazla {maxLevels} seviye   ofset penceresi 0x{maxOffset:X}");
        Console.WriteLine();

        var currentTargets = new List<(ulong Value, int NodeIndex)> { (target, -1) };
        var staticHits = new List<int>();

        for (var level = 1; level <= maxLevels; level++)
        {
            var sorted = currentTargets.OrderBy(t => t.Value).ToArray();
            var values = sorted.Select(t => t.Value).ToArray();
            var prefixes = new HashSet<uint>();
            foreach (var v in values)
            {
                prefixes.Add((uint)(v >> 24));
                prefixes.Add((uint)((v - (ulong)maxOffset) >> 24));
            }

            var sw = Stopwatch.StartNew();
            var found = new List<Node>();

            var buffer = new byte[ChunkSize];
            foreach (var (regionBase, regionSize) in this.regions)
            {
                for (long off = 0; off < regionSize; off += ChunkSize)
                {
                    var want = (int)Math.Min(ChunkSize, regionSize - off);
                    if (!Native.ReadProcessMemory(this.handle, (IntPtr)(regionBase + (ulong)off), buffer,
                            (IntPtr)want, out var got) || (long)got < 8)
                    {
                        continue;
                    }

                    var n = (long)got;
                    for (long i = 0; i + 8 <= n; i += 8)
                    {
                        var v = BitConverter.ToUInt64(buffer, (int)i);
                        if (!prefixes.Contains((uint)(v >> 24))) { continue; }

                        var idx = LowerBound(values, v);
                        if (idx >= values.Length) { continue; }
                        var delta = values[idx] - v;
                        if (delta > (ulong)maxOffset) { continue; }

                        var at = regionBase + (ulong)off + (ulong)i;
                        found.Add(new Node(at, sorted[idx].NodeIndex, (int)delta, level));
                        if (found.Count > 200000) { break; }
                    }
                }
            }

            sw.Stop();

            var isStatic = new Func<Node, bool>(nd =>
                this.moduleBase != 0 && nd.Address >= this.moduleBase && nd.Address < this.moduleEnd);

            var statics = found.Where(isStatic).ToList();
            Console.WriteLine($"seviye {level}: {found.Count} isaretci bulundu   ({sw.Elapsed.TotalSeconds:F0} sn)   bunlardan SABIT: {statics.Count}");

            var baseIndex = this.nodes.Count;
            this.nodes.AddRange(found);

            for (var i = 0; i < found.Count; i++)
            {
                if (isStatic(found[i])) { staticHits.Add(baseIndex + i); }
            }

            if (staticHits.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("=== SABIT ADRESE VARAN ZINCIRLER ===");
                foreach (var hit in staticHits.Take(15)) { this.PrintChain(hit); }
                return 0;
            }

            // Sonraki seviyeye taşınacak adaylar: çok fazlaysa küçük ofsetli olanlar
            // daha muhtemel (yapı başına yakın alanlar), onları önceliklendiriyoruz.
            currentTargets = found
                .Select((nd, i) => (nd, i))
                .OrderBy(x => x.nd.Offset)
                .Take(MaxCandidatesPerLevel)
                .Select(x => (x.nd.Address, baseIndex + x.i))
                .ToList();

            if (currentTargets.Count == 0)
            {
                Console.WriteLine("bu seviyede aday kalmadi, durduruluyor.");
                return 0;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{maxLevels} seviyede sabit adrese ulasilamadi. Seviye sayisini artirmayi dene.");
        return 0;
    }

    private static int LowerBound(ulong[] sortedValues, ulong v)
    {
        int lo = 0, hi = sortedValues.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sortedValues[mid] < v) { lo = mid + 1; } else { hi = mid; }
        }

        return lo;
    }

    private void PrintChain(int index)
    {
        var parts = new List<string>();
        var i = index;
        while (i >= 0)
        {
            var nd = this.nodes[i];
            parts.Add($"{nd.Address:X} (+0x{nd.Offset:X})");
            i = nd.ParentIndex;
        }

        var head = this.nodes[index];
        var rva = head.Address - this.moduleBase;
        Console.WriteLine($"  modul+0x{rva:X}  ->  " + string.Join("  ->  ", parts));
    }
}
