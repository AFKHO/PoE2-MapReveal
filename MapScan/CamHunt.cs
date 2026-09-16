using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Kameranin odak noktasini bulur.
///
///     Neden: haritayi yon tuslariyla kaydirmak ayri bir "kaydirma" alanina yazmiyor.
///     Butun UI ogelerinin konum/kaydirma alanlari tarandi, temiz bir 2B kaydirma
///     bulunamadi. Bulunan sey, kaydirinca ~4200'e cikip ortalayinca ~25-74'e donen
///     nesne-kamera MESAFELERI oldu. Yani kaydirma kamerayi hareket ettiriyor.
///
///     O halde dogru hedef kameranin konumu. Aradigimiz sey su iki sarti birden
///     tutmak zorunda, ve bu tesadufe kapali:
///
///       1. Harita ORTALIYKEN oyuncunun konumuna cok yakin
///       2. Harita KAYDIRILMISKEN oradan belirgin bicimde uzaklasmis
///
///     Adim adim, zamanlama sende (bu projede benim baslatip senin yetismeye
///     calistigin olcumler dort kez ust uste bosa gitti):
///
///         MapScan camhunt snap      harita ORTALI - oyuncuya yakin ciftleri topla
///         MapScan camhunt panned    harita KAYDIRILMIS - uzaklasanlari tut
///         MapScan camhunt centred   harita ORTALI - geri donenleri tut
///         MapScan camhunt show      kalanlari listele
/// </summary>
internal static class CamHunt
{
    private const int ReadBlock = 1 << 20;

    /// <summary>
    ///     Kamera, harita ortaliyken oyuncunun tam ustunde olmali - bu yuzden pencere
    ///     dar. 400 birimle denendiginde 12.7 milyon cift cikti, cunku alanin kendi
    ///     geometrisi de oyuncunun yakininda bir suru dunya koordinati tasiyor.
    /// </summary>
    private const float NearPlayer = 120f;

    private const float MinTravel = 400f;

    private static string File_ => Path.Combine(Path.GetTempPath(), "poe2-camhunt.bin");

    /// <summary>
    ///     Kaydir/ortala sinavini gecen adreslerin yazildigi dosya. MapView bunu
    ///     okuyor. Ayri dosya, cunku bunlar surece ozel adresler - oyun yeniden
    ///     baslarsa gecersizler, ama okunan deger sacma olunca kaydirma zaten
    ///     sifir kaliyor, yani bayat dosya zarar vermiyor.
    /// </summary>
    internal static string FocusFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "poe2-map", "camera-focus.txt");

    /// <summary>Kaydedilmis odak adreslerini okur. Dosya yoksa bos liste.</summary>
    internal static List<ulong> LoadSaved()
    {
        var list = new List<ulong>();
        try
        {
            if (!System.IO.File.Exists(FocusFile)) { return list; }

            foreach (var line in System.IO.File.ReadAllLines(FocusFile))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith('#')) { continue; }
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { text = text[2..]; }
                if (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var v))
                {
                    list.Add(v);
                }
            }
        }
        catch
        {
            // Bozuk dosya kaydirma takibini kapatir, uygulamayi durdurmaz.
        }

        return list;
    }

    private static void SaveFocus(List<ulong> list)
    {
        var dir = Path.GetDirectoryName(FocusFile)!;
        Directory.CreateDirectory(dir);
        var lines = new List<string>
        {
            "# Kamera odagi adresleri - MapScan camhunt tarafindan yazildi",
            $"# {DateTime.Now:dd.MM.yyyy HH:mm}  {list.Count} adres",
            "# Surece ozel: oyun yeniden baslarsa camhunt'i tekrar calistir.",
            "",
        };
        lines.AddRange(list.Select(a => $"0x{a:X}"));
        System.IO.File.WriteAllLines(FocusFile, lines);
    }

    internal static int Run(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "show";

        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        ulong moduleBase;
        try { moduleBase = (ulong)game.MainModule!.BaseAddress.ToInt64(); }
        catch (Exception ex) { Console.Error.WriteLine("modul alinamadi: " + ex.Message); return 1; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            // Once "MapScan pin" ile sabitlenen adres, sonra sabit zincir. Zincirler
            // artik guvenilir degil; sabitleme tek isleyen yol.
            PlayerChain.Load();
            var p0 = PlayerFinder.ReadAt(handle, PlayerChain.PinnedAddress);
            p0 ??= PlayerChain.ReadPosition(handle, PlayerChain.Resolve(handle, moduleBase));

            if (p0 is not { } p)
            {
                Console.Error.WriteLine("Oyuncu konumu okunamadi. Once:  MapScan pin");
                return 1;
            }

            Console.WriteLine($"oyuncu ({p.X:F1}, {p.Y:F1})");

            return mode switch
            {
                "snap" => Snap(handle, p),
                "panned" => Filter(handle, p, panned: true),
                "centred" => Filter(handle, p, panned: false),
                _ => Show(handle, p),
            };
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>Oyuncunun etrafindaki butun (x, y) float ciftlerini toplar.</summary>
    private static int Snap(IntPtr handle, (float X, float Y, float Z) p)
    {
        var sw = Stopwatch.StartNew();
        var found = new List<ulong>();
        var buf = new byte[ReadBlock];
        var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();
        ulong address = 0x10000;

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
                for (long off = 0; off < size; off += ReadBlock - 8)
                {
                    var want = (int)Math.Min(ReadBlock, size - off);
                    if (want < 8) { break; }
                    if (!Native.ReadProcessMemory(handle, (IntPtr)(regionBase + (ulong)off), buf,
                            (IntPtr)want, out var got) || (long)got < 8)
                    {
                        continue;
                    }

                    var n = (long)got;
                    for (long i = 0; i + 8 <= n; i += 4)
                    {
                        // Kosul POZITIF yonde yazilmali: "> NearPlayer ise atla" dedigimizde
                        // NaN karsilastirmalari false dondugu icin butun bozuk float'lar
                        // testi geciyordu - 120 birimlik pencere 400 birimlikle ayni sayida
                        // aday veriyordu, 12.6 milyon.
                        var x = BitConverter.ToSingle(buf, (int)i);
                        if (!(Math.Abs(x - p.X) <= NearPlayer)) { continue; }

                        var y = BitConverter.ToSingle(buf, (int)i + 4);
                        if (!(Math.Abs(y - p.Y) <= NearPlayer)) { continue; }

                        found.Add(regionBase + (ulong)off + (ulong)i);
                    }
                }
            }

            address += (ulong)size;
        }

        sw.Stop();
        Save(found);
        Console.WriteLine($"oyuncuya {NearPlayer:F0} birimden yakin {found.Count} cift, " +
                          $"{sw.Elapsed.TotalSeconds:F0} sn");
        Console.WriteLine();
        Console.WriteLine("Simdi haritayi KAYDIR, sonra:  MapScan camhunt panned");
        return 0;
    }

    /// <summary>
    ///     panned=true: oyuncudan uzaklasanlari tut. panned=false: geri donenleri tut.
    /// </summary>
    private static int Filter(IntPtr handle, (float X, float Y, float Z) p, bool panned)
    {
        var list = Load();
        if (list.Count == 0) { Console.Error.WriteLine("Once 'MapScan camhunt snap' calistir."); return 1; }

        // Adresleri tek tek okumak olmaz: milyonlarca aday var ve her biri ayri bir
        // ReadProcessMemory cagrisi demek. Bloklar halinde okuyup, o blogun icine
        // dusen adaylari tampondan degerlendiriyoruz.
        list.Sort();
        var kept = new List<ulong>();
        var buf = new byte[ReadBlock];
        var index = 0;

        while (index < list.Count)
        {
            var blockStart = list[index] & ~(ulong)0xFFF;
            var blockEnd = blockStart + ReadBlock;

            if (!Native.ReadProcessMemory(handle, (IntPtr)blockStart, buf, (IntPtr)ReadBlock, out var got) ||
                (long)got < 8)
            {
                // Blok okunamadi: bu bloga dusen adaylari atla.
                while (index < list.Count && list[index] < blockEnd) { index++; }
                continue;
            }

            var n = (long)got;
            while (index < list.Count && list[index] < blockEnd)
            {
                var a = list[index];
                var off = (long)(a - blockStart);
                index++;

                if (off < 0 || off + 8 > n) { continue; }

                var vx = BitConverter.ToSingle(buf, (int)off);
                var vy = BitConverter.ToSingle(buf, (int)off + 4);
                if (!float.IsFinite(vx) || !float.IsFinite(vy)) { continue; }

                var away = Math.Sqrt(Math.Pow(vx - p.X, 2) + Math.Pow(vy - p.Y, 2));
                if (panned ? away > MinTravel : away < NearPlayer) { kept.Add(a); }
            }
        }

        Save(kept);
        Console.WriteLine($"{list.Count} -> {kept.Count} aday " +
                          (panned ? "(oyuncudan uzaklasti)" : "(oyuncuya geri dondu)"));
        Console.WriteLine();

        if (panned)
        {
            Console.WriteLine("Simdi haritayi ORTALA, sonra:  MapScan camhunt centred");
            return 0;
        }

        // Kaydir/ortala sinavini gecenler gercek odak kopyalari. MapView'in
        // okuyabilmesi icin diske yaziyoruz - tahminle bulmaya calismak ise
        // yaramadi, bu sinav yaradi.
        SaveFocus(kept);
        Console.WriteLine($"{kept.Count} adres kaydedildi: {FocusFile}");
        Console.WriteLine("MapView'de \"Haritayi bul\"a basmak yeterli.");
        return 0;
    }

    private static int Show(IntPtr handle, (float X, float Y, float Z) p)
    {
        var list = Load();
        Console.WriteLine($"{list.Count} aday");
        foreach (var a in list.Take(40))
        {
            if (ReadPair(handle, a) is not { } v) { continue; }
            var away = Math.Sqrt(Math.Pow(v.X - p.X, 2) + Math.Pow(v.Y - p.Y, 2));
            Console.WriteLine($"  {a:X}   ({v.X,10:F1}, {v.Y,10:F1})   oyuncudan {away,8:F1}");
        }

        return 0;
    }

    // ------------------------------------------------------- calisma zamani kullanimi

    /// <summary>
    ///     Kameranin odak noktasinin kopyalarini bulur: harita ortaliyken oyuncunun
    ///     konumunun AYNISINI tutan float ciftleri.
    ///
    ///     Oyun buyuk haritayi oyuncuya degil bu odaga gore ciziyor - yon tuslariyla
    ///     kaydirmak ayri bir "kaydirma" alanina yazmiyor, odagi kaydiriyor. Olcumde
    ///     on bir kopya cikti; hepsini okuyup ortancasini aliyoruz ki aralarina karisan
    ///     yanlis bir aday sonucu bozmasin.
    /// </summary>
    internal static List<ulong> FindFocusCandidates(
        IntPtr handle, (float X, float Y, float Z) player, ulong exclude, int max = 400)
    {
        var found = new List<ulong>();
        var buf = new byte[ReadBlock];
        var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();
        ulong address = 0x10000;

        while (address < 0x7FFFFFFF0000UL && found.Count < max)
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
                for (long off = 0; off < size && found.Count < max; off += ReadBlock - 8)
                {
                    var want = (int)Math.Min(ReadBlock, size - off);
                    if (want < 8) { break; }
                    if (!Native.ReadProcessMemory(handle, (IntPtr)(regionBase + (ulong)off), buf,
                            (IntPtr)want, out var got) || (long)got < 8)
                    {
                        continue;
                    }

                    var n = (long)got;
                    for (long i = 0; i + 8 <= n; i += 4)
                    {
                        var x = BitConverter.ToSingle(buf, (int)i);
                        if (!(Math.Abs(x - player.X) <= 0.5f)) { continue; }

                        var y = BitConverter.ToSingle(buf, (int)i + 4);
                        if (!(Math.Abs(y - player.Y) <= 0.5f)) { continue; }

                        var at = regionBase + (ulong)off + (ulong)i;
                        if (at == exclude) { continue; }
                        found.Add(at);
                        if (found.Count >= max) { break; }
                    }
                }
            }

            address += (ulong)size;
        }

        return found;
    }

    /// <summary>
    ///     Haritanin kaydirma miktari, dunya birimi cinsinden: odak eksi oyuncu.
    ///     Harita ortaliyken (0, 0). Adaylarin ortancasi alindigi icin tek bir
    ///     bozuk aday sonucu etkilemiyor.
    /// </summary>
    internal static (float X, float Y) PanOffset(
        IntPtr handle, List<ulong> candidates, (float X, float Y) player)
    {
        if (candidates.Count == 0) { return (0f, 0f); }

        // Ortanca ise yaramadi: adaylarin cogu oyuncunun konumunun hic kipirdamayan
        // kopyalari ve onlar ortancayi sifirda tutuyor. Onun yerine BIRLIKTE hareket
        // eden en buyuk kumeyi ariyoruz - gercek odak kopyalari ayni degeri tasiyor.
        var offsets = new List<(float X, float Y)>(candidates.Count);
        foreach (var a in candidates)
        {
            if (ReadPair(handle, a) is not { } v) { continue; }

            // Odagin KENDISI makul bir dunya koordinati olmali. Sabitleme aninda
            // oyuncunun konumuna esit olan bazi adresler gecici tamponmus ve sonradan
            // (0, 0) okumaya basladi; onlar "kume" olusturup kaydirmayi tam olarak
            // eksi oyuncu konumu gosteriyordu, harita da haritanin disina uctu.
            if (!(v.X > 400f) || !(v.Y > 400f) || v.X > 500000f || v.Y > 500000f) { continue; }

            var ox = v.X - player.X;
            var oy = v.Y - player.Y;

            // Kaydirma alanin boyutundan buyuk olamaz.
            if (Math.Abs(ox) > 20000f || Math.Abs(oy) > 20000f) { continue; }

            offsets.Add((ox, oy));
        }

        if (offsets.Count == 0) { return (0f, 0f); }

        var groups = offsets
            .GroupBy(o => ((int)Math.Round(o.X / 5f), (int)Math.Round(o.Y / 5f)))
            .Where(g => g.Count() >= 3)
            .Select(g => (
                Count: g.Count(),
                X: g.Average(o => o.X),
                Y: g.Average(o => o.Y),
                Size: Math.Sqrt(g.Average(o => (double)o.X) * g.Average(o => (double)o.X) +
                                g.Average(o => (double)o.Y) * g.Average(o => (double)o.Y))))
            .OrderByDescending(g => g.Size)
            .ToList();

        return groups.Count == 0 ? (0f, 0f) : (groups[0].X, groups[0].Y);
    }

    private static (float X, float Y)? ReadPair(IntPtr handle, ulong address)
    {
        var eight = new byte[8];
        if (!Native.ReadProcessMemory(handle, (IntPtr)address, eight, (IntPtr)8, out var got) ||
            (long)got != 8)
        {
            return null;
        }

        var x = BitConverter.ToSingle(eight, 0);
        var y = BitConverter.ToSingle(eight, 4);
        return float.IsFinite(x) && float.IsFinite(y) ? (x, y) : null;
    }

    private static void Save(List<ulong> list)
    {
        using var w = new BinaryWriter(System.IO.File.Create(File_));
        w.Write(list.Count);
        foreach (var a in list) { w.Write(a); }
    }

    private static List<ulong> Load()
    {
        if (!System.IO.File.Exists(File_)) { return new List<ulong>(); }
        using var r = new BinaryReader(System.IO.File.OpenRead(File_));
        var n = r.ReadInt32();
        var list = new List<ulong>(n);
        for (var i = 0; i < n; i++) { list.Add(r.ReadUInt64()); }
        return list;
    }
}
