namespace Poe2Map;

/// <summary>
///     Zemin karolarini okur: alanin her 23x23 hucrelik parcasi bir karo dosyasina
///     (.tdt) bagli.
///
///     Neden: cikis kapisi VARLIKLARI uzaktayken bellekte yok - 16.09.2026'da Infested
///     Barrens'a girildiginde sadece girilen kapi gorundu, digerleri yaklasinca yuklendi.
///     Karolar ise zemin izgarasi gibi alan yuklenir yuklenmez TAMAMEN bellekte.
///     Radar uzaktaki onemli yerleri (gecisler, boss arenalari) bu yuzden karolardan buluyor.
///
///     Yapi (GameHelper AreaInstance.GetTgtFileData):
///         zemin + 0x28   std::vector&lt;karo&gt;, karo basina 0x38 bayt
///         karo  + 0x08   karo dosyasi -> +0x08 yol (std::wstring)
///         karo  + 0x34   metakaro icinde x,   + 0x35 y,   + 0x36 donus
///     Anahtar Radar'in listesiyle ayni bicimde: yol + "x:X-y:Y", donus tekse X/Y yer degisir.
///     Yol ".tdt" ile bittigi icin anahtar "...tdtx:1-y:0" gibi gorunuyor.
/// </summary>
internal static class TileReader
{
    private const int TileSize = 0x38;
    internal const int CellsPerTile = 23;

    internal readonly record struct Tile(string Key, string Path, int TileX, int TileY);

    internal static List<Tile> Read(IntPtr h, TerrainFinder.Terrain terrain) =>
        Read(h, terrain.StructAddress, terrain.TilesX, terrain.TilesY);

    /// <summary>
    ///     Overlay zemin yapisinin kendisini tutmuyor, alan adresini ve izgara boyutunu
    ///     tutuyor; karo sayisi izgara / 23. Vektor boyutu karo sayisina esit degilse
    ///     okuma reddediliyor - yanlis adres ya da kaymis offset sessizce cop uretmesin.
    /// </summary>
    internal static List<Tile> Read(IntPtr h, ulong structAddress, long tilesX, long tilesY)
    {
        var tiles = new List<Tile>();
        var header = new byte[0x38];
        if (!EntityReader.Read(h, structAddress, header)) { return tiles; }

        var first = BitConverter.ToUInt64(header, 0x28);
        var last = BitConverter.ToUInt64(header, 0x30);
        if (!PlayerChain.LooksLikePointer(first) || last <= first) { return tiles; }

        var count = (long)(last - first) / TileSize;
        if (tilesX <= 0 || count != tilesX * tilesY || count > 2_000_000) { return tiles; }

        var data = new byte[count * TileSize];
        if (!EntityReader.Read(h, first, data)) { return tiles; }

        // Ayni karo dosyasi yuzlerce karoda tekrar ediyor; yolu isaretci basina bir kez oku.
        var paths = new Dictionary<ulong, string>();
        var file = new byte[0x28];

        for (var i = 0; i < count; i++)
        {
            var at = (int)(i * TileSize);
            var tgt = BitConverter.ToUInt64(data, at + 0x08);
            if (!PlayerChain.LooksLikePointer(tgt)) { continue; }

            if (!paths.TryGetValue(tgt, out var path))
            {
                path = EntityReader.Read(h, tgt, file) ? EntityReader.ReadWString(h, file, 0x08) : "";
                paths[tgt] = path;
            }

            if (path.Length == 0) { continue; }

            var idX = data[at + 0x34];
            var idY = data[at + 0x35];
            var rotation = data[at + 0x36];
            var key = rotation % 2 == 0 ? $"{path}x:{idX}-y:{idY}" : $"{path}x:{idY}-y:{idX}";

            tiles.Add(new Tile(key, path, (int)(i % tilesX), (int)(i / tilesX)));
        }

        return tiles;
    }

    /// <summary>Karonun ortasinin dunya konumu.</summary>
    internal static (float X, float Y) Center(Tile tile) =>
        (((tile.TileX * CellsPerTile) + (CellsPerTile / 2f)) * PlayerChain.WorldPerCell,
         ((tile.TileY * CellsPerTile) + (CellsPerTile / 2f)) * PlayerChain.WorldPerCell);
}
