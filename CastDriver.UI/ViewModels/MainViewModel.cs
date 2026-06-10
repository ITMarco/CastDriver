using System.Collections.ObjectModel;
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
    private string _updateUrl = "";

    public string VolumeLabel => $"{(int)Volume}%";
    public string AppVersion  => AppInfo.Display;

    public MainViewModel()
    {
        _levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30 fps
        _levelTimer.Tick += OnLevelTick;

        Log.Enabled                   = _settings.LoggingEnabled;
        _manager.SourceDeviceId       = _settings.SourceDeviceId;
        _manager.Codec                = _settings.UseMp3 ? StreamCodec.Mp3 : StreamCodec.Wav;
        _manager.Mp3Bitrate           = _settings.Mp3Bitrate;
        _manager.DeviceDiscovered    += OnDeviceDiscovered;
        _manager.DeviceLost          += OnDeviceLost;
        _manager.SessionError        += (_, msg) => UpdateDiscoveryStatus(msg);
        _manager.DeviceVolumeReported += OnDeviceVolumeReported;

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
        Sources = new ObservableCollection<AudioEndpointInfo>(AudioCapture.ListEndpoints());
        SelectedSource = Sources.FirstOrDefault(s => s.Id == _settings.SourceDeviceId)
                      ?? Sources.FirstOrDefault();
    }

    // Re-enumerate sources (e.g. apps that began playing after launch), preserving the
    // current selection. Called when the window is opened. The change-detection guard in
    // OnSelectedSourceChanged keeps re-selecting the same source from restarting capture.
    public void RefreshSources()
    {
        var currentId = SelectedSource?.Id;
        Sources = new ObservableCollection<AudioEndpointInfo>(AudioCapture.ListEndpoints());
        SelectedSource = Sources.FirstOrDefault(s => s.Id == currentId) ?? Sources.FirstOrDefault();
    }

    // ── Stream format / codec ──────────────────────────────────────────────────

    public const string WavOption = "WAV — lossless, high bandwidth";
    public const string Mp3Option = "MP3 — compressed, best compatibility";
    public IReadOnlyList<string> CodecOptions { get; } = [WavOption, Mp3Option];

    partial void OnSelectedCodecChanged(string value)
    {
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
            UpdateText      = $"Update available: {res.LatestTag}";
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
        }
        catch { CaptureDeviceName = "Unknown device"; }
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
            if (vm != null) Devices.Remove(vm);
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
            vm.IsCasting  = false;
            vm.HasError   = false;
            vm.ErrorText  = "";
        }
        else
        {
            await CastDeviceAsync(vm);
        }
    }

    private async Task CastDeviceAsync(DeviceViewModel vm)
    {
        vm.IsConnecting = true;
        vm.HasError     = false;
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

    // ── Startup with Windows ─────────────────────────────────────────────────

    public bool StartsWithWindows
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("CastDriver") != null;
        }
        set
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (value)
                key.SetValue("CastDriver", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("CastDriver", throwOnMissingValue: false);
            OnPropertyChanged();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpdateDiscoveryStatus(string msg) =>
        WpfApp.Current.Dispatcher.Invoke(() => DiscoveryStatus = msg);

    public async ValueTask DisposeAsync()
    {
        _levelTimer.Stop();
        await _manager.DisposeAsync();
    }
}
