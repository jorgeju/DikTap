using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DikTap.Localization;
using DikTap.Services.Audio;
using DikTap.Services.Hotkeys;
using DikTap.Services.Settings;
using DikTap.Services.Startup;
using DikTap.Services.Uninstall;
using DikTap.Services.Update;
using DikTap.Services.Usage;

namespace DikTap.Views;

internal interface ISettingsHost
{
    AppSettings Settings { get; }
    IUsageStore Usage { get; }
    bool HasApiKey { get; }
    HotkeyApplyResult CurrentHotkey { get; }
    StartupState StartupState { get; }
    Task<IReadOnlyList<AudioDeviceInfo>> ListMicrophonesAsync();
    void SaveApiKey(string apiKey);
    void DeleteApiKey();
    // Persists, applies the hotkey and startup entry; returns the actual hotkey state.
    HotkeyApplyResult Apply(AppSettings settings, bool startWithWindows);
    // Removes the key and startup entry and closes the application; a helper process deletes the rest on exit. Returns an error or null.
    string? Uninstall(bool keepSettings);
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct);
    Task<StagedUpdate> DownloadUpdateAsync(UpdateCheckResult update, IProgress<double>? progress, CancellationToken ct);
    void ApplyUpdateAndRestart(StagedUpdate update);
}

// Settings window created on open and released on close; nothing is left rendering in the background.
internal sealed class SettingsWindow : Window
{
    private readonly ISettingsHost _host;
    private readonly bool _firstRun;
    private bool _isEditingKey;

    private readonly Border _keyCard = ModernTheme.CreateCard(new StackPanel());
    private readonly StackPanel _keyPanel = new();
    private readonly Border _firstRunBanner = new();
    private readonly TextBlock _firstRunText = new();
    private readonly TextBlock _keyStatus = new();
    private readonly Grid _keyRow = new();
    private readonly StackPanel _keyActionRow = new() { Orientation = Orientation.Horizontal };
    private readonly PasswordBox _keyBox = new() { MinWidth = 250 };
    private readonly Button _saveKey = new();
    private readonly Button _delKey = new();
    private readonly Button _changeKey = new();
    private readonly Button _cancelEditKey = new();

    private readonly ComboBox _mic = new() { MinWidth = 300 };
    private readonly ComboBox _mode = new() { MinWidth = 420 };
    private readonly TextBlock _hotkeyStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly TextBox _languages = new();
    private readonly TextBox _keywords = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 62, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _context = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 62, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    private readonly RadioButton _clipRestore = new() { GroupName = "ClipboardMode" };
    private readonly RadioButton _clipKeep = new() { GroupName = "ClipboardMode" };
    private readonly RadioButton _clipKeepHistory = new() { GroupName = "ClipboardMode" };

    private readonly ComboBox _language = new() { MinWidth = 300 };
    private readonly CheckBox _startup = new();
    private readonly TextBlock _startupNote = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray, FontSize = 11 };
    private readonly CheckBox _singleClickCopy = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _uninstall = new();
    private readonly Button _save = new();
    private readonly Button _close = new();
    private readonly Button _btnCheckUpdates = new();
    private readonly Button _btnApplyUpdate = new();
    private readonly TextBlock _updateStatus = new();
    private UpdateCheckResult? _availableUpdate;
    private IReadOnlyList<AudioDeviceInfo> _devices = Array.Empty<AudioDeviceInfo>();
    private StartupState _initialStartup;

    public SettingsWindow(ISettingsHost host, bool firstRun)
    {
        _host = host;
        _firstRun = firstRun;
        Title = Str.SettingsTitle;
        Width = 580;
        Height = 650;
        MinHeight = 480;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ModernTheme.ApplyWindow(this);

        _saveKey.Click += (_, _) => SaveKey();
        _delKey.Click += (_, _) => DeleteKey();
        _changeKey.Click += (_, _) =>
        {
            _isEditingKey = true;
            UpdateKeyVisibility();
            _keyBox.Clear();
            _keyBox.Focus();
        };
        _cancelEditKey.Click += (_, _) =>
        {
            _isEditingKey = false;
            _keyBox.Clear();
            UpdateKeyVisibility();
        };

        _btnCheckUpdates.Click += async (_, _) => await CheckForUpdatesAsync();
        _btnApplyUpdate.Click += async (_, _) => await ApplyUpdateAsync();

        _uninstall.Click += (_, _) => Uninstall();
        _save.Click += (_, _) => SaveAll();
        _close.Click += (_, _) => Close();

        Content = BuildContent();
        Loaded += async (_, _) => await LoadAsync();
    }

    // Stateful controls are reused when rebuilding the window in another language: they must be detached from the previous tree.
    private IEnumerable<FrameworkElement> StatefulControls() =>
        new FrameworkElement[]
        {
            _keyCard, _firstRunBanner, _firstRunText, _keyStatus, _keyBox, _saveKey, _delKey, _changeKey, _cancelEditKey,
            _mic, _mode, _hotkeyStatus, _languages, _keywords, _context,
            _clipRestore, _clipKeep, _clipKeepHistory,
            _language, _startup, _startupNote, _singleClickCopy, _message,
            _uninstall, _save, _close,
            _btnCheckUpdates, _btnApplyUpdate, _updateStatus
        };

    private UIElement BuildContent()
    {
        foreach (var c in StatefulControls())
        {
            if (c.Parent is Panel p) p.Children.Remove(c);
            else if (c.Parent is Decorator d) d.Child = null;
            else if (c.Parent is ContentControl cc) cc.Content = null;
        }
        _firstRunBanner.Child = null;
        _keyCard.Child = null;
        _keyPanel.Children.Clear();
        _keyRow.Children.Clear();
        _keyActionRow.Children.Clear();

        _mode.Items.Clear();
        _language.Items.Clear();
        _mic.Items.Clear();
        _startup.Content = Str.ChkStartup;
        _singleClickCopy.Content = Str.ChkSingleClickCopy;
        _clipRestore.Content = new TextBlock { Text = Str.ClipRestore, TextWrapping = TextWrapping.Wrap };
        _clipKeep.Content = new TextBlock { Text = Str.ClipKeep, TextWrapping = TextWrapping.Wrap };
        _clipKeepHistory.Content = new TextBlock { Text = Str.ClipKeepHistory, TextWrapping = TextWrapping.Wrap };

        var cardsPanel = new StackPanel { Margin = new Thickness(18, 16, 18, 10) };

        // 1. API key card (unified first-use notice and on-demand edit mode)
        _firstRunText.Text = Str.FirstUse;
        _firstRunText.TextWrapping = TextWrapping.Wrap;
        _firstRunText.FontWeight = FontWeights.SemiBold;
        _firstRunText.Foreground = ModernTheme.Palette.TextPrimary;

        _firstRunBanner.Child = _firstRunText;
        _firstRunBanner.Background = ModernTheme.Palette.IsDark
            ? new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x78, 0xD4))
            : new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x67, 0xC0));
        _firstRunBanner.BorderBrush = ModernTheme.Palette.Accent;
        _firstRunBanner.BorderThickness = new Thickness(1);
        _firstRunBanner.CornerRadius = new CornerRadius(6);
        _firstRunBanner.Padding = new Thickness(12, 8, 12, 8);
        _firstRunBanner.Margin = new Thickness(0, 0, 0, 10);
        _keyPanel.Children.Add(_firstRunBanner);

        _keyPanel.Children.Add(ModernTheme.CardHeader(Str.HdrKey, Str.NoteKey));
        _keyPanel.Children.Add(_keyStatus);

        _keyRow.Children.Clear();
        _keyRow.ColumnDefinitions.Clear();
        _keyRow.Margin = new Thickness(0, 6, 0, 0);
        _keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        _keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _saveKey.Content = Str.BtnSaveKey;
        _saveKey.Margin = new Thickness(8, 0, 0, 0);
        _saveKey.Padding = new Thickness(12, 4, 12, 4);
        ModernTheme.MakePrimary(_saveKey);

        _cancelEditKey.Content = Str.BtnCancel;
        _cancelEditKey.Margin = new Thickness(6, 0, 0, 0);
        _cancelEditKey.Padding = new Thickness(12, 4, 12, 4);

        _delKey.Content = Str.BtnDelete;
        _delKey.Margin = new Thickness(6, 0, 0, 0);
        _delKey.Padding = new Thickness(12, 4, 12, 4);
        ModernTheme.MakeDanger(_delKey);

        Grid.SetColumn(_keyBox, 0);
        Grid.SetColumn(_saveKey, 1);
        Grid.SetColumn(_delKey, 2);
        _keyRow.Children.Add(_keyBox);
        _keyRow.Children.Add(_saveKey);
        _keyRow.Children.Add(_delKey);
        _keyPanel.Children.Add(_keyRow);

        _keyActionRow.Children.Clear();
        _keyActionRow.Margin = new Thickness(0, 8, 0, 0);
        _changeKey.Content = Str.BtnChangeKey;
        _changeKey.Padding = new Thickness(12, 4, 12, 4);
        _keyActionRow.Children.Add(_changeKey);
        _keyPanel.Children.Add(_keyActionRow);

        _keyCard.Child = null;
        _keyCard.Child = _keyPanel;

        cardsPanel.Children.Add(BuildWelcomeCard());
        cardsPanel.Children.Add(_keyCard);

        // 2. Audio & Hotkey card
        var audioPanel = new StackPanel();
        audioPanel.Children.Add(ModernTheme.CardHeader(Str.HdrMic));
        audioPanel.Children.Add(_mic);
        var shortcutHeader = ModernTheme.CardHeader(Str.HdrShortcut, Str.NoteEscape);
        shortcutHeader.Margin = new Thickness(0, 12, 0, 6);
        audioPanel.Children.Add(shortcutHeader);
        _mode.Items.Add(Str.ModeWinH);
        _mode.Items.Add(Str.ModeCtrlAltSpace);
        audioPanel.Children.Add(_mode);
        _hotkeyStatus.Margin = new Thickness(0, 6, 0, 0);
        audioPanel.Children.Add(_hotkeyStatus);
        cardsPanel.Children.Add(ModernTheme.CreateCard(audioPanel));

        // 3. Context card
        var contextPanel = new StackPanel();
        contextPanel.Children.Add(ModernTheme.CardHeader(Str.HdrContext));
        contextPanel.Children.Add(Label(Str.LblLanguages));
        contextPanel.Children.Add(_languages);
        contextPanel.Children.Add(Label(Str.LblKeywords));
        contextPanel.Children.Add(_keywords);
        contextPanel.Children.Add(Label(Str.LblContext));
        contextPanel.Children.Add(_context);
        var reset = new Button { Content = Str.BtnResetContext, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        reset.Click += (_, _) =>
        {
            _languages.Text = string.Empty;
            _keywords.Text = string.Empty;
            _context.Text = string.Empty;
        };
        contextPanel.Children.Add(reset);
        cardsPanel.Children.Add(ModernTheme.CreateCard(contextPanel));

        // 4. Clipboard card (3 visible selectable options)
        var clipPanel = new StackPanel();
        clipPanel.Children.Add(ModernTheme.CardHeader(Str.HdrClipboard, Str.NoteClipboard));
        _clipRestore.Margin = new Thickness(0, 4, 0, 8);
        _clipKeep.Margin = new Thickness(0, 0, 0, 8);
        _clipKeepHistory.Margin = new Thickness(0, 0, 0, 4);
        clipPanel.Children.Add(_clipRestore);
        clipPanel.Children.Add(_clipKeep);
        clipPanel.Children.Add(_clipKeepHistory);
        cardsPanel.Children.Add(ModernTheme.CreateCard(clipPanel));

        // 5. Usage and cost card
        var usagePanel = new StackPanel();
        usagePanel.Children.Add(ModernTheme.CardHeader(Str.HdrUsage, Str.NoteUsage));
        var stats = new Button { Content = Str.BtnStats, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        stats.Click += (_, _) => new UsageWindow(_host.Usage) { Owner = this }.ShowDialog();
        usagePanel.Children.Add(stats);
        cardsPanel.Children.Add(ModernTheme.CreateCard(usagePanel));

        // 6. Options and language card
        var optionsPanel = new StackPanel();
        optionsPanel.Children.Add(ModernTheme.CardHeader(Str.HdrOptions));
        optionsPanel.Children.Add(ModernTheme.CardHeader(Str.HdrLanguage, Str.NoteLanguage));
        _language.Items.Add(Str.LanguageAuto);
        _language.Items.Add(Str.LanguageSpanish);
        _language.Items.Add(Str.LanguageEnglish);
        optionsPanel.Children.Add(_language);
        _startup.Margin = new Thickness(0, 12, 0, 0);
        optionsPanel.Children.Add(_startup);
        optionsPanel.Children.Add(_startupNote);
        _singleClickCopy.Margin = new Thickness(0, 8, 0, 0);
        optionsPanel.Children.Add(_singleClickCopy);
        cardsPanel.Children.Add(ModernTheme.CreateCard(optionsPanel));

        var scrollViewer = new ScrollViewer
        {
            Content = cardsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(scrollViewer, 0);
        rootGrid.Children.Add(scrollViewer);

        // Fixed bottom bar with permanently visible buttons
        _uninstall.Content = Str.BtnUninstall;
        _uninstall.Padding = new Thickness(14, 6, 14, 6);
        ModernTheme.MakeDanger(_uninstall);

        _save.Content = Str.BtnSave;
        _save.IsDefault = true;
        _save.Padding = new Thickness(20, 6, 20, 6);
        ModernTheme.MakePrimary(_save);

        _close.Content = Str.BtnClose;
        _close.IsCancel = true;
        _close.Margin = new Thickness(8, 0, 0, 0);
        _close.Padding = new Thickness(20, 6, 20, 6);

        var bottomBar = new Border
        {
            Background = ModernTheme.Palette.CardBackground,
            BorderBrush = ModernTheme.Palette.CardBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 10, 18, 12),
        };

        var bottomStack = new StackPanel();
        _message.Margin = new Thickness(0, 0, 0, 6);
        bottomStack.Children.Add(_message);

        var barGrid = new Grid();
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(_uninstall, 0);
        barGrid.Children.Add(_uninstall);

        var rightButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        rightButtons.Children.Add(_save);
        rightButtons.Children.Add(_close);
        Grid.SetColumn(rightButtons, 1);
        barGrid.Children.Add(rightButtons);

        bottomStack.Children.Add(barGrid);
        bottomBar.Child = bottomStack;

        Grid.SetRow(bottomBar, 1);
        rootGrid.Children.Add(bottomBar);

        UpdateKeyVisibility();
        return rootGrid;
    }

    private static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 4, 0, 4), Foreground = ModernTheme.Palette.TextPrimary };
    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 6, 0, 2), FontSize = 12, Foreground = ModernTheme.Palette.TextSecondary };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = ModernTheme.Palette.TextMuted, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private UIElement BuildWelcomeCard()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconSource = ModernTheme.GetAppIcon(128);
        if (iconSource is not null)
        {
            var iconImg = new Image
            {
                Source = iconSource,
                Width = 52,
                Height = 52,
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(iconImg, BitmapScalingMode.HighQuality);
            Grid.SetColumn(iconImg, 0);
            grid.Children.Add(iconImg);
        }

        var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = AppInfo.Name,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = ModernTheme.Palette.TextPrimary,
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = Str.AboutVersion(AppInfo.Version),
            FontSize = 11,
            Foreground = ModernTheme.Palette.TextMuted,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 3),
        });
        textPanel.Children.Add(titleRow);
        textPanel.Children.Add(new TextBlock
        {
            Text = Str.WelcomeSubtitle,
            FontSize = 11.5,
            Foreground = ModernTheme.Palette.TextSecondary,
            Margin = new Thickness(0, 2, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        });

        var shortcuts = new WrapPanel();
        shortcuts.Children.Add(CreateShortcutBadge("Win+H", Str.ShortcutDictate));
        shortcuts.Children.Add(CreateShortcutBadge("Ctrl+Alt+C", Str.ShortcutCopyLast));
        shortcuts.Children.Add(CreateShortcutBadge("Esc", Str.BtnCancel));
        textPanel.Children.Add(shortcuts);

        var updateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        _btnCheckUpdates.Content = Str.BtnCheckUpdates;
        _btnCheckUpdates.FontSize = 11;
        _btnCheckUpdates.Padding = new Thickness(8, 2, 8, 2);

        _btnApplyUpdate.Content = Str.BtnInstallUpdate;
        _btnApplyUpdate.FontSize = 11;
        _btnApplyUpdate.Padding = new Thickness(10, 2, 10, 2);
        _btnApplyUpdate.Margin = new Thickness(8, 0, 0, 0);
        _btnApplyUpdate.Visibility = _availableUpdate is not null ? Visibility.Visible : Visibility.Collapsed;
        ModernTheme.MakePrimary(_btnApplyUpdate);

        updateRow.Children.Add(_btnCheckUpdates);
        updateRow.Children.Add(_btnApplyUpdate);
        textPanel.Children.Add(updateRow);

        _updateStatus.FontSize = 11;
        _updateStatus.Margin = new Thickness(0, 4, 0, 0);
        _updateStatus.TextWrapping = TextWrapping.Wrap;
        textPanel.Children.Add(_updateStatus);

        var links = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        links.Children.Add(AboutLink(Str.LinkRepository, AppInfo.RepositoryUrl));
        links.Children.Add(AboutLink(Str.LinkReportBug, AppInfo.BugReportUrl));
        links.Children.Add(AboutLink(Str.LinkRequestFeature, AppInfo.FeatureRequestUrl));
        links.Children.Add(AboutLink(Str.LinkLicense, AppInfo.LicenseUrl));
        textPanel.Children.Add(links);
        textPanel.Children.Add(new TextBlock
        {
            Text = $"© 2026 {AppInfo.Author} · {Str.AboutLicenseNote}",
            FontSize = 10.5,
            Foreground = ModernTheme.Palette.TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(textPanel);

        return ModernTheme.CreateCard(grid);
    }

    // Only the fixed addresses from AppInfo are opened.
    private static TextBlock AboutLink(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url), Foreground = ModernTheme.Palette.Accent, TextDecorations = null };
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        link.RequestNavigate += (_, e) =>
        {
            e.Handled = true;
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Error("Could not open the link", ex); }
        };
        return new TextBlock(link) { FontSize = 11.5, Margin = new Thickness(0, 0, 14, 2) };
    }

    private static Border CreateShortcutBadge(string key, string label)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var kbd = new Border
        {
            Background = ModernTheme.Palette.ControlBackground,
            BorderBrush = ModernTheme.Palette.ControlBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = key,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ModernTheme.Palette.TextPrimary,
            },
        };
        sp.Children.Add(kbd);
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = ModernTheme.Palette.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            Background = ModernTheme.Palette.IsDark
                ? new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromArgb(0x0C, 0x00, 0x00, 0x00)),
            BorderBrush = ModernTheme.Palette.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 3, 8, 3),
            Margin = new Thickness(0, 0, 8, 2),
            Child = sp,
        };
    }

    private async Task LoadAsync()
    {
        var s = _host.Settings;
        UpdateKeyVisibility();
        _mode.SelectedIndex = s.HotkeyMode == HotkeyMode.WinH ? 0 : 1;
        _languages.Text = s.Languages;
        _keywords.Text = s.Keywords;
        _context.Text = s.Context;
        _clipRestore.IsChecked = s.ClipboardMode == ClipboardMode.Restore;
        _clipKeep.IsChecked = s.ClipboardMode == ClipboardMode.Keep;
        _clipKeepHistory.IsChecked = s.ClipboardMode == ClipboardMode.KeepWithHistory;
        _language.SelectedIndex = (int)s.Language;
        ShowHotkeyStatus(_host.CurrentHotkey);

        _initialStartup = _host.StartupState;
        _startup.IsChecked = _initialStartup == StartupState.DisabledByWindows ? false : s.StartWithWindows;
        _singleClickCopy.IsChecked = s.SingleClickCopiesLast;
        _startupNote.Text = _initialStartup switch
        {
            StartupState.DisabledByWindows => Str.StartupDisabledByWindows,
            StartupState.StalePath => Str.StartupStalePath,
            _ => string.Empty,
        };

        _mic.Items.Add(Str.MicDefault);
        _mic.SelectedIndex = 0;
        _devices = await _host.ListMicrophonesAsync();
        foreach (var d in _devices) _mic.Items.Add(d.Name);
        if (s.MicrophoneId is { } id)
        {
            int idx = _devices.ToList().FindIndex(d => d.Id == id);
            if (idx >= 0) _mic.SelectedIndex = idx + 1;
            else
            {
                _mic.Items.Add(Str.MicSavedMissing);
                _mic.SelectedIndex = _mic.Items.Count - 1;
            }
        }
    }

    private void UpdateKeyVisibility()
    {
        bool hasKey = _host?.HasApiKey == true;
        _keyStatus.Text = hasKey ? Str.KeyStored : Str.KeyNone;
        _keyStatus.Foreground = hasKey ? ModernTheme.Palette.SuccessText : ModernTheme.Palette.TextMuted;
        _keyStatus.FontWeight = hasKey ? FontWeights.SemiBold : FontWeights.Normal;

        bool showNotice = !hasKey;
        _firstRunBanner.Visibility = showNotice ? Visibility.Visible : Visibility.Collapsed;
        _keyCard.BorderBrush = showNotice ? ModernTheme.Palette.Accent : ModernTheme.Palette.CardBorder;
        _keyCard.BorderThickness = new Thickness(showNotice ? 1.5 : 1);

        if (hasKey)
        {
            _keyActionRow.Visibility = _isEditingKey ? Visibility.Collapsed : Visibility.Visible;
            _keyRow.Visibility = _isEditingKey ? Visibility.Visible : Visibility.Collapsed;
            if (_isEditingKey)
            {
                if (_keyRow.Children.Contains(_delKey)) _keyRow.Children.Remove(_delKey);
                if (!_keyRow.Children.Contains(_cancelEditKey))
                {
                    Grid.SetColumn(_cancelEditKey, 2);
                    _keyRow.Children.Add(_cancelEditKey);
                }
            }
            else
            {
                if (_keyRow.Children.Contains(_cancelEditKey)) _keyRow.Children.Remove(_cancelEditKey);
                if (!_keyRow.Children.Contains(_delKey))
                {
                    Grid.SetColumn(_delKey, 2);
                    _keyRow.Children.Add(_delKey);
                }
            }
        }
        else
        {
            _isEditingKey = false;
            _keyActionRow.Visibility = Visibility.Collapsed;
            _keyRow.Visibility = Visibility.Visible;
            if (_keyRow.Children.Contains(_cancelEditKey)) _keyRow.Children.Remove(_cancelEditKey);
            if (!_keyRow.Children.Contains(_delKey))
            {
                Grid.SetColumn(_delKey, 2);
                _keyRow.Children.Add(_delKey);
            }
        }
    }

    private void SaveKey()
    {
        try
        {
            _host.SaveApiKey(_keyBox.Password);
            _keyBox.Clear();
            _isEditingKey = false;
            UpdateKeyVisibility();
            Say(Str.KeySaved, false);
        }
        catch (DictationException ex)
        {
            Say(ex.Message, true);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not save the key", ex);
            Say(Str.KeySaveFailed, true);
        }
    }

    private void DeleteKey()
    {
        try
        {
            _host.DeleteApiKey();
            _isEditingKey = false;
            UpdateKeyVisibility();
            Say(Str.KeyDeleted, false);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not delete the key", ex);
            Say(Str.KeyDeleteFailed, true);
        }
    }

    private void SaveAll()
    {
        try
        {
            // If a typed key has not been saved, it is saved too.
            if (_keyBox.Password.Length > 0) SaveKey();

            string? micId = null;
            int sel = _mic.SelectedIndex;
            if (sel >= 1 && sel - 1 < _devices.Count) micId = _devices[sel - 1].Id;
            else if (sel >= 1) micId = _host.Settings.MicrophoneId;

            ClipboardMode clipMode = _clipKeepHistory.IsChecked == true ? ClipboardMode.KeepWithHistory :
                                     _clipKeep.IsChecked == true ? ClipboardMode.Keep :
                                     ClipboardMode.Restore;

            var updated = _host.Settings with
            {
                HotkeyMode = _mode.SelectedIndex == 0 ? HotkeyMode.WinH : HotkeyMode.CtrlAltSpace,
                MicrophoneId = micId,
                Languages = _languages.Text.Trim(),
                Keywords = _keywords.Text.Trim(),
                Context = _context.Text.Trim(),
                ClipboardMode = clipMode,
                StartWithWindows = _startup.IsChecked == true,
                SingleClickCopiesLast = _singleClickCopy.IsChecked == true,
                Language = _language.SelectedIndex is >= 0 and <= 2 ? (UiLanguage)_language.SelectedIndex : UiLanguage.Auto,
            };
            bool languageChanged = updated.Language != _host.Settings.Language;
            var result = _host.Apply(updated, _startup.IsChecked == true);
            if (languageChanged) RebuildForLanguage();
            ShowHotkeyStatus(result);
            _initialStartup = _host.StartupState;
            Say(_host.HasApiKey ? Str.SettingsSaved : Str.SettingsSavedNoKey, !_host.HasApiKey);
            if (_firstRun && _host.HasApiKey) Close();
        }
        catch (DictationException ex)
        {
            Say(ex.Message, true);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not save the settings", ex);
            Say(Str.SettingsSaveFailed, true);
        }
    }

    // Rebuilds the texts in the newly applied language; values are already saved, so they are read again.
    private void RebuildForLanguage()
    {
        Title = Str.SettingsTitle;
        Content = BuildContent();
        _ = LoadAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_host is null) return;
        _btnCheckUpdates.IsEnabled = false;
        _btnApplyUpdate.Visibility = Visibility.Collapsed;
        _updateStatus.Text = Str.CheckingUpdates;
        _updateStatus.ToolTip = _updateStatus.Text;
        _updateStatus.Foreground = ModernTheme.Palette.TextMuted;

        try
        {
            var result = await _host.CheckForUpdatesAsync(CancellationToken.None);
            if (result.HasUpdate && result.LatestVersion is not null)
            {
                _availableUpdate = result;
                _updateStatus.Text = Str.UpdateAvailable(result.LatestVersion);
                _updateStatus.ToolTip = _updateStatus.Text;
                _updateStatus.Foreground = ModernTheme.Palette.Accent;
                _btnApplyUpdate.Visibility = Visibility.Visible;
            }
            else
            {
                _availableUpdate = null;
                _updateStatus.Text = Str.AlreadyLatestVersion;
                _updateStatus.ToolTip = _updateStatus.Text;
                _updateStatus.Foreground = ModernTheme.Palette.SuccessText;
            }
        }
        catch (Exception ex)
        {
            _availableUpdate = null;
            Logger.Error("Update check failed", ex);
            _updateStatus.Text = ex is DictationException de ? de.Message : Str.UpdateCheckFailed;
            _updateStatus.ToolTip = _updateStatus.Text;
            _updateStatus.Foreground = ModernTheme.Palette.DangerText;
        }
        finally
        {
            _btnCheckUpdates.IsEnabled = true;
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (_host is null || _availableUpdate is null) return;
        _btnCheckUpdates.IsEnabled = false;
        _btnApplyUpdate.IsEnabled = false;
        _updateStatus.Foreground = ModernTheme.Palette.TextPrimary;

        try
        {
            _updateStatus.Text = Str.DownloadingUpdate;
            _updateStatus.ToolTip = _updateStatus.Text;
            var progress = new Progress<double>(p =>
            {
                int percent = (int)Math.Clamp(p * 100, 0, 100);
                _updateStatus.Text = Str.DownloadingUpdatePercent(percent);
                _updateStatus.ToolTip = _updateStatus.Text;
            });

            var staged = await _host.DownloadUpdateAsync(_availableUpdate, progress, CancellationToken.None);
            _updateStatus.Text = Str.InstallingUpdate;
            _updateStatus.ToolTip = _updateStatus.Text;
            _host.ApplyUpdateAndRestart(staged);
        }
        catch (Exception ex)
        {
            Logger.Error("Update download or installation failed", ex);
            _updateStatus.Text = ex is DictationException de ? de.Message : ex.Message;
            _updateStatus.ToolTip = _updateStatus.Text;
            _updateStatus.Foreground = ModernTheme.Palette.DangerText;
            _btnCheckUpdates.IsEnabled = true;
            _btnApplyUpdate.IsEnabled = true;
        }
    }

    private void Uninstall()
    {
        var dialog = new UninstallDialog(SelfUninstaller.IsWingetInstall(Environment.ProcessPath ?? string.Empty)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            string? error = _host.Uninstall(dialog.KeepSettings);
            if (error is not null) Say(error, true);
        }
        catch (Exception ex)
        {
            Logger.Error("Uninstall failed", ex);
            Say(Str.UninstallFailed, true);
        }
    }

    private void ShowHotkeyStatus(HotkeyApplyResult r)
    {
        _hotkeyStatus.Text = r.IsActive ? Str.HotkeyStatusActive(r.Description, r.Warning) : Str.HotkeyStatusInactive(r.Warning);
    }

    private void Say(string text, bool error)
    {
        _message.Text = text;
        _message.Foreground = error ? ModernTheme.Palette.DangerText : ModernTheme.Palette.SuccessText;
        _message.Margin = new Thickness(0, 0, 0, 8);
    }
}

// Uninstall confirmation with both variants (everything, or keep only the non-secret settings).
internal sealed class UninstallDialog : Window
{
    private readonly RadioButton _all = new() { IsChecked = true, GroupName = "UninstallOptions" };
    private readonly RadioButton _keep = new() { GroupName = "UninstallOptions" };

    public bool KeepSettings => _keep.IsChecked == true;

    public UninstallDialog(bool installedWithWinget = false)
    {
        Title = Str.UninstallTitle;
        Width = 530;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ModernTheme.ApplyWindow(this);

        var root = new StackPanel { Margin = new Thickness(20) };

        // Header with warning icon and clear description
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = ModernTheme.Palette.DangerBackground,
            BorderBrush = ModernTheme.Palette.DangerBorder,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 2, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "\u26A0", // Warning icon
                FontSize = 18,
                Foreground = ModernTheme.Palette.DangerText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(badge, 0);
        headerGrid.Children.Add(badge);

        var headerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headerText.Children.Add(new TextBlock
        {
            Text = Str.UninstallTitle,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ModernTheme.Palette.TextPrimary,
        });
        headerText.Children.Add(new TextBlock
        {
            Text = Str.UninstallIntro,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ModernTheme.Palette.TextSecondary,
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
        });
        Grid.SetColumn(headerText, 1);
        headerGrid.Children.Add(headerText);
        root.Children.Add(headerGrid);

        // Option 1: Remove everything
        var allPanel = new StackPanel();
        allPanel.Children.Add(new TextBlock
        {
            Text = Str.UninstallAllTitle,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = ModernTheme.Palette.TextPrimary,
        });
        allPanel.Children.Add(new TextBlock
        {
            Text = Str.UninstallAllDesc,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ModernTheme.Palette.TextSecondary,
            FontSize = 11.5,
            Margin = new Thickness(0, 2, 0, 0),
        });
        _all.Content = allPanel;
        _all.HorizontalAlignment = HorizontalAlignment.Stretch;

        var allCard = ModernTheme.CreateCard(_all);
        allCard.Cursor = System.Windows.Input.Cursors.Hand;
        allCard.MouseLeftButtonUp += (_, _) => _all.IsChecked = true;
        allCard.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(allCard);

        // Option 2: Keep settings
        var keepPanel = new StackPanel();
        keepPanel.Children.Add(new TextBlock
        {
            Text = Str.UninstallKeepTitle,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = ModernTheme.Palette.TextPrimary,
        });
        keepPanel.Children.Add(new TextBlock
        {
            Text = Str.UninstallKeepDesc,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ModernTheme.Palette.TextSecondary,
            FontSize = 11.5,
            Margin = new Thickness(0, 2, 0, 0),
        });
        _keep.Content = keepPanel;
        _keep.HorizontalAlignment = HorizontalAlignment.Stretch;

        var keepCard = ModernTheme.CreateCard(_keep);
        keepCard.Cursor = System.Windows.Input.Cursors.Hand;
        keepCard.MouseLeftButtonUp += (_, _) => _keep.IsChecked = true;
        keepCard.Margin = new Thickness(0, 0, 0, installedWithWinget ? 8 : 16);
        root.Children.Add(keepCard);

        if (installedWithWinget)
        {
            root.Children.Add(new TextBlock
            {
                Text = Str.UninstallWingetNote,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ModernTheme.Palette.TextSecondary,
                FontSize = 11.5,
                Margin = new Thickness(2, 0, 0, 16),
            });
        }

        // Action buttons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = Str.BtnUninstallConfirm, Padding = new Thickness(18, 5, 18, 5) };
        ModernTheme.MakeDanger(ok);
        ok.Click += (_, _) => DialogResult = true;

        var cancel = new Button { Content = Str.BtnCancel, IsDefault = true, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(18, 5, 18, 5) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
    }
}
