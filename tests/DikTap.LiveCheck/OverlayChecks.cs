using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using DikTap.Interop;
using DikTap.Views;

namespace DikTap.LiveCheck;

internal static class OverlayChecks
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);

    public static int Run(string outDir)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var win = Desktop.MakeInputWindow(out var box);
        IntPtr target = new WindowInteropHelper(win).Handle;
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "OK" : "FALLO")}] {what}");
            if (!ok) failures++;
        }

        var overlay = new OverlayWindow();
        var states = new (OverlayKind Kind, string Text, string File)[]
        {
            (OverlayKind.Recording, "Grabando", "overlay-recording"),
            (OverlayKind.Transcribing, "Transcribiendo…", "overlay-transcribing"),
            (OverlayKind.Error, "API key inválida o revocada (401). Actualízala en Configuración.", "overlay-error"),
            (OverlayKind.Info, "Cambiaste de ventana durante el dictado. Texto copiado; pega donde quieras.", "overlay-info"),
            (OverlayKind.Cancelled, "Cancelado", "overlay-cancelled"),
        };

        foreach (var (kind, text, file) in states)
        {
            overlay.Present(kind, text, target, null);
            Desktop.Pump(kind == OverlayKind.Recording ? 1700 : 500);
            IntPtr hwnd = overlay.Handle;
            NativeMethods.GetWindowRect(hwnd, out RECT r);
            Console.WriteLine($"{kind}: rect=({r.Left},{r.Top},{r.Right},{r.Bottom}) {r.Width}x{r.Height}");

            Check(NativeMethods.GetForegroundWindow() == target, "el overlay no roba el foco");
            Check(win.IsActive && box.IsKeyboardFocused, "el campo conserva el foco de teclado");
            long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            Check((ex & NativeMethods.WS_EX_NOACTIVATE) != 0 && (ex & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (ex & NativeMethods.WS_EX_TRANSPARENT) != 0,
                "estilos NOACTIVATE|TOOLWINDOW|TRANSPARENT (sin Alt+Tab ni barra de tareas, transparente al ratón)");
            var center = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
            Check(WindowFromPoint(center) != hwnd, "los clics atraviesan el overlay");

            var mon = NativeMethods.MonitorFromWindow(target, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            NativeMethods.GetMonitorInfoW(mon, ref mi);
            bool inside = r.Left >= mi.rcWork.Left && r.Right <= mi.rcWork.Right && r.Bottom <= mi.rcWork.Bottom && r.Top >= mi.rcWork.Top;
            int centerDelta = Math.Abs((r.Left + r.Right) / 2 - (mi.rcWork.Left + mi.rcWork.Right) / 2);
            Check(inside && centerDelta <= 2, $"dentro del área útil, centrado abajo (desviación horizontal {centerDelta}px)");

            using var bmp = new Bitmap(r.Width + 40, r.Height + 40);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left - 20, r.Top - 20, 0, 0, bmp.Size);
            string path = Path.Combine(outDir, file + ".png");
            bmp.Save(path, ImageFormat.Png);
        }

        overlay.HideOverlay();
        Desktop.Pump(200);
        Check(!overlay.IsVisible, "se oculta");
        Console.WriteLine(failures == 0 ? "TODO OK" : failures + " fallos");
        overlay.Dispose();
        win.Close();
        return failures == 0 ? 0 : 1;
    }
}
