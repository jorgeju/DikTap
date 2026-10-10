using System.Runtime.InteropServices;
using DikTap.Interop;

namespace DikTap.Services.Paste;

internal interface IKeyboardInjector
{
    // true if the physical modifiers (Shift/Ctrl/Alt/Win) are released.
    bool ModifiersReleased();
    // true only if Windows accepted all 4 Ctrl+V events. Does not prove the control pasted.
    bool SendPaste();
}

internal sealed class SendInputKeyboardInjector : IKeyboardInjector
{
    // Marker of our own events so the Win+H hook ignores them.
    public static readonly UIntPtr Marker = new(0x4F414431);

    private static readonly ushort[] Modifiers =
    {
        NativeMethods.VK_SHIFT, NativeMethods.VK_CONTROL, NativeMethods.VK_MENU, NativeMethods.VK_LWIN, NativeMethods.VK_RWIN,
    };

    public bool ModifiersReleased()
    {
        foreach (ushort vk in Modifiers)
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0) return false;
        return true;
    }

    public bool SendPaste()
    {
        var inputs = new[]
        {
            Key(NativeMethods.VK_CONTROL, false),
            Key(NativeMethods.VK_V, false),
            Key(NativeMethods.VK_V, true),
            Key(NativeMethods.VK_CONTROL, true),
        };
        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            Logger.Info($"SendInput sent {sent}/{inputs.Length} events (error {Marshal.GetLastWin32Error()})");
            return false;
        }
        return true;
    }

    internal static INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0, dwExtraInfo = Marker },
    };
}
