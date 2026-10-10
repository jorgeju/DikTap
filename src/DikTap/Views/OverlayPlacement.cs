using System.Text;
using DikTap.Interop;

namespace DikTap.Views;

// Overlay position. Start, Search, Notification Center and similar live in a z-order band above
// any normal window, even TOPMOST (only beaten by a UIAccess-signed executable, which is not the case):
// if the target is one of them, the overlay is placed outside its rectangle so it stays visible.
internal static class OverlayPlacement
{
    private static readonly string[] ShellProcesses = { "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost", "TextInputHost" };
    private static readonly string[] ExplorerHostedClasses = { "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow" };

    internal static bool IsShellSurface(string? processName, string? className)
    {
        if (processName is null) return false;
        if (ShellProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase)) return true;
        return string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase)
            && className is not null && ExplorerHostedClasses.Contains(className, StringComparer.Ordinal);
    }

    // Rectangle of the target window if it is a shell surface; null otherwise.
    public static RECT? ShellRect(IntPtr window)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window)) return null;
        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint pid);
            if (pid == 0) return null;
            var cls = new StringBuilder(256);
            NativeMethods.GetClassNameW(window, cls, cls.Capacity);
            if (!IsShellSurface(ImageName(pid), cls.ToString())) return null;
            return NativeMethods.GetWindowRect(window, out RECT r) ? r : null;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not check whether the destination is a shell window", ex);
            return null;
        }
    }

    private static string? ImageName(uint pid)
    {
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(520);
            uint size = (uint)path.Capacity;
            return NativeMethods.QueryFullProcessImageNameW(process, 0, path, ref size) ? Path.GetFileNameWithoutExtension(path.ToString()) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    // Detects the active foreground window, or the reference one, if it is a shell surface
    // or a TOPMOST window that could overlap the overlay.
    public static RECT? GetCurrentObstacle(IntPtr referenceWindow, IntPtr overlayHwnd)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != overlayHwnd)
        {
            if (ShellRect(fg) is { } sr) return sr;

            if (NativeMethods.IsWindowVisible(fg))
            {
                long ex = NativeMethods.GetWindowLongPtr(fg, NativeMethods.GWL_EXSTYLE).ToInt64();
                if ((ex & NativeMethods.WS_EX_TOPMOST) != 0)
                {
                    if (NativeMethods.GetWindowRect(fg, out RECT r) && r.Width > 0 && r.Height > 0)
                    {
                        return r;
                    }
                }
            }
        }

        if (referenceWindow != IntPtr.Zero && referenceWindow != fg)
        {
            if (ShellRect(referenceWindow) is { } rr) return rr;
        }

        return null;
    }

    // Bottom and centered in the work area by default.
    // If it collides with an obstacle (shell surface or TOPMOST window), tries in priority order:
    // 1. Just above the obstacle
    // 2. Bottom-right corner
    // 3. Top-right corner
    // 4. Bottom-left corner
    // 5. Top-left corner
    // 6. Top center
    // 7. Free gaps on the sides of the obstacle
    // 8. Fallback: stay at the default position, covered
    public static (int X, int Y) Compute(RECT work, int width, int height, int margin, RECT? obstacle)
    {
        int defX = work.Left + (work.Width - width) / 2;
        int defY = work.Bottom - height - margin;

        defX = Math.Clamp(defX, work.Left, Math.Max(work.Left, work.Right - width));
        defY = Math.Clamp(defY, work.Top, Math.Max(work.Top, work.Bottom - height));

        if (obstacle is not { } s || !Intersects(defX, defY, width, height, s))
            return (defX, defY);

        // 1. Top: just above the obstacle (centered relative to the screen or the obstacle)
        int aboveY = s.Top - height - margin;
        if (IsFree(defX, aboveY, width, height, work, s))
            return (defX, aboveY);

        int sCenterX = s.Left + (s.Width - width) / 2;
        sCenterX = Math.Clamp(sCenterX, work.Left, Math.Max(work.Left, work.Right - width));
        if (IsFree(sCenterX, aboveY, width, height, work, s))
            return (sCenterX, aboveY);

        // 2. Bottom-right
        int brX = work.Right - width - margin;
        int brY = work.Bottom - height - margin;
        if (IsFree(brX, brY, width, height, work, s))
            return (brX, brY);

        // 3. Top-right
        int trX = work.Right - width - margin;
        int trY = work.Top + margin;
        if (IsFree(trX, trY, width, height, work, s))
            return (trX, trY);

        // 4. Bottom-left
        int blX = work.Left + margin;
        int blY = work.Bottom - height - margin;
        if (IsFree(blX, blY, width, height, work, s))
            return (blX, blY);

        // 5. Top-left
        int tlX = work.Left + margin;
        int tlY = work.Top + margin;
        if (IsFree(tlX, tlY, width, height, work, s))
            return (tlX, tlY);

        // 6. Top center
        int tcX = defX;
        int tcY = work.Top + margin;
        if (IsFree(tcX, tcY, width, height, work, s))
            return (tcX, tcY);

        // 7. Free gaps on the sides of the obstacle
        int rightX = s.Right + margin;
        if (IsFree(rightX, defY, width, height, work, s))
            return (rightX, defY);
        if (IsFree(rightX, work.Top + margin, width, height, work, s))
            return (rightX, work.Top + margin);

        int leftX = s.Left - width - margin;
        if (IsFree(leftX, defY, width, height, work, s))
            return (leftX, defY);
        if (IsFree(leftX, work.Top + margin, width, height, work, s))
            return (leftX, work.Top + margin);

        // 8. Fallback: stay at the default position, covered
        return (defX, defY);
    }

    private static bool IsFree(int x, int y, int width, int height, RECT work, RECT obstacle)
    {
        if (x < work.Left || x + width > work.Right || y < work.Top || y + height > work.Bottom)
            return false;

        return !Intersects(x, y, width, height, obstacle);
    }

    private static bool Intersects(int x, int y, int width, int height, RECT r)
        => x < r.Right && x + width > r.Left && y < r.Bottom && y + height > r.Top;
}
