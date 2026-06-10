using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Forms;
using CastDriver.UI.ViewModels;
using Application = System.Windows.Application;

namespace CastDriver.UI;

public partial class App : Application
{
    private NotifyIcon?    _trayIcon;
    private MainWindow?    _window;
    private MainViewModel? _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _vm     = new MainViewModel();
        _window = new MainWindow(_vm);

        BuildTrayIcon();

        // Show the window on launch rather than starting hidden in the tray.
        // (A "start minimized" preference will join the planned "auto-stream on start".)
        _window.ShowAtTrayCorner();
    }

    private void BuildTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Icon    = CreateCastIcon(active: false),
            Visible = true,
            Text    = "Cast Sound",
        };

        // Left click: toggle the popup window.
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ToggleWindow();
        };

        // Right-click context menu.
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => ToggleWindow());
        menu.Items.Add("-");
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;

        // Update tray icon colour when casting state changes.
        if (_vm != null)
            _vm.Devices.CollectionChanged += (_, _) => UpdateTrayIcon();
    }

    private void ToggleWindow()
    {
        if (_window == null) return;

        if (_window.IsVisible)
            _window.Hide();
        else
            _window.ShowAtTrayCorner();
    }

    private void UpdateTrayIcon()
    {
        var anyCasting = _vm?.Devices.Any(d => d.IsCasting) ?? false;
        if (_trayIcon != null)
        {
            _trayIcon.Icon = CreateCastIcon(active: anyCasting);
            _trayIcon.Text = anyCasting ? "Cast Sound — Live" : "Cast Sound";
        }
    }

    public void ExitApp()
    {
        _trayIcon?.Dispose();
        _vm?.DisposeAsync();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    // ── Icon generation ───────────────────────────────────────────────────────
    // Draws a Chromecast-style cast icon (screen + corner arcs) in GDI+.

    private static Icon CreateCastIcon(bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using var g   = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var main  = active
            ? Color.FromArgb(48, 209, 88)   // green when casting
            : Color.FromArgb(200, 200, 200); // light grey at rest

        using var pen   = new Pen(main, 2f);
        using var brush = new SolidBrush(main);

        // Screen outline (bottom rectangle)
        g.DrawRectangle(pen, 6, 18, 20, 10);

        // Three cast arcs in the bottom-left corner of the screen
        g.DrawArc(pen, 3,  16, 6,  6,  180, 90);  // small
        g.DrawArc(pen, 0,  12, 12, 12, 180, 90);  // medium
        g.DrawArc(pen, -3, 8,  18, 18, 180, 90);  // large

        // Solid dot in the corner
        g.FillEllipse(brush, 4, 22, 4, 4);

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}
