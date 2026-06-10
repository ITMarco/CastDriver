namespace CastDriver.Cast;

public sealed class ChromecastDevice : ICastDevice
{
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int    Port { get; init; } = 8009;

    public string   Id   => Host;
    public CastKind Kind => CastKind.Chromecast;

    public override string ToString() => Name;
}
