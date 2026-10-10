using DikTap.Localization;
using DikTap.Services.Audio;
using DikTap.Services.Credentials;
using DikTap.Services.Hotkeys;
using DikTap.Services.Paste;
using DikTap.Services.Settings;
using DikTap.Services.Startup;
using DikTap.Services.Transcription;
using DikTap.Services.Uninstall;
using DikTap.Services.Usage;
using DikTap.Views;

namespace DikTap;

// Composition root: creates the services, wires hotkeys/tray/controller and handles shutdown.
internal sealed class AppCoordinator : ISettingsHost, IDisposable
{
    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private readonly ISettingsStore _store = new JsonSettingsStore(AppPaths.SettingsFile);
    private readonly IUsageStore _usage = new JsonlUsageStore(AppPaths.UsageFile);
    private readonly ICredentialStore _credentials = new WindowsCredentialStore(AppInfo.CredentialTarget);
    private readonly IStartupRegistration _startup;
    private readonly WasapiRecorder _recorder = new();
    private readonly HostWindow _host = new();
    private readonly HotkeyManager _hotkeys;
    private readonly OverlayPresenter _overlay;
    private readonly IPasteService _paste;
    private readonly DictationController _controller;
    private readonly TrayIcon _tray;

    private AppSettings _settings;
    private HotkeyApplyResult _hotkey = new(HotkeyMode.CtrlAltSpace, false, false, null);
    private SettingsWindow? _settingsWindow;
    private bool _exiting;
    // Runs once the window, tray and hotkeys have been released, right before the message loop ends.
    private Action? _afterExit;

    public AppCoordinator(string exePath)
    {
        _startup = new RunKeyStartupRegistration(exePath);
        _settings = _store.Load();
        Loc.Configure(_settings.Language);
        _overlay = new OverlayPresenter(_dispatcher);
        _hotkeys = new HotkeyManager(_host);
        _paste = new PasteService(new Win32Clipboard(() => _host.Handle), new SendInputKeyboardInjector(), new Win32DestinationService());

        var http = new Lazy<System.Net.Http.HttpClient>(OpenAITranscriptionClient.CreateHttpClient);
        _controller = new DictationController(
            _recorder,
            new OpenAITranscriptionClient(() => http.Value),
            _paste,
            new Win32DestinationService(),
            _credentials,
            _overlay,
            _hotkeys,
            () => _settings,
            _usage);

        _hotkeys.TogglePressed += _controller.Toggle;
        _hotkeys.EscapePressed += () => _controller.Cancel(CancelReason.User);
        _host.SessionLocked += () => _controller.Cancel(CancelReason.SessionInterrupted);
        _host.Suspending += () => _controller.Cancel(CancelReason.SessionInterrupted);
        _host.CloseRequested += Exit;
        _host.Resumed += () => _ = _hotkeys.EnsureHealthyAsync();
        _hotkeys.Notice += m => OnUi(() => _overlay.ShowError(m));
        _controller.StateChanged += s =>
        {
            OnUi(UpdateTooltip);
            if (s == SessionState.Idle) OnUi(() => _ = _hotkeys.EnsureHealthyAsync());
        };
        _host.SessionEnding += () => _controller.Cancel(CancelReason.Shutdown);

        _tray = new TrayIcon(_host, BuildMenu, ShowSettings);
    }

    public void Start()
    {
        _hotkey = _hotkeys.Apply(_settings.HotkeyMode);
        if (_hotkey.Warning is { } w) _overlay.ShowError(w);
        UpdateTooltip();
        Logger.Info($"Ready (shortcut={_hotkey.ActiveMode}, backup={_hotkey.BackupRegistered}, active={_hotkey.IsActive})");
        WarmUp();

        if (_settings.StartWithWindows && _startup.GetState() == StartupState.NotRegistered)
        {
            try { _startup.Enable(); }
            catch (Exception ex) { Logger.Error("Could not enable startup registration at launch", ex); }
        }

        bool firstRun = !_settings.FirstRunCompleted;
        if (firstRun || !HasApiKey) ShowSettings(firstRun);
    }

    private void OnUi(Action a)
    {
        if (_dispatcher.CheckAccess()) a();
        else _dispatcher.InvokeAsync(a);
    }

    private void WarmUp()
    {
        _overlay.WarmUp();
        // Loads the COM audio stack (without opening the microphone) so the first capture is as fast as the rest.
        _ = Task.Run(async () =>
        {
            try { await _recorder.ListCaptureDevicesAsync(); }
            catch (Exception) { }
        });
    }

    private void UpdateTooltip()
    {
        string state = _controller.State switch
        {
            SessionState.Idle => Str.TipReady(_hotkey.Description),
            SessionState.Starting or SessionState.Recording or SessionState.Stopping => Str.TipRecording,
            SessionState.Transcribing => Str.TipTranscribing,
            _ => Str.TipWorking,
        };
        _tray.Tooltip = $"{AppInfo.Name} · {state}";
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        _ = _hotkeys.EnsureHealthyAsync();
        return new[]
        {
            new TrayMenuItem(Str.TrayShortcut(_hotkey.Description, _hotkey.IsActive), Enabled: false),
            TrayMenuItem.Divider,
            new TrayMenuItem(Str.TraySettings, () => ShowSettings()),
            new TrayMenuItem(Str.TrayCopyLast, CopyLast, Enabled: _controller.LastDictation is not null),
            new TrayMenuItem(Str.TrayStartup, ToggleStartup, Checked: _startup.GetState() == StartupState.Enabled),
            TrayMenuItem.Divider,
            new TrayMenuItem(Str.TrayExit, Exit),
        };
    }

    private void ShowSettings() => ShowSettings(!_settings.FirstRunCompleted);

    private void ShowSettings(bool firstRun)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        // WPF is only loaded when settings open; software rendering avoids initializing the GPU.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var w = new SettingsWindow(this, firstRun);
        w.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = w;
        w.Show();
        w.Activate();
    }

    // Invoked from another instance (second launch) through the named event.
    public void ShowSettingsFromAnotherInstance() => OnUi(ShowSettings);

    private async void CopyLast()
    {
        if (_controller.LastDictation is not { } text) return;
        bool ok = await _paste.CopyTextAsync(text);
        if (ok) _overlay.ShowInfo(Str.LastCopied);
        else _overlay.ShowError(Str.ClipboardBusyRetry);
    }

    private void ToggleStartup()
    {
        try
        {
            if (_startup.GetState() == StartupState.Enabled) _startup.Disable();
            else _startup.Enable();
        }
        catch (Exception ex)
        {
            Logger.Error("Could not change startup registration", ex);
            _overlay.ShowError(ex is DictationException ? ex.Message : Str.StartupToggleFailed);
        }
    }

    private async void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        // Leave the window procedure before destroying the host window.
        await Task.Yield();
        await _controller.ShutdownAsync(TimeSpan.FromSeconds(3));
        // Let a pending clipboard restore finish so the previous contents are not lost.
        await Task.WhenAny(_paste.FlushPendingRestoreAsync(), Task.Delay(1500));
        Dispose();
        _afterExit?.Invoke();
        _dispatcher.InvokeShutdown();
    }

    // ---- ISettingsHost ----

    public AppSettings Settings => _settings;
    public IUsageStore Usage => _usage;
    public bool HasApiKey => _credentials.HasKey();
    public HotkeyApplyResult CurrentHotkey => _hotkey;
    public StartupState StartupState => _startup.GetState();
    public Task<IReadOnlyList<AudioDeviceInfo>> ListMicrophonesAsync() => _recorder.ListCaptureDevicesAsync();
    public void SaveApiKey(string apiKey) => _credentials.Save(apiKey);
    public void DeleteApiKey() => _credentials.Delete();

    public string? Uninstall(bool keepSettings)
    {
        var paths = SelfUninstaller.CurrentPaths(Environment.ProcessPath ?? string.Empty);
        if (SelfUninstaller.BuildCleanupCommand(paths, keepSettings) is null)
            return Str.UninstallPathUnsafe;

        try
        {
            _credentials.Delete();
            _startup.Disable();
        }
        catch (Exception ex)
        {
            Logger.Error("Uninstall: could not remove the key or startup registration", ex);
            return Str.UninstallKeyFailed;
        }

        Logger.Info($"Uninstall started (keep settings: {keepSettings})");
        _afterExit = () =>
        {
            SelfUninstaller.RemoveWindowsTraces(paths.ExePath);
            SelfUninstaller.LaunchCleanup(paths, keepSettings);
        };
        Exit();
        return null;
    }

    public HotkeyApplyResult Apply(AppSettings settings, bool startWithWindows)
    {
        var toSave = settings with { FirstRunCompleted = _settings.FirstRunCompleted || HasApiKey };
        bool hotkeyChanged = toSave.HotkeyMode != _settings.HotkeyMode || !_hotkey.IsActive || _hotkey.ActiveMode != toSave.HotkeyMode;
        _store.Save(toSave);
        _settings = toSave;
        Loc.Configure(toSave.Language);

        if (hotkeyChanged)
        {
            _hotkey = _hotkeys.Apply(toSave.HotkeyMode);
            if (_hotkey.Warning is { } w && !_hotkey.IsActive) _overlay.ShowError(w);
        }

        var state = _startup.GetState();
        if (startWithWindows && state != StartupState.Enabled) _startup.Enable();
        else if (!startWithWindows && state is StartupState.Enabled or StartupState.StalePath) _startup.Disable();

        UpdateTooltip();
        return _hotkey;
    }

    public void Dispose()
    {
        _settingsWindow?.Close();
        _tray.Dispose();
        _hotkeys.Dispose();
        _overlay.Close();
        _host.Dispose();
    }
}
