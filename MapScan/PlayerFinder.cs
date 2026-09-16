namespace Poe2Map;

/// <summary>
///     Oyuncunun konum alanini alan yapisindan baslayarak bulur.
///
///     Neden sabit bir yol saklamiyoruz: oyuncu VARLIGINA kadar olan kisim kararli
///     (alan + 0x5D0), ama sonrasi degil. Varligin +0xB0'i bilesen listesi ve
///     bilesenlerin sirasi her oturumda degisiyor - kaydettigimiz +0x238 yeni
///     oturumda bir metin bolgesine dustu (0x6E006F). Gordin'in kodu da bu yuzden
///     bilesenleri isimle ariyor, sabit slotla almiyor.
///
///     Bizim ayracimiz isim degil, ZEMIN: oyuncunun konumu, bulundugu alanin
///     izgarasinda yurunebilir bir hucreye denk gelmek zorunda. Varliktan itibaren
///     sig bir arama yapip bu sarti tutan float ucluyu buluyoruz. Arama alan basina
///     bir kez yapiliyor, bulunan adres onbelleklenip 60 Hz okunuyor.
/// </summary>
internal static class PlayerFinder
{
    private const int NodeRead = 0x800;
    private const int MaxNodes = 4000;

    /// <summary>
    ///     Alan yapisindan baslayip oyuncunun konum adresini dondurur.
    ///     Kayitli yolun CALISAN en uzun on ekini yuruyor, sonra oradan zemin
    ///     sinavini gecen bir float ucluyu ariyor. Bulamazsa null.
    /// </summary>
    internal static ulong? Find(IntPtr handle, ulong areaBase, TerrainFinder.Terrain terrain, int maxDepth = 2)
    {
        PlayerChain.Load();

        // SADECE ilk halka: alan + 0x5D0 = o alanin LocalPlayer isaretcisi.
        //
        // Alan yapisinin kendisinden derin arama yapmak ise yaramiyor: uc seviyede
        // paylasilan tekil nesnelere ulasiliyor ve alti alan da AYNI adresi buluyor,
        // yani hicbir ayrim kalmiyor. Oyuncu isaretcisinden baslayinca her alan kendi
        // varligini veriyor; eski alanlarin varligi da eski konumu tasiyor.
        var entry = PlayerChain.AreaHops.Length > 0 ? PlayerChain.AreaHops[0] : 0x5D0;
        var entity = PlayerChain.ReadPointer(handle, areaBase + entry);
        if (!PlayerChain.LooksLikePointer(entity)) { return null; }

        return Search(handle, entity, terrain, maxDepth);
    }

    /// <summary>
    ///     Bir nesneden baslayarak sig genislik-oncelikli arama: her dugumde once
    ///     zemin sinavini gecen float uclu var mi diye bakiyor, yoksa isaretcileri
    ///     kuyruga atiyor.
    /// </summary>
    private static ulong? Search(IntPtr handle, ulong start, TerrainFinder.Terrain terrain, int maxDepth)
    {
        var seen = new HashSet<ulong> { start };
        var queue = new Queue<(ulong Address, int Depth)>();
        queue.Enqueue((start, 0));
        var buf = new byte[NodeRead];
        var visited = 0;

        while (queue.Count > 0 && visited < MaxNodes)
        {
            var (address, depth) = queue.Dequeue();
            visited++;

            if (!Native.ReadProcessMemory(handle, (IntPtr)address, buf, (IntPtr)NodeRead, out var got) ||
                (long)got < 12)
            {
                continue;
            }

            var n = (long)got;

            for (long i = 0; i + 12 <= n; i += 4)
            {
                var x = BitConverter.ToSingle(buf, (int)i);
                if (!(x > 400f)) { continue; }

                var y = BitConverter.ToSingle(buf, (int)i + 4);
                if (!(y > 400f)) { continue; }

                var z = BitConverter.ToSingle(buf, (int)i + 8);
                if (!float.IsFinite(z) || Math.Abs(z) > 100000f) { continue; }

                // X ile Y'nin tam olarak esit olmasi dolgu isaretidir. Eski alanlarin
                // hepsi (1435, 1435) veriyordu ve z farkli oldugu icin "ucu birden
                // esit" testini geciyordu - o test cok zayifti.
                if (Math.Abs(x - y) < 0.01f) { continue; }
                if (!Walkable(handle, terrain, x, y)) { continue; }

                return address + (ulong)i;
            }

            if (depth + 1 > maxDepth) { continue; }

            for (long i = 0; i + 8 <= n; i += 8)
            {
                var value = BitConverter.ToUInt64(buf, (int)i);
                if (!PlayerChain.LooksLikePointer(value)) { continue; }
                if (!seen.Add(value)) { continue; }
                queue.Enqueue((value, depth + 1));
            }
        }

        return null;
    }

    /// <summary>
    ///     Tek sayi genislikli izgaralarda satir basina bir dolgu yarim bayti var,
    ///     o yuzden hucre adresi y * satirBayt + x / 2.
    /// </summary>
    internal static bool Walkable(IntPtr handle, TerrainFinder.Terrain t, float worldX, float worldY)
    {
        var cx = (int)(worldX / PlayerChain.WorldPerCell);
        var cy = (int)(worldY / PlayerChain.WorldPerCell);
        if (cx < 0 || cy < 0 || cx >= t.GridX || cy >= t.GridY) { return false; }

        var one = new byte[1];
        var at = t.DataStart + (ulong)((long)cy * t.BytesPerRow + (cx / 2));
        if (!Native.ReadProcessMemory(handle, (IntPtr)at, one, (IntPtr)1, out var got) || (long)got != 1)
        {
            return false;
        }

        var nibble = (cx & 1) == 0 ? one[0] & 0x0F : (one[0] >> 4) & 0x0F;
        return nibble > 0;
    }

    /// <summary>
    ///     Bir alanin oyuncu isaretcisinden ulasilabilen BUTUN makul konum adaylarini
    ///     toplar - yurunebilirlik sarti aranmadan.
    ///
    ///     Ayirmayi harekete birakiyoruz, cunku konuma dayali sinavlarin hicbiri
    ///     alanlari ayirt etmedi: oyuncu izgaralarin ortasindayken hucresi alti
    ///     izgaranin hepsinde yurunebilir cikiyor, modulden referans sayisi da
    ///     alti alan icin sifir (bu oyun kendi verilerine duz isaretci tutmuyor).
    /// </summary>
    internal static List<ulong> Candidates(IntPtr handle, ulong areaBase, int maxDepth = 2)
    {
        PlayerChain.Load();
        var entry = PlayerChain.AreaHops.Length > 0 ? PlayerChain.AreaHops[0] : 0x5D0;
        var entity = PlayerChain.ReadPointer(handle, areaBase + entry);
        if (!PlayerChain.LooksLikePointer(entity)) { return new List<ulong>(); }

        var hits = new List<ulong>();
        var seen = new HashSet<ulong> { entity };
        var queue = new Queue<(ulong Address, int Depth)>();
        queue.Enqueue((entity, 0));
        var buf = new byte[NodeRead];
        var visited = 0;

        while (queue.Count > 0 && visited < MaxNodes)
        {
            var (address, depth) = queue.Dequeue();
            visited++;

            if (!Native.ReadProcessMemory(handle, (IntPtr)address, buf, (IntPtr)NodeRead, out var got) ||
                (long)got < 12)
            {
                continue;
            }

            var n = (long)got;
            for (long i = 0; i + 12 <= n; i += 4)
            {
                var x = BitConverter.ToSingle(buf, (int)i);
                if (!(x > 400f) || x > 500000f) { continue; }

                var y = BitConverter.ToSingle(buf, (int)i + 4);
                if (!(y > 400f) || y > 500000f) { continue; }
                if (Math.Abs(x - y) < 0.01f) { continue; }

                var z = BitConverter.ToSingle(buf, (int)i + 8);
                if (!float.IsFinite(z) || Math.Abs(z) > 100000f) { continue; }

                hits.Add(address + (ulong)i);
            }

            if (depth + 1 > maxDepth) { continue; }

            for (long i = 0; i + 8 <= n; i += 8)
            {
                var value = BitConverter.ToUInt64(buf, (int)i);
                if (!PlayerChain.LooksLikePointer(value)) { continue; }
                if (!seen.Add(value)) { continue; }
                queue.Enqueue((value, depth + 1));
            }
        }

        return hits;
    }

    /// <summary>Bulunan adresten konumu okur.</summary>
    internal static (float X, float Y, float Z)? ReadAt(IntPtr handle, ulong address)
    {
        if (address == 0) { return null; }

        var twelve = new byte[12];
        if (!Native.ReadProcessMemory(handle, (IntPtr)address, twelve, (IntPtr)12, out var got) ||
            (long)got != 12)
        {
            return null;
        }

        var x = BitConverter.ToSingle(twelve, 0);
        var y = BitConverter.ToSingle(twelve, 4);
        var z = BitConverter.ToSingle(twelve, 8);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) { return null; }
        if (x <= 0 || y <= 0 || x > 500000 || y > 500000) { return null; }
        return (x, y, z);
    }
}
