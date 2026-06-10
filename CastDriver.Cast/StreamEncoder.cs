using NAudio.Lame;
using NAudio.Wave;

namespace CastDriver.Cast;

public enum StreamCodec { Wav, Mp3 }

// Wraps LAME to encode a continuous PCM16 stream into MP3 frames. After each Write we
// drain whatever encoded bytes LAME produced so they can be fanned to clients in real
// time. A single shared encoder feeds all clients; late joiners sync on MP3 frame headers.
internal sealed class Mp3StreamEncoder : IDisposable
{
    private readonly MemoryStream      _buffer = new();
    private readonly LameMP3FileWriter _lame;
    private readonly object            _gate = new();

    public Mp3StreamEncoder(WaveFormat pcm16, int bitrateKbps)
    {
        _lame = new LameMP3FileWriter(_buffer, pcm16, bitrateKbps);
    }

    public byte[] Encode(byte[] pcm)
    {
        lock (_gate)
        {
            _lame.Write(pcm, 0, pcm.Length);
            if (_buffer.Length == 0) return [];
            var bytes = _buffer.ToArray();
            _buffer.SetLength(0);
            return bytes;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _lame.Flush(); } catch { /* finalising */ }
            _lame.Dispose();
            _buffer.Dispose();
        }
    }
}
