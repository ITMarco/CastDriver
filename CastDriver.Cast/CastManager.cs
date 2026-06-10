using System.Collections.Concurrent;
using System.Net.Sockets;
using CastDriver.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CastDriver.Cast;

// Wires together discovery, audio capture, the HTTP media server, and Cast sessions.
// The UI creates one CastManager and calls it as the user picks/unpicks devices.
public sealed class CastManager : IAsyncDisposable
{
    private readonly ChromecastDiscovery       _discovery  = new();
    private readonly LocalMediaServer          _mediaServer = new();
    private readonly ConcurrentDictionary<string, CastSession>  _sessions = new();
    private readonly ConcurrentDictionary<string, ChromecastDevice> _knownDevices = new();
    private AudioCapture? _capture;
    private bool          _capturing;
    private bool          _mediaServerStarted;
    private MMDevice?     _captureDevice;
    private bool          _isLoopback = true;

    // The chosen audio source endpoint id (null = default render device).
    public string? SourceDeviceId { get; set; }

    // User-controlled cast stream level, independent of the local PC volume.
    // 0 = silent, 1 = source level. Applied as software gain to the PCM we stream.
    // The capture is (on this setup) pre-volume, so the PC slider does NOT affect it.
    private volatile float _castGain = 1.0f;
    public float CastVolume
    {
        get => _castGain;
        set => _castGain = Math.Clamp(value, 0f, 1f);
    }

    // The friendly name of the device being captured (shown in the UI).
    public string? CaptureDeviceName { get; private set; }

    public event EventHandler<ChromecastDevice>? DeviceDiscovered;
    public event EventHandler<ChromecastDevice>? DeviceLost;
    public event EventHandler<string>?           SessionError;
    // (device, level 0–1) — raised when a receiver reports its own volume.
    public event EventHandler<(ChromecastDevice Device, float Level)>? DeviceVolumeReported;

    public IReadOnlyDictionary<string, ChromecastDevice> KnownDevices => _knownDevices;

    public void StartDiscovery()
    {
        _discovery.DeviceFound += (sender, d) =>
        {
            _knownDevices[d.Host] = d;
            DeviceDiscovered?.Invoke(this, d);
        };
        _discovery.DeviceLost += (sender, d) =>
        {
            _knownDevices.TryRemove(d.Host, out ChromecastDevice? removed);
            DeviceLost?.Invoke(this, d);
        };
        _discovery.Start();
    }

    // Start the HTTP media server and audio capture from the render endpoint.
    public async Task StartAudioAsync()
    {
        if (_capturing) return;

        EnsureCaptureDevice();

        _capture = new AudioCapture(_captureDevice!, _isLoopback);
        var rawFormat = _capture.WaveFormat;
        var pcm16     = PcmConverter.ToPcm16Format(rawFormat);
        _mediaServer.SetWaveFormat(pcm16);

        _capture.DataAvailable += (_, e) =>
        {
            var pcm = PcmConverter.Convert(e.Buffer[..e.BytesRecorded], rawFormat, _castGain);
            _mediaServer.PushPcmData(pcm);
        };

        if (!_mediaServerStarted)
        {
            _ = _mediaServer.RunAsync();
            _mediaServerStarted = true;
        }
        _capture.Start();
        _capturing = true;
        Log.Write($"[audio] capturing '{CaptureDeviceName}' format={rawFormat} → PCM16 {pcm16}, http port {_mediaServer.Port}, log at {Log.FilePath}");
    }

    // Stop capturing once nobody is listening, to avoid running the loopback forever.
    private void StopAudio()
    {
        if (!_capturing) return;
        _capture?.Dispose();
        _capture   = null;
        _capturing = false;
        Log.Write("[audio] capture stopped (no active casts)");
    }

    private void EnsureCaptureDevice()
    {
        if (_captureDevice != null) return;
        (_captureDevice, _isLoopback) = AudioCapture.Resolve(SourceDeviceId);
        CaptureDeviceName = _captureDevice.FriendlyName;
    }

    public bool IsCaptureDeviceAvailable => _captureDevice != null;

    // Switch the capture source. If we're already casting, restart capture on the new
    // device so the change takes effect live.
    public async Task SetSourceDeviceAsync(string? id)
    {
        SourceDeviceId = id;
        var wasCapturing = _capturing;
        StopAudio();
        _captureDevice = null; // force re-resolve on next EnsureCaptureDevice
        if (wasCapturing) await StartAudioAsync();
    }

    // "Cast only" = silence the local speakers (mute the endpoint) while the cast keeps
    // playing. This works because the loopback capture is pre-volume on this setup, so
    // muting the output does not silence what we stream.
    public void EnableCastOnlyMode()
    {
        EnsureCaptureDevice();
        _captureDevice!.AudioEndpointVolume.Mute = true;
    }

    public void DisableCastOnlyMode()
    {
        if (_captureDevice != null)
            _captureDevice.AudioEndpointVolume.Mute = false;
    }

    // Begin casting to a specific device (called when user checks a device in the UI).
    public async Task CastToDeviceAsync(ChromecastDevice device, CancellationToken ct = default)
    {
        if (_sessions.ContainsKey(device.Host)) return;

        if (!_capturing)
            await StartAudioAsync();

        var localIp  = GetLocalIpFor(device.Host);
        var audioUrl = _mediaServer.GetStreamUrl(localIp);
        Log.Write($"[cast] local IP for {device.Host} = {localIp}; stream URL = {audioUrl}");

        var session = new CastSession(device);
        session.Disconnected += (_, _) =>
        {
            _sessions.TryRemove(device.Host, out _);
            if (_sessions.IsEmpty) StopAudio();
        };
        session.ErrorOccurred  += (_, msg) => SessionError?.Invoke(this, $"{device.Name}: {msg}");
        session.VolumeReported += (_, level) => DeviceVolumeReported?.Invoke(this, (device, level));

        _sessions[device.Host] = session;
        await session.StartAsync(audioUrl, ct);
    }

    // Set a specific Chromecast's own volume (0–1). No-op if not currently casting to it.
    public async Task SetDeviceVolumeAsync(ChromecastDevice device, float level)
    {
        if (_sessions.TryGetValue(device.Host, out var session))
            await session.SetVolumeAsync(level);
    }

    // Stop casting to a specific device.
    public async Task StopCastingAsync(ChromecastDevice device)
    {
        if (_sessions.TryRemove(device.Host, out var session))
            await session.DisposeAsync();

        if (_sessions.IsEmpty) StopAudio();
    }

    public bool IsCasting(ChromecastDevice device) => _sessions.ContainsKey(device.Host);

    // UDP connect trick: the OS picks the right local interface for the target IP.
    private static string GetLocalIpFor(string remoteHost)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(remoteHost, 80);
        return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Address.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _discovery.Dispose();
        _capture?.Dispose();

        foreach (var (_, session) in _sessions)
            await session.DisposeAsync();

        await _mediaServer.DisposeAsync();
    }
}
