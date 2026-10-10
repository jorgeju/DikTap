using DikTap.Interop;

namespace DikTap.Services.Hotkeys;

internal readonly record struct KeyDecision(bool Swallow, bool Toggle, bool InjectMask)
{
    public static readonly KeyDecision Pass = new(false, false, false);
}

// Pure Win+H logic, separated from the hook so it can be verified without a desktop.
// Rules: only Win(left/right)+H without Shift/Ctrl/Alt. One toggle per physical press (auto-repeat is swallowed).
// The H keyup is also swallowed and, so Windows does not open Start when Win is released, an
// unassigned key (VK 0xE8) is injected once per Win press.
internal sealed class WinHShortcutFilter
{
    private static readonly ushort[] Tracked =
    {
        NativeMethods.VK_LWIN, NativeMethods.VK_RWIN,
        NativeMethods.VK_LSHIFT, NativeMethods.VK_RSHIFT,
        NativeMethods.VK_LCONTROL, NativeMethods.VK_RCONTROL,
        NativeMethods.VK_LMENU, NativeMethods.VK_RMENU,
    };

    private readonly bool[] _down = new bool[Tracked.Length];
    // Reconciliation with the asynchronous key state: only for keys other than that of the current event.
    private readonly Func<ushort, bool>? _asyncProbe;
    private bool _hHandled;
    private bool _maskInjected;

    public WinHShortcutFilter(Func<ushort, bool>? asyncProbe = null) => _asyncProbe = asyncProbe;

    public KeyDecision OnKey(ushort vk, bool isDown, bool ownInjected)
    {
        // Events injected by this application (Ctrl+V, mask key): neither recorded nor swallowed.
        if (ownInjected) return KeyDecision.Pass;

        int idx = Array.IndexOf(Tracked, vk);
        if (idx >= 0)
        {
            _down[idx] = isDown;
            if (!AnyWin()) _maskInjected = false;
            return KeyDecision.Pass;
        }

        if (vk != NativeMethods.VK_H) return KeyDecision.Pass;

        if (!isDown)
        {
            if (!_hHandled) return KeyDecision.Pass;
            _hHandled = false;
            return new KeyDecision(true, false, false);
        }

        if (_hHandled) return new KeyDecision(true, false, false); // auto-repeat

        Reconcile();
        if (!AnyWin() || AnyOf(2, 3) || AnyOf(4, 5) || AnyOf(6, 7)) return KeyDecision.Pass;

        _hHandled = true;
        bool mask = !_maskInjected;
        _maskInjected = true;
        return new KeyDecision(true, true, mask);
    }

    private bool AnyWin() => _down[0] || _down[1];
    private bool AnyOf(int a, int b) => _down[a] || _down[b];

    private void Reconcile()
    {
        if (_asyncProbe is null) return;
        for (int i = 0; i < Tracked.Length; i++) _down[i] = _asyncProbe(Tracked[i]);
    }
}
