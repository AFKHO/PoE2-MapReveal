using System.Collections.Concurrent;
using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Oyunun dunya-ekran matrisini bulur ve bir dunya noktasini ekrana dusurur.
///
///     Neden lazim: Tab haritasi dunyaya sabit ciziliyor, oyunun kamerasi ise fare
///     imlecine dogru hafifce yasliyor. Biz overlay'i oyuncunun konumuna sabitledigimiz
///     icin fare gezdirilince aramizda fark aciliyor. Kamera matrisi o yaslanmayi
///     iceriyor, dolayisiyla farki OLCEBILIYORUZ.
///
///     Nasil buluyoruz - hicbir dis offset olmadan, matrisin kendi geometrisinden:
///     PoE kamerasi sabit 45 derece yaw'li bir 3/4 bakis. Dunyada +X ve +Y yonunde esit
///     adimlar ekranda esit uzunlukta, dikey eksene gore ayna simetrik iki vektor verir.
///     Bu, matrisin ilk iki satirinda dogrudan okunabilen bir iliski: M[0][0] = -M[1][0]
///     ve M[0][1] = M[1][1] (ya da devrik duzende ayni sey sutunlarda). Rastgele 16 float
///     bunu saglamaz; saglayanlar arasinda da oyuncuyu ekranin icine dusuren tek olur.
///
///     Iki mod: "camera" ikiz kopyali (Gordin'in notu: matris bellekte pes pese iki kez
///     yazili) adaylara bakar, hizlidir. "camera all" ikizlik sarti olmadan her adrese
///     bakar; on filtre ucuz oldugu icin 8 GB'i paralel olarak 10-20 saniyede gecer.
/// </summary>
internal static class CameraFinder
{
    private const int MatrixBytes = 64;
    private const int ReadBlock = 1 << 20;

    internal readonly record struct Candidate(ulong Address, float[] M, bool Transposed, float Score, bool Twin);

    /// <summary>Dunya noktasini ekran pikseline cevirir. Oyunun kendi hesabi: t = v * M.</summary>
    internal static (float X, float Y)? WorldToScreen(
        float[] m, float worldX, float worldY, float height, int clientW, int clientH)
    {
        Span<double> v = stackalloc double[4] { worldX, worldY, height, 1.0 };
        Span<double> t = stackalloc double[4];

        for (var i = 0; i < 4; i++)
        {
            t[i] = 0;
            for (var j = 0; j < 4; j++) { t[i] += m[(j * 4) + i] * v[j]; }
        }

        if (t[3] == 0 || !double.IsFinite(t[3])) { return null; }
        for (var i = 0; i < 4; i++) { t[i] /= t[3]; }
        if (!double.IsFinite(t[0]) || !double.IsFinite(t[1])) { return null; }

        return ((float)((t[0] + 1.0) * (clientW / 2.0)),
                (float)((1.0 - t[1]) * (clientH / 2.0)));
    }

    internal static float[]? Read(IntPtr handle, ulong address)
    {
        var buf = new byte[MatrixBytes];
        if (!Native.ReadProcessMemory(handle, (IntPtr)address, buf, (IntPtr)MatrixBytes, out var got) ||
            (long)got != MatrixBytes)
        {
            return null;
        }

        var m = new float[16];
        for (var k = 0; k < 16; k++) { m[k] = BitConverter.ToSingle(buf, k * 4); }
        return m;
    }

    internal static float[] Transpose(float[] m)
    {
        var t = new float[16];
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++) { t[(c * 4) + r] = m[(r * 4) + c]; }
        }

        return t;
    }

    /// <summary>
    ///     Izometrik kalite puani - kucuk iyi. Dunyada +1000 X ve +1000 Y'nin ekrandaki
    ///     vektorleri esit boyda ve ayna simetrik olmali; oyuncu ekranin icinde olmali.
    ///     Yukseklik yonu icin sadece hafif bir ceza var: PoE2'de isaret ters olabilir.
    /// </summary>
    internal static float IsometricScore(float[] m, float px, float py, int clientW, int clientH)
    {
        const float Step = 1000f;
        var o = WorldToScreen(m, px, py, 0f, clientW, clientH);
        var ax = WorldToScreen(m, px + Step, py, 0f, clientW, clientH);
        var ay = WorldToScreen(m, px, py + Step, 0f, clientW, clientH);
        if (o is not { } o0 || ax is not { } a || ay is not { } b) { return float.MaxValue; }

        var (adx, ady) = (a.X - o0.X, a.Y - o0.Y);
        var (bdx, bdy) = (b.X - o0.X, b.Y - o0.Y);
        var la = MathF.Sqrt((adx * adx) + (ady * ady));
        var lb = MathF.Sqrt((bdx * bdx) + (bdy * bdy));
        if (!float.IsFinite(la) || !float.IsFinite(lb)) { return float.MaxValue; }

        // 1000 birim ekranda ne gorunmeyecek kadar kucuk ne de ekrandan tasacak kadar buyuk.
        if (la < 15f || la > 4000f || lb < 15f || lb > 4000f) { return float.MaxValue; }

        var big = MathF.Max(la, lb);
        var score = (MathF.Abs(la - lb) / big) +
                    (MathF.Abs(adx + bdx) / big) +
                    (MathF.Abs(ady - bdy) / big);

        // Oyuncu ekranin disindaysa bu kamera ona bakmiyor.
        if (o0.X < -clientW || o0.X > 2 * clientW || o0.Y < -2 * clientH || o0.Y > 3 * clientH) { return float.MaxValue; }
        score += MathF.Abs(o0.X - (clientW / 2f)) / clientW * 0.5f;

        return score;
    }

    /// <summary>
    ///     Ucuz on filtre: 45 derecelik yaw'in matristeki izi. Satir duzeninde
    ///     M[0][0] = -M[1][0] ve M[0][1] = M[1][1]; devrik duzende M[0][0] = -M[0][1] ve
    ///     M[1][0] = M[1][1]. Yuzde 20 tolerans, sifir olmayan degerler.
    /// </summary>
    private static bool YawSymmetry(float m00, float m01, float m10, float m11, out bool transposed)
    {
        transposed = false;
        const float Tol = 0.2f;

        var big = MathF.Max(MathF.Max(MathF.Abs(m00), MathF.Abs(m01)), MathF.Max(MathF.Abs(m10), MathF.Abs(m11)));
        if (!(big > 1e-5f) || !float.IsFinite(big)) { return false; }

        // satir duzeni
        if (MathF.Abs(m00 + m10) <= Tol * big && MathF.Abs(m01 - m11) <= Tol * big &&
            MathF.Abs(m00) > 0.2f * big && MathF.Abs(m01) > 0.05f * big)
        {
            return true;
        }

        // devrik duzen
        if (MathF.Abs(m00 + m01) <= Tol * big && MathF.Abs(m10 - m11) <= Tol * big &&
            MathF.Abs(m00) > 0.2f * big && MathF.Abs(m10) > 0.05f * big)
        {
            transposed = true;
            return true;
        }

        return false;
    }

    internal static List<Candidate> Find(
        IntPtr handle, float playerX, float playerY, int clientW, int clientH,
        bool requireTwin, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();

        // Once bolgeleri topla, sonra paralel tara.
        var regions = new List<(ulong Base, long Size)>();
        ulong address = 0x10000;
        while (address < 0x7FFFFFFF0000UL)
        {
            if (Native.VirtualQueryEx(handle, (IntPtr)address, out var mbi, mbiSize) == IntPtr.Zero) { break; }
            var size = (long)mbi.RegionSize;
            if (size <= 0) { break; }

            // Sadece ozel yigin degil: modulun kendi veri bolgeleri (image) ve eslenmis
            // bolgeler de dahil. Kamera nesnesi statik bir yerde duruyor olabilir.
            var usable = mbi.State == Native.MemCommit &&
                         (mbi.Protect & Native.PageGuard) == 0 &&
                         (mbi.Protect == Native.PageReadWrite || mbi.Protect == 0x40);
            if (usable) { regions.Add(((ulong)mbi.BaseAddress.ToInt64(), size)); }
            address += (ulong)size;
        }

        var hits = new ConcurrentBag<Candidate>();
        long prefilter = 0;
        long scored = 0;
        var span = requireTwin ? MatrixBytes * 2 : MatrixBytes;

        Parallel.ForEach(
            regions,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            () => new byte[ReadBlock],
            (region, _, buf) =>
            {
                for (long off = 0; off < region.Size; off += ReadBlock - span)
                {
                    var want = (int)Math.Min(ReadBlock, region.Size - off);
                    if (want < span) { break; }
                    if (!Native.ReadProcessMemory(handle, (IntPtr)(region.Base + (ulong)off), buf,
                            (IntPtr)want, out var got) || (long)got < span)
                    {
                        continue;
                    }

                    var n = (int)got;
                    for (var i = 0; i + span <= n; i += 4)
                    {
                        bool transposed;
                        if (requireTwin)
                        {
                            if (!SameBlock(buf, i)) { continue; }
                            if (i >= MatrixBytes && SameBlock(buf, i - MatrixBytes)) { continue; }
                            transposed = false;
                        }
                        else
                        {
                            var m00 = BitConverter.ToSingle(buf, i);
                            var m01 = BitConverter.ToSingle(buf, i + 4);
                            var m10 = BitConverter.ToSingle(buf, i + 16);
                            var m11 = BitConverter.ToSingle(buf, i + 20);
                            if (!YawSymmetry(m00, m01, m10, m11, out transposed)) { continue; }
                        }

                        var m = new float[16];
                        var ok = true;
                        var distinct = 0;
                        for (var k = 0; k < 16 && ok; k++)
                        {
                            m[k] = BitConverter.ToSingle(buf, i + (k * 4));
                            if (!float.IsFinite(m[k]) || MathF.Abs(m[k]) > 1e7f) { ok = false; }
                            else if (MathF.Abs(m[k]) > 1e-7f) { distinct++; }
                        }

                        if (!ok || distinct < 6) { continue; }
                        Interlocked.Increment(ref prefilter);

                        // Ikiz modunda duzeni bilmiyoruz: ikisini de puanla, iyisini al.
                        var here = region.Base + (ulong)off + (ulong)i;
                        if (requireTwin)
                        {
                            var s1 = IsometricScore(m, playerX, playerY, clientW, clientH);
                            var t = Transpose(m);
                            var s2 = IsometricScore(t, playerX, playerY, clientW, clientH);
                            Interlocked.Increment(ref scored);
                            if (s1 < 1.5f) { hits.Add(new Candidate(here, m, false, s1, true)); }
                            if (s2 < 1.5f) { hits.Add(new Candidate(here, t, true, s2, true)); }
                        }
                        else
                        {
                            var mm = transposed ? Transpose(m) : m;
                            var s = IsometricScore(mm, playerX, playerY, clientW, clientH);
                            Interlocked.Increment(ref scored);
                            if (s < 1.5f) { hits.Add(new Candidate(here, mm, transposed, s, false)); }
                        }
                    }
                }

                return buf;
            },
            _ => { });

        sw.Stop();
        log?.Invoke($"{regions.Count} bolge, on filtreyi gecen {prefilter}, puanlanan {scored}, " +
                    $"izometrik gorunen {hits.Count}, {sw.Elapsed.TotalSeconds:F0} sn");
        return hits.OrderBy(h => h.Score).ToList();
    }

    private static bool SameBlock(byte[] buf, int i)
    {
        for (var k = 0; k < MatrixBytes; k += 8)
        {
            if (BitConverter.ToUInt64(buf, i + k) != BitConverter.ToUInt64(buf, i + MatrixBytes + k))
            {
                return false;
            }
        }

        return true;
    }

    internal static int Run(bool all)
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            ulong moduleBase;
            try { moduleBase = (ulong)(game.MainModule?.BaseAddress.ToInt64() ?? 0); }
            catch { moduleBase = 0; }

            var pos = ReadPlayerPosition(handle, moduleBase);
            if (pos is not { } p)
            {
                Console.Error.WriteLine("Oyuncu konumu okunamadi - dogrulama yapilamaz.");
                return 1;
            }

            GetClientRect(game.MainWindowHandle, out var rect);
            var clientW = rect.Right - rect.Left;
            var clientH = rect.Bottom - rect.Top;

            Console.WriteLine($"oyuncu dunya konumu ({p.X:F1}, {p.Y:F1}, {p.Z:F1})");
            Console.WriteLine($"istemci {clientW}x{clientH}, ortasi ({clientW / 2f:F0}, {clientH / 2f:F0})");
            Console.WriteLine(all ? "mod: ikizsiz tam tarama" : "mod: ikiz kopyali adaylar");
            Console.WriteLine();

            var hits = Find(handle, p.X, p.Y, clientW, clientH, !all, Console.WriteLine);
            Console.WriteLine();

            var seen = new HashSet<string>();
            var shown = 0;
            foreach (var c in hits)
            {
                var key = string.Join(",", c.M.Select(f => f.ToString("R")));
                if (!seen.Add(key)) { continue; }
                if (++shown > 15) { break; }

                var m = c.M;
                var s0 = WorldToScreen(m, p.X, p.Y, 0f, clientW, clientH);
                var sz = WorldToScreen(m, p.X, p.Y, p.Z, clientW, clientH);
                var sx = WorldToScreen(m, p.X + 1000f, p.Y, 0f, clientW, clientH);
                var sy = WorldToScreen(m, p.X, p.Y + 1000f, 0f, clientW, clientH);
                var su = WorldToScreen(m, p.X, p.Y, 100f, clientW, clientH);
                Console.WriteLine($"  {c.Address:X}  puan {c.Score:F3}  {(c.Transposed ? "devrik" : "satir-sirali")}  " +
                                  $"{(c.Twin ? "ikiz" : "")}  ({hits.Count(h => h.Address == c.Address)} kez)");
                Console.WriteLine($"      oyuncu h=0 ({s0?.X:F1}, {s0?.Y:F1})  h={p.Z:F0} ({sz?.X:F1}, {sz?.Y:F1})  " +
                                  $"+1000X ({sx?.X - s0?.X:F1}, {sx?.Y - s0?.Y:F1})  +1000Y ({sy?.X - s0?.X:F1}, {sy?.Y - s0?.Y:F1})  " +
                                  $"+100h ({su?.X - s0?.X:F1}, {su?.Y - s0?.Y:F1})");
                for (var r = 0; r < 4; r++)
                {
                    Console.WriteLine($"        {m[r * 4],14:F7} {m[(r * 4) + 1],14:F7} {m[(r * 4) + 2],14:F7} {m[(r * 4) + 3],14:F7}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"{seen.Count} farkli matris gosterildi, toplam aday {hits.Count}");
            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    ///     Canli kamerayi bulur: adaylari 8 saniye boyunca 100 ms'de bir okur. Fare
    ///     gezdirilirken kamera yaslanir; canli matrisin ceviri satiri her karede
    ///     degisir, eski kopyalar ya hic degismez ya da oyuncuyu ekranin disina dusurur.
    ///     Her aday icin: kac kez degisti, oyuncunun izdusumu nerelerde dolasti.
    /// </summary>
    internal static int Track()
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            ulong moduleBase;
            try { moduleBase = (ulong)(game.MainModule?.BaseAddress.ToInt64() ?? 0); }
            catch { moduleBase = 0; }

            if (ReadPlayerPosition(handle, moduleBase) is not { } p)
            {
                Console.Error.WriteLine("Oyuncu konumu okunamadi.");
                return 1;
            }

            GetClientRect(game.MainWindowHandle, out var rect);
            var clientW = rect.Right - rect.Left;
            var clientH = rect.Bottom - rect.Top;

            var found = Find(handle, p.X, p.Y, clientW, clientH, requireTwin: false, Console.WriteLine);
            var candidates = found.Where(c => c.Score < 0.6f)
                .GroupBy(c => c.Address).Select(g => g.First()).ToList();
            Console.WriteLine($"{candidates.Count} aday izleniyor - FAREYI EKRANDA GEZDIR (8 sn)");
            Console.WriteLine();

            // Her ornekte uc soru: (1) hala izometrik bir matris mi, (2) oyuncuyu ekranin
            // icine dusuruyor mu, (3) bir onceki GECERLI ornekten farkli mi?
            // Canli kamera: her ornekte gecerli VE cok degisiyor.
            // Bayat kopya: her ornekte gecerli, hic degismiyor.
            // Gecici tampon: az ornekte gecerli - arada cop var.
            var last = new Dictionary<ulong, float[]>();
            var changes = new Dictionary<ulong, int>();
            var valid = new Dictionary<ulong, int>();
            var minX = new Dictionary<ulong, float>();
            var maxX = new Dictionary<ulong, float>();
            var minY = new Dictionary<ulong, float>();
            var maxY = new Dictionary<ulong, float>();
            var lastPos = new Dictionary<ulong, (float X, float Y)>();

            // Kullaniciyi bekle: oyun penceresi on plana gelip imlec oynamaya baslayana
            // kadar ornekleme yok. Kamera ancak oyun on plandayken yaslanir; onceki
            // denemeler bu yuzden bos cikmisti - komut calisirken oyunda degildi.
            Console.WriteLine("OYUNA GEC ve fareyi ekranda gezdir. Bekliyorum (en fazla 90 sn)...");
            GetCursorPos(out var prevCursor);
            var armed = 0.0;
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < 90)
            {
                Thread.Sleep(50);
                if (GetForegroundWindow() != game.MainWindowHandle) { continue; }
                if (!GetCursorPos(out var c0)) { continue; }
                armed += Math.Sqrt(Math.Pow(c0.X - prevCursor.X, 2) + Math.Pow(c0.Y - prevCursor.Y, 2));
                prevCursor = c0;
                if (armed > 150) { break; }
            }

            if (armed <= 150)
            {
                Console.WriteLine("Oyun on plana gelmedi ya da fare oynamadi; vazgectim.");
                return 1;
            }

            Console.WriteLine("Basladi - gezdirmeye devam et (8 sn).");
            var cursorTravel = 0.0;
            var foregroundSamples = 0;

            var sw = Stopwatch.StartNew();
            var samples = 0;
            while (sw.Elapsed.TotalSeconds < 8)
            {
                if (GetForegroundWindow() == game.MainWindowHandle) { foregroundSamples++; }
                if (GetCursorPos(out var cur))
                {
                    cursorTravel += Math.Sqrt(Math.Pow(cur.X - prevCursor.X, 2) + Math.Pow(cur.Y - prevCursor.Y, 2));
                    prevCursor = cur;
                }

                var pos = ReadPlayerPosition(handle, moduleBase) ?? p;
                foreach (var c in candidates)
                {
                    var m = Read(handle, c.Address);
                    if (m is null) { continue; }
                    if (c.Transposed) { m = Transpose(m); }

                    if (IsometricScore(m, pos.X, pos.Y, clientW, clientH) >= 0.6f) { continue; }
                    if (WorldToScreen(m, pos.X, pos.Y, 0f, clientW, clientH) is not { } s) { continue; }
                    if (s.X < 0 || s.X > clientW || s.Y < 0 || s.Y > clientH) { continue; }

                    valid[c.Address] = valid.GetValueOrDefault(c.Address) + 1;
                    if (last.TryGetValue(c.Address, out var prev) && !prev.SequenceEqual(m))
                    {
                        changes[c.Address] = changes.GetValueOrDefault(c.Address) + 1;
                    }

                    last[c.Address] = m;
                    lastPos[c.Address] = s;
                    minX[c.Address] = MathF.Min(minX.GetValueOrDefault(c.Address, float.MaxValue), s.X);
                    maxX[c.Address] = MathF.Max(maxX.GetValueOrDefault(c.Address, float.MinValue), s.X);
                    minY[c.Address] = MathF.Min(minY.GetValueOrDefault(c.Address, float.MaxValue), s.Y);
                    maxY[c.Address] = MathF.Max(maxY.GetValueOrDefault(c.Address, float.MinValue), s.Y);
                }

                samples++;
                Thread.Sleep(100);
            }

            Console.WriteLine($"{samples} ornek alindi; oyun {foregroundSamples}/{samples} ornekte on plandaydi; " +
                              $"imlec toplam {cursorTravel:F0} piksel yol aldi" +
                              (cursorTravel < 500 || foregroundSamples < samples / 2
                                  ? "  <-- GECERSIZ ORNEKLEME (fare oynamadi ya da oyun on planda degildi)"
                                  : "") + ".");
            Console.WriteLine("Gecerli ornek sayisina, sonra degisme sayisina gore:");
            Console.WriteLine();
            foreach (var c in candidates
                         .Where(c => valid.ContainsKey(c.Address))
                         .OrderByDescending(c => valid.GetValueOrDefault(c.Address))
                         .ThenByDescending(c => changes.GetValueOrDefault(c.Address))
                         .Take(25))
            {
                var a = c.Address;
                var lp = lastPos.GetValueOrDefault(a);
                Console.WriteLine($"  {a:X}  {(c.Transposed ? "devrik" : "satir ")}  gecerli {valid.GetValueOrDefault(a),3}/{samples}  " +
                                  $"degisti {changes.GetValueOrDefault(a),3} kez  " +
                                  $"oyuncu X [{minX.GetValueOrDefault(a):F0} .. {maxX.GetValueOrDefault(a):F0}]  " +
                                  $"Y [{minY.GetValueOrDefault(a):F0} .. {maxY.GetValueOrDefault(a):F0}]  " +
                                  $"son ({lp.X:F1}, {lp.Y:F1})");
            }

            return 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>Oyuncunun dunya konumu: modul+0x4588798 -> +0x1E0 -> +0x3F0 -> +0x88.</summary>
    internal static (float X, float Y, float Z)? ReadPlayerPosition(IntPtr handle, ulong moduleBase)
    {
        if (moduleBase == 0) { return null; }

        var eight = new byte[8];
        var cursor = moduleBase + 0x4588798;
        foreach (var hop in new ulong[] { 0x1E0, 0x3F0 })
        {
            if (!Native.ReadProcessMemory(handle, (IntPtr)cursor, eight, (IntPtr)8, out var g) || (long)g != 8)
            {
                return null;
            }

            var value = BitConverter.ToUInt64(eight, 0);
            if (value == 0) { return null; }
            cursor = value + hop;
        }

        if (!Native.ReadProcessMemory(handle, (IntPtr)cursor, eight, (IntPtr)8, out var g2) || (long)g2 != 8)
        {
            return null;
        }

        var obj = BitConverter.ToUInt64(eight, 0);
        if (obj == 0) { return null; }

        var twelve = new byte[12];
        if (!Native.ReadProcessMemory(handle, (IntPtr)(obj + 0x88), twelve, (IntPtr)12, out var g3) ||
            (long)g3 != 12)
        {
            return null;
        }

        var x = BitConverter.ToSingle(twelve, 0);
        var y = BitConverter.ToSingle(twelve, 4);
        var z = BitConverter.ToSingle(twelve, 8);
        if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0 || y < 0) { return null; }
        return (x, y, z);
    }

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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WinPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
