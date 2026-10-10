using System.ComponentModel;
using System.Runtime.InteropServices;
using DikTap.Interop;

namespace DikTap.Services.Hotkeys;

// Hidden top-level window (not message-only: it must receive TaskbarCreated) that centralizes WM_HOTKEY,
// the tray icon and session/power events. Pure Win32: loads neither WPF nor GPU drivers while idle.
internal sealed class HostWindow : IDisposable
{
    public const int TrayCallbackMessage = NativeMethods.WM_USER + 1;
    private const string ClassName = "DikTap.Host";

    private readonly WndProcDelegate _proc;
    private readonly IntPtr _instance = NativeMethods.GetModuleHandleW(null);
    private IntPtr _hwnd;
    private bool _classRegistered;
    private bool _sessionNotifications;

    public IntPtr Handle => _hwnd;
    public uint TaskbarCreatedMessage { get; } = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

    public event Action<int>? HotkeyPressed;
    public event Action<int>? TrayMessage;
    public event Action? TaskbarCreated;
    public event Action? SessionLocked;
    public event Action? Suspending;
    // Session unlock or resume from suspend: moments when a keyboard hook may have been lost.
    public event Action? Resumed;
    // Change in Windows settings or theme (e.g. taskbar light/dark theme switch).
    public event Action? SettingChanged;
    // WM_CLOSE (e.g. taskkill without /f): orderly shutdown.
    public event Action? CloseRequested;
    // Windows session end (logoff/shutdown).
    public event Action? SessionEnding;

    public HostWindow()
    {
        // The delegate is kept in a field: the GC must not collect it while the window class exists.
        _proc = WndProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = _proc,
            hInstance = _instance,
            lpszClassName = ClassName,
        };
        if (NativeMethods.RegisterClassExW(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _classRegistered = true;

        _hwnd = NativeMethods.CreateWindowExW((uint)NativeMethods.WS_EX_TOOLWINDOW, ClassName, AppInfo.Name + " Host", NativeMethods.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _sessionNotifications = NativeMethods.WTSRegisterSessionNotification(_hwnd, 0); // NOTIFY_FOR_THIS_SESSION
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case NativeMethods.WM_HOTKEY:
                    HotkeyPressed?.Invoke((int)wParam);
                    return IntPtr.Zero;
                case HostWindow.TrayCallbackMessage:
                    TrayMessage?.Invoke((int)((long)lParam & 0xFFFF));
                    return IntPtr.Zero;
                case NativeMethods.WM_WTSSESSION_CHANGE when (int)wParam == NativeMethods.WTS_SESSION_LOCK:
                    SessionLocked?.Invoke();
                    return IntPtr.Zero;
                case NativeMethods.WM_WTSSESSION_CHANGE when (int)wParam == NativeMethods.WTS_SESSION_UNLOCK:
                    Resumed?.Invoke();
                    return IntPtr.Zero;
                case NativeMethods.WM_POWERBROADCAST when (int)wParam == NativeMethods.PBT_APMSUSPEND:
                    Suspending?.Invoke();
                    return new IntPtr(1);
                case NativeMethods.WM_POWERBROADCAST when (int)wParam is NativeMethods.PBT_APMRESUMESUSPEND or NativeMethods.PBT_APMRESUMEAUTOMATIC:
                    Resumed?.Invoke();
                    return new IntPtr(1);
                case NativeMethods.WM_SETTINGCHANGE:
                    SettingChanged?.Invoke();
                    return IntPtr.Zero;
                case NativeMethods.WM_QUERYENDSESSION:
                    return new IntPtr(1);
                case NativeMethods.WM_ENDSESSION when wParam != IntPtr.Zero:
                    SessionEnding?.Invoke();
                    return IntPtr.Zero;
                case NativeMethods.WM_CLOSE:
                    CloseRequested?.Invoke();
                    return IntPtr.Zero;
            }
            if (msg == TaskbarCreatedMessage) TaskbarCreated?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error("Exception in the host window", ex);
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_sessionNotifications && _hwnd != IntPtr.Zero)
        {
            NativeMethods.WTSUnRegisterSessionNotification(_hwnd);
            _sessionNotifications = false;
        }
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_classRegistered)
        {
            NativeMethods.UnregisterClassW(ClassName, _instance);
            _classRegistered = false;
        }
    }
}
