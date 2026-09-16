namespace Poe2Map;

/// <summary>
///     MapScan arealink: oyuncu varligindan canli ALANA giden bir isaretci arar.
///
///     Neden: alan degisiminde yeni zemini bulmak butun bellegi (7-13 GB) taramak
///     demek, 4-7 saniye. Oyuncu varligi alanlar arasi ayni kaliyor; icinde bulundugu
///     alana bir isaretci tutuyorsa (GameHelper'da entity + 0x70 "CurrentAreaInstanceOwner"
///     yorum satiri olarak duruyor) yeni alan taramasiz, dogrudan okunur.
///
///     Sinav: isaretcinin gosterdigi adres + 0x8D0'da gecerli bir zemin yapisi var mi,
///     ve o alanin uyanik listesi oyuncunun kendisini iceriyor mu (eski alanlarinki bos).
///     Iki seviye bakiyor: varlik + o, ve varlik + o -> + o2.
/// </summary>
internal static class AreaLinkCommand
{
    private const int Window = 0x400;

    internal static int Run()
    {
        var game = Native.FindGame();
        if (game == null) { Console.Error.WriteLine("Oyun sureci bulunamadi."); return 1; }

        var h = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (h == IntPtr.Zero) { Console.Error.WriteLine("OpenProcess basarisiz."); return 1; }

        try
        {
            var terrains = TerrainFinder.Find(h, Console.WriteLine);
            if (LocalPlayer.Locate(h, terrains, Console.WriteLine) is not { } live)
            {
                Console.Error.WriteLine("Canli alan bulunamadi.");
                return 1;
            }

            var liveArea = live.Area.StructAddress - 0x8D0;
            Console.WriteLine($"canli alan {liveArea:X} ({live.Area.GridX}x{live.Area.GridY}), oyuncu varligi {live.Entity:X}");
            Console.WriteLine();

            var entity = new byte[Window];
            if (!EntityReader.Read(h, live.Entity, entity)) { Console.Error.WriteLine("varlik okunamadi"); return 1; }

            var hits = 0;
            for (var o = 0; o + 8 <= Window; o += 8)
            {
                var v = BitConverter.ToUInt64(entity, o);
                if (!PlayerChain.LooksLikePointer(v)) { continue; }

                hits += Check(h, $"varlik +0x{o:X}", v, liveArea, live.Entity);

                var inner = new byte[Window];
                if (!EntityReader.Read(h, v, inner)) { continue; }

                for (var o2 = 0; o2 + 8 <= Window; o2 += 8)
                {
                    var w = BitConverter.ToUInt64(inner, o2);
                    if (PlayerChain.LooksLikePointer(w)) { hits += Check(h, $"varlik +0x{o:X} -> +0x{o2:X}", w, liveArea, live.Entity); }
                }
            }

            Console.WriteLine();
            Console.WriteLine(hits == 0 ? "Oyuncu varligindan alana giden isaretci bulunamadi." : $"{hits} bag bulundu.");
            return 0;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    private static int Check(IntPtr h, string path, ulong candidate, ulong liveArea, ulong entity)
    {
        if (TerrainFinder.TryReadAt(h, candidate + 0x8D0) is not { } t) { return 0; }

        var isLive = candidate == liveArea;
        var awake = LocalPlayer.AwakeContains(h, candidate, entity);
        Console.WriteLine($"  {path,-28} -> alan {candidate:X}  izgara {t.GridX}x{t.GridY}  " +
                          $"{(isLive ? "CANLI ALAN" : "eski alan")}  uyanik listede oyuncu: {(awake ? "EVET" : "hayir")}");
        return 1;
    }
}
