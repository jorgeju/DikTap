using System.Runtime.InteropServices;
using DikTap.Interop;

namespace DikTap.Services.Paste;

// Window, process and control with focus at the moment dictation starts.
// FocusRuntimeId tracks the deep UI Automation element (e.g. edit field inside Electron/VS Code/WPF/Chrome).
internal sealed record DestinationSnapshot(IntPtr Window, uint ProcessId, uint ThreadId, IntPtr FocusWindow, int[]? FocusRuntimeId = null);

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
    void WarmUp() {}
}

internal sealed class Win32DestinationService : IDestinationService
{
    private static readonly Lazy<bool> SelfElevated = new(() => ProcessElevation.IsElevated((uint)Environment.ProcessId) == true);
    private static readonly Guid ClsidCUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static IUIAutomation? _uia;
    private static bool _uiaInitFailed;

    private readonly Func<int[]?> _getFocusedRuntimeId;

    public Win32DestinationService(Func<int[]?>? getFocusedRuntimeId = null)
    {
        _getFocusedRuntimeId = getFocusedRuntimeId ?? DefaultGetFocusedRuntimeId;
    }

    public void WarmUp() => EnsureUia();

    public DestinationSnapshot? Capture()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        uint tid = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        int[]? runtimeId = _getFocusedRuntimeId();
        return new DestinationSnapshot(hwnd, pid, tid, GetFocus(tid), runtimeId);
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

        if (s.FocusRuntimeId is { Length: > 0 })
        {
            int[]? current = _getFocusedRuntimeId();
            if (current is null || !s.FocusRuntimeId.SequenceEqual(current))
                return DestinationStatus.FocusChanged;
        }

        return DestinationStatus.Valid;
    }

    private static IntPtr GetFocus(uint threadId)
    {
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        return NativeMethods.GetGUIThreadInfo(threadId, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    private static IUIAutomation? EnsureUia()
    {
        if (_uia is not null) return _uia;
        if (_uiaInitFailed) return null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidCUIAutomation);
            if (type is not null)
                _uia = (IUIAutomation)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            _uiaInitFailed = true;
            Logger.Error("Could not initialize UI Automation", ex);
        }
        return _uia;
    }

    private static int[]? DefaultGetFocusedRuntimeId()
    {
        var uia = EnsureUia();
        if (uia is null) return null;
        try
        {
            var task = Task.Run(() =>
            {
                if (uia.GetFocusedElement(out IntPtr el) != 0 || el == IntPtr.Zero) return null;
                try
                {
                    var elem = (IUIAutomationElement)Marshal.GetObjectForIUnknown(el);
                    return elem.GetRuntimeId(out int[] id) == 0 ? id : null;
                }
                finally
                {
                    Marshal.Release(el);
                }
            });
            return task.Wait(150) ? task.Result : null;
        }
        catch
        {
            return null;
        }
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
