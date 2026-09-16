namespace Poe2Map;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--test")
        {
            NativeConsole.Attach();
            RunLocatorTest();
            return;
        }

        if (args.Length > 0 && args[0] == "--calib")
        {
            NativeConsole.Attach();
            RunCalibration(
                args.Length > 1 ? int.Parse(args[1]) : 24,
                args.Length > 2 ? int.Parse(args[2]) : 1200);
            return;
        }

        // Kayitli orneklerle aramayi tekrar calistirir - yeniden yurumeye gerek kalmadan.
        if (args.Length > 0 && args[0] == "--align")
        {
            NativeConsole.Attach();
            RunAlignOnly();
            return;
        }

        // Yakalanmayan her hata diske yazilsin. Onceden uygulama sessizce kapaniyordu:
        // olay gunlugunde de iz yoktu, sebebini gormek imkansizdi. Arayuz is
        // parcacigindaki hatalar da artik uygulamayi oldurmuyor - zamanlayicidan gelen
        // tek bir gecici okuma hatasi yuzunden harita kaybolmasin.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => CrashLog.Write("arayuz", e.Exception);
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => CrashLog.Write("surec", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("gorev", e.Exception);
            e.SetObserved();
        };

        CrashLog.Write("basladi", null);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        CrashLog.Write("normal kapandi", null);
    }

    private static (IntPtr Handle, ulong ModuleBase)? Open()
    {
        var game = Native.FindGame();
        if (game == null) { Console.WriteLine("Oyun sureci bulunamadi."); return null; }

        var handle = Native.OpenProcess(Native.ProcessQueryInformation | Native.ProcessVmRead, false, game.Id);
        if (handle == IntPtr.Zero) { Console.WriteLine("OpenProcess basarisiz."); return null; }

        ulong moduleBase = 0;
        try { moduleBase = (ulong)(game.MainModule?.BaseAddress.ToInt64() ?? 0); } catch { }

        Console.WriteLine($"surec: {game.ProcessName} (pid {game.Id})  modul {moduleBase:X}");
        return (handle, moduleBase);
    }

    private static void RunLocatorTest()
    {
        var opened = Open();
        if (opened is null) { return; }
        var (handle, moduleBase) = opened.Value;

        try
        {
            var pos = Player.TryRead(handle, moduleBase);
            Console.WriteLine(pos is { } p
                ? $"konum: ({p.X:F1}, {p.Y:F1})  -> hucre ({p.CellX}, {p.CellY})"
                : "konum okunamadi");

            var result = new ScanLocator().SurveyFull(handle, Console.WriteLine)
                .OrderByDescending(c => c.BandFraction)
                .ThenByDescending(c => c.ByteLength)
                .ToList();
            Console.WriteLine();
            Console.WriteLine($"ADAYLAR ({result.Count} adet, en buyuk 15):");
            var i = 0;
            foreach (var c in result.Take(15)) { Console.WriteLine($"  {++i,2}. {c}"); }
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private static void RunAlignOnly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "son_kalibrasyon.txt");
        if (!File.Exists(path)) { Console.WriteLine("Kayitli ornek yok - once --calib calistir."); return; }

        var positions = File.ReadAllLines(path)
            .Select(l => l.Split(' '))
            .Where(p => p.Length >= 2)
            .Select(p => new PlayerPos(float.Parse(p[0]), float.Parse(p[1]), p.Length > 2 ? float.Parse(p[2]) : 0))
            .ToList();

        Console.WriteLine($"kayitli ornek: {positions.Count}");

        var opened = Open();
        if (opened is null) { return; }
        var (handle, _) = opened.Value;

        try
        {
            var candidates = new ScanLocator().Survey(handle, Console.WriteLine);
            Console.WriteLine();
            var aligned = Calibrator.Search(handle, candidates, positions, Console.WriteLine);
            Console.WriteLine();
            Console.WriteLine("SONUC:");
            var i = 0;
            foreach (var a in aligned.Take(8)) { Console.WriteLine($"  {++i,2}. {a}"); }
            if (aligned.Count == 0) { Console.WriteLine("  hicbir hizalama yolu aciklamadi."); }
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private static void RunCalibration(int samples, int intervalMs)
    {
        var opened = Open();
        if (opened is null) { return; }
        var (handle, moduleBase) = opened.Value;

        try
        {
            Console.WriteLine();
            Console.WriteLine($"=== KONUM ORNEKLERI ({samples} adet, {intervalMs} ms arayla) ===");
            Console.WriteLine("SIMDI DOLASMAYA BASLA.");
            var positions = Calibrator.Sample(handle, moduleBase, samples, intervalMs, Console.WriteLine);

            Console.WriteLine();
            Console.WriteLine($"toplanan farkli nokta: {positions.Count}");

            // Ã–rnekleri saklÄ±yoruz: arama Ã¶lÃ§Ã¼tÃ¼nÃ¼ deÄŸiÅŸtirmek gerekirse kullanÄ±cÄ±yÄ±
            // tekrar yÃ¼rÃ¼tmek yerine aynÄ± veriyle yeniden Ã§alÄ±ÅŸtÄ±rabilelim.
            var samplePath = Path.Combine(AppContext.BaseDirectory, "son_kalibrasyon.txt");
            File.WriteAllLines(samplePath, positions.Select(p => $"{p.X} {p.Y} {p.Z}"));
            Console.WriteLine($"ornekler kaydedildi: {samplePath}");
            if (positions.Count < 5)
            {
                Console.WriteLine("Yeterli farkli nokta yok - daha genis dolasmak gerek.");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("=== BOLGE TARAMASI (on eleme yok) ===");
            var candidates = new ScanLocator().Survey(handle, Console.WriteLine);

            Console.WriteLine();
            Console.WriteLine("=== HIZALAMA ARAMASI ===");
            var aligned = Calibrator.Search(handle, candidates, positions, Console.WriteLine);

            Console.WriteLine();
            Console.WriteLine("SONUC (en iyi hizalamalar):");
            var i = 0;
            foreach (var a in aligned.Take(8)) { Console.WriteLine($"  {++i,2}. {a}"); }
            if (aligned.Count == 0) { Console.WriteLine("  hicbir hizalama butun ornekleri aciklamadi."); }
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}

/// <summary>
///     WinExe olarak derlendiÄŸi iÃ§in konsola baÄŸlÄ± deÄŸil; test/kalibrasyon kiplerinde
///     Ã§Ä±ktÄ±yÄ± gÃ¶rebilmek adÄ±na Ã§aÄŸÄ±ran konsola iliÅŸtiriyoruz.
/// </summary>
internal static class NativeConsole
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    internal static void Attach()
    {
        AttachConsole(-1);
        var stdout = Console.OpenStandardOutput();
        var writer = new StreamWriter(stdout) { AutoFlush = true };
        Console.SetOut(writer);
    }
}


