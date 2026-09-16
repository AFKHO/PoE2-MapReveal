using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Harita UI ogesini bellekte bulur ve merkezini oyunun kendi hesabiyla cikarir.
///
///     Her UI ogesinin +0x08 alani KENDI adresini tutuyor. Bu, bellekte aranabilecek
///     en temiz imzalardan biri: rastgele veride bir degerin bulundugu adrese esit
///     olmasi neredeyse hic olmuyor. Once boylece butun UI ogeleri bulunuyor, sonra
///     harita ogesi kendi alanlarindan ayirt ediliyor.
///
///     Onemli olan kisim merkez. Bir ogenin ekrandaki yeri tek bir alanda YAZMIYOR;
///     oyun onu ebeveyn zincirini yukari yuruyerek hesapliyor. Her adimda cocugun
///     goreli konumu ekleniyor, bayragi uygunsa ebeveynin konum degistiricisi
///     ekleniyor, ve iki ogenin olcek sinifi farkliysa arada bir oran uygulaniyor.
///     En sonunda sonuc pencere olcegiyle carpilip kenar bandi (cull) ekleniyor.
///
///     Buyuk haritada bu sonuc dogrudan haritanin MERKEZI oluyor - sol ust kosesi
///     degil. Onceden merkezi "boyut / 2 + kaydirma" diye tahmin ediyorduk; aradaki
///     fark elle -95 girmek zorunda kalmamizin sebebiydi.
/// </summary>
internal static class UiFinder
{
    private const int Span = 0x3A0;
    private const int ReadBlock = 1 << 20;

    /// <summary>Oyunun UI olcegini hesapladigi taban cozunurluk (UISettings.xml).</summary>
    internal const double BaseResX = 2560.0;
    internal const double BaseResY = 1600.0;

    private const int OffParent = 0xB8;
    private const int OffRelative = 0x100;
    private const int OffModifier = 0x108;
    private const int OffLocalScale = 0x118;
    private const int OffFlags = 0x168;
    private const int OffScaleIndex = 0x172;
    private const int OffUnscaledSize = 0x270;
    private const int OffShift = 0x350;
    private const int OffDefaultShift = 0x358;
    private const int OffZoom = 0x390;

    private const int FlagShouldModifyPos = 0x0A;
    private const int FlagIsVisible = 0x0B;

    internal readonly record struct UiElement(
        ulong Address,
        ulong ParentPtr,
        float SizeX, float SizeY,
        float RelX, float RelY,
        float ModX, float ModY,
        float ShiftX, float ShiftY,
        float DefShiftX, float DefShiftY,
        float Zoom,
        float LocalScale,
        byte ScaleIndex,
        uint Flags)
    {
        internal bool Visible => (this.Flags & (1u << FlagIsVisible)) != 0;

        internal bool ShouldModifyPos => (this.Flags & (1u << FlagShouldModifyPos)) != 0;

        public override string ToString() =>
            $"{this.Address:X}  boyut {this.SizeX,7:F0}x{this.SizeY,-7:F0} " +
            $"goreli ({this.RelX,7:F1},{this.RelY,7:F1})  degistirici ({this.ModX,6:F1},{this.ModY,6:F1})  " +
            $"zoom {this.Zoom,6:F3}  kaydirma ({this.ShiftX,7:F1},{this.ShiftY,7:F1})  " +
            $"varsayilan ({this.DefShiftX,6:F1},{this.DefShiftY,6:F1})  " +
            $"olcek[{this.ScaleIndex}]x{this.LocalScale:F3}  ebeveyn {this.ParentPtr:X}  " +
            (this.Visible ? "GORUNUR" : "gizli");
    }

    private static UiElement Parse(byte[] buf, int i, ulong address) => new(
        address,
        BitConverter.ToUInt64(buf, i + OffParent),
        BitConverter.ToSingle(buf, i + OffUnscaledSize),
        BitConverter.ToSingle(buf, i + OffUnscaledSize + 4),
        BitConverter.ToSingle(buf, i + OffRelative),
        BitConverter.ToSingle(buf, i + OffRelative + 4),
        BitConverter.ToSingle(buf, i + OffModifier),
        BitConverter.ToSingle(buf, i + OffModifier + 4),
        BitConverter.ToSingle(buf, i + OffShift),
        BitConverter.ToSingle(buf, i + OffShift + 4),
        BitConverter.ToSingle(buf, i + OffDefaultShift),
        BitConverter.ToSingle(buf, i + OffDefaultShift + 4),
        BitConverter.ToSingle(buf, i + OffZoom),
        BitConverter.ToSingle(buf, i + OffLocalScale),
        buf[i + OffScaleIndex],
        BitConverter.ToUInt32(buf, i + OffFlags));

    // ------------------------------------------------------------------ olcek

    /// <summary>
    ///     Oyunun pencere olcegi. Bellekten OKUNMUYOR - oyunun kendisi de bunu
    ///     hesapliyor, biz de ayni hesabi yapiyoruz: genislik tabani 2560, yukseklik
    ///     tabani 1600. Boylece patch bu degeri tasisa bile bizim tarafta kirilmiyor.
    /// </summary>
    internal static (float W, float H) ScaleValue(int index, float multiplier, int clientW, int clientH, int cull)
    {
        var v1 = (float)((clientW - cull - cull) / BaseResX);
        var v2 = (float)(clientH / BaseResY);
        return index switch
        {
            1 => (multiplier * v1, multiplier * v1),
            2 => (multiplier * v2, multiplier * v2),
            3 => (multiplier * v1, multiplier * v2),
            _ => (multiplier, multiplier),
        };
    }

    /// <summary>Tek bir ogeyi adresinden okur. Kendi adresini tutmuyorsa null.</summary>
    internal static UiElement? Reread(IntPtr handle, ulong address)
    {
        if (address == 0) { return null; }

        var buf = new byte[Span];
        if (!Native.ReadProcessMemory(handle, (IntPtr)address, buf, (IntPtr)Span, out var got) ||
            (long)got != Span)
        {
            return null;
        }

        if (BitConverter.ToUInt64(buf, 8) != address) { return null; }
        return Parse(buf, 0, address);
    }

    /// <summary>Ebeveyn zinciri: [oge, ebeveyn, ..., kok]. Dongu olursa 64 halkada kesiliyor.</summary>
    private static List<UiElement> Chain(IntPtr handle, ulong address)
    {
        var chain = new List<UiElement>();
        var seen = new HashSet<ulong>();
        var cursor = address;

        while (cursor != 0 && chain.Count < 64 && seen.Add(cursor))
        {
            var el = Reread(handle, cursor);
            if (el is not { } e) { break; }
            chain.Add(e);
            cursor = e.ParentPtr;
        }

        return chain;
    }

    /// <summary>
    ///     Oyunun kendi konum hesabi, olcek uygulanmadan onceki hali.
    ///     Kokten asagi dogru yuruyoruz; oyun ayni seyi yukari dogru ozyinelemeyle yapiyor.
    /// </summary>
    private static (float X, float Y) UnScaledPosition(List<UiElement> chain, int clientW, int clientH, int cull)
    {
        if (chain.Count == 0) { return (0f, 0f); }

        var root = chain[^1];
        var x = root.RelX;
        var y = root.RelY;

        for (var k = chain.Count - 2; k >= 0; k--)
        {
            var parent = chain[k + 1];
            var child = chain[k];

            if (child.ShouldModifyPos)
            {
                x += parent.ModX;
                y += parent.ModY;
            }

            if (parent.ScaleIndex == child.ScaleIndex &&
                Math.Abs(parent.LocalScale - child.LocalScale) < 1e-6f)
            {
                x += child.RelX;
                y += child.RelY;
                continue;
            }

            var (pw, ph) = ScaleValue(parent.ScaleIndex, parent.LocalScale, clientW, clientH, cull);
            var (mw, mh) = ScaleValue(child.ScaleIndex, child.LocalScale, clientW, clientH, cull);
            if (mw == 0 || mh == 0) { return (x, y); }

            x = (x * pw / mw) + child.RelX;
            y = (y * ph / mh) + child.RelY;
        }

        return (x, y);
    }

    /// <summary>
    ///     Ogenin ekrandaki SOL UST kosesi. Oyunun kendi hesabinin birebir kopyasi.
    /// </summary>
    internal static (float X, float Y)? ElementPosition(
        IntPtr handle, ulong address, int clientW, int clientH, int cull = 0)
    {
        if (clientW <= 0 || clientH <= 0) { return null; }

        var chain = Chain(handle, address);
        if (chain.Count == 0) { return null; }

        var (ux, uy) = UnScaledPosition(chain, clientW, clientH, cull);
        var (w, h) = ScaleValue(chain[0].ScaleIndex, chain[0].LocalScale, clientW, clientH, cull);
        return ((ux * w) + cull, uy * h);
    }

    /// <summary>
    ///     Haritanin cizim MERKEZI - oyuncunun ustune oturmasi gereken nokta.
    ///
    ///     Gordin'in kodunda buyuk harita ogesinin konumu dogrudan merkez sayiliyor;
    ///     bizim bulduğumuz dugumde oyle degil, konum sol ust kose. Olculen sayilar
    ///     bunu net gosteriyor: konum (349.6, 185.6), ekrandaki boyut 1220x646,
    ///     konum + boyut/2 = (959.8, 508.8) - yani 1920x1017 pencerenin tam ortasi.
    ///
    ///     Ustune oyunun kaydirmasi ve durgun haldeki varsayilan kaydirmasi ekleniyor.
    /// </summary>
    internal static (float X, float Y)? MapCenter(
        IntPtr handle, ulong address, int clientW, int clientH, int cull = 0)
    {
        if (Reread(handle, address) is not { } e) { return null; }
        if (ElementPosition(handle, address, clientW, clientH, cull) is not { } p) { return null; }

        var (w, h) = ScaleValue(e.ScaleIndex, e.LocalScale, clientW, clientH, cull);
        // Shift (+0x350) EKLENMIYOR, sadece DefaultShift. Radar ikisini de ekliyor ama
        // bizim dugumumuzde Shift oyunun kendi haritasina uygulanmiyor: 16.09.2026'da
        // Shift ilk kez sifirdan farkli okundu, (-13, 30), ve overlay oyunun haritasina
        // gore tam o kadar sola-asagi kaymis gorundu. Onceki butun dogru hizalamalarda
        // Shift zaten (0, 0) idi, o yuzden fark hic ortaya cikmamisti.
        return (p.X + (e.SizeX * w / 2f) + e.DefShiftX,
                p.Y + (e.SizeY * h / 2f) + e.DefShiftY);
    }

    /// <summary>Zincirin kac halkali oldugu - tanilama icin.</summary>
    internal static int ChainLength(IntPtr handle, ulong address) => Chain(handle, address).Count;

    /// <summary>
    ///     Izdusumun kosegen uzunlugu. Oyunun kendi olceginden geliyor ve SADECE
    ///     pencere YUKSEKLIGINE bagli - genislik degisince harita olcegi degismiyor.
    ///     Ogenin kendi UnscaledSize alanindan hesaplamak yanlisti.
    /// </summary>
    internal static double DiagonalLength(int clientH) =>
        Math.Sqrt((BaseResX * BaseResX) + (BaseResY * BaseResY)) * clientH / BaseResY;

    // ------------------------------------------------------------------ arama

    /// <summary>
    ///     Butun kendi kendini gosteren yapilar, hicbir suzgec olmadan.
    ///     Kaydirmayi arayan olcum icin: aradigimiz alan "harita olabilir"
    ///     suzgecinden gecmeyen bir ogede olabilir.
    /// </summary>
    internal static List<UiElement> FindAll(IntPtr handle, Action<string>? log = null) =>
        Find(handle, log, filter: false);

    internal static List<UiElement> Find(IntPtr handle, Action<string>? log = null, bool filter = true)
    {
        var found = new List<UiElement>();
        var sw = Stopwatch.StartNew();
        var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();
        var buf = new byte[ReadBlock];
        ulong address = 0x10000;
        long selfPointers = 0;

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
                for (long off = 0; off < size; off += ReadBlock - Span)
                {
                    var want = (int)Math.Min(ReadBlock, size - off);
                    if (want < Span) { break; }
                    if (!Native.ReadProcessMemory(handle, (IntPtr)(regionBase + (ulong)off), buf,
                            (IntPtr)want, out var got) || (long)got < Span)
                    {
                        continue;
                    }

                    var n = (long)got;
                    for (long i = 0; i + Span <= n; i += 8)
                    {
                        var here = regionBase + (ulong)off + (ulong)i;
                        if (BitConverter.ToUInt64(buf, (int)i + 8) != here) { continue; }

                        selfPointers++;

                        var sizeX = BitConverter.ToSingle(buf, (int)i + OffUnscaledSize);
                        var sizeY = BitConverter.ToSingle(buf, (int)i + OffUnscaledSize + 4);
                        var zoom = BitConverter.ToSingle(buf, (int)i + OffZoom);

                        if (filter)
                        {
                            if (!float.IsFinite(sizeX) || !float.IsFinite(sizeY) || !float.IsFinite(zoom)) { continue; }
                            if (sizeX < 50 || sizeX > 20000 || sizeY < 50 || sizeY > 20000) { continue; }
                            if (zoom < 0.05f || zoom > 20f) { continue; }
                        }

                        found.Add(Parse(buf, (int)i, here));
                    }
                }
            }

            address += (ulong)size;
        }

        sw.Stop();
        log?.Invoke($"kendi kendini gosteren {selfPointers} yapi, harita olabilecek {found.Count} tanesi, " +
                    $"{sw.Elapsed.TotalSeconds:F0} sn");
        return found;
    }

    /// <summary>
    ///     Buyuk harita ogesini secer: tam ekran boyutunda, gorunur, ve varsayilan
    ///     kaydirmasi (0, -20) civarinda olan oge. Bu ucu birden tutan baska bir sey yok.
    /// </summary>
    internal static UiElement? FindLargeMap(IntPtr handle, Action<string>? log = null, bool requireVisible = true)
    {
        // requireVisible=false: ayarsiz exe ogeyi Tab kapaliyken de bulabilsin.
        // Yoksa Tab kapali oldugu surece her denemede bes saniyelik bellek taramasi
        // yapip bos donerdi. Bulunan oge adresi ayni alanda sabit kaliyor; Tab'in
        // acik olup olmadigi sonra ogenin kendi gorunurluk bitinden okunuyor.
        var all = Find(handle, log);
        return all
            .Where(e => (!requireVisible || e.Visible) && e.SizeX > 800 && e.SizeY > 500)
            .Where(e => Math.Abs(e.DefShiftX) < 5f && Math.Abs(e.DefShiftY + 20f) < 5f)
            .Where(e => e.Zoom is > 0.05f and < 20f)
            .OrderByDescending(e => e.Visible)
            .ThenByDescending(e => e.SizeX * e.SizeY)
            .Select(e => (UiElement?)e)
            .FirstOrDefault();
    }

    internal static int Run()
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            GetClientRect(game.MainWindowHandle, out var rect);
            var clientW = rect.Right - rect.Left;
            var clientH = rect.Bottom - rect.Top;
            Console.WriteLine($"oyun istemci alani: {clientW} x {clientH}");
            Console.WriteLine($"kosegen: {DiagonalLength(clientH):F2}");
            Console.WriteLine();

            var list = Find(handle, Console.WriteLine);
            Console.WriteLine();

            foreach (var e in list
                         .OrderByDescending(e => Math.Abs(e.DefShiftY + 20f) < 5f && Math.Abs(e.DefShiftX) < 5f)
                         .ThenByDescending(e => e.Visible)
                         .ThenByDescending(e => e.SizeX * e.SizeY)
                         .Take(25))
            {
                Console.WriteLine("  " + e);
                var c = ElementPosition(handle, e.Address, clientW, clientH);
                var m = MapCenter(handle, e.Address, clientW, clientH);
                if (c is { } pos && m is { } center)
                {
                    Console.WriteLine($"      konum ({pos.X,8:F2},{pos.Y,8:F2})   " +
                                      $"merkez ({center.X,8:F2},{center.Y,8:F2})   " +
                                      $"zincir {ChainLength(handle, e.Address)} halka");
                }
            }

            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    ///     Tek bir ogenin ebeveyn zincirini dokerek merkez hesabini adim adim gosterir.
    ///     Hesap tutmadiginda hangi halkada koptugunu gormek icin.
    /// </summary>
    internal static int RunChain(string hexAddress)
    {
        var address = ulong.Parse(
            hexAddress.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.HexNumber);

        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            GetClientRect(game.MainWindowHandle, out var rect);
            var clientW = rect.Right - rect.Left;
            var clientH = rect.Bottom - rect.Top;
            var v1 = (clientW) / BaseResX;
            var v2 = clientH / BaseResY;
            Console.WriteLine($"istemci {clientW}x{clientH}   v1={v1:F5}  v2={v2:F5}");
            Console.WriteLine();

            var chain = Chain(handle, address);
            Console.WriteLine($"zincir {chain.Count} halka (0 = ogenin kendisi, son = kok)");
            for (var k = 0; k < chain.Count; k++)
            {
                var c = chain[k];
                Console.WriteLine($"  [{k}] {c.Address:X}  goreli ({c.RelX,8:F1},{c.RelY,8:F1})  " +
                                  $"degistirici ({c.ModX,7:F1},{c.ModY,7:F1})  " +
                                  $"olcek[{c.ScaleIndex}]x{c.LocalScale:F3}  " +
                                  $"bayrak {c.Flags:X8}  konumDegistir={(c.ShouldModifyPos ? "E" : "H")}  " +
                                  $"gorunur={(c.Visible ? "E" : "H")}  " +
                                  $"boyut {c.SizeX:F0}x{c.SizeY:F0}  ebeveyn {c.ParentPtr:X}");
            }

            // Son halkanin ebeveyni gercekten yok mu, yoksa okuyamadik mi?
            if (chain.Count > 0)
            {
                var last = chain[^1];
                if (last.ParentPtr != 0)
                {
                    var raw = new byte[8];
                    var ok = Native.ReadProcessMemory(handle, (IntPtr)(last.ParentPtr + 8), raw, (IntPtr)8, out _);
                    var self = ok ? BitConverter.ToUInt64(raw, 0) : 0;
                    Console.WriteLine();
                    Console.WriteLine($"  DIKKAT: kok sanilan halkanin ebeveyni var: {last.ParentPtr:X}");
                    Console.WriteLine($"          orada +8 = {self:X}  (esit olmasi gerekirdi) -> zincir ERKEN KOPTU");
                }
            }

            Console.WriteLine();
            var (ux, uy) = UnScaledPosition(chain, clientW, clientH, 0);
            Console.WriteLine($"olceksiz konum ({ux:F2}, {uy:F2})");
            var c0 = ElementPosition(handle, address, clientW, clientH);
            if (c0 is { } cc) { Console.WriteLine($"ekrandaki konum (sol ust) ({cc.X:F2}, {cc.Y:F2})"); }
            var m0 = MapCenter(handle, address, clientW, clientH);
            if (m0 is { } mm) { Console.WriteLine($"harita merkezi ({mm.X:F2}, {mm.Y:F2})"); }
            Console.WriteLine($"pencerenin ortasi ({clientW / 2f:F1}, {clientH / 2f:F1})");
            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    ///     Buyuk harita ogesinin alanlarini imlecle birlikte izler.
    ///
    ///     Cevaplamak istedigimiz soru: fare oynayinca overlay kayiyor ama yururken
    ///     kaymiyor. Eger oyunun haritasi imlece dogru kayiyorsa bu Shift alaninda
    ///     gorunmeli. Goruluyorsa hesabimizda Shift'i yanlis olcekte ekliyoruz demektir;
    ///     gorulmuyorsa kayma baska bir yerden geliyor.
    /// </summary>
    internal static int Watch()
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            GetClientRect(game.MainWindowHandle, out var rect);
            var clientW = rect.Right - rect.Left;
            var clientH = rect.Bottom - rect.Top;

            var found = FindLargeMap(handle, Console.WriteLine);
            if (found is not { } e0)
            {
                Console.Error.WriteLine("Buyuk harita ogesi bulunamadi - oyunda Tab acik mi?");
                return 1;
            }

            Console.WriteLine($"oge {e0.Address:X}, istemci {clientW}x{clientH}");
            Console.WriteLine("OYUNA GEC. Bekliyorum (en fazla 90 sn)...");

            // Tetikleyici artik fareye bagli degil: yon tuslariyla kaydirmayi olcecegiz,
            // o sirada fare hic oynamayabilir. Oyun on plana gelip bir sn gecmesi yeterli.
            var wait = Stopwatch.StartNew();
            var foregroundSince = -1.0;
            while (wait.Elapsed.TotalSeconds < 90)
            {
                Thread.Sleep(100);
                if (GetForegroundWindow() != game.MainWindowHandle)
                {
                    foregroundSince = -1.0;
                    continue;
                }

                if (foregroundSince < 0) { foregroundSince = wait.Elapsed.TotalSeconds; }
                if (wait.Elapsed.TotalSeconds - foregroundSince > 1.0) { break; }
            }

            if (foregroundSince < 0)
            {
                Console.WriteLine("Oyun on plana gelmedi; vazgectim.");
                return 1;
            }

            Console.WriteLine("Basladi - haritayi YON TUSLARIYLA kaydir (12 sn).");
            Console.WriteLine();
            Console.WriteLine("  imlec(istemci)      kaydirma            varsayilan        zoom     merkez");

            var origin = new WinPoint { X = 0, Y = 0 };
            ClientToScreen(game.MainWindowHandle, ref origin);

            // Karsilastirma SADECE harita alanlari uzerinden: imlec zaten degisiyor,
            // onu satira katarsak her satir "farkli" cikar ve hicbir sey ogrenmeyiz.
            var sw = Stopwatch.StartNew();
            var lastFields = "";
            var changes = 0;
            var samples = 0;
            var minCx = int.MaxValue;
            var maxCx = int.MinValue;
            var minCy = int.MaxValue;
            var maxCy = int.MinValue;

            while (sw.Elapsed.TotalSeconds < 12)
            {
                Thread.Sleep(100);
                if (!GetCursorPos(out var cur)) { continue; }
                var e = Reread(handle, e0.Address);
                if (e is not { } el) { continue; }
                var c = MapCenter(handle, e0.Address, clientW, clientH);

                var cx = cur.X - origin.X;
                var cy = cur.Y - origin.Y;
                minCx = Math.Min(minCx, cx);
                maxCx = Math.Max(maxCx, cx);
                minCy = Math.Min(minCy, cy);
                maxCy = Math.Max(maxCy, cy);
                samples++;

                var fields = $"({el.ShiftX,8:F2},{el.ShiftY,8:F2})   " +
                             $"({el.DefShiftX,6:F1},{el.DefShiftY,6:F1})   {el.Zoom:F3}   " +
                             (c is { } cc ? $"({cc.X,8:F2},{cc.Y,8:F2})" : "-");
                if (fields == lastFields) { continue; }
                lastFields = fields;
                changes++;
                Console.WriteLine($"  ({cx,5},{cy,5})   {fields}");
            }

            Console.WriteLine();
            Console.WriteLine($"{samples} ornek; imlec X {minCx}..{maxCx} (ekran 0..{clientW}), " +
                              $"Y {minCy}..{maxCy} (ekran 0..{clientH})");

            var reachedEdges = minCx < clientW * 0.1 && maxCx > clientW * 0.9 &&
                               minCy < clientH * 0.1 && maxCy > clientH * 0.9;
            if (!reachedEdges)
            {
                Console.WriteLine("DIKKAT: imlec dort kenarin hepsine gitmedi - kenar kaydirmasi test EDILMEDI.");
            }

            Console.WriteLine(changes <= 1
                ? "Harita alanlari HIC degismedi: oge imlecten etkilenmiyor."
                : $"Harita alanlari {changes} kez degisti - imlecle birlikte hareket ediyor.");
            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    ///     Harita ogesinin BUTUN alanlarini once/sonra karsilastirir.
    ///
    ///     "Kaydirma nerede yaziyor" sorusuna tahminle cevap vermek yerine olcuyoruz:
    ///     ogenin 0x3A0 baytini kaydediyoruz, sen haritayi kaydiriyorsun, sonra
    ///     degisen her alani ofsetiyle basiyoruz. Patch UI yapisini oynatmis olsa bile
    ///     bu yontem etkilenmiyor.
    ///
    ///     Iki adim, arada zamanlama derdi yok:
    ///         MapScan mapdiff snap     (haritayi kaydirmadan once)
    ///         MapScan mapdiff          (kaydirdiktan sonra)
    /// </summary>
    internal static int Diff(string[] args)
    {
        var file = Path.Combine(Path.GetTempPath(), "poe2-mapdiff.bin");

        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            GetClientRect(game.MainWindowHandle, out var rc);
            var clientWidth = rc.Right - rc.Left;
            var clientHeight = rc.Bottom - rc.Top;

            var snap = args.Length > 0 && args[0].Equals("snap", StringComparison.OrdinalIgnoreCase);
            var watch = args.Length > 0 && args[0].Equals("watch", StringComparison.OrdinalIgnoreCase);

            ulong address;
            byte[]? before = null;

            if ((snap || watch) && args.Length > 1)
            {
                // Adres verilebiliyor: her koşuda 88 aday arasindan secim yapmak
                // tutarsiz sonuc veriyordu, bilinen ogeyi sabitlemek daha guvenli.
                address = ulong.Parse(
                    args[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                    System.Globalization.NumberStyles.HexNumber);
            }
            else if (snap || watch)
            {
                var found = FindLargeMap(handle, Console.WriteLine);
                if (found is not { } e)
                {
                    Console.Error.WriteLine("Buyuk harita ogesi bulunamadi - oyunda Tab acik mi?");
                    return 1;
                }

                address = e.Address;
            }
            else
            {
                if (!File.Exists(file))
                {
                    Console.Error.WriteLine("Once 'MapScan mapdiff snap' calistir.");
                    return 1;
                }

                var saved = File.ReadAllBytes(file);
                address = BitConverter.ToUInt64(saved, 0);
                before = saved[8..];
            }

            var now = new byte[Span];
            if (!Native.ReadProcessMemory(handle, (IntPtr)address, now, (IntPtr)Span, out var got) ||
                (long)got != Span)
            {
                Console.Error.WriteLine($"Oge okunamadi ({address:X}) - oyun yeniden mi baslatildi?");
                return 1;
            }

            // "mapdiff watch": tetikleyici yok. Ben baslatirim, sen kaydirirsin;
            // degisen her alan degistigi anda basiliyor. Boylece "kaydirdiktan sonra
            // oyun haritayi geri topluyor mu" sorusu da cevaplanmis oluyor.
            if (args.Length > 0 && args[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"oge {address:X} - 25 sn boyunca izliyorum. OYUNA GEC ve haritayi kaydir.");
                Console.WriteLine();
                Console.WriteLine("  sn      ofset   deger           merkez");

                // Zamanlama derdi yok: ilk degisikligi gorene kadar bekliyor (90 sn),
                // sonra 6 saniye daha izleyip cikiyor. Boylece bastan izlemeye
                // yetismek zorunda degilsin.
                var prev = (byte[])now.Clone();
                var clock = Stopwatch.StartNew();
                double? firstChange = null;

                while (clock.Elapsed.TotalSeconds < 90)
                {
                    Thread.Sleep(80);
                    if (!Native.ReadProcessMemory(handle, (IntPtr)address, now, (IntPtr)Span, out var g2) ||
                        (long)g2 != Span)
                    {
                        continue;
                    }

                    var changed = false;
                    for (var i = 0; i + 4 <= Span; i += 4)
                    {
                        if (BitConverter.ToInt32(prev, i) == BitConverter.ToInt32(now, i)) { continue; }
                        changed = true;
                        break;
                    }

                    if (changed)
                    {
                        if (firstChange is null)
                        {
                            firstChange = clock.Elapsed.TotalSeconds;
                            Console.WriteLine($"  (hareket {firstChange:F1}. saniyede basladi)");
                        }

                        var center = MapCenter(handle, address, clientWidth, clientHeight);
                        for (var i = 0; i + 4 <= Span; i += 4)
                        {
                            if (BitConverter.ToInt32(prev, i) == BitConverter.ToInt32(now, i)) { continue; }

                            var v = BitConverter.ToSingle(now, i);
                            Console.WriteLine($"  {clock.Elapsed.TotalSeconds,5:F1}   +0x{i:X3}   {v,12:F2}   " +
                                              (center is { } cc ? $"({cc.X,8:F1},{cc.Y,8:F1})" : "-"));
                        }

                        now.CopyTo(prev, 0);
                    }

                    if (firstChange is { } t && clock.Elapsed.TotalSeconds - t > 6) { break; }
                }

                Console.WriteLine();
                Console.WriteLine(firstChange is null
                    ? "90 sn boyunca hicbir alan degismedi."
                    : "izleme bitti.");
                return 0;
            }

            if (snap)
            {
                var blob = new byte[8 + Span];
                BitConverter.GetBytes(address).CopyTo(blob, 0);
                now.CopyTo(blob, 8);
                File.WriteAllBytes(file, blob);

                Console.WriteLine($"oge {address:X} kaydedildi.");
                Console.WriteLine();
                Console.WriteLine("Simdi oyunda haritayi KAYDIR (yon tuslariyla), sonra:");
                Console.WriteLine("  MapScan mapdiff");
                return 0;
            }

            Console.WriteLine($"oge {address:X}");
            Console.WriteLine();
            Console.WriteLine("  ofset   once            sonra           fark");

            var changes = 0;
            for (var i = 0; i + 4 <= Span; i += 4)
            {
                var a = BitConverter.ToSingle(before!, i);
                var b = BitConverter.ToSingle(now, i);
                var ia = BitConverter.ToInt32(before!, i);
                var ib = BitConverter.ToInt32(now, i);
                if (ia == ib) { continue; }

                changes++;
                var asFloat = float.IsFinite(a) && float.IsFinite(b) &&
                              Math.Abs(a) < 1e9 && Math.Abs(b) < 1e9;

                Console.WriteLine(asFloat
                    ? $"  +0x{i:X3}   {a,14:F3}  {b,14:F3}  {b - a,14:F3}"
                    : $"  +0x{i:X3}   {ia,14}  {ib,14}  (tamsayi)");
            }

            Console.WriteLine();
            Console.WriteLine(changes == 0
                ? "Hicbir alan degismedi - kaydirma bu ogede DEGIL, baska bir yerde tutuluyor."
                : $"{changes} alan degisti. Kaydirma bunlardan biri.");
            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    ///     BUTUN UI ogelerinin konum/kaydirma alanlarini once-sonra karsilastirir.
    ///
    ///     Tek ogeye bakmak yetmedi: haritayi kaydirdigimizda bizim okudugumuz
    ///     alanlarin hicbiri anlamli degismedi. O halde kaydirma baska bir ogede
    ///     tutuluyor. Hangisi oldugunu tahmin etmek yerine hepsini olcuyoruz.
    ///
    ///         MapScan uisnap     (kaydirmadan once)
    ///         MapScan uidiff     (kaydirdiktan sonra)
    /// </summary>
    internal static int Snapshot(bool compare)
    {
        var file = Path.Combine(Path.GetTempPath(), "poe2-uisnap.bin");

        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            if (!compare)
            {
                var all = FindAll(handle, Console.WriteLine);
                using var w = new BinaryWriter(File.Create(file));
                w.Write(all.Count);
                foreach (var e in all)
                {
                    w.Write(e.Address);
                    w.Write(e.RelX);
                    w.Write(e.RelY);
                    w.Write(e.ShiftX);
                    w.Write(e.ShiftY);
                    w.Write(e.ModX);
                    w.Write(e.ModY);
                    w.Write(e.Zoom);
                }

                Console.WriteLine($"{all.Count} oge kaydedildi.");
                Console.WriteLine();
                Console.WriteLine("Simdi oyunda haritayi KAYDIR, sonra:  MapScan uidiff");
                return 0;
            }

            if (!File.Exists(file))
            {
                Console.Error.WriteLine("Once 'MapScan uisnap' calistir.");
                return 1;
            }

            using var r = new BinaryReader(File.OpenRead(file));
            var count = r.ReadInt32();
            Console.WriteLine($"{count} oge karsilastiriliyor...");
            Console.WriteLine();
            Console.WriteLine("  adres            alan       once        sonra       fark");

            var hits = 0;
            for (var k = 0; k < count; k++)
            {
                var address = r.ReadUInt64();
                var relX = r.ReadSingle();
                var relY = r.ReadSingle();
                var shiftX = r.ReadSingle();
                var shiftY = r.ReadSingle();
                var modX = r.ReadSingle();
                var modY = r.ReadSingle();
                var zoom = r.ReadSingle();

                if (Reread(handle, address) is not { } e) { continue; }

                Report(address, "goreli X", relX, e.RelX, ref hits);
                Report(address, "goreli Y", relY, e.RelY, ref hits);
                Report(address, "kaydirma X", shiftX, e.ShiftX, ref hits);
                Report(address, "kaydirma Y", shiftY, e.ShiftY, ref hits);
                Report(address, "degistir X", modX, e.ModX, ref hits);
                Report(address, "degistir Y", modY, e.ModY, ref hits);
                Report(address, "zoom", zoom, e.Zoom, ref hits);
            }

            Console.WriteLine();
            Console.WriteLine(hits == 0
                ? "Hicbir UI ogesinde konum/kaydirma degismedi."
                : $"{hits} alan degisti - kaydirmayi tutan oge bunlarin arasinda.");
            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private static void Report(ulong address, string field, float before, float after, ref int hits)
    {
        if (!float.IsFinite(before) || !float.IsFinite(after)) { return; }
        if (Math.Abs(after - before) < 1f) { return; }
        if (Math.Abs(before) > 1e6 || Math.Abs(after) > 1e6) { return; }

        hits++;
        Console.WriteLine($"  {address:X}  {field,-11} {before,10:F2}  {after,10:F2}  {after - before,10:F2}");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WinPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref WinPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out WinRect lpRect);
}
