namespace CastDriver.Cast;

// Dead-simple file logger so we can see what's happening in this windowless tray app.
// Writes to %TEMP%\CastDriver.log (path also echoed to Debug output).
public static class Log
{
    private static readonly object _gate = new();

    // Master switch — the Debug screen toggles this. When off, nothing is written.
    public static bool Enabled { get; set; } = true;

    public static string FilePath { get; } =
        Path.Combine(Path.GetTempPath(), "CastDriver.log");

    public static void Write(string msg)
    {
        if (!Enabled) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        try { lock (_gate) File.AppendAllText(FilePath, line + Environment.NewLine); }
        catch { /* logging must never throw */ }
    }

    public static string Read()
    {
        try { lock (_gate) return File.Exists(FilePath) ? File.ReadAllText(FilePath) : ""; }
        catch (Exception ex) { return $"(could not read log: {ex.Message})"; }
    }

    public static void Delete()
    {
        try { lock (_gate) if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* best-effort */ }
    }
}
