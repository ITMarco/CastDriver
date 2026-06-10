using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CastDriver.Driver;

public static class DriverInstaller
{
    public static bool IsInstalled()
    {
        // Check if the Scream virtual audio device is present
        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT * FROM Win32_SoundDevice WHERE Name LIKE '%Scream%'");
        return searcher.Get().Count > 0;
    }

    public static async Task<bool> InstallAsync()
    {
        var arch = GetArchFolder();
        var driverDir = Path.Combine(AppContext.BaseDirectory, "ScreamDriver", arch);
        var infPath = Path.Combine(driverDir, "Scream.inf");

        if (!File.Exists(infPath))
            throw new FileNotFoundException($"Scream driver not found at {infPath}. See ScreamDriver/README.txt.");

        // pnputil requires elevation — caller must ensure the process is elevated
        var psi = new ProcessStartInfo
        {
            FileName = "pnputil.exe",
            Arguments = $"/add-driver \"{infPath}\" /install",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0;
    }

    public static async Task<bool> UninstallAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pnputil.exe",
            Arguments = "/delete-driver oem*.inf /uninstall /force",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        // A proper uninstall would find the exact oem INF name first — placeholder for now
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0;
    }

    private static string GetArchFolder()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return "arm64";
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86) return "x86";
        return "x64";
    }
}
