using System.Collections.ObjectModel;
using CastDriver.Audio;
using CastDriver.Cast;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CastDriver.UI.ViewModels;

public partial class EqBandViewModel : ObservableObject
{
    private readonly Action<int, double> _onChanged;

    public int    Index { get; }
    public string Label { get; }

    [ObservableProperty] private double _gain;

    public EqBandViewModel(int index, int freq, double gain, Action<int, double> onChanged)
    {
        Index      = index;
        Label      = freq >= 1000 ? $"{freq / 1000}k" : freq.ToString();
        _gain      = gain;
        _onChanged = onChanged;
    }

    public string GainLabel => $"{(Gain >= 0 ? "+" : "")}{Gain:0}";

    partial void OnGainChanged(double value)
    {
        OnPropertyChanged(nameof(GainLabel));
        _onChanged(Index, value);
    }
}

public partial class EqViewModel : ObservableObject
{
    private readonly CastManager _manager;
    private readonly AppSettings _settings;
    private bool _suppress;

    public ObservableCollection<EqBandViewModel> Bands   { get; } = [];
    public ObservableCollection<string>          Presets { get; } = [];

    [ObservableProperty] private bool    _enabled;
    [ObservableProperty] private string? _selectedPreset;
    [ObservableProperty] private double  _preamp;

    public string PreampLabel => $"{(Preamp >= 0 ? "+" : "")}{Preamp:0} dB";

    // Built-in presets (10 bands: 31 62 125 250 500 1k 2k 4k 8k 16k Hz).
    private static readonly Dictionary<string, double[]> BuiltIn = new()
    {
        ["Flat"]         = new double[10],
        ["Bass boost"]   = [6, 5, 4, 2, 0, 0, 0, 0, 0, 0],
        ["Treble boost"] = [0, 0, 0, 0, 0, 0, 2, 4, 5, 6],
        ["Vocal"]        = [-2, -1, 0, 1, 3, 4, 3, 1, 0, -1],
        ["Rock"]         = [4, 3, 1, -1, -1, 0, 2, 3, 4, 4],
        ["Pop"]          = [-1, 0, 2, 3, 3, 2, 0, -1, -1, -1],
        ["Loudness"]     = [6, 4, 2, 0, -1, 0, 1, 3, 5, 6],
    };

    public EqViewModel(CastManager manager, AppSettings settings)
    {
        _manager  = manager;
        _settings = settings;

        var gains = manager.Eq.GetGains();
        var freqs = Equalizer.Frequencies;
        for (var i = 0; i < freqs.Length; i++)
            Bands.Add(new EqBandViewModel(i, freqs[i], i < gains.Length ? gains[i] : 0, OnBandChanged));

        _enabled = manager.Eq.Enabled;
        _preamp  = manager.Eq.PreampDb;
        ReloadPresets();
    }

    partial void OnPreampChanged(double value)
    {
        OnPropertyChanged(nameof(PreampLabel));
        _manager.Eq.PreampDb = value;
        _settings.EqPreamp   = value;
        _settings.Save();
    }

    private void OnBandChanged(int index, double value)
    {
        if (_suppress) return;
        _manager.Eq.SetGain(index, value);
        SaveGains();
        if (SelectedPreset != null) SelectedPreset = null; // hand-edited → no longer a preset
    }

    partial void OnEnabledChanged(bool value)
    {
        _manager.Eq.Enabled = value;
        _settings.EqEnabled = value;
        _settings.Save();
    }

    partial void OnSelectedPresetChanged(string? value)
    {
        if (value == null) return;
        var gains = BuiltIn.TryGetValue(value, out var g)
            ? g
            : _settings.EqPresets.FirstOrDefault(p => p.Name == value)?.Gains;
        if (gains != null) ApplyGains(gains);
    }

    [RelayCommand] private void ToggleBypass() => Enabled = !Enabled;

    [RelayCommand] private void Reset() => SelectedPreset = "Flat";

    public void SaveCurrentAs(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        var gains = _manager.Eq.GetGains();
        _settings.EqPresets.RemoveAll(p => p.Name == name);
        _settings.EqPresets.Add(new EqPresetData { Name = name, Gains = gains });
        _settings.Save();
        ReloadPresets();
        SelectedPreset = name;
    }

    private void ApplyGains(double[] gains)
    {
        _suppress = true;
        for (var i = 0; i < Bands.Count; i++) Bands[i].Gain = i < gains.Length ? gains[i] : 0;
        _suppress = false;
        _manager.Eq.SetGains(gains);
        SaveGains();
    }

    private void SaveGains()
    {
        _settings.EqGains = _manager.Eq.GetGains();
        _settings.Save();
    }

    private void ReloadPresets()
    {
        Presets.Clear();
        foreach (var name in BuiltIn.Keys) Presets.Add(name);
        foreach (var p in _settings.EqPresets) Presets.Add(p.Name);
    }
}
