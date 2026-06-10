using CastDriver.Cast;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CastDriver.UI.ViewModels;

public partial class DeviceViewModel : ObservableObject
{
    private readonly CastManager _manager;
    private bool _suppressVolumeCallback;

    public ChromecastDevice Device { get; }

    public string Name => Device.Name;
    public string Host => Device.Host;

    [ObservableProperty] private bool   _isCasting;
    [ObservableProperty] private bool   _isConnecting;
    [ObservableProperty] private bool   _hasError;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private double _volume = 100;   // this device's own volume (0–100)

    public string VolumeLabel => $"{(int)Volume}%";

    public DeviceViewModel(ChromecastDevice device, CastManager manager)
    {
        Device   = device;
        _manager = manager;
    }

    // User dragged this device's slider → push the new level to the Chromecast.
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
