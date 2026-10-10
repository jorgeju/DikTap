using System.Runtime.InteropServices;
using System.Text;
using DikTap.Interop;

namespace DikTap.Services.Paste;

internal sealed record ClipboardFormatData(uint Format, byte[] Data);

// In-memory copy of the clipboard formats that can be duplicated (HGLOBAL-based ones).
internal sealed record ClipboardSnapshot(IReadOnlyList<ClipboardFormatData> Formats, int SkippedFormats)
{
    public long TotalBytes => Formats.Sum(f => (long)f.Data.Length);
}

internal interface IClipboard
{
    uint SequenceNumber { get; }
    // null if it could not be read within the time limit (blocked owner, too large...).
    Task<ClipboardSnapshot?> TakeSnapshotAsync(TimeSpan timeout, CancellationToken cancellationToken);
    // Returns the sequence number after writing, or null if the clipboard was busy.
    Task<uint?> SetTextAsync(string text, bool excludeFromHistory, CancellationToken cancellationToken);
    Task<bool> RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken);
}

internal sealed class Win32Clipboard : IClipboard
{
    private const int OpenAttempts = 20;
    private static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(15);
    private const long MaxSnapshotBytes = 32L * 1024 * 1024;

    private readonly Func<IntPtr> _ownerWindow;

    public Win32Clipboard(Func<IntPtr> ownerWindow) => _ownerWindow = ownerWindow;

    public uint SequenceNumber => NativeMethods.GetClipboardSequenceNumber();

    public async Task<ClipboardSnapshot?> TakeSnapshotAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Read on a worker thread: an owner with delayed rendering may be slow or block.
        var work = Task.Run(SnapshotCore, CancellationToken.None);
        try
        {
            return await work.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            Logger.Info("Clipboard snapshot: timed out");
            return null;
        }
    }

    private static ClipboardSnapshot? SnapshotCore()
    {
        if (!OpenWithRetrySync(IntPtr.Zero)) return null;
        try
        {
            var list = new List<ClipboardFormatData>();
            int skipped = 0;
            long total = 0;
            uint fmt = 0;
            while ((fmt = NativeMethods.EnumClipboardFormats(fmt)) != 0)
            {
                if (!IsGlobalMemoryFormat(fmt) || IsOleBookkeeping(fmt)) { skipped++; continue; }
                IntPtr h = NativeMethods.GetClipboardData(fmt);
                if (h == IntPtr.Zero) { skipped++; continue; }
                long size = (long)NativeMethods.GlobalSize(h);
                if (size <= 0) { skipped++; continue; }
                total += size;
                if (total > MaxSnapshotBytes) { Logger.Info("Clipboard snapshot too large"); return null; }
                IntPtr p = NativeMethods.GlobalLock(h);
                if (p == IntPtr.Zero) { skipped++; continue; }
                try
                {
                    var data = new byte[size];
                    Marshal.Copy(p, data, 0, data.Length);
                    list.Add(new ClipboardFormatData(fmt, data));
                }
                finally
                {
                    NativeMethods.GlobalUnlock(h);
                }
            }
            return new ClipboardSnapshot(list, skipped);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    // Restoring the raw OLE internal state (a reference to an IDataObject that no longer exists) makes OleGetClipboard return empty data.
    private static bool IsOleBookkeeping(uint f)
    {
        if (f < 0xC000) return false;
        var sb = new StringBuilder(64);
        return NativeMethods.GetClipboardFormatNameW(f, sb, sb.Capacity) > 0 && sb.ToString() is "DataObject" or "Ole Private Data";
    }

    // CF_BITMAP, palettes, metafiles and the GDI range are not duplicable HGLOBAL blocks.
    private static bool IsGlobalMemoryFormat(uint f) =>
        f is not (NativeMethods.CF_BITMAP or NativeMethods.CF_METAFILEPICT or NativeMethods.CF_PALETTE or NativeMethods.CF_ENHMETAFILE
            or NativeMethods.CF_OWNERDISPLAY or 0x82 or 0x83 or 0x8E)
        && !(f >= 0x300 && f <= 0x3FF);

    public async Task<uint?> SetTextAsync(string text, bool excludeFromHistory, CancellationToken cancellationToken)
    {
        if (!await OpenWithRetryAsync(_ownerWindow(), cancellationToken).ConfigureAwait(true)) return null;
        try
        {
            if (!NativeMethods.EmptyClipboard()) return null;
            IntPtr mem = AllocString(text);
            if (mem == IntPtr.Zero) return null;
            if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, mem) == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(mem);
                return null;
            }
            if (excludeFromHistory)
            {
                // Keeps the transcription out of the history (Win+V) and from syncing to the cloud.
                SetFlag("ExcludeClipboardContentFromMonitorProcessing");
                SetFlag("CanIncludeInClipboardHistory");
            }
            // Dictation is never uploaded to the cloud clipboard, even if it is kept in the local history.
            SetFlag("CanUploadToCloudClipboard");
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
        return NativeMethods.GetClipboardSequenceNumber();
    }

    public async Task<bool> RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        // More patience than when pasting: another process (history, sync) may hold the clipboard open for a moment.
        if (!await OpenWithRetryAsync(_ownerWindow(), cancellationToken, attempts: 60).ConfigureAwait(true)) return false;
        try
        {
            if (!NativeMethods.EmptyClipboard()) return false;
            foreach (var f in snapshot.Formats)
            {
                IntPtr mem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)f.Data.Length);
                if (mem == IntPtr.Zero) continue;
                IntPtr p = NativeMethods.GlobalLock(mem);
                if (p == IntPtr.Zero) { NativeMethods.GlobalFree(mem); continue; }
                Marshal.Copy(f.Data, 0, p, f.Data.Length);
                NativeMethods.GlobalUnlock(mem);
                if (NativeMethods.SetClipboardData(f.Format, mem) == IntPtr.Zero) NativeMethods.GlobalFree(mem);
            }
            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static void SetFlag(string formatName)
    {
        uint fmt = NativeMethods.RegisterClipboardFormatW(formatName);
        if (fmt == 0) return;
        IntPtr mem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)4);
        if (mem == IntPtr.Zero) return;
        IntPtr p = NativeMethods.GlobalLock(mem);
        if (p == IntPtr.Zero) { NativeMethods.GlobalFree(mem); return; }
        Marshal.WriteInt32(p, 0);
        NativeMethods.GlobalUnlock(mem);
        if (NativeMethods.SetClipboardData(fmt, mem) == IntPtr.Zero) NativeMethods.GlobalFree(mem);
    }

    private static IntPtr AllocString(string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
        IntPtr mem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (mem == IntPtr.Zero) return IntPtr.Zero;
        IntPtr p = NativeMethods.GlobalLock(mem);
        if (p == IntPtr.Zero) { NativeMethods.GlobalFree(mem); return IntPtr.Zero; }
        Marshal.Copy(bytes, 0, p, bytes.Length);
        NativeMethods.GlobalUnlock(mem);
        return mem;
    }

    private static bool OpenWithRetrySync(IntPtr owner)
    {
        for (int i = 0; i < OpenAttempts; i++)
        {
            if (NativeMethods.OpenClipboard(owner)) return true;
            Thread.Sleep(OpenDelay);
        }
        return false;
    }

    private static async Task<bool> OpenWithRetryAsync(IntPtr owner, CancellationToken ct, int attempts = OpenAttempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (NativeMethods.OpenClipboard(owner)) return true;
            await Task.Delay(OpenDelay, ct).ConfigureAwait(true);
        }
        return false;
    }
}
