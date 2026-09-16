using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Poe2Map;

/// <summary>
///     Haritayı oyunun kendi görüntüsünün üstüne çizen şeffaf pencere.
///
///     İzdüşüm klasik izometrik: ızgara eksenleri 45 derece döndürülüp dikeyde kamera
///     açısıyla basıklaştırılıyor. Hücre farkından ekran farkına:
///
///         ekranX = (dx - dy) * cos
///         ekranY = -(dx + dy) * sin
///
///     cos ve sin, kamera açısı (38.7 derece) ile ölçekten geliyor. Ölçeği ve merkezi
///     oyunun UI öğesinden okumak yerine ŞİMDİLİK elle ayarlıyoruz: karakterin kendisi
///     ekranda görünen bilinen bir nokta olduğu için, işaret onun üstüne oturana kadar
///     ayarlamak yeterli. Zoom yapınca yeniden ayar gerekir - onu canlı okumak sonraki iş.
/// </summary>
internal sealed partial class OverlayForm : Form
{
    private const int WsExLayered = 0x80000;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int GwlExStyle = -20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>
    ///     Kamera acisi. Radar 38.7 kullaniyor ama bizim izdusumumuz onunkiyle ayni
    ///     referansta olmayabilir, o yuzden ayarlanabilir: bu sayi dikey basikligi
    ///     belirliyor ve merkeze uzak noktalardaki kaymanin sebebi genelde bu oluyor.
    /// </summary>
    internal float CameraAngleDeg = 38.7f;

    /// <summary>Dikey ekseni ters cevirir - izdusum aynalanmissa.</summary>
    internal bool FlipY;

    /// <summary>
    ///     Yon tuslariyla kaydirma hizi, kare basina ekran pikseli.
    ///
    ///     Kaydirmayi oyundan OKUMUYORUZ, taklit ediyoruz. Okunacak bir yer yok:
    ///     123 bin UI ogesinin konum/kaydirma alanlari tarandi (temiz bir 2B kaydirma
    ///     cikmadi), harita ogesi kaydirilmisken ortaliyken okunanin birebir aynisini
    ///     veriyor, kamera odagi sandigimiz adresler kaydirilmisken de oyuncunun
    ///     konumunu okuyor, ve dunya-ekran matrisinin kalici bir kopyasi yok. Tek
    ///     gercek sinyal nesne-kamera MESAFELERIydi: kaydirinca ~25'ten ~4200'e
    ///     ciiyor. Yani kaydirma dunya kamerasini tasiyor ve o okunamiyor.
    ///
    ///     O yuzden tuslari kendimiz dinliyoruz ve ayni hizda kaydiriyoruz. Tab
    ///     sifirliyor - oyunun kendi davranisi da bu.
    /// </summary>
    internal float PanSpeed;

    /// <summary>Biriken kaydirma, ekran pikseli - durum satirinda gostermek icin.</summary>
    internal PointF PanScreen => this.panScreen;

    private PointF panScreen;
    private long panClock;
    private IntPtr gameWindow;

    // Oyundan cikmadan ayar yaparken degeri gorebilmek icin kisa sureli yazi.
    private string notice = "";
    private DateTime noticeUntil = DateTime.MinValue;

    /// <summary>
    ///     Overlay uzerinde gorunen kisa bir bilgi yazisi. Varsayilan iki saniye;
    ///     ayarsiz exe "birkac saniye yuru" gibi kalici mesajlar icin daha uzun
    ///     sure veriyor, bos metinle de siliyor.
    /// </summary>
    internal void ShowNotice(string text, double seconds = 2)
    {
        this.notice = text;
        this.noticeUntil = DateTime.UtcNow.AddSeconds(seconds);
        this.Invalidate();
    }

    /// <summary>
    ///     Acikken harita yalnizca oyunun buyuk haritasi (Tab) aciksa ciziliyor.
    ///
    ///     Izdusumun olcegi ve merkezi buyuk harita ogesinden geliyor; Tab kapaliyken
    ///     cizilen sekil dunyanin uzerinde yanlis olcekte yuzer. MapView'de kapali
    ///     (eski davranis korunuyor), ayarsiz exe'de acik.
    /// </summary>
    internal bool HideWhenMapClosed;

    private bool mapOpen = true;

    /// <summary>Oyunun buyuk haritasi su an acik mi (son okumaya gore) - gunluk icin.</summary>
    internal bool MapOpen => this.mapOpen;

    /// <summary>
    ///     Ayar paneli acikken harita, oyun on planda olmasa da gorunur kalir. Panel
    ///     on plana gectigi anda overlay gizlenirse simge boyutunu ayarlarken sonucu
    ///     gormek imkansiz olur.
    /// </summary>
    internal bool PanelOpen;

    /// <summary>
    ///     Oyun tutamacini birakir; bir sonraki okumada yeniden acilir.
    ///     Oyun kapanip yeniden acildiginda eski tutamac olu surece bakiyor.
    /// </summary>
    internal void ResetGameHandle()
    {
        if (this.handle != IntPtr.Zero)
        {
            Native.CloseHandle(this.handle);
            this.handle = IntPtr.Zero;
        }

        this.moduleBase = 0;
        this.MapElement = 0;
        this.Player = null;
    }

    /// <summary>
    ///     Haritayi isaretten BAGIMSIZ kaydirir, hucre cinsinden.
    ///
    ///     Isaret her zaman okudugumuz oyuncu hucresinde duruyor. Sekil zeminle
    ///     ortusurken isaret karakterin uzerinde degilse, okudugumuz konum ile
    ///     karakterin gercek hucresi arasinda sabit bir fark var demektir. Bu iki
    ///     sayi o farki OLCMEYE yariyor: sekli yerine oturtan deger, farkin ta kendisi.
    /// </summary>
    internal float MapOffsetX;
    internal float MapOffsetY;

    /// <summary>
    ///     Haritanin kaydirma miktari, dunya birimi cinsinden (odak eksi oyuncu).
    ///     Yon tuslariyla kaydirma ayri bir alana yazilmiyor, kameranin odagini
    ///     tasiyor; odagin kopyalarindan ortanca alinarak her karede okunuyor.
    /// </summary>
    internal float PanX;
    internal float PanY;

    /// <summary>
    ///     Oyunun harita UI ogesinden canli okunan izdusum. Acikken olcek, aci ve
    ///     merkez elle ayarlanmiyor - oyundan geliyor, dolayisiyla oyuncu zoom
    ///     yaptikca overlay de onunla beraber hareket ediyor.
    /// </summary>
    internal bool UseLive;
    internal float LiveCos;
    internal float LiveSin;
    internal PointF LiveCenter;

    /// <summary>
    ///     Harita merkezinin titremeye karsi olu bolgesi (ekran pikseli). Oyunun harita
    ///     paneli acilirken ya da uzerine gelince ebeveyn zincirindeki bir alan bir sure
    ///     kare kare 1-2 piksel oynayabiliyor; harita da tamamen bu merkeze cakili oldugu
    ///     icin cizgiler git-gel yapiyordu. Bu esigin altindaki oynamalar yok sayiliyor.
    ///
    ///     Gercek hareketi GECIRMIYOR: yurumeyi harita merkezden degil oyuncu konumundan
    ///     takip ediyor (bu deger degismiyor), yon-tusu kaydirmasi da kare basina esigin
    ///     ustunde (5.4 px) ilerledigi icin oldugu gibi gecip gidiyor. Yani gecikme yok.
    /// </summary>
    private const float CenterDeadZone = 2f;
    private PointF centerHeld;
    private bool centerHeldInit;

    private readonly System.Windows.Forms.Timer timer = new();
    private Bitmap? map;
    private int gridWidth;
    private int gridHeight;

    // Ozellik degil alan: WinForms cozumleyicisi Form uzerindeki ozelliklerden
    // tasarimci serilestirme bilgisi istiyor, bize gereksiz.
    internal PlayerPos? Player;

    /// <summary>Hücre başına ekran pikseli. Zoom değişince ayarlanması gereken sayı.</summary>
    internal float CellScale = 1.6f;

    /// <summary>Karakterin ekrandaki yeri, pencere ortasına göre kayma.</summary>
    internal PointF CenterShift = new(0, 0);

    internal OverlayForm()
    {
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = true;
        this.BackColor = Color.Black;
        this.TransparencyKey = Color.Black;
        this.StartPosition = FormStartPosition.Manual;
        this.DoubleBuffered = true;

        // 60 kare/saniyeye yakin: oyunla ayni tempoda guncellensin ki hareket ederken kaymasin.
        this.timer.Interval = 16;
        this.timer.Tick += (_, _) =>
        {
            this.ReadLive();
            this.TrackGameWindow();
            this.FollowGameFocus();
            this.Invalidate();
        };
        this.timer.Start();
    }

    // Kendi kalici tutamaci: konumu ve harita ogesini yuksek hizda kendisi okuyor.
    // Ana pencerenin 400 ms'lik dongusune baglanirsa saniyede 2.5 guncelleme kalir
    // ve oyun 60+ kare cizerken overlay gorunur sekilde geriden gelir.
    private IntPtr handle;
    private ulong moduleBase;
    internal ulong MapElement;

    /// <summary>
    ///     Hizalama kipi. Acikken overlay tiklamalari yakalar (normalde geciriyor).
    ///     Karakterin uzerine tiklayinca, isaretin oldugu yer ile tikladigin yer
    ///     arasindaki fark merkeze ekleniyor - yani goz karari degil, olculerek hizalanir.
    /// </summary>
    /// <summary>
    ///     Hizalama artik tiklamayla degil kisayol tusuyla yapiliyor: saydamlik anahtariyla
    ///     calisan bir pencerede yari saydam bir katman kurulamiyor (piksel ya tam saydam ya
    ///     tam opak), opak katman da oyunu tamamen kapatiyordu.
    /// </summary>
    internal void SetCalibrateMode(bool on)
    {
        this.calibrating = on;
        this.Invalidate();
    }

    private bool calibrating;

    internal bool CalibratingNow => this.calibrating;
    private PointF lastCenter;
    private Point mouseAt;

    /// <summary>Tiklanan nokta, isaretin oldugu nokta, yeni kayma.</summary>
    internal event Action<PointF, PointF, PointF>? Aligned;

    private void SetClickThrough(bool on)
    {
        if (!this.IsHandleCreated) { return; }
        var ex = GetWindowLong(this.Handle, GwlExStyle);
        ex = on ? ex | WsExTransparent : ex & ~WsExTransparent;
        SetWindowLong(this.Handle, GwlExStyle, ex);
    }

    /// <summary>
    ///     Imlecin bulundugu noktayi karakterin yeri kabul edip merkezi oraya tasir.
    ///     Ekran koordinati overlay'in kendi istemci koordinatina cevriliyor.
    /// </summary>
    internal (PointF Cursor, PointF Marker, PointF Shift)? AlignToCursor(Point screenPoint)
    {
        var client = new PointF(screenPoint.X - this.Bounds.Left, screenPoint.Y - this.Bounds.Top);
        var marker = this.lastCenter;
        this.CenterShift = new PointF(
            this.CenterShift.X + (client.X - marker.X),
            this.CenterShift.Y + (client.Y - marker.Y));
        this.Invalidate();
        return (client, marker, this.CenterShift);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!this.calibrating) { return; }

        // Uygulamanin gordugu imlec konumu. Cizilen yesil arti gercek imlecin altinda
        // durmuyorsa koordinat duzlemimiz oyununkiyle ayni degil demektir.
        this.mouseAt = e.Location;
        this.Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!this.calibrating) { return; }

        // Tiklanan nokta karakterin gercek yeri; isaret oradan ne kadar sapmissa
        // merkeze o kadar ekliyoruz.
        var click = new PointF(e.X, e.Y);
        var marker = this.lastCenter;
        var dx = click.X - marker.X;
        var dy = click.Y - marker.Y;
        this.CenterShift = new PointF(this.CenterShift.X + dx, this.CenterShift.Y + dy);
        // Kip acik kalsin: art arda tiklayarak yaklastirmak, tek atista tutturmaktan kolay.
        this.Aligned?.Invoke(click, marker, this.CenterShift);
    }

    private void EnsureHandle()
    {
        if (this.handle != IntPtr.Zero) { return; }

        var game = Native.FindGame();
        if (game == null) { return; }

        this.handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        try { this.moduleBase = (ulong)(game.MainModule?.BaseAddress.ToInt64() ?? 0); }
        catch { this.moduleBase = 0; }
    }

    private void ReadLive()
    {
        this.EnsureHandle();
        if (this.handle == IntPtr.Zero) { return; }

        // Tam nitelikli: bu sinifta "Player" adinda bir alan da var, o sinifi golgeliyor.
        var p = global::Poe2Map.Player.TryRead(this.handle, this.moduleBase);
        if (p is not null) { this.Player = p; }

        // Dunya birimi cinsinden kaydirma artik kullanilmiyor: kaydirmayi oyundan
        // okuyamadigimiz icin yon tuslarindan taklit ediyoruz ve o ekran pikseli
        // cinsinden merkeze uygulaniyor (KeyboardPan).
        this.PanX = 0f;
        this.PanY = 0f;

        if (!this.UseLive || this.MapElement == 0)
        {
            this.mapOpen = false;
            return;
        }

        var el = UiFinder.Reread(this.handle, this.MapElement);
        if (el is not { } e)
        {
            this.mapOpen = false;
            return;
        }

        this.mapOpen = e.Visible;

        // Overlay penceresi oyunun istemci alanina birebir oturtuluyor, dolayisiyla
        // bizim ClientSize degerimiz oyunun kendi hesabinda kullandigi olcu.
        var clientW = this.ClientSize.Width;
        var clientH = this.ClientSize.Height;
        if (clientW <= 0 || clientH <= 0) { return; }

        const float ScaleBaseline = 0.187812f;
        var angle = 38.7 * Math.PI / 180;

        // Kosegen ogenin kendi boyutundan DEGIL, oyunun taban cozunurlugundan ve
        // pencere YUKSEKLIGINDEN geliyor. Genislik degistiginde harita olcegi
        // degismiyor - onceki hesabimiz bu yuzden tutmuyordu.
        var diagonal = UiFinder.DiagonalLength(clientH);
        var scale = e.Zoom * ScaleBaseline;
        if (scale <= 0) { return; }

        var mapScale = 240.0 / scale;
        this.LiveCos = (float)(diagonal * Math.Cos(angle) / mapScale);
        this.LiveSin = (float)(diagonal * Math.Sin(angle) / mapScale);

        // Merkez artik tahmin edilmiyor: ogenin ebeveyn zinciri yurunup oyunun
        // kendi konum hesabi tekrarlaniyor, ustune ogenin yarim boyu ve kaydirmalar.
        var computed = UiFinder.MapCenter(this.handle, this.MapElement, clientW, clientH);
        if (computed is not { } c) { return; }

        var pan = this.KeyboardPan();
        this.LiveCenter = this.StabilizeCenter(new PointF(c.X + pan.X, c.Y + pan.Y));
    }

    /// <summary>
    ///     Merkezi eksen basina tutar: yeni deger eskisinden esikten fazla ayrilmadikca
    ///     eski deger korunuyor (kucuk titremeler yutulur), esigi asinca aynen atlanir
    ///     (gercek harekette gecikme olmaz). Alan degisince ilk buyuk fark zaten esigi
    ///     astigi icin yeni merkeze kendiliginden oturuyor - ayrica sifirlama gerekmiyor.
    /// </summary>
    private PointF StabilizeCenter(PointF raw)
    {
        if (!this.centerHeldInit)
        {
            this.centerHeldInit = true;
            this.centerHeld = raw;
            return raw;
        }

        var x = Math.Abs(raw.X - this.centerHeld.X) > CenterDeadZone ? raw.X : this.centerHeld.X;
        var y = Math.Abs(raw.Y - this.centerHeld.Y) > CenterDeadZone ? raw.Y : this.centerHeld.Y;
        this.centerHeld = new PointF(x, y);
        return this.centerHeld;
    }

    /// <summary>
    ///     Yon tuslarindan biriktirilen kaydirma, ekran pikseli cinsinden.
    ///
    ///     Sadece oyun on plandayken sayiyor - baska pencerede yazarken harita
    ///     kaymasin. Tab sifirliyor, oyunun kendisi de haritayi kapatip acinca
    ///     kaydirmayi sifirliyor.
    /// </summary>
    private PointF KeyboardPan()
    {
        if (this.PanSpeed == 0 || this.gameWindow == IntPtr.Zero ||
            GetForegroundWindow() != this.gameWindow)
        {
            // Saati sifirla: oyuna geri donuldugunde aradaki butun sure tek adimda
            // kaydirma olarak uygulanmasin.
            this.panClock = 0;
            return this.panScreen;
        }

        const int VkTab = 0x09;
        const int VkLeft = 0x25;
        const int VkUp = 0x26;
        const int VkRight = 0x27;
        const int VkDown = 0x28;

        if (Down(VkTab)) { this.panScreen = PointF.Empty; return this.panScreen; }

        // Kaydirma KARE basina degil GECEN SUREYE bagli. Zamanlayici 16 ms istiyor
        // ama Windows'ta granulerlik ~15.6 ms ve ara sira geciktiriyor; kare basina
        // sabit ekleyince her adimda kucuk bir hata birikiyor ve uzun kaydirmada
        // gozle gorulur kaymaya donusuyor.
        //
        // Hizin anlami degismesin diye 60 kare/sn'ye normalize ediyoruz: kutudaki
        // sayi hala "60 fps'de kare basina piksel" demek.
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var dt = this.panClock == 0
            ? 0.0
            : (now - this.panClock) / (double)System.Diagnostics.Stopwatch.Frequency;
        this.panClock = now;

        // Uygulama takilip kalirsa tek adimda uc metre kaydirmasin.
        if (dt > 0.2) { dt = 0.2; }

        var step = (float)(this.PanSpeed * dt * 60.0);

        var dx = 0f;
        var dy = 0f;
        if (Down(VkLeft)) { dx += step; }
        if (Down(VkRight)) { dx -= step; }
        if (Down(VkUp)) { dy += step; }
        if (Down(VkDown)) { dy -= step; }

        this.panScreen = new PointF(this.panScreen.X + dx, this.panScreen.Y + dy);
        return this.panScreen;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    protected override void Dispose(bool disposing)
    {
        if (disposing && this.handle != IntPtr.Zero)
        {
            Native.CloseHandle(this.handle);
            this.handle = IntPtr.Zero;
        }

        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var ex = GetWindowLong(this.Handle, GwlExStyle);
        SetWindowLong(this.Handle, GwlExStyle, ex | WsExLayered | WsExTransparent | WsExToolWindow);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow;
            return cp;
        }
    }

    /// <summary>
    ///     Izgarayı, sıfır olmayan hücreleri yarı saydam çizecek şekilde hazırlar.
    ///     Sıfırlar tamamen saydam kalıyor ki oyunun görüntüsü görünsün.
    /// </summary>
    internal void SetMap(byte[] raw, int bytesPerRow, int width, int height)
    {
        // Ham veriyi saklıyoruz: cizgi/dolgu secimi ya da renk degisince haritayi
        // yeniden istemek yerine buradan tekrar cizeceğiz.
        this.raw = raw;
        this.rawBytesPerRow = bytesPerRow;
        this.gridWidth = width;
        this.gridHeight = height;
        this.wallDistance = null;
        this.RebuildMap();
    }

    /// <summary>
    ///     Cizim kipi, renk ve cizgi kalinligi (hucre). Ozellik degil yontem: WinForms
    ///     cozumleyicisi Form uzerindeki ozelliklerden tasarimci serilestirme bilgisi istiyor.
    /// </summary>
    internal void SetStyle(bool onlyOutline, Color color, int thickness = 1)
    {
        thickness = Math.Clamp(thickness, 1, MaxThickness);
        if (this.outlineOnly == onlyOutline && this.lineColor == color && this.lineThickness == thickness)
        {
            return;
        }

        this.outlineOnly = onlyOutline;
        this.lineColor = color;
        this.lineThickness = thickness;
        this.RebuildMap();
    }

    internal const int MaxThickness = 8;

    private bool outlineOnly = true;
    private Color lineColor = Color.FromArgb(120, 170, 190);
    private int lineThickness = 1;
    private byte[]? raw;
    private int rawBytesPerRow;

    /// <summary>
    ///     Her hucrenin en yakin yurunemez hucreye uzakligi (hucre, MaxThickness'ta
    ///     kirpilmis). Harita basina bir kez hesaplaniyor; kalinlik degisince yeniden
    ///     hesaplamaya gerek yok, sadece bitmap yeniden uretiliyor.
    /// </summary>
    private byte[]? wallDistance;

    /// <summary>
    ///     Izgaradan bitmap uretir.
    ///
    ///     Cizgi kipinde bir hucre, yurunebilir olup en yakin duvara uzakligi kalinlik
    ///     kadar ya da daha az ise ciziliyor. Kalinlik 1 = sadece duvara degen hucreler,
    ///     yani onceki tek hucrelik cizgi. Odalarin ici bos kaliyor. Izgara kenari da
    ///     duvar sayiliyor.
    ///
    ///     Neden kalinlik: zoom 0.5'te bir hucre ekranda bir pikselden kucuk, tek
    ///     hucrelik cizgi en yakin komsu ornekleme yuzunden ince ve kesik gorunuyor.
    /// </summary>
    private void RebuildMap()
    {
        if (this.raw is null || this.gridWidth <= 0 || this.gridHeight <= 0) { return; }

        var width = this.gridWidth;
        var height = this.gridHeight;
        this.wallDistance ??= this.ComputeWallDistance();
        var distance = this.wallDistance;

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var row = new byte[data.Stride];
            for (var y = 0; y < height; y++)
            {
                Array.Clear(row);
                var line = y * width;
                for (var x = 0; x < width; x++)
                {
                    var d = distance[line + x];
                    if (d == 0) { continue; }

                    byte alpha;
                    if (this.outlineOnly)
                    {
                        if (d > this.lineThickness) { continue; }
                        alpha = 235;
                    }
                    else
                    {
                        alpha = (byte)(this.Cell(x, y) >= 5 ? 90 : 55);
                    }

                    var o = x * 4;
                    row[o] = this.lineColor.B;
                    row[o + 1] = this.lineColor.G;
                    row[o + 2] = this.lineColor.R;
                    row[o + 3] = alpha;
                }

                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        this.map?.Dispose();
        this.map = bmp;
        this.Invalidate();
    }

    /// <summary>
    ///     Iki gecisli satranc tahtasi uzaklik donusumu: 0 = yurunemez, n = en yakin
    ///     yurunemez hucreye n adim (MaxThickness'ta kirpilir). Dogrusal zamanda;
    ///     her hucre icin pencere taramak kalinlik 3'te hucre basina 49 okuma olurdu.
    /// </summary>
    private byte[] ComputeWallDistance()
    {
        var width = this.gridWidth;
        var height = this.gridHeight;
        var dist = new byte[(long)width * height];
        const byte Far = MaxThickness + 1;

        for (var y = 0; y < height; y++)
        {
            var line = y * width;
            for (var x = 0; x < width; x++)
            {
                dist[line + x] = this.Cell(x, y) == 0 ? (byte)0 : Far;
            }
        }

        // Izgara disi duvar: kenar hucreleri en fazla 1 olabilir.
        byte Get(int x, int y) => x < 0 || y < 0 || x >= width || y >= height ? (byte)0 : dist[(y * width) + x];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (dist[i] == 0) { continue; }
                var m = Math.Min(Get(x - 1, y), Math.Min(Get(x, y - 1), Math.Min(Get(x - 1, y - 1), Get(x + 1, y - 1))));
                dist[i] = (byte)Math.Min(dist[i], m + 1);
            }
        }

        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = width - 1; x >= 0; x--)
            {
                var i = (y * width) + x;
                if (dist[i] == 0) { continue; }
                var m = Math.Min(Get(x + 1, y), Math.Min(Get(x, y + 1), Math.Min(Get(x + 1, y + 1), Get(x - 1, y + 1))));
                dist[i] = (byte)Math.Min(dist[i], m + 1);
            }
        }

        return dist;
    }

    /// <summary>
    ///     Hucre degeri; izgara disi 0 sayiliyor. Tek sayi genislikli izgaralarda
    ///     satir basina bir dolgu yarim bayti var, o yuzden y * satirBayt + x / 2.
    /// </summary>
    private int Cell(int x, int y)
    {
        if (this.raw is null) { return 0; }
        if (x < 0 || y < 0 || x >= this.gridWidth || y >= this.gridHeight) { return 0; }

        var index = ((long)y * this.rawBytesPerRow) + (x / 2);
        if (index < 0 || index >= this.raw.Length) { return 0; }
        return x % 2 == 0 ? this.raw[index] & 0x0F : this.raw[index] >> 4;
    }

    /// <summary>
    ///     Harita yalnizca oyun on plandayken gorunsun. Alt+Tab ile baska bir
    ///     pencereye gecildiginde overlay onunde durmasin.
    ///
    ///     Kalibrasyon kipinde gizlemiyoruz: o sirada MapView on planda oluyor ve
    ///     harita gorunmezse ayar yapilamaz.
    /// </summary>
    private void FollowGameFocus()
    {
        var shouldShow = this.calibrating || this.PanelOpen ||
                         (this.gameWindow != IntPtr.Zero && GetForegroundWindow() == this.gameWindow);

        if (this.Visible != shouldShow) { this.Visible = shouldShow; }
    }

    private void TrackGameWindow()
    {
        try
        {
            var game = Native.FindGame();
            if (game == null || game.MainWindowHandle == IntPtr.Zero) { return; }
            this.gameWindow = game.MainWindowHandle;

            // Pencere degil ISTEMCI alani: pencere dikdortgeni baslik cubugunu ve kenarligi
            // da kapsiyor, oysa oyun sadece istemci alanina ciziyor. Pencereye gore
            // hizalarsak her sey baslik yuksekligi kadar asagi kayiyor.
            if (!GetClientRect(game.MainWindowHandle, out var c)) { return; }
            var origin = new Point(0, 0);
            if (!ClientToScreen(game.MainWindowHandle, ref origin)) { return; }

            var bounds = new Rectangle(origin.X, origin.Y, c.Right - c.Left, c.Bottom - c.Top);
            if (this.Bounds != bounds) { this.Bounds = bounds; }
        }
        catch
        {
            // oyun kapandiysa sessizce gec
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            this.PaintOverlay(e);
            this.PaintNotice(e);
        }
        catch
        {
            // Gecici bir bozuk deger uygulamayi cokertmesin; sonraki karede duzelir.
        }
    }

    private void PaintNotice(PaintEventArgs e)
    {
        if (this.notice.Length == 0 || DateTime.UtcNow > this.noticeUntil) { return; }

        using var font = new Font(this.Font.FontFamily, 16f, FontStyle.Bold);
        using var brush = new SolidBrush(Color.FromArgb(255, 240, 120));
        e.Graphics.DrawString(this.notice, font, brush, 24f, 24f);
    }

    private void PaintOverlay(PaintEventArgs e)
    {
        if (this.map is null || this.Player is not { } p) { return; }
        if (this.HideWhenMapClosed && !this.mapOpen) { return; }

        float cos, sin;
        PointF center;

        if (this.UseLive)
        {
            cos = this.LiveCos;
            sin = this.LiveSin * (this.FlipY ? -1f : 1f);
            center = new PointF(this.LiveCenter.X + this.CenterShift.X, this.LiveCenter.Y + this.CenterShift.Y);
        }
        else
        {
            var angle = this.CameraAngleDeg * Math.PI / 180;
            cos = (float)Math.Cos(angle) * this.CellScale;
            sin = (float)Math.Sin(angle) * this.CellScale * (this.FlipY ? -1f : 1f);
            center = new PointF(
                this.ClientSize.Width / 2f + this.CenterShift.X,
                this.ClientSize.Height / 2f + this.CenterShift.Y);
        }

        // Canli degerler henuz okunmadiysa cos/sin sifir kalir; sifir matrisi GDI+
        // tarafindan reddediliyor ("Parameter is not valid") ve cizim hatasi tum
        // pencereyi kirmizi carpiya cevirir. Hazir olmadan cizmiyoruz.
        if (Math.Abs(cos) < 1e-4f || Math.Abs(sin) < 1e-4f) { return; }

        // Hucre -> ekran: (dx - dy) * cos , -(dx + dy) * sin
        var m = new Matrix(cos, -sin, -cos, -sin, 0, 0);

        // Ekran merkezine gelen sey oyuncu DEGIL, kameranin odagi: oyun buyuk haritayi
        // odaga gore ciziyor ve yon tuslariyla kaydirmak odagi tasiyor. Harita ortaliyken
        // odak oyuncunun ustunde oldugu icin ikisi ayni yere denk geliyor.
        this.lastCenter = center;
        var anchorX = (p.X + this.PanX) / PlayerPos.UnitsPerCell;
        var anchorY = (p.Y + this.PanY) / PlayerPos.UnitsPerCell;
        var pts = new[] { new PointF(anchorX + this.MapOffsetX, anchorY + this.MapOffsetY) };
        m.TransformPoints(pts);
        m.Translate(center.X - pts[0].X, center.Y - pts[0].Y, MatrixOrder.Append);

        // Oyuncunun kendi yeri: kaydirma varken merkezden ayrilmasi gerekiyor.
        var playerPts = new[] { new PointF(p.CellX + this.MapOffsetX, p.CellY + this.MapOffsetY) };
        m.TransformPoints(playerPts);
        var playerAt = playerPts[0];

        e.Graphics.SmoothingMode = SmoothingMode.None;
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;

        var saved = e.Graphics.Transform;
        e.Graphics.Transform = m;
        e.Graphics.DrawImage(this.map, 0, 0, this.gridWidth, this.gridHeight);
        e.Graphics.Transform = saved;

        // Cikis yollari haritanin ustune (OverlayForm.Paths.cs), canavarlar onlarin
        // ustune (OverlayForm.Monsters.cs), oyuncu isareti en uste.
        this.PaintPaths(e, m, p);
        this.PaintMonsters(e, m);

            if (this.calibrating)
            {
                using var green = new Pen(Color.FromArgb(255, 90, 255, 120), 2f);
                e.Graphics.DrawLine(green, this.mouseAt.X - 26, this.mouseAt.Y, this.mouseAt.X + 26, this.mouseAt.Y);
                e.Graphics.DrawLine(green, this.mouseAt.X, this.mouseAt.Y - 26, this.mouseAt.X, this.mouseAt.Y + 26);
                e.Graphics.DrawString($"gordugum imlec: {this.mouseAt.X}, {this.mouseAt.Y}",
                    this.Font, Brushes.LightGreen, this.mouseAt.X + 12, this.mouseAt.Y + 12);
            }

        // Oyuncu isareti. Merkeze degil oyuncunun kendi yerine ciziliyor: harita
        // kaydirildiginda oyuncu merkezde kalmiyor, oyunun kendi haritasinda da oyle.
        using var pen = new Pen(Color.FromArgb(220, 255, 90, 60), 2f);
        e.Graphics.DrawEllipse(pen, playerAt.X - 8, playerAt.Y - 8, 16, 16);
        e.Graphics.DrawLine(pen, playerAt.X - 14, playerAt.Y, playerAt.X - 4, playerAt.Y);
        e.Graphics.DrawLine(pen, playerAt.X + 4, playerAt.Y, playerAt.X + 14, playerAt.Y);
        e.Graphics.DrawLine(pen, playerAt.X, playerAt.Y - 14, playerAt.X, playerAt.Y - 4);
        e.Graphics.DrawLine(pen, playerAt.X, playerAt.Y + 4, playerAt.X, playerAt.Y + 14);
    }
}
