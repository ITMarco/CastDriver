using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;

namespace CastDriver.UI;

public partial class AboutWindow : Window
{
    private string _updateUrl = "";

    private bool _ready;

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text       = $"Version {AppInfo.Short}";
        StartupCheck.IsChecked   = StartupRegistration.Enabled;
        MinimizedCheck.IsChecked = AppSettings.Current.StartMinimized;
        DisableCheck.IsChecked   = AppSettings.Current.DisableUpdateCheck;

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
                UpdateStatus.Text        = $"Update available: {res.LatestTag}";
                DownloadButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateStatus.Text = $"You're up to date ({AppInfo.Display}).";
            }
        }
        finally { CheckButton.IsEnabled = true; }
    }

    private void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_updateUrl)) OpenUrl(_updateUrl);
    }

    private void OnToggleDisableCheck(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.DisableUpdateCheck = DisableCheck.IsChecked == true;
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* nothing sensible to do */ }
    }
}
