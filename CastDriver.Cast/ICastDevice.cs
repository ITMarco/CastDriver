namespace CastDriver.Cast;

public enum CastKind { Chromecast, Dlna }

// A castable target, regardless of protocol. Id is a stable unique key; Host is the
// device's IP (used to pick the right local network interface for the stream URL).
public interface ICastDevice
{
    string   Name { get; }
    string   Id   { get; }
    string   Host { get; }
    CastKind Kind { get; }
}

// A UPnP/DLNA MediaRenderer (smart TVs, AV receivers, Sonos, networked speakers).
public sealed class DlnaDevice : ICastDevice
{
    public string Name { get; init; } = "";
    public string Id   { get; init; } = "";   // device UDN
    public string Host { get; init; } = "";

    // Absolute SOAP control URLs for the two services we use.
    public string AvTransportControlUrl      { get; init; } = "";
    public string RenderingControlControlUrl { get; init; } = "";

    // True for Sonos players (UDN starts with RINCON_). Enables the Sonos-tuned streaming
    // path; the friendlier "Sonos" label is shown in the UI.
    public bool IsSonos { get; init; }

    // Per-device opt-out: when true, use the plain generic DLNA path instead of the Sonos
    // fast path (fallback for players where the optimised path misbehaves). Mutable so the
    // UI can flip it at runtime.
    public bool SonosCompatibilityMode { get; set; }

    public CastKind Kind => CastKind.Dlna;

    public override string ToString() => Name;
}
