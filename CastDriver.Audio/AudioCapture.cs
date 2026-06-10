using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CastDriver.Audio;

// Describes a selectable audio source: either a render device (captured via loopback)
// or an input device (captured directly).
public sealed record AudioEndpointInfo(string Id, string Name, bool IsInput)
{
    public string DisplayName => (IsInput ? "🎤 " : "🔊 ") + Name;
    public override string ToString() => DisplayName;
}

public sealed class AudioCapture : IDisposable
{
    private readonly WasapiCapture _capture;

    public WaveFormat WaveFormat => _capture.WaveFormat;
    public event EventHandler<WaveInEventArgs>? DataAvailable;

    // loopback = true → capture what plays on a render device; false → capture an input device.
    public AudioCapture(MMDevice device, bool loopback)
    {
        _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        _capture.DataAvailable += (s, e) => DataAvailable?.Invoke(this, e);
    }

    public void Start() => _capture.StartRecording();

    public void Stop()
    {
        try { _capture.StopRecording(); } catch { /* already stopped */ }
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
    }

    // ── Device enumeration / selection ─────────────────────────────────────────

    public static MMDevice? FindScreamDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        return devices.FirstOrDefault(d =>
            d.FriendlyName.Contains("Scream", StringComparison.OrdinalIgnoreCase));
    }

    // Default render device (used when no explicit source is chosen).
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

    // All selectable sources: render endpoints (loopback) followed by input endpoints.
    public static IReadOnlyList<AudioEndpointInfo> ListEndpoints()
    {
        using var enumerator = new MMDeviceEnumerator();
        var list = new List<AudioEndpointInfo>();

        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            list.Add(new AudioEndpointInfo(d.ID, d.FriendlyName, IsInput: false));

        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            list.Add(new AudioEndpointInfo(d.ID, d.FriendlyName, IsInput: true));

        return list;
    }

    // Resolve a saved endpoint id to a device + capture mode.
    // Falls back to the default render device when the id is null/missing.
    public static (MMDevice Device, bool Loopback) Resolve(string? id)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrEmpty(id))
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                if (d.ID == id) return (d, true);

            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                if (d.ID == id) return (d, false);
        }

        return (GetCaptureDevice(), true);
    }
}
