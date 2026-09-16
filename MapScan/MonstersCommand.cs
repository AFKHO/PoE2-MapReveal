namespace Poe2Map;

/// <summary>
///     MapScan monsters: canavar okuyucusunu canli oyunda sinar.
///
///     Iki is yapiyor:
///       1. Her alan icin varlik listesinin halkalarini raporluyor.
///       2. Canli alanda, bilesen icindeki her suphe alani icin (konum, can, tepki,
///          nadirlik) OLASI BUTUN offsetleri tarayip canavarlarin cogunda mantikli
///          deger veren offseti oneriyor. Patch bir alani kaydirdiysa dogru offset
///          boylece tahmin edilmeden bulunuyor.
///
///     16.09.2026: uyanik 23 canavarin hicbiri elegi gecmedi - bir alanin kaydigini
///     gosteriyordu, bu komut o yuzden var.
/// </summary>
internal static class MonstersCommand
{
    private const int ComponentWindow = 0x400;

    internal static int Run()
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var h = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (h == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            var terrains = TerrainFinder.Find(h, Console.WriteLine);
            Console.WriteLine();

            // Canli alan: en cok canavari olan. Eski alanlarin listelerinde canavar kalmiyor.
            var best = terrains
                .Select(t => (Terrain: t, Monsters: CollectMonsters(h, t.StructAddress - 0x8D0)))
                .OrderByDescending(x => x.Monsters.Count)
                .FirstOrDefault();

            if (best.Monsters is null || best.Monsters.Count == 0)
            {
                Console.WriteLine("Hicbir alanda canavar yok - etrafta canavar olan bir yerde dene.");
                return 1;
            }

            var t = best.Terrain;
            var monsters = best.Monsters;
            Console.WriteLine($"canli alan {t.StructAddress - 0x8D0:X}  izgara {t.GridX}x{t.GridY}  canavar {monsters.Count}");
            Console.WriteLine();

            SuggestRenderPosition(h, t, monsters);
            SuggestLife(h, monsters);
            SuggestReaction(h, monsters);
            SuggestRarity(h, monsters);
            DumpSamples(h, t, monsters);
            return 0;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    private static List<(ulong Entity, EntityReader.EntityInfo Info)> CollectMonsters(IntPtr h, ulong area)
    {
        var list = new List<(ulong, EntityReader.EntityInfo)>();
        foreach (var offset in new[] { EntityReader.AwakeMap, EntityReader.SleepingMap })
        {
            if (EntityReader.ReadMapHeader(h, area + offset) is not { } map) { continue; }

            foreach (var (_, entity) in EntityReader.WalkMap(h, map))
            {
                if (EntityReader.ReadInfo(h, entity) is { IsMonster: true } info) { list.Add((entity, info)); }
            }
        }

        return list;
    }

    private static byte[]? Window(IntPtr h, EntityReader.EntityInfo info, string component)
    {
        if (!info.Components.TryGetValue(component, out var address)) { return null; }
        var buf = new byte[ComponentWindow];
        return EntityReader.Read(h, address, buf) ? buf : null;
    }

    /// <summary>Konum: float uclunun hucresi izgaranin icinde ve yurunebilir olmali.</summary>
    private static void SuggestRenderPosition(
        IntPtr h, TerrainFinder.Terrain t, List<(ulong Entity, EntityReader.EntityInfo Info)> monsters)
    {
        var windows = monsters.Select(m => Window(h, m.Info, "Render")).Where(w => w != null).ToList();
        var scores = new List<(int Offset, int Count)>();

        for (var o = 0x10; o + 12 <= ComponentWindow; o += 4)
        {
            var count = windows.Count(w =>
            {
                var x = BitConverter.ToSingle(w!, o);
                var y = BitConverter.ToSingle(w!, o + 4);
                return x > 400f && y > 400f && x < 500000f && y < 500000f && PlayerFinder.Walkable(h, t, x, y);
            });
            if (count > 0) { scores.Add((o, count)); }
        }

        Report("Render konumu", EntityReader.RenderPosition, windows.Count, scores);
    }

    /// <summary>Can: (toplam, simdiki) tamsayi cifti, 0 &lt; simdiki &lt;= toplam.</summary>
    private static void SuggestLife(IntPtr h, List<(ulong Entity, EntityReader.EntityInfo Info)> monsters)
    {
        var windows = monsters.Select(m => Window(h, m.Info, "Life")).Where(w => w != null).ToList();
        var scores = new List<(int Offset, int Count)>();

        for (var o = 0x10; o + 8 <= ComponentWindow; o += 4)
        {
            var count = windows.Count(w =>
            {
                var total = BitConverter.ToInt32(w!, o);
                var current = BitConverter.ToInt32(w!, o + 4);
                return total > 0 && total < 50_000_000 && current > 0 && current <= total;
            });
            if (count > 0) { scores.Add((o, count)); }
        }

        Report("Life toplam (+4 = simdiki)", EntityReader.LifeTotal, windows.Count, scores);
    }

    /// <summary>
    ///     Tepki: dusman canavarlar icin 0x01 (dost) OLMAMALI. Her offsetin deger
    ///     dagilimini basiyoruz; mevcut offset herkese 1 veriyorsa kaymis demektir.
    /// </summary>
    private static void SuggestReaction(IntPtr h, List<(ulong Entity, EntityReader.EntityInfo Info)> monsters)
    {
        var windows = monsters.Select(m => Window(h, m.Info, "Positioned")).Where(w => w != null).ToList();
        var at = (int)EntityReader.Reaction;
        var values = windows.GroupBy(w => w![at] & 0x7F).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key:X2}x{g.Count()}");
        Console.WriteLine($"Positioned tepki @0x{at:X}: {string.Join("  ", values)}   (0x01 = dost)");
        Console.WriteLine();
    }

    /// <summary>Nadirlik: 0..3. Dagilim cogunlukla 0 olmali.</summary>
    private static void SuggestRarity(IntPtr h, List<(ulong Entity, EntityReader.EntityInfo Info)> monsters)
    {
        var windows = monsters.Select(m => Window(h, m.Info, "ObjectMagicProperties")).Where(w => w != null).ToList();
        var at = (int)EntityReader.RarityOffset;
        var values = windows.GroupBy(w => BitConverter.ToInt32(w!, at)).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}x{g.Count()}");
        Console.WriteLine($"ObjectMagicProperties nadirlik @0x{at:X}: {string.Join("  ", values)}   (0-3 olmali)");
        Console.WriteLine();
    }

    private static void Report(string what, ulong current, int total, List<(int Offset, int Count)> scores)
    {
        var top = scores.OrderByDescending(s => s.Count).Take(5).ToList();
        var currentScore = scores.FirstOrDefault(s => s.Offset == (int)current).Count;

        Console.WriteLine($"{what}: simdiki 0x{current:X} -> {currentScore}/{total} tutuyor");
        foreach (var s in top)
        {
            Console.WriteLine($"    0x{s.Offset:X3}  {s.Count}/{total}{(s.Offset == (int)current ? "   <- simdiki" : "")}");
        }

        Console.WriteLine();
    }

    private static void DumpSamples(
        IntPtr h, TerrainFinder.Terrain t, List<(ulong Entity, EntityReader.EntityInfo Info)> monsters)
    {
        Console.WriteLine("ornekler (simdiki offsetlerle):");
        foreach (var (entity, info) in monsters.Take(10))
        {
            var render = Window(h, info, "Render");
            var life = Window(h, info, "Life");
            var positioned = Window(h, info, "Positioned");
            var magic = Window(h, info, "ObjectMagicProperties");

            var pos = render is null ? "-" :
                $"({BitConverter.ToSingle(render, (int)EntityReader.RenderPosition):F0}, " +
                $"{BitConverter.ToSingle(render, (int)EntityReader.RenderPosition + 4):F0})";
            var hp = life is null ? "-" :
                $"{BitConverter.ToInt32(life, (int)EntityReader.LifeCurrent)}/{BitConverter.ToInt32(life, (int)EntityReader.LifeTotal)}";
            var reaction = positioned is null ? "-" : $"{positioned[(int)EntityReader.Reaction]:X2}";
            var rarity = magic is null ? "-" : BitConverter.ToInt32(magic, (int)EntityReader.RarityOffset).ToString();
            var name = info.Path.Length > 48 ? "..." + info.Path[^45..] : info.Path;

            var owned = string.Join("", new[] { "Render", "Life", "Positioned", "ObjectMagicProperties" }
                .Select(c => info.Components.TryGetValue(c, out var a) && EntityReader.OwnedBy(h, a, entity) ? "+" : "-"));

            Console.WriteLine($"  {pos,-16} can {hp,-14} tepki {reaction}  nadirlik {rarity}  sahip[{owned}]  {name}");
        }
    }
}
