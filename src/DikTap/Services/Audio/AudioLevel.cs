namespace DikTap.Services.Audio;

// Input level for the visual indicator: peak of each captured block, on a 0..1 logarithmic scale.
internal static class AudioLevel
{
    // Below -52 dB counts as silence; -6 dB or more is the maximum level.
    private const double FloorDb = -52, CeilingDb = -6;

    public static int Peak(ReadOnlySpan<short> samples)
    {
        int peak = 0;
        foreach (short s in samples)
        {
            int a = s < 0 ? -s : s;
            if (a > peak) peak = a;
        }
        return peak;
    }

    public static float ToUnit(int peak)
    {
        if (peak <= 0) return 0;
        double db = 20 * Math.Log10(peak / 32768.0);
        return (float)Math.Clamp((db - FloorDb) / (CeilingDb - FloorDb), 0, 1);
    }
}
