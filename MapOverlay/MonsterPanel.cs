namespace Poe2Map;

/// <summary>
///     Canavar simgeleri bolumu: her nadirlik icin AYRI bir satir.
///
///         [x] Normal   boyut [ 8]
///         [x] Magic    boyut [ 8]
///         [x] Rare     boyut [12]
///         [x] Unique   boyut [16]
///
///     Kutu isaretini kaldirmak o nadirligi tamamen kapatiyor: overlay onu ne
///     tariyor ne ciziyor. Boyut simgenin capi (piksel). Degisiklik aninda
///     haritaya gidiyor - panel acikken harita gorunur kaliyor ki sonuc gorulsun.
///
///     Nadirlik adlari ve renkler OverlayForm'dan geliyor; burada ikinci bir
///     liste tutmuyoruz, yoksa renk degisirse panel yalan soylerdi.
/// </summary>
internal sealed class MonsterPanel : GroupBox
{
    private const int RowHeight = 30;

    internal MonsterPanel(OverlaySettings settings, Action apply)
    {
        this.Text = "Canavar simgeleri";
        this.Size = new Size(236, 30 + (OverlayForm.RarityNames.Length * RowHeight));

        for (var i = 0; i < OverlayForm.RarityNames.Length; i++)
        {
            var rarity = i;
            var top = 22 + (i * RowHeight);

            var box = new CheckBox
            {
                Text = OverlayForm.RarityNames[rarity],
                Checked = settings.MonsterVisible[rarity],
                ForeColor = OverlayForm.RarityColor(rarity),
                AutoSize = false,
            };
            box.SetBounds(12, top, 86, 24);

            var sizeBox = new NumericUpDown
            {
                Minimum = OverlayForm.MinIconSize,
                Maximum = OverlayForm.MaxIconSize,
                Value = Math.Clamp(settings.MonsterSize[rarity], OverlayForm.MinIconSize, OverlayForm.MaxIconSize),
                Enabled = settings.MonsterVisible[rarity],
            };
            sizeBox.SetBounds(150, top + 1, 66, 24);

            var sizeLabel = new Label { Text = "boyut", AutoSize = true };
            sizeLabel.SetBounds(106, top + 4, 44, 20);

            box.CheckedChanged += (_, _) =>
            {
                settings.MonsterVisible[rarity] = box.Checked;
                sizeBox.Enabled = box.Checked;
                apply();
            };

            sizeBox.ValueChanged += (_, _) =>
            {
                settings.MonsterSize[rarity] = (int)sizeBox.Value;
                apply();
            };

            this.Controls.AddRange(new Control[] { box, sizeLabel, sizeBox });
        }
    }
}
