using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;

namespace CastDriver.UI;

// In-app updater. Downloads the matching release asset directly (no browser, so no
// Mark-of-the-Web / SmartScreen download block), verifies it, then has the freshly-downloaded
// build install itself over the running one and relaunch.
public static class SelfUpdater
{
    private static string StageDir =>
        Path.Combine(Path.GetTempPath(), "CastDriver", "update");

    // Self-update only makes sense for the published .exe (not a `dotnet App.dll` dev run),
    // and only when we can actually overwrite the running exe in place.
    public static bool CanSelfUpdate(out string reason)
    {
        reason = "";
        var exe = Environment.ProcessPath;
        if (exe == null ||
            Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            reason = "running from the .NET host (dev build)";
            return false;
        }
        try
        {
            var probe = exe + ".update-probe";
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            reason = "the app folder isn't writable";
            return false;
        }
    }

    // Download the matching asset, verify its hash, and launch it to install itself over us.
    // Returns true once the installer is launched — the caller should then exit the app.
    public static async Task<bool> DownloadAndApplyAsync(
        string assetUrl, string? expectedSha256, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(StageDir);
        var staged = Path.Combine(StageDir, AppInfo.UpdateAssetName);

        // 1. Stream the download to disk, reporting progress.
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CastDriver-updater");
            using var resp = await http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1L;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(staged);
            var buffer = new byte[81920];
            long readTotal = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                readTotal += n;
                if (total > 0) progress?.Report((double)readTotal / total);
            }
        }

        // 2. Strip the Mark-of-the-Web if present (a raw download won't have one — defensive).
        try { File.Delete(staged + ":Zone.Identifier"); } catch { /* none present */ }

        // 3. Verify integrity before we run anything.
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = await ComputeSha256Async(staged, ct);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(staged); } catch { /* best-effort */ }
                throw new InvalidOperationException("Downloaded update failed its integrity check.");
            }
        }

        // 4. Hand off: the new build installs itself over us (it understands --apply-update).
        var target = Environment.ProcessPath!;
        var pid    = Environment.ProcessId;
        Process.Start(new ProcessStartInfo(staged, $"--apply-update \"{target}\" {pid}")
        {
            UseShellExecute = true,
        });
        return true;
    }

    // Installer mode: invoked on a freshly-downloaded build via
    //   <new>.exe --apply-update "<targetPath>" <oldPid>
    // Waits for the old instance to exit, copies itself over it, and relaunches.
    public static void ApplyUpdate(string targetPath, string pidText)
    {
        if (int.TryParse(pidText, out var pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(15000); }
            catch { /* already exited */ }
        }

        var src = Environment.ProcessPath!;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try { File.Copy(src, targetPath, overwrite: true); break; }
            catch { Thread.Sleep(500); } // the old exe's handle may take a moment to release
        }

        try { Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true }); }
        catch { /* nothing more we can do here */ }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash);
    }
}
