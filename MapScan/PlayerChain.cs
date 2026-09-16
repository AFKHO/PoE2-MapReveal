using System.Globalization;

namespace Poe2Map;

/// <summary>
///     Oyuncu nesnesine giden sabit isaretci zinciri - TEK kaynak.
///
///     Bu projedeki tek elle bulunmus sabit bu. Zemin bulucu tamamen yapisal
///     imzalarla calistigi icin patch'lerden sag cikiyor, ama oyuncunun konumu
///     icin bir yerden baslamak gerekiyor ve o baslangic global bir degisken.
///     Global degiskenler her derlemede yer degistirir.
///
///     Onemli: zincir KODA GOMULU DEGIL. Yan taraftaki ayar dosyasindan okunuyor,
///     boylece patch gunu yeniden derleme gerekmiyor:
///
///         %LOCALAPPDATA%\poe2-map\player-chain.txt
///
///     Dosyada oyunun exe boyutu da yaziyor. Boyut degismisse zincirin bayat
///     oldugunu kendimiz soyluyoruz, sen fark etmeye calismak zorunda kalmiyorsun.
///
///     Yenisini bulmak: MapScan poshunt      (bulunca dosyayi kendi yaziyor)
/// </summary>
internal static class PlayerChain
{
    /// <summary>modul + StaticRva -> +Hops[0] -> +Hops[1] ... = oyuncu nesnesi.</summary>
    internal static ulong StaticRva { get; set; } = 0x4588798;

    internal static ulong[] Hops { get; set; } = { 0x1E0, 0x3F0 };

    /// <summary>
    ///     ALAN yapisindan oyuncu nesnesine giden ofset yolu.
    ///
    ///     Modul tabanli zincir oyun yeniden baslatilinca kirildi (12.09.2026: exe ayni,
    ///     surec yeni, zincir 0 dondu) - cunku gecici bir global uzerinden geciyordu.
    ///     Alan yapisini ise her acilista yapisal imzayla buluyoruz, o yuzden ona gore
    ///     kaydedilen yol hem yeniden baslatmaya hem patch'e dayaniyor.
    ///
    ///     Bos degilse bu yol tercih ediliyor, modul zinciri sadece yedek.
    /// </summary>
    internal static ulong[] AreaHops { get; set; } = Array.Empty<ulong>();

    /// <summary>Nesne icinde konumun yeri: X, Y, yukseklik (3 float).</summary>
    internal static ulong PositionOffset { get; set; } = 0x88;

    /// <summary>Zincir bulundugunda oyunun exe boyutu. Degistiyse zincir bayat.</summary>
    internal static long RecordedExeSize { get; set; }

    /// <summary>
    ///     "MapScan pin" ile sabitlenen konum adresi. Surece ozel oldugu icin oyun
    ///     yeniden baslayinca gecersiz - ama ayri komutlarin (camhunt gibi) oyuncuyu
    ///     tekrar aramak zorunda kalmamasi icin kaydediyoruz. Kullanmadan once
    ///     okudugun degerin makul olup olmadigina bakmak yeterli.
    /// </summary>
    internal static ulong PinnedAddress { get; set; }

    /// <summary>
    ///     Sabitlenen adresin ait oldugu oyun sureci. Adres surece ozel; ayarsiz exe
    ///     ayni surece yeniden baglandiginda yeniden yurumeyi atlamak icin bunu kontrol ediyor.
    /// </summary>
    internal static int PinnedPid { get; set; }

    /// <summary>Bir dunya biriminin kac hucreye denk geldigi: 250 / 23.</summary>
    internal const float WorldPerCell = 250f / 23f;

    // ---------------------------------------------------------------- ayar dosyasi

    internal static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "poe2-map", "player-chain.txt");

    private static bool loaded;

    /// <summary>Ayar dosyasi varsa okur. Ilk kullanimda kendiliginden cagriliyor.</summary>
    internal static void Load()
    {
        if (loaded) { return; }
        loaded = true;

        try
        {
            if (!File.Exists(ConfigPath)) { return; }

            foreach (var raw in File.ReadAllLines(ConfigPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) { continue; }

                var split = line.IndexOf('=');
                if (split <= 0) { continue; }

                var key = line[..split].Trim().ToLowerInvariant();
                var value = line[(split + 1)..].Trim();

                switch (key)
                {
                    case "rva":
                        StaticRva = ParseHex(value);
                        break;
                    case "hops":
                        Hops = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(ParseHex).ToArray();
                        break;
                    case "areahops":
                        AreaHops = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(ParseHex).ToArray();
                        break;
                    case "position":
                        PositionOffset = ParseHex(value);
                        break;
                    case "pinned":
                        PinnedAddress = ParseHex(value);
                        break;
                    case "pinnedpid":
                        PinnedPid = int.TryParse(value, out var pid) ? pid : 0;
                        break;
                    case "exesize":
                        RecordedExeSize = long.TryParse(value, out var s) ? s : 0;
                        break;
                }
            }
        }
        catch
        {
            // Bozuk dosya yuzunden uygulama acilmamasin; gomulu varsayilanla devam.
        }
    }

    /// <summary>Bulunan zinciri diske yazar; bir daha aranmasi gerekmez.</summary>
    internal static void Save(long exeSize, string note = "")
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);

        var hops = string.Join(", ", Hops.Select(h => $"0x{h:X}"));
        var lines = new List<string>
        {
            "# PoE2 oyuncu konum zinciri - MapScan poshunt tarafindan yazildi",
            $"# {DateTime.Now:dd.MM.yyyy HH:mm}" + (note.Length > 0 ? "  " + note : ""),
            "#",
            "# modul + rva -> +hops[0] -> +hops[1] ... -> +position = X, Y, yukseklik",
            "",
            $"rva      = 0x{StaticRva:X}",
            $"hops     = {hops}",
            $"areahops = {string.Join(", ", AreaHops.Select(h => $"0x{h:X}"))}",
            $"position = 0x{PositionOffset:X}",
            $"exesize  = {exeSize}",
            $"pinned   = 0x{PinnedAddress:X}",
            $"pinnedpid = {PinnedPid}",
        };

        File.WriteAllLines(ConfigPath, lines);
    }

    private static ulong ParseHex(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { text = text[2..]; }
        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>
    ///     Zincirin bayat olup olmadigini soyler: exe boyutu kayitliyken degismisse
    ///     patch gelmis demektir. Sorun yoksa null doner.
    /// </summary>
    internal static string? StaleWarning(long currentExeSize)
    {
        Load();
        if (RecordedExeSize == 0 || currentExeSize == 0 || RecordedExeSize == currentExeSize) { return null; }

        return $"Oyun guncellenmis (exe {RecordedExeSize} -> {currentExeSize} bayt). " +
               "Oyuncu zinciri bayat: MapScan poshunt";
    }

    // ---------------------------------------------------------------- cozme

    /// <summary>Zinciri cozer. Basarisizsa 0.</summary>
    internal static ulong Resolve(IntPtr handle, ulong moduleBase)
    {
        Load();
        return Resolve(handle, moduleBase, StaticRva, Hops);
    }

    /// <summary>
    ///     ALAN yapisindan oyuncu nesnesine cozer. Yol kayitli degilse 0.
    ///     Alan yapisi her acilista yapisal olarak bulundugu icin bu yol
    ///     oyun yeniden baslatilsa da patch gelse de gecerli kaliyor.
    /// </summary>
    internal static ulong ResolveFromArea(IntPtr handle, ulong areaBase)
    {
        Load();
        if (AreaHops.Length == 0 || areaBase == 0) { return 0; }

        var cursor = areaBase;
        for (var i = 0; i < AreaHops.Length; i++)
        {
            var value = ReadPointer(handle, cursor + AreaHops[i]);
            if (!LooksLikePointer(value)) { return 0; }
            cursor = value;
        }

        return cursor;
    }

    internal static ulong Resolve(IntPtr handle, ulong moduleBase, ulong rva, ulong[] hops)
    {
        if (moduleBase == 0) { return 0; }

        var cursor = moduleBase + rva;
        foreach (var hop in hops)
        {
            var value = ReadPointer(handle, cursor);
            if (value == 0) { return 0; }
            cursor = value + hop;
        }

        return ReadPointer(handle, cursor);
    }

    /// <summary>
    ///     Alan tabanli yolu adim adim basar. Yol kirildiginda hangi halkada
    ///     koptugunu gormek icin - "cozulemedi" demek tek basina bir sey ogretmiyor.
    /// </summary>
    internal static void TraceFromArea(IntPtr handle, ulong areaBase, Action<string> log)
    {
        Load();
        if (AreaHops.Length == 0) { log("  alan tabanli yol kayitli degil"); return; }

        var cursor = areaBase;
        log($"  alan {areaBase:X}");
        for (var i = 0; i < AreaHops.Length; i++)
        {
            var at = cursor + AreaHops[i];
            var value = ReadPointer(handle, at);
            var ok = LooksLikePointer(value);
            log($"    +0x{AreaHops[i]:X} @ {at:X} -> {value:X}   " +
                (ok ? "isaretci" : "ISARETCI DEGIL - burada koptu"));
            if (!ok) { return; }
            cursor = value;
        }

        var pos = ReadPosition(handle, cursor);
        log($"    nesne {cursor:X}  +0x{PositionOffset:X} -> " +
            (pos is { } p ? $"({p.X:F0}, {p.Y:F0}, {p.Z:F0})" : "konum makul degil"));
    }

    internal static ulong ReadPointer(IntPtr handle, ulong address)
    {
        var eight = new byte[8];
        if (!Native.ReadProcessMemory(handle, (IntPtr)address, eight, (IntPtr)8, out var got) || (long)got != 8)
        {
            return 0;
        }

        return BitConverter.ToUInt64(eight, 0);
    }

    /// <summary>Bir nesnenin konumunu okur. Makul degil ya da okunamazsa null.</summary>
    internal static (float X, float Y, float Z)? ReadPosition(IntPtr handle, ulong objectBase)
    {
        Load();
        if (objectBase < 0x10000) { return null; }

        var twelve = new byte[12];
        if (!Native.ReadProcessMemory(handle, (IntPtr)(objectBase + PositionOffset), twelve,
                (IntPtr)12, out var got) || (long)got != 12)
        {
            return null;
        }

        var x = BitConverter.ToSingle(twelve, 0);
        var y = BitConverter.ToSingle(twelve, 4);
        var z = BitConverter.ToSingle(twelve, 8);

        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) { return null; }
        if (x <= 0 || y <= 0 || x > 500000 || y > 500000) { return null; }

        // Yukseklik de sinirli olmali. Aksi halde 4.8e32 gibi degerler "gecerli konum"
        // sayiliyor; oyuncu avinda adaylarin yarisi boyle sacma yuksekliklerle geldi.
        if (Math.Abs(z) > 100000) { return null; }

        return (x, y, z);
    }

    /// <summary>Bir isaretci degeri yigin adresi olabilir mi.</summary>
    internal static bool LooksLikePointer(ulong value) =>
        value >= 0x10000 && value <= 0x7FFFFFFFFFFF && (value & 7) == 0;
}
