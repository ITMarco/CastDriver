namespace CastDriver.Cast;

// What to play, plus the metadata receivers display ("now casting").
public sealed record CastMedia(string Url, string ContentType, string Title, string ArtUrl);

// A live casting session to one device. Chromecast and DLNA each implement this.
public interface ICastSession : IAsyncDisposable
{
    bool IsActive { get; }

    event EventHandler?         Disconnected;
    event EventHandler<string>? ErrorOccurred;
    event EventHandler<float>?  VolumeReported;   // device's own volume, 0–1

    Task StartAsync(CastMedia media, CancellationToken ct = default);
    Task SetVolumeAsync(float level, CancellationToken ct = default);
}
