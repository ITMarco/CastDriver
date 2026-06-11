using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CastDriver.Audio;

public enum SourceKind { RenderLoopback, Input, App }

// A selectable audio source: a render device (loopback), an input device, or a single
// application (process loopback, identified by its process id).
public sealed record AudioEndpointInfo(string Id, string Name, SourceKind Kind, uint ProcessId = 0)
{
    public string DisplayName => Kind switch
    {
        SourceKind.Input => "🎤 " + Name,
        SourceKind.App   => "🎵 " + Name,
        _                => "🔊 " + Name,
    };

    // Grouping for the source dropdown: devices on top, applications below a divider.
    public string Category => Kind == SourceKind.App ? "Applications" : "Devices";

    public override string ToString() => DisplayName;
}

// Captures a render or input device via WASAPI. (Per-app capture is ProcessLoopbackCapture.)
public sealed class AudioCapture : ICaptureSource
{
    private readonly WasapiCapture _capture;

    public WaveFormat WaveFormat => _capture.WaveFormat;
    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public AudioCapture(MMDevice device, bool loopback)
    {
        _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        _capture.DataAvailable += (s, e) => DataAvailable?.Invoke(this, e);
    }

    public void Start() => _capture.StartRecording();
    public void Stop()  { try { _capture.StopRecording(); } catch { } }
    public void Dispose() { Stop(); _capture.Dispose(); }

    // ── Device / app enumeration ───────────────────────────────────────────────

    private const string AppIdPrefix = "app:";

    public static bool IsAppId(string? id, out uint processId)
    {
        processId = 0;
        return id != null && id.StartsWith(AppIdPrefix, StringComparison.Ordinal)
            && uint.TryParse(id.AsSpan(AppIdPrefix.Length), out processId);
    }

    public static MMDevice? FindScreamDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains("Scream", StringComparison.OrdinalIgnoreCase));
    }

    public static MMDevice GetCaptureDevice()
    {
        var scream = FindScreamDevice();
        if (scream != null) return scream;
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public static IEnumerable<MMDevice> GetAllRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    }

    // All selectable sources: render endpoints, input endpoints, then per-app entries.
    public static IReadOnlyList<AudioEndpointInfo> ListEndpoints()
    {
        using var enumerator = new MMDeviceEnumerator();
        var list = new List<AudioEndpointInfo>();

        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            list.Add(new AudioEndpointInfo(d.ID, d.FriendlyName, SourceKind.RenderLoopback));

        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            list.Add(new AudioEndpointInfo(d.ID, d.FriendlyName, SourceKind.Input));

        list.AddRange(ListAudioApps(enumerator));
        return list;
    }

    // Apps that currently have an audio session on the default render device. Multi-process
    // apps (Firefox, Chrome, …) play audio from child processes, so we collapse each app to
    // ONE entry that targets its main (window-owning) process — process-loopback then
    // captures the whole tree, i.e. audio from any window/tab of that app.
    public static IReadOnlyList<AudioEndpointInfo> ListAudioApps(MMDeviceEnumerator? enumerator = null)
    {
        var owns = enumerator == null;
        enumerator ??= new MMDeviceEnumerator();
        var byProcessName = new Dictionary<string, AudioEndpointInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dev      = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = dev.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var pid = sessions[i].GetProcessID;
                if (pid == 0) continue;

                string procName;
                try { using var p = Process.GetProcessById((int)pid); procName = p.ProcessName; }
                catch { continue; } // process gone / inaccessible

                if (byProcessName.ContainsKey(procName)) continue; // already have this app

                // Target the main (window-owning) process of this app, so the tree-include
                // captures audio from whichever child process is actually playing.
                var (targetPid, title) = MainProcessOf(procName, pid);
                var display = string.IsNullOrWhiteSpace(title) ? procName : $"{procName} — {title}";
                byProcessName[procName] =
                    new AudioEndpointInfo($"{AppIdPrefix}{targetPid}", display, SourceKind.App, targetPid);
            }
        }
        catch { /* session enumeration unavailable */ }
        finally { if (owns) enumerator.Dispose(); }

        return byProcessName.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Mute/unmute an app's audio in the Windows volume mixer (its session(s) on the
    // default render device) — so it plays on the cast but not the local speakers. We mute
    // every session whose process shares the target's name, to cover multi-process apps.
    public static void SetAppMuted(uint pid, bool muted)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var dev        = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var targetName = ProcessNameOf(pid);
            var sessions   = dev.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s    = sessions[i];
                var spid = s.GetProcessID;
                if (spid == 0) continue;
                if (spid == pid || (targetName != null && ProcessNameOf(spid) == targetName))
                    try { s.SimpleAudioVolume.Mute = muted; } catch { }
            }
        }
        catch { /* session API unavailable */ }
    }

    private static string? ProcessNameOf(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return null; }
    }

    // Among all processes with this name, prefer the one that owns a visible main window
    // (the app's root process); fall back to the audio-session process itself.
    private static (uint Pid, string? Title) MainProcessOf(string processName, uint fallbackPid)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    return ((uint)p.Id, p.MainWindowTitle);
            }
        }
        catch { /* fall through */ }
        return (fallbackPid, null);
    }

    // Resolve a device id to a device + capture mode. App ids and unknown ids fall back
    // to the default render device (used for the UI's volume/meter controls).
    public static (MMDevice Device, bool Loopback) Resolve(string? id)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrEmpty(id) && !IsAppId(id, out _))
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                if (d.ID == id) return (d, true);
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                if (d.ID == id) return (d, false);
        }

        return (GetCaptureDevice(), true);
    }
}
