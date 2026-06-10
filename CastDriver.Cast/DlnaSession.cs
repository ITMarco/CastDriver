using System.Net.Http;
using System.Text;
using System.Xml.Linq;

namespace CastDriver.Cast;

// Casts to a UPnP/DLNA MediaRenderer via SOAP: SetAVTransportURI + Play, and
// SetVolume/GetVolume on the RenderingControl service.
public sealed class DlnaSession : ICastSession
{
    private const string AvTransport      = "urn:schemas-upnp-org:service:AVTransport:1";
    private const string RenderingControl = "urn:schemas-upnp-org:service:RenderingControl:1";

    // Generous timeout: on the first cast the renderer often holds the SOAP response
    // open while it shows an on-screen "allow casting?" prompt the user must accept.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly DlnaDevice _device;

    public bool IsActive { get; private set; }

    public event EventHandler?         Disconnected;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<float>?  VolumeReported;

    public DlnaSession(DlnaDevice device) => _device = device;

    public async Task StartAsync(string audioUrl, string contentType, CancellationToken ct = default)
    {
        try
        {
            var metadata = BuildDidl(audioUrl, contentType);
            Log.Write($"[dlna] SetAVTransportURI {audioUrl} → {_device.Name}");
            await SendTolerantAsync(_device.AvTransportControlUrl, AvTransport, "SetAVTransportURI",
                $"<InstanceID>0</InstanceID><CurrentURI>{Xml(audioUrl)}</CurrentURI>" +
                $"<CurrentURIMetaData>{Xml(metadata)}</CurrentURIMetaData>", ct);

            await SendTolerantAsync(_device.AvTransportControlUrl, AvTransport, "Play",
                "<InstanceID>0</InstanceID><Speed>1</Speed>", ct);

            IsActive = true;

            // Report the device's current volume so the UI slider reflects it.
            _ = ReportVolumeAsync();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
            throw;
        }
    }

    // Some renderers (notably LG webOS) hold the SOAP response open until playback has
    // begun — sometimes indefinitely — even though the command was received and works.
    // So we cap the wait: if the device doesn't answer in time we assume it accepted the
    // command and move on. A genuine connection failure (HttpRequestException) still throws.
    private async Task SendTolerantAsync(
        string controlUrl, string serviceType, string action, string argsXml, CancellationToken callerCt)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await SoapAsync(controlUrl, serviceType, action, argsXml, timeout.Token);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            Log.Write($"[dlna] {action} had no response in 8s — assuming the device accepted it");
        }
    }

    public async Task SetVolumeAsync(float level, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_device.RenderingControlControlUrl)) return;
        var vol = (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100);
        try
        {
            await SoapAsync(_device.RenderingControlControlUrl, RenderingControl, "SetVolume",
                $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>{vol}</DesiredVolume>", ct);
        }
        catch (Exception ex) { Log.Write($"[dlna] SetVolume failed: {ex.Message}"); }
    }

    private async Task ReportVolumeAsync()
    {
        if (string.IsNullOrEmpty(_device.RenderingControlControlUrl)) return;
        try
        {
            var resp = await SoapAsync(_device.RenderingControlControlUrl, RenderingControl, "GetVolume",
                "<InstanceID>0</InstanceID><Channel>Master</Channel>", CancellationToken.None);
            var val = XDocument.Parse(resp).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "CurrentVolume")?.Value;
            if (int.TryParse(val, out var v))
                VolumeReported?.Invoke(this, v / 100f);
        }
        catch { /* volume reporting is best-effort */ }
    }

    public async Task StopAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await SoapAsync(_device.AvTransportControlUrl, AvTransport, "Stop",
                "<InstanceID>0</InstanceID>", cts.Token);
        }
        catch { /* best-effort on teardown */ }
        IsActive = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    // ── SOAP helper ────────────────────────────────────────────────────────────

    private static async Task<string> SoapAsync(
        string controlUrl, string serviceType, string action, string argsXml, CancellationToken ct)
    {
        var body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
            $"<u:{action} xmlns:u=\"{serviceType}\">{argsXml}</u:{action}>" +
            "</s:Body></s:Envelope>";

        using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
        req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        req.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{action}\"");

        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{action} → HTTP {(int)resp.StatusCode}");
        return text;
    }

    // Minimal DIDL-Lite metadata so renderers that require it will accept the stream.
    private static string BuildDidl(string url, string contentType) =>
        "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" " +
        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">" +
        "<item id=\"0\" parentID=\"-1\" restricted=\"1\">" +
        "<dc:title>CastDriver</dc:title>" +
        "<upnp:class>object.item.audioItem.musicTrack</upnp:class>" +
        $"<res protocolInfo=\"http-get:*:{contentType}:*\">{Xml(url)}</res>" +
        "</item></DIDL-Lite>";

    private static string Xml(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    public async ValueTask DisposeAsync() => await StopAsync();
}
