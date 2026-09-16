using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Poe2Map;

internal sealed class MainForm : Form
{
    private readonly Button locateButton = new();
    private readonly Button calibButton = new();
    private readonly CheckBox autoScan = new();
    private readonly CheckBox follow = new();
    private readonly CheckBox topMost = new();
    private readonly Label info = new();
    private readonly Button overlayButton = new();
    private readonly NumericUpDown scaleBox = new();
    private readonly NumericUpDown shiftXBox = new();
    private readonly NumericUpDown shiftYBox = new();
    private readonly NumericUpDown angleBox = new();
    private readonly CheckBox flipBox = new();
    private readonly NumericUpDown mapDxBox = new();
    private readonly NumericUpDown mapDyBox = new();
    private readonly CheckBox liveBox = new();
    private readonly NumericUpDown leanBox = new();
    private readonly CheckBox outlineBox = new();
    private readonly Button colorButton = new();
    private readonly Button alignButton = new();
    private readonly Label status = new();
    private readonly PictureBox view = new();
    private readonly System.Windows.Forms.Timer timer = new();

    private GridLocation? active;
    private PlayerPos? player;
    private PlayerPos? lastSeen;
    private bool scanning;
    private bool everLocated;
    private string calibNote = "";

    /// <summary>Oyundan canli okunan izdusum degerleri - hesap tutuyor mu gormek icin.</summary>
    private string liveNote = "";
    private int bytesPerRow;
    private byte[]? rawMap;
    private int gridW, gridH;
    private OverlayForm? overlay;
    private ulong mapElement;

    private const float AreaChangeJump = 1500f;

    internal MainForm()
    {
        this.Text = "PoE2 Harita";
        this.Width = 1600;
        this.Height = 800;
        this.BackColor = Color.FromArgb(18, 18, 20);

        this.locateButton.Text = "Haritayi bul";
        this.locateButton.SetBounds(10, 10, 120, 30);
        this.locateButton.Click += async (_, _) => await this.LocateAsync(false);

        this.calibButton.Text = "Olcegi kalibre et";
        this.calibButton.SetBounds(136, 10, 140, 30);
        this.calibButton.Click += async (_, _) => await this.CalibrateScaleAsync();

        this.autoScan.Text = "Alan degisince tara";
        this.autoScan.SetBounds(290, 16, 150, 20);
        this.autoScan.ForeColor = Color.Gainsboro;
        this.autoScan.Checked = true;

        this.follow.Text = "Konumu takip et";
        this.follow.SetBounds(450, 16, 130, 20);
        this.follow.ForeColor = Color.Gainsboro;
        this.follow.Checked = true;

        this.topMost.Text = "Ustte kal";
        this.topMost.SetBounds(590, 16, 90, 20);
        this.topMost.ForeColor = Color.Gainsboro;
        this.topMost.CheckedChanged += (_, _) => this.TopMost = this.topMost.Checked;


        this.overlayButton.Text = "Overlay";
        this.overlayButton.SetBounds(690, 12, 80, 26);
        this.overlayButton.Click += (_, _) => this.ToggleOverlay();

        // Kalibrasyon kontrolleri: olcegi ve merkezi elle ayarlamak icin.
        // Karakter ekranda gorunen bilinen bir nokta oldugu icin, isaret onun
        // uzerine oturana kadar bu uc sayiyi oynatmak yetiyor.
        this.scaleBox.SetBounds(778, 15, 58, 22);
        this.scaleBox.DecimalPlaces = 2;
        this.scaleBox.Increment = 0.05m;
        this.scaleBox.Minimum = 0.1m;
        this.scaleBox.Maximum = 20m;
        this.scaleBox.Value = 1.6m;
        this.scaleBox.ValueChanged += (_, _) => this.ApplyOverlaySettings();

        this.shiftXBox.SetBounds(842, 15, 62, 22);
        this.shiftXBox.Minimum = -2000;
        this.shiftXBox.Maximum = 2000;
        this.shiftXBox.ValueChanged += (_, _) => this.ApplyOverlaySettings();

        this.shiftYBox.SetBounds(910, 15, 62, 22);
        this.shiftYBox.Minimum = -2000;
        this.shiftYBox.Maximum = 2000;
        this.shiftYBox.ValueChanged += (_, _) => this.ApplyOverlaySettings();

        this.angleBox.SetBounds(978, 15, 58, 22);
        this.angleBox.DecimalPlaces = 1;
        this.angleBox.Increment = 0.5m;
        this.angleBox.Minimum = 5m;
        this.angleBox.Maximum = 85m;
        this.angleBox.Value = 38.7m;
        this.angleBox.ValueChanged += (_, _) => this.ApplyOverlaySettings();

        this.flipBox.Text = "ters";
        this.flipBox.SetBounds(1042, 17, 55, 20);
        this.flipBox.ForeColor = Color.Gainsboro;
        this.flipBox.CheckedChanged += (_, _) => this.ApplyOverlaySettings();

        // Haritayi isaretten bagimsiz kaydirir - hucre cinsinden olcum aracidir.
        this.mapDxBox.SetBounds(1102, 15, 58, 22);
        this.mapDxBox.Minimum = -5000;
        this.mapDxBox.Maximum = 5000;
        this.mapDxBox.ValueChanged += (_, _) => this.ApplyOverlaySettings();

        this.mapDyBox.SetBounds(1166, 15, 58, 22);
        this.mapDyBox.Minimum = -5000;
        this.mapDyBox.Maximum = 5000;
        this.mapDyBox.ValueChanged += (_, _) => this.ApplyOverlaySettings();

        this.liveBox.Text = "canli";
        this.liveBox.SetBounds(1232, 17, 60, 20);
        this.liveBox.ForeColor = Color.Gainsboro;
        this.liveBox.Checked = true;
        this.liveBox.CheckedChanged += (_, _) => this.ApplyOverlaySettings();

        // Yon tuslariyla kaydirma hizi, kare basina piksel (60 kare/sn).
        // Kaydirma oyundan okunamiyor - hicbir UI alaninda, hicbir kamera kopyasinda
        // yok ve dunya-ekran matrisinin kalici kopyasi bulunmuyor. O yuzden tuslari
        // kendimiz dinleyip ayni hizda kaydiriyoruz; Tab sifirliyor. Isaret ters
        // geliyorsa negatif deger ver.
        this.leanBox.SetBounds(1512, 15, 64, 22);
        this.leanBox.DecimalPlaces = 2;
        this.leanBox.Increment = 0.05m;
        this.leanBox.Minimum = -60m;
        this.leanBox.Maximum = 60m;
        // 5.4 gozle kalibre edildi (12.09.2026, 1920x1017, zoom 0.5).
        this.leanBox.Value = 5.4m;
        this.leanBox.ValueChanged += (_, _) => this.ApplyOverlaySettings();

        // Sadece dis cizgiler: odalarin ici saydam kalir, oyunun goruntusu gorunur.
        this.outlineBox.Text = "sadece cizgi";
        this.outlineBox.SetBounds(1240, 42, 100, 20);
        this.outlineBox.ForeColor = Color.Gainsboro;
        this.outlineBox.Checked = true;
        this.outlineBox.CheckedChanged += (_, _) => this.ApplyOverlaySettings();

        this.colorButton.SetBounds(1348, 40, 54, 24);
        this.colorButton.BackColor = this.lineColor;
        this.colorButton.FlatStyle = FlatStyle.Flat;
        this.colorButton.Click += (_, _) => this.PickColor();

        // Tiklamayla hizalama: merkezi artik ebeveyn zincirinden hesapliyoruz, yani
        // bu dugmeye normalde gerek yok. Hesap tutmazsa farki OLCMEK icin duruyor.
        this.alignButton.Text = "Hizala (F8)";
        this.alignButton.SetBounds(1298, 12, 110, 26);
        this.alignButton.Click += (_, _) => this.StartAlign();

        this.info.SetBounds(1416, 16, 90, 20);
        this.info.ForeColor = Color.FromArgb(150, 150, 160);
        this.info.Text = $"olcek: {PlayerPos.UnitsPerCell:F4} birim/hucre";

        this.status.SetBounds(10, 46, 1500, 20);
        this.status.ForeColor = Color.Gainsboro;
        this.status.Text = "Oyun acikken \"Haritayi bul\"a bas.";

        this.view.SetBounds(10, 72, 1140, 680);
        this.view.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        this.view.SizeMode = PictureBoxSizeMode.Zoom;
        this.view.BackColor = Color.FromArgb(10, 10, 12);
        this.view.Paint += this.OnViewPaint;

        this.timer.Interval = 400;
        this.timer.Tick += async (_, _) => await this.Tick();
        this.timer.Start();

        this.Controls.AddRange(this.locateButton, this.calibButton, this.autoScan,
            this.follow, this.topMost, this.overlayButton, this.scaleBox, this.shiftXBox,
            this.shiftYBox, this.angleBox, this.flipBox, this.mapDxBox, this.mapDyBox,
            this.liveBox, this.alignButton, this.info, this.leanBox,
            this.outlineBox, this.colorButton, this.status, this.view);

        // Kutularin ustune ne olduklarini yazan kucuk etiketler. Hangisinin dY
        // oldugunu hatirlamak zorunda kalmamak icin.
        this.AddBoxLabel("olcek", this.scaleBox);
        this.AddBoxLabel("dX", this.shiftXBox);
        this.AddBoxLabel("dY", this.shiftYBox);
        this.AddBoxLabel("aci", this.angleBox);
        this.AddBoxLabel("harita dX", this.mapDxBox);
        this.AddBoxLabel("harita dY", this.mapDyBox);
        this.AddBoxLabel("kaydirma hizi", this.leanBox);
    }

    /// <summary>Bir sayi kutusunun tam ustune adini yazar.</summary>
    private void AddBoxLabel(string text, Control box)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = false,
            Font = new Font(this.Font.FontFamily, 6.75f),
            ForeColor = Color.FromArgb(140, 140, 150),
            TextAlign = ContentAlignment.BottomLeft,
        };

        label.SetBounds(box.Left + 1, 0, box.Width + 20, 14);
        this.Controls.Add(label);
        label.BringToFront();
    }

    private static (IntPtr Handle, ulong ModuleBase) OpenGame()
    {
        var game = Native.FindGame() ?? throw new InvalidOperationException("Oyun sureci bulunamadi. PoE2 acik mi?");
        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { throw new InvalidOperationException("Surece baglanilamadi (OpenProcess)."); }

        ulong moduleBase = 0;
        try { moduleBase = (ulong)(game.MainModule?.BaseAddress.ToInt64() ?? 0); }
        catch { /* modul bilgisi yoksa konum okunamaz */ }

        return (handle, moduleBase);
    }

    // ------------------------------------------------------------ zamanlayici

    private async Task Tick()
    {
        if (this.scanning) { return; }

        PlayerPos? now = null;
        try
        {
            var (handle, moduleBase) = OpenGame();
            try
            {
                now = Player.TryRead(handle, moduleBase);
                this.RefreshLiveTransform(handle);
            }
            finally { Native.CloseHandle(handle); }
        }
        catch
        {
            return;
        }

        // Alan degisimi: konum surekliligi kopar. Ayni alanda karakter bir karede
        // bu kadar yol alamaz.
        var changed = now is { } n && this.lastSeen is { } prev &&
                      (Math.Abs(n.X - prev.X) > AreaChangeJump || Math.Abs(n.Y - prev.Y) > AreaChangeJump);

        this.lastSeen = now;
        if (this.follow.Checked)
        {
            this.player = now;
            if (this.overlay is { IsDisposed: false }) { this.overlay.Player = now; }
            this.UpdateStatus();
            this.view.Invalidate();
        }

        if (changed && this.autoScan.Checked && this.everLocated)
        {
            await this.LocateAsync(true);
            return;
        }

        // Ilk yukleme de kendiliginden olsun: "Haritayi bul"a basmak gerekmiyor.
        // Basarisiz olursa (henuz bir alanda degilsen) her 10 saniyede bir tekrar
        // deniyor - her tik denemek bosa bes saniyelik tarama demek olurdu.
        if (!this.everLocated && this.autoScan.Checked && DateTime.UtcNow >= this.nextAutoTry)
        {
            this.nextAutoTry = DateTime.UtcNow.AddSeconds(10);
            await this.LocateAsync(false);
        }
    }

    private DateTime nextAutoTry = DateTime.MinValue;
    private Color lineColor = Color.FromArgb(120, 170, 190);

    private void PickColor()
    {
        using var dialog = new ColorDialog { Color = this.lineColor, FullOpen = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) { return; }

        this.lineColor = dialog.Color;
        this.colorButton.BackColor = this.lineColor;
        this.ApplyOverlaySettings();
    }

    /// <summary>Overlay kapaliysa acar. Harita yuklenince kendiliginden cagriliyor.</summary>
    private void EnsureOverlay()
    {
        if (this.overlay is { IsDisposed: false }) { return; }
        this.ToggleOverlay();
    }

    // ------------------------------------------------------------ bulma

    /// <summary>
    ///     HaritayÄ± bulur. Tahmin yok:
    ///
    ///     1. Bellekte zemin yapÄ±larÄ± aranÄ±r - kendi iÃ§ tutarlÄ±lÄ±ÄŸÄ±ndan tanÄ±nÄ±rlar
    ///        (satÄ±r uzunluÄŸu dÃ¶ÅŸeme sayÄ±sÄ±yla, veri vektÃ¶rÃ¼nÃ¼n boyutu ikisiyle uyumlu).
    ///     2. Bulunanlardan hangisinin GÃœNCEL alan olduÄŸu, oyuncuya ulaÅŸabilen tek alan
    ///        nesnesi olmasÄ±ndan anlaÅŸÄ±lÄ±r - eskiler Ã¶nbellekte durur ama baÄŸlarÄ± kopmuÅŸtur.
    ///
    ///     Boyutlar, satÄ±r uzunluÄŸu ve veri adresi yapÄ±nÄ±n kendisinden okunuyor.
    /// </summary>
    private async Task LocateAsync(bool afterAreaChange)
    {
        if (this.scanning) { return; }
        this.scanning = true;
        this.locateButton.Enabled = false;
        this.status.Text = afterAreaChange
            ? "Alan degisti, harita araniyor..."
            : "Harita araniyor...  OYUNA GEC ve YURUMEYE BASLA.";

        try
        {
            var result = await Task.Run(() =>
            {
                var (handle, moduleBase) = OpenGame();
                try
                {
                    // Sabitleme tek bir yuruyusle hem oyuncuyu hem canli alani buluyor.
                    // Sabit zincir saklamiyoruz artik: modul tabanli olan oyun yeniden
                    // baslatilinca kiriliyor, alan tabanli olan da oyuncu varligindan
                    // sonrasinda bilesen sirasi her oturumda degistigi icin tutmuyor.
                    var pinned = Pin.Run(handle, s => this.BeginInvoke(() => this.status.Text = s));
                    var pos = Player.TryRead(handle, moduleBase);
                    return (Pos: pos, Live: pinned?.Area);
                }
                finally
                {
                    Native.CloseHandle(handle);
                }
            });

            this.player = result.Pos;

            if (result.Live is not { } t)
            {
                this.status.Text = "Oyuncu sabitlenemedi. \"Haritayi bul\"a bas ve " +
                                   "istendiginde bir kac saniye YURU.";
                return;
            }

            this.everLocated = true;
            this.bytesPerRow = t.BytesPerRow;
            this.LoadTerrain(new GridLocation(t.DataStart, t.DataLength, t.GridX, 0, 0, 0), t);
        }
        catch (Exception ex)
        {
            this.status.Text = "HATA: " + ex.Message;
        }
        finally
        {
            this.scanning = false;
            this.locateButton.Enabled = true;
        }
    }

    // ------------------------------------------------------------ olcek kalibrasyonu

    /// <summary>
    ///     DÃ¼nya biriminden hÃ¼creye Ã§evirme bÃ¶lenini yÃ¼rÃ¼yerek Ã¶lÃ§er.
    ///
    ///     Bu, projede kalan son belirsizlik: oyunun kendi kodunda bir dÃ¶ÅŸeme 250 birim ve
    ///     23 hÃ¼cre (10.8696), ama bizim bulduÄŸumuz konum float'larÄ± iÃ§in 10 daha iyi
    ///     oturuyor gibi gÃ¶rÃ¼nÃ¼yor. Tek bir noktadan ayÄ±rt edilemiyor - ikisi de haritanÄ±n
    ///     iÃ§ine dÃ¼ÅŸÃ¼yor. DoÄŸru bÃ¶len, gezilen BÃœTÃœN noktalarÄ± zeminde tutan tek bÃ¶lendir.
    /// </summary>
    private async Task CalibrateScaleAsync()
    {
        if (this.scanning || this.active is null)
        {
            this.status.Text = "Once \"Haritayi bul\"a bas.";
            return;
        }

        this.scanning = true;
        this.calibButton.Enabled = false;

        try
        {
            this.status.Text = "SIMDI YURU - 15 saniye durma...";
            await Task.Delay(300);

            var samples = await Task.Run(() =>
            {
                var (handle, moduleBase) = OpenGame();
                try
                {
                    var list = new List<PlayerPos>();
                    for (var i = 0; i < 15; i++)
                    {
                        if (Player.TryRead(handle, moduleBase) is { } p &&
                            (list.Count == 0 || Math.Abs(list[^1].X - p.X) > 20 || Math.Abs(list[^1].Y - p.Y) > 20))
                        {
                            list.Add(p);
                        }

                        Thread.Sleep(1000);
                    }

                    return list;
                }
                finally
                {
                    Native.CloseHandle(handle);
                }
            });

            if (samples.Count < 6)
            {
                this.status.Text = $"Yeterli farkli nokta yok ({samples.Count}). Daha genis dolasip tekrar dene.";
                return;
            }

            var loc = this.active;
            var raw = await Task.Run(() =>
            {
                var (handle, _) = OpenGame();
                try
                {
                    var b = new byte[loc.ByteLength];
                    Native.ReadProcessMemory(handle, (IntPtr)loc.Address, b, (IntPtr)loc.ByteLength, out _);
                    return b;
                }
                finally { Native.CloseHandle(handle); }
            });

            // Sadece noktalara bakmak ayirt etmiyor: acik alanda her olcek seni zeminde
            // gosterir. Onun icin noktalar ARASINDAKI yolu da siniyoruz - dar bir
            // koridorda yanlis olcek yolu duvarin icinden gecirir.
            var bestScore = -1.0;
            var results = new List<(float Divisor, double Score)>();

            for (var d = 8.5f; d <= 12.5f; d += 0.02f)
            {
                var allOn = true;
                foreach (var s in samples)
                {
                    if (!Walkable(raw, this.bytesPerRow, loc.Width, (int)(s.X / d), (int)(s.Y / d))) { allOn = false; break; }
                }

                if (!allOn) { results.Add((d, -1)); continue; }

                long total = 0, blocked = 0;
                for (var i = 1; i < samples.Count; i++)
                {
                    var ax = (int)(samples[i - 1].X / d);
                    var ay = (int)(samples[i - 1].Y / d);
                    var bx = (int)(samples[i].X / d);
                    var by = (int)(samples[i].Y / d);
                    var steps = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
                    if (steps <= 0 || steps > 500) { continue; }

                    for (var k = 0; k <= steps; k++)
                    {
                        total++;
                        if (!Walkable(raw, this.bytesPerRow, loc.Width, ax + (bx - ax) * k / steps, ay + (by - ay) * k / steps))
                        {
                            blocked++;
                        }
                    }
                }

                var score = total == 0 ? 0 : 1.0 - (double)blocked / total;
                results.Add((d, score));
                if (score > bestScore) { bestScore = score; }
            }

            if (bestScore <= 0)
            {
                this.calibNote = "Hicbir olcek butun noktalari zeminde tutmadi. Tekrar dene.";
                return;
            }

            var winners = results.Where(r => r.Score >= bestScore - 0.01).Select(r => r.Divisor).ToList();
            var lo = winners.Min();
            var hi = winners.Max();
            PlayerPos.UnitsPerCell = (lo + hi) / 2f;

            var sharp = hi - lo < 0.6f;
            this.info.Text = $"olcek {PlayerPos.UnitsPerCell:F4}  (aralik {lo:F2}-{hi:F2})";
            this.calibNote = $"{samples.Count} nokta, yolun %{bestScore * 100:F0} kadari zeminde. " +
                             (sharp ? "Olcum KESKIN." : "Aralik GENIS - dar koridorlarda tekrar dene.") +
                             $"  Oyunun kendi orani {PlayerPos.TileBased:F4}.";

            this.view.Invalidate();
        }
        catch (Exception ex)
        {
            this.status.Text = "HATA: " + ex.Message;
        }
        finally
        {
            this.scanning = false;
            this.calibButton.Enabled = true;
        }
    }

    // ------------------------------------------------------------ cizim

    private void LoadTerrain(GridLocation loc, TerrainFinder.Terrain t)
    {
        try
        {
            var (handle, moduleBase) = OpenGame();
            byte[] raw;
            try
            {
                this.player = Player.TryRead(handle, moduleBase);
                raw = new byte[loc.ByteLength];
                if (!Native.ReadProcessMemory(handle, (IntPtr)loc.Address, raw, (IntPtr)loc.ByteLength, out var got) ||
                    (long)got != loc.ByteLength)
                {
                    throw new InvalidOperationException("Harita verisi okunamadi.");
                }
            }
            finally
            {
                Native.CloseHandle(handle);
            }

            this.rawMap = raw;
            this.gridW = t.GridX;
            this.gridH = t.GridY;

            // Overlay kapaliysa kendiliginden acilsin - dugmeye basmak gerekmiyor.
            this.EnsureOverlay();
            this.overlay?.SetMap(raw, t.BytesPerRow, t.GridX, t.GridY);
            this.overlay?.SetArea(t.StructAddress - 0x8D0);

            this.view.Image?.Dispose();
            this.view.Image = Render(raw, t.BytesPerRow, t.GridX, t.GridY);
            this.active = loc;
            this.UpdateStatus($"dosem {t.TilesX}x{t.TilesY}, satirBayt {t.BytesPerRow}");
            this.view.Invalidate();
        }
        catch (Exception ex)
        {
            this.status.Text = "HATA: " + ex.Message;
        }
    }

    // ------------------------------------------------------------ overlay

    private void ToggleOverlay()
    {
        if (this.overlay is { IsDisposed: false })
        {
            this.overlay.Close();
            this.overlay = null;
            this.overlayButton.Text = "Overlay";
            return;
        }

        this.overlay = new OverlayForm();
        this.ApplyOverlaySettings();
        if (this.rawMap != null)
        {
            this.overlay.SetMap(this.rawMap, this.bytesPerRow, this.gridW, this.gridH);
        }

        this.overlay.Player = this.player;
        this.overlay.Show();
        this.overlayButton.Text = "Overlay kapat";
    }

    /// <summary>
    ///     Harita UI ogesini canli okur ve izdusumu Radar'in formuluyle hesaplar:
    ///
    ///         mapScale = 240 / (zoom * 0.187812)
    ///         cos = kosegen * cos(38.7) / mapScale
    ///         sin = kosegen * sin(38.7) / mapScale
    ///         merkez = boyut/2 + kaydirma + varsayilan kaydirma
    ///
    ///     Boylece olcek ve merkez tahmin edilmiyor, oyundan geliyor - ve oyuncu zoom
    ///     yapinca overlay de onunla beraber degisiyor.
    /// </summary>
    // ------------------------------------------------------------ kisayol tusu

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    private const int HotkeyId = 0xA17;
    private const int LeanDownId = 0xA18;
    private const int LeanUpId = 0xA19;
    private const uint VkF8 = 0x77;
    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotKey(this.Handle, HotkeyId, 0, VkF8);

        // Yaslanma katsayisi oyundan cikmadan ayarlanabilmeli: yaslanma sadece oyun
        // on plandayken calisiyor, dolayisiyla etkisini gormek icin oyunda olmak
        // gerekiyor. Kutuya gitmek icin oyundan cikmak kalibrasyonu imkansiz kiliyordu.
        RegisterHotKey(this.Handle, LeanDownId, 0, VkF9);
        RegisterHotKey(this.Handle, LeanUpId, 0, VkF10);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterHotKey(this.Handle, HotkeyId);
        UnregisterHotKey(this.Handle, LeanDownId);
        UnregisterHotKey(this.Handle, LeanUpId);
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        const int WmHotkey = 0x0312;
        if (m.Msg == WmHotkey)
        {
            switch (m.WParam.ToInt32())
            {
                case HotkeyId: this.AlignAtCursor(); break;
                case LeanDownId: this.NudgeLean(-0.05m); break;
                case LeanUpId: this.NudgeLean(+0.05m); break;
            }
        }

        base.WndProc(ref m);
    }

    /// <summary>Yaslanma katsayisini kaydirir ve degeri overlay uzerinde gosterir.</summary>
    private void NudgeLean(decimal step)
    {
        var next = Math.Clamp(this.leanBox.Value + step, this.leanBox.Minimum, this.leanBox.Maximum);
        this.leanBox.Value = next;
        this.overlay?.ShowNotice($"kaydirma hizi {next:F2}");
    }

    private void AlignAtCursor()
    {
        if (this.overlay is not { IsDisposed: false } ov)
        {
            this.status.Text = "Once Overlay'i ac.";
            return;
        }

        if (!GetCursorPos(out var cursor)) { return; }

        var result = ov.AlignToCursor(cursor);
        if (result is not { } r) { return; }

        this.shiftXBox.Value = Math.Clamp((decimal)r.Shift.X, this.shiftXBox.Minimum, this.shiftXBox.Maximum);
        this.shiftYBox.Value = Math.Clamp((decimal)r.Shift.Y, this.shiftYBox.Minimum, this.shiftYBox.Maximum);
        this.calibNote = $"F8: imlec ({r.Cursor.X:F0},{r.Cursor.Y:F0})  isaret ({r.Marker.X:F0},{r.Marker.Y:F0})  " +
                         $"kayma ({r.Shift.X:F0},{r.Shift.Y:F0})";
        this.UpdateStatus();
    }

    private void StartAlign()
    {
        if (this.overlay is not { IsDisposed: false } ov)
        {
            this.status.Text = "Once Overlay'i ac.";
            return;
        }

        ov.Aligned -= this.OnAligned;
        ov.Aligned += this.OnAligned;
        this.AlignAtCursor();
    }

    private void OnAligned(PointF click, PointF marker, PointF shift)
    {
        this.shiftXBox.Value = Math.Clamp((decimal)shift.X, this.shiftXBox.Minimum, this.shiftXBox.Maximum);
        this.shiftYBox.Value = Math.Clamp((decimal)shift.Y, this.shiftYBox.Minimum, this.shiftYBox.Maximum);
        // calibNote kalici: durum satiri her 400 ms yenileniyor ve dogrudan yazilan
        // mesaji eziyor.
        this.calibNote = $"tiklanan ({click.X:F0},{click.Y:F0})  isaret ({marker.X:F0},{marker.Y:F0})  " +
                         $"fark ({click.X - marker.X:F0},{click.Y - marker.Y:F0})  kayma ({shift.X:F0},{shift.Y:F0})";
        this.UpdateStatus();
    }

    private void RefreshLiveTransform(IntPtr handle)
    {
        if (this.overlay is not { IsDisposed: false } ov || !this.liveBox.Checked) { return; }

        if (this.mapElement == 0)
        {
            var found = UiFinder.FindLargeMap(handle);
            if (found is not { } f) { return; }
            this.mapElement = f.Address;
        }

        // Ogeyi bulmak pahali (bellek taramasi), okumak ucuz. Bu yuzden burada sadece
        // bir kez buluyoruz; degerleri overlay kendi dongusunde 60 Hz okuyor.
        ov.MapElement = this.mapElement;

        var el = UiFinder.Reread(handle, this.mapElement);

        // Kendi adresini tutmaya devam etmesi ogenin HALA harita oldugu anlamina
        // gelmiyor. Tab kapanip acildiginda ya da alan degistiginde oyun ogeyi
        // yeniden yaratiyor; eski adres bir sure daha okunabiliyor ama degerleri
        // donuyor. Sonuc: oyunun haritasi kayiyor, bizimki kaymiyor. O yuzden
        // ogenin harita OLDUGUNU da her seferinde siniyoruz.
        if (el is { } probe && !LooksLikeLargeMap(probe, ov.ClientSize))
        {
            el = null;
        }

        if (el is not { } e)
        {
            this.mapElement = 0;
            ov.MapElement = 0;
            this.liveNote = "harita ogesi bayatladi, yeniden araniyor";
            return;
        }

        this.info.Text = $"zoom {e.Zoom:F3}";

        // Merkezi burada da hesaplayip yaziyoruz: overlay ile ayni hesap, ama
        // gozle gorulebilir yerde. Ekrandaki isaretle tutmuyorsa fark buradan olculur.
        var cw = ov.ClientSize.Width;
        var ch = ov.ClientSize.Height;
        var c = UiFinder.MapCenter(handle, this.mapElement, cw, ch);

        // Yon tuslarindan biriken kaydirma - hiz kalibre edilirken gorunmesi lazim.
        var pan = ov.PanScreen;

        this.liveNote = c is { } center
            ? $"merkez ({center.X:F1}, {center.Y:F1})  kaydirma ({pan.X:F0}, {pan.Y:F0})  " +
              $"olcek[{e.ScaleIndex}]  kosegen {UiFinder.DiagonalLength(ch):F0}"
            : "";
    }

    /// <summary>
    ///     Bir ogenin hala buyuk harita olup olmadigi. FindLargeMap'in kullandigi
    ///     ayni olcutler: gorunur, tam ekran boyutunda, varsayilan kaydirmasi (0, -20).
    /// </summary>
    private static bool LooksLikeLargeMap(UiFinder.UiElement e, Size client) =>
        e.Visible &&
        e.SizeX > 800 && e.SizeY > 500 &&
        Math.Abs(e.DefShiftX) < 5f && Math.Abs(e.DefShiftY + 20f) < 5f &&
        e.Zoom is > 0.05f and < 20f &&
        (client.Width <= 0 || Math.Abs(e.SizeX - client.Width) < client.Width * 0.5f);

    private void ApplyOverlaySettings()
    {
        if (this.overlay is not { IsDisposed: false }) { return; }
        this.overlay.CellScale = (float)this.scaleBox.Value;
        this.overlay.CenterShift = new PointF((float)this.shiftXBox.Value, (float)this.shiftYBox.Value);
        this.overlay.CameraAngleDeg = (float)this.angleBox.Value;
        this.overlay.FlipY = this.flipBox.Checked;
        this.overlay.MapOffsetX = (float)this.mapDxBox.Value;
        this.overlay.MapOffsetY = (float)this.mapDyBox.Value;
        this.overlay.UseLive = this.liveBox.Checked;
        this.overlay.PanSpeed = (float)this.leanBox.Value;
        this.overlay.SetStyle(this.outlineBox.Checked, this.lineColor);
        this.overlay.Invalidate();
    }

    /// <summary>
    ///     Bir hucrenin yurunebilir olup olmadigi.
    ///
    ///     Satir adresi BytesPerRow uzerinden hesaplanmali: izgara genisligi TEK sayi
    ///     oldugunda satirin sonunda bir dolgu yarim bayti kaliyor. Duz dizi gibi okursak
    ///     her satirda bir hucre kayiyor ve harita kosegen kayiyor.
    /// </summary>
    private static bool Walkable(byte[] raw, int bytesPerRow, int width, int x, int y)
    {
        if (x < 0 || x >= width || y < 0) { return false; }
        var byteIndex = (long)y * bytesPerRow + x / 2;
        if (byteIndex < 0 || byteIndex >= raw.Length) { return false; }
        var value = x % 2 == 0 ? raw[byteIndex] & 0x0F : raw[byteIndex] >> 4;
        return value > 0;
    }

    private static byte[] Unpack(byte[] raw)
    {
        var cells = new byte[raw.Length * 2];
        for (var i = 0; i < raw.Length; i++)
        {
            cells[i * 2] = (byte)(raw[i] & 0x0F);
            cells[i * 2 + 1] = (byte)(raw[i] >> 4);
        }

        return cells;
    }

    private void UpdateStatus(string? extra = null)
    {
        if (this.active is null) { return; }
        var loc = this.active;

        var head = $"{loc.Width} x {loc.Height} hucre   |   {loc.Address:X}";
        if (extra != null) { head += "   |   " + extra; }

        this.status.Text = (this.player is { } p
            ? head + $"   |   konum ({p.X:F0}, {p.Y:F0})  ->  hucre ({p.CellX}, {p.CellY})"
            : head + "   |   konum okunamadi")
            + (this.liveNote.Length > 0 ? "     ||  " + this.liveNote : "")
            + (this.calibNote.Length > 0 ? "     ||  " + this.calibNote : "");
    }

    private void OnViewPaint(object? sender, PaintEventArgs e)
    {
        if (this.view.Image is null || this.active is null || this.player is null) { return; }

        var p = this.player.Value;
        var img = this.view.Image;
        if (p.CellX < 0 || p.CellX >= img.Width || p.CellY < 0 || p.CellY >= img.Height) { return; }

        var scale = Math.Min((float)this.view.Width / img.Width, (float)this.view.Height / img.Height);
        var originX = (this.view.Width - img.Width * scale) / 2f;
        var originY = (this.view.Height - img.Height * scale) / 2f;

        var sx = originX + p.CellX * scale;
        var sy = originY + p.CellY * scale;

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(255, 80, 60), 2f);
        e.Graphics.DrawEllipse(pen, sx - 7, sy - 7, 14, 14);
        e.Graphics.DrawLine(pen, sx - 12, sy, sx - 3, sy);
        e.Graphics.DrawLine(pen, sx + 3, sy, sx + 12, sy);
        e.Graphics.DrawLine(pen, sx, sy - 12, sx, sy - 3);
        e.Graphics.DrawLine(pen, sx, sy + 3, sx, sy + 12);
    }

    private static Bitmap Render(byte[] raw, int bytesPerRow, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

        try
        {
            var stride = data.Stride;
            var row = new byte[stride];

            for (var y = 0; y < height; y++)
            {
                Array.Clear(row);
                var baseIndex = (long)y * bytesPerRow;
                for (var x = 0; x < width; x++)
                {
                    var byteIndex = baseIndex + x / 2;
                    var v = byteIndex < raw.Length
                        ? (byte)(x % 2 == 0 ? raw[byteIndex] & 0x0F : raw[byteIndex] >> 4)
                        : (byte)0;
                    byte tone = v switch
                    {
                        0 => 14,
                        1 => 60,
                        2 => 85,
                        3 => 110,
                        4 => 140,
                        5 => 190,
                        _ => 230
                    };

                    var o = x * 3;
                    row[o] = tone;
                    row[o + 1] = tone;
                    row[o + 2] = tone;
                }

                Marshal.Copy(row, 0, data.Scan0 + y * stride, stride);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bmp;
    }
}

