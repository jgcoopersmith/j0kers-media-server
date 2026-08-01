using J0kersMediaServer.Config;

namespace J0kersMediaServer.Media;

/// <summary>
/// Produces 20 ms frames (160 samples) of G.711 µ-law audio at 8 kHz —
/// RTP payload type 0 (PCMU), the static mapping from RFC 3551.
/// </summary>
public interface IMediaSource
{
    /// <summary>Fill <paramref name="frame"/> (160 bytes) with the next 20 ms of µ-law audio.</summary>
    void NextFrame(Span<byte> frame);
}

public static class MediaSourceFactory
{
    public const int SampleRate = 8000;
    public const int FrameSamples = 160; // 20 ms
    public const int PayloadTypePcmu = 0;

    public static IMediaSource Create(MountConfig mount, string baseDirectory)
    {
        return mount.Source.ToLowerInvariant() switch
        {
            "tone" => new ToneSource(mount.ToneFrequencyHz),
            "file" => new UlawFileSource(ResolvePath(mount.File, baseDirectory)),
            _ => throw new InvalidOperationException(
                $"Mount '{mount.Path}': unknown source '{mount.Source}' (expected 'tone' or 'file')."),
        };
    }

    private static string ResolvePath(string? file, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException("Mount with source=file requires a 'file' setting.");
        return Path.IsPathRooted(file) ? file : Path.Combine(baseDirectory, file);
    }
}

/// <summary>Continuous sine tone, µ-law encoded.</summary>
public sealed class ToneSource : IMediaSource
{
    private readonly double _frequency;
    private double _phase;

    public ToneSource(double frequencyHz) => _frequency = frequencyHz;

    public void NextFrame(Span<byte> frame)
    {
        var step = 2 * Math.PI * _frequency / MediaSourceFactory.SampleRate;
        for (var i = 0; i < frame.Length; i++)
        {
            var sample = (short)(Math.Sin(_phase) * 12000);
            frame[i] = G711.LinearToUlaw(sample);
            _phase += step;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
        }
    }
}

/// <summary>Loops a raw 8 kHz G.711 µ-law file.</summary>
public sealed class UlawFileSource : IMediaSource
{
    private readonly byte[] _data;
    private int _pos;

    public UlawFileSource(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"µ-law source file not found: {path}");
        _data = File.ReadAllBytes(path);
        if (_data.Length == 0)
            throw new InvalidOperationException($"µ-law source file is empty: {path}");
    }

    public void NextFrame(Span<byte> frame)
    {
        for (var i = 0; i < frame.Length; i++)
        {
            frame[i] = _data[_pos];
            _pos = (_pos + 1) % _data.Length;
        }
    }
}

/// <summary>G.711 µ-law encoder (ITU-T G.711).</summary>
public static class G711
{
    private const int Bias = 0x84;
    private const int Clip = 32635;

    public static short UlawToLinear(byte ulaw)
    {
        ulaw = (byte)~ulaw;
        var sign = ulaw & 0x80;
        var exponent = (ulaw >> 4) & 0x07;
        var mantissa = ulaw & 0x0F;
        var magnitude = (((mantissa << 3) + Bias) << exponent) - Bias;
        return (short)(sign != 0 ? -magnitude : magnitude);
    }

    public static byte LinearToUlaw(short pcm)
    {
        var sign = (pcm >> 8) & 0x80;
        if (sign != 0) pcm = (short)-pcm;
        if (pcm > Clip) pcm = Clip;
        pcm = (short)(pcm + Bias);

        var exponent = 7;
        for (var mask = 0x4000; (pcm & mask) == 0 && exponent > 0; mask >>= 1) exponent--;

        var mantissa = (pcm >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }
}
