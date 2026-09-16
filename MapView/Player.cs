namespace Poe2Map;

/// <summary>
///     Oyuncunun dünya konumu.
/// </summary>
internal readonly record struct PlayerPos(float X, float Y, float Z)
{
    /// <summary>
    ///     Dunya biriminden hucreye cevirme boleni: 250 / 23 = 10.8696.
    ///
    ///     Oyunun kendi kodunda bir doseme 250 dunya birimi ve 23 hucre tutuyor. Bir ara
    ///     bizim olcumumuz 10'u isaret ediyor gibi gorundu, ama o yanilma tek sayi genislikli
    ///     izgaralarda satir basina bir hucre kaymasindan geliyordu; dolgu yarim bayti
    ///     duzeltilince yuruyerek yapilan olcum 10.8201 verdi - yani oyunun kendi orani.
    ///
    ///     Yine de ayarlanabilir birakiyoruz: "Olcegi kalibre et" bunu yeniden olcebiliyor.
    /// </summary>
    internal const float TileBased = PlayerChain.WorldPerCell;

    internal static float UnitsPerCell { get; set; } = TileBased;

    internal int CellX => (int)(this.X / UnitsPerCell);

    internal int CellY => (int)(this.Y / UnitsPerCell);
}

/// <summary>
///     Oyuncunun konumunu sabit bir işaretçi zinciriyle okur.
///
///     Zincirin kendisi <see cref="PlayerChain" /> içinde, tek kaynak olarak duruyor:
///     patch geldiğinde değişecek yer orası. Okuma başarısız olursa uygulama çökmüyor,
///     sadece "buradasın" işareti kayboluyor - harita yine çalışıyor.
/// </summary>
internal static class Player
{
    internal static PlayerPos? TryRead(IntPtr handle, ulong moduleBase)
    {
        // Birinci kaynak: oyuncu VARLIGININ Render konumu - oyunun cizdigi konumun kendisi.
        // Tek kaynak oldugu icin kopyalar arasinda gidip gelme (yururken titreme) yok.
        // Sahiplik her okumada sinaniyor; alan degisince bilesen el degistirirse null doner.
        if (LocalPlayer.Current is { } lp)
        {
            // Varlik biliniyorsa okunamayan karede eski kopyalara DUSMUYORUZ: onlar onceki
            // bir sabitlemeden kalma, farkli konum tasiyabilir ve harita o karede siccrar.
            // null donmesi yeterli; alan degisimi algilayicisi yeniden buluyor.
            return LocalPlayer.ReadPosition(handle, lp.Render, lp.Entity) is { } rp
                ? new PlayerPos(rp.X, rp.Y, rp.Z)
                : null;
        }

        // Yedek: canli kopyalar. Yuruyus sinavini gecen butun adresler tutuluyor ve
        // en son degisen kullaniliyor. Tek adres olebiliyor (alan degisince oyun
        // nesneyi yeniden olusturuyor) ve olu kopya eski konumu gostermeye devam
        // ediyor - harita da takip etmeyi birakiyor.
        if (LivePosition.Read(handle) is { } live)
        {
            return new PlayerPos(live.X, live.Y, live.Z);
        }

        // Sonra tek sabitlenmis adres. Sabit zincirlerin hicbiri guvenilir degil -
        // modul tabanli olan oyun yeniden baslatilinca kiriliyor, alan tabanli olan
        // da varliktan sonrasinda bilesen sirasi degistigi icin.
        if (LiveArea.LastPositionAddress != 0 &&
            PlayerFinder.ReadAt(handle, LiveArea.LastPositionAddress) is { } pinned)
        {
            return new PlayerPos(pinned.X, pinned.Y, pinned.Z);
        }

        var objectBase = PlayerChain.ResolveFromArea(handle, LiveArea.LastAreaBase);
        if (objectBase == 0) { objectBase = PlayerChain.Resolve(handle, moduleBase); }
        if (objectBase == 0) { return null; }

        return PlayerChain.ReadPosition(handle, objectBase) is { } p
            ? new PlayerPos(p.X, p.Y, p.Z)
            : null;
    }
}
