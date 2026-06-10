using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CastDriver.Audio;

public sealed class AudioCapture : IDisposable
{
    private WasapiCapture? _capture;
    private readonly MMDevice _device;

    public WaveFormat WaveFormat => _device.AudioClient.MixFormat;
    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public AudioCapture(MMDevice device)
    {
        _device = device;
    }

    public static MMDevice? FindScreamDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        return devices.FirstOrDefault(d =>
            d.FriendlyName.Contains("Scream", StringComparison.OrdinalIgnoreCase));
    }

    // Returns the Scream device if installed, otherwise the Windows default render device.
    // This lets the app work without the virtual driver installed.
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

    public void Start()
    {
        _capture = new WasapiLoopbackCapture(_device);
        _capture.DataAvailable += (s, e) => DataAvailable?.Invoke(this, e);
        _capture.StartRecording();
    }

    public void Stop()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
