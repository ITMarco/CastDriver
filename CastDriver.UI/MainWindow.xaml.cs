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
        IsPinned = !IsPinned;
        ShowInTaskbar        = IsPinned;        // so it can be minimised to the taskbar
        Topmost              = !IsPinned;       // pinned = behaves like a normal window
        PinButton.Opacity    = IsPinned ? 1.0 : 0.5;
        PinButton.ToolTip    = IsPinned ? "Unpin (auto-hide)" : "Pin (keep open)";
        MinButton.Visibility = IsPinned ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private DebugWindow? _debugWindow;
    private void OnOpenDebug(object sender, RoutedEventArgs e)
    {
        if (_debugWindow is { IsVisible: true })
        {
            _debugWindow.Activate();
            return;
        }
        _debugWindow = new DebugWindow { Owner = this };
        _debugWindow.Show();
    }

    private void OnOpenAbout(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    // Clicking outside the window hides it — unless the user pinned it open.
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!IsPinned) Hide();
    }

    public void ShowAtTrayCorner()
    {
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
