using System.IO;
using System.Text.Json;

namespace CastDriver.UI;

// A user-saved equalizer preset.
public sealed class EqPresetData
{
    public string   Name  { get; set; } = "";
    public double[] Gains { get; set; } = [];
}

// Small JSON-backed settings file in %APPDATA%\CastDriver\settings.json.
public sealed class AppSettings
{
    // Shared instance — load once, everyone reads/writes the same object.
    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    // The selected audio source endpoint id (null = default render device).
    public string? SourceDeviceId { get; set; }

    // Whether diagnostic logging is enabled (toggled from the Debug screen).
    public bool LoggingEnabled { get; set; } = true;

    // Whether the window is pinned open (doesn't auto-hide on focus loss).
    public bool Pinned { get; set; }

    // Stream as MP3 (compressed, wide compatibility) instead of WAV (lossless).
    public bool UseMp3 { get; set; }
    public int  Mp3Bitrate { get; set; } = 256;

    // When true, the app does not check GitHub for a newer version at startup.
    public bool DisableUpdateCheck { get; set; }

    // When true, the app launches hidden in the tray instead of showing its window.
    public bool StartMinimized { get; set; }

    // Stream prebuffer in ms — the latency/stability cushion (lower = less lag).
    public int PrebufferMs { get; set; } = 1500;

    // For an app source: cast everything EXCEPT the chosen app (instead of only it).
    public bool ExcludeApp { get; set; }

    // Equalizer: enabled flag, per-band gains (dB), and user-saved presets.
    public bool               EqEnabled { get; set; }
    public double[]?          EqGains   { get; set; }
    public List<EqPresetData> EqPresets { get; set; } = [];

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CastDriver");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
}
