using DikTap.Interop;

namespace DikTap.Services.Paste;

// Window, process and control with focus at the moment dictation starts.
internal sealed record DestinationSnapshot(IntPtr Window, uint ProcessId, uint ThreadId, IntPtr FocusWindow);

public enum DestinationStatus
{
    Valid,
    WindowClosed,
    ForegroundChanged,
    FocusChanged,
    // The target runs elevated and UIPI prevents injecting keys from this application.
    Elevated,
}

internal interface IDestinationService
{
    DestinationSnapshot? Capture();
    // State of the target right now: a temporary window change during dictation does not count if it has returned by the end.
    DestinationStatus Validate(DestinationSnapshot snapshot);
}

internal sealed class Win32DestinationService : IDestinationService
{
    private static readonly Lazy<bool> SelfElevated = new(() => ProcessElevation.IsElevated((uint)Environment.ProcessId) == true);

    public DestinationSnapshot? Capture()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        uint tid = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        return new DestinationSnapshot(hwnd, pid, tid, GetFocus(tid));
    }

    public DestinationStatus Validate(DestinationSnapshot s)
    {
        if (!NativeMethods.IsWindow(s.Window)) return DestinationStatus.WindowClosed;
        uint tid = NativeMethods.GetWindowThreadProcessId(s.Window, out uint pid);
        if (pid != s.ProcessId) return DestinationStatus.WindowClosed;
        if (NativeMethods.GetForegroundWindow() != s.Window) return DestinationStatus.ForegroundChanged;
        if (!SelfElevated.Value && ProcessElevation.IsElevated(pid) != false) return DestinationStatus.Elevated;
        IntPtr focus = GetFocus(tid);
        if (s.FocusWindow != IntPtr.Zero && focus != IntPtr.Zero && focus != s.FocusWindow) return DestinationStatus.FocusChanged;
        return DestinationStatus.Valid;
    }

    private static IntPtr GetFocus(uint threadId)
    {
        var info = new GUITHREADINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        return NativeMethods.GetGUIThreadInfo(threadId, ref info) ? info.hwndFocus : IntPtr.Zero;
    }
}

internal static class ProcessElevation
{
    private const int ERROR_ACCESS_DENIED = 5;

    // true = elevated, false = not elevated, null = could not be determined.
    public static bool? IsElevated(uint processId)
    {
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out IntPtr token))
            {
                // A higher-integrity process denies access to its token to a non-elevated one.
                return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? true : null;
            }
            try
            {
                return NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, out TOKEN_ELEVATION e, 4, out _)
                    ? e.TokenIsElevated != 0
                    : null;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}
