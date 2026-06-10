using NAudio.Wave;

namespace CastDriver.Audio;

// A source of live PCM audio: WASAPI device loopback, an input device, or a single
// application's audio (process loopback). All raise DataAvailable like NAudio capturers.
public interface ICaptureSource : IDisposable
{
    WaveFormat WaveFormat { get; }
    event EventHandler<WaveInEventArgs> DataAvailable;
    void Start();
}
