using System.ComponentModel;
using System.Runtime.InteropServices;
using DikTap.Interop;
using DikTap.Localization;
using DikTap.Services.Settings;

namespace DikTap.Services.Hotkeys;

// The shortcut text is computed when displayed, so it follows the current language without reapplying the shortcut.
internal sealed record HotkeyApplyResult(HotkeyMode ActiveMode, bool BackupRegistered, bool IsActive, string? Warning)
{
    public string Description => Str.HotkeyDescription(ActiveMode == HotkeyMode.WinH, BackupRegistered);
}

// Toggle shortcut (Win+H via hook or Ctrl+Alt+Space via RegisterHotKey) and Escape only during the session.
internal sealed class HotkeyManager : IEscapeKey, IDisposable
{
    private const int ToggleId = 0x4401;
    private const int EscapeId = 0x4402;
    private const int CopyLastId = 0x4403;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private readonly HostWindow _host;
    private IToggleHotkey? _hook;
    private bool _toggleRegistered;
    private bool _copyLastRegistered;
    private bool _escapeArmed;
    private bool _healing;

    public event Action? TogglePressed;
    public event Action? EscapePressed;
    public event Action? CopyLastPressed;
    // User notices (e.g. the hook could not be recovered).
    public event Action<string>? Notice;

    public HotkeyManager(HostWindow host)
    {
        _host = host;
        _host.HotkeyPressed += OnHotkey;
    }

    private void OnHotkey(int id)
    {
        if (id == ToggleId) TogglePressed?.Invoke();
        else if (id == EscapeId) EscapePressed?.Invoke();
        else if (id == CopyLastId) CopyLastPressed?.Invoke();
    }

    public HotkeyApplyResult Apply(HotkeyMode mode)
    {
        Release();
        string? warning = null;

        if (mode == HotkeyMode.WinH)
        {
            try
            {
                var hook = WinHKeyboardHook.Start();
                hook.Pressed += () => TogglePressed?.Invoke();
                _hook = hook;
                // Fallback: if the hook stops receiving events, Ctrl+Alt+Space keeps working (if it is free).
                _toggleRegistered = NativeMethods.RegisterHotKey(_host.Handle, ToggleId,
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_SPACE);
                Logger.Info($"Shortcut active: Win+H (low-level keyboard hook); Ctrl+Alt+Space fallback: {(_toggleRegistered ? "yes" : "no")}");
                EnsureCopyLastRegistered();
                return new HotkeyApplyResult(HotkeyMode.WinH, _toggleRegistered, true, null);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ThreadStartException)
            {
                Logger.Error("Could not install the Win+H hook", ex);
                warning = Str.WinHFailed;
            }
        }

        _toggleRegistered = NativeMethods.RegisterHotKey(_host.Handle, ToggleId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_SPACE);
        EnsureCopyLastRegistered();
        if (_toggleRegistered)
        {
            Logger.Info("Shortcut active: Ctrl+Alt+Space");
            return new HotkeyApplyResult(HotkeyMode.CtrlAltSpace, false, true, warning);
        }

        int err = Marshal.GetLastWin32Error();
        Logger.Info($"RegisterHotKey failed (error {err})");
        string reason = err == ERROR_HOTKEY_ALREADY_REGISTERED ? Str.CtrlAltSpaceInUse : Str.CtrlAltSpaceFailed(err);
        return new HotkeyApplyResult(HotkeyMode.CtrlAltSpace, false, false, warning is null ? reason : $"{warning} {reason}");
    }

    // Windows silently removes a hook that is slow to respond. It is checked after system events or at the end of a
    // session (never periodically) and, if it does not respond, it is reinstalled.
    public async Task EnsureHealthyAsync()
    {
        if (_hook is not WinHKeyboardHook h || _healing) return;
        _healing = true;
        try
        {
            if (await h.SelfTestAsync()) return;
            if (!ReferenceEquals(_hook, h)) return;
            Logger.Info($"The Win+H hook is not responding (events seen: {h.EventsSeen}); reinstalling");
            h.Dispose();
            _hook = null;
            try
            {
                var fresh = WinHKeyboardHook.Start();
                fresh.Pressed += () => TogglePressed?.Invoke();
                _hook = fresh;
                Logger.Info("Win+H hook reinstalled");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ThreadStartException)
            {
                Logger.Error("Could not reinstall the Win+H hook", ex);
                Notice?.Invoke(_toggleRegistered ? Str.WinHStopped : Str.WinHStoppedRestart);
            }
        }
        finally
        {
            _healing = false;
        }
    }

    private void EnsureCopyLastRegistered()
    {
        if (_copyLastRegistered) return;
        _copyLastRegistered = NativeMethods.RegisterHotKey(_host.Handle, CopyLastId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_C);
        if (_copyLastRegistered)
            Logger.Info("Copy last shortcut active: Ctrl+Alt+C");
        else
            Logger.Info($"Could not register Ctrl+Alt+C (error {Marshal.GetLastWin32Error()})");
    }

    private void Release()
    {
        if (_hook is WinHKeyboardHook h)
            Logger.Info($"Win+H hook: {h.EventsSeen} events, max callback {h.MaxCallbackMilliseconds:F2} ms");
        _hook?.Dispose();
        _hook = null;
        if (_toggleRegistered)
        {
            NativeMethods.UnregisterHotKey(_host.Handle, ToggleId);
            _toggleRegistered = false;
        }
    }

    public void Arm()
    {
        if (_escapeArmed) return;
        _escapeArmed = NativeMethods.RegisterHotKey(_host.Handle, EscapeId, NativeMethods.MOD_NOREPEAT, NativeMethods.VK_ESCAPE);
        if (!_escapeArmed) Logger.Info($"Could not register Escape (error {Marshal.GetLastWin32Error()})");
    }

    public void Disarm()
    {
        if (!_escapeArmed) return;
        NativeMethods.UnregisterHotKey(_host.Handle, EscapeId);
        _escapeArmed = false;
    }

    public void Dispose()
    {
        Disarm();
        if (_copyLastRegistered)
        {
            NativeMethods.UnregisterHotKey(_host.Handle, CopyLastId);
            _copyLastRegistered = false;
        }
        Release();
        _host.HotkeyPressed -= OnHotkey;
    }
}
