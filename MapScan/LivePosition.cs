using System.Diagnostics;

namespace Poe2Map;

/// <summary>
///     Oyuncunun konumunu TEK bir adresten degil, yuruyus sinavini gecen butun
///     kopyalardan okur ve her karede en son DEGISENI kullanir.
///
///     Neden: tek adres oluyor. 13.09.2026'da ayarsiz exe bir adresi sabitledi,
///     sonra alan degisince oyun o nesneyi yeniden olusturdu. Adres eski konumu
///     tutmaya devam etti - makul, yurunebilir bir konum - ama bir daha hic
///     degismedi. Gunlukte "overlay konumu (22505, 7766)" iki dakika boyunca ayni
///     kaldi, oyuncu yururken. Harita da haliyle takip etmedi.
///
///     Oyun konumu birkac yerde birden tutuyor. Biri olurse donar, digerleri
///     hareket etmeye devam eder; en son degiseni secmek olu kopyayi kendiliginden
///     birakir. Hepsi birden olurse (nadir) bir sonraki sabitlemeye kadar donar.
/// </summary>
internal static class LivePosition
{
    private static readonly object Gate = new();

    private static ulong[] addresses = Array.Empty<ulong>();
    private static (float X, float Y, float Z)?[] last = Array.Empty<(float, float, float)?>();
    private static long[] changedAt = Array.Empty<long>();
    private static int chosen;

    internal static bool HasAddresses
    {
        get { lock (Gate) { return addresses.Length > 0; } }
    }

    internal static int Count
    {
        get { lock (Gate) { return addresses.Length; } }
    }

    internal static ulong[] Snapshot()
    {
        lock (Gate) { return (ulong[])addresses.Clone(); }
    }

    /// <summary>Su an kullanilan kopyanin adresi; yoksa 0.</summary>
    internal static ulong Current
    {
        get { lock (Gate) { return addresses.Length == 0 ? 0 : addresses[chosen]; } }
    }

    internal static void Set(IEnumerable<ulong> list, ulong preferred = 0)
    {
        lock (Gate)
        {
            addresses = list.Where(a => a != 0).Distinct().ToArray();
            last = new (float, float, float)?[addresses.Length];
            changedAt = new long[addresses.Length];
            chosen = Math.Max(0, Array.IndexOf(addresses, preferred));
        }
    }

    internal static void Clear() => Set(Array.Empty<ulong>());

    /// <summary>
    ///     Butun kopyalari okur, degisenlerin zamanini gunceller ve en son
    ///     degisenin degerini dondurur. Hic biri henuz degismediyse secili kalan.
    /// </summary>
    internal static (float X, float Y, float Z)? Read(IntPtr handle)
    {
        lock (Gate)
        {
            if (addresses.Length == 0) { return null; }

            var now = Stopwatch.GetTimestamp();
            for (var i = 0; i < addresses.Length; i++)
            {
                var v = PlayerFinder.ReadAt(handle, addresses[i]);
                if (v is { } nv && last[i] is { } lv &&
                    (Math.Abs(nv.X - lv.X) > 0.5f || Math.Abs(nv.Y - lv.Y) > 0.5f))
                {
                    changedAt[i] = now;
                }

                last[i] = v;
            }

            var best = chosen;
            for (var i = 0; i < addresses.Length; i++)
            {
                if (last[i] is null) { continue; }
                if (last[best] is null || changedAt[i] > changedAt[best]) { best = i; }
            }

            chosen = best;
            return last[chosen];
        }
    }
}
