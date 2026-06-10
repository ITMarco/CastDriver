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

    // Apps that currently have an audio session on the default render device.
    public static IReadOnlyList<AudioEndpointInfo> ListAudioApps(MMDeviceEnumerator? enumerator = null)
    {
        var owns = enumerator == null;
        enumerator ??= new MMDeviceEnumerator();
        var apps = new List<AudioEndpointInfo>();
        var seen = new HashSet<uint>();
        try
        {
            var dev      = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = dev.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s   = sessions[i];
                var pid = s.GetProcessID;
                if (pid == 0 || !seen.Add(pid)) continue;

                var name = ProcessName(pid);
                if (name == null) continue; // process gone / inaccessible
                apps.Add(new AudioEndpointInfo($"{AppIdPrefix}{pid}", name, SourceKind.App, pid));
            }
        }
        catch { /* session enumeration unavailable */ }
        finally { if (owns) enumerator.Dispose(); }

        return apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var title = p.MainWindowTitle;
            return !string.IsNullOrWhiteSpace(title) ? $"{p.ProcessName} — {title}" : p.ProcessName;
        }
        catch { return null; }
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
