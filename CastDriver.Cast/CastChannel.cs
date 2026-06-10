using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using CastDriver.Cast.Proto;

namespace CastDriver.Cast;

// Low-level Cast V2 transport: TLS socket + 4-byte big-endian length prefix framing.
internal sealed class CastChannel : IAsyncDisposable
{
    private readonly TcpClient  _tcp;
    private readonly SslStream  _ssl;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private CastChannel(TcpClient tcp, SslStream ssl)
    {
        _tcp = tcp;
        _ssl = ssl;
    }

    public static async Task<CastChannel> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, ct);

        // Chromecasts use self-signed certificates — accept them unconditionally.
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        }, ct);

        return new CastChannel(tcp, ssl);
    }

    public async Task SendAsync(CastMessage msg, CancellationToken ct = default)
    {
        var payload = msg.ToByteArray();
        var header  = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);

        await _writeLock.WaitAsync(ct);
        try
        {
            await _ssl.WriteAsync(header,  ct);
            await _ssl.WriteAsync(payload, ct);
            await _ssl.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<CastMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(header, ct)) return null;

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > 64 * 1024) return null; // sanity guard

        var body = new byte[length];
        if (!await ReadExactAsync(body, ct)) return null;

        return CastMessage.FromByteArray(body);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _ssl.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        await _ssl.DisposeAsync();
        _tcp.Dispose();
    }
}
