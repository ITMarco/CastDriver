using System.Text.Json;
using CastDriver.Cast.Proto;

namespace CastDriver.Cast;

// Implements the Cast V2 application-level protocol over a CastChannel.
// Flow: CONNECT → LAUNCH default receiver → wait RECEIVER_STATUS → CONNECT session → LOAD audio URL.
public sealed class CastSession : ICastSession
{
    // Cast namespaces
    private const string NsConnection = "urn:x-cast:com.google.cast.tp.connection";
    private const string NsHeartbeat  = "urn:x-cast:com.google.cast.tp.heartbeat";
    private const string NsReceiver   = "urn:x-cast:com.google.cast.receiver";
    private const string NsMedia      = "urn:x-cast:com.google.cast.media";

    // The Default Media Receiver supports audio/video without a custom Cast app.
    private const string DefaultMediaReceiver = "CC1AD845";
    private const string SenderId             = "sender-0";
    private const string ReceiverId           = "receiver-0";

    private CastChannel? _channel;
    private string?      _sessionTransportId;
    private string?      _pendingAudioUrl;
    private bool         _sessionConnected;
    private int          _requestId;
    private int          _mediaSessionId;

    private readonly CancellationTokenSource _cts = new();

    public ChromecastDevice Device     { get; }
    public bool             IsActive   { get; private set; }

    public event EventHandler?         Disconnected;
    public event EventHandler<string>? ErrorOccurred;
    // Raised when the receiver reports its volume (0–1), so the UI can reflect it.
    public event EventHandler<float>?  VolumeReported;

    public CastSession(ChromecastDevice device) => Device = device;

    // Set the Chromecast's own playback volume (0.0–1.0). This is a control message,
    // so it takes effect instantly — no audio-buffer delay.
    public async Task SetVolumeAsync(float level, CancellationToken ct = default)
    {
        level = Math.Clamp(level, 0f, 1f);
        var json = $$"""{"type":"SET_VOLUME","volume":{"level":{{level.ToString(System.Globalization.CultureInfo.InvariantCulture)}}},"requestId":{{NextId()}}}""";
        await SendJsonAsync(NsReceiver, ReceiverId, json, ct);
    }

    public async Task StartAsync(string audioUrl, CancellationToken ct = default)
    {
        _pendingAudioUrl  = audioUrl;
        Log.Write($"[cast] connecting to {Device.Name} @ {Device.Host}:{Device.Port}, will load {audioUrl}");
        _channel          = await CastChannel.ConnectAsync(Device.Host, Device.Port, ct);
        IsActive          = true;

        // Step 1 — open the transport-level connection to the receiver.
        await SendJsonAsync(NsConnection, ReceiverId, """{"type":"CONNECT","origin":{}}""", ct);

        // Step 2 — ask the Chromecast to launch the default media receiver app.
        var launchId = NextId();
        await SendJsonAsync(NsReceiver, ReceiverId,
            $$"""{"type":"LAUNCH","appId":"{{DefaultMediaReceiver}}","requestId":{{launchId}}}""", ct);

        // Step 3 — receive loop handles RECEIVER_STATUS → connects to session → LOADs audio.
        _ = RunReceiveLoopAsync(_cts.Token);
        _ = RunHeartbeatAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        // Tell the receiver to stop playback so it releases its HTTP pull from our media
        // server — otherwise it keeps playing after we close the control connection.
        try
        {
            if (_channel != null && _sessionTransportId != null && _mediaSessionId != 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                Log.Write($"[cast] STOP mediaSessionId={_mediaSessionId}");
                await SendJsonAsync(NsMedia, _sessionTransportId,
                    $$"""{"type":"STOP","mediaSessionId":{{_mediaSessionId}},"requestId":{{NextId()}}}""",
                    timeout.Token);
            }
        }
        catch { /* best-effort — we're tearing down anyway */ }

        _cts.Cancel();
        if (_channel != null)
        {
            try { await _channel.DisposeAsync(); } catch { /* ignore on teardown */ }
            _channel = null;
        }
        IsActive = false;
    }

    // ── Receive loop ─────────────────────────────────────────────────────────

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _channel != null)
            {
                var msg = await _channel.ReceiveAsync(ct);
                if (msg == null) { OnDisconnected(); return; }
                await HandleMessageAsync(msg, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
            OnDisconnected();
        }
    }

    private async Task HandleMessageAsync(CastMessage msg, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(msg.PayloadUtf8)) return;

        // Don't spam the log with PINGs, but record everything else the receiver says.
        if (msg.Namespace != NsHeartbeat)
            Log.Write($"[cast] recv {msg.Namespace}: {Truncate(msg.PayloadUtf8, 400)}");

        switch (msg.Namespace)
        {
            case NsHeartbeat:
                if (msg.PayloadUtf8.Contains("\"PING\""))
                    await SendJsonAsync(NsHeartbeat, msg.SourceId, """{"type":"PONG"}""", ct);
                break;

            case NsReceiver:
                await HandleReceiverStatusAsync(msg.PayloadUtf8, ct);
                break;

            case NsMedia:
                HandleMediaStatus(msg.PayloadUtf8);
                break;
        }
    }

    // The media channel is where the receiver tells us LOAD_FAILED / errors / play state.
    private void HandleMediaStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

            // Track the media session id so we can later issue a STOP for it.
            if (doc.RootElement.TryGetProperty("status", out var statusArr) &&
                statusArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in statusArr.EnumerateArray())
                    if (s.TryGetProperty("mediaSessionId", out var msid) &&
                        msid.TryGetInt32(out var id))
                        _mediaSessionId = id;
            }

            if (type is "LOAD_FAILED" or "LOAD_CANCELLED" or "ERROR" or "INVALID_REQUEST")
            {
                var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
                var detail = doc.RootElement.TryGetProperty("detailedErrorCode", out var d) ? d.ToString() : null;
                var full   = $"{type}{(reason != null ? $" reason={reason}" : "")}{(detail != null ? $" code={detail}" : "")}";
                Log.Write($"[cast] MEDIA ERROR: {full}");
                ErrorOccurred?.Invoke(this, full);
            }
        }
        catch { /* not JSON we care about */ }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private async Task HandleReceiverStatusAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var t) || t.GetString() != "RECEIVER_STATUS") return;
        if (!root.TryGetProperty("status", out var status)) return;

        // Report the device's current volume so the UI slider can reflect it.
        if (status.TryGetProperty("volume", out var vol) &&
            vol.TryGetProperty("level", out var lvl) &&
            lvl.ValueKind == JsonValueKind.Number)
            VolumeReported?.Invoke(this, (float)lvl.GetDouble());

        if (!status.TryGetProperty("applications", out var apps)) return;

        foreach (var app in apps.EnumerateArray())
        {
            if (!app.TryGetProperty("appId", out var appId)) continue;
            if (appId.GetString() != DefaultMediaReceiver) continue;
            if (!app.TryGetProperty("transportId", out var tidEl)) continue;

            var tid = tidEl.GetString()!;
            if (_sessionConnected && tid == _sessionTransportId) break; // already handled

            _sessionTransportId = tid;
            _sessionConnected   = true;

            // Connect to the launched app's session transport.
            await SendJsonAsync(NsConnection, tid, """{"type":"CONNECT","origin":{}}""", ct);

            if (_pendingAudioUrl != null)
            {
                await LoadMediaAsync(_pendingAudioUrl, ct);
                _pendingAudioUrl = null;
            }
            break;
        }
    }

    private async Task LoadMediaAsync(string url, CancellationToken ct)
    {
        if (_sessionTransportId == null) return;
        var id = NextId();
        Log.Write($"[cast] LOAD {url} (transport {_sessionTransportId})");
        await SendJsonAsync(NsMedia, _sessionTransportId,
            $$"""{"type":"LOAD","requestId":{{id}},"media":{"contentId":"{{url}}","contentType":"audio/wav","streamType":"LIVE"},"autoplay":true}""",
            ct);
    }

    // ── Heartbeat ────────────────────────────────────────────────────────────

    private async Task RunHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5_000, ct);
                await SendJsonAsync(NsHeartbeat, ReceiverId, """{"type":"PING"}""", ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SendJsonAsync(string ns, string destinationId, string json, CancellationToken ct)
    {
        if (_channel == null) return;
        await _channel.SendAsync(new CastMessage
        {
            SourceId      = SenderId,
            DestinationId = destinationId,
            Namespace     = ns,
            PayloadUtf8   = json,
        }, ct);
    }

    private void OnDisconnected()
    {
        IsActive = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private int NextId() => Interlocked.Increment(ref _requestId);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
