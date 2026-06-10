using System.Windows;
using CastDriver.Cast;

namespace CastDriver.UI;

public partial class DebugWindow : Window
{
    public DebugWindow()
    {
        InitializeComponent();
        LoggingCheck.IsChecked = Log.Enabled;
        PathText.Text          = $"Log file: {Log.FilePath}";
        RefreshLog();
    }

    private void OnToggleLogging(object sender, RoutedEventArgs e) =>
        Log.Enabled = LoggingCheck.IsChecked == true;

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshLog();

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        Log.Delete();
        RefreshLog();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void RefreshLog()
    {
        var text = Log.Read();
        LogText.Text = string.IsNullOrEmpty(text) ? "(log is empty)" : text;
        LogText.ScrollToEnd();
    }
}
