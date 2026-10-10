using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using DikTap.Interop;
using DikTap.Services.Hotkeys;

namespace DikTap.LiveCheck;

internal static class Desktop
{
    // Runs an async test on the STA thread pumping the dispatcher (awaits return to this thread).
    public static T Await<T>(Func<Task<T>> test)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var task = test();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    public static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    public static void Key(ushort vk, bool up, bool extended = false)
    {
        var input = new INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? NativeMethods.KEYEVENTF_KEYUP : 0u) | (extended ? 1u : 0u) },
        };
        uint n = NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        if (n != 1) throw new InvalidOperationException("SendInput devolvió " + n);
        Pump(25);
    }

    public static string ForegroundProcess()
    {
        IntPtr h = NativeMethods.GetForegroundWindow();
        if (h == IntPtr.Zero) return "(ninguno)";
        NativeMethods.GetWindowThreadProcessId(h, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "pid " + pid; }
    }

    public static Window MakeInputWindow(out TextBox box)
    {
        box = new TextBox { FontSize = 20, AcceptsReturn = true };
        var w = new Window { Title = "LiveCheck destino", Width = 520, Height = 200, Content = box, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        w.Show();
        w.Activate();
        box.Focus();
        Pump(300);
        IntPtr hwnd = new WindowInteropHelper(w).Handle;
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            // A harmless keypress from the application itself grants the foreground right.
            Key(0xFF, false); Key(0xFF, true);
            NativeMethods.SetForegroundWindow(hwnd);
            w.Activate();
            box.Focus();
            Pump(300);
        }
        return w;
    }

    public static bool Foreground(Window w) => NativeMethods.GetForegroundWindow() == new WindowInteropHelper(w).Handle;
}

internal static class WinHChecks
{
    private static readonly HashSet<string> StartLike = new(StringComparer.OrdinalIgnoreCase) { "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost" };

    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var win = Desktop.MakeInputWindow(out var box);
        Console.WriteLine($"Ventana de prueba en primer plano: {Desktop.Foreground(win)} (proceso: {Desktop.ForegroundProcess()})");

        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "OK" : "FALLO")}] {what}");
            if (!ok) failures++;
        }

        using var hook = WinHKeyboardHook.Start();
        int pressed = 0;
        hook.Pressed += () => pressed++;
        Console.WriteLine("Hook instalado.");
        Check(Desktop.Await(() => hook.SelfTestAsync()), "autocomprobación del hook: responde al latido propio");

        void Combo(string name, ushort win, bool ext, int hRepeats, ushort? extra = null)
        {
            Desktop.Key(win, false, ext);
            if (extra is { } e) Desktop.Key(e, false);
            Desktop.Key(NativeMethods.VK_H, false);
            for (int i = 1; i < hRepeats; i++) Desktop.Key(NativeMethods.VK_H, false);
            Desktop.Key(NativeMethods.VK_H, true);
            if (extra is { } e2) Desktop.Key(e2, true);
            Desktop.Key(win, true, ext);
            Desktop.Pump(300);
            Console.WriteLine($"{name}: pressed={pressed}, texto='{box.Text}', primer plano={Desktop.ForegroundProcess()}");
        }

        Console.WriteLine("A) Win izquierdo + H");
        Combo("LWin+H", NativeMethods.VK_LWIN, true, 1);
        Check(pressed == 1, "un toggle");
        Check(box.Text == "", "no queda una H suelta en el campo");
        Check(Desktop.Foreground(win) && !StartLike.Contains(Desktop.ForegroundProcess()), "no se abrió Inicio ni el dictado de Windows");

        Console.WriteLine("B) Win derecho + H");
        Combo("RWin+H", NativeMethods.VK_RWIN, true, 1);
        Check(pressed == 2, "segundo toggle");
        Check(box.Text == "", "sin H suelta");
        Check(Desktop.Foreground(win) && !StartLike.Contains(Desktop.ForegroundProcess()), "no se abrió Inicio ni el dictado");

        Console.WriteLine("C) Autorepetición (H mantenida)");
        Combo("LWin+H x8", NativeMethods.VK_LWIN, true, 8);
        Check(pressed == 3, "la autorepetición produce un único toggle");
        Check(box.Text == "", "sin H suelta");
        Check(Desktop.Foreground(win) && !StartLike.Contains(Desktop.ForegroundProcess()), "sin Inicio/dictado");

        Console.WriteLine("D) Win+Shift+H no se intercepta");
        int before = pressed;
        Desktop.Key(NativeMethods.VK_LWIN, false, true);
        Desktop.Key(NativeMethods.VK_LSHIFT, false);
        Desktop.Key(NativeMethods.VK_H, false);
        Desktop.Key(NativeMethods.VK_H, true);
        Desktop.Key(NativeMethods.VK_LSHIFT, true);
        Desktop.Key(NativeMethods.VK_LWIN, true, true);
        Desktop.Pump(400);
        Console.WriteLine($"  primer plano tras Win+Shift+H: {Desktop.ForegroundProcess()}");
        Check(pressed == before, "Win+Shift+H no dispara el toggle");

        // Returns focus to the test window in case Windows reacted to the foreign key combination.
        win.Activate(); box.Focus(); Desktop.Pump(300);
        if (!Desktop.Foreground(win)) { Desktop.Key(0xFF, false); Desktop.Key(0xFF, true); NativeMethods.SetForegroundWindow(new WindowInteropHelper(win).Handle); Desktop.Pump(300); }

        Console.WriteLine("E) H sola y otras teclas siguen funcionando");
        box.Clear();
        before = pressed;
        Desktop.Key(NativeMethods.VK_H, false); Desktop.Key(NativeMethods.VK_H, true);
        Desktop.Key(0x41, false); Desktop.Key(0x41, true);
        Desktop.Pump(200);
        Check(pressed == before, "H sin Win no dispara");
        Check(box.Text.Equals("ha", StringComparison.OrdinalIgnoreCase), $"el texto escrito llega al campo ('{box.Text}')");

        Console.WriteLine("F) Control: Win solo abre Inicio (valida que la comprobación detecta el menú)");
        Desktop.Key(NativeMethods.VK_LWIN, false, true);
        Desktop.Key(NativeMethods.VK_LWIN, true, true);
        Desktop.Pump(900);
        string fg = Desktop.ForegroundProcess();
        Console.WriteLine($"  primer plano tras Win solo: {fg}");
        Check(StartLike.Contains(fg), "Win solo abre Inicio (la detección es sensible)");
        Desktop.Key(NativeMethods.VK_ESCAPE, false); Desktop.Key(NativeMethods.VK_ESCAPE, true);
        Desktop.Pump(400);

        Console.WriteLine($"Hook: {hook.EventsSeen} eventos vistos, callback máximo {hook.MaxCallbackMilliseconds:F3} ms");
        Check(hook.MaxCallbackMilliseconds < 50, "el callback del hook nunca superó 50 ms (el límite de Windows es ~300 ms)");
        Console.WriteLine($"Resultado: {(failures == 0 ? "TODO OK" : failures + " fallos")}");
        win.Close();
        return failures == 0 ? 0 : 1;
    }
}
