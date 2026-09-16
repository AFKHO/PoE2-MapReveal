using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Bilinen bir adrese giden sabit (modul tabanli) isaretci zincirini arar.
///
///     Oyuncunun konum adresini bulduk ama o adres her oturumda degisiyor. Kalici
///     olan sey modulun icindeki global degisken; ona giden yolu cikarmamiz gerek.
///
///     Zincirin anlami sudur:
///         A = oku(modul + rva)
///         B = oku(A + hop1)
///         nesne = oku(B + hop2)
///         konum = nesne + positionOffset
///
///     Yani geriye dogru yuruyoruz: once "nesne" degerini TUTAN adresi ariyoruz,
///     sonra o adrese isaret eden bir isaretciyi, ta ki modulun icine dusene kadar.
///     Her seviye bir bellek taramasi; ucuncu seviyede duruyoruz.
/// </summary>
internal static class StaticChain
{
    private const int ReadBlock = 1 << 20;
    private const ulong MaxOffset = 0x600;
    private const int MaxPerLevel = 1500;
    private const int MaxLevels = 6;

    internal readonly record struct Result(ulong Rva, ulong[] Hops, ulong PositionOffset);

    /// <summary>
    ///     Bir adimda bulunan bag. Target alani onemli: hangi hedefi tutturdugunu
    ///     bilmezsek, seviyeler arasinda BASKA yollara ait baglari birbirine
    ///     ekleyip gecersiz bir zincir uretiyoruz - ilk denemede tam bu oldu.
    /// </summary>
    private readonly record struct Link(ulong Holder, ulong Value, ulong Target)
    {
        internal ulong Offset => this.Target - this.Value;
    }

    /// <summary>Bir hedefe nasil varildigi: kesfedilme sirasindaki hop'lar ve konum ofseti.</summary>
    private readonly record struct Path(List<ulong> Hops, ulong PositionOffset);

    internal static Result? Find(IntPtr handle, Process game, ulong positionAddress, Action<string> log)
    {
        ulong moduleBase;
        long moduleSize;
        try
        {
            moduleBase = (ulong)game.MainModule!.BaseAddress.ToInt64();
            moduleSize = game.MainModule.ModuleMemorySize;
        }
        catch (Exception ex)
        {
            log("modul alinamadi: " + ex.Message);
            return null;
        }

        var moduleEnd = moduleBase + (ulong)moduleSize;

        // Her hedefe hangi yolla vardigimizi ayri ayri tutuyoruz. Seviye 0'da tek
        // hedef var: konum adresi. Ona varmanin "yolu" henuz bos.
        var paths = new Dictionary<ulong, Path> { [positionAddress] = new(new List<ulong>(), 0) };

        for (var level = 0; level < MaxLevels; level++)
        {
            var sw = Stopwatch.StartNew();
            var links = FindHolders(handle, paths.Keys.ToList());
            sw.Stop();
            log($"  seviye {level}: {links.Count} bag, {sw.Elapsed.TotalSeconds:F0} sn");

            if (links.Count == 0) { return null; }

            // Modulun icinde tutulan bir bag varsa zincir bitti. En kucuk ofsetliyi
            // seciyoruz: kucuk ofset daha kararli bir alan demek.
            var inModule = links
                .Where(l => l.Holder >= moduleBase && l.Holder < moduleEnd)
                .OrderBy(l => l.Offset)
                .ToList();

            foreach (var win in inModule)
            {
                if (!paths.TryGetValue(win.Target, out var parent)) { continue; }

                if (level == 0)
                {
                    // Statik dogrudan nesneyi tutuyor: hop yok, ofset konum ofsetidir.
                    return new Result(win.Holder - moduleBase, Array.Empty<ulong>(), win.Offset);
                }

                // Kesfetme sirasi asagidan yukariya; zincir kokten asagiya isliyor.
                var hops = new List<ulong>(parent.Hops) { win.Offset };
                hops.Reverse();
                return new Result(win.Holder - moduleBase, hops.ToArray(), parent.PositionOffset);
            }

            // Modulde yok: her bag icin yolu uzatip bir seviye daha yukari cikiyoruz.
            var next = new Dictionary<ulong, Path>();
            foreach (var l in links.OrderBy(l => l.Offset))
            {
                if (next.Count >= MaxPerLevel) { break; }
                if (!paths.TryGetValue(l.Target, out var parent)) { continue; }
                if (next.ContainsKey(l.Holder)) { continue; }

                next[l.Holder] = level == 0
                    ? new Path(new List<ulong>(), l.Offset)
                    : new Path(new List<ulong>(parent.Hops) { l.Offset }, parent.PositionOffset);
            }

            if (next.Count == 0) { return null; }
            paths = next;
        }

        return null;
    }

    /// <summary>
    ///     Tek gecis: degeri [hedef - MaxOffset, hedef] araliginda olan butun
    ///     isaretcileri bulur. Hedefler siralanip ikili arama yapiliyor, yoksa
    ///     her isaretci icin butun hedefleri denemek gerekirdi.
    /// </summary>
    private static List<Link> FindHolders(IntPtr handle, List<ulong> targets)
    {
        var sorted = targets.Distinct().OrderBy(t => t).ToArray();
        var links = new List<Link>();
        var buf = new byte[ReadBlock];
        var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();
        ulong address = 0x10000;

        while (address < 0x7FFFFFFF0000UL && links.Count < 200000)
        {
            if (Native.VirtualQueryEx(handle, (IntPtr)address, out var mbi, mbiSize) == IntPtr.Zero) { break; }
            var size = (long)mbi.RegionSize;
            if (size <= 0) { break; }

            // Modulun kendi bolgeleri de dahil: kok orada olacak.
            var usable = mbi.State == Native.MemCommit &&
                         (mbi.Protect & Native.PageGuard) == 0 &&
                         (mbi.Protect == Native.PageReadWrite || mbi.Protect == Native.PageReadOnly);

            if (usable)
            {
                var regionBase = (ulong)mbi.BaseAddress.ToInt64();
                for (long off = 0; off < size; off += ReadBlock)
                {
                    var want = (int)Math.Min(ReadBlock, size - off);
                    if (want < 8) { break; }
                    if (!Native.ReadProcessMemory(handle, (IntPtr)(regionBase + (ulong)off), buf,
                            (IntPtr)want, out var got) || (long)got < 8)
                    {
                        continue;
                    }

                    var n = (long)got;
                    for (long i = 0; i + 8 <= n; i += 8)
                    {
                        var value = BitConverter.ToUInt64(buf, (int)i);
                        if (!PlayerChain.LooksLikePointer(value)) { continue; }

                        // value <= hedef <= value + MaxOffset olan bir hedef var mi?
                        var lo = LowerBound(sorted, value);
                        if (lo >= sorted.Length) { continue; }
                        if (sorted[lo] > value + MaxOffset) { continue; }

                        links.Add(new Link(regionBase + (ulong)off + (ulong)i, value, sorted[lo]));
                    }
                }
            }

            address += (ulong)size;
        }

        return links;
    }

    /// <summary>Diziden, degerden kucuk olmayan ilk ogenin sirasi.</summary>
    private static int LowerBound(ulong[] sorted, ulong value)
    {
        var lo = 0;
        var hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < value) { lo = mid + 1; } else { hi = mid; }
        }

        return lo;
    }
}
