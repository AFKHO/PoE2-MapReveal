using System.Globalization;

namespace Poe2Map;

/// <summary>
///     Haritanin ayarlari: cizgi rengi/kalinligi ve nadirlik basina canavar simgeleri.
///     Diskte duruyor ki exe her acildiginda yeniden secmek gerekmesin.
///
///         %LOCALAPPDATA%\poe2-map\mapoverlay-ayar.txt
///
///     Dosya elle de duzenlenebilir; bozuk satir varsayilani bozmuyor.
/// </summary>
internal sealed class OverlaySettings
{
    internal Color LineColor { get; set; } = Color.FromArgb(120, 170, 190);

    internal int Thickness { get; set; } = 2;

    /// <summary>Nadirlik basina simge gorunur mu. Sira: <see cref="OverlayForm.RarityNames"/>.</summary>
    internal bool[] MonsterVisible { get; } = { true, true, true, true };

    /// <summary>Nadirlik basina simge capi (piksel). Sira: <see cref="OverlayForm.RarityNames"/>.</summary>
    internal int[] MonsterSize { get; } =
    {
        OverlayForm.DefaultIconSize, OverlayForm.DefaultIconSize,
        OverlayForm.DefaultIconSize, OverlayForm.DefaultIconSize,
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "poe2-map", "mapoverlay-ayar.txt");

    internal static OverlaySettings Load()
    {
        var settings = new OverlaySettings();
        try
        {
            if (!File.Exists(FilePath)) { return settings; }

            foreach (var raw in File.ReadAllLines(FilePath))
            {
                var split = raw.IndexOf('=');
                if (split <= 0) { continue; }

                var key = raw[..split].Trim().ToLowerInvariant();
                var value = raw[(split + 1)..].Trim();

                switch (key)
                {
                    case "renk" when int.TryParse(value.TrimStart('#'), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var rgb):
                        settings.LineColor = Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                        break;
                    case "kalinlik" when int.TryParse(value, out var t):
                        settings.Thickness = Math.Clamp(t, 1, OverlayForm.MaxThickness);
                        break;
                    default:
                        ReadMonsterLine(settings, key, value);
                        break;
                }
            }
        }
        catch
        {
            // Bozuk dosya varsayilanlarla acilmayi engellemesin.
        }

        return settings;
    }

    /// <summary>
    ///     "normal = acik 8" satiri: nadirlik adi = gorunurluk + simge capi. Iki parca
    ///     da istege bagli ve sirasi onemsiz; sayi capi, yazi gorunurlugu belirliyor.
    /// </summary>
    private static void ReadMonsterLine(OverlaySettings settings, string key, string value)
    {
        var rarity = Array.FindIndex(
            OverlayForm.RarityNames,
            n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase));
        if (rarity < 0) { return; }

        foreach (var part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var size))
            {
                settings.MonsterSize[rarity] = Math.Clamp(size, OverlayForm.MinIconSize, OverlayForm.MaxIconSize);
            }
            else
            {
                settings.MonsterVisible[rarity] = !part.StartsWith("kapali", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var lines = new List<string>
            {
                "# PoE2 Harita ayari",
                $"renk     = #{this.LineColor.R:X2}{this.LineColor.G:X2}{this.LineColor.B:X2}",
                $"kalinlik = {this.Thickness}",
                "",
                "# Canavar simgeleri: acik/kapali ve cap (piksel)",
            };

            for (var i = 0; i < OverlayForm.RarityNames.Length; i++)
            {
                var name = OverlayForm.RarityNames[i].ToLowerInvariant();
                lines.Add($"{name,-8} = {(this.MonsterVisible[i] ? "acik" : "kapali")} {this.MonsterSize[i]}");
            }

            File.WriteAllLines(FilePath, lines);
        }
        catch
        {
            // Kaydedilemezse bu oturumda yine gecerli; bir sonraki acilista varsayilana doner.
        }
    }
}
