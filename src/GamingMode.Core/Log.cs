namespace GamingMode.Core;

public static class Paths
{
    /// <summary>%ProgramData%\AceBot\GamingMode - owned by the service.</summary>
    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AceBot", "GamingMode");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string LogFile => Path.Combine(DataDir, "log.txt");
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.DataDir);
                var file = new FileInfo(Paths.LogFile);
                if (file.Exists && file.Length > 1_000_000) file.Delete();
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch { /* logging must never break gaming mode */ }
        }
    }

    public static string Tail(int lines)
    {
        lock (Gate)
        {
            try { return string.Join(Environment.NewLine, File.ReadAllLines(Paths.LogFile).TakeLast(lines)); }
            catch { return ""; }
        }
    }
}
