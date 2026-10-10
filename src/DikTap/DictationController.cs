using DikTap.Localization;
using DikTap.Services.Audio;
using DikTap.Services.Credentials;
using DikTap.Services.Paste;
using DikTap.Services.Settings;
using DikTap.Services.Transcription;
using DikTap.Services.Usage;

namespace DikTap;

internal enum SessionState
{
    Idle,
    Starting,
    Recording,
    Stopping,
    Transcribing,
    Pasting,
    Cancelling,
}

internal interface IOverlay
{
    // A session is in progress but capture is not active yet (shows a loading indicator, not "Grabando" / recording).
    void ShowPreparing(DestinationSnapshot? destination);
    // The window polls the level on every frame (0..1) to draw the waveform.
    void ShowRecording(DestinationSnapshot? destination, Func<float>? level = null);
    void ShowTranscribing(string? note);
    void ShowCancelled();
    void ShowError(string message, TimeSpan? duration = null);
    void ShowInfo(string message, TimeSpan? duration = null);
    void Hide();
}

// Escape key only while a session is active: registered on start and released on finish.
internal interface IEscapeKey
{
    void Arm();
    void Disarm();
}

internal enum CancelReason
{
    User,
    SessionInterrupted,
    Shutdown,
}

// Serializes the cycle Idle > Starting > Recording > Stopping > Transcribing > Pasting > Idle.
// All transitions happen under a short lock with no waiting; long operations are async.
internal sealed class DictationController
{
    private sealed class Session
    {
        public required int Id { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource StopSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DestinationSnapshot? Destination;
        public IRecordingSession? Recording;
        public bool Cancelled;
        public CancelReason CancelReason;
        public bool LimitHit;
        public Task Run = Task.CompletedTask;
    }

    private readonly object _gate = new();
    // Final notice after losing the microphone: longer than usual so there is time to read it.
    private static readonly TimeSpan MicLostNoticeDuration = TimeSpan.FromSeconds(5);
    private readonly IAudioRecorder _recorder;
    private readonly ITranscriptionClient _transcriber;
    private readonly IPasteService _paste;
    private readonly IDestinationService _destination;
    private readonly ICredentialStore _credentials;
    private readonly IOverlay _overlay;
    private readonly IEscapeKey _escape;
    private readonly Func<AppSettings> _settings;
    private readonly IUsageStore? _usage;
    private SessionState _state = SessionState.Idle;
    private Session? _session;
    private int _nextId;

    public event Action<SessionState>? StateChanged;
    public event Action<string>? LastDictationChanged;

    public string? LastDictation { get; private set; }

    public DictationController(
        IAudioRecorder recorder,
        ITranscriptionClient transcriber,
        IPasteService paste,
        IDestinationService destination,
        ICredentialStore credentials,
        IOverlay overlay,
        IEscapeKey escape,
        Func<AppSettings> settings,
        IUsageStore? usage = null)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _paste = paste;
        _destination = destination;
        _credentials = credentials;
        _overlay = overlay;
        _escape = escape;
        _settings = settings;
        _usage = usage;
    }

    public SessionState State
    {
        get { lock (_gate) return _state; }
    }

    // Global hotkey: starts or stops depending on the state. Ignores duplicate commands.
    public void Toggle()
    {
        Session? start = null;
        SessionState? changed = null;
        lock (_gate)
        {
            switch (_state)
            {
                case SessionState.Idle:
                    start = _session = new Session { Id = ++_nextId };
                    _state = SessionState.Starting;
                    changed = _state;
                    break;
                case SessionState.Starting:
                    // Second press while the device is opening: it will stop as soon as it starts.
                    _session!.StopSignal.TrySetResult();
                    break;
                case SessionState.Recording:
                    _state = SessionState.Stopping;
                    changed = _state;
                    _session!.StopSignal.TrySetResult();
                    break;
                default:
                    break;
            }
        }
        if (changed is { } c) StateChanged?.Invoke(c);
        if (start is not null) start.Run = RunSessionAsync(start);
    }

    public void Cancel(CancelReason reason)
    {
        Session? s;
        lock (_gate)
        {
            s = _session;
            if (s is null || s.Cancelled || _state == SessionState.Idle) return;
            s.Cancelled = true;
            s.CancelReason = reason;
        }
        Logger.Info($"Session {s.Id} cancelled ({reason})");
        try
        {
            s.Cts.Cancel();
            s.Recording?.Abort();
        }
        catch (ObjectDisposedException)
        {
            // The session just finished.
        }
    }

    public async Task ShutdownAsync(TimeSpan timeout)
    {
        Session? s;
        lock (_gate) s = _session;
        if (s is null) return;
        Cancel(CancelReason.Shutdown);
        try { await s.Run.WaitAsync(timeout).ConfigureAwait(false); }
        catch (Exception) { }
    }

    // Counts every upload OpenAI processed (billed even without speech); a failure here never affects dictation.
    private void RecordUsage(string model, string apiKey, double recordedSeconds, TranscriptionUsage? usage)
    {
        if (_usage is null) return;
        try
        {
            double seconds = usage?.Seconds is > 0 ? usage.Seconds.Value : recordedSeconds;
            _usage.Append(new UsageRecord
            {
                Time = DateTimeOffset.Now,
                Model = model,
                Key = UsagePricing.KeyTag(apiKey),
                AudioSeconds = seconds,
                InputTokens = usage?.InputTokens ?? 0,
                OutputTokens = usage?.OutputTokens ?? 0,
                CostUsd = UsagePricing.Estimate(model, seconds),
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Could not record usage", ex);
        }
    }

    private void SetState(SessionState state)
    {
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
        }
        StateChanged?.Invoke(state);
    }

    private async Task RunSessionAsync(Session s)
    {
        CancellationToken ct = s.Cts.Token;
        RecordingResult? recording = null;
        Task<IRecordingSession>? starting = null;
        try
        {
            var settings = _settings();
            if (!_credentials.HasKey())
                throw new DictationException(Str.MissingKey, ErrorKind.Configuration);

            s.Destination = _destination.Capture();
            // Capture starts right away (on another thread); the rest of the setup happens while the device opens.
            starting = _recorder.StartAsync(settings.MicrophoneId, ct);
            _escape.Arm();
            _overlay.ShowPreparing(s.Destination);
            Logger.Info($"Session {s.Id}: start");

            s.Recording = await starting;
            ct.ThrowIfCancellationRequested();
            var rec = s.Recording;

            // Only now is audio actually flowing in: the indicator does not promise too early.
            if (!s.StopSignal.Task.IsCompleted) _overlay.ShowRecording(s.Destination, () => AudioLevel.ToUnit(rec.TakePeak()));
            // While the user speaks, the OpenAI connection is opened so the upload does not pay for it.
            _ = Task.Run(() => _transcriber.WarmUpAsync(ct), CancellationToken.None);

            rec.LimitReached += () => OnLimitReached(s);
            rec.DeviceLost += () => OnDeviceLost(s);
            rec.Faulted += ex => s.StopSignal.TrySetException(ex);

            lock (_gate)
            {
                if (_state == SessionState.Starting)
                    _state = s.StopSignal.Task.IsCompleted ? SessionState.Stopping : SessionState.Recording;
            }
            StateChanged?.Invoke(State);

            await s.StopSignal.Task.WaitAsync(ct);
            SetState(SessionState.Stopping);
            recording = await rec.StopAsync();
            ct.ThrowIfCancellationRequested();

            if (recording.Wav.Duration < RecordingLimits.MinDuration)
                throw recording.DeviceLost
                    ? new DictationException(Str.MicLostTooShort, ErrorKind.Audio) { DeviceLost = true }
                    : new DictationException(Str.TooShort, ErrorKind.NoSpeech);
            if (!recording.HasSound)
                throw new DictationException(Str.NoSound, ErrorKind.NoSpeech);

            bool deviceLost = recording.DeviceLost;
            SetState(SessionState.Transcribing);
            _overlay.ShowTranscribing(deviceLost ? Str.NoteMicDisconnected : recording.LimitReached ? Str.NoteLimitReached : null);

            string apiKey = _credentials.Read()
                ?? throw new DictationException(Str.MissingKey, ErrorKind.Configuration);
            var options = TranscriptionOptions.FromSettings(settings);
            var transcription = await _transcriber.TranscribeAsync(recording.Wav, apiKey, options, ct);
            RecordUsage(options.Model, apiKey, recording.Wav.Duration.TotalSeconds, transcription.Usage);
            string text = transcription.Text;
            apiKey = string.Empty;

            // The audio is no longer needed: released before touching the clipboard.
            recording.Wav.Dispose();
            recording = null;
            ct.ThrowIfCancellationRequested();

            text = text.Trim();
            if (text.Length == 0)
                throw new DictationException(Str.NoSpeech, ErrorKind.NoSpeech);

            LastDictation = text;
            LastDictationChanged?.Invoke(text);

            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                _state = SessionState.Pasting;
            }
            StateChanged?.Invoke(SessionState.Pasting);

            var result = await _paste.PasteAsync(new PasteRequest(text, s.Destination, settings.ClipboardMode), ct);
            if (result.Outcome == PasteOutcome.Pasted)
            {
                if (result.Message.Length > 0) _overlay.ShowInfo(result.Message, deviceLost ? MicLostNoticeDuration : null);
                else if (deviceLost) _overlay.ShowInfo(Str.PastedAfterMicLost, MicLostNoticeDuration);
                else _overlay.Hide();
            }
            else
            {
                _overlay.ShowInfo(result.Message, deviceLost ? MicLostNoticeDuration : null);
            }
            Logger.Info($"Session {s.Id}: {result.Outcome} ({result.Message})");
        }
        catch (OperationCanceledException) when (s.Cts.IsCancellationRequested)
        {
            if (s.CancelReason == CancelReason.User) _overlay.ShowCancelled();
            else _overlay.Hide();
        }
        catch (DictationException ex)
        {
            Logger.Info($"Session {s.Id}: error {ex.Kind}");
            _overlay.ShowError(ex.Message, ex.DeviceLost ? MicLostNoticeDuration : null);
        }
        catch (Exception ex)
        {
            Logger.Error($"Session {s.Id}: unexpected error", ex);
            _overlay.ShowError(Str.Unexpected);
        }
        finally
        {
            recording?.Wav.Dispose();
            if (s.Recording is { } r) _ = Task.Run(r.Dispose);
            else if (starting is not null)
            {
                // Capture was requested but never collected: released once it finishes opening.
                _ = starting.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
            }
            _escape.Disarm();
            lock (_gate)
            {
                _session = null;
                _state = SessionState.Idle;
            }
            StateChanged?.Invoke(SessionState.Idle);
            s.Cts.Dispose();
        }
    }

    private void OnLimitReached(Session s)
    {
        lock (_gate)
        {
            // A late event from a finished session must not touch the current session's state.
            if (!ReferenceEquals(_session, s)) return;
            if (_state == SessionState.Recording) _state = SessionState.Stopping;
            s.LimitHit = true;
        }
        s.StopSignal.TrySetResult();
    }

    // The microphone disappeared with audio already recorded: treated as a stop and what was captured is transcribed.
    private void OnDeviceLost(Session s)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, s)) return;
            if (_state == SessionState.Recording) _state = SessionState.Stopping;
        }
        Logger.Info($"Session {s.Id}: microphone lost; transcribing what was recorded");
        s.StopSignal.TrySetResult();
    }
}
