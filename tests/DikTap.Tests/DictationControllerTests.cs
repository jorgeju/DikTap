using DikTap;
using DikTap.Services.Settings;

namespace DikTap.Tests;

public class DictationControllerTests
{
    private sealed class Harness
    {
        public readonly FakeRecorder Recorder = new();
        public readonly FakeTranscriber Transcriber = new();
        public readonly FakePaste Paste = new();
        public readonly FakeDestination Destination = new();
        public readonly FakeCredentials Credentials = new();
        public readonly FakeOverlay Overlay = new();
        public readonly FakeEscape Escape = new();
        public readonly DictationController Controller;

        public Harness(ClipboardMode mode = ClipboardMode.Restore)
        {
            Controller = new DictationController(Recorder, Transcriber, Paste, Destination, Credentials, Overlay, Escape, () => new AppSettings { ClipboardMode = mode });
        }

        public async Task StartRecordingAsync()
        {
            Controller.Toggle();
            await Wait.UntilAsync(() => Controller.State == SessionState.Recording, what: "Recording");
        }

        public Task IdleAsync() => Wait.UntilAsync(() => Controller.State == SessionState.Idle, what: "Idle");

        public async Task FullDictationAsync()
        {
            await StartRecordingAsync();
            Controller.Toggle();
            await IdleAsync();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Configured_clipboard_mode_reaches_the_paste_service(int modeIndex)
    {
        var mode = (ClipboardMode)modeIndex;
        var h = new Harness(mode);
        await h.FullDictationAsync();
        Assert.Equal(mode, Assert.Single(h.Paste.Requests).Mode);
    }

    [Fact]
    public async Task Full_flow_transcribes_pastes_and_releases_everything()
    {
        var h = new Harness();
        await h.FullDictationAsync();

        Assert.Equal(1, h.Transcriber.Calls);
        var req = Assert.Single(h.Paste.Requests);
        Assert.Equal("hola mundo", req.Text);
        Assert.Equal(ClipboardMode.Restore, req.Mode);
        Assert.Equal("hola mundo", h.Controller.LastDictation);
        Assert.Contains("recording", h.Overlay.Snapshot());
        Assert.Contains("transcribing", h.Overlay.Snapshot());
        Assert.Equal("hide", h.Overlay.Snapshot()[^1]);
        Assert.False(h.Escape.Armed);
        await Wait.UntilAsync(() => h.Recorder.Sessions[0].Disposed, what: "session disposed");
    }

    [Fact]
    public async Task Escape_during_recording_discards_audio_and_sends_nothing()
    {
        var h = new Harness();
        await h.StartRecordingAsync();
        Assert.True(h.Escape.Armed);

        h.Controller.Cancel(CancelReason.User);
        await h.IdleAsync();

        Assert.Equal(0, h.Transcriber.Calls);
        Assert.Empty(h.Paste.Requests);
        Assert.True(h.Recorder.Sessions[0].Aborted);
        Assert.Contains("cancelled", h.Overlay.Snapshot());
        await Wait.UntilAsync(() => h.Recorder.Sessions[0].Disposed);
    }

    [Fact]
    public async Task Late_http_response_after_escape_is_never_pasted_nor_revives_the_session()
    {
        var h = new Harness { };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Transcriber.HonorCancellation = false; // simulates a response that arrives even though it was cancelled
        h.Transcriber.Gate = _ => gate.Task;

        await h.StartRecordingAsync();
        h.Controller.Toggle();
        await Wait.UntilAsync(() => h.Controller.State == SessionState.Transcribing, what: "Transcribing");

        h.Controller.Cancel(CancelReason.User);
        gate.SetResult(); // the late response arrives now
        await h.IdleAsync();

        Assert.Empty(h.Paste.Requests);
        Assert.Null(h.Controller.LastDictation);
        Assert.Contains("cancelled", h.Overlay.Snapshot());

        // The application remains operational and there is no zombie session.
        h.Transcriber.Gate = null;
        await h.FullDictationAsync();
        Assert.Single(h.Paste.Requests);
        Assert.Equal(2, h.Recorder.Starts);
    }

    [Fact]
    public async Task Escape_during_http_cancels_the_request()
    {
        var h = new Harness();
        CancellationToken seen = default;
        h.Transcriber.Gate = async ct => { seen = ct; await Task.Delay(Timeout.Infinite, ct); };

        await h.StartRecordingAsync();
        h.Controller.Toggle();
        await Wait.UntilAsync(() => h.Controller.State == SessionState.Transcribing);
        h.Controller.Cancel(CancelReason.User);
        await h.IdleAsync();

        Assert.True(seen.IsCancellationRequested);
        Assert.Empty(h.Paste.Requests);
    }

    [Fact]
    public async Task Rapid_and_repeated_toggles_do_not_duplicate_capture_or_request()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Recorder.Gate = _ => gate.Task;

        h.Controller.Toggle();
        for (int i = 0; i < 6; i++) h.Controller.Toggle(); // double press and key auto-repeat while the device is opening
        Assert.Equal(1, h.Recorder.Starts);
        gate.SetResult();
        await h.IdleAsync();

        Assert.Equal(1, h.Recorder.Starts);
        Assert.Equal(1, h.Transcriber.Calls);
        Assert.Single(h.Paste.Requests);
    }

    [Fact]
    public async Task Toggle_while_transcribing_is_ignored()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Transcriber.Gate = _ => gate.Task;
        await h.StartRecordingAsync();
        h.Controller.Toggle();
        await Wait.UntilAsync(() => h.Controller.State == SessionState.Transcribing);

        h.Controller.Toggle();
        h.Controller.Toggle();
        Assert.Equal(1, h.Recorder.Starts);
        Assert.Equal(SessionState.Transcribing, h.Controller.State);

        gate.SetResult();
        await h.IdleAsync();
        Assert.Equal(1, h.Transcriber.Calls);
    }

    [Fact]
    public async Task Audio_start_failure_reports_error_and_allows_another_dictation()
    {
        var h = new Harness();
        h.Recorder.StartFails = new DictationException("No hay ningún micrófono disponible.", ErrorKind.Audio);
        h.Controller.Toggle();
        await h.IdleAsync();
        Assert.Contains(h.Overlay.Snapshot(), e => e.StartsWith("error:"));
        Assert.False(h.Escape.Armed);

        h.Recorder.StartFails = null;
        await h.FullDictationAsync();
        Assert.Single(h.Paste.Requests);
    }

    [Fact]
    public async Task Network_failure_releases_resources_and_allows_another_dictation()
    {
        var h = new Harness();
        h.Transcriber.Fails = new DictationException("No se pudo conectar con OpenAI.", ErrorKind.Network);
        await h.FullDictationAsync();

        Assert.Empty(h.Paste.Requests);
        Assert.Contains(h.Overlay.Snapshot(), e => e.StartsWith("error:No se pudo conectar"));
        await Wait.UntilAsync(() => h.Recorder.Sessions[0].Disposed);

        h.Transcriber.Fails = null;
        await h.FullDictationAsync();
        Assert.Single(h.Paste.Requests);
    }

    [Fact]
    public async Task Unexpected_exception_does_not_leave_controller_blocked()
    {
        var h = new Harness();
        h.Transcriber.Fails = new InvalidOperationException("boom");
        await h.FullDictationAsync();
        Assert.Contains(h.Overlay.Snapshot(), e => e.StartsWith("error:Error inesperado"));
        h.Transcriber.Fails = null;
        await h.FullDictationAsync();
        Assert.Single(h.Paste.Requests);
    }

    [Fact]
    public async Task Missing_api_key_does_not_open_the_microphone()
    {
        var h = new Harness();
        h.Credentials.Key = null;
        h.Controller.Toggle();
        await h.IdleAsync();
        Assert.Equal(0, h.Recorder.Starts);
        Assert.Contains(h.Overlay.Snapshot(), e => e.Contains("API key"));
    }

    [Theory]
    [InlineData(0, 16000)]
    [InlineData(1000, 100)]
    public async Task Silent_or_too_short_recordings_are_not_sent(short value, int samples)
    {
        var h = new Harness();
        h.Recorder.Configure = s => { s.Value = value; s.Samples = samples; };
        await h.FullDictationAsync();
        Assert.Equal(0, h.Transcriber.Calls);
        Assert.Contains(h.Overlay.Snapshot(), e => e.StartsWith("error:"));
    }

    [Fact]
    public async Task Empty_transcription_does_not_paste()
    {
        var h = new Harness();
        h.Transcriber.Text = "   ";
        await h.FullDictationAsync();
        Assert.Empty(h.Paste.Requests);
        Assert.Null(h.Controller.LastDictation);
        Assert.Contains(h.Overlay.Snapshot(), e => e.Contains("No se detectó voz"));
    }

    [Fact]
    public async Task Recording_limit_finishes_cleanly_and_transcribes_what_was_captured()
    {
        var h = new Harness();
        await h.StartRecordingAsync();
        h.Recorder.Sessions[0].RaiseLimit();
        await h.IdleAsync();
        Assert.Equal(1, h.Transcriber.Calls);
        Assert.Single(h.Paste.Requests);
    }

    [Fact]
    public async Task Microphone_lost_with_audio_captured_transcribes_and_pastes_what_was_recorded()
    {
        var h = new Harness();
        await h.StartRecordingAsync();
        h.Recorder.Sessions[0].RaiseDeviceLost();
        await h.IdleAsync();

        Assert.Equal(1, h.Transcriber.Calls);
        Assert.Equal("hola mundo", Assert.Single(h.Paste.Requests).Text);
        var events = h.Overlay.Snapshot();
        Assert.Contains(events, e => e == "transcribing");
        Assert.Contains(events, e => e.StartsWith("info:Micrófono desconectado"));
        Assert.DoesNotContain(events, e => e.StartsWith("error:"));
        Assert.Equal(TimeSpan.FromSeconds(5), h.Overlay.LastInfoDuration);
        await Wait.UntilAsync(() => h.Recorder.Sessions[0].Disposed);
    }

    [Fact]
    public async Task Recording_overlay_receives_a_level_source_fed_by_the_session_peak()
    {
        var h = new Harness();
        await h.StartRecordingAsync();
        var level = h.Overlay.LevelSource;
        Assert.NotNull(level);
        Assert.Equal(0f, level!());
        h.Recorder.Sessions[0].Peak = 20000; // above -6 dB
        Assert.Equal(1f, level());
        Assert.Equal(0f, level());           // reading consumes the peak
        h.Recorder.Sessions[0].RaiseLimit();
        await h.IdleAsync();
    }

    [Fact]
    public async Task Microphone_lost_with_almost_no_audio_reports_the_disconnection_instead_of_sending()
    {
        var h = new Harness();
        h.Recorder.Configure = s => s.Samples = 100;
        await h.StartRecordingAsync();
        h.Recorder.Sessions[0].RaiseDeviceLost();
        await h.IdleAsync();

        Assert.Equal(0, h.Transcriber.Calls);
        Assert.Empty(h.Paste.Requests);
        Assert.Contains(h.Overlay.Snapshot(), e => e.StartsWith("error:El micrófono se desconectó"));
        Assert.Equal(TimeSpan.FromSeconds(5), h.Overlay.LastErrorDuration);
    }

    [Fact]
    public async Task Device_fault_during_recording_aborts_without_sending()
    {
        var h = new Harness();
        await h.StartRecordingAsync();
        h.Recorder.Sessions[0].RaiseFault(new DictationException("El micrófono se desconectó o cambió.", ErrorKind.Audio));
        await h.IdleAsync();
        Assert.Equal(0, h.Transcriber.Calls);
        Assert.Contains(h.Overlay.Snapshot(), e => e.Contains("desconectó"));
        await Wait.UntilAsync(() => h.Recorder.Sessions[0].Disposed);
    }

    [Fact]
    public async Task Session_interruption_cancels_silently()
    {
        var h = new Harness();
        await h.StartRecordingAsync();
        h.Controller.Cancel(CancelReason.SessionInterrupted);
        await h.IdleAsync();
        Assert.Equal(0, h.Transcriber.Calls);
        Assert.DoesNotContain("cancelled", h.Overlay.Snapshot());
    }

    [Fact]
    public async Task Shutdown_cancels_active_session_and_waits_for_cleanup()
    {
        var h = new Harness();
        await h.StartRecordingAsync();
        await h.Controller.ShutdownAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(SessionState.Idle, h.Controller.State);
        Assert.True(h.Recorder.Sessions[0].Aborted);
    }

    [Fact]
    public async Task Cancel_while_device_is_opening_leaves_no_orphan_capture()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Recorder.Gate = _ => gate.Task;
        h.Controller.Toggle();
        h.Controller.Cancel(CancelReason.User);
        gate.SetResult();
        await h.IdleAsync();
        // The device opened after cancelling: the created session must have been released.
        Assert.All(h.Recorder.Sessions, s => Assert.True(s.Disposed || s.Aborted));
        Assert.Equal(0, h.Transcriber.Calls);
    }

    [Fact]
    public async Task Overlay_shows_preparing_and_only_says_recording_once_capture_is_live()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Recorder.Gate = _ => gate.Task;

        h.Controller.Toggle();
        await Wait.UntilAsync(() => h.Overlay.Snapshot().Contains("preparing"), 3000, "preparing");
        Assert.DoesNotContain("recording", h.Overlay.Snapshot());
        Assert.Equal(1, h.Recorder.Starts); // capture was already requested, in parallel with preparation
        Assert.Equal(0, h.Transcriber.WarmUps);

        gate.SetResult();
        await Wait.UntilAsync(() => h.Overlay.Snapshot().Contains("recording"), 3000, "recording");
        var events = h.Overlay.Snapshot();
        Assert.True(Array.IndexOf(events, "preparing") < Array.IndexOf(events, "recording"));
        await Wait.UntilAsync(() => h.Transcriber.WarmUps == 1, 3000, "warm-up de la conexion");
        h.Controller.Cancel(CancelReason.User);
        await h.IdleAsync();
    }

    [Fact]
    public async Task Cancel_while_the_device_is_opening_never_claims_to_be_recording()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Recorder.Gate = _ => gate.Task;
        h.Controller.Toggle();
        await Wait.UntilAsync(() => h.Overlay.Snapshot().Contains("preparing"), 3000);
        h.Controller.Cancel(CancelReason.User);
        gate.SetResult();
        await h.IdleAsync();
        Assert.DoesNotContain("recording", h.Overlay.Snapshot());
        Assert.Equal(0, h.Transcriber.WarmUps);
    }

    [Fact]
    public async Task Failed_device_start_never_claims_to_be_recording_nor_warms_the_connection()
    {
        var h = new Harness();
        h.Recorder.StartFails = new DictationException("No hay ningún micrófono disponible.", ErrorKind.Audio);
        h.Controller.Toggle();
        await h.IdleAsync();
        Assert.DoesNotContain("recording", h.Overlay.Snapshot());
        Assert.Equal(0, h.Transcriber.WarmUps);
    }

    [Fact]
    public async Task Stale_limit_event_from_a_finished_session_does_not_corrupt_the_next_one()
    {
        var h = new Harness();
        await h.FullDictationAsync();
        var old = h.Recorder.Sessions[0];

        await h.StartRecordingAsync();
        old.RaiseLimit(); // late event from the previous capture
        Assert.Equal(SessionState.Recording, h.Controller.State);

        h.Controller.Toggle();
        await h.IdleAsync();
        Assert.Equal(2, h.Paste.Requests.Count);
    }

    [Fact]
    public async Task Cancel_in_idle_is_a_noop()
    {
        var h = new Harness();
        h.Controller.Cancel(CancelReason.User);
        Assert.Equal(SessionState.Idle, h.Controller.State);
        await h.FullDictationAsync();
        Assert.Single(h.Paste.Requests);
    }
}
