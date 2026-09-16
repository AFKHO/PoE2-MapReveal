namespace Poe2Map;

/// <summary>
///     Alanin cikislarini zemin karolarindan cikarir ve bellekteki kapi varliklariyla birlestirir.
///
///     16.09.2026 Infested Barrens olcumu: alana girildiginde kapi varliklarindan sadece
///     girilen kapi bellekteydi. Karolar ise 15 bin birim uzaktaki cikisi bile veriyordu:
///     Chimeral Wetlands, Jungle Ruins, Larva Hollow - 19 ms'de.
///
///     Iki kaynak:
///       - Radar'in onemli karo listesi (data/important_tgt_files.txt): kapinin kesin alt
///         karosu ve gittigi yerin adi. Listede cikis olmayan yerler de var (odul veren
///         noktalar, boss arenalari); parantezli adlar ve yolunda arena/boss gecenler eleniyor.
///       - Genel kural: yolunda transition/entrance/stairs gecen karolar. Listede olmayan
///         alanlarda da calisiyor; kapinin hangi alt karoda oldugu bilinmedigi icin metakaronun
///         ortasi aliniyor.
///
///     Kapi varligi yaklasinca bellege yukleniyor; o zaman karo tahmininin yerine kapinin
///     kesin konumu kullaniliyor.
/// </summary>
internal static class TileExits
{
    internal readonly record struct Target(string Key, string Name, float X, float Y);

    private const float MergeDistance = 1000f;
    private const float EntitySnapDistance = 900f;

    private static Dictionary<string, string>? radarList;

    internal static List<Target> FromTiles(List<TileReader.Tile> tiles)
    {
        var radar = RadarList;
        var targets = new List<Target>();
        var namedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tile in tiles)
        {
            if (!radar.TryGetValue(tile.Key, out var name) || !LooksLikeExit(tile.Path, name)) { continue; }

            var (x, y) = TileReader.Center(tile);
            targets.Add(new Target($"karo:{tile.Key}@{tile.TileX},{tile.TileY}", name.Trim(), x, y));
            namedPaths.Add(tile.Path);
        }

        foreach (var group in tiles.Where(t => IsGenericExit(t.Path) && !namedPaths.Contains(t.Path)).GroupBy(t => t.Path))
        {
            foreach (var cluster in Clusters(group.ToList()))
            {
                var x = cluster.Average(t => TileReader.Center(t).X);
                var y = cluster.Average(t => TileReader.Center(t).Y);
                targets.Add(new Target($"karo:{group.Key}@{cluster[0].TileX},{cluster[0].TileY}", PrettyName(group.Key), x, y));
            }
        }

        // Ayni kapiya ait yakin girisleri tekillestir (listede ayni kapi icin iki alt karo olabiliyor).
        var merged = new List<Target>();
        foreach (var t in targets)
        {
            if (merged.Any(m => Distance(m.X, m.Y, t.X, t.Y) < MergeDistance)) { continue; }
            merged.Add(t);
        }

        return merged;
    }

    /// <summary>Bellekteki kapi varliklari: yakin bir karo cikisi varsa onun konumunu duzeltir, yoksa ekler.</summary>
    internal static List<Target> WithEntities(List<Target> fromTiles, List<ExitFinder.Exit> entities)
    {
        var result = new List<Target>(fromTiles);

        foreach (var e in entities)
        {
            var nearest = -1;
            var best = EntitySnapDistance;
            for (var i = 0; i < result.Count; i++)
            {
                var d = Distance(result[i].X, result[i].Y, e.X, e.Y);
                if (d < best) { best = d; nearest = i; }
            }

            if (nearest >= 0)
            {
                result[nearest] = result[nearest] with { X = e.X, Y = e.Y };
            }
            else
            {
                result.Add(new Target($"varlik:{e.Id}", e.Name, e.X, e.Y));
            }
        }

        return result;
    }

    private static bool IsGenericExit(string path) =>
        path.Contains("transition", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("entrance", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("stairs", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeExit(string path, string name) =>
        IsGenericExit(path) ||
        (!name.Contains('(') &&
         !path.Contains("arena", StringComparison.OrdinalIgnoreCase) &&
         !path.Contains("boss", StringComparison.OrdinalIgnoreCase));

    /// <summary>Ayni dosyanin karolarini komsuluga gore ayri kapilara bolur (bir alanda iki kez gecebilir).</summary>
    private static List<List<TileReader.Tile>> Clusters(List<TileReader.Tile> tiles)
    {
        var clusters = new List<List<TileReader.Tile>>();
        var left = new List<TileReader.Tile>(tiles);

        while (left.Count > 0)
        {
            var cluster = new List<TileReader.Tile> { left[0] };
            left.RemoveAt(0);

            for (var i = 0; i < cluster.Count; i++)
            {
                for (var j = left.Count - 1; j >= 0; j--)
                {
                    if (Math.Abs(left[j].TileX - cluster[i].TileX) <= 1 && Math.Abs(left[j].TileY - cluster[i].TileY) <= 1)
                    {
                        cluster.Add(left[j]);
                        left.RemoveAt(j);
                    }
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    /// <summary>".../JungleDepths_to_ChimeralWetlands.tdt" -> "JungleDepths_to_ChimeralWetlands".</summary>
    private static string PrettyName(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static float Distance(float ax, float ay, float bx, float by) =>
        MathF.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

    /// <summary>
    ///     Radar'in listesi: { alan: { karo anahtari: ad } }. Alan ayrimi atlaniyor: anahtarlar
    ///     karo yoluyla birlikte zaten alana ozgu. Dosya exe'nin yaninda; yoksa sadece genel kural calisir.
    /// </summary>
    private static Dictionary<string, string> RadarList
    {
        get
        {
            if (radarList is not null) { return radarList; }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var file = Path.Combine(AppContext.BaseDirectory, "important_tgt_files.txt");
                if (File.Exists(file))
                {
                    using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                    foreach (var area in json.RootElement.EnumerateObject())
                    {
                        foreach (var tile in area.Value.EnumerateObject())
                        {
                            map[tile.Name] = tile.Value.GetString() ?? "";
                        }
                    }
                }
            }
            catch
            {
                // Bozuk liste haritayi durdurmasin; genel kural yine calisir.
            }

            radarList = map;
            return map;
        }
    }
}
