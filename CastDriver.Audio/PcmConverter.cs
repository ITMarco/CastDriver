using NAudio.Wave;

namespace CastDriver.Audio;

// Converts whatever format WASAPI loopback gives us into 16-bit signed PCM.
// Also applies an optional software gain (used for "cast-only" mode where Windows
// volume is dropped to ~1% and the cast stream is boosted to compensate).
public static class PcmConverter
{
    // GUIDs for WaveFormatExtensible sub-formats
    private static readonly Guid SubTypeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubTypePcm   = new("00000001-0000-0010-8000-00aa00389b71");

    public static WaveFormat ToPcm16Format(WaveFormat source) =>
        new WaveFormat(source.SampleRate, 16, source.Channels);

    // gain: 1.0 = unity, >1.0 = boost (e.g. 100 when Windows volume is at 1%).
    public static byte[] Convert(byte[] input, WaveFormat format, float gain = 1.0f)
    {
        var (encoding, bits) = Unwrap(format);

        if (encoding == WaveFormatEncoding.IeeeFloat && bits == 32)
            return Float32ToPcm16(input, gain);

        if (encoding == WaveFormatEncoding.Pcm && bits == 16)
            return gain == 1.0f ? Copy(input) : ScalePcm16(input, gain);

        if (encoding == WaveFormatEncoding.Pcm && bits == 32)
            return Int32ToPcm16(input, gain);

        // Unknown format — best-effort: treat 32-bit as float, 16-bit as PCM.
        if (bits == 32) return Float32ToPcm16(input, gain);
        if (bits == 16) return gain == 1.0f ? Copy(input) : ScalePcm16(input, gain);

        throw new NotSupportedException(
            $"Unsupported WASAPI format: encoding={encoding} bits={bits}");
    }

    // Decode any supported input format to interleaved float samples (-1..1) — used by the
    // equalizer, which works in the float domain.
    public static float[] ToFloat(byte[] input, WaveFormat format)
    {
        var (encoding, bits) = Unwrap(format);

        if (encoding == WaveFormatEncoding.IeeeFloat && bits == 32 || bits == 32 && encoding != WaveFormatEncoding.Pcm)
        {
            var n = input.Length / 4;
            var o = new float[n];
            for (var i = 0; i < n; i++) o[i] = BitConverter.ToSingle(input, i * 4);
            return o;
        }
        if (encoding == WaveFormatEncoding.Pcm && bits == 32)
        {
            var n = input.Length / 4;
            var o = new float[n];
            for (var i = 0; i < n; i++) o[i] = BitConverter.ToInt32(input, i * 4) / 2147483648f;
            return o;
        }
        // default: 16-bit PCM
        {
            var n = input.Length / 2;
            var o = new float[n];
            for (var i = 0; i < n; i++) o[i] = BitConverter.ToInt16(input, i * 2) / 32768f;
            return o;
        }
    }

    // Encode interleaved float samples to 16-bit PCM (with gain), clamping to avoid overflow.
    public static byte[] FloatToPcm16(float[] samples, float gain = 1.0f)
    {
        var output = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var s16 = (short)Math.Clamp((int)(samples[i] * gain * 32767f), short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(output.AsSpan(i * 2), s16);
        }
        return output;
    }

    // Unwraps WaveFormatExtensible (which WASAPI almost always returns) to the
    // real encoding and bit depth.
    private static (WaveFormatEncoding encoding, int bits) Unwrap(WaveFormat format)
    {
        if (format is WaveFormatExtensible ext)
        {
            var enc = ext.SubFormat == SubTypeFloat ? WaveFormatEncoding.IeeeFloat
                    : ext.SubFormat == SubTypePcm   ? WaveFormatEncoding.Pcm
                    : format.Encoding;
            return (enc, ext.BitsPerSample);
        }
        return (format.Encoding, format.BitsPerSample);
    }

    private static byte[] Float32ToPcm16(byte[] input, float gain)
    {
        var samples = input.Length / 4;
        var output  = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var f   = BitConverter.ToSingle(input, i * 4) * gain;
            var s16 = (short)Math.Clamp((int)(f * 32767f), short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(output.AsSpan(i * 2), s16);
        }
        return output;
    }

    private static byte[] Int32ToPcm16(byte[] input, float gain)
    {
        var samples = input.Length / 4;
        var output  = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var s32 = BitConverter.ToInt32(input, i * 4);
            var s16 = (short)Math.Clamp((int)(s32 / 65536.0f * gain), short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(output.AsSpan(i * 2), s16);
        }
        return output;
    }

    private static byte[] ScalePcm16(byte[] input, float gain)
    {
        var samples = input.Length / 2;
        var output  = new byte[input.Length];
        for (var i = 0; i < samples; i++)
        {
            var s = BitConverter.ToInt16(input, i * 2);
            var scaled = (short)Math.Clamp((int)(s * gain), short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(output.AsSpan(i * 2), scaled);
        }
        return output;
    }

    private static byte[] Copy(byte[] input)
    {
        var copy = new byte[input.Length];
        input.CopyTo(copy, 0);
        return copy;
    }
}
