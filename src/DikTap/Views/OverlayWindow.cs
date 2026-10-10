using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DikTap.Interop;
using DikTap.Localization;
using DikTap.Services.Paste;

namespace DikTap.Views;

internal enum OverlayKind
{
    Preparing,
    Recording,
    Transcribing,
    Cancelled,
    Error,
    Info,
}

// Floating overlay in Win32 + GDI+ (no WPF, to avoid loading the render engine and GPU drivers in a
// resident process): per-pixel layered window, borderless, non-activating, hidden from Alt+Tab/taskbar and mouse-transparent.
// Animations only run while it is visible.
internal sealed class OverlayWindow : IDisposable
{
    private const string ClassName = "DikTap.Overlay";
    private const int AnimTimer = 1, HideTimer = 2;
    // The Windows timer advances in 15.6 ms ticks: 30 ms gives ~32 fps; 33 ms would fall to 3 ticks (~21 fps) and 15 ms would give ~62 fps.
    private const uint AnimIntervalMs = 30;
    // Each bar summarizes 100 ms of audio (the peak); 30 bars = 3 s of history, ~50 px/s of scrolling.
    private const int WaveBars = 30;
    private const double WaveBarPitch = 5, WaveBarWidth = 3, WavePushMs = 100;
    // Visual level (0..1) above which bars are drawn; only affects the animation, not the capture.
    private const float WaveVoiceThreshold = 0.22f;
    private const double PositionCheckMs = 150;

    private static readonly Color Panel = Color.FromArgb(0xE6, 0x1E, 0x1F, 0x22);
    private static readonly Color White = Color.White;
    private static readonly Color Muted = Color.FromArgb(0xB8, 0xBC, 0xC4);
    private static readonly Color Red = Color.FromArgb(0xFF, 0x5A, 0x5F);
    private static readonly Color Amber = Color.FromArgb(0xFF, 0xC1, 0x07);
    private static readonly Color Green = Color.FromArgb(0x4C, 0xD9, 0x64);

    private readonly WndProcDelegate _proc;
    private readonly IntPtr _instance = NativeMethods.GetModuleHandleW(null);
    private readonly Stopwatch _clock = new();
    private IntPtr _hwnd;
    private bool _classRegistered;

    private OverlayKind _kind;
    private string _text = string.Empty;
    private double _scale = 1.0;
    private int _x, _y, _w, _h;
    private RECT _work;
    private IntPtr _referenceWindow;
    private Font? _labelFont, _timerFont;
    private float _textLeft, _textWidth, _timerLeft;

    // Waveform history: -1 = no audio yet (very faint dots), 0..1 = level of each slice.
    private readonly float[] _wave = new float[WaveBars];
    private float _wavePending;
    private double _wavePushedAt, _positionCheckedAt;
    private Func<float>? _levelSource;

    public IntPtr Handle => _hwnd;
    public bool IsVisible => _hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(_hwnd);

    public OverlayWindow()
    {
        _proc = WndProc;
    }

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero) return;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = _proc,
            hInstance = _instance,
            lpszClassName = ClassName,
        };
        if (NativeMethods.RegisterClassExW(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _classRegistered = true;

        uint ex = (uint)(NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST);
        _hwnd = NativeMethods.CreateWindowExW(ex, ClassName, AppInfo.Name + " Overlay", NativeMethods.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // Creates the (hidden) window and initializes GDI+ and fonts: the first keypress of the day does not pay that load.
    public void WarmUp()
    {
        EnsureWindow();
        using var bmp = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var font = new Font("Segoe UI Semibold", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var pen = new Pen(White, 2f);
        g.MeasureString("Grabando 0:00", font);
        g.DrawArc(pen, 1, 1, 6, 6, 0, 270);
        IntPtr h = bmp.GetHbitmap(Color.FromArgb(0));
        NativeMethods.DeleteObject(h);
    }

    public void Present(OverlayKind kind, string text, IntPtr referenceWindow, TimeSpan? autoHideAfter, Func<float>? levelSource = null)
    {
        EnsureWindow();
        StopTimers();
        DisposeFonts();

        _kind = kind;
        _text = text;
        _levelSource = levelSource;
        Array.Fill(_wave, -1f);
        _wavePending = 0;
        _wavePushedAt = 0;
        _positionCheckedAt = 0;
        _referenceWindow = referenceWindow;
        _work = ResolveMonitor(_referenceWindow, out _scale);
        _labelFont = new Font("Segoe UI Semibold", (float)(14 * _scale), FontStyle.Regular, GraphicsUnit.Pixel);
        _timerFont = new Font("Segoe UI", (float)(13 * _scale), FontStyle.Regular, GraphicsUnit.Pixel);
        Layout();

        UpdatePosition();

        _clock.Restart();
        Render();
        if (!NativeMethods.IsWindowVisible(_hwnd))
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, _x, _y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }

        if (kind is OverlayKind.Recording or OverlayKind.Transcribing or OverlayKind.Preparing)
            NativeMethods.SetTimer(_hwnd, (UIntPtr)AnimTimer, AnimIntervalMs, IntPtr.Zero);
        if (autoHideAfter is { } d)
            NativeMethods.SetTimer(_hwnd, (UIntPtr)HideTimer, (uint)d.TotalMilliseconds, IntPtr.Zero);
    }

    private void UpdatePosition()
    {
        var obstacle = OverlayPlacement.GetCurrentObstacle(_referenceWindow, _hwnd);
        var (newX, newY) = OverlayPlacement.Compute(_work, _w, _h, (int)(24 * _scale), obstacle);
        if (newX != _x || newY != _y)
        {
            _x = newX;
            _y = newY;
            if (_hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(_hwnd))
            {
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, _x, _y, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
        }
    }

    public void HideOverlay()
    {
        if (_hwnd == IntPtr.Zero) return;
        StopTimers();
        _clock.Stop();
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        DisposeFonts();
    }

    private void StopTimers()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.KillTimer(_hwnd, (UIntPtr)AnimTimer);
        NativeMethods.KillTimer(_hwnd, (UIntPtr)HideTimer);
    }

    private void DisposeFonts()
    {
        _labelFont?.Dispose();
        _timerFont?.Dispose();
        _labelFont = _timerFont = null;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case NativeMethods.WM_MOUSEACTIVATE:
                    return new IntPtr(NativeMethods.MA_NOACTIVATE);
                case NativeMethods.WM_NCHITTEST:
                    return new IntPtr(NativeMethods.HTTRANSPARENT);
                case NativeMethods.WM_TIMER:
                    if ((int)wParam == AnimTimer) Render();
                    else if ((int)wParam == HideTimer) HideOverlay();
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Exception in the overlay", ex);
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // Work area of the target window's monitor (or the cursor's) and its DPI scale.
    private static RECT ResolveMonitor(IntPtr reference, out double scale)
    {
        IntPtr monitor = reference != IntPtr.Zero && NativeMethods.IsWindow(reference)
            ? NativeMethods.MonitorFromWindow(reference, NativeMethods.MONITOR_DEFAULTTONEAREST)
            : IntPtr.Zero;
        if (monitor == IntPtr.Zero)
        {
            NativeMethods.GetCursorPos(out POINT pt);
            monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        }
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        NativeMethods.GetMonitorInfoW(monitor, ref info);
        scale = NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        return info.rcWork;
    }

    private void Layout()
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord };

        float padL = (float)(16 * _scale), padR = (float)(18 * _scale), padV = (float)(9 * _scale);
        float icon = (float)(24 * _scale), gap = (float)(10 * _scale);
        float maxText = (float)((_kind is OverlayKind.Error or OverlayKind.Info ? 520 : 400) * _scale);

        var size = g.MeasureString(_text, _labelFont!, new SizeF(maxText, 10000), fmt);
        _textWidth = (float)Math.Ceiling(size.Width);
        float textHeight = (float)Math.Ceiling(size.Height);
        if (_kind == OverlayKind.Recording)
        {
            // Instead of the text, the waveform: fixed width (the last bar has no gap behind it).
            _textWidth = (float)((WaveBars * WaveBarPitch - (WaveBarPitch - WaveBarWidth)) * _scale);
            textHeight = icon;
        }

        float timerWidth = 0;
        if (_kind == OverlayKind.Recording)
            timerWidth = gap + (float)Math.Ceiling(g.MeasureString("00:00", _timerFont!, 1000, fmt).Width);

        _textLeft = padL + icon + gap;
        _timerLeft = _textLeft + _textWidth + gap;
        _w = (int)Math.Ceiling(padL + icon + gap + _textWidth + timerWidth + padR);
        _h = (int)Math.Ceiling(Math.Max(icon, textHeight) + 2 * padV);
    }

    private void Render()
    {
        if (_hwnd == IntPtr.Zero || _labelFont is null || _timerFont is null) return;

        if (_kind is OverlayKind.Recording or OverlayKind.Preparing or OverlayKind.Transcribing)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if (now - _positionCheckedAt >= PositionCheckMs || now < _positionCheckedAt)
            {
                _positionCheckedAt = now;
                UpdatePosition();
            }
        }
        if (_kind == OverlayKind.Recording) AdvanceWave();

        using var bmp = new Bitmap(_w, _h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            float radius = Math.Min((float)(20 * _scale), _h / 2f);
            using (var path = RoundedRect(0, 0, _w, _h, radius))
            using (var fill = new SolidBrush(Panel))
                g.FillPath(fill, path);

            DrawIcon(g);

            using var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord, LineAlignment = StringAlignment.Center };
            if (_kind == OverlayKind.Recording)
            {
                DrawWave(g);
            }
            else
            {
                using var textBrush = new SolidBrush(White);
                g.DrawString(_text, _labelFont, textBrush, new RectangleF(_textLeft, 0, _textWidth + 2, _h), fmt);
            }

            if (_kind == OverlayKind.Recording)
            {
                var t = _clock.Elapsed;
                using var timerBrush = new SolidBrush(Muted);
                g.DrawString($"{(int)t.TotalMinutes}:{t.Seconds:00}", _timerFont, timerBrush, new RectangleF(_timerLeft, 0, _w - _timerLeft, _h), fmt);
            }
        }

        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr memDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        IntPtr old = NativeMethods.SelectObject(memDc, hBitmap);
        try
        {
            var dst = new POINT { X = _x, Y = _y };
            var size = new SIZE { cx = _w, cy = _h };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = NativeMethods.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = NativeMethods.AC_SRC_ALPHA };
            NativeMethods.UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.SelectObject(memDc, old);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.DeleteObject(hBitmap);
        }
    }

    // Takes this frame's level and, every WavePushMs, shifts the history one bar to the left.
    private void AdvanceWave()
    {
        if (_levelSource is not null) _wavePending = Math.Max(_wavePending, _levelSource());
        double now = _clock.Elapsed.TotalMilliseconds;
        while (now - _wavePushedAt >= WavePushMs)
        {
            Array.Copy(_wave, 1, _wave, 0, _wave.Length - 1);
            _wave[^1] = _wavePending;
            _wavePending = 0;
            _wavePushedAt += WavePushMs;
        }
    }

    // Thin rounded bars that enter from the right and scroll smoothly to the left.
    // No audio yet: nearly invisible dots; silence: gray dots; voice: white bars with height by level.
    private void DrawWave(Graphics g)
    {
        float s = (float)_scale;
        float pitch = (float)WaveBarPitch * s, bw = (float)WaveBarWidth * s;
        float maxH = _h - 2 * 9 * s;
        float cy = _h / 2f;
        float frac = (float)Math.Clamp((_clock.Elapsed.TotalMilliseconds - _wavePushedAt) / WavePushMs, 0, 1);

        using var dim = new Pen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), bw) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var idle = new Pen(Color.FromArgb(0x8C, 0xB8, 0xBC, 0xC4), bw) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var voice = new Pen(White, bw) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        var state = g.Save();
        g.SetClip(new RectangleF(_textLeft, 0, _textWidth, _h));
        for (int i = 0; i <= WaveBars; i++)
        {
            float v = i < WaveBars ? _wave[i] : _wavePending;
            float x = _textLeft + i * pitch - frac * pitch + bw / 2;
            if (x < _textLeft - bw || x > _textLeft + _textWidth + bw) continue;

            Pen pen;
            float half = 0; // half-height of the straight part (without the rounded ends)
            if (v < 0) pen = dim;
            else if (v < WaveVoiceThreshold) pen = idle;
            else
            {
                pen = voice;
                half = Math.Max(0, (bw + v * (maxH - bw) - bw) / 2);
            }
            g.DrawLine(pen, x, cy - half, x, cy + half + 0.01f);
        }
        g.Restore(state);
    }

    // Icons drawn on a 24-unit grid.
    private void DrawIcon(Graphics g)
    {
        float s = (float)_scale;
        float x = (float)(16 * _scale), y = (_h - 24 * s) / 2f;
        var state = g.Save();
        g.TranslateTransform(x, y);
        g.ScaleTransform(s, s);
        double ms = _clock.Elapsed.TotalMilliseconds;

        switch (_kind)
        {
            case OverlayKind.Recording:
            {
                float pulse = (float)(0.35 + 0.65 * (0.5 + 0.5 * Math.Cos(ms / 1600.0 * 2 * Math.PI)));
                var c = Color.FromArgb((int)(255 * pulse), Red);
                using var brush = new SolidBrush(c);
                using var pen = new Pen(c, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var body = new GraphicsPath();
                body.AddArc(9, 2, 6, 6, 180, 180);
                body.AddArc(9, 8, 6, 6, 0, 180);
                body.CloseFigure();
                g.FillPath(brush, body);
                g.DrawArc(pen, 6, 4.5f, 12, 12, 0, 180);
                g.DrawLine(pen, 12, 16.5f, 12, 21);
                break;
            }
            case OverlayKind.Transcribing:
            case OverlayKind.Preparing:
            {
                using var pen = new Pen(White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, 3, 3, 18, 18, (float)(ms / 900.0 * 360 % 360), 270);
                break;
            }
            case OverlayKind.Cancelled:
            {
                using var pen = new Pen(Muted, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pen, 7, 7, 17, 17);
                g.DrawLine(pen, 17, 7, 7, 17);
                break;
            }
            case OverlayKind.Error:
            {
                using var pen = new Pen(Amber, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var brush = new SolidBrush(Amber);
                g.DrawLine(pen, 12, 5.5f, 12, 14);
                g.FillEllipse(brush, 10.7f, 16.9f, 2.6f, 2.6f);
                break;
            }
            case OverlayKind.Info:
            {
                using var pen = new Pen(Green, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(pen, new[] { new PointF(6, 12.5f), new PointF(10.5f, 17), new PointF(18, 7.5f) });
                break;
            }
        }
        g.Restore(state);
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        float d = r * 2;
        var p = new GraphicsPath();
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        StopTimers();
        DisposeFonts();
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

// IOverlay adapter: can be called from any thread; the window is created on first use.
internal sealed class OverlayPresenter : IOverlay
{
    private readonly Dispatcher _dispatcher;
    private OverlayWindow? _window;
    private IntPtr _reference;
    private int _generation;

    // If capture starts in less than this, nothing intermediate is shown (avoids a flicker); if it takes longer, a loading indicator appears.
    private static readonly TimeSpan PreparingGrace = TimeSpan.FromMilliseconds(120);

    public OverlayPresenter(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void ShowPreparing(DestinationSnapshot? destination)
    {
        Post(() =>
        {
            _reference = destination?.Window ?? IntPtr.Zero;
            int gen = ++_generation;
            _window?.HideOverlay();
            _ = Task.Delay(PreparingGrace).ContinueWith(_ => Post(() =>
            {
                if (gen == _generation) Window().Present(OverlayKind.Preparing, Str.OverlayPreparing, _reference, null);
            }), TaskScheduler.Default);
        });
    }

    public void ShowRecording(DestinationSnapshot? destination, Func<float>? level = null)
        => Post(() => { _generation++; _reference = destination?.Window ?? IntPtr.Zero; Window().Present(OverlayKind.Recording, Str.OverlayRecording, _reference, null, level); });

    public void ShowTranscribing(string? note)
        => Post(() => { _generation++; Window().Present(OverlayKind.Transcribing, note is null ? Str.OverlayTranscribing : Str.OverlayTranscribingNote(note), _reference, null); });

    public void ShowCancelled()
        => Post(() => { _generation++; Window().Present(OverlayKind.Cancelled, Str.OverlayCancelled, _reference, TimeSpan.FromMilliseconds(1200)); });

    public void ShowError(string message, TimeSpan? duration = null)
        => Post(() => { _generation++; Window().Present(OverlayKind.Error, message, _reference, duration ?? TimeSpan.FromSeconds(5)); });

    public void ShowInfo(string message, TimeSpan? duration = null)
        => Post(() => { _generation++; Window().Present(OverlayKind.Info, message, _reference, duration ?? TimeSpan.FromSeconds(3.5)); });

    public void Hide() => Post(() => { _generation++; _window?.HideOverlay(); });

    // With idle priority, after startup finishes.
    public void WarmUp() => _dispatcher.InvokeAsync(() => Guard(() => Window().WarmUp()), DispatcherPriority.ApplicationIdle);

    private OverlayWindow Window() => _window ??= new OverlayWindow();

    private void Post(Action a)
    {
        if (_dispatcher.CheckAccess()) Guard(a);
        else _dispatcher.InvokeAsync(() => Guard(a));
    }

    // A drawing failure must not break the dictation flow.
    private static void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) { Logger.Error("Failed to show the overlay", ex); }
    }

    public void Close() => Post(() =>
    {
        _window?.Dispose();
        _window = null;
    });
}
