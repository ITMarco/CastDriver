using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;

namespace CastDriver.UI;

public partial class AboutWindow : Window
{
    private string  _updateUrl = "";
    private string? _assetUrl;
    private string? _assetSha;

    private bool _ready;

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text       = $"Version {AppInfo.Short}";
        StartupCheck.IsChecked   = StartupRegistration.Enabled;
        MinimizedCheck.IsChecked = AppSettings.Current.StartMinimized;
        DisableCheck.IsChecked   = AppSettings.Current.DisableUpdateCheck;
        NotificationsCheck.IsChecked = AppSettings.Current.SuppressNotifications;

        ThemeCombo.ItemsSource = new[] { "Dark", "Light" };
        ThemeCombo.SelectedItem = ThemeManager.Current.ToString();
        _ready = true;
    }

    private void OnThemeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_ready || ThemeCombo.SelectedItem is not string name) return;
        ThemeManager.Apply(ThemeManager.Parse(name));
        AppSettings.Current.Theme = name;
        AppSettings.Current.Save();
    }

    private void OnToggleStartup(object sender, RoutedEventArgs e) =>
        StartupRegistration.Enabled = StartupCheck.IsChecked == true;

    private void OnToggleStartMinimized(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.StartMinimized = MinimizedCheck.IsChecked == true;
        AppSettings.Current.Save();
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled    = false;
        DownloadButton.Visibility = Visibility.Collapsed;
        UpdateStatus.Text        = "Checking…";
        try
        {
            var res = await UpdateChecker.CheckAsync(AppInfo.Version);
            if (res == null)
            {
                UpdateStatus.Text = "Couldn't check (no connection?).";
            }
            else if (res.Available)
            {
                _updateUrl               = res.Url;
                _assetUrl                = res.AssetUrl;
                _assetSha                = res.AssetSha256;
                UpdateStatus.Text        = string.IsNullOrWhiteSpace(res.Feature)
                    ? $"Update available: {res.LatestTag}"
                    : $"Update available: {res.LatestTag} — {res.Feature}";
                DownloadButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateStatus.Text = $"You're up to date ({AppInfo.Display}).";
            }
        }
        finally { CheckButton.IsEnabled = true; }
    }

    private async void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_assetUrl) || !SelfUpdater.CanSelfUpdate(out _))
        {
            if (!string.IsNullOrEmpty(_updateUrl)) OpenUrl(_updateUrl);
            return;
        }

        try
        {
            DownloadButton.IsEnabled = false;
            var progress = new Progress<double>(p => UpdateStatus.Text = $"Downloading… {(int)(p * 100)}%");
            if (await SelfUpdater.DownloadAndApplyAsync(_assetUrl, _assetSha, progress))
            {
                UpdateStatus.Text = "Restarting to finish update…";
                (System.Windows.Application.Current as App)?.ExitApp();
            }
        }
        catch
        {
            UpdateStatus.Text = "Update failed — opening the download page";
            if (!string.IsNullOrEmpty(_updateUrl)) OpenUrl(_updateUrl);
        }
        finally { DownloadButton.IsEnabled = true; }
    }

    private void OnToggleDisableCheck(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.DisableUpdateCheck = DisableCheck.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnToggleSuppressNotifications(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.SuppressNotifications = NotificationsCheck.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OnViewLicense(object sender, RoutedEventArgs e)
    {
        // Prefer the copy bundled next to the exe; fall back to the canonical URL.
        var local = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        if (File.Exists(local)) OpenUrl(local);
        else                    OpenUrl("https://www.gnu.org/licenses/gpl-3.0.txt");
    }

    private void OnOpenDebug(object sender, RoutedEventArgs e) =>
        new DebugWindow { Owner = this }.ShowDialog();

    private void OnResetPreferences(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(this,
            "Reset all preferences to their defaults? This clears your theme, Sonos streaming modes, " +
            "notification and dialog choices, saved EQ presets, and other settings.\n\nThe app will restart.",
            "Reset all preferences", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        AppSettings.ResetAll();

        // Relaunch a fresh instance, then shut this one down so the defaults take effect.
        // Published builds run as the .exe (ProcessPath is the app); a dev `dotnet App.dll`
        // run has ProcessPath = the dotnet host, so relaunch the DLL through it instead.
        var host = Environment.ProcessPath;
        var dll  = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (host != null)
        {
            var underDotnet = Path.GetFileNameWithoutExtension(host)
                                  .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            var psi = underDotnet && !string.IsNullOrEmpty(dll)
                ? new ProcessStartInfo(host, $"\"{dll}\"") { UseShellExecute = false }
                : new ProcessStartInfo(host) { UseShellExecute = true };
            Process.Start(psi);
        }
        System.Windows.Application.Current.Shutdown();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* nothing sensible to do */ }
    }
}
