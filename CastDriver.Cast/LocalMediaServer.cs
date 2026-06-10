using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using NAudio.Wave;

namespace CastDriver.Cast;

// Minimal HTTP server that streams PCM audio as WAV to any number of Chromecast clients.
// Each connecting client receives the WAV header followed by a live PCM feed.
// No admin / URL ACL registration required (uses TcpListener, not HttpListener).
public sealed class LocalMediaServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly ConcurrentDictionary<string, byte> _muted = new(); // device ids muted
    private WaveFormat? _waveFormat;
    private StreamCodec _codec = StreamCodec.Wav;
    private Mp3StreamEncoder? _mp3;
    private Mp3StreamEncoder? _mp3Silence; // produces silence frames for muted MP3 clients
    private byte[]            _zero = [];   // reusable zero buffer for WAV silence
    private CancellationTokenSource _cts = new();

    // One connected receiver. DeviceId comes from the "?dev=" query so we can mute it.
    private sealed class Client
    {
        public required Channel<byte[]> Channel;
        public string DeviceId = "";
    }

    // Per-device mute: muted devices receive silence instead of real audio, at the same
    // byte rate, so they stay in sync and resume instantly (no volume change involved).
    public void SetMuted(string deviceId, bool muted)
    {
        if (muted) _muted[deviceId] = 1;
        else       _muted.TryRemove(deviceId, out _);
    }

    // What clients receive. Wav = raw PCM16 (lossless, high bandwidth); Mp3 = compressed
    // (lower bandwidth, better compatibility with picky renderers like Sonos).
    public string ContentType => _codec == StreamCodec.Mp3 ? "audio/mpeg" : "audio/wav";
    private string FileName    => _codec == StreamCodec.Mp3 ? "audio.mp3"  : "audio.wav";

    // Silence keep-alive: WasapiLoopbackCapture delivers NO buffers while the system
    // is idle/silent, which would stall the Chromecast (perpetual buffering, no sound).
    // We inject silence at the real-time byte rate whenever real audio goes quiet.
    private const int SilenceIntervalMs = 100;

    // Initial silence sent to each client on connect. The receiver pulls this instantly
    // to build its playback buffer; after that we can only feed at real time, so this
    // value sets BOTH the end-to-end latency and the stability cushion. Too small ⇒
    // the receiver rides the edge of underrun and flaps PLAYING/BUFFERING. User-adjustable
    // via the latency slider; takes effect on the next connection.
    public int PrebufferMs { get; set; } = 1500;
    private System.Threading.Timer? _silenceTimer;
    private byte[]                   _silenceChunk = [];
    private long                     _lastDataTicks;

    public int Port { get; }

    public LocalMediaServer()
    {
        _listener = new TcpListener(IPAddress.Any, 0); // OS assigns a free port
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    // Call before the first PushPcmData to set the PCM format, codec, and (for MP3) bitrate.
    public void SetFormat(WaveFormat pcm16, StreamCodec codec, int mp3BitrateKbps)
    {
        _waveFormat = pcm16;
        _codec      = codec;

        _mp3?.Dispose();
        _mp3Silence?.Dispose();
        _mp3        = codec == StreamCodec.Mp3 ? new Mp3StreamEncoder(pcm16, mp3BitrateKbps) : null;
        _mp3Silence = codec == StreamCodec.Mp3 ? new Mp3StreamEncoder(pcm16, mp3BitrateKbps) : null;

        // Size one silence chunk to SilenceIntervalMs of audio, block-aligned.
        var bytes = pcm16.AverageBytesPerSecond * SilenceIntervalMs / 1000;
        bytes -= bytes % Math.Max(1, pcm16.BlockAlign);
        _silenceChunk  = new byte[Math.Max(pcm16.BlockAlign, bytes)];
        _lastDataTicks = DateTime.UtcNow.Ticks;

        _silenceTimer?.Dispose();
        _silenceTimer = new System.Threading.Timer(
            _ => PumpSilence(), null, SilenceIntervalMs, SilenceIntervalMs);
    }

    // Turn one PCM buffer into the bytes to stream, and fan it out — sending muted devices
    // an equivalent silence stream instead of the real audio.
    private void Distribute(byte[] pcm)
    {
        var anyMuted = !_muted.IsEmpty;

        byte[] real, silence;
        if (_codec == StreamCodec.Mp3 && _mp3 != null)
        {
            real    = _mp3.Encode(pcm);
            silence = anyMuted ? _mp3Silence!.Encode(Zero(pcm.Length)) : [];
        }
        else
        {
            real    = pcm;
            silence = anyMuted ? Zero(pcm.Length) : [];
        }

        foreach (var (_, c) in _clients)
        {
            var data = anyMuted && _muted.ContainsKey(c.DeviceId) ? silence : real;
            if (data.Length == 0) continue;
            if (!c.Channel.Writer.TryWrite(data)) // DropOldest policy handles slow clients
                Interlocked.Increment(ref _droppedChunks);
        }
    }

    // Reusable zero buffer of the given length (for WAV silence / MP3 silence input).
    private byte[] Zero(int length)
    {
        if (_zero.Length != length) _zero = new byte[length];
        return _zero;
    }

    // Diagnostics — how much real vs. silence we feed, and how often we drop.
    private long _realBytes;
    private long _silenceBytes;
    private long _droppedChunks;
    private long _lastSummaryTicks = DateTime.UtcNow.Ticks;

    // Called by AudioCapture for every incoming PCM buffer.
    // Fans the data out to all connected Chromecast clients.
    public void PushPcmData(byte[] pcm)
    {
        Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);
        Interlocked.Add(ref _realBytes, pcm.Length);
        Distribute(pcm);
    }

    // Fires every SilenceIntervalMs. If real audio hasn't arrived within that window,
    // feed every client a chunk of silence so the HTTP stream keeps flowing at roughly
    // real time and the receiver never starves.
    private void PumpSilence()
    {
        LogSummaryIfDue();

        if (_clients.IsEmpty || _silenceChunk.Length == 0) return;

        var idleMs = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastDataTicks))
                     / TimeSpan.TicksPerMillisecond;
        if (idleMs < SilenceIntervalMs) return; // real audio is flowing — leave it alone

        Interlocked.Add(ref _silenceBytes, _silenceChunk.Length);
        Distribute(_silenceChunk);
    }

    // Every ~2 s, report the real/silence byte rates and drops so we can see whether
    // capture is starving, silence is dominating, or chunks are being dropped.
    private void LogSummaryIfDue()
    {
        var now     = DateTime.UtcNow.Ticks;
        var elapsed = (now - _lastSummaryTicks) / TimeSpan.TicksPerMillisecond;
        if (elapsed < 2000) return;
        _lastSummaryTicks = now;

        var real    = Interlocked.Exchange(ref _realBytes, 0);
        var silence  = Interlocked.Exchange(ref _silenceBytes, 0);
        var dropped = Interlocked.Exchange(ref _droppedChunks, 0);
        if (_clients.IsEmpty && real == 0 && silence == 0) return;

        Log.Write($"[feed] {elapsed}ms: real={real / 1024}KB silence={silence / 1024}KB " +
                  $"dropped={dropped} clients={_clients.Count}");
    }

    // Returns the URL the Chromecast should request. The device id rides along in the
    // query so the server can identify (and mute) that specific client.
    public string GetStreamUrl(string localIp, string deviceId) =>
        $"http://{localIp}:{Port}/{FileName}?dev={Uri.EscapeDataString(deviceId)}";
    public string GetArtUrl(string localIp) => $"http://{localIp}:{Port}/art.png";

    private static string ParseDev(string requestLine)
    {
        var i = requestLine.IndexOf("dev=", StringComparison.Ordinal);
        if (i < 0) return "";
        var rest = requestLine[(i + 4)..];
        var end  = rest.IndexOfAny([' ', '&']);
        return Uri.UnescapeDataString(end >= 0 ? rest[..end] : rest);
    }

    public Task RunAsync() => RunAsync(_cts.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }

            _ = ServeClientAsync(tcp, ct);
        }
    }

    private async Task ServeClientAsync(TcpClient tcp, CancellationToken ct)
    {
        var remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        Log.Write($"[http] client connected from {remote}");

        tcp.NoDelay = true;
        var stream  = tcp.GetStream();

        // Read the request line so we can route (audio stream vs. the "now casting" artwork).
        var requestLine = await DiscardHttpRequestAsync(stream, ct);
        Log.Write($"[http] request from {remote}: {requestLine}");

        if (requestLine.Contains("/art.png", StringComparison.OrdinalIgnoreCase))
        {
            await ServeArtAsync(stream, ct);
            tcp.Dispose();
            return;
        }

        var clientId = Guid.NewGuid();
        var channel  = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _clients[clientId] = new Client { Channel = channel, DeviceId = ParseDev(requestLine) };

        long sent = 0;
        try
        {
            // Send HTTP response headers (chunked — an endless live stream, no Content-Length).
            var headers = BuildHttpHeaders();
            await stream.WriteAsync(headers, ct);

            // WAV mode only: send the WAV header + a silence prebuffer so the receiver
            // starts with a playback cushion. MP3 is self-framing and flows continuously
            // (the silence pump keeps frames coming), so it needs neither.
            if (_codec == StreamCodec.Wav && _waveFormat != null)
            {
                var wavHeader = BuildWavHeader(_waveFormat);
                await WriteChunkAsync(stream, wavHeader, ct);
                sent += wavHeader.Length;

                if (_silenceChunk.Length > 0)
                    for (var i = 0; i < PrebufferMs / SilenceIntervalMs; i++)
                    {
                        await WriteChunkAsync(stream, _silenceChunk, ct);
                        sent += _silenceChunk.Length;
                    }
            }
            Log.Write($"[http] streaming to {remote} (sent header, {sent} bytes so far)");

            // Stream PCM forever until disconnected or cancelled.
            var nextLog = 1_000_000L;
            await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
            {
                await WriteChunkAsync(stream, chunk, ct);
                sent += chunk.Length;
                if (sent >= nextLog)
                {
                    Log.Write($"[http] {remote}: {sent / 1024} KB streamed");
                    nextLog += 1_000_000L;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[http] {remote} ended: {ex.GetType().Name} {ex.Message} (sent {sent} bytes)");
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            tcp.Dispose();
            Log.Write($"[http] client {remote} disconnected (total {sent} bytes)");
        }
    }

    // Reads the HTTP request until the blank line that ends the headers.
    // Returns the request line (first line) for logging.
    private static async Task<string> DiscardHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var all   = new StringBuilder();
        var carry = new byte[4];
        var ci    = 0;

        // Look for \r\n\r\n — the end of HTTP headers.
        while (true)
        {
            var b = new byte[1];
            var read = await stream.ReadAsync(b, ct);
            if (read == 0) break;

            all.Append((char)b[0]);
            carry[ci++ % 4] = b[0];

            if (ci >= 4)
            {
                // Check last 4 bytes for \r\n\r\n
                if (carry[(ci - 4) % 4] == '\r' && carry[(ci - 3) % 4] == '\n' &&
                    carry[(ci - 2) % 4] == '\r' && carry[(ci - 1) % 4] == '\n')
                    break;
            }
        }

        var text = all.ToString();
        var nl   = text.IndexOf('\r');
        return nl > 0 ? text[..nl] : text.Trim();
    }

    // Serves the embedded "now casting" artwork as a normal (finite) PNG response.
    private static readonly byte[] ArtPng = LoadArt();
    private static async Task ServeArtAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: image/png\r\n" +
            $"Content-Length: {ArtPng.Length}\r\n" +
            "Cache-Control: max-age=3600\r\n" +
            "Connection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(header, ct);
            if (ArtPng.Length > 0) await stream.WriteAsync(ArtPng, ct);
        }
        catch { /* client went away */ }
    }

    private static byte[] LoadArt()
    {
        try
        {
            var asm  = typeof(LocalMediaServer).Assembly;
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("art.png", StringComparison.OrdinalIgnoreCase));
            if (name == null) return [];
            using var s  = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return []; }
    }

    private byte[] BuildHttpHeaders()
    {
        // Chunked transfer, no Content-Length: tells the receiver this is an endless
        // live stream so it uses a small live buffer instead of a large VOD buffer.
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 200 OK\r\n");
        sb.Append($"Content-Type: {ContentType}\r\n");
        sb.Append("Transfer-Encoding: chunked\r\n");
        sb.Append("Cache-Control: no-cache\r\n");
        sb.Append("Connection: keep-alive\r\n");
        sb.Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // Writes one HTTP chunk: "<hex length>\r\n<data>\r\n".
    private static async Task WriteChunkAsync(NetworkStream stream, byte[] data, CancellationToken ct)
    {
        if (data.Length == 0) return;
        var prefix = Encoding.ASCII.GetBytes($"{data.Length:X}\r\n");
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(data, ct);
        await stream.WriteAsync("\r\n"u8.ToArray(), ct);
    }

    // Builds a 44-byte standard PCM WAV header.
    // dataSize is set to 0x7FFFF000 — large but not 0xFFFFFFFF, which some players reject.
    private static byte[] BuildWavHeader(WaveFormat fmt)
    {
        // Normalise to PCM 16-bit — the format we stream.
        // (AudioCapture converts float32 to int16 before pushing here.)
        var channels      = (short)fmt.Channels;
        var sampleRate    = fmt.SampleRate;
        const short bits  = 16;
        const short pcm   = 1;
        var byteRate      = sampleRate * channels * bits / 8;
        var blockAlign    = (short)(channels * bits / 8);
        // 0xFFFFFFFF = "unknown/streaming" sentinel — avoids the receiver deriving a
        // finite duration (which made it buffer like a VOD file and added ~10s latency).
        const uint data   = 0xFFFFFFFFu;
        const uint riff   = 0xFFFFFFFFu;

        var h = new byte[44];
        var s = h.AsSpan();

        "RIFF"u8.CopyTo(s[0..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..],  riff);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 16);               // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], (ushort)pcm);      // audio format = PCM
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(s[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(s[34..], (ushort)bits);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], data);

        return h;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _silenceTimer?.Dispose();
        _mp3?.Dispose();
        _mp3Silence?.Dispose();
        _listener.Stop();

        foreach (var (_, c) in _clients)
            c.Channel.Writer.TryComplete();

        _cts.Dispose();
    }
}
