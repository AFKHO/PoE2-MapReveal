using System.Drawing.Drawing2D;

namespace Poe2Map;

/// <summary>
///     Canavar isaretleri: nadirliga gore renkli noktalar.
///
///         normal kirmizi   magic mavi   rare sari   unique kahverengi
///
///     Iki hizda calisiyor:
///       - Saniyede dort kez (arka plan): varlik listesi geziliyor, canli ve dusman
///         canavarlarin Render bilesen adresleri ve nadirligi cikariliyor. Yol ve
///         bilesen adresleri varlik basina bir kez okunup onbellekleniyor.
///       - Her karede (cizim): sadece bu adreslerden konum okunuyor. Onceden konum da
///         saniyede dort kez okunuyordu; hareket eden canavarin noktasi harita akarken
///         adim adim ziplyordu.
///
///     Her nadirlik tek tek acilip kapatilabiliyor ve simge capi ayri ayri
///     verilebiliyor (SetMonsterStyle). Kapatilan nadirlik ne taraniyor ne ciziliyor.
///
///     Sadece UYANIK varliklar: ag balonunun (ekran civari) disindakileri istemci
///     bilmiyor, uyuyan listedekilerin konumu bayat - onlari cizmek hayalet uretir.
/// </summary>
internal sealed partial class OverlayForm
{
    private const int MonsterRefreshMs = 250;

    /// <summary>Simge capi (piksel): ayar panelinin sinirlari ve varsayilani.</summary>
    internal const int MinIconSize = 2;
    internal const int MaxIconSize = 40;
    internal const int DefaultIconSize = 8;

    /// <summary>Nadirlik sirasi. Butun dizilerin (renk, gorunurluk, boyut) indeksi bu.</summary>
    internal static readonly string[] RarityNames = { "Normal", "Magic", "Rare", "Unique" };

    private static readonly SolidBrush[] RarityBrushes =
    {
        new(Color.FromArgb(235, 50, 50)),    // normal  - kirmizi
        new(Color.FromArgb(70, 130, 255)),   // magic   - mavi
        new(Color.FromArgb(255, 215, 0)),    // rare    - sari
        new(Color.FromArgb(165, 100, 45)),   // unique  - kahverengi
    };

    /// <summary>Nadirlik basina simge rengi - ayar paneli etiketlerini boyamak icin.</summary>
    internal static Color RarityColor(int rarity) => RarityBrushes[Math.Clamp(rarity, 0, RarityBrushes.Length - 1)].Color;

    /// <summary>
    ///     Nadirlik basina gorunurluk ve simge capi. Tek nesne olarak degistiriliyor:
    ///     ayari arayuz is parcacigi yazarken cizim ve arka plan okumasi yarim bir
    ///     durum gormesin - basvuru atamasi bolunmez.
    /// </summary>
    private sealed record IconStyle(bool[] Visible, float[] Diameter);

    private IconStyle icons = new(
        new[] { true, true, true, true },
        new[] { (float)DefaultIconSize, DefaultIconSize, DefaultIconSize, DefaultIconSize });

    /// <summary>
    ///     Nadirlik basina simge ayari: gorunurluk ve cap (piksel). Dizilerin sirasi
    ///     <see cref="RarityNames"/> ile ayni. Degerler kopyalaniyor, cagiran taraf
    ///     kendi dizisini degistirmeye devam edebilir.
    /// </summary>
    internal void SetMonsterStyle(IReadOnlyList<bool> visible, IReadOnlyList<int> sizes)
    {
        var count = RarityBrushes.Length;
        var v = new bool[count];
        var d = new float[count];

        for (var i = 0; i < count; i++)
        {
            v[i] = i < visible.Count && visible[i];
            d[i] = Math.Clamp(i < sizes.Count ? sizes[i] : DefaultIconSize, MinIconSize, MaxIconSize);
        }

        this.icons = new IconStyle(v, d);
    }

    // Saf siyah saydamlik anahtari; kontur ondan ayrilsin diye 20.
    private static readonly Pen MonsterOutline = new(Color.FromArgb(20, 20, 20), 1f);

    private readonly Dictionary<(uint Id, ulong Entity), EntityReader.EntityInfo?> entityCache = new();
    private readonly byte[] monsterPosition = new byte[8];
    private System.Threading.Timer? monsterTimer;
    private (ulong Render, int Rarity)[] trackedMonsters = Array.Empty<(ulong, int)>();
    private ulong areaBase;
    private int monsterRefreshing;

    /// <summary>
    ///     Canli alanin nesnesi (zemin yapisi - 0x8D0). Harita yuklendiginde denetleyici
    ///     cagiriyor; 0 verilirse canavar okumasi durur.
    /// </summary>
    internal void SetArea(ulong area)
    {
        if (this.areaBase != area)
        {
            lock (this.entityCache) { this.entityCache.Clear(); }
            this.trackedMonsters = Array.Empty<(ulong, int)>();

            // Eski alanin yollari yeni alanin haritasi uzerinde bes saniye durmasin.
            this.pathTargets = Array.Empty<PathTarget>();
        }

        var changed = this.areaBase != area;
        this.areaBase = area;
        this.StartPathTracking();

        // Yeni alanin yollarini bes saniyelik zamanlayiciyi beklemeden hemen hesapla.
        if (changed && area != 0) { this.pathTimer?.Change(50, ExitRefreshMs); }

        if (this.monsterTimer is null)
        {
            this.monsterTimer = new System.Threading.Timer(_ => this.RefreshMonsters(), null, 0, MonsterRefreshMs);
            this.Disposed += (_, _) => this.monsterTimer?.Dispose();
        }
    }

    private void RefreshMonsters()
    {
        // Bir tur uzun surerse ust uste binmesin.
        if (Interlocked.Exchange(ref this.monsterRefreshing, 1) == 1) { return; }

        try
        {
            var h = this.handle;
            var area = this.areaBase;
            var style = this.icons;

            // Hepsi kapaliysa varlik listesini gezmeye de gerek yok.
            if (h == IntPtr.Zero || area == 0 || Array.IndexOf(style.Visible, true) < 0 ||
                EntityReader.ReadMapHeader(h, area + EntityReader.AwakeMap) is not { } map)
            {
                this.trackedMonsters = Array.Empty<(ulong, int)>();
                return;
            }

            var nodes = EntityReader.WalkMap(h, map, 5000);
            var found = new List<(ulong, int)>(64);
            var seen = new HashSet<(uint, ulong)>();

            lock (this.entityCache)
            {
                foreach (var (id, entity) in nodes)
                {
                    var key = (id, entity);
                    seen.Add(key);

                    // Varlik numarasi da anahtarda: olen bir varligin adresi yenisine
                    // verilebiliyor, sadece adrese gore onbellek eski yolu gosterirdi.
                    if (!this.entityCache.TryGetValue(key, out var info))
                    {
                        info = EntityReader.ReadInfo(h, entity);
                        this.entityCache[key] = info;
                    }

                    if (info is { IsMonster: true } &&
                        EntityReader.TryReadMonster(h, entity, info, out var monster) &&
                        style.Visible[monster.Rarity] &&
                        info.Components.TryGetValue("Render", out var render))
                    {
                        found.Add((render, monster.Rarity));
                    }
                }

                // Listeden cikanlari unut, yoksa onbellek alan boyunca buyur.
                if (this.entityCache.Count > (seen.Count * 2) + 64)
                {
                    foreach (var stale in this.entityCache.Keys.Where(k => !seen.Contains(k)).ToList())
                    {
                        this.entityCache.Remove(stale);
                    }
                }
            }

            this.trackedMonsters = found.ToArray();
        }
        catch (Exception ex)
        {
            // Oyun kapanirken ya da alan degisirken okumalar yarim kalabilir; bir sonraki tur duzelir.
            CrashLog.Write("canavar okuma", ex);
        }
        finally
        {
            Interlocked.Exchange(ref this.monsterRefreshing, 0);
        }
    }

    /// <summary>
    ///     Haritanin donusumuyle canavarlari cizer. PaintOverlay'den, arayuz is
    ///     parcaciginda cagriliyor; konumlar burada, her karede okunuyor.
    /// </summary>
    private void PaintMonsters(PaintEventArgs e, Matrix transform)
    {
        var list = this.trackedMonsters;
        var h = this.handle;
        var style = this.icons;
        if (list.Length == 0 || h == IntPtr.Zero) { return; }

        var points = new PointF[list.Length];
        var valid = new bool[list.Length];

        for (var i = 0; i < list.Length; i++)
        {
            // Kapatilan nadirligin konumunu okumaya da gerek yok; tarama arasinda
            // (250 ms) kapatilmis olabilir, o yuzden burada da bakiyoruz.
            if (!style.Visible[list[i].Rarity]) { continue; }

            if (!EntityReader.Read(h, list[i].Render + EntityReader.RenderPosition, this.monsterPosition)) { continue; }

            var x = BitConverter.ToSingle(this.monsterPosition, 0);
            var y = BitConverter.ToSingle(this.monsterPosition, 4);
            if (!float.IsFinite(x) || !float.IsFinite(y) || x <= 0 || y <= 0) { continue; }

            points[i] = new PointF((x / PlayerPos.UnitsPerCell) + this.MapOffsetX, (y / PlayerPos.UnitsPerCell) + this.MapOffsetY);
            valid[i] = true;
        }

        transform.TransformPoints(points);

        // Kucuk noktalar duzlestirme olmadan daha keskin; buyutulmus simgede ise
        // basamakli kenar goze batiyor.
        var large = false;
        for (var i = 0; i < style.Diameter.Length; i++)
        {
            if (style.Visible[i] && style.Diameter[i] > DefaultIconSize) { large = true; }
        }

        var saved = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = large ? SmoothingMode.AntiAlias : SmoothingMode.None;

        var width = this.ClientSize.Width;
        var height = this.ClientSize.Height;

        for (var i = 0; i < list.Length; i++)
        {
            if (!valid[i]) { continue; }

            var rarity = list[i].Rarity;
            var d = style.Diameter[rarity];
            var p = points[i];
            if (p.X < -d || p.Y < -d || p.X > width + d || p.Y > height + d) { continue; }

            var left = p.X - (d / 2f);
            var top = p.Y - (d / 2f);
            e.Graphics.FillEllipse(RarityBrushes[rarity], left, top, d, d);
            e.Graphics.DrawEllipse(MonsterOutline, left, top, d, d);
        }

        e.Graphics.SmoothingMode = saved;
    }
}
