using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using WpfApp = System.Windows.Application;
using CastDriver.Audio;
using CastDriver.Cast;
using CastDriver.Driver;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;

namespace CastDriver.UI.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CastManager   _manager = new();
    private readonly AppSettings   _settings = AppSettings.Current;
    private MMDevice?              _captureDevice;
    private readonly DispatcherTimer _levelTimer;
    private bool                  _initializing = true;

    // Tracks the Windows system volume so our slider stays in sync when the user changes
    // volume from the tray flyout or media keys. Kept so we can detach on device switch.
    private AudioEndpointVolumeNotificationDelegate? _volNotify;
    private MMDevice?              _volNotifyDevice;

    [ObservableProperty] private ObservableCollection<DeviceViewModel> _devices = [];
    [ObservableProperty] private ObservableCollection<AudioEndpointInfo> _sources = [];
    [ObservableProperty] private AudioEndpointInfo? _selectedSource;
    [ObservableProperty] private string _selectedCodec = WavOption;
    [ObservableProperty] private double  _volume          = 75;
    [ObservableProperty] private bool    _isMuted;
    [ObservableProperty] private float   _audioLevel;
    [ObservableProperty] private bool    _isDriverInstalled;
    [ObservableProperty] private bool    _isScanning      = true;
    [ObservableProperty] private string  _discoveryStatus = "Scanning for cast devices…";
    [ObservableProperty] private bool    _isDefaultDevice;
    [ObservableProperty] private string  _captureDeviceName = "Loading…";
    [ObservableProperty] private bool    _isCastOnlyMode;
    [ObservableProperty] private bool    _updateAvailable;
    [ObservableProperty] private string  _updateText = "";
    [ObservableProperty] private string  _updateFeature = "";
    [ObservableProperty] private bool    _firewallWarning;
    [ObservableProperty] private double  _latencyMs = 1500;
    [ObservableProperty] private bool    _excludeApp;
    [ObservableProperty] private int     _selectedBitrate = 256;
    private string _updateUrl = "";

    private readonly CollectionViewSource _sourcesView = new();
    public ICollectionView SourcesView => _sourcesView.View;

    public IReadOnlyList<int> Bitrates { get; } = [128, 192, 256, 320];
    public EqViewModel Eq { get; }

    public string VolumeLabel  => $"{(int)Volume}%";
    public string AppVersion   => AppInfo.Display;
    public string LatencyLabel => $"{(int)LatencyMs} ms";

    // An app source is selected → show the "exclude this app" option.
    public bool IsAppSelected => SelectedSource?.Kind == SourceKind.App;
    // MP3 codec selected → show the bitrate selector.
    public bool IsMp3 => SelectedCodec == Mp3Option;

    public MainViewModel()
    {
        _levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30 fps
        _levelTimer.Tick += OnLevelTick;

        Log.Enabled                   = _settings.LoggingEnabled;
        _manager.SourceDeviceId       = _settings.SourceDeviceId;
        _manager.Codec                = _settings.UseMp3 ? StreamCodec.Mp3 : StreamCodec.Wav;
        _manager.Mp3Bitrate           = _settings.Mp3Bitrate;
        _manager.PrebufferMs          = _settings.PrebufferMs;
        _manager.ExcludeApp           = _settings.ExcludeApp;
        _latencyMs                    = _settings.PrebufferMs;
        _excludeApp                   = _settings.ExcludeApp;
        _selectedBitrate              = _settings.Mp3Bitrate;

        // Group the source dropdown: Devices, then Applications.
        _sourcesView.Source = Sources;
        _sourcesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AudioEndpointInfo.Category)));
        _manager.DeviceDiscovered    += OnDeviceDiscovered;
        _manager.DeviceLost          += OnDeviceLost;
        _manager.SessionError        += (_, msg) => UpdateDiscoveryStatus(msg);
        _manager.DeviceVolumeReported += OnDeviceVolumeReported;
        _manager.CastEnded           += OnCastEnded;
        _manager.Reconnecting        += OnReconnecting;
        _manager.Reconnected         += OnReconnected;
        _manager.ReceiverUnreachable += (_, _) =>
            WpfApp.Current.Dispatcher.Invoke(() => FirewallWarning = true);

        // Restore saved EQ state, then build its view-model.
        _manager.Eq.Enabled  = _settings.EqEnabled;
        _manager.Eq.PreampDb = _settings.EqPreamp;
        _manager.Eq.SetGains(_settings.EqGains);
        Eq = new EqViewModel(_manager, _settings);

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        IsDriverInstalled = DriverInstaller.IsInstalled();

        LoadSources();
        SelectedCodec = _settings.UseMp3 ? Mp3Option : WavOption;
        _settings.SourceDeviceId = SelectedSource?.Id;   // baseline for change-detection
        _manager.SourceDeviceId  = SelectedSource?.Id;   // keep manager in sync
        RefreshCaptureDevice(SelectedSource?.Id);

        _manager.StartDiscovery();
        _levelTimer.Start();
        _initializing = false;

        _ = CheckForUpdateAsync();

        await Task.Delay(10_000);

        IsScanning = false;
        UpdateDiscoveryStatus(Devices.Count == 0
            ? "No cast devices found on your network."
            : $"{Devices.Count} device(s) found.");
    }

    // ── Source device selection ────────────────────────────────────────────────

    private void LoadSources()
    {
        PopulateSources();
        SelectedSource = Sources.FirstOrDefault(s => s.Id == _settings.SourceDeviceId)
                      ?? Sources.FirstOrDefault();
    }

    // Re-enumerate sources (e.g. apps that began playing after launch), preserving the
    // current selection. Called when the window is opened. The change-detection guard in
    // OnSelectedSourceChanged keeps re-selecting the same source from restarting capture.
    public void RefreshSources()
    {
        var currentId = SelectedSource?.Id;
        PopulateSources();
        SelectedSource = Sources.FirstOrDefault(s => s.Id == currentId) ?? Sources.FirstOrDefault();
    }

    // Refill the existing collection (don't replace it — the grouped view binds to it).
    private void PopulateSources()
    {
        Sources.Clear();
        foreach (var e in AudioCapture.ListEndpoints()) Sources.Add(e);
    }

    // ── Stream format / codec ──────────────────────────────────────────────────

    public const string WavOption = "WAV — lossless, high bandwidth";
    public const string Mp3Option = "MP3 — compressed, best compatibility";
    public IReadOnlyList<string> CodecOptions { get; } = [WavOption, Mp3Option];

    partial void OnSelectedCodecChanged(string value)
    {
        OnPropertyChanged(nameof(IsMp3));
        if (_initializing) return;
        var useMp3 = value == Mp3Option;
        _settings.UseMp3 = useMp3;
        _settings.Save();
        _ = ApplyCodecAsync(useMp3);
    }

    // Changing codec changes the stream's content type, so active casts are re-established
    // automatically (stop, switch, re-cast at the same volume).
    private async Task ApplyCodecAsync(bool useMp3)
    {
        await RestartCastsAsync(() =>
        {
            _manager.Codec = useMp3 ? StreamCodec.Mp3 : StreamCodec.Wav;
            return Task.CompletedTask;
        });
    }

    // ── Update check ───────────────────────────────────────────────────────────

    private async Task CheckForUpdateAsync()
    {
        if (_settings.DisableUpdateCheck) return;

        var res = await UpdateChecker.CheckAsync(AppInfo.Version);
        if (res is not { Available: true }) return;

        WpfApp.Current.Dispatcher.Invoke(() =>
        {
            _updateUrl      = res.Url;
            UpdateText      = $"New version available {res.LatestTag}";
            UpdateFeature   = string.IsNullOrWhiteSpace(res.Feature) ? "" : $"Feature: {res.Feature}";
            UpdateAvailable = true;
        });
    }

    [RelayCommand]
    private void OpenUpdate()
    {
        if (!string.IsNullOrEmpty(_updateUrl))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl)
            {
                UseShellExecute = true,
            });
    }

    partial void OnSelectedSourceChanged(AudioEndpointInfo? value)
    {
        OnPropertyChanged(nameof(IsAppSelected));
        if (value == null || _initializing) return;
        if (value.Id == _settings.SourceDeviceId) return; // no real change (e.g. a refresh)

        _settings.SourceDeviceId = value.Id;
        _settings.Save();
        _ = RestartCastsAsync(async () =>
        {
            await _manager.SetSourceDeviceAsync(value.Id);
            RefreshCaptureDevice(value.Id);
        });
    }

    // ── App include/exclude + bitrate ──────────────────────────────────────────

    partial void OnExcludeAppChanged(bool value)
    {
        if (_initializing) return;
        _settings.ExcludeApp = value;
        _settings.Save();
        _manager.ExcludeApp = value;
        if (SelectedSource is { Kind: SourceKind.App } app)
            _ = RestartCastsAsync(() => _manager.SetSourceDeviceAsync(app.Id));
    }

    partial void OnSelectedBitrateChanged(int value)
    {
        if (_initializing) return;
        _settings.Mp3Bitrate = value;
        _settings.Save();
        _ = RestartCastsAsync(() => { _manager.Mp3Bitrate = value; return Task.CompletedTask; });
    }

    // Manually re-scan the network for cast devices. Existing devices stay; this prompts
    // late/new ones to announce. Shows the scanning indicator for a couple of seconds.
    [RelayCommand]
    private async Task RefreshDevices()
    {
        if (IsScanning) return;
        IsScanning = true;
        UpdateDiscoveryStatus("Scanning for cast devices…");

        // Snapshot the moment before re-querying: any device that doesn't answer within the
        // scan window is considered gone and pruned, so the list reflects what's live now.
        var cutoff = DateTime.UtcNow;
        _manager.RefreshDiscovery();

        await Task.Delay(4_000);

        _manager.PruneStaleDevices(cutoff);

        IsScanning = false;
        UpdateDiscoveryStatus(Devices.Count == 0
            ? "No cast devices found on your network."
            : $"{Devices.Count} device(s) found.");
    }

    [RelayCommand]
    private void FixFirewall()
    {
        FirewallHelper.AddRuleElevated();
        FirewallWarning = false;
    }

    // ── Latency / buffer ───────────────────────────────────────────────────────

    partial void OnLatencyMsChanged(double value)
    {
        OnPropertyChanged(nameof(LatencyLabel));
        if (_initializing) return;
        _manager.PrebufferMs    = (int)value;
        _settings.PrebufferMs   = (int)value;
        _settings.Save();
    }

    // Raised when a cast stops because the device became unreachable (auto-reconnect gave up),
    // as opposed to the user stopping it. The app shows a tray notification.
    public event Action<string>? CastInterrupted;

    // Raised on each auto-reconnect attempt so the app can notify when minimized.
    public event Action<(string Name, int Attempt, int Max)>? ReconnectAttempt;

    private void OnCastEnded(object? sender, ICastDevice d)
    {
        WpfApp.Current.Dispatcher.Invoke(() =>
        {
            var vm = Devices.FirstOrDefault(x => x.Key == d.Id);
            if (vm == null) return;
            vm.IsCasting       = false;
            vm.IsReconnecting  = false;
            vm.ReconnectText   = "";
            vm.HasError        = true;
            vm.ErrorText       = "Connection lost";
            CastInterrupted?.Invoke(vm.Name);
        });
    }

    private void OnReconnecting(object? sender, (ICastDevice Device, int Attempt, int Max) e)
    {
        WpfApp.Current.Dispatcher.Invoke(() =>
        {
            var vm = Devices.FirstOrDefault(x => x.Key == e.Device.Id);
            if (vm == null) return;
            vm.IsReconnecting = true;
            vm.HasError       = false;
            vm.ReconnectText  = $"Reconnecting… ({e.Attempt} of {e.Max})";
            ReconnectAttempt?.Invoke((vm.Name, e.Attempt, e.Max));
        });
    }

    private void OnReconnected(object? sender, ICastDevice d)
    {
        WpfApp.Current.Dispatcher.Invoke(() =>
        {
            var vm = Devices.FirstOrDefault(x => x.Key == d.Id);
            if (vm == null) return;
            vm.IsReconnecting = false;
            vm.ReconnectText  = "";
            vm.IsCasting      = true;
            vm.HasError       = false;
            vm.ErrorText      = "";
        });
    }

    private void RefreshCaptureDevice(string? id)
    {
        try
        {
            (_captureDevice, _) = AudioCapture.Resolve(id);
            CaptureDeviceName   = SelectedSource?.Name ?? _captureDevice.FriendlyName;
            Volume              = Math.Round(_captureDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100.0);
            IsMuted             = _captureDevice.AudioEndpointVolume.Mute;

            using var enumerator = new MMDeviceEnumerator();
            var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            IsDefaultDevice = def.ID == _captureDevice.ID;

            SubscribeVolumeNotifications();
        }
        catch { CaptureDeviceName = "Unknown device"; }
    }

    // Subscribe to the capture endpoint's volume notifications so external changes (Windows
    // tray volume, media keys, other apps) push into our slider. We update the backing fields
    // directly — not the setters — so we don't echo the change back to the device.
    private void SubscribeVolumeNotifications()
    {
        if (_volNotifyDevice != null && _volNotify != null)
            try { _volNotifyDevice.AudioEndpointVolume.OnVolumeNotification -= _volNotify; } catch { }
        _volNotify = null;
        _volNotifyDevice = null;

        if (_captureDevice == null) return;

        _volNotifyDevice = _captureDevice;
        _volNotify = data => WpfApp.Current.Dispatcher.Invoke(() =>
        {
            var v = Math.Round(data.MasterVolume * 100.0);
            if (Math.Abs(v - _volume) > 0.5)
            {
                _volume = v;
                OnPropertyChanged(nameof(Volume));
                OnPropertyChanged(nameof(VolumeLabel));
            }
            if (data.Muted != _isMuted)
            {
                _isMuted = data.Muted;
                OnPropertyChanged(nameof(IsMuted));
            }
        });
        try { _captureDevice.AudioEndpointVolume.OnVolumeNotification += _volNotify; } catch { }
    }

    // ── Volume ───────────────────────────────────────────────────────────────

    partial void OnVolumeChanged(double value)
    {
        if (_captureDevice == null) return;
        _captureDevice.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(value / 100.0);
        OnPropertyChanged(nameof(VolumeLabel));
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_captureDevice != null)
            _captureDevice.AudioEndpointVolume.Mute = value;
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    partial void OnIsCastOnlyModeChanged(bool value)
    {
        if (value)
            _manager.EnableCastOnlyMode();
        else
            _manager.DisableCastOnlyMode();

        // Cast-only mutes the local endpoint — keep the local mute button in sync
        // without re-triggering OnIsMutedChanged into a feedback loop.
        if (_captureDevice != null)
        {
            _isMuted = _captureDevice.AudioEndpointVolume.Mute;
            OnPropertyChanged(nameof(IsMuted));
        }
    }

    [RelayCommand]
    private void ToggleCastOnlyMode() => IsCastOnlyMode = !IsCastOnlyMode;

    // ── Level meter ──────────────────────────────────────────────────────────

    private void OnLevelTick(object? sender, EventArgs e)
    {
        if (_captureDevice == null || IsMuted) { AudioLevel = 0f; return; }
        try { AudioLevel = _captureDevice.AudioMeterInformation.MasterPeakValue; }
        catch { AudioLevel = 0f; }
    }

    // ── Device management ────────────────────────────────────────────────────

    private void OnDeviceDiscovered(object? sender, ICastDevice d)
    {
        WpfApp.Current.Dispatcher.Invoke(() =>
        {
            if (!Devices.Any(x => x.Key == d.Id))
                Devices.Add(new DeviceViewModel(d, _manager));

            UpdateDiscoveryStatus($"{Devices.Count} device(s) found.");
        });
    }

    private void OnDeviceLost(object? sender, ICastDevice d)
    {
        WpfApp.Current.Dispatcher.Invoke(() =>
        {
            var vm = Devices.FirstOrDefault(x => x.Key == d.Id);
            if (vm == null) return;
            // A device we're actively casting to is obviously still here — keep it even if it
            // missed a discovery reply. A genuinely dead cast is handled by OnCastEnded.
            if (vm.IsCasting) return;
            Devices.Remove(vm);
        });
    }

    private void OnDeviceVolumeReported(object? sender, (ICastDevice Device, float Level) e)
    {
        WpfApp.Current.Dispatcher.Invoke(() =>
            Devices.FirstOrDefault(x => x.Key == e.Device.Id)?.SetVolumeFromDevice(e.Level));
    }

    [RelayCommand]
    private async Task ToggleDevice(DeviceViewModel vm)
    {
        if (vm.IsConnecting) return;

        if (vm.IsCasting)
        {
            await _manager.StopCastingAsync(vm.Device);
            vm.IsCasting       = false;
            vm.IsReconnecting  = false;
            vm.ReconnectText   = "";
            vm.HasError        = false;
            vm.ErrorText       = "";
        }
        else
        {
            await CastDeviceAsync(vm);
        }
    }

    // Sonos only: flip this device between the fast and compatibility streaming paths. If it's
    // currently casting, restart that one cast so the new path takes effect immediately.
    [RelayCommand]
    private async Task ToggleSonosMode(DeviceViewModel vm)
    {
        if (!vm.IsSonos) return;

        if (!vm.SonosCompatibilityMode)
        {
            // About to enable compatibility mode. Nudge the user toward MP3 first (the usual
            // Sonos fix) — but only when it's relevant: not already on MP3, hint not silenced.
            if (!IsMp3 && !_settings.SuppressSonosMp3Hint)
            {
                var dlg = new SonosCompatibilityDialog();
                dlg.ShowDialog();

                if (dlg.Choice == SonosHintChoice.Cancel) return; // backed out — stay on fast path

                if (dlg.DontShowAgain)
                {
                    _settings.SuppressSonosMp3Hint = true;
                    _settings.Save();
                }

                if (dlg.Choice == SonosHintChoice.ChangeToMp3)
                {
                    SelectedCodec = Mp3Option; // switches codec + restarts active casts
                    return;                    // keep fast path; don't enable compatibility
                }
                // else EnableCompatibility — fall through
            }
            vm.SonosCompatibilityMode = true;      // persists + updates the device
        }
        else
        {
            vm.SonosCompatibilityMode = false;     // back to the fast path
        }

        if (vm.IsCasting)
        {
            await _manager.StopCastingAsync(vm.Device); // intentional stop — no auto-reconnect
            await CastDeviceAsync(vm);
        }
    }

    private async Task CastDeviceAsync(DeviceViewModel vm)
    {
        vm.IsConnecting  = true;
        vm.HasError      = false;
        FirewallWarning  = false; // re-evaluated by the reachability check
        try
        {
            await _manager.CastToDeviceAsync(vm.Device);
            vm.IsCasting = true;
        }
        catch (Exception ex)
        {
            vm.HasError  = true;
            vm.ErrorText = ex.Message;
        }
        finally { vm.IsConnecting = false; }
    }

    [RelayCommand]
    private async Task StopAll()
    {
        var casting = Devices.Where(d => d.IsCasting).ToList();
        foreach (var vm in casting)
            await ToggleDevice(vm);
    }

    // Apply a change that invalidates the live stream (source or codec switch): stop the
    // active casts, apply the change, then automatically re-cast to the same devices at
    // their previous volume — so the user never has to manually restart.
    private async Task RestartCastsAsync(Func<Task> applyChange)
    {
        var resume = Devices.Where(d => d.IsCasting)
                            .Select(d => (vm: d, vol: d.Volume)).ToList();
        await StopAll();
        await applyChange();
        foreach (var (vm, vol) in resume)
        {
            await CastDeviceAsync(vm);
            if (vm.IsCasting) vm.Volume = vol; // re-apply the device's volume
        }
        if (resume.Count > 0)
            UpdateDiscoveryStatus($"{resume.Count} device(s) resumed.");
    }

    // ── Driver ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task InstallDriver()
    {
        try
        {
            var ok = await DriverInstaller.InstallAsync();
            IsDriverInstalled = ok;
            if (ok) { LoadSources(); RefreshCaptureDevice(_settings.SourceDeviceId); }
        }
        catch (Exception ex)
        {
            UpdateDiscoveryStatus($"Driver install failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SetAsDefault()
    {
        // pnputil can't do this — we use the Windows shell policy to set default audio.
        // For now, open the sound control panel so the user can set it manually.
        System.Diagnostics.Process.Start("rundll32.exe", "shell32.dll,Control_RunDLL mmsys.cpl,,0");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpdateDiscoveryStatus(string msg) =>
        WpfApp.Current.Dispatcher.Invoke(() => DiscoveryStatus = msg);

    public async ValueTask DisposeAsync()
    {
        _levelTimer.Stop();
        if (_volNotifyDevice != null && _volNotify != null)
            try { _volNotifyDevice.AudioEndpointVolume.OnVolumeNotification -= _volNotify; } catch { }
        await _manager.DisposeAsync();
    }
}
