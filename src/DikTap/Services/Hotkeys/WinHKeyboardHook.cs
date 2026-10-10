using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DikTap.Interop;
using DikTap.Services.Paste;

namespace DikTap.Services.Hotkeys;

internal interface IToggleHotkey : IDisposable
{
    string Description { get; }
    event Action? Pressed;
}

// Intercepts Win+H with WH_KEYBOARD_LL while the application is running. The callback only updates
// the key state and queues the command on the UI thread; nothing heavy happens inside the hook.
// Windows silently removes a hook whose callback takes too long: hence the thread has high priority, the path
// is compiled before the first keypress, the maximum duration is measured and a self-test exists (SelfTestAsync).
internal sealed class WinHKeyboardHook : IToggleHotkey
{
    private readonly WinHShortcutFilter _filter = new(vk => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0);
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    // The delegate is kept in a field so the GC does not collect it while the hook is installed.
    private readonly LowLevelKeyboardProc _proc;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _hook;
    private uint _threadId;
    private Exception? _startError;
    private TaskCompletionSource? _beat;
    private long _events;
    private long _maxCallbackTicks;

    public string Description => "Win+H";
    public event Action? Pressed;

    // Keyboard events seen by the hook and maximum duration of its callback (diagnostics).
    public long EventsSeen => Interlocked.Read(ref _events);
    public double MaxCallbackMilliseconds => Interlocked.Read(ref _maxCallbackTicks) * 1000.0 / Stopwatch.Frequency;
    public bool IsAlive => _hook != IntPtr.Zero && _thread.IsAlive;

    private WinHKeyboardHook()
    {
        _proc = HookProc;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Dictation.WinHHook", Priority = ThreadPriority.Highest };
    }

    public static WinHKeyboardHook Start()
    {
        var h = new WinHKeyboardHook();
        h.WarmUp();
        h._thread.Start();
        h._ready.Wait();
        if (h._startError is not null)
        {
            h._thread.Join();
            throw h._startError;
        }
        return h;
    }

    // Precompiles the callback path so the first real keypress does not pay for JIT.
    private void WarmUp()
    {
        RuntimeHelpers.PrepareDelegate(_proc);
        var f = new WinHShortcutFilter(_ => false);
        f.OnKey(NativeMethods.VK_LWIN, true, false);
        f.OnKey(NativeMethods.VK_H, true, false);
        f.OnKey(NativeMethods.VK_H, false, false);
        f.OnKey(NativeMethods.VK_LWIN, false, false);
        Stopwatch.GetTimestamp();
    }

    private void Loop()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) _startError = new Win32Exception(Marshal.GetLastWin32Error());
        _ready.Set();
        if (_hook == IntPtr.Zero) return;

        while (NativeMethods.GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private unsafe IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        long t0 = Stopwatch.GetTimestamp();
        bool swallow = false;
        try
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                bool down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                bool up = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
                if (down || up)
                {
                    Interlocked.Increment(ref _events);
                    var k = *(KBDLLHOOKSTRUCT*)lParam;
                    bool own = k.dwExtraInfo == SendInputKeyboardInjector.Marker;
                    if (own && k.vkCode == NativeMethods.VK_UNASSIGNED_MASK) Volatile.Read(ref _beat)?.TrySetResult();
                    var d = _filter.OnKey((ushort)k.vkCode, down, own);
                    if (d.InjectMask) InjectMask();
                    if (d.Toggle) _dispatcher.InvokeAsync(() => Pressed?.Invoke());
                    swallow = d.Swallow;
                }
            }
        }
        catch (Exception)
        {
            // An exception must not cross into native code; when in doubt, the key goes on its way.
            swallow = false;
        }

        long dt = Stopwatch.GetTimestamp() - t0;
        if (dt > Volatile.Read(ref _maxCallbackTicks)) Interlocked.Exchange(ref _maxCallbackTicks, dt);
        return swallow ? (IntPtr)1 : NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void InjectMask() => SendMarkerKey();

    // Unassigned key (0xE8) with our own marker: masks the Start menu and serves as a heartbeat for the self-test.
    private static void SendMarkerKey()
    {
        var inputs = new[]
        {
            SendInputKeyboardInjector.Key(NativeMethods.VK_UNASSIGNED_MASK, false),
            SendInputKeyboardInjector.Key(NativeMethods.VK_UNASSIGNED_MASK, true),
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // true if the hook is still receiving events. Windows does not notify when it removes a hook, so it is checked by sending
    // our own invisible heartbeat (not called periodically: only after system or session events).
    public async Task<bool> SelfTestAsync(int timeoutMs = 500)
    {
        if (!IsAlive) return false;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _beat, tcs);
        try
        {
            SendMarkerKey();
            return await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(true) == tcs.Task;
        }
        finally
        {
            Volatile.Write(ref _beat, null);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0 && _thread.IsAlive)
        {
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        _ready.Dispose();
        Pressed = null;
    }
}
