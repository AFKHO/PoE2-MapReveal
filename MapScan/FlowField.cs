using System.Drawing;

namespace Poe2Map;

/// <summary>
///     Bir hedefe (cikis kapisi) olan yurume mesafesinin butun izgaraya yayilmis hali.
///
///     Neden A* degil: oyuncu surekli yuruyor. A* her yeni konum icin bastan arama
///     ister (Radar oyle yapiyor, arka planda). Mesafe haritasi hedef basina BIR kez
///     hesaplaniyor; sonra oyuncu nerede olursa olsun yol, komsulardan mesafesi en
///     kucuk olana adim adim inerek her karede ucuza cikiyor.
///
///     Maliyet: duz adim 10, capraz 14, ve duvara yakinlik cezasi. Zemin degerleri
///     1-4 duvara yakin hucreler (5 = acik alan); ceza (5 - deger) * 3. Yol boylece
///     duvara surtunmeden koridorun ortasindan gidiyor.
///
///     Izgara 2x2 kaba hucrelere indiriliyor: 12420x828'lik bir alanda hedef basina
///     41 MB yerine 10 MB, ve hesap dort kat hizli. Kaba hucre, dort ince hucresinden
///     biri bile yurunebilirse yurunebilir - dar gecitler kapanmasin diye.
///
///     Algoritma Dial'in kova kuyrugu: maliyetler kucuk tamsayi oldugu icin ikili
///     yigindan cok daha hizli, milyonlarca hucrede dogrusal.
/// </summary>
internal sealed class FlowField
{
    internal const int Factor = 2;

    private const int Unreached = int.MaxValue;
    private const int Buckets = 64;

    private static readonly (int Dx, int Dy, int Cost)[] Steps =
    {
        (1, 0, 10), (-1, 0, 10), (0, 1, 10), (0, -1, 10),
        (1, 1, 14), (1, -1, 14), (-1, 1, 14), (-1, -1, 14),
    };

    private readonly int width;
    private readonly int height;
    private readonly byte[] cost;
    private readonly int[] distance;

    private FlowField(int width, int height, byte[] cost, int[] distance)
    {
        this.width = width;
        this.height = height;
        this.cost = cost;
        this.distance = distance;
    }

    /// <summary>
    ///     Kaba maliyet izgarasi: 0 = kapali, 1..5 = en acik ince hucrenin degeri.
    ///     Kapilar (zeminde kapali gorunen ama acilan gecitler) yurunebilir sayiliyor.
    /// </summary>
    internal static byte[] BuildCostGrid(
        byte[] raw, int bytesPerRow, int gridX, int gridY, IEnumerable<(float X, float Y)> doors,
        out int width, out int height)
    {
        width = (gridX + Factor - 1) / Factor;
        height = (gridY + Factor - 1) / Factor;
        var grid = new byte[(long)width * height];

        for (var y = 0; y < gridY; y++)
        {
            var row = (long)y * bytesPerRow;
            var cy = y / Factor;
            for (var x = 0; x < gridX; x++)
            {
                var index = row + (x / 2);
                if (index >= raw.Length) { break; }

                var v = (byte)((x & 1) == 0 ? raw[index] & 0x0F : raw[index] >> 4);
                if (v == 0) { continue; }
                if (v > 5) { v = 5; }

                var c = (cy * width) + (x / Factor);
                if (v > grid[c]) { grid[c] = v; }
            }
        }

        // Radar'daki gibi: kapinin cevresindeki 5x5 ince hucre (kaba izgarada 3x3) acik.
        foreach (var (dx, dy) in doors)
        {
            var cx = (int)(dx / PlayerChain.WorldPerCell) / Factor;
            var cy = (int)(dy / PlayerChain.WorldPerCell) / Factor;
            for (var oy = -1; oy <= 1; oy++)
            {
                for (var ox = -1; ox <= 1; ox++)
                {
                    var x = cx + ox;
                    var y = cy + oy;
                    if (x < 0 || y < 0 || x >= width || y >= height) { continue; }
                    var c = (y * width) + x;
                    if (grid[c] == 0) { grid[c] = 3; }
                }
            }
        }

        return grid;
    }

    /// <summary>
    ///     Hedefe olan mesafeyi butun izgaraya yayar. Kapi cogu zaman duvarin icinde
    ///     durdugu icin hedefin cevresindeki yurunebilir hucreler de baslangic sayiliyor.
    /// </summary>
    internal static FlowField Build(byte[] cost, int width, int height, float targetX, float targetY)
    {
        var distance = new int[(long)width * height];
        Array.Fill(distance, Unreached);

        var buckets = new List<int>[Buckets];
        for (var i = 0; i < Buckets; i++) { buckets[i] = new List<int>(); }
        var pending = 0;

        var tx = (int)(targetX / PlayerChain.WorldPerCell) / Factor;
        var ty = (int)(targetY / PlayerChain.WorldPerCell) / Factor;
        const int SeedRadius = 6;

        for (var y = Math.Max(0, ty - SeedRadius); y <= Math.Min(height - 1, ty + SeedRadius); y++)
        {
            for (var x = Math.Max(0, tx - SeedRadius); x <= Math.Min(width - 1, tx + SeedRadius); x++)
            {
                var c = (y * width) + x;
                if (cost[c] == 0) { continue; }
                distance[c] = 0;
                buckets[0].Add(c);
                pending++;
            }
        }

        var current = 0;
        while (pending > 0)
        {
            var bucket = buckets[current & (Buckets - 1)];
            while (bucket.Count > 0)
            {
                var c = bucket[^1];
                bucket.RemoveAt(bucket.Count - 1);
                pending--;
                if (distance[c] != current) { continue; }

                var x = c % width;
                var y = c / width;

                foreach (var (sx, sy, stepCost) in Steps)
                {
                    var nx = x + sx;
                    var ny = y + sy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) { continue; }

                    var n = (ny * width) + nx;
                    if (cost[n] == 0) { continue; }

                    // Capraz adimda koseden kesme yok: iki yan komsu da acik olmali.
                    if (sx != 0 && sy != 0 &&
                        (cost[(y * width) + nx] == 0 || cost[(ny * width) + x] == 0))
                    {
                        continue;
                    }

                    var next = current + stepCost + ((5 - cost[n]) * 3);
                    if (next >= distance[n]) { continue; }

                    distance[n] = next;
                    buckets[next & (Buckets - 1)].Add(n);
                    pending++;
                }
            }

            current++;
        }

        return new FlowField(width, height, cost, distance);
    }

    /// <summary>
    ///     Oyuncunun dunya konumundan hedefe giden yol, INCE hucre koordinatlarinda.
    ///     Oyuncunun hucresine ulasilamiyorsa (kapi esigi, kopru) yakin cevrede
    ///     ulasilabilir en iyi hucreden basliyor. Yol yoksa bos liste.
    /// </summary>
    internal List<PointF> PathFrom(float worldX, float worldY, int maxSteps = 20000)
    {
        var path = new List<PointF>();
        var cx = (int)(worldX / PlayerChain.WorldPerCell) / Factor;
        var cy = (int)(worldY / PlayerChain.WorldPerCell) / Factor;
        if (cx < 0 || cy < 0 || cx >= this.width || cy >= this.height) { return path; }

        var c = (cy * this.width) + cx;
        if (this.distance[c] == Unreached)
        {
            c = this.NearestReached(cx, cy, 10);
            if (c < 0) { return path; }
        }

        for (var step = 0; step < maxSteps && this.distance[c] > 0; step++)
        {
            var x = c % this.width;
            var y = c / this.width;

            if ((step & 1) == 0) { path.Add(new PointF((x * Factor) + (Factor / 2f), (y * Factor) + (Factor / 2f))); }

            var best = -1;
            var bestDistance = this.distance[c];
            foreach (var (sx, sy, _) in Steps)
            {
                var nx = x + sx;
                var ny = y + sy;
                if (nx < 0 || ny < 0 || nx >= this.width || ny >= this.height) { continue; }

                var n = (ny * this.width) + nx;
                if (this.distance[n] >= bestDistance) { continue; }

                if (sx != 0 && sy != 0 &&
                    (this.cost[(y * this.width) + nx] == 0 || this.cost[(ny * this.width) + x] == 0))
                {
                    continue;
                }

                best = n;
                bestDistance = this.distance[n];
            }

            if (best < 0) { break; }
            c = best;
        }

        path.Add(new PointF(((c % this.width) * Factor) + (Factor / 2f), ((c / this.width) * Factor) + (Factor / 2f)));
        return path;
    }

    private int NearestReached(int cx, int cy, int radius)
    {
        var best = -1;
        var bestDistance = Unreached;
        for (var y = Math.Max(0, cy - radius); y <= Math.Min(this.height - 1, cy + radius); y++)
        {
            for (var x = Math.Max(0, cx - radius); x <= Math.Min(this.width - 1, cx + radius); x++)
            {
                var c = (y * this.width) + x;
                if (this.distance[c] < bestDistance)
                {
                    best = c;
                    bestDistance = this.distance[c];
                }
            }
        }

        return best;
    }
}
