namespace Poe2Map;

/// <summary>
///     Ayarsiz haritanin denetleyicisi. Pencere yok; saniyede bir su soruları soruyor:
///
///       - oyun acik mi, ayni surec mi (yeniden baslatildiysa her sey sifirlanir)
///       - harita yuklu mu (degilse oyuncuyu sabitleyip zemini yukle)
///       - alan degisti mi (degistiyse yeniden yukle)
///       - buyuk harita ogesi biliniyor mu (bilinmiyorsa bul)
///
///     Pahali isler (bellek taramalari, yuruyus beklemesi) arka planda yapiliyor;
///     overlay her zaman arayuz is parcaciginda guncelleniyor.
/// </summary>
internal sealed partial class OverlayContext : ApplicationContext
{
    // MapView'de kalibre edilen deger.
    private const float PanSpeed = 5.4f;

    private readonly OverlaySettings settings = OverlaySettings.Load();
    private SettingsForm? settingsForm;
    private string lastState = "";
    private DateTime nextStateLog = DateTime.MinValue;

    /// <summary>
    ///     Iki okuma arasinda bu kadar yol alinamaz: teleport, waypoint ya da alan
    ///     degisimi demek. Hareket becerileriyle bir saniyede alinabilecek yolun
    ///     ustunde tutuldu.
    /// </summary>
    private const float AreaJump = 2500f;

    /// <summary>
    ///     Oyuncunun konumu arka arkaya bu kadar okumada yuklu zeminde yurunebilir
    ///     degilse alan degismis sayiliyor. Atlama testi kacirirsa bu yakalar: yeni
    ///     alanin dogma noktasi eski konuma yakin olabilir, ama eski izgarada
    ///     yurunebilir bir hucreye denk gelmesi pek olasi degil.
    /// </summary>
    private const int StrikesForAreaChange = 3;

    private readonly OverlayForm overlay = new();
    private readonly NotifyIcon tray = new();
    private readonly System.Windows.Forms.Timer timer = new();

    private IntPtr handle;
    private ulong moduleBase;
    private int gamePid;

    private TerrainFinder.Terrain? terrain;
    private PlayerPos? lastPos;
    private int strikes;
    private int failures;
    private DateTime nextGameCheck = DateTime.MinValue;

    /// <summary>
    ///     Bilinen alan yapilari. Eski olsalar da + 0x5D0'da guncel oyuncuyu gosteriyorlar;
    ///     oyuncu varligi degisse bile yeni alana taramasiz buradan ulasiliyor.
    /// </summary>
    private readonly List<ulong> knownAreas = new();
    private bool busy;
    private DateTime nextLocate = DateTime.MinValue;
    private DateTime nextElementSearch = DateTime.MinValue;

    internal OverlayContext()
    {
        this.overlay.UseLive = true;
        this.overlay.HideWhenMapClosed = true;
        this.overlay.PanSpeed = PanSpeed;
        this.ApplySettings();
        this.overlay.Show();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Ayarlar", null, (_, _) => this.OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Kapat", null, (_, _) => this.ExitThread());

        this.tray.Icon = SystemIcons.Application;
        this.tray.Text = "PoE2 Harita";
        this.tray.ContextMenuStrip = menu;
        this.tray.DoubleClick += (_, _) => this.OpenSettings();
        this.tray.Visible = true;

        // 250 ms: alan degisimi artik tek bir isaretci okumasiyla algilaniyor, sik bakmak ucuz.
        this.timer.Interval = 250;
        this.timer.Tick += (_, _) => this.Tick();
        this.timer.Start();
    }

    private void ApplySettings()
    {
        this.overlay.SetStyle(true, this.settings.LineColor, this.settings.Thickness);
        this.overlay.SetMonsterStyle(this.settings.MonsterVisible, this.settings.MonsterSize);
    }

    private void OpenSettings()
    {
        if (this.settingsForm is { IsDisposed: false })
        {
            this.settingsForm.Activate();
            return;
        }

        this.settingsForm = new SettingsForm(this.settings, this.ApplySettings);

        // Panel on plana gecince oyun arkada kaliyor; harita gizlenirse yaptigin
        // ayarin sonucu gorulmez.
        this.overlay.PanelOpen = true;
        this.settingsForm.FormClosed += (_, _) => this.overlay.PanelOpen = false;
        this.settingsForm.Show();
    }

    /// <summary>
    ///     Durumu gunluge yazar, sadece degistiginde ve en fazla bes saniyede bir.
    ///     Pencere olmadigi icin "harita takip etmiyor" gibi bir sorunda uygulamanin
    ///     ne gordugunu gormenin tek yolu bu.
    /// </summary>
    private void LogState(string note = "")
    {
        var overlayPos = this.overlay.Player is { } op ? $"({op.X:F0}, {op.Y:F0})" : "yok";
        var area = this.terrain is { } t ? $"{t.GridX}x{t.GridY}" : "yok";

        // Shift merkeze eklenmiyor ama degeri gunlukte duruyor: bir gun yeniden
        // hizalama bozulursa ilk bakilacak yer.
        var shift = this.handle != IntPtr.Zero && UiFinder.Reread(this.handle, this.overlay.MapElement) is { } el
            ? $"({el.ShiftX:F0}, {el.ShiftY:F0})"
            : "-";
        var source = LocalPlayer.Current is not null ? "varlik" : $"kopya {LivePosition.Count}";

        var state = $"kaynak {source}  overlay konumu {overlayPos}  alan {area}  " +
                    $"oge 0x{this.overlay.MapElement:X}  shift {shift}  tab {(this.overlay.MapOpen ? "acik" : "kapali")}  " +
                    $"gorunur {this.overlay.Visible}";

        if (note.Length > 0)
        {
            CrashLog.Write(note + "  |  " + state, null);
            this.lastState = state;
            return;
        }

        if (state == this.lastState || DateTime.UtcNow < this.nextStateLog) { return; }
        this.lastState = state;
        this.nextStateLog = DateTime.UtcNow.AddSeconds(5);
        CrashLog.Write("durum  |  " + state, null);
    }

    protected override void ExitThreadCore()
    {
        this.timer.Stop();

        // Kapatirken paneli kendimiz kapatiyoruz ki ayarlar diske yazilsin: mesaj
        // dongusu bitince FormClosed artik calismiyor.
        if (this.settingsForm is { IsDisposed: false }) { this.settingsForm.Close(); }

        this.tray.Visible = false;
        this.tray.Dispose();
        this.overlay.Close();
        this.CloseOwnHandle();
        base.ExitThreadCore();
    }

    private void Tick()
    {
        if (this.busy) { return; }

        // Surec listesini dolasmak pahali; tik 250 ms'ye inince her tikte yapilmasin.
        if (DateTime.UtcNow >= this.nextGameCheck)
        {
            this.nextGameCheck = DateTime.UtcNow.AddSeconds(2);
            var game = Native.FindGame();
            if (game is null)
            {
                this.SetTray("oyun bekleniyor");
                return;
            }

            if (game.Id != this.gamePid) { this.AttachTo(game); }
        }

        if (this.handle == IntPtr.Zero) { return; }

        if (this.terrain is not { } current)
        {
            if (DateTime.UtcNow >= this.nextLocate) { this.StartLocate(); }
            return;
        }

        if (this.AreaChanged(current))
        {
            this.LogState("alan degisti");
            this.overlay.SetArea(0);
            this.terrain = null;
            this.lastPos = null;
            this.strikes = 0;
            this.StartLocate();
            return;
        }

        this.EnsureElement();
        this.LogState();
    }

    /// <summary>Yeni bir oyun sureci (ilk calisma ya da oyun yeniden baslatildi).</summary>
    private void AttachTo(System.Diagnostics.Process game)
    {
        this.CloseOwnHandle();
        this.overlay.ResetGameHandle();
        this.overlay.SetArea(0);

        this.handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        try { this.moduleBase = (ulong)(game.MainModule?.BaseAddress.ToInt64() ?? 0); }
        catch { this.moduleBase = 0; }

        this.gamePid = game.Id;
        lock (this.knownAreas) { this.knownAreas.Clear(); }
        LocalPlayer.Current = null;
        this.terrain = null;
        this.lastPos = null;
        this.strikes = 0;
        this.failures = 0;
        this.nextLocate = DateTime.MinValue;

        // Sabitlenmis adres surece ozel. Ayni oyun oturumunda daha once sabitlendiyse
        // aday olarak veriyoruz; baska bir surecten kaldiysa hic kullanmiyoruz.
        //
        // Aday, kabul degil: Pin.Run adresi artik HAREKETLE siniyor. Eskiden sadece
        // "makul ve yurunebilir mi" diye bakiyordu; alan degisiminden sonra olmus bir
        // kopya bu sinavi gecti ve harita iki dakika boyunca ayni konumda dondu.
        PlayerChain.Load();
        var seed = PlayerChain.PinnedPid == game.Id ? PlayerChain.PinnedAddress : 0;
        LiveArea.LastPositionAddress = seed;
        if (seed != 0) { LivePosition.Set(new[] { seed }); } else { LivePosition.Clear(); }
        this.LogState($"oyuna baglandi, surec {game.Id}, kayitli adres " +
                      (LiveArea.LastPositionAddress != 0 ? "kullaniliyor" : "yok"));
    }

    private bool AreaChanged(TerrainFinder.Terrain current)
    {
        // Birincil algilayici: oyuncu varliginin gosterdigi alan (varlik + 0x78). Tek okuma,
        // tahmin yok, yukleme ekrani sirasinda uc saniye beklemek de yok.
        if (LocalPlayer.Current is { } tracked)
        {
            var linked = LocalPlayer.CurrentAreaOf(this.handle, tracked.Entity);
            if (PlayerChain.LooksLikePointer(linked) && linked != current.StructAddress - 0x8D0) { return true; }
        }

        var pos = Player.TryRead(this.handle, this.moduleBase);
        if (pos is not { } p)
        {
            // Yukleme ekraninda konum okunamiyor; uc kez ust uste olursa yeniden yukle.
            return ++this.strikes >= StrikesForAreaChange;
        }

        var jumped = this.lastPos is { } q &&
                     (Math.Abs(p.X - q.X) > AreaJump || Math.Abs(p.Y - q.Y) > AreaJump);
        this.lastPos = p;

        // Oyuncu varligi biliniyorsa ayrac uyanik liste: canli alanin listesi oyuncuyu
        // iceriyor, eski alaninki bosaliyor. Yurunebilirlik sadece yedek - kopru, kapi
        // gibi izgarada kapali gorunen hucrelerde yanlis alarm verebiliyor.
        var stillHere = LocalPlayer.Current is { } lp
            ? LocalPlayer.AwakeContains(this.handle, current.StructAddress - 0x8D0, lp.Entity)
            : PlayerFinder.Walkable(this.handle, current, p.X, p.Y);

        this.strikes = stillHere ? 0 : this.strikes + 1;
        return jumped || this.strikes >= StrikesForAreaChange;
    }

    /// <summary>
    ///     Oyuncuyu sabitler, canli alanin zeminini okur ve harita ogesini bulur.
    ///     Hepsi arka planda; sonuc arayuz is parcaciginda overlay'e veriliyor.
    /// </summary>
    private void StartLocate()
    {
        this.locateClock.Restart();
        this.busy = true;
        this.SetTray("harita yukleniyor");

        var h = this.handle;
        var needElement = this.overlay.MapElement == 0;

        Task.Run(() =>
        {
            TerrainFinder.Terrain? area = null;
            byte[]? raw = null;
            UiFinder.UiElement? element = null;

            try
            {
                // 1. Taramasiz yol: oyuncu varligi + 0x78 = yeni alan, zemini + 0x8D0'da.
                //    Yukleme ekrani surerken alan yapisi henuz hazir olmayabiliyor; birkac
                //    saniye yeniden deniyoruz. Denenecek bir sey yoksa (ilk acilis) hemen tarama.
                var found = this.FastLocate(h);

                // 2. Yedek: butun bellegi tara (ilk acilis ya da taramasiz yol tutmadiysa).
                if (found is null)
                {
                    var terrains = TerrainFinder.Find(h, null);
                    lock (this.knownAreas)
                    {
                        foreach (var t in terrains) { AddKnown(this.knownAreas, t.StructAddress - 0x8D0); }
                    }

                    found = LocalPlayer.Locate(h, terrains);
                }

                if (found is { } f)
                {
                    LocalPlayer.Current = new LocalPlayer.Tracked(f.Entity, f.Render);
                    lock (this.knownAreas) { AddKnown(this.knownAreas, f.Area.StructAddress - 0x8D0); }
                    area = f.Area;
                }
                else if (Pin.Run(h, this.OnPinMessage) is { } pinned)
                {
                    // Yedek: eski sabitleme (yuruyus ister). Varlik okunamazsa - ornegin bir
                    // patch 0x5D0'i kaydirirsa - harita yine de gelsin.
                    LocalPlayer.Current = null;
                    area = pinned.Area;
                }

                if (area is { } a) { raw = ReadTerrain(h, a); }

                if (raw is not null && needElement)
                {
                    element = UiFinder.FindLargeMap(h, null, requireVisible: false);
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("harita yukleme", ex);
            }

            this.Post(() => this.FinishLocate(area, raw, element));
        });
    }

    private void OnPinMessage(string message)
    {
        // Pin'in ayrintili kayitlari oyun ekranina yazilmiyor; oyuncunun bilmesi
        // gereken tek sey yurumesi gerektigi.
        if (message.Contains("YURU", StringComparison.OrdinalIgnoreCase))
        {
            this.Post(() => this.overlay.ShowNotice("Harita icin birkac saniye yuru", 120));
        }
    }

    private void FinishLocate(TerrainFinder.Terrain? area, byte[]? raw, UiFinder.UiElement? element)
    {
        this.busy = false;
        this.overlay.ShowNotice("", 0);

        if (area is not { } pn || raw is null)
        {
            // Alanda degilsen (karakter secimi, yukleme) her denemede bes saniyelik
            // tarama bosa gidiyor; aralari 20 saniyeye kadar aciyoruz.
            this.failures++;
            this.nextLocate = DateTime.UtcNow.AddSeconds(Math.Min(20, 5 * this.failures));
            this.SetTray("harita bulunamadi, tekrar denenecek");
            this.LogState($"yukleme basarisiz ({this.failures}. deneme)");
            return;
        }

        this.failures = 0;
        this.terrain = pn;
        this.lastPos = null;
        this.strikes = 0;

        this.overlay.SetMap(raw, pn.BytesPerRow, pn.GridX, pn.GridY);
        this.overlay.SetArea(pn.StructAddress - 0x8D0);
        if (element is { } el) { this.overlay.MapElement = el.Address; }

        // Oge zaten biliniyorsa aranmiyor; "bulunamadi" yazmak gunlugu yalanci yapardi.
        var elementNote = element is not null ? "bulundu"
            : this.overlay.MapElement != 0 ? "biliniyor"
            : "bulunamadi";

        this.SetTray("hazir");
        this.LogState($"harita yuklendi ({(LocalPlayer.Current is null ? "yuruyusle" : "varliktan")}) " +
                      $"{this.locateClock.ElapsedMilliseconds} ms, oge {elementNote}");
    }

    /// <summary>
    ///     Buyuk harita ogesinin hala gecerli oldugunu siniyor; degilse arka planda
    ///     yeniden buluyor. Tarama bes saniye surdugu icin en fazla on saniyede bir.
    /// </summary>
    private void EnsureElement()
    {
        if (this.overlay.MapElement != 0)
        {
            if (UiFinder.Reread(this.handle, this.overlay.MapElement) is { } e &&
                e.SizeX > 800 && e.SizeY > 500 &&
                Math.Abs(e.DefShiftX) < 5f && Math.Abs(e.DefShiftY + 20f) < 5f)
            {
                return;
            }

            this.overlay.MapElement = 0;
        }

        if (DateTime.UtcNow < this.nextElementSearch) { return; }
        this.nextElementSearch = DateTime.UtcNow.AddSeconds(10);

        this.busy = true;
        var h = this.handle;
        Task.Run(() =>
        {
            UiFinder.UiElement? found = null;
            try { found = UiFinder.FindLargeMap(h, null, requireVisible: false); }
            catch (Exception ex) { CrashLog.Write("harita ogesi", ex); }

            this.Post(() =>
            {
                this.busy = false;
                if (found is { } f) { this.overlay.MapElement = f.Address; }
            });
        });
    }

    private static byte[]? ReadTerrain(IntPtr h, TerrainFinder.Terrain t)
    {
        if (t.DataLength <= 0 || t.DataLength > int.MaxValue) { return null; }

        var raw = new byte[t.DataLength];
        return Native.ReadProcessMemory(h, (IntPtr)t.DataStart, raw, (IntPtr)t.DataLength, out var got) &&
               (long)got == t.DataLength
            ? raw
            : null;
    }

    private void Post(Action action)
    {
        if (this.overlay.IsDisposed || !this.overlay.IsHandleCreated) { return; }
        try { this.overlay.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void SetTray(string state)
    {
        var text = "PoE2 Harita - " + state;
        this.tray.Text = text.Length <= 63 ? text : text[..63];
    }

    private void CloseOwnHandle()
    {
        if (this.handle == IntPtr.Zero) { return; }
        Native.CloseHandle(this.handle);
        this.handle = IntPtr.Zero;
    }
}
