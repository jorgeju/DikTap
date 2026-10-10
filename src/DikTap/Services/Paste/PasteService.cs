using DikTap.Localization;
using DikTap.Services.Settings;

namespace DikTap.Services.Paste;

internal enum PasteOutcome
{
    // Ctrl+V was injected. There is no confirmation the control processed it.
    Pasted,
    // The text stayed on the clipboard to be pasted by hand.
    CopiedManual,
}

// Empty Message = normal paste, nothing to report.
internal sealed record PasteResult(PasteOutcome Outcome, string Message);

internal sealed record PasteRequest(string Text, DestinationSnapshot? Destination, ClipboardMode Mode);

internal sealed record PasteTimings
{
    public TimeSpan SnapshotTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ModifierWait { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan ModifierPoll { get; init; } = TimeSpan.FromMilliseconds(20);
    // The target control reads the clipboard asynchronously: restoring immediately could paste the old content.
    public TimeSpan RestoreDelay { get; init; } = TimeSpan.FromMilliseconds(800);
}

internal interface IPasteService
{
    Task<PasteResult> PasteAsync(PasteRequest request, CancellationToken cancellationToken);
    // Leaves the text on the clipboard without restoring anything (manual copy, "Copiar ultimo dictado" / copy last dictation).
    Task<bool> CopyTextAsync(string text);
    // Waits for a pending restore to finish (used before another paste and on exit).
    Task FlushPendingRestoreAsync();
}

internal sealed class PasteService : IPasteService
{
    private readonly IClipboard _clipboard;
    private readonly IKeyboardInjector _keyboard;
    private readonly IDestinationService _destination;
    private readonly PasteTimings _timings;
    private Task _pendingRestore = Task.CompletedTask;

    public PasteService(IClipboard clipboard, IKeyboardInjector keyboard, IDestinationService destination, PasteTimings? timings = null)
    {
        _clipboard = clipboard;
        _keyboard = keyboard;
        _destination = destination;
        _timings = timings ?? new PasteTimings();
    }

    public Task FlushPendingRestoreAsync() => _pendingRestore;

    public async Task<bool> CopyTextAsync(string text)
    {
        await FlushPendingRestoreAsync().ConfigureAwait(true);
        return await _clipboard.SetTextAsync(text, excludeFromHistory: false, CancellationToken.None).ConfigureAwait(true) is not null;
    }

    public async Task<PasteResult> PasteAsync(PasteRequest request, CancellationToken cancellationToken)
    {
        await FlushPendingRestoreAsync().ConfigureAwait(true);

        if (CheckDestination(request) is { } early)
            return await ManualAsync(request.Text, early).ConfigureAwait(true);

        ClipboardSnapshot? snapshot = null;
        bool snapshotFailed = false;
        if (request.Mode == ClipboardMode.Restore)
        {
            snapshot = await _clipboard.TakeSnapshotAsync(_timings.SnapshotTimeout, cancellationToken).ConfigureAwait(true);
            snapshotFailed = snapshot is null;
        }

        bool excludeFromHistory = request.Mode != ClipboardMode.KeepWithHistory;
        uint? seq = await _clipboard.SetTextAsync(request.Text, excludeFromHistory, cancellationToken).ConfigureAwait(true);
        if (seq is null)
            throw new DictationException(Str.ClipboardBusyThrow, ErrorKind.Clipboard);

        try
        {
            bool released = await WaitForModifiersAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            string? problem = released ? CheckDestination(request) : Str.ModifiersHeld;
            if (problem is not null)
                return await ManualAsync(request.Text, problem).ConfigureAwait(true);

            // Last check immediately before injecting; after this there is no turning back.
            cancellationToken.ThrowIfCancellationRequested();
            if (!_keyboard.SendPaste())
                return await ManualAsync(request.Text, Str.PasteBlocked).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before injecting: returns the previous clipboard if nobody has changed it.
            if (snapshot is not null && _clipboard.SequenceNumber == seq)
                await _clipboard.RestoreAsync(snapshot, CancellationToken.None).ConfigureAwait(true);
            throw;
        }

        if (snapshot is not null)
            _pendingRestore = RestoreLaterAsync(snapshot, seq.Value);

        return new PasteResult(PasteOutcome.Pasted, snapshotFailed ? Str.PastedNoPreviousClipboard : string.Empty);
    }

    private async Task RestoreLaterAsync(ClipboardSnapshot snapshot, uint ourSequence)
    {
        try
        {
            await Task.Delay(_timings.RestoreDelay).ConfigureAwait(true);
            if (_clipboard.SequenceNumber != ourSequence)
            {
                Logger.Info("Clipboard modified by another app; not restoring");
                return;
            }
            if (!await _clipboard.RestoreAsync(snapshot, CancellationToken.None).ConfigureAwait(true))
            {
                Logger.Info("Clipboard restore failed; retrying once");
                await Task.Delay(_timings.RestoreDelay).ConfigureAwait(true);
                if (_clipboard.SequenceNumber == ourSequence)
                    await _clipboard.RestoreAsync(snapshot, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                Logger.Info("Clipboard restored");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Clipboard restore failed", ex);
        }
    }

    private async Task<bool> WaitForModifiersAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _timings.ModifierWait;
        while (!_keyboard.ModifiersReleased())
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(_timings.ModifierPoll, ct).ConfigureAwait(true);
        }
        return true;
    }

    private string? CheckDestination(PasteRequest r)
    {
        if (r.Destination is null) return Str.NoDestination;
        return _destination.Validate(r.Destination) switch
        {
            DestinationStatus.Valid => null,
            DestinationStatus.WindowClosed => Str.DestinationClosed,
            DestinationStatus.ForegroundChanged => Str.DestinationNotActive,
            DestinationStatus.FocusChanged => Str.FocusChanged,
            DestinationStatus.Elevated => Str.DestinationElevated,
            _ => Str.DestinationUnknown,
        };
    }

    private async Task<PasteResult> ManualAsync(string text, string reason)
    {
        // Nothing restored on top: the text must remain available to paste by hand.
        bool ok = await _clipboard.SetTextAsync(text, excludeFromHistory: false, CancellationToken.None).ConfigureAwait(true) is not null;
        if (!ok)
            throw new DictationException(Str.ManualBusy(reason), ErrorKind.Clipboard);
        return new PasteResult(PasteOutcome.CopiedManual, Str.ManualCopied(reason));
    }
}
