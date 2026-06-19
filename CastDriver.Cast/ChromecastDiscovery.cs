using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace CastDriver.Cast;

// Discovers Chromecast devices via raw mDNS (UDP multicast on 224.0.0.251:5353).
// We roll our own instead of relying on the library's higher-level ServiceDiscovery
// because Chromecast responses spread PTR/SRV/TXT/A records across separate messages
// and most library abstractions miss them.
public sealed class ChromecastDiscovery : IDisposable
{
    private const string MulticastGroup = "224.0.0.251";
    private const int    MdnsPort       = 5353;
    private const string ServiceType    = "_googlecast._tcp.local";

    private readonly List<UdpClient>  _sockets  = [];
    private readonly Dictionary<string, ChromecastDevice> _seen = [];
    private readonly Dictionary<string, PendingDevice>    _pending = [];
    private CancellationTokenSource   _cts = new();
    private System.Timers.Timer?      _reQueryTimer;

    public event EventHandler<ChromecastDevice>? DeviceFound;
    public event EventHandler<ChromecastDevice>? DeviceLost;

    public void Start()
    {
        // Bind one UDP socket per active non-loopback IPv4 interface so we reach
        // Chromecasts on all local subnets (especially with multiple adapters / VPNs).
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in iface.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                try
                {
                    var socket = new UdpClient(AddressFamily.InterNetwork);
                    socket.Client.SetSocketOption(SocketOptionLevel.Socket,
                        SocketOptionName.ReuseAddress, true);
                    socket.Client.Bind(new IPEndPoint(ua.Address, MdnsPort));
                    socket.JoinMulticastGroup(IPAddress.Parse(MulticastGroup), ua.Address);
                    socket.MulticastLoopback = false;
                    _sockets.Add(socket);

                    _ = ReceiveLoopAsync(socket, _cts.Token);
                }
                catch { /* interface doesn't support multicast — skip */ }
            }
        }

        if (_sockets.Count == 0)
        {
            // Fallback: single socket bound to any interface
            var socket = new UdpClient(MdnsPort);
            socket.JoinMulticastGroup(IPAddress.Parse(MulticastGroup));
            _sockets.Add(socket);
            _ = ReceiveLoopAsync(socket, _cts.Token);
        }

        SendQuery();

        // Re-query every 30 s — Chromecast TTLs are short and devices may join late.
        _reQueryTimer = new System.Timers.Timer(30_000);
        _reQueryTimer.Elapsed += (_, _) => SendQuery();
        _reQueryTimer.Start();
    }

    // Re-send the discovery query immediately (manual "refresh now"). Known devices stay;
    // this just prompts late/new devices and ones with expired TTLs to re-announce.
    public void Refresh()
    {
        if (_sockets.Count > 0) SendQuery();
    }

    // ── Send PTR query ────────────────────────────────────────────────────────

    private void SendQuery()
    {
        var packet = BuildPtrQuery(ServiceType + ".");
        var target = new IPEndPoint(IPAddress.Parse(MulticastGroup), MdnsPort);
        foreach (var s in _sockets)
        {
            try { s.Send(packet, packet.Length, target); } catch { }
        }
    }

    // ── Receive loop ──────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(UdpClient socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveAsync(ct);
                ParseMessage(result.Buffer, result.RemoteEndPoint.Address);
            }
            catch (OperationCanceledException) { break; }
            catch { /* malformed packet — ignore */ }
        }
    }

    // ── DNS message parser ────────────────────────────────────────────────────

    private void ParseMessage(byte[] data, IPAddress senderIp)
    {
        var r = new DnsReader(data);
        if (!r.ReadHeader(out _, out int answers, out int authority, out int additional))
            return;

        // Skip questions section.
        if (!r.SkipQuestions()) return;

        // Collect all records from all sections.
        var totalRecords = answers + authority + additional;
        string? ptrTarget = null;
        string? friendlyName = null;
        int     port = 8009;
        IPAddress? hostIp = null;
        string? instanceName = null;

        for (int i = 0; i < totalRecords; i++)
        {
            if (!r.ReadRecord(out var name, out var type, out var rdata)) break;

            switch (type)
            {
                case 12: // PTR
                    var ptr = ParseLabel(rdata);
                    if (name.Contains("_googlecast._tcp", StringComparison.OrdinalIgnoreCase))
                    {
                        ptrTarget    = ptr;
                        instanceName = ptr?.Split('.')[0];
                    }
                    break;

                case 33: // SRV
                    if (rdata.Length >= 6)
                    {
                        port = (rdata[4] << 8) | rdata[5];
                        // bytes 6..end = target hostname (labels) — we use senderIp instead
                    }
                    break;

                case 16: // TXT
                    friendlyName = ParseTxtFriendlyName(rdata);
                    break;

                case 1: // A
                    if (rdata.Length == 4)
                        hostIp = new IPAddress(rdata);
                    break;
            }
        }

        // Use sender IP as fallback when A record is absent (common in same-subnet responses).
        hostIp ??= senderIp;

        if (ptrTarget == null || hostIp == null) return;

        var key = hostIp.ToString();

        // Accumulate partial info across messages before emitting.
        if (!_pending.TryGetValue(key, out var p))
            p = _pending[key] = new PendingDevice { Host = hostIp };

        if (instanceName  != null) p.InstanceName  = instanceName;
        if (friendlyName  != null) p.FriendlyName  = friendlyName;
        if (port != 8009  || p.Port == 8009) p.Port = port;

        if (_seen.ContainsKey(key)) return; // already announced

        var displayName = p.FriendlyName ?? p.InstanceName ?? key;
        var device = new ChromecastDevice { Name = displayName, Host = key, Port = p.Port };
        _seen[key] = device;
        DeviceFound?.Invoke(this, device);
    }

    // ── DNS helpers ───────────────────────────────────────────────────────────

    // Build a minimal DNS PTR query packet.
    private static byte[] BuildPtrQuery(string name)
    {
        using var ms  = new MemoryStream();
        using var bw  = new BinaryWriter(ms);

        bw.Write((ushort)0);      // ID = 0 for mDNS
        bw.Write(ToBigEndian16(0x0000)); // flags: standard query
        bw.Write(ToBigEndian16(1));      // QDCOUNT = 1
        bw.Write((ushort)0);             // ANCOUNT
        bw.Write((ushort)0);             // NSCOUNT
        bw.Write((ushort)0);             // ARCOUNT

        // Question: name labels
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            bw.Write((byte)bytes.Length);
            bw.Write(bytes);
        }
        bw.Write((byte)0);               // root label
        bw.Write(ToBigEndian16(12));     // QTYPE = PTR
        bw.Write(ToBigEndian16(1));      // QCLASS = IN

        return ms.ToArray();
    }

    private static string? ParseLabel(byte[] data)
    {
        var sb = new StringBuilder();
        int i  = 0;
        while (i < data.Length && data[i] != 0)
        {
            int len = data[i++];
            if ((len & 0xC0) == 0xC0) break; // pointer — skip
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(data, i, Math.Min(len, data.Length - i)));
            i += len;
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static string? ParseTxtFriendlyName(byte[] data)
    {
        int i = 0;
        while (i < data.Length)
        {
            int len = data[i++];
            if (len == 0 || i + len > data.Length) break;
            var kv = Encoding.UTF8.GetString(data, i, len);
            if (kv.StartsWith("fn=", StringComparison.OrdinalIgnoreCase))
                return kv[3..];
            i += len;
        }
        return null;
    }

    private static ushort ToBigEndian16(ushort v) =>
        (ushort)((v >> 8) | (v << 8));

    public void Dispose()
    {
        _cts.Cancel();
        _reQueryTimer?.Dispose();
        foreach (var s in _sockets) s.Dispose();
        _cts.Dispose();
    }

    private sealed class PendingDevice
    {
        public IPAddress Host          { get; set; } = IPAddress.None;
        public string?   InstanceName  { get; set; }
        public string?   FriendlyName  { get; set; }
        public int       Port          { get; set; } = 8009;
    }

    // ── Minimal DNS wire-format reader ────────────────────────────────────────

    private sealed class DnsReader(byte[] data)
    {
        private int _pos;

        public bool ReadHeader(out int id, out int anCount, out int nsCount, out int arCount)
        {
            id = anCount = nsCount = arCount = 0;
            if (data.Length < 12) return false;
            id      = (data[0] << 8) | data[1];
            // flags at 2-3, skip
            var qdCount = (data[4] << 8) | data[5];
            anCount     = (data[6] << 8) | data[7];
            nsCount     = (data[8] << 8) | data[9];
            arCount     = (data[10] << 8) | data[11];
            _pos = 12;
            return true;
        }

        public bool SkipQuestions()
        {
            // We don't track the question count here — called after ReadHeader so caller skips them.
            // Actually questions were counted in ReadHeader but not stored — skip whole section by
            // re-reading the count from bytes 4-5.
            int qdCount = (data[4] << 8) | data[5];
            for (int q = 0; q < qdCount; q++)
            {
                if (!SkipLabels()) return false;
                _pos += 4; // QTYPE + QCLASS
            }
            return true;
        }

        public bool ReadRecord(out string name, out int type, out byte[] rdata)
        {
            name  = "";
            type  = 0;
            rdata = [];
            if (_pos >= data.Length) return false;

            name = ReadLabels();
            if (_pos + 10 > data.Length) return false;

            type     = (data[_pos] << 8) | data[_pos + 1]; _pos += 2;
            _pos    += 2; // class
            _pos    += 4; // TTL
            int rdlen = (data[_pos] << 8) | data[_pos + 1]; _pos += 2;

            if (_pos + rdlen > data.Length) return false;
            rdata = data[_pos..(_pos + rdlen)];
            _pos += rdlen;
            return true;
        }

        private string ReadLabels()
        {
            var sb = new StringBuilder();
            while (_pos < data.Length)
            {
                int len = data[_pos];
                if (len == 0) { _pos++; break; }
                if ((len & 0xC0) == 0xC0)
                {
                    // Pointer — jump but don't follow for the name string
                    _pos += 2; break;
                }
                _pos++;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.ASCII.GetString(data, _pos, Math.Min(len, data.Length - _pos)));
                _pos += len;
            }
            return sb.ToString();
        }

        private bool SkipLabels()
        {
            while (_pos < data.Length)
            {
                int len = data[_pos];
                if (len == 0) { _pos++; return true; }
                if ((len & 0xC0) == 0xC0) { _pos += 2; return true; }
                _pos += 1 + len;
            }
            return false;
        }
    }
}
