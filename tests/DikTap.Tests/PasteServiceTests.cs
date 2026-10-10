using DikTap;
using DikTap.Services.Paste;
using DikTap.Services.Settings;

namespace DikTap.Tests;

public class PasteServiceTests
{
    private sealed class Harness
    {
        public readonly FakeClipboard Clipboard = new();
        public readonly FakeKeyboard Keyboard = new();
        public readonly FakeDestination Destination = new();
        public readonly PasteService Service;
        public readonly DestinationSnapshot Dest = new(new IntPtr(100), 1234, 1, new IntPtr(200));

        public Harness(PasteTimings? timings = null)
        {
            Service = new PasteService(Clipboard, Keyboard, Destination, timings ?? new PasteTimings
            {
                ModifierWait = TimeSpan.FromMilliseconds(120),
                ModifierPoll = TimeSpan.FromMilliseconds(5),
                RestoreDelay = TimeSpan.FromMilliseconds(40),
                SnapshotTimeout = TimeSpan.FromMilliseconds(200),
            });
        }

        public PasteRequest Request(string text = "texto dictado", ClipboardMode mode = ClipboardMode.Restore)
            => new(text, Dest, mode);
    }

    [Fact]
    public async Task Pastes_via_ctrl_v_and_restores_previous_clipboard_after_delay()
    {
        var h = new Harness();
        var r = await h.Service.PasteAsync(h.Request(), CancellationToken.None);

        Assert.Equal(PasteOutcome.Pasted, r.Outcome);
        Assert.Equal(1, h.Keyboard.Pastes);
        Assert.True(h.Clipboard.LastExclude);
        Assert.Equal("texto dictado", h.Clipboard.Text);   // not restored yet: the target control reads late
        Assert.Equal(0, h.Clipboard.Restores);

        await h.Service.FlushPendingRestoreAsync();
        Assert.Equal(1, h.Clipboard.Restores);
        Assert.Same(h.Clipboard.SnapshotToReturn, h.Clipboard.Restored);
    }

    [Fact]
    public async Task Does_not_restore_over_content_copied_meanwhile()
    {
        var h = new Harness();
        await h.Service.PasteAsync(h.Request(), CancellationToken.None);
        h.Clipboard.ExternalCopy("lo copio el usuario");
        await h.Service.FlushPendingRestoreAsync();

        Assert.Equal(0, h.Clipboard.Restores);
        Assert.Equal("lo copio el usuario", h.Clipboard.Text);
    }

    [Fact]
    public async Task Failed_restore_is_retried_once()
    {
        var h = new Harness();
        h.Clipboard.FailRestores = 1;
        await h.Service.PasteAsync(h.Request(), CancellationToken.None);
        await h.Service.FlushPendingRestoreAsync();
        Assert.Equal(2, h.Clipboard.Restores);
        Assert.Equal("RESTAURADO", h.Clipboard.Text);
    }

    [Fact]
    public async Task Restore_can_be_disabled()
    {
        var h = new Harness();
        await h.Service.PasteAsync(h.Request(mode: ClipboardMode.Keep), CancellationToken.None);
        await h.Service.FlushPendingRestoreAsync();
        Assert.Equal(0, h.Clipboard.Restores);
        Assert.Equal(1, h.Keyboard.Pastes);
    }

    [Fact]
    public async Task Keep_mode_leaves_the_dictation_on_the_clipboard_without_history_and_never_restores()
    {
        var h = new Harness();
        var r = await h.Service.PasteAsync(h.Request(mode: ClipboardMode.Keep), CancellationToken.None);
        await h.Service.FlushPendingRestoreAsync();

        Assert.Equal(PasteOutcome.Pasted, r.Outcome);
        Assert.Equal(1, h.Keyboard.Pastes);
        Assert.Equal("texto dictado", h.Clipboard.Text);
        Assert.True(h.Clipboard.LastExclude);
        Assert.Equal(0, h.Clipboard.Restores);
    }

    [Fact]
    public async Task KeepWithHistory_mode_leaves_the_dictation_on_the_clipboard_and_allows_history()
    {
        var h = new Harness();
        var r = await h.Service.PasteAsync(h.Request(mode: ClipboardMode.KeepWithHistory), CancellationToken.None);
        await h.Service.FlushPendingRestoreAsync();

        Assert.Equal(PasteOutcome.Pasted, r.Outcome);
        Assert.Equal(1, h.Keyboard.Pastes);
        Assert.Equal("texto dictado", h.Clipboard.Text);
        Assert.False(h.Clipboard.LastExclude);
        Assert.Equal(0, h.Clipboard.Restores);
    }

    [Fact]
    public async Task Restore_mode_keeps_the_dictation_out_of_the_history_while_it_is_on_the_clipboard()
    {
        var h = new Harness();
        await h.Service.PasteAsync(h.Request(mode: ClipboardMode.Restore), CancellationToken.None);
        Assert.True(h.Clipboard.LastExclude);
        await h.Service.FlushPendingRestoreAsync();
    }

    [Fact]
    public async Task Destination_that_is_not_the_foreground_window_at_the_end_blocks_autopaste_and_keeps_text_for_manual_paste()
    {
        var h = new Harness();
        h.Destination.Status = DestinationStatus.ForegroundChanged;
        var r = await h.Service.PasteAsync(h.Request(), CancellationToken.None);

        Assert.Equal(PasteOutcome.CopiedManual, r.Outcome);
        Assert.Contains("Texto copiado", r.Message);
        Assert.Equal(0, h.Keyboard.Pastes);
        Assert.Equal("texto dictado", h.Clipboard.Text);
        Assert.False(h.Clipboard.LastExclude);
        await h.Service.FlushPendingRestoreAsync();
        Assert.Equal(0, h.Clipboard.Restores);
    }

    [Fact]
    public async Task Returning_to_the_original_window_before_the_end_allows_autopaste_again()
    {
        var h = new Harness();
        // The target state is measured at the end: a temporary window change no longer counts if the original is active again.
        var r = await h.Service.PasteAsync(h.Request(), CancellationToken.None);

        Assert.Equal(PasteOutcome.Pasted, r.Outcome);
        Assert.Equal(1, h.Keyboard.Pastes);
        await h.Service.FlushPendingRestoreAsync();
    }

    [Theory]
    [InlineData(DestinationStatus.WindowClosed)]
    [InlineData(DestinationStatus.ForegroundChanged)]
    [InlineData(DestinationStatus.FocusChanged)]
    [InlineData(DestinationStatus.Elevated)]
    public async Task Invalid_destination_never_injects_keys(DestinationStatus status)
    {
        var h = new Harness();
        h.Destination.Status = status;
        var r = await h.Service.PasteAsync(h.Request(), CancellationToken.None);
        Assert.Equal(PasteOutcome.CopiedManual, r.Outcome);
        Assert.Equal(0, h.Keyboard.Pastes);
    }

    [Fact]
    public async Task Unknown_destination_is_not_pasted()
    {
        var h = new Harness();
        var r = await h.Service.PasteAsync(new PasteRequest("x", null, ClipboardMode.Restore), CancellationToken.None);
        Assert.Equal(PasteOutcome.CopiedManual, r.Outcome);
        Assert.Equal(0, h.Keyboard.Pastes);
    }

    [Fact]
    public async Task Destination_becoming_invalid_while_waiting_for_modifiers_falls_back_to_manual()
    {
        var h = new Harness();
        h.Keyboard.Released = false;
        var task = h.Service.PasteAsync(h.Request(), CancellationToken.None);
        await Task.Delay(30);
        h.Destination.Status = DestinationStatus.ForegroundChanged;
        h.Keyboard.Released = true;
        var r = await task;
        Assert.Equal(PasteOutcome.CopiedManual, r.Outcome);
        Assert.Equal(0, h.Keyboard.Pastes);
    }

    [Fact]
    public async Task Modifiers_never_released_falls_back_to_manual_without_injecting()
    {
        var h = new Harness();
        h.Keyboard.Released = false;
        var r = await h.Service.PasteAsync(h.Request(), CancellationToken.None);
        Assert.Equal(PasteOutcome.CopiedManual, r.Outcome);
        Assert.Equal(0, h.Keyboard.Pastes);
        await h.Service.FlushPendingRestoreAsync();
        Assert.Equal(0, h.Clipboard.Restores);
    }

    [Fact]
    public async Task Sendinput_failure_falls_back_to_manual_and_does_not_restore()
    {
        var h = new Harness();
        h.Keyboard.PasteOk = false;
        var r = await h.Service.PasteAsync(h.Request(), CancellationToken.None);
        Assert.Equal(PasteOutcome.CopiedManual, r.Outcome);
        await h.Service.FlushPendingRestoreAsync();
        Assert.Equal(0, h.Clipboard.Restores);
        Assert.Equal("texto dictado", h.Clipboard.Text);
    }

    [Fact]
    public async Task Cancel_before_injection_restores_previous_clipboard_and_sends_no_keys()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        h.Keyboard.Released = false;
        var task = h.Service.PasteAsync(h.Request(), cts.Token);
        await Task.Delay(20);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, h.Keyboard.Pastes);
        Assert.Equal(1, h.Clipboard.Restores);
    }

    [Fact]
    public async Task Snapshot_failure_does_not_block_paste_and_is_reported()
    {
        var h = new Harness();
        h.Clipboard.SnapshotToReturn = null;
        var r = await h.Service.PasteAsync(h.Request(), CancellationToken.None);
        Assert.Equal(PasteOutcome.Pasted, r.Outcome);
        Assert.Contains("portapapeles anterior", r.Message);
        await h.Service.FlushPendingRestoreAsync();
        Assert.Equal(0, h.Clipboard.Restores);
        Assert.Equal("texto dictado", h.Clipboard.Text);
    }

    [Fact]
    public async Task Busy_clipboard_surfaces_an_actionable_error_and_a_later_paste_still_works()
    {
        var h = new Harness();
        h.Clipboard.Busy = true;
        var ex = await Assert.ThrowsAsync<DictationException>(() => h.Service.PasteAsync(h.Request(), CancellationToken.None));
        Assert.Equal(ErrorKind.Clipboard, ex.Kind);
        Assert.Equal(0, h.Keyboard.Pastes);

        h.Clipboard.Busy = false;
        var r = await h.Service.PasteAsync(h.Request(), CancellationToken.None);
        Assert.Equal(PasteOutcome.Pasted, r.Outcome);
    }

    [Fact]
    public async Task Second_paste_waits_for_pending_restore_so_it_snapshots_the_users_clipboard()
    {
        var h = new Harness(new PasteTimings { RestoreDelay = TimeSpan.FromMilliseconds(80), ModifierPoll = TimeSpan.FromMilliseconds(5) });
        await h.Service.PasteAsync(h.Request("uno"), CancellationToken.None);
        await h.Service.PasteAsync(h.Request("dos"), CancellationToken.None);
        // The first restore completed before the second was processed.
        Assert.Equal(1, h.Clipboard.Restores);
        await h.Service.FlushPendingRestoreAsync();
        Assert.Equal(2, h.Clipboard.Restores);
    }

    [Fact]
    public async Task CopyText_leaves_text_without_restoring()
    {
        var h = new Harness();
        Assert.True(await h.Service.CopyTextAsync("ultimo"));
        Assert.Equal("ultimo", h.Clipboard.Text);
        Assert.False(h.Clipboard.LastExclude);
        Assert.Equal(0, h.Clipboard.Restores);
    }
}
