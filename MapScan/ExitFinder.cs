namespace Poe2Map;

/// <summary>
///     Alanin cikislarini bulur: AreaTransition bileseni olan varliklar.
///
///     16.09.2026'da Valley of the Titans'ta olculdu (MapScan exits):
///       - Gercek gecis kapilari (AncientSeal1/2/3Portal) AreaTransition tasiyor; kapi
///         gibi gorunen ama gecis olmayanlarda (titan heykelleri, kamera nesneleri) yok.
///         Bu yuzden ayrac yol adi degil, bilesen.
///       - Statik nesneler alanin TAMAMI icin yuklu: 4410 varligin 3981'i haritaya
///         dagilmis sus nesnesi ve cogu uyuyan listede; 2978 birim uzaktaki kontrol
///         noktasi bile listede. Kapilar da statik oldugu icin uyuyan listedeki
///         konumlari bayat degil - iki liste de okunuyor.
/// </summary>
internal static class ExitFinder
{
    internal readonly record struct Exit(uint Id, string Name, float X, float Y);

    /// <summary>
    ///     Iki listedeki cikislari ve kapilari dondurur. Kapilar yol bulma icin: zeminde
    ///     kapali gorunen ama acilan gecitler (Radar'daki gibi TriggerableBlockage bileseni
    ///     ya da yolunda "Door" gecen varliklar). Onlar olmadan yol kapali kapinin
    ///     etrafindan dolasiyor. Yol ve bilesenler cache'ten geliyor; bir alan binlerce
    ///     varlik iceriyor, her taramada hepsini yeniden okumak bosa.
    /// </summary>
    internal static List<Exit> Find(
        IntPtr h, ulong area, Dictionary<(uint, ulong), EntityReader.EntityInfo?> cache,
        List<(float X, float Y)>? doors = null)
    {
        var exits = new List<Exit>();

        foreach (var offset in new[] { EntityReader.AwakeMap, EntityReader.SleepingMap })
        {
            if (EntityReader.ReadMapHeader(h, area + offset) is not { } map) { continue; }

            foreach (var (id, entity) in EntityReader.WalkMap(h, map, 20000))
            {
                var key = (id, entity);
                if (!cache.TryGetValue(key, out var info))
                {
                    info = EntityReader.ReadInfo(h, entity);
                    cache[key] = info;
                }

                if (info is null) { continue; }

                var isExit = info.Components.ContainsKey("AreaTransition");
                var isDoor = doors is not null &&
                             (info.Components.ContainsKey("TriggerableBlockage") ||
                              info.Path.Contains("Door", StringComparison.OrdinalIgnoreCase));
                if (!isExit && !isDoor) { continue; }

                if (!info.Components.TryGetValue("Render", out var render)) { continue; }
                if (!EntityReader.OwnedBy(h, render, entity)) { continue; }
                if (LocalPlayer.ReadPosition(h, render) is not { } p) { continue; }

                if (isDoor) { doors!.Add((p.X, p.Y)); }
                if (isExit) { exits.Add(new Exit(id, ShortName(info.Path), p.X, p.Y)); }
            }
        }

        // Ayni kapi iki listede birden gorunebilir; varlik numarasiyla tekillestiriyoruz.
        return exits.GroupBy(e => e.Id).Select(g => g.First()).ToList();
    }

    /// <summary>"Metadata/Terrain/.../AncientSeal1Portal" -> "AncientSeal1Portal".</summary>
    private static string ShortName(string path)
    {
        var at = path.LastIndexOf('/');
        return at >= 0 && at < path.Length - 1 ? path[(at + 1)..] : path;
    }
}
