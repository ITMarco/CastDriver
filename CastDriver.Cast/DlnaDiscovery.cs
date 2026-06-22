using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace CastDriver.Cast;

// Discovers UPnP/DLNA MediaRenderers via SSDP (UDP multicast on 239.255.255.250:1900),
// then fetches each device's description XML to find its AVTransport / RenderingControl
// control URLs. Binds one socket per network interface (multi-homed / VPN friendly) and
// uses several search targets, since many devices only answer the broad "ssdp:all".
public sealed class DlnaDiscovery : IDisposable
{
    private const string MulticastIp = "239.255.255.250";
    private const int    SsdpPort    = 1900;

    private static readonly string[] SearchTargets =
    [
        "ssdp:all",
        "urn:schemas-upnp-org:device:MediaRenderer:1",
        "upnp:rootdevice",
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private readonly List<UdpClient>  _sockets       = [];
    private readonly HashSet<string>  _seenLocations = [];          // locations being/already fetched
    private readonly Dictionary<string, string>     _locationUdn = []; // location -> UDN
    private readonly Dictionary<string, DlnaDevice> _devices     = []; // UDN -> device
    private readonly Dictionary<string, DateTime>   _lastSeen    = []; // UDN -> last announce
    private readonly object           _gate = new();
    private CancellationTokenSource   _cts = new();
    private System.Timers.Timer?      _reQueryTimer;

    public event EventHandler<DlnaDevice>? DeviceFound;
    public event EventHandler<DlnaDevice>? DeviceLost;

    public void Start()
    {
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
                    socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    socket.Client.Bind(new IPEndPoint(ua.Address, 0));
                    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    _sockets.Add(socket);
                    _ = ReceiveLoopAsync(socket, _cts.Token);
                }
                catch { /* interface can't multicast — skip */ }
            }
        }

        if (_sockets.Count == 0)
        {
            try
            {
                var socket = new UdpClient(AddressFamily.InterNetwork);
                socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                _sockets.Add(socket);
                _ = ReceiveLoopAsync(socket, _cts.Token);
            }
            catch { return; }
        }

        Log.Write($"[dlna] SSDP discovery started on {_sockets.Count} interface(s)");
        SendSearch();

        _reQueryTimer = new System.Timers.Timer(30_000);
        _reQueryTimer.Elapsed += (_, _) => SendSearch();
        _reQueryTimer.Start();
    }

    // Re-send the SSDP M-SEARCH immediately (manual "refresh now"). Already-seen devices are
    // skipped by the location/UDN guards, so this surfaces newly-arrived renderers.
    public void Refresh()
    {
        if (_sockets.Count > 0) SendSearch();
    }

    private void SendSearch()
    {
        var target = new IPEndPoint(IPAddress.Parse(MulticastIp), SsdpPort);
        foreach (var st in SearchTargets)
        {
            var msg =
                "M-SEARCH * HTTP/1.1\r\n" +
                $"HOST: {MulticastIp}:{SsdpPort}\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                $"ST: {st}\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(msg);
            foreach (var s in _sockets)
                try { s.Send(bytes, bytes.Length, target); } catch { }
        }
    }

    private async Task ReceiveLoopAsync(UdpClient socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result   = await socket.ReceiveAsync(ct);
                var response = Encoding.ASCII.GetString(result.Buffer);
                var location = HeaderValue(response, "LOCATION");
                if (location != null)
                {
                    // If we already know this location, just refresh its last-seen time;
                    // only fetch the (slow) description XML for genuinely new locations.
                    string? knownUdn;
                    lock (_gate) _locationUdn.TryGetValue(location, out knownUdn);
                    if (knownUdn != null)
                        lock (_gate) _lastSeen[knownUdn] = DateTime.UtcNow;
                    else
                        _ = HandleLocationAsync(location);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore malformed */ }
        }
    }

    private async Task HandleLocationAsync(string location)
    {
        lock (_gate)
            if (!_seenLocations.Add(location)) return; // already processing/processed

        try
        {
            var xml  = await Http.GetStringAsync(location, _cts.Token);
            var doc  = XDocument.Parse(xml);
            var dev  = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
            if (dev == null) return;

            var name = Local(dev, "friendlyName") ?? "DLNA device";
            var udn  = Local(dev, "UDN") ?? location;

            // Resolve the base URL for relative control URLs.
            var baseUrl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value;
            var root    = new Uri(string.IsNullOrEmpty(baseUrl) ? location : baseUrl);

            var avControl  = FindControlUrl(doc, "AVTransport", root);
            if (avControl == null) return; // not a renderer we can drive
            var rcControl  = FindControlUrl(doc, "RenderingControl", root);

            DlnaDevice? device = null;
            lock (_gate)
            {
                _locationUdn[location] = udn;
                _lastSeen[udn] = DateTime.UtcNow;
                if (!_devices.ContainsKey(udn))
                {
                    // Sonos players have a UDN like "uuid:RINCON_XXXXXXXX..." — a reliable
                    // brand signal that lets us use the Sonos-tuned streaming path.
                    var isSonos = udn.Contains("RINCON_", StringComparison.OrdinalIgnoreCase);

                    device = new DlnaDevice
                    {
                        Name = name,
                        Id   = udn,
                        Host = root.Host,
                        AvTransportControlUrl      = avControl,
                        RenderingControlControlUrl = rcControl ?? "",
                        IsSonos = isSonos,
                    };
                    _devices[udn] = device;
                }
            }

            if (device != null)
            {
                Log.Write($"[dlna] found '{name}' @ {root.Host} (AVTransport {avControl})");
                DeviceFound?.Invoke(this, device);
            }
        }
        catch (Exception ex)
        {
            // Allow a transient failure to be retried on the next announce/refresh.
            lock (_gate) _seenLocations.Remove(location);
            Log.Write($"[dlna] description fetch failed: {ex.Message}");
        }
    }

    // Drop devices that haven't announced since the given cutoff (set just before a manual
    // refresh re-searched). Raises DeviceLost so the manager/UI can remove them.
    public void PruneStale(DateTime notSeenSince)
    {
        List<DlnaDevice> lost = [];
        lock (_gate)
        {
            foreach (var udn in _devices.Keys.ToList())
            {
                if (_lastSeen.TryGetValue(udn, out var ts) && ts >= notSeenSince) continue;
                if (_devices.Remove(udn, out var dev))
                {
                    _lastSeen.Remove(udn);
                    lost.Add(dev);
                    // Forget its location(s) so the device can be re-discovered if it returns.
                    foreach (var loc in _locationUdn.Where(kv => kv.Value == udn)
                                                    .Select(kv => kv.Key).ToList())
                    {
                        _locationUdn.Remove(loc);
                        _seenLocations.Remove(loc);
                    }
                }
            }
        }
        foreach (var d in lost) DeviceLost?.Invoke(this, d);
    }

    private static string? FindControlUrl(XDocument doc, string serviceSuffix, Uri root)
    {
        foreach (var svc in doc.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            var type = Local(svc, "serviceType");
            if (type == null || !type.Contains(serviceSuffix, StringComparison.OrdinalIgnoreCase)) continue;

            var control = Local(svc, "controlURL");
            if (string.IsNullOrEmpty(control)) continue;

            return new Uri(root, control).ToString();
        }
        return null;
    }

    private static string? Local(XElement parent, string localName) =>
        parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static string? HeaderValue(string response, string header)
    {
        foreach (var line in response.Split("\r\n"))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            if (line[..idx].Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
                return line[(idx + 1)..].Trim();
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _reQueryTimer?.Dispose();
        foreach (var s in _sockets) s.Dispose();
        _cts.Dispose();
    }
}
