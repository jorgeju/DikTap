using DikTap.Services.Audio;

namespace DikTap.Tests;

public class AudioLevelTests
{
    [Fact]
    public void Peak_is_the_largest_absolute_sample_including_the_most_negative_value()
    {
        Assert.Equal(0, AudioLevel.Peak(ReadOnlySpan<short>.Empty));
        Assert.Equal(700, AudioLevel.Peak(new short[] { 10, -700, 300 }));
        Assert.Equal(32768, AudioLevel.Peak(new short[] { short.MinValue, 5 }));
    }

    [Fact]
    public void Level_maps_silence_to_zero_and_loud_speech_to_one()
    {
        Assert.Equal(0f, AudioLevel.ToUnit(0));
        Assert.Equal(0f, AudioLevel.ToUnit(50));      // below -52 dB
        Assert.Equal(1f, AudioLevel.ToUnit(17000));   // above -6 dB
        Assert.Equal(1f, AudioLevel.ToUnit(32768));
    }

    [Fact]
    public void Level_grows_monotonically_with_the_peak()
    {
        float previous = -1;
        foreach (int p in new[] { 100, 300, 1000, 3000, 8000, 16000 })
        {
            float v = AudioLevel.ToUnit(p);
            Assert.True(v > previous, $"{p}: {v} <= {previous}");
            previous = v;
        }
    }
}
