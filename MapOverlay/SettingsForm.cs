namespace Poe2Map;

/// <summary>
///     Ayar penceresi: harita cizgisi (renk, kalinlik) ve canavar simgeleri
///     (<see cref="MonsterPanel"/>). Degisiklik aninda haritaya uygulaniyor,
///     "Tamam"a basmak gerekmiyor; pencere kapaninca diske yaziliyor.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly OverlaySettings settings;
    private readonly Action apply;
    private readonly Button colorButton = new();
    private readonly NumericUpDown thicknessBox = new();

    internal SettingsForm(OverlaySettings settings, Action apply)
    {
        this.settings = settings;
        this.apply = apply;

        this.Text = "PoE2 Harita - Ayarlar";
        this.FormBorderStyle = FormBorderStyle.FixedToolWindow;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.TopMost = true;

        var colorLabel = new Label { Text = "Cizgi rengi", AutoSize = true };
        colorLabel.SetBounds(16, 20, 100, 20);

        this.colorButton.SetBounds(130, 14, 110, 28);
        this.colorButton.FlatStyle = FlatStyle.Flat;
        this.colorButton.BackColor = settings.LineColor;
        this.colorButton.Click += (_, _) => this.PickColor();

        var thicknessLabel = new Label { Text = "Cizgi kalinligi", AutoSize = true };
        thicknessLabel.SetBounds(16, 60, 100, 20);

        this.thicknessBox.SetBounds(130, 56, 110, 24);
        this.thicknessBox.Minimum = 1;
        this.thicknessBox.Maximum = OverlayForm.MaxThickness;
        this.thicknessBox.Value = Math.Clamp(settings.Thickness, 1, OverlayForm.MaxThickness);
        this.thicknessBox.ValueChanged += (_, _) =>
        {
            this.settings.Thickness = (int)this.thicknessBox.Value;
            this.apply();
        };

        var monsters = new MonsterPanel(settings, apply) { Location = new Point(12, 92) };

        this.Controls.AddRange(new Control[]
        {
            colorLabel, this.colorButton, thicknessLabel, this.thicknessBox, monsters,
        });

        this.ClientSize = new Size(260, monsters.Bottom + 12);
        this.FormClosed += (_, _) => this.settings.Save();
    }

    private void PickColor()
    {
        using var dialog = new ColorDialog { Color = this.settings.LineColor, FullOpen = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) { return; }

        this.settings.LineColor = dialog.Color;
        this.colorButton.BackColor = dialog.Color;
        this.apply();
    }
}
