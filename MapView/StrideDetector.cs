namespace Poe2Map;

/// <summary>
///     Bir hücre dizisinin satır uzunluğunu ölçer.
///
///     Fikir: iki boyutlu bir ızgarada, diziyi tam bir satır kadar kaydırdığında her hücre
///     bir üstteki komşusunun üstüne gelir - ve komşu hücreler birbirine benzer. "Kaydır ve
///     farkı ölç" işlemi bu yüzden doğru satır uzunluğunda belirgin bir dip yapar.
///
///     Arama kaba-ince yapılıyor: önce 8'er adımla geniş tarama, sonra en iyi adayın
///     etrafında birer birer. Tek tek 6000 genişlik denemek adayları teker teker
///     dakikalara çıkarıyordu.
/// </summary>
internal static class StrideDetector
{
    private const int MinWidth = 64;
    private const int MaxWidth = 6000;
    private const int SampleCells = 1 << 20;
    private const int MaxComparisons = 20000;

    /// <returns>(satır uzunluğu, düzenlilik oranı). Bulunamazsa (0, 0).</returns>
    internal static (int Stride, double Strength) Detect(byte[] cells)
    {
        var sample = Math.Min(cells.Length, SampleCells);
        if (sample < MinWidth * 8) { return (0, 0); }

        var maxWidth = Math.Min(MaxWidth, sample / 4);
        if (maxWidth <= MinWidth) { return (0, 0); }

        // Kaba tarama
        var coarse = new List<(int Width, double Score)>();
        for (var w = MinWidth; w <= maxWidth; w += 8)
        {
            coarse.Add((w, Score(cells, sample, w)));
        }

        if (coarse.Count == 0) { return (0, 0); }

        var bestCoarse = coarse.MinBy(c => c.Score);
        var median = coarse.OrderBy(c => c.Score).ElementAt(coarse.Count / 2).Score;

        // İnce arama: kaba kazananın etrafı
        var best = bestCoarse.Score;
        var bestWidth = bestCoarse.Width;
        var lo = Math.Max(MinWidth, bestCoarse.Width - 8);
        var hi = Math.Min(maxWidth, bestCoarse.Width + 8);
        for (var w = lo; w <= hi; w++)
        {
            var s = Score(cells, sample, w);
            if (s < best) { best = s; bestWidth = w; }
        }

        var strength = best > 0 ? median / best : 0;
        return (bestWidth, strength);
    }

    private static double Score(byte[] cells, int sample, int width)
    {
        long diff = 0;
        long counted = 0;
        var n = sample - width;
        var step = Math.Max(1, n / MaxComparisons);

        for (var i = 0; i < n; i += step)
        {
            diff += Math.Abs(cells[i] - cells[i + width]);
            counted++;
        }

        return counted == 0 ? double.MaxValue : (double)diff / counted;
    }

    /// <summary>
    ///     Verinin tekdüze olup olmadığını söyler. Tek bir değerden ibaret tamponlar
    ///     (örneğin baştan sona "yürünebilir") her kaydırmada kendine benzediği için
    ///     düzenlilik ölçüsünü kandırıyor - onları burada eliyoruz.
    /// </summary>
    internal static double DominantValueFraction(byte[] cells)
    {
        var counts = new long[16];
        foreach (var c in cells)
        {
            if (c < 16) { counts[c]++; }
        }

        long max = 0;
        foreach (var c in counts) { if (c > max) { max = c; } }
        return (double)max / cells.Length;
    }
}
