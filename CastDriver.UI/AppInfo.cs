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

    // Which release asset this build updates itself with. The standalone (self-contained)
    // publish defines SELF_CONTAINED via release.ps1; the framework build doesn't.
#if SELF_CONTAINED
    public const string UpdateAssetName = "CastDriver-standalone.exe";
#else
    public const string UpdateAssetName = "CastDriver.exe";
#endif
}
