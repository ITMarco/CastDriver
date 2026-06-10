using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;

namespace CastDriver.UI;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Short}";
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* nothing sensible to do */ }
    }
}
