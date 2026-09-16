namespace Poe2Map;

/// <summary>
///     Yakalanmayan hatalari ve acilis/kapanis anlarini diske yazar.
///     Uygulama sessizce kapandiginda sebebi buradan okunuyor.
///
///     Iki exe de kullaniyor; dosya adi calisan exe'nin adindan geliyor ki
///     kayitlar birbirine karismasin: mapview.log, mapoverlay.log.
/// </summary>
internal static class CrashLog
{
    internal static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "poe2-map",
        (System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "poe2-map").ToLowerInvariant() + ".log");

    internal static void Write(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            var line = $"{DateTime.Now:dd.MM.yyyy HH:mm:ss}  {kind}" +
                       (ex is null ? "" : Environment.NewLine + ex) + Environment.NewLine;
            File.AppendAllText(Path_, line);
        }
        catch
        {
            // Gunluk yazilamazsa uygulamayi durdurmayalim.
        }
    }
}
