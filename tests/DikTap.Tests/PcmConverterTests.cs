using System.Buffers.Binary;
using DikTap;
using DikTap.Services.Audio;

namespace DikTap.Tests;

public class PcmConverterTests
{
    private static byte[] Floats(params float[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), v[i]);
        return b;
    }

    [Fact]
    public void Float_mono_converts_scales_and_clips()
    {
        var c = new PcmConverter(new AudioFormat(SampleEncoding.Float, 1, 48000, 32, 4));
        var src = Floats(0f, 0.5f, -0.5f, 1f, -1f, 2f, -2f, float.NaN);
        var dst = new short[8];
        Assert.Equal(8, c.Convert(src, 8, dst));
        Assert.Equal(new short[] { 0, 16384, -16384, 32767, -32767, 32767, -32768, 0 }, dst);
    }

    [Fact]
    public void Float_stereo_is_downmixed_by_average()
    {
        var c = new PcmConverter(new AudioFormat(SampleEncoding.Float, 2, 48000, 32, 8));
        var src = Floats(1f, 0f, 0.5f, 0.5f, -1f, 1f);
        var dst = new short[3];
        c.Convert(src, 3, dst);
        Assert.Equal(new short[] { 16384, 16384, 0 }, dst);
    }

    [Fact]
    public void Pcm16_stereo_is_downmixed_without_overflow()
    {
        var c = new PcmConverter(new AudioFormat(SampleEncoding.Pcm, 2, 44100, 16, 4));
        var src = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(src.AsSpan(0), 32767);
        BinaryPrimitives.WriteInt16LittleEndian(src.AsSpan(2), 32767);
        BinaryPrimitives.WriteInt16LittleEndian(src.AsSpan(4), -32768);
        BinaryPrimitives.WriteInt16LittleEndian(src.AsSpan(6), -32768);
        var dst = new short[2];
        c.Convert(src, 2, dst);
        Assert.Equal(new short[] { 32767, -32768 }, dst);
    }

    [Fact]
    public void Pcm24_and_Pcm32_keep_the_most_significant_16_bits()
    {
        var c24 = new PcmConverter(new AudioFormat(SampleEncoding.Pcm, 1, 48000, 24, 3));
        // 0x7FFFFF y -0x800000 (little endian)
        var s24 = new byte[] { 0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x80, 0x00, 0x00, 0x40 };
        var d24 = new short[3];
        c24.Convert(s24, 3, d24);
        Assert.Equal(new short[] { 32767, -32768, 16384 }, d24);

        var c32 = new PcmConverter(new AudioFormat(SampleEncoding.Pcm, 1, 48000, 32, 4));
        var s32 = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(s32.AsSpan(0), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(s32.AsSpan(4), int.MinValue);
        var d32 = new short[2];
        c32.Convert(s32, 2, d32);
        Assert.Equal(new short[] { 32767, -32768 }, d32);
    }

    [Fact]
    public void Silence_stays_silence()
    {
        var c = new PcmConverter(new AudioFormat(SampleEncoding.Float, 2, 48000, 32, 8));
        var dst = new short[100];
        Array.Fill(dst, (short)7);
        c.Convert(new byte[800], 100, dst);
        Assert.All(dst, s => Assert.Equal(0, s));
    }

    [Theory]
    [InlineData(SampleEncoding.Pcm, 8, 1)]
    [InlineData(SampleEncoding.Float, 64, 8)]
    [InlineData(SampleEncoding.Pcm, 20, 3)]
    public void Unsupported_formats_are_rejected_with_a_clear_error(SampleEncoding enc, int bits, int blockAlign)
    {
        var ex = Assert.Throws<DictationException>(() => new PcmConverter(new AudioFormat(enc, 1, 48000, bits, blockAlign)));
        Assert.Equal(ErrorKind.Audio, ex.Kind);
    }

    [Fact]
    public void Output_buffer_too_small_is_rejected()
    {
        var c = new PcmConverter(new AudioFormat(SampleEncoding.Float, 1, 48000, 32, 4));
        Assert.Throws<ArgumentException>(() => c.Convert(new byte[40], 10, new short[5]));
    }
}
