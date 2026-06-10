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
    public string KindLabel => Device.Kind == CastKind.Dlna ? "DLNA" : "Cast";

    [ObservableProperty] private bool   _isCasting;
    [ObservableProperty] private bool   _isConnecting;
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
