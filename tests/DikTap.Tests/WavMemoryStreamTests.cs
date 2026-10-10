using System.Buffers.Binary;
using DikTap.Services.Audio;

namespace DikTap.Tests;

public class WavMemoryStreamTests
{
    private static WavMemoryStream NewStream(int rate = 16000, long max = 22_000_000, int seconds = 600)
        => new(rate, max, TimeSpan.FromSeconds(seconds));

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Header_has_valid_riff_sizes_format_and_data()
    {
        using var w = NewStream();
        var samples = Enumerable.Range(0, 1000).Select(i => (short)(i - 500)).ToArray();
        w.Append(samples);
        w.Seal();

        var bytes = ReadAll(w);
        Assert.Equal(44 + 2000, bytes.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(bytes, 8, 8));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)));   // PCM
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)));   // mono
        Assert.Equal(16000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(32000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(32)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));
        Assert.Equal(2000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        for (int i = 0; i < samples.Length; i++)
            Assert.Equal(samples[i], BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + i * 2)));
    }

    [Fact]
    public void Data_spanning_multiple_chunks_is_preserved_and_chunks_are_allocated_lazily()
    {
        using var w = NewStream();
        Assert.Equal(0, w.AllocatedBytes);
        var samples = new short[100_000];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(i % 30000 + 1);
        // Writes of irregular sizes to cross block boundaries at different points.
        int pos = 0, step = 777;
        while (pos < samples.Length)
        {
            int n = Math.Min(step, samples.Length - pos);
            w.Append(samples.AsSpan(pos, n));
            pos += n;
            step = step * 3 % 5001 + 100;
        }
        w.Seal();
        Assert.True(w.AllocatedBytes < 400_000, "No debe reservar el maximo de antemano");

        var bytes = ReadAll(w);
        Assert.Equal(44 + samples.Length * 2, bytes.Length);
        for (int i = 0; i < samples.Length; i += 997)
            Assert.Equal(samples[i], BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + i * 2)));
        Assert.Equal(samples[^1], BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(bytes.Length - 2)));
    }

    [Fact]
    public void Reading_with_tiny_buffers_matches_full_read_and_supports_seek()
    {
        using var w = NewStream();
        w.Append(Enumerable.Range(1, 70_000).Select(i => (short)(i % 1000)).ToArray());
        w.Seal();
        var full = ReadAll(w);

        w.Position = 0;
        var tiny = new MemoryStream();
        var buf = new byte[7];
        int n;
        while ((n = w.Read(buf, 0, buf.Length)) > 0) tiny.Write(buf, 0, n);
        Assert.Equal(full, tiny.ToArray());

        w.Seek(65_536 + 44 - 3, SeekOrigin.Begin);
        var across = new byte[6];
        Assert.Equal(6, w.Read(across, 0, 6));
        Assert.Equal(full.AsSpan(65_536 + 44 - 3, 6).ToArray(), across);
    }

    [Fact]
    public void Limit_by_bytes_truncates_at_sample_boundary_and_flags_limit()
    {
        // 44 header bytes + at most 1000 data bytes.
        using var w = NewStream(max: 44 + 1000);
        Assert.True(w.Append(new short[400]));
        Assert.False(w.Append(Enumerable.Repeat((short)5, 400).ToArray()));
        Assert.True(w.LimitReached);
        Assert.Equal(1000, w.DataBytes);
        Assert.False(w.Append(new short[10]));
        w.Seal();
        Assert.Equal(1044, w.Length);
    }

    [Fact]
    public void Limit_by_duration_uses_real_sample_rate()
    {
        using var w = NewStream(rate: 48000, seconds: 2);
        Assert.False(w.Append(new short[48000 * 3]));
        Assert.Equal(48000 * 2 * 2, w.DataBytes);
        Assert.Equal(TimeSpan.FromSeconds(2), w.Duration);
    }

    [Fact]
    public void Default_limits_cap_wav_below_22_000_000_bytes()
    {
        using var w = new WavMemoryStream(48000, RecordingLimits.MaxWavBytes, RecordingLimits.MaxDuration);
        var chunk = new short[48000];
        while (w.Append(chunk)) { }
        w.Seal();
        Assert.True(w.Length <= 22_000_000);
        Assert.True(w.Length > 21_900_000);
    }

    [Fact]
    public void Silence_is_detected()
    {
        using var w = NewStream();
        w.Append(new short[1600]);
        Assert.False(w.HasNonZeroSamples);
        w.Append(new short[] { 0, 0, 1 });
        Assert.True(w.HasNonZeroSamples);
    }

    [Fact]
    public void Append_after_seal_and_read_before_seal_fail()
    {
        using var w = NewStream();
        Assert.Throws<NotSupportedException>(() => w.Read(new byte[4], 0, 4));
        w.Seal();
        Assert.Throws<InvalidOperationException>(() => w.Append(new short[1]));
    }

    [Fact]
    public void Dispose_releases_chunks()
    {
        var w = NewStream();
        w.Append(new short[40000]);
        w.Seal();
        w.Dispose();
        Assert.Equal(0, w.AllocatedBytes);
        Assert.Throws<ObjectDisposedException>(() => w.Read(new byte[4], 0, 4));
    }
}
