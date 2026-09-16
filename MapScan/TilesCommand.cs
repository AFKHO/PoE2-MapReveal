namespace Poe2Map;

/// <summary>
///     MapScan tiles: canli alanin karolarindan cikislari bulmayi sinar.
///
///     Cikti:
///       - karo okumasinin kendi sinavi (vektor boyutu = karo sayisi)
///       - yolunda "transition" gecen karolar, metakaro basina kumelenmis, oyuncuya
///         uzakliklariyla
///       - Radar'in onemli karo listesiyle (important_tgt_files.txt) eslesenler ve
///         kapinin nereye acildigi
///       - karsilastirma icin: su an bellekte olan cikis VARLIKLARI
/// </summary>
internal static class TilesCommand
{
    private const string RadarList =
        @"C:\Users\erkan\Desktop\p2\Gamehelper-src\Plugins\Radar\important_tgt_files.txt";

    internal static int Run()
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var h = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (h == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            var terrains = TerrainFinder.Find(h, null);
            if (LocalPlayer.Locate(h, terrains, Console.WriteLine) is not { } live)
            {
                Console.Error.WriteLine("Canli alan bulunamadi.");
                return 1;
            }

            var player = LocalPlayer.ReadPosition(h, live.Render);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tiles = TileReader.Read(h, live.Area);
            Console.WriteLine($"alan {live.Area.GridX}x{live.Area.GridY}, {live.Area.TilesX}x{live.Area.TilesY} karo, " +
                              $"okunan {tiles.Count} ({sw.ElapsedMilliseconds} ms), " +
                              $"{tiles.Select(t => t.Path).Distinct().Count()} farkli karo dosyasi");
            Console.WriteLine();

            var radar = LoadRadarList();
            Console.WriteLine($"Radar listesi: {radar.Count} anahtar");
            Console.WriteLine();

            Console.WriteLine("yolunda 'transition' gecen karolar (dosya basina):");
            foreach (var group in tiles
                         .Where(t => t.Path.Contains("transition", StringComparison.OrdinalIgnoreCase))
                         .GroupBy(t => t.Path))
            {
                var list = group.ToList();
                var cx = list.Average(t => TileReader.Center(t).X);
                var cy = list.Average(t => TileReader.Center(t).Y);
                Console.WriteLine($"  {list.Count,3} karo  merkez ({cx,7:F0}, {cy,7:F0})  {Distance(player, cx, cy)}  {group.Key}");

                foreach (var t in list.Where(t => radar.ContainsKey(t.Key)))
                {
                    var (x, y) = TileReader.Center(t);
                    Console.WriteLine($"        RADAR: {t.Key[(t.Key.LastIndexOf('/') + 1)..]} -> \"{radar[t.Key]}\"  " +
                                      $"({x:F0}, {y:F0})  {Distance(player, x, y)}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("Radar listesiyle eslesen BUTUN karolar:");
            foreach (var t in tiles.Where(t => radar.ContainsKey(t.Key)))
            {
                var (x, y) = TileReader.Center(t);
                Console.WriteLine($"  ({x,7:F0}, {y,7:F0})  {Distance(player, x, y)}  \"{radar[t.Key]}\"  {t.Key}");
            }

            Console.WriteLine();
            Console.WriteLine("karsilastirma - bellekteki cikis VARLIKLARI:");
            var area = live.Area.StructAddress - 0x8D0;
            var entities = ExitFinder.Find(h, area, new Dictionary<(uint, ulong), EntityReader.EntityInfo?>());
            foreach (var e in entities)
            {
                Console.WriteLine($"  ({e.X,7:F0}, {e.Y,7:F0})  {Distance(player, e.X, e.Y)}  {e.Name}");
            }

            Console.WriteLine();
            Console.WriteLine("OVERLAY'IN CIZECEGI CIKISLAR (TileExits):");
            foreach (var target in TileExits.WithEntities(TileExits.FromTiles(tiles), entities))
            {
                Console.WriteLine($"  ({target.X,7:F0}, {target.Y,7:F0})  {Distance(player, target.X, target.Y)}  " +
                                  $"\"{target.Name}\"  [{target.Key[..Math.Min(target.Key.Length, 70)]}]");
            }

            return 0;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    private static string Distance((float X, float Y, float Z)? player, float x, float y) =>
        player is { } p ? $"uzaklik {Math.Sqrt(Math.Pow(x - p.X, 2) + Math.Pow(y - p.Y, 2)),6:F0}" : "";

    /// <summary>Radar'in listesi: { alan: { karo anahtari: ad } }. Alan ayrimini atliyoruz.</summary>
    private static Dictionary<string, string> LoadRadarList()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(RadarList)) { return map; }

        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RadarList));
        foreach (var areaEntry in json.RootElement.EnumerateObject())
        {
            foreach (var tile in areaEntry.Value.EnumerateObject())
            {
                map[tile.Name] = tile.Value.GetString() ?? "";
            }
        }

        return map;
    }
}
