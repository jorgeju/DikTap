using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DikTap.Localization;

namespace DikTap.Services.Audio;

public enum SampleEncoding { Pcm, Float }

// Resolved capture format (WAVEFORMATEX / WAVEFORMATEXTENSIBLE).
internal readonly record struct AudioFormat(SampleEncoding Encoding, int Channels, int SampleRate, int BitsPerSample, int BlockAlign)
{
    public override string ToString() => $"{Encoding} {BitsPerSample}-bit {Channels}ch {SampleRate} Hz";

    private const ushort WAVE_FORMAT_PCM = 1, WAVE_FORMAT_IEEE_FLOAT = 3, WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    // Reads a native WAVEFORMATEX (2-byte packed) and resolves the WAVEFORMATEXTENSIBLE subformat.
    public static AudioFormat FromNative(IntPtr p)
    {
        ushort tag = (ushort)Marshal.ReadInt16(p, 0);
        int channels = (ushort)Marshal.ReadInt16(p, 2);
        int rate = Marshal.ReadInt32(p, 4);
        int blockAlign = (ushort)Marshal.ReadInt16(p, 12);
        int bits = (ushort)Marshal.ReadInt16(p, 14);
        int cb = (ushort)Marshal.ReadInt16(p, 16);
        if (tag == WAVE_FORMAT_EXTENSIBLE && cb >= 22)
        {
            // SubFormat starts at offset 24; its first 2 bytes are the equivalent format tag.
            tag = (ushort)Marshal.ReadInt16(p, 24);
        }
        var enc = tag switch
        {
            WAVE_FORMAT_PCM => SampleEncoding.Pcm,
            WAVE_FORMAT_IEEE_FLOAT => SampleEncoding.Float,
            _ => throw new DictationException(Str.UnsupportedMicFormat(tag), ErrorKind.Audio),
        };
        return new AudioFormat(enc, channels, rate, bits, blockAlign);
    }
}

// Converts frames from the native format to mono PCM16. Reduces channels by averaging.
internal sealed class PcmConverter
{
    private readonly AudioFormat _src;
    private readonly int _bytesPerSample;

    public PcmConverter(AudioFormat src)
    {
        if (src.Channels < 1 || src.SampleRate < 8000)
            throw new DictationException(Str.InvalidAudioFormat, ErrorKind.Audio);

        bool ok = src.Encoding == SampleEncoding.Float
            ? src.BitsPerSample == 32
            : src.BitsPerSample is 16 or 24 or 32;
        if (!ok)
            throw new DictationException(Str.UnsupportedFormat(src), ErrorKind.Audio);

        _src = src;
        _bytesPerSample = src.BitsPerSample / 8;
        if (src.BlockAlign != _bytesPerSample * src.Channels)
            throw new DictationException(Str.UnsupportedFormatAlignment(src, src.BlockAlign), ErrorKind.Audio);
    }

    public int SampleRate => _src.SampleRate;
    public int FrameBytes => _src.BlockAlign;

    // Returns the number of PCM16 samples written (one per frame).
    public int Convert(ReadOnlySpan<byte> src, int frames, Span<short> dst)
    {
        if (src.Length < frames * _src.BlockAlign) throw new ArgumentException("Buffer de origen insuficiente.", nameof(src));
        if (dst.Length < frames) throw new ArgumentException("Buffer de destino insuficiente.", nameof(dst));

        int ch = _src.Channels;
        int bps = _bytesPerSample;
        for (int f = 0; f < frames; f++)
        {
            var frame = src.Slice(f * _src.BlockAlign, _src.BlockAlign);
            if (_src.Encoding == SampleEncoding.Float)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++)
                    sum += BitConverter.ToSingle(frame.Slice(c * 4, 4));
                dst[f] = FloatToPcm16(sum / ch);
            }
            else
            {
                long sum = 0;
                for (int c = 0; c < ch; c++)
                    sum += ReadPcmAs16(frame.Slice(c * bps, bps));
                dst[f] = (short)(sum / ch);
            }
        }
        return frames;
    }

    internal static short FloatToPcm16(float v)
    {
        if (float.IsNaN(v)) return 0;
        float s = MathF.Round(v * 32767f);
        if (s > 32767f) return short.MaxValue;
        if (s < -32768f) return short.MinValue;
        return (short)s;
    }

    private static int ReadPcmAs16(ReadOnlySpan<byte> s) => s.Length switch
    {
        2 => BinaryPrimitives.ReadInt16LittleEndian(s),
        3 => ((sbyte)s[2] << 8) | s[1],
        _ => BinaryPrimitives.ReadInt32LittleEndian(s) >> 16,
    };
}
