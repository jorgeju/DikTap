using DikTap;
using DikTap.Services.Audio;
using DikTap.Services.Credentials;
using DikTap.Services.Paste;
using DikTap.Services.Settings;
using DikTap.Services.Transcription;

namespace DikTap.Tests;

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condicion no cumplida: " + what);
            await Task.Delay(5);
        }
    }
}

internal sealed class FakeSession : IRecordingSession
{
    public int Samples = 16000;
    public short Value = 1000;
    public bool Aborted, Disposed, StopCalled, Lost;
    public TaskCompletionSource<bool> StopGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action? LimitReached;
    public event Action? DeviceLost;
    public event Action<DictationException>? Faulted;

    public FakeSession() => StopGate.SetResult(true);

    public async Task<RecordingResult> StopAsync()
    {
        StopCalled = true;
        await StopGate.Task;
        var w = new WavMemoryStream(16000, RecordingLimits.MaxWavBytes, RecordingLimits.MaxDuration);
        w.Append(Enumerable.Repeat(Value, Samples).ToArray());
        w.Seal();
        return new RecordingResult(w, false, w.HasNonZeroSamples, Lost);
    }

    public void Abort() => Aborted = true;
    public int Peak;
    public int TakePeak() => Interlocked.Exchange(ref Peak, 0);
    public void Dispose() => Disposed = true;
    public void RaiseLimit() => LimitReached?.Invoke();
    public void RaiseDeviceLost() { Lost = true; DeviceLost?.Invoke(); }
    public void RaiseFault(DictationException ex) => Faulted?.Invoke(ex);
}

internal sealed class FakeRecorder : IAudioRecorder
{
    public int Starts;
    public readonly List<FakeSession> Sessions = new();
    public Func<CancellationToken, Task>? Gate;
    public Exception? StartFails;
    public Action<FakeSession>? Configure;

    public async Task<IRecordingSession> StartAsync(string? deviceId, CancellationToken ct)
    {
        Interlocked.Increment(ref Starts);
        if (Gate is not null) await Gate(ct);
        ct.ThrowIfCancellationRequested();
        if (StartFails is not null) throw StartFails;
        var s = new FakeSession();
        Configure?.Invoke(s);
        lock (Sessions) Sessions.Add(s);
        return s;
    }
}

internal sealed class FakeTranscriber : ITranscriptionClient
{
    public int Calls;
    public int WarmUps;
    public string Text = "hola mundo";
    public TranscriptionUsage? Usage;
    public Exception? Fails;
    public Func<CancellationToken, Task>? Gate;
    public bool HonorCancellation = true;

    public Task WarmUpAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref WarmUps);
        return Task.CompletedTask;
    }

    public async Task<TranscriptionResult> TranscribeAsync(Stream wav, string apiKey, TranscriptionOptions options, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (Gate is not null) await Gate(HonorCancellation ? ct : CancellationToken.None);
        if (Fails is not null) throw Fails;
        return new TranscriptionResult(Text, Usage);
    }
}

internal sealed class FakeCredentials : ICredentialStore
{
    public string? Key = "sk-test-0000000000";
    public bool HasKey() => Key is not null;
    public string? Read() => Key;
    public void Save(string apiKey) => Key = apiKey;
    public void Delete() => Key = null;
}

internal sealed class FakeOverlay : IOverlay
{
    public readonly List<string> Events = new();
    public Func<float>? LevelSource;
    public TimeSpan? LastInfoDuration, LastErrorDuration;
    private void Add(string e) { lock (Events) Events.Add(e); }
    public string[] Snapshot() { lock (Events) return Events.ToArray(); }
    public void ShowPreparing(DestinationSnapshot? d) => Add("preparing");
    public void ShowRecording(DestinationSnapshot? d, Func<float>? level = null) { LevelSource = level; Add("recording"); }
    public void ShowTranscribing(string? note) => Add("transcribing");
    public void ShowCancelled() => Add("cancelled");
    public void ShowError(string m, TimeSpan? duration = null) { LastErrorDuration = duration; Add("error:" + m); }
    public void ShowInfo(string m, TimeSpan? duration = null) { LastInfoDuration = duration; Add("info:" + m); }
    public void Hide() => Add("hide");
}

internal sealed class FakeEscape : IEscapeKey
{
    public int Arms, Disarms;
    public volatile bool Armed;
    public void Arm() { Interlocked.Increment(ref Arms); Armed = true; }
    public void Disarm() { Interlocked.Increment(ref Disarms); Armed = false; }
}

internal sealed class FakeDestination : IDestinationService
{
    public DestinationStatus Status = DestinationStatus.Valid;
    public DestinationSnapshot? Snapshot = new(new IntPtr(100), 1234, 1, new IntPtr(200));
    public DestinationSnapshot? Capture() => Snapshot;
    public DestinationStatus Validate(DestinationSnapshot snapshot) => Status;
}

internal sealed class FakeClipboard : IClipboard
{
    public string? Text = "contenido previo";
    public uint Seq = 10;
    public ClipboardSnapshot? SnapshotToReturn = new(new[] { new ClipboardFormatData(13, new byte[] { 1, 2, 3 }) }, 0);
    public bool Busy;
    public int Restores;
    public int FailRestores;
    public ClipboardSnapshot? Restored;
    public bool LastExclude;
    public readonly List<string> Writes = new();

    public uint SequenceNumber => Volatile.Read(ref Seq);

    public Task<ClipboardSnapshot?> TakeSnapshotAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(SnapshotToReturn);

    public Task<uint?> SetTextAsync(string text, bool excludeFromHistory, CancellationToken ct)
    {
        if (Busy) return Task.FromResult<uint?>(null);
        Text = text;
        LastExclude = excludeFromHistory;
        Writes.Add(text);
        return Task.FromResult<uint?>(Interlocked.Increment(ref Seq));
    }

    public Task<bool> RestoreAsync(ClipboardSnapshot snapshot, CancellationToken ct)
    {
        Restores++;
        if (FailRestores > 0) { FailRestores--; return Task.FromResult(false); }
        Restored = snapshot;
        Text = "RESTAURADO";
        Interlocked.Increment(ref Seq);
        return Task.FromResult(true);
    }

    public void ExternalCopy(string text)
    {
        Text = text;
        Interlocked.Increment(ref Seq);
    }
}

internal sealed class FakeKeyboard : IKeyboardInjector
{
    public bool Released = true;
    public bool PasteOk = true;
    public int Pastes;
    public bool ModifiersReleased() => Released;
    public bool SendPaste() { Pastes++; return PasteOk; }
}

internal sealed class FakePaste : IPasteService
{
    public readonly List<PasteRequest> Requests = new();
    public PasteResult Result = new(PasteOutcome.Pasted, string.Empty);
    public Func<CancellationToken, Task>? Gate;

    public async Task<PasteResult> PasteAsync(PasteRequest request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        if (Gate is not null) await Gate(ct);
        return Result;
    }

    public Task<bool> CopyTextAsync(string text) => Task.FromResult(true);
    public Task FlushPendingRestoreAsync() => Task.CompletedTask;
}
