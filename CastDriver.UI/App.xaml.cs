using System.Collections.Specialized;
using System.ComponentModel;
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

        PreloadLame();

        _vm     = new MainViewModel();
        _window = new MainWindow(_vm);

        BuildTrayIcon();

        // Show the window on launch unless the user opted to start minimized in the tray.
        if (!AppSettings.Current.StartMinimized)
            _window.ShowAtTrayCorner();
    }

    // NAudio.Lame loads libmp3lame.*.dll by plain name, but it only ships them as content
    // (not native runtime libs) so they don't make it into the single-file bundle. We embed
    // them in this assembly, extract the right one, and load it — then NAudio.Lame's
    // DllImport resolves to the already-loaded module.
    private static void PreloadLame()
    {
        try
        {
            var name = Environment.Is64BitProcess ? "libmp3lame.64.dll" : "libmp3lame.32.dll";
            var asm  = typeof(App).Assembly;
            var res  = Array.Find(asm.GetManifestResourceNames(),
                                   n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
            if (res == null) return;

            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CastDriver", "lame", AppInfo.Short);
            System.IO.Directory.CreateDirectory(dir);
            var dll = System.IO.Path.Combine(dir, name);

            if (!System.IO.File.Exists(dll))
            {
                using var s  = asm.GetManifestResourceStream(res)!;
                using var fs = System.IO.File.Create(dll);
                s.CopyTo(fs);
            }
            System.Runtime.InteropServices.NativeLibrary.Load(dll);
        }
        catch { /* MP3 will surface its own error if the DLL is genuinely unavailable */ }
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
        menu.Items.Add(new ToolStripSeparator());

        var castTo = new ToolStripMenuItem("Cast to");
        castTo.DropDownOpening += (_, _) => PopulateCastMenu(castTo);
        castTo.DropDownItems.Add(new ToolStripMenuItem("(loading…)") { Enabled = false });
        menu.Items.Add(castTo);
        menu.Items.Add("Stop all casting", null, (_, _) => _vm?.StopAllCommand.Execute(null));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;

        // Update the tray icon colour both when the device list changes AND when any
        // device's casting state flips (so casting from the tray recolours it instantly).
        if (_vm != null)
        {
            _vm.Devices.CollectionChanged += OnDevicesChanged;
            foreach (var d in _vm.Devices) d.PropertyChanged += OnDevicePropertyChanged;
        }
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (DeviceViewModel d in e.OldItems) d.PropertyChanged -= OnDevicePropertyChanged;
        if (e.NewItems != null)
            foreach (DeviceViewModel d in e.NewItems) d.PropertyChanged += OnDevicePropertyChanged;
        UpdateTrayIcon();
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceViewModel.IsCasting)) UpdateTrayIcon();
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

    // Rebuilds the "Cast to" submenu from the current device list each time it opens.
    private void PopulateCastMenu(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        var devices = _vm?.Devices;
        if (devices == null || devices.Count == 0)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("No devices found") { Enabled = false });
            return;
        }

        foreach (var d in devices)
        {
            var device = d;
            var item = new ToolStripMenuItem(d.Name) { Checked = d.IsCasting, CheckOnClick = false };
            item.Click += (_, _) => _vm?.ToggleDeviceCommand.Execute(device);
            parent.DropDownItems.Add(item);
        }
    }

    // Vanish immediately (hide window + tray icon) so exit feels instant, THEN gracefully
    // tear down in the background — sending each device a clean STOP/disconnect — and quit.
    public async void ExitApp()
    {
        _window?.Hide();
        _trayIcon?.Dispose();
        try { if (_vm != null) await _vm.DisposeAsync(); } catch { /* shutting down anyway */ }
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
