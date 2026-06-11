using System.Diagnostics;

namespace CastDriver.UI;

// Adds a Windows Firewall inbound rule for this exe so cast devices can reach the local
// media server. Adding a rule requires admin, so we launch an elevated PowerShell window
// (UAC prompt) — the user doesn't need to know to "run as administrator".
public static class FirewallHelper
{
    public static void AddRuleElevated()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        var script =
            "Remove-NetFirewallRule -DisplayName 'CastDriver' -ErrorAction SilentlyContinue; " +
            $"New-NetFirewallRule -DisplayName 'CastDriver' -Direction Inbound -Program '{exe}' " +
            "-Action Allow -Profile Any | Out-Null; " +
            "Write-Host 'CastDriver is now allowed through Windows Firewall.' -ForegroundColor Green; " +
            "Write-Host 'You can close this window and start casting again.'; " +
            "Read-Host 'Press Enter to close'";

        var psi = new ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            UseShellExecute = true,
            Verb            = "runas", // triggers the UAC elevation prompt
        };

        try { Process.Start(psi); }
        catch { /* user declined the UAC prompt — nothing to do */ }
    }
}
