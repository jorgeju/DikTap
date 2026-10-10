namespace DikTap.Services.Audio;

// DeviceLost: capture ended because the microphone disappeared; the WAV contains only what was recorded up to that point.
internal sealed record RecordingResult(WavMemoryStream Wav, bool LimitReached, bool HasSound, bool DeviceLost = false);

internal interface IRecordingSession : IDisposable
{
    // Duration/size limit reached: capture stopped by itself and is ready for StopAsync().
    event Action? LimitReached;

    // The microphone disappeared with audio already captured: capture stopped by itself and StopAsync() returns what was recorded up to that point.
    event Action? DeviceLost;

    // Device failure during capture with no usable audio (or unrecoverable error).
    event Action<DictationException>? Faulted;

    // Drains pending buffers, stops capture and returns the finalized WAV.
    Task<RecordingResult> StopAsync();

    // Stops and discards the audio.
    void Abort();

    // Sample peak (0..32767) since the last call; feeds the level indicator and may be called from any thread.
    int TakePeak();
}

internal interface IAudioRecorder
{
    Task<IRecordingSession> StartAsync(string? deviceId, CancellationToken cancellationToken);
}

internal sealed record AudioDeviceInfo(string Id, string Name);

internal interface IAudioDeviceCatalog
{
    Task<IReadOnlyList<AudioDeviceInfo>> ListCaptureDevicesAsync();
}

internal static class RecordingLimits
{
    public const long MaxWavBytes = 22_000_000;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(150);
}
