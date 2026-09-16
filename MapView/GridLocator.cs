namespace Poe2Map;

/// <summary>
///     Bellekte bulunmuş bir zemin ızgarası adayı.
/// </summary>
internal sealed record GridLocation(
    ulong Address,
    long ByteLength,
    int Stride,
    double Periodicity,
    double ZeroFraction,
    double BandFraction)
{
    internal int Width => this.Stride;

    internal int Height => (int)(this.ByteLength * 2 / this.Stride);

    /// <summary>
    ///     Gerçek bir alan haritasında duvar kenarları 1-2-3-4 geçiş değerleriyle
    ///     yumuşatılmış oluyor ve bu bant toplamın yüzde birkaçını tutuyor. Harita
    ///     olmayan tamponlarda böyle bir bant hiç bulunmuyor - bu yüzden en keskin
    ///     ayırt edici ölçüt bu.
    /// </summary>
    internal bool LooksLikeRealMap => this.BandFraction >= 0.002;

    public override string ToString() =>
        $"{this.Address:X}  {this.Width}x{this.Height}  ({this.ByteLength / 1024} KB, " +
        $"kenar bandi %{this.BandFraction * 100:F2}, bosluk %{this.ZeroFraction * 100:F0}, " +
        $"duzenlilik {this.Periodicity:F1}x)";
}

/// <summary>
///     Izgaranın bellekte nerede olduğunu bulan bileşen.
///
///     Bu bir arayüz çünkü iki farklı yolu var ve ikisini birden tutmak istiyoruz:
///     tarama (yavaş ama patch'ten etkilenmez) ve işaretçi zinciri (hızlı ama patch'te kırılır).
///     Uygulamanın geri kalanı hangisinin kullanıldığını bilmez.
/// </summary>
internal interface IGridLocator
{
    string Name { get; }

    IReadOnlyList<GridLocation> Locate(IntPtr processHandle, Action<string>? log = null);
}
