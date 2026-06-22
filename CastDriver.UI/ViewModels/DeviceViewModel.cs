using CastDriver.Cast;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CastDriver.UI.ViewModels;

public partial class DeviceViewModel : ObservableObject
{
    private readonly CastManager _manager;
    private bool _suppressVolumeCallback;

    public ICastDevice Device { get; }

    public string Name => Device.Name;
    public string Key  => Device.Id;
    public bool   IsSonos => Device is DlnaDevice { IsSonos: true };
    public string KindLabel => IsSonos ? "Sonos" : Device.Kind == CastKind.Dlna ? "DLNA" : "Cast";

    // Sonos only: false = optimised "fast" streaming, true = generic DLNA fallback.
    [ObservableProperty] private bool _sonosCompatibilityMode;
    public string SonosModeLabel => SonosCompatibilityMode ? "Streaming: Compatibility" : "Streaming: Fast";

    [ObservableProperty] private bool   _isCasting;
    [ObservableProperty] private bool   _isConnecting;
    [ObservableProperty] private bool   _isReconnecting;
    [ObservableProperty] private string _reconnectText = "";
    [ObservableProperty] private bool   _hasError;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private double _volume = 100;   // this device's own volume (0–100)
    [ObservableProperty] private bool   _isDeviceMuted;   // mute via silence stream

    public string VolumeLabel => $"{(int)Volume}%";

    partial void OnIsDeviceMutedChanged(bool value) => _manager.SetDeviceMute(Device, value);

    [RelayCommand]
    private void ToggleDeviceMute() => IsDeviceMuted = !IsDeviceMuted;

    public DeviceViewModel(ICastDevice device, CastManager manager)
    {
        Device   = device;
        _manager = manager;

        // Restore this Sonos player's saved streaming preference (set the backing field
        // directly so we don't trigger a save during construction).
        if (device is DlnaDevice { IsSonos: true } sonos)
        {
            _sonosCompatibilityMode   = AppSettings.Current.SonosCompatDeviceIds.Contains(Key);
            sonos.SonosCompatibilityMode = _sonosCompatibilityMode;
        }
    }

    partial void OnSonosCompatibilityModeChanged(bool value)
    {
        if (Device is DlnaDevice d) d.SonosCompatibilityMode = value;

        var ids = AppSettings.Current.SonosCompatDeviceIds;
        if (value) { if (!ids.Contains(Key)) ids.Add(Key); }
        else       ids.Remove(Key);
        AppSettings.Current.Save();

        OnPropertyChanged(nameof(SonosModeLabel));
    }

    // User dragged this device's slider → push the new level to the device.
    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeLabel));
        if (_suppressVolumeCallback) return;
        _ = _manager.SetDeviceVolumeAsync(Device, (float)(value / 100.0));
    }

    // Reflect a volume the device itself reported, without echoing it back as a command.
    public void SetVolumeFromDevice(float level)
    {
        _suppressVolumeCallback = true;
        Volume = System.Math.Round(level * 100.0);
        _suppressVolumeCallback = false;
    }
}
