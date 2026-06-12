using NAudio.Dsp;

namespace CastDriver.Audio;

// A 10-band graphic equalizer applied to the cast stream: one chain of peaking biquad
// filters per channel. Cheap, near-zero latency. Thread-safe — the capture thread filters
// while the UI thread tweaks gains.
public sealed class Equalizer
{
    public static readonly int[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    public const double MaxGainDb = 12.0;
    private const float Q = 1.4f;

    private readonly object       _gate   = new();
    private readonly double[]     _gains  = new double[Frequencies.Length];
    private BiQuadFilter[][]      _filters = [];   // [channel][band]
    private int    _sampleRate;
    private int    _channels;
    private double _preampDb;
    private float  _preampLinear = 1f;

    public bool Enabled { get; set; }
    public int  BandCount => Frequencies.Length;

    // Overall make-up gain (dB) applied after the bands — to add loudness or pull back to
    // avoid clipping from boosted bands.
    public double PreampDb
    {
        get => _preampDb;
        set { lock (_gate) { _preampDb = Math.Clamp(value, -MaxGainDb, MaxGainDb); _preampLinear = (float)Math.Pow(10, _preampDb / 20.0); } }
    }

    public double[] GetGains()
    {
        lock (_gate) return (double[])_gains.Clone();
    }

    public void SetGains(double[]? gains)
    {
        lock (_gate)
        {
            for (var i = 0; i < _gains.Length; i++)
                _gains[i] = gains != null && i < gains.Length ? Clamp(gains[i]) : 0;
            Rebuild();
        }
    }

    public void SetGain(int band, double dB)
    {
        lock (_gate)
        {
            if (band < 0 || band >= _gains.Length) return;
            _gains[band] = Clamp(dB);
            if (_filters.Length == 0) return;
            for (var c = 0; c < _channels; c++)
                _filters[c][band] = BiQuadFilter.PeakingEQ(_sampleRate, Frequencies[band], Q, (float)_gains[band]);
        }
    }

    public void Configure(int sampleRate, int channels)
    {
        lock (_gate)
        {
            _sampleRate = sampleRate;
            _channels   = channels;
            Rebuild();
        }
    }

    private void Rebuild()
    {
        if (_sampleRate <= 0 || _channels <= 0) { _filters = []; return; }
        _filters = new BiQuadFilter[_channels][];
        for (var c = 0; c < _channels; c++)
        {
            _filters[c] = new BiQuadFilter[Frequencies.Length];
            for (var b = 0; b < Frequencies.Length; b++)
                _filters[c][b] = BiQuadFilter.PeakingEQ(_sampleRate, Frequencies[b], Q, (float)_gains[b]);
        }
    }

    // Filters interleaved float samples in place. No-op unless enabled and configured.
    public void Process(float[] samples, int channels)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            if (_filters.Length != channels) return; // not configured for this format
            var preamp = _preampLinear;
            for (var i = 0; i + channels <= samples.Length; i += channels)
                for (var c = 0; c < channels; c++)
                {
                    var x     = samples[i + c];
                    var chain = _filters[c];
                    for (var b = 0; b < chain.Length; b++) x = chain[b].Transform(x);
                    samples[i + c] = x * preamp;
                }
        }
    }

    private static double Clamp(double dB) => Math.Clamp(dB, -MaxGainDb, MaxGainDb);
}
