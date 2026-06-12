using System.Windows;
using System.Windows.Input;
using CastDriver.UI.ViewModels;

namespace CastDriver.UI;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        ApplyPinned(AppSettings.Current.Pinned);
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();

    // When pinned, the window stays put (like a normal window) instead of auto-hiding.
    public bool IsPinned { get; private set; }

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        ApplyPinned(!IsPinned);
        AppSettings.Current.Pinned = IsPinned;
        AppSettings.Current.Save();
    }

    private void ApplyPinned(bool pinned)
    {
        IsPinned             = pinned;
        ShowInTaskbar        = pinned;          // so it can be minimised to the taskbar
        Topmost              = !pinned;         // pinned = behaves like a normal window
        PinButton.Opacity    = pinned ? 1.0 : 0.5;
        PinButton.ToolTip    = pinned ? "Unpin (auto-hide)" : "Pin (keep open)";
        MinButton.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnExit(object sender, RoutedEventArgs e) =>
        (System.Windows.Application.Current as App)?.ExitApp();

    private void OnOpenAbout(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void OnOpenEq(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            new EqWindow { Owner = this, DataContext = vm.Eq }.Show();
    }

    // Clicking outside the window hides it — unless the user pinned it open.
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!IsPinned) Hide();
    }

    public void ShowAtTrayCorner()
    {
        // Refresh the source/app list each time the window opens so newly-playing apps appear.
        (DataContext as MainViewModel)?.RefreshSources();

        // A pinned window keeps its current position; only reposition when auto-hiding.
        if (!IsPinned)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right  - Width  - 12;
            Top  = area.Bottom - Height - 12;
        }
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
