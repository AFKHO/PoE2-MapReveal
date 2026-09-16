using System.Diagnostics;

namespace Poe2Map;

internal static class Program
{
    // Rastgele isimli kopyalarin tutuldugu klasor.
    private static readonly string KopyaKlasoru =
        Path.Combine(Path.GetTempPath(), "mo_cache");

    [STAThread]
    private static void Main(string[] args)
    {
        bool relaunched = args.Length > 0 && args[0] == "--relaunched";

        // Visual Studio'da F5 (debugger) altinda ise dokunma; normal cift
        // tiklamada rastgele isimli kopya olusturup onu calistir.
        if (!relaunched && !Debugger.IsAttached)
        {
            try
            {
                string kaynakKlasor = AppContext.BaseDirectory;
                string exeAdi = Path.GetFileName(Environment.ProcessPath!); // MapOverlay.exe

                TemizleEskiKopyalar();

                string rastgele = Guid.NewGuid().ToString("N").Substring(0, 12);
                string hedefKlasor = Path.Combine(KopyaKlasoru, rastgele);
                Directory.CreateDirectory(hedefKlasor);

                // Tum dosyalari kopyala; SADECE ana exe rastgele isim alir,
                // digerleri (MapOverlay.dll, .json ...) aynen kalir.
                foreach (string src in Directory.GetFiles(
                    kaynakKlasor, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(kaynakKlasor, src);
                    if (string.Equals(rel, exeAdi, StringComparison.OrdinalIgnoreCase))
                    {
                        rel = rastgele + ".exe";
                    }

                    string dst = Path.Combine(hedefKlasor, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, true);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(hedefKlasor, rastgele + ".exe"),
                    Arguments = "--relaunched",
                    UseShellExecute = false
                });
                return;
            }
            catch
            {
                // Kopyalama olmazsa isim degismeden normal calissin.
            }
        }

        // ---- Orijinal program ----
        using var single = new Mutex(true, @"Local\Poe2Map.MapOverlay", out var first);
        if (!first) { return; }

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
        Application.Run(new OverlayContext());
        CrashLog.Write("normal kapandi", null);

        if (relaunched)
        {
            KendiniSil();
        }
    }

    private static void TemizleEskiKopyalar()
    {
        try
        {
            if (!Directory.Exists(KopyaKlasoru))
            {
                return;
            }

            foreach (string dir in Directory.GetDirectories(KopyaKlasoru))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch
        {
        }
    }

    private static void KendiniSil()
    {
        try
        {
            string klasor = Path.GetDirectoryName(Environment.ProcessPath!)!;
            string cmd = "/C choice /C Y /N /D Y /T 2 & rmdir /S /Q \"" + klasor + "\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmd,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch
        {
        }
    }
}