namespace CastDriver.Cast;

public sealed class ChromecastDevice
{
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 8009;

    public override string ToString() => Name;
}
