namespace Poe2Map;

/// <summary>
///     Oyuncuyu bir VARLIK olarak bulur: yuruyus yok, konum kopyasi yok.
///
///     16.09.2026'da canli oyunda dogrulandi:
///
///         alan + 0x5D0  ->  Metadata/Characters/Dex/DexFourb
///         Render bileseni sahiplik sinavini geciyor, +0x138 konumu canli izgarada yurunebilir
///
///     Alti alan yapisinin hepsi AYNI oyuncu varligini gosteriyor. Canli alani ayiran
///     sey uyanik varlik listesi: eski alanlarin listesi bos, canli alaninki oyuncunun
///     kendisini iceriyor. Yurunebilirlik sadece yedek - oyuncu kucuk izgaralarin
///     ortasindayken birden fazla izgarada gecebildigi icin tek basina yetmiyor.
///
///     Bu, eski sabitleme duzeneginin (Pin: yuruyus + birden fazla konum kopyasi)
///     yerine geciyor. O duzenekte kopyalar birkac birim farkli degerler tasiyordu ve
///     yururken "en son degisen" secimi aralarinda gidip geliyordu - harita titriyordu.
///     Render konumu ise oyunun cizdigi konumun ta kendisi.
/// </summary>
internal static class LocalPlayer
{
    internal const ulong Offset = 0x5D0;

    internal readonly record struct Found(ulong Entity, ulong Render, TerrainFinder.Terrain Area);

    /// <summary>Takip edilen oyuncu. Sinif, ki is parcaciklari arasi atama tek hamlede olsun.</summary>
    internal sealed record Tracked(ulong Entity, ulong Render);

    private static Tracked? current;

    /// <summary>Su an takip edilen oyuncu varligi; bulunana kadar null.</summary>
    internal static Tracked? Current
    {
        get => Volatile.Read(ref current);
        set => Volatile.Write(ref current, value);
    }

    /// <summary>
    ///     Canli alani ve oyuncunun Render bilesenini bulur. Bulunamazsa null
    ///     (alanda degilsin, yukleme ekrani, karakter secimi).
    /// </summary>
    internal static Found? Locate(IntPtr h, IReadOnlyList<TerrainFinder.Terrain> terrains, Action<string>? log = null)
    {
        var candidates = new List<Found>();

        foreach (var t in terrains.OrderByDescending(t => t.DataLength))
        {
            var area = t.StructAddress - 0x8D0;
            if (Resolve(h, area) is not { } player) { continue; }
            candidates.Add(new Found(player.Entity, player.Render, t));
        }

        if (candidates.Count == 0)
        {
            log?.Invoke("oyuncu varligi bulunamadi");
            return null;
        }

        // Birincil ayrac: uyanik listesinde oyuncunun kendisi olan alan.
        foreach (var c in candidates)
        {
            if (AwakeContains(h, c.Area.StructAddress - 0x8D0, c.Entity))
            {
                log?.Invoke($"canli alan {c.Area.GridX}x{c.Area.GridY} (uyanik listede oyuncu var)");
                return c;
            }
        }

        // Yedek: oyuncunun konumu tek bir izgarada yurunebilir.
        var fits = candidates
            .Where(c => ReadPosition(h, c.Render) is { } p && PlayerFinder.Walkable(h, c.Area, p.X, p.Y))
            .ToList();

        if (fits.Count == 1)
        {
            log?.Invoke($"canli alan {fits[0].Area.GridX}x{fits[0].Area.GridY} (yurunebilirlik yedegi)");
            return fits[0];
        }

        log?.Invoke($"canli alan ayirt edilemedi ({fits.Count} izgara uyuyor)");
        return null;
    }

    /// <summary>
    ///     Oyuncu varligindan icinde bulundugu alana giden isaretci. 16.09.2026'da bulundu
    ///     (MapScan arealink): varlik + 0x78 canli alani gosteriyor; GameHelper'da + 0x70
    ///     olarak yorum satirinda duruyordu, bu surumde 8 bayt kaymis.
    /// </summary>
    internal const ulong AreaLink = 0x78;

    /// <summary>
    ///     TARAMASIZ yol: bilinen bir alandan (eski olsa da) oyuncu varligina, oradan
    ///     varligin su an icinde bulundugu alana. Eski alan yapilari da + 0x5D0'da guncel
    ///     oyuncuyu gosteriyor, o yuzden alan degistikten sonra da isliyor. Her halka
    ///     sinaniyor: alanda gecerli zemin yapisi olmali ve uyanik listesi oyuncuyu icermeli.
    /// </summary>
    internal static Found? FromKnownAreas(IntPtr h, IEnumerable<ulong> knownAreas)
    {
        var tried = new HashSet<ulong>();
        foreach (var known in knownAreas)
        {
            var entity = PlayerChain.ReadPointer(h, known + Offset);
            if (!PlayerChain.LooksLikePointer(entity) || !tried.Add(entity)) { continue; }

            if (FromEntity(h, entity) is { } found) { return found; }
        }

        return null;
    }

    /// <summary>Oyuncu varligindan canli alani ve zeminini okur. Alan henuz hazir degilse null.</summary>
    internal static Found? FromEntity(IntPtr h, ulong entity)
    {
        var area = PlayerChain.ReadPointer(h, entity + AreaLink);
        if (!PlayerChain.LooksLikePointer(area)) { return null; }
        if (TerrainFinder.TryReadAt(h, area + 0x8D0) is not { } terrain) { return null; }
        if (Resolve(h, area) is not { } player || player.Entity != entity) { return null; }
        if (!AwakeContains(h, area, entity)) { return null; }

        return new Found(entity, player.Render, terrain);
    }

    /// <summary>Oyuncunun su an icinde bulundugu alanin adresi, tek okuma. Alan degisimini algilamak icin.</summary>
    internal static ulong CurrentAreaOf(IntPtr h, ulong entity) => PlayerChain.ReadPointer(h, entity + AreaLink);

    /// <summary>Bir alan yapisindan oyuncu varligini ve Render bilesenini cozer; sinavlar dahil.</summary>
    internal static (ulong Entity, ulong Render)? Resolve(IntPtr h, ulong area)
    {
        var entity = PlayerChain.ReadPointer(h, area + Offset);
        if (!PlayerChain.LooksLikePointer(entity)) { return null; }

        if (EntityReader.ReadInfo(h, entity) is not { } info) { return null; }
        if (!info.Path.StartsWith("Metadata/Characters/", StringComparison.Ordinal)) { return null; }
        if (!info.Components.TryGetValue("Render", out var render)) { return null; }
        if (!EntityReader.OwnedBy(h, render, entity)) { return null; }

        return (entity, render);
    }

    /// <summary>Alanin uyanik varlik listesi bu varligi iceriyor mu.</summary>
    internal static bool AwakeContains(IntPtr h, ulong area, ulong entity)
    {
        if (EntityReader.ReadMapHeader(h, area + EntityReader.AwakeMap) is not { Size: > 0 } map) { return false; }
        return EntityReader.WalkMap(h, map, 20000).Any(n => n.Entity == entity);
    }

    /// <summary>
    ///     Render bileseninden konum. Bilesen artik bu oyuncuya ait degilse (alan
    ///     degisti, karakter yeniden dogdu) cagiran tarafin yeniden bulmasi icin null.
    /// </summary>
    internal static (float X, float Y, float Z)? ReadPosition(IntPtr h, ulong render, ulong entity = 0)
    {
        if (entity != 0 && !EntityReader.OwnedBy(h, render, entity)) { return null; }

        var buf = new byte[12];
        if (!EntityReader.Read(h, render + EntityReader.RenderPosition, buf)) { return null; }

        var x = BitConverter.ToSingle(buf, 0);
        var y = BitConverter.ToSingle(buf, 4);
        var z = BitConverter.ToSingle(buf, 8);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) { return null; }
        if (x <= 0 || y <= 0 || x > 500000 || y > 500000) { return null; }
        return (x, y, z);
    }
}
