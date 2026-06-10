using System.Reflection;

namespace CastDriver.UI;

// Single source of truth for the app version (driven by <Version> in the .csproj).
public static class AppInfo
{
    public const string Repo = "ITMarco/CastDriver";

    public static Version Version =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);

    public static string Short   => $"{Version.Major}.{Version.Minor}";
    public static string Display => "v" + Short;
}
