namespace Poe2Map;

/// <summary>
///     MapScan exits: bir alanin cikislarinin bellekte nasil durdugunu kesfeder.
///
///     Cevaplanacak soru: alan gecis kapilari varlik listesinde var mi, ve uzaktakiler
///     de var mi? Oyun ag balonunun disindaki varliklari istemciye gondermeyebiliyor;
///     oyle ise cikisi uzaktan bulmak icin baska bir kaynak (zemin dosyalari) gerekir.
///
///     Cikti: canli alandaki butun varliklar yollarina gore gruplu, ve adinda ya da
///     bilesenlerinde gecis/portal/kapi gecen her varlik konumu ve oyuncuya uzakligiyla.
/// </summary>
internal static class ExitsCommand
{
    private static readonly string[] Hints =
    {
        "Transition", "Portal", "Exit", "Door", "Waypoint", "Checkpoint", "Teleport", "Stairs", "Entrance",
    };

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
                Console.Error.WriteLine("Canli alan bulunamadi - bir alanda misin?");
                return 1;
            }

            var area = live.Area.StructAddress - 0x8D0;
            var player = LocalPlayer.ReadPosition(h, live.Render);
            Console.WriteLine($"alan {live.Area.GridX}x{live.Area.GridY}  oyuncu " +
                              (player is { } pp ? $"({pp.X:F0}, {pp.Y:F0})" : "?"));
            Console.WriteLine();

            var all = new List<(string List, ulong Entity, EntityReader.EntityInfo Info)>();
            foreach (var (label, offset) in new[] { ("uyanik", EntityReader.AwakeMap), ("uyuyan", EntityReader.SleepingMap) })
            {
                if (EntityReader.ReadMapHeader(h, area + offset) is not { } map) { continue; }
                foreach (var (_, entity) in EntityReader.WalkMap(h, map))
                {
                    if (EntityReader.ReadInfo(h, entity) is { } info) { all.Add((label, entity, info)); }
                }
            }

            Console.WriteLine($"toplam {all.Count} varlik. Yol gruplari (ilk iki klasor):");
            foreach (var g in all.GroupBy(x => Prefix(x.Info.Path)).OrderByDescending(g => g.Count()).Take(30))
            {
                var awake = g.Count(x => x.List == "uyanik");
                Console.WriteLine($"  {g.Count(),5}  (uyanik {awake,4})  {g.Key}");
            }

            Console.WriteLine();
            Console.WriteLine("gecis/portal/kapi adaylari:");

            var hits = all
                .Where(x => Hints.Any(k => x.Info.Path.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                           x.Info.Components.Keys.Any(c => c.Contains(k, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            foreach (var (list, entity, info) in hits.Take(60))
            {
                var pos = info.Components.TryGetValue("Render", out var render)
                    ? LocalPlayer.ReadPosition(h, render)
                    : null;
                var distance = pos is { } p && player is { } q
                    ? Math.Sqrt(Math.Pow(p.X - q.X, 2) + Math.Pow(p.Y - q.Y, 2)).ToString("F0")
                    : "?";
                var where = pos is { } w ? $"({w.X,7:F0}, {w.Y,7:F0})" : "(konum yok)";

                Console.WriteLine($"  {list,-6} {where}  uzaklik {distance,6}  {info.Path}");
                Console.WriteLine($"         bilesenler: {string.Join(", ", info.Components.Keys)}");
            }

            if (hits.Count == 0) { Console.WriteLine("  (hic yok)"); }

            TestPaths(h, live, area, player);
            return 0;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    /// <summary>
    ///     Overlay'in yapacagini burada yapip olcer: cikislari ve kapilari bul, maliyet
    ///     izgarasini kur, her cikisa mesafe haritasi hesapla, oyuncudan yolu cikar.
    /// </summary>
    private static void TestPaths(IntPtr h, LocalPlayer.Found live, ulong area, (float X, float Y, float Z)? player)
    {
        Console.WriteLine();
        Console.WriteLine("=== yol sinavi ===");
        if (player is not { } pp) { Console.WriteLine("oyuncu konumu yok"); return; }

        var t = live.Area;
        var raw = new byte[t.DataLength];
        if (!EntityReader.Read(h, t.DataStart, raw)) { Console.WriteLine("zemin okunamadi"); return; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var doors = new List<(float X, float Y)>();
        var exits = ExitFinder.Find(h, area, new Dictionary<(uint, ulong), EntityReader.EntityInfo?>(), doors);
        Console.WriteLine($"{exits.Count} cikis, {doors.Count} kapi  ({sw.ElapsedMilliseconds} ms)");

        sw.Restart();
        var grid = FlowField.BuildCostGrid(raw, t.BytesPerRow, t.GridX, t.GridY, doors, out var w, out var hh);
        Console.WriteLine($"maliyet izgarasi {w}x{hh}  ({sw.ElapsedMilliseconds} ms)");

        foreach (var exit in exits)
        {
            sw.Restart();
            var field = FlowField.Build(grid, w, hh, exit.X, exit.Y);
            var built = sw.ElapsedMilliseconds;

            sw.Restart();
            var path = field.PathFrom(pp.X, pp.Y);
            var traced = sw.Elapsed.TotalMilliseconds;

            var straight = Math.Sqrt(Math.Pow(exit.X - pp.X, 2) + Math.Pow(exit.Y - pp.Y, 2)) / PlayerChain.WorldPerCell;
            var walked = 0.0;
            for (var i = 1; i < path.Count; i++)
            {
                walked += Math.Sqrt(Math.Pow(path[i].X - path[i - 1].X, 2) + Math.Pow(path[i].Y - path[i - 1].Y, 2));
            }

            var end = path.Count > 0 ? path[^1] : default;
            var endGap = path.Count > 0
                ? Math.Sqrt(Math.Pow((end.X * PlayerChain.WorldPerCell) - exit.X, 2) + Math.Pow((end.Y * PlayerChain.WorldPerCell) - exit.Y, 2)) / PlayerChain.WorldPerCell
                : double.NaN;

            Console.WriteLine($"  {exit.Name}: harita {built} ms, yol izi {traced:F2} ms, {path.Count} nokta, " +
                              $"yol {walked:F0} hucre (kus ucusu {straight:F0}), yolun sonu kapiya {endGap:F0} hucre");
        }
    }

    private static string Prefix(string path)
    {
        var parts = path.Split('/');
        return parts.Length >= 3 ? $"{parts[0]}/{parts[1]}/{parts[2]}" : path;
    }
}
