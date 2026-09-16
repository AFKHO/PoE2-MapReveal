using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Izgaranın nerede başladığını, oyuncunun gezdiği yolu kullanarak bulur.
///
///     Tek bir konum örneği yetmiyor: ızgarayı satır satır kaydırdıkça oyuncu her
///     seferinde farklı ama yine de geçerli görünen bir hücreye düşüyor. Oysa gerçek
///     hizalama, gezilen BÜTÜN noktaları aynı anda açıklayan tek hizalamadır - yanlış
///     olan er ya da geç oyuncuyu kayanın içinde gösterir.
///
///     Bu yüzden: sen dolaşırken konum örnekleri toplanır, sonra bütün aday tamponlar
///     ve olası satır kaymaları taranıp her örneği zeminde tutan kombinasyon aranır.
/// </summary>
internal sealed record Alignment(ulong GridStart, int Stride, int Rows, int Hits, int Total, int Structured)
{
    internal double Ratio => this.Total == 0 ? 0 : (double)this.Hits / this.Total;

    /// <summary>
    ///     Örneklerin kaçının yakınında engel var. Gerçek bir haritada oyuncu duvarların
    ///     arasında yürür, yani çevresinde 0 bulunur. "Her yeri yürünebilir" bloklar
    ///     zemin testini bedavaya geçtiği için asıl ayırt edici bu.
    /// </summary>
    internal double StructureRatio => this.Total == 0 ? 0 : (double)this.Structured / this.Total;

    public override string ToString() =>
        $"{this.GridStart:X}  {this.Stride}x{this.Rows}  zeminde {this.Hits}/{this.Total}, " +
        $"cevresinde duvar {this.Structured}/{this.Total} (%{this.StructureRatio * 100:F0})";
}

internal static class Calibrator
{
    private const long MaxRegionRead = 64L * 1024 * 1024;

    internal static List<PlayerPos> Sample(IntPtr handle, ulong moduleBase, int count, int intervalMs,
        Action<string>? log = null)
    {
        var list = new List<PlayerPos>();
        for (var i = 0; i < count; i++)
        {
            var p = Player.TryRead(handle, moduleBase);
            if (p is { } pos)
            {
                // Aynı yerde duruyorsa yeni bilgi yok; sadece farklı noktaları topluyoruz.
                if (list.Count == 0 || Math.Abs(list[^1].X - pos.X) > 20 || Math.Abs(list[^1].Y - pos.Y) > 20)
                {
                    list.Add(pos);
                }
            }

            log?.Invoke($"  ornek {i + 1}/{count}  ({(p is { } q ? $"{q.X:F0}, {q.Y:F0}" : "okunamadi")})   " +
                        $"farkli nokta: {list.Count}");
            if (i < count - 1) { Thread.Sleep(intervalMs); }
        }

        return list;
    }

    /// <summary>
    ///     Adayın içinde bulunduğu gerçek bellek bölgesini bulur. VirtualQueryEx sorulan
    ///     adresi taban kabul ettiği için, bölge sınırını görmek adına tahsis başından
    ///     ileri yürümek gerekiyor.
    /// </summary>
    private static (ulong Base, long Size) ContainingRegion(IntPtr handle, ulong address)
    {
        var mbiSize = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryBasicInformation>();
        if (Native.VirtualQueryEx(handle, (IntPtr)address, out var mbi, mbiSize) == IntPtr.Zero)
        {
            return (address, 0);
        }

        var allocBase = (ulong)mbi.AllocationBase.ToInt64();
        if (allocBase == 0) { return (address, (long)mbi.RegionSize); }

        var cursor = allocBase;
        while (Native.VirtualQueryEx(handle, (IntPtr)cursor, out var m2, mbiSize) != IntPtr.Zero)
        {
            if ((ulong)m2.AllocationBase.ToInt64() != allocBase) { break; }
            var size = (long)m2.RegionSize;
            if (size <= 0) { break; }

            var end = cursor + (ulong)size;
            if (m2.State == Native.MemCommit && address >= cursor && address < end)
            {
                return (cursor, size);
            }

            cursor = end;
        }

        return (address, (long)mbi.RegionSize);
    }

    /// <summary>
    ///     Harita bellekte tek parça değil, aynı genişlikte YATAY BANTLAR hâlinde duruyor.
    ///     Ölçüldü: bir alanın parçalarının hepsi aynı satır uzunluğunu veriyor (Shrike'ta 4
    ///     parça 3588, Holten'de 4 parça 2278, Howling Caves'te 8 parça 2140).
    ///
    ///     Bu yüzden doğru genişlik, "aynı adımı paylaşan parçaların toplam boyutu" en büyük
    ///     olan genişliktir. Aramayı bununla sınırlamak hayati: kalibrasyon ilk denemede
    ///     251 bölge x 30 bin kaydırma arasında boğulmuştu ve 26 kısıt tesadüfen sağlanıyordu.
    ///     Tek bir genişlik ve tek bir bölgeye inince o tesadüf ihtimali kalmıyor.
    /// </summary>
    internal static List<(ulong Base, long Size, int Stride)> DominantBands(
        IReadOnlyList<(ulong Base, long Size, int Stride)> survey, Action<string>? log = null)
    {
        var groups = survey
            .Where(s => s.Stride >= 256)
            .GroupBy(s => s.Stride)
            .Select(g => (Stride: g.Key, Total: g.Sum(x => x.Size), Items: g.ToList()))
            .OrderByDescending(g => g.Total)
            .Take(3)
            .ToList();

        foreach (var g in groups)
        {
            log?.Invoke($"  adim {g.Stride,5}: {g.Items.Count} parca, toplam {g.Total / 1024} KB");
        }

        return groups.SelectMany(g => g.Items).ToList();
    }

    internal static List<Alignment> Search(IntPtr handle,
        IReadOnlyList<(ulong Base, long Size, int Stride)> candidates,
        IReadOnlyList<PlayerPos> samples, Action<string>? log = null)
    {
        var results = new List<Alignment>();
        if (samples.Count == 0) { return results; }

        var seen = new HashSet<(ulong, int)>();

        foreach (var cand in candidates)
        {
            var (regionBase, regionSize) = ContainingRegion(handle, cand.Base);
            if (regionSize <= 0) { continue; }
            if (!seen.Add((regionBase, cand.Stride))) { continue; }

            var readSize = (int)Math.Min(regionSize, MaxRegionRead);
            var buf = new byte[readSize];
            if (!Native.ReadProcessMemory(handle, (IntPtr)regionBase, buf, (IntPtr)readSize, out var got) ||
                (long)got < 4096)
            {
                continue;
            }

            var length = (long)got;
            var stride = cand.Stride;
            var rowBytes = stride / 2;
            if (rowBytes <= 0) { continue; }

            var maxRowOffset = (int)(length / rowBytes);
            var best = new Alignment(0, stride, 0, -1, samples.Count, 0);

            for (var rowOffset = 0; rowOffset < maxRowOffset; rowOffset++)
            {
                var startByte = (long)rowOffset * rowBytes;
                var hits = 0;
                var usable = 0;
                var structured = 0;

                foreach (var s in samples)
                {
                    var cx = s.CellX;
                    var cy = s.CellY;
                    if (cx < 0 || cx >= stride || cy < 0) { continue; }

                    var index = (long)cy * stride + cx;
                    var byteIndex = startByte + index / 2;
                    if (byteIndex < 0 || byteIndex >= length) { continue; }

                    usable++;
                    var b = buf[byteIndex];
                    var value = index % 2 == 0 ? b & 0x0F : b >> 4;
                    if (value == 0) { continue; }

                    hits++;
                    if (HasWallNearby(buf, length, startByte, stride, cx, cy)) { structured++; }
                }

                if (usable != samples.Count || hits != samples.Count) { continue; }

                // Noktalar tek başına zayıf kanıt: büyük bir bölgede binlerce kaydırma
                // 26 kısıtı tesadüfen sağlayabiliyor. Oysa yürürken izlenen YOL da
                // baştan sona yürünebilir olmak zorunda - bu, kısıt sayısını yüzlerce
                // katına çıkarıyor ve tesadüfi eşleşmeleri eliyor.
                if (!PathIsWalkable(buf, length, startByte, stride, samples)) { continue; }

                if (structured > best.Structured)
                {
                    var rows = (int)((length - startByte) * 2 / stride);
                    best = new Alignment(regionBase + (ulong)startByte, stride, rows, hits, samples.Count, structured);
                }
            }

            if (best.Hits > 0)
            {
                log?.Invoke($"  bolge {regionBase:X} ({regionSize / 1024 / 1024} MB, adim {stride}) -> {best}");
                results.Add(best);
            }
        }

        return results
            .OrderByDescending(r => r.StructureRatio)
            .ThenByDescending(r => r.Ratio)
            .ToList();
    }

    /// <summary>
    ///     Ardışık örnekler arasındaki doğru parçasının üstündeki her hücrenin yürünebilir
    ///     olmasını ister. Örnekler arası mesafe kısa olduğu için gerçek yol ile düz çizgi
    ///     birbirine yakın; yine de küçük bir tolerans bırakıyoruz (yolun %10'u engel
    ///     çıkabilir - köşe kesme, örnekleme aralığı).
    /// </summary>
    private static bool PathIsWalkable(byte[] buf, long length, long startByte, int stride,
        IReadOnlyList<PlayerPos> samples)
    {
        var total = 0;
        var blocked = 0;

        for (var i = 1; i < samples.Count; i++)
        {
            var ax = samples[i - 1].CellX;
            var ay = samples[i - 1].CellY;
            var bx = samples[i].CellX;
            var by = samples[i].CellY;

            var steps = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
            if (steps == 0) { continue; }

            // Çok uzun sıçramalar (ışınlanma, kaçırılan örnek) yol sayılmaz.
            if (steps > 400) { continue; }

            for (var s = 0; s <= steps; s++)
            {
                var x = ax + (bx - ax) * s / steps;
                var y = ay + (by - ay) * s / steps;
                if (x < 0 || x >= stride || y < 0) { return false; }

                var index = (long)y * stride + x;
                var byteIndex = startByte + index / 2;
                if (byteIndex < 0 || byteIndex >= length) { return false; }

                total++;
                var b = buf[byteIndex];
                var value = index % 2 == 0 ? b & 0x0F : b >> 4;
                if (value == 0) { blocked++; }
            }
        }

        return total > 100 && (double)blocked / total <= 0.10;
    }

    /// <summary>
    ///     Verilen hücrenin çevresinde (yarıçap 30 hücre, seyrek örnekleme) engel var mı.
    /// </summary>
    private static bool HasWallNearby(byte[] buf, long length, long startByte, int stride, int cx, int cy)
    {
        for (var dy = -30; dy <= 30; dy += 5)
        {
            for (var dx = -30; dx <= 30; dx += 5)
            {
                var x = cx + dx;
                var y = cy + dy;
                if (x < 0 || x >= stride || y < 0) { continue; }

                var index = (long)y * stride + x;
                var byteIndex = startByte + index / 2;
                if (byteIndex < 0 || byteIndex >= length) { continue; }

                var b = buf[byteIndex];
                var value = index % 2 == 0 ? b & 0x0F : b >> 4;
                if (value == 0) { return true; }
            }
        }

        return false;
    }
}
