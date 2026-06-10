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
    private readonly DlnaDiscovery             _dlna        = new();
    private readonly LocalMediaServer          _mediaServer = new();
    private readonly ConcurrentDictionary<string, ICastSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ICastDevice>  _knownDevices = new();
    private ICaptureSource? _capture;
    private bool          _capturing;
    private bool          _mediaServerStarted;
    private MMDevice?     _captureDevice;
    private bool          _isLoopback = true;

    // The chosen audio source endpoint id (null = default render device).
    public string? SourceDeviceId { get; set; }

    // Streaming codec. WAV = lossless/high bandwidth; MP3 = compressed/wide compatibility.
    public StreamCodec Codec { get; set; } = StreamCodec.Wav;
    public int         Mp3Bitrate { get; set; } = 256;

    // "Now casting" title shown on receivers (e.g. the app name). Falls back to the PC name.
    public string? NowPlayingTitle { get; set; }

    // When an app source is selected: false = cast only that app; true = cast everything
    // except that app (e.g. mute notifications / a meeting from the cast).
    public bool ExcludeApp { get; set; }

    // Prebuffer / latency cushion in ms (forwarded to the media server).
    public int PrebufferMs
    {
        get => _mediaServer.PrebufferMs;
        set => _mediaServer.PrebufferMs = value;
    }

    // Device ids the user wants casting — drives auto-reconnect on unexpected drops.
    private readonly HashSet<string> _desired = [];
    public event EventHandler<ICastDevice>? CastEnded; // raised when a cast stops for good

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

    public event EventHandler<ICastDevice>? DeviceDiscovered;
    public event EventHandler<ICastDevice>? DeviceLost;
    public event EventHandler<string>?      SessionError;
    // (device, level 0–1) — raised when a device reports its own volume.
    public event EventHandler<(ICastDevice Device, float Level)>? DeviceVolumeReported;

    public IReadOnlyDictionary<string, ICastDevice> KnownDevices => _knownDevices;

    public void StartDiscovery()
    {
        _discovery.DeviceFound += (sender, d) => OnFound(d);
        _discovery.DeviceLost  += (sender, d) =>
        {
            _knownDevices.TryRemove(d.Id, out _);
            DeviceLost?.Invoke(this, d);
        };
        _dlna.DeviceFound += (sender, d) => OnFound(d);

        _discovery.Start();
        _dlna.Start();
    }

    private void OnFound(ICastDevice d)
    {
        _knownDevices[d.Id] = d;
        DeviceDiscovered?.Invoke(this, d);
    }

    // Start the HTTP media server and audio capture from the render endpoint.
    public async Task StartAudioAsync()
    {
        if (_capturing) return;

        EnsureCaptureDevice();

        if (AudioCapture.IsAppId(SourceDeviceId, out var pid))
        {
            Log.Write($"[audio] process-loopback capture for pid {pid} (exclude={ExcludeApp})");
            _capture = new ProcessLoopbackCapture(pid, ExcludeApp);
        }
        else
        {
            _capture = new AudioCapture(_captureDevice!, _isLoopback);
        }

        var rawFormat = _capture.WaveFormat;
        var pcm16     = PcmConverter.ToPcm16Format(rawFormat);
        _mediaServer.SetFormat(pcm16, Codec, Mp3Bitrate);

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
        _capture       = null;
        _capturing     = false;
        // Drop the cached device handle so the next cast resolves a FRESH one. Reusing a
        // stale MMDevice after a drop fails every subsequent cast with 0x9000FFFF.
        _captureDevice = null;
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
    public async Task CastToDeviceAsync(ICastDevice device, CancellationToken ct = default)
    {
        _desired.Add(device.Id);
        if (_sessions.ContainsKey(device.Id)) return;

        if (!_capturing)
            await StartAudioAsync();

        var localIp  = GetLocalIpFor(device.Host);
        var title    = Sanitize(NowPlayingTitle) is { Length: > 0 } t
            ? t : $"CastDriver — {Environment.MachineName}";
        var media    = new CastMedia(
            _mediaServer.GetStreamUrl(localIp), _mediaServer.ContentType,
            title, _mediaServer.GetArtUrl(localIp));
        Log.Write($"[cast] {device.Kind} '{device.Name}' local IP {localIp}; stream URL = {media.Url}");

        ICastSession session = device switch
        {
            ChromecastDevice cc => new CastSession(cc),
            DlnaDevice dlna     => new DlnaSession(dlna),
            _ => throw new NotSupportedException($"Unknown device kind: {device.Kind}"),
        };

        session.Disconnected += (_, _) =>
        {
            _sessions.TryRemove(device.Id, out _);
            if (_desired.Contains(device.Id)) _ = ReconnectAsync(device);
            else if (_sessions.IsEmpty)       StopAudio();
        };
        session.ErrorOccurred  += (_, msg) => SessionError?.Invoke(this, $"{device.Name}: {msg}");
        session.VolumeReported += (_, level) => DeviceVolumeReported?.Invoke(this, (device, level));

        _sessions[device.Id] = session;
        try
        {
            await session.StartAsync(media, ct);
        }
        catch
        {
            // Don't leave a half-started (zombie) session behind — it would desync the
            // UI and block both stopping and re-casting. Tear it down and rethrow.
            _sessions.TryRemove(device.Id, out _);
            await session.DisposeAsync();
            if (_sessions.IsEmpty) StopAudio();
            throw;
        }
    }

    // Auto-reconnect after an unexpected drop (Wi-Fi blip, receiver hiccup). Retries with
    // backoff while the user still wants this device casting; gives up after a few tries.
    private async Task ReconnectAsync(ICastDevice device)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (!_desired.Contains(device.Id)) return; // user stopped it meanwhile
            await Task.Delay(2500);
            if (!_desired.Contains(device.Id) || _sessions.ContainsKey(device.Id)) return;

            Log.Write($"[cast] reconnect attempt {attempt} → {device.Name}");
            try { await CastToDeviceAsync(device); return; }
            catch (Exception ex) { Log.Write($"[cast] reconnect failed: {ex.Message}"); }
        }

        // Out of attempts — stop wanting it and tell the UI.
        _desired.Remove(device.Id);
        if (_sessions.IsEmpty) StopAudio();
        CastEnded?.Invoke(this, device);
        Log.Write($"[cast] giving up reconnect to {device.Name}");
    }

    // Set a specific device's own volume (0–1). No-op if not currently casting to it.
    public async Task SetDeviceVolumeAsync(ICastDevice device, float level)
    {
        if (_sessions.TryGetValue(device.Id, out var session))
            await session.SetVolumeAsync(level);
    }

    // Mute/unmute a device by feeding it silence (no volume change, resumes at the same
    // level). Works on devices that don't support volume control. Keyed by the device's
    // IP — that's the address it connects to our media server from.
    public void SetDeviceMute(ICastDevice device, bool muted) =>
        _mediaServer.SetMuted(device.Host, muted);

    // Strip control characters (and cap length) so a stray SMTC title can't break the
    // LOAD JSON or the DLNA DIDL XML.
    private static string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = new string(s.Where(c => c >= ' ').ToArray()).Trim();
        return clean.Length > 100 ? clean[..100] : clean;
    }

    // Stop casting to a specific device.
    public async Task StopCastingAsync(ICastDevice device)
    {
        _desired.Remove(device.Id); // intentional stop — don't auto-reconnect

        if (_sessions.TryRemove(device.Id, out var session))
            await session.DisposeAsync();

        if (_sessions.IsEmpty) StopAudio();
    }

    public bool IsCasting(ICastDevice device) => _sessions.ContainsKey(device.Id);

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
        _dlna.Dispose();
        _capture?.Dispose();

        foreach (var (_, session) in _sessions)
            await session.DisposeAsync();

        await _mediaServer.DisposeAsync();
    }
}
