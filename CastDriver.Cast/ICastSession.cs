namespace CastDriver.Cast;

// A live casting session to one device. Chromecast and DLNA each implement this.
public interface ICastSession : IAsyncDisposable
{
    bool IsActive { get; }

    event EventHandler?         Disconnected;
    event EventHandler<string>? ErrorOccurred;
    event EventHandler<float>?  VolumeReported;   // device's own volume, 0–1

    Task StartAsync(string audioUrl, CancellationToken ct = default);
    Task SetVolumeAsync(float level, CancellationToken ct = default);
}
