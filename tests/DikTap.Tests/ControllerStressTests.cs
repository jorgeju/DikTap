using DikTap;
using DikTap.Services.Paste;
using DikTap.Services.Settings;

namespace DikTap.Tests;

// Random interleavings of presses, cancellations, failures and limits: the controller must always return to idle
// and leave nothing open (captures, target hooks, Escape).
public class ControllerStressTests
{
    private sealed class CountingDestination : IDestinationService
    {
        public DestinationSnapshot? Capture() => new(new IntPtr(1), 1, 1, IntPtr.Zero);
        public DestinationStatus Validate(DestinationSnapshot snapshot) => DestinationStatus.Valid;
    }

    [Fact]
    public async Task Random_interleavings_always_return_to_idle_without_leaks()
    {
        var rnd = new Random(int.TryParse(Environment.GetEnvironmentVariable("STRESS_SEED"), out var seed) ? seed : 20261007);
        var gate = new object();
        int Next(int n) { lock (gate) return rnd.Next(n); }

        var recorder = new FakeRecorder { Gate = _ => Task.Delay(Next(8)) };
        var transcriber = new FakeTranscriber { Gate = ct => Task.Delay(Next(15), ct) };
        var paste = new FakePaste { Gate = ct => Task.Delay(Next(6), ct) };
        var dest = new CountingDestination();
        var escape = new FakeEscape();
        var overlay = new FakeOverlay();
        var controller = new DictationController(recorder, transcriber, paste, dest, new FakeCredentials(), overlay, escape, () => new AppSettings());

        int iterations = int.TryParse(Environment.GetEnvironmentVariable("STRESS_ITER"), out var n) ? n : 250;
        for (int i = 0; i < iterations; i++)
        {
            recorder.StartFails = Next(8) == 0 ? new DictationException("sin microfono", ErrorKind.Audio) : null;
            transcriber.Fails = Next(6) == 0 ? new DictationException("red", ErrorKind.Network) : null;

            controller.Toggle();
            int actions = Next(5);
            for (int a = 0; a < actions; a++)
            {
                await Task.Delay(Next(12));
                FakeSession? last;
                lock (recorder.Sessions) last = recorder.Sessions.LastOrDefault();
                switch (Next(7))
                {
                    case 0:
                    case 1: controller.Toggle(); break;
                    case 2: controller.Cancel(CancelReason.User); break;
                    case 3: controller.Cancel(CancelReason.SessionInterrupted); break;
                    case 4: last?.RaiseFault(new DictationException("desconectado", ErrorKind.Audio)); break;
                    case 5: last?.RaiseDeviceLost(); break;
                    default: last?.RaiseLimit(); break;
                }
            }

            // A session that is still recording waits for the user: stop it as the shortcut would.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (controller.State != SessionState.Idle)
            {
                if (sw.ElapsedMilliseconds > 10_000) throw new TimeoutException($"Atascado en {controller.State} (iteracion {i})");
                if (controller.State == SessionState.Recording) controller.Toggle();
                await Task.Delay(5);
            }
        }

        await Wait.UntilAsync(() => recorder.Sessions.ToArray().All(s => s.Disposed), 5000, "capturas liberadas");
        Assert.Equal(SessionState.Idle, controller.State);
        Assert.False(escape.Armed);
        Assert.Equal(escape.Arms, escape.Disarms);
        Assert.True(paste.Requests.Count <= transcriber.Calls);
        Assert.All(paste.Requests.ToArray(), r => Assert.Equal("hola mundo", r.Text));

        // After all the abuse, a normal dictation still works.
        recorder.StartFails = null;
        transcriber.Fails = null;
        transcriber.Gate = null;
        paste.Gate = null;
        int before = paste.Requests.Count;
        controller.Toggle();
        await Wait.UntilAsync(() => controller.State == SessionState.Recording, 5000, "Recording");
        controller.Toggle();
        await Wait.UntilAsync(() => controller.State == SessionState.Idle && paste.Requests.Count == before + 1, 5000, "dictado final");
    }

    [Theory]
    [InlineData("starting")]
    [InlineData("recording")]
    [InlineData("transcribing")]
    [InlineData("pasting")]
    public async Task Cancel_at_each_stage_frees_resources_and_never_completes_a_paste(string stage)
    {
        var recorderGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new FakeRecorder { Gate = stage == "starting" ? (_ => recorderGate.Task) : null };
        var transcriber = new FakeTranscriber { Gate = stage == "transcribing" ? (ct => Task.Delay(Timeout.Infinite, ct)) : null };
        var paste = new FakePaste { Gate = stage == "pasting" ? (ct => Task.Delay(Timeout.Infinite, ct)) : null };
        var dest = new CountingDestination();
        var escape = new FakeEscape();
        var overlay = new FakeOverlay();
        var c = new DictationController(recorder, transcriber, paste, dest, new FakeCredentials(), overlay, escape, () => new AppSettings());

        c.Toggle();
        if (stage != "starting")
        {
            await Wait.UntilAsync(() => c.State == SessionState.Recording, 3000, "Recording");
            if (stage != "recording")
            {
                c.Toggle();
                await Wait.UntilAsync(() => c.State == (stage == "transcribing" ? SessionState.Transcribing : SessionState.Pasting), 3000, stage);
            }
        }

        c.Cancel(CancelReason.User);
        recorderGate.TrySetResult();
        await Wait.UntilAsync(() => c.State == SessionState.Idle, 5000, "Idle tras cancelar en " + stage);

        Assert.False(escape.Armed);
        await Wait.UntilAsync(() => recorder.Sessions.ToArray().All(s => s.Disposed), 3000, "capturas liberadas");
        Assert.Contains("cancelled", overlay.Snapshot());
        Assert.DoesNotContain("hide", overlay.Snapshot());
        Assert.DoesNotContain(overlay.Snapshot(), e => e.StartsWith("info:"));
        if (stage != "pasting") Assert.Empty(paste.Requests);
    }
}
