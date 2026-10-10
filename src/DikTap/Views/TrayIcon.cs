using System.Drawing;
using System.Reflection;
using Microsoft.Win32;
using DikTap.Interop;
using DikTap.Services.Hotkeys;

namespace DikTap.Views;

internal sealed record TrayMenuItem(string Text, Action? OnClick = null, bool Checked = false, bool Enabled = true, bool Separator = false)
{
    public static readonly TrayMenuItem Divider = new(string.Empty, Separator: true);
}

// Tray icon with Shell_NotifyIcon and native menu; no WinForms.
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;

    private readonly HostWindow _host;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private readonly Action _open;
    private IntPtr _icon;
    private bool _added;
    private string _tooltip = AppInfo.Name;

    public TrayIcon(HostWindow host, Func<IReadOnlyList<TrayMenuItem>> menuProvider, Action onOpen)
    {
        _host = host;
        _menu = menuProvider;
        _open = onOpen;
        _icon = LoadTrayIcon();
        _host.TrayMessage += OnTrayMessage;
        _host.TaskbarCreated += Add;
        _host.SettingChanged += OnSettingChanged;
        Add();
    }

    public string Tooltip
    {
        get => _tooltip;
        set
        {
            _tooltip = value.Length > 127 ? value[..127] : value;
            if (_added) Notify(NativeMethods.NIM_MODIFY);
        }
    }

    private void OnSettingChanged()
    {
        IntPtr old = _icon;
        _icon = LoadTrayIcon();
        if (_added) Notify(NativeMethods.NIM_MODIFY);
        if (old != IntPtr.Zero && old != _icon) NativeMethods.DestroyIcon(old);
    }

    private static bool IsColorTrayRequested()
    {
        string? env = Environment.GetEnvironmentVariable("DIKTAP_TRAY_COLOR");
        if (!string.IsNullOrEmpty(env))
        {
            return env == "1" || env.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
#if TRAY_MONOCHROME
        return false;
#else
        return true;
#endif
    }

    private static IntPtr LoadTrayIcon()
    {
        bool lightTaskbar = IsLightTaskbar();

        if (IsColorTrayRequested())
        {
            // On a light taskbar: the original purple (#6121db) has perfect contrast.
            // On a dark taskbar: the vibrant lavender variant (#b388ff) stands out with high contrast and personality.
            string colorRes = lightTaskbar ? "DikTap.Assets.app.ico" : "DikTap.Assets.tray-color-dark.ico";
            return LoadResourceIcon(colorRes);
        }

        // Monochrome mode: white on a dark background, dark on a light background.
        string resName = lightTaskbar ? "DikTap.Assets.tray-dark.ico" : "DikTap.Assets.tray-light.ico";
        return LoadResourceIcon(resName);
    }

    private static IntPtr LoadResourceIcon(string resName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName);
            if (stream is not null)
            {
                int cx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
                int cy = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSMICON);
                if (cx <= 0) cx = 16;
                if (cy <= 0) cy = 16;
                using var ico = new Icon(stream, new Size(cx, cy));
                return NativeMethods.CopyIcon(ico.Handle);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not load the embedded icon {resName}", ex);
        }
        return LoadAppIcon();
    }

    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("SystemUsesLightTheme") is int val) return val != 0;
        }
        catch { }
        return false;
    }

    private static IntPtr LoadAppIcon()
    {
        string? exe = Environment.ProcessPath;
        if (exe is not null && NativeMethods.ExtractIconExW(exe, 0, out IntPtr large, out IntPtr small, 1) > 0)
        {
            if (large != IntPtr.Zero) NativeMethods.DestroyIcon(large);
            if (small != IntPtr.Zero) return small;
        }
        // IDI_APPLICATION
        return NativeMethods.LoadIconW(IntPtr.Zero, new IntPtr(32512));
    }

    private void Add()
    {
        _added = Notify(NativeMethods.NIM_ADD);
    }

    private bool Notify(uint message)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _host.Handle,
            uID = IconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = HostWindow.TrayCallbackMessage,
            hIcon = _icon,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
        return NativeMethods.Shell_NotifyIconW(message, ref data);
    }

    private void OnTrayMessage(int mouseMessage)
    {
        switch (mouseMessage)
        {
            case NativeMethods.WM_LBUTTONDBLCLK:
                _open();
                break;
            case NativeMethods.WM_RBUTTONUP:
            case NativeMethods.WM_CONTEXTMENU:
                ShowMenu();
                break;
        }
    }

    private void ShowMenu()
    {
        try { NativeMethods.SetPreferredAppMode(1); } catch { }
        var items = _menu();
        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                uint flags = NativeMethods.MF_STRING;
                if (it.Separator) flags = NativeMethods.MF_SEPARATOR;
                if (it.Checked) flags |= NativeMethods.MF_CHECKED;
                if (!it.Enabled) flags |= NativeMethods.MF_GRAYED;
                NativeMethods.AppendMenuW(menu, flags, (UIntPtr)(uint)(i + 1), it.Separator ? null : it.Text);
            }
            NativeMethods.GetCursorPos(out POINT pt);
            // Required so the menu closes when clicking outside.
            NativeMethods.SetForegroundWindow(_host.Handle);
            int cmd = NativeMethods.TrackPopupMenuEx(menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY, pt.X, pt.Y, _host.Handle, IntPtr.Zero);
            NativeMethods.PostMessageW(_host.Handle, NativeMethods.WM_NULL, UIntPtr.Zero, IntPtr.Zero);
            if (cmd > 0 && cmd <= items.Count) items[cmd - 1].OnClick?.Invoke();
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        _host.TrayMessage -= OnTrayMessage;
        _host.TaskbarCreated -= Add;
        _host.SettingChanged -= OnSettingChanged;
        if (_added) Notify(NativeMethods.NIM_DELETE);
        _added = false;
        if (_icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }
}
