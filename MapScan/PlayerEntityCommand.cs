namespace Poe2Map;

/// <summary>
///     MapScan playerentity: oyuncuyu bir VARLIK olarak okumayi sinar.
///
///     Fikir: alan + 0x5D0 (Gordin'de LocalPlayerPtr; bizim yapisal aramamizin da
///     buldugu tek kararli halka) oyuncu varligini gosteriyorsa, konumu canavarlarla
///     ayni sekilde - bileseni ISIMLE bulup Render + 0x138'den - okuyabiliriz.
///
///     Bunun iki kazanci olur:
///       - Sabit yol kirilmaz: 0xB0 -> 0x238 gibi bilesen SIRASINA bagli halkalar yok,
///         isim tablosu siralar degisse de dogru bileseni verir.
///       - Yuruyus gerekmez ve tek kaynak olur: oyunun cizdigi Render konumu. Birden
///         fazla kopya arasinda gidip gelmekten dogan titreme de ortadan kalkar.
/// </summary>
internal static class PlayerEntityCommand
{
    internal const ulong LocalPlayerOffset = 0x5D0;

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

            foreach (var t in terrains.OrderByDescending(t => t.DataLength))
            {
                var area = t.StructAddress - 0x8D0;
                var entity = PlayerChain.ReadPointer(h, area + LocalPlayerOffset);
                Console.Write($"alan {area:X} ({t.GridX}x{t.GridY})  +0x5D0 -> {entity:X}  ");

                if (!PlayerChain.LooksLikePointer(entity)) { Console.WriteLine("isaretci degil"); continue; }
                if (EntityReader.ReadInfo(h, entity) is not { } info) { Console.WriteLine("varlik okunamadi"); continue; }

                Console.WriteLine(info.Path);

                if (!info.Components.TryGetValue("Render", out var render))
                {
                    Console.WriteLine($"    Render yok. bilesenler: {string.Join(", ", info.Components.Keys)}");
                    continue;
                }

                var owned = EntityReader.OwnedBy(h, render, entity);
                var pos = new byte[12];
                EntityReader.Read(h, render + EntityReader.RenderPosition, pos);
                var x = BitConverter.ToSingle(pos, 0);
                var y = BitConverter.ToSingle(pos, 4);
                var walkable = PlayerFinder.Walkable(h, t, x, y);

                Console.WriteLine($"    Render sahiplik {(owned ? "GECTI" : "gecemedi")}  konum ({x:F1}, {y:F1})  " +
                                  $"kendi izgarasinda yurunebilir: {(walkable ? "EVET" : "hayir")}");
                Console.WriteLine($"    bilesenler: {string.Join(", ", info.Components.Keys)}");
            }

            // Karsilastirma: sabitlenmis kopyalar ne diyor?
            PlayerChain.Load();
            if (PlayerFinder.ReadAt(h, PlayerChain.PinnedAddress) is { } pinned)
            {
                Console.WriteLine();
                Console.WriteLine($"karsilastirma - sabitlenmis adres 0x{PlayerChain.PinnedAddress:X}: ({pinned.X:F1}, {pinned.Y:F1})");
            }

            return 0;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }
}
