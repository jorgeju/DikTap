using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DikTap.Localization;
using DikTap.Services.Usage;

namespace DikTap.Views;

// Usage statistics and estimated cost from the local log; created on open and released on close.
internal sealed class UsageWindow : Window
{
    private const double ChartHeight = 110;
    private static Brush Muted => ModernTheme.Palette.TextMuted;

    private readonly IUsageStore _store;
    private IReadOnlyList<UsageRecord> _all = Array.Empty<UsageRecord>();
    private readonly List<string> _keys = new();

    private readonly ComboBox _period = new() { MinWidth = 150 };
    private readonly ComboBox _key = new() { MinWidth = 150 };
    private readonly TextBox _from = new() { Width = 110 };
    private readonly TextBox _to = new() { Width = 110 };
    private readonly TextBlock _error = new() { Foreground = ModernTheme.Palette.DangerText, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = ModernTheme.Palette.SuccessText };
    private readonly StackPanel _body = new();
    private bool _building = true;

    public UsageWindow(IUsageStore store)
    {
        _store = store;
        Title = Str.StatsTitle;
        Width = 660;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 780;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ModernTheme.ApplyWindow(this);
        Content = new ScrollViewer { Content = BuildLayout(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _period.Items.Add(Str.PeriodToday);
        _period.Items.Add(Str.PeriodLast7);
        _period.Items.Add(Str.PeriodLast30);
        _period.Items.Add(Str.PeriodMonth);
        _period.Items.Add(Str.PeriodAll);
        _period.Items.Add(Str.PeriodCustom);
        _period.SelectedIndex = (int)UsagePeriod.Last30Days;

        Reload();
        _building = false;
        ApplyPeriod();
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(18) };

        var filters = new WrapPanel();
        filters.Children.Add(Field(Str.StatsPeriod, _period));
        filters.Children.Add(Field(Str.StatsKey, _key));
        filters.Children.Add(Field(Str.StatsFrom, _from));
        filters.Children.Add(Field(Str.StatsTo, _to));
        root.Children.Add(ModernTheme.CreateCard(filters));
        root.Children.Add(_error);

        _period.SelectionChanged += (_, _) => { if (!_building) ApplyPeriod(); };
        _key.SelectionChanged += (_, _) => { if (!_building) Refresh(); };
        foreach (var box in new[] { _from, _to })
        {
            box.LostKeyboardFocus += (_, _) => { if (!_building && IsCustom) Refresh(); };
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter && IsCustom) Refresh(); };
        }

        _body.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(_body);

        var model = AppInfo.TranscriptionModel;
        root.Children.Add(new TextBlock
        {
            Text = Str.StatsNote(model, Fmt.Usd(UsagePricing.PerMinute(model))),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted,
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
        });

        _status.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var reset = new Button { Content = Str.BtnReset, Padding = new Thickness(18, 5, 18, 5) };
        ModernTheme.MakeDanger(reset);
        reset.Click += (_, _) => Reset();
        var close = new Button { Content = Str.BtnClose, IsCancel = true, IsDefault = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(18, 5, 18, 5) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(reset);
        buttons.Children.Add(close);
        root.Children.Add(buttons);
        return root;
    }

    private static UIElement Field(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 6) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
        panel.Children.Add(control);
        return panel;
    }

    private bool IsCustom => _period.SelectedIndex == (int)UsagePeriod.Custom;

    private void Reload()
    {
        try
        {
            _all = _store.Load();
            _error.Text = string.Empty;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not read usage.jsonl", ex);
            _all = Array.Empty<UsageRecord>();
            _error.Text = Str.StatsLoadFailed;
        }

        string? selected = _key.SelectedIndex > 0 && _key.SelectedIndex - 1 < _keys.Count ? _keys[_key.SelectedIndex - 1] : null;
        _keys.Clear();
        _keys.AddRange(_all.Select(r => r.Key).Distinct().OrderBy(k => k, StringComparer.Ordinal));
        _key.Items.Clear();
        _key.Items.Add(Str.KeyAll);
        foreach (string k in _keys) _key.Items.Add(KeyLabel(k));
        int idx = selected is null ? -1 : _keys.IndexOf(selected);
        _key.SelectedIndex = idx >= 0 ? idx + 1 : 0;
    }

    private static string KeyLabel(string key) => key == UsagePricing.UnknownKey ? Str.KeyUnknown : "…" + key;

    // Fixed periods fill in the dates (read-only); in Custom the user types them.
    private void ApplyPeriod()
    {
        bool custom = IsCustom;
        _from.IsEnabled = _to.IsEnabled = custom;
        if (!custom)
        {
            var f = UsageFilter.ForPeriod((UsagePeriod)_period.SelectedIndex, Today, null);
            _from.Text = f.From is { } a ? Fmt.Date(a) : string.Empty;
            _to.Text = f.To is { } b ? Fmt.Date(b) : string.Empty;
        }
        Refresh();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    // Returns null if the typed dates are not valid (and flags it).
    private UsageFilter? ReadFilter()
    {
        string? key = _key.SelectedIndex > 0 && _key.SelectedIndex - 1 < _keys.Count ? _keys[_key.SelectedIndex - 1] : null;
        if (!IsCustom) return UsageFilter.ForPeriod((UsagePeriod)_period.SelectedIndex, Today, key);

        DateOnly? from = null, to = null;
        if (_from.Text.Trim().Length > 0)
        {
            if (!Fmt.TryParseDate(_from.Text, out var d)) return Invalid();
            from = d;
        }
        if (_to.Text.Trim().Length > 0)
        {
            if (!Fmt.TryParseDate(_to.Text, out var d)) return Invalid();
            to = d;
        }
        _error.Text = string.Empty;
        return new UsageFilter(from, to, key);
    }

    private UsageFilter? Invalid()
    {
        _error.Text = Str.StatsInvalidDate;
        return null;
    }

    private void Refresh()
    {
        _status.Text = string.Empty;
        if (ReadFilter() is not { } filter) return;
        var report = UsageReport.Build(_all, filter);

        _body.Children.Clear();
        if (report.Count == 0)
        {
            _body.Children.Add(new TextBlock { Text = Str.NoData, Foreground = Muted, Margin = new Thickness(0, 8, 0, 8) });
            return;
        }

        var cards = new WrapPanel();
        cards.Children.Add(Card(Str.StatDictations, Fmt.Integer(report.Count)));
        cards.Children.Add(Card(Str.StatMinutes, Fmt.Minutes(report.Seconds)));
        cards.Children.Add(Card(Str.StatCost, Fmt.Usd(report.CostUsd)));
        cards.Children.Add(Card(Str.StatAverage, Fmt.Usd(report.AverageCostUsd)));
        _body.Children.Add(cards);

        if (report.InputTokens + report.OutputTokens > 0)
            _body.Children.Add(new TextBlock { Text = Str.StatTokens(Fmt.Integer(report.InputTokens), Fmt.Integer(report.OutputTokens)), Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });

        if (report.Days.Count > 1)
            _body.Children.Add(BuildChart(report.Days));

        if (report.ByKey.Count > 1)
        {
            _body.Children.Add(new TextBlock { Text = Str.ByKeyTitle, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
            foreach (var k in report.ByKey)
            {
                _body.Children.Add(new TextBlock
                {
                    Text = $"{KeyLabel(k.Key)}: {Str.UsageLine(Fmt.Integer(k.Count), Fmt.Minutes(k.Seconds), Fmt.Usd(k.CostUsd))}",
                    Margin = new Thickness(0, 1, 0, 1),
                });
            }
        }
    }

    private static Border Card(string label, string value)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = ModernTheme.Palette.TextPrimary });
        panel.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
        return new Border
        {
            Child = panel,
            Background = ModernTheme.Palette.CardBackground,
            BorderBrush = ModernTheme.Palette.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 8, 8),
            MinWidth = 135,
        };
    }

    private static UIElement BuildChart(IReadOnlyList<DayUsage> days)
    {
        double max = days.Max(d => d.CostUsd);
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = Str.ChartTitle(Fmt.Usd(max)), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6), Foreground = ModernTheme.Palette.TextPrimary });

        var bars = new UniformGrid { Rows = 1, Height = ChartHeight };
        double gap = days.Count > 31 ? 0.5 : 1.5;
        foreach (var d in days)
        {
            double h = max > 0 && d.CostUsd > 0 ? Math.Max(2, ChartHeight * d.CostUsd / max) : 0;
            var cell = new Grid
            {
                Margin = new Thickness(gap, 0, gap, 0),
                Background = Brushes.Transparent,
                ToolTip = $"{Fmt.Date(d.Day)}\n{Str.UsageLine(Fmt.Integer(d.Count), Fmt.Minutes(d.Seconds), Fmt.Usd(d.CostUsd))}",
            };
            cell.Children.Add(new Border { Height = h, VerticalAlignment = VerticalAlignment.Bottom, Background = ModernTheme.Palette.Accent, CornerRadius = new CornerRadius(2, 2, 0, 0) });
            bars.Children.Add(cell);
        }
        var chartBg = ModernTheme.Palette.IsDark ? new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)) : new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF8));
        root.Children.Add(new Border { Child = bars, BorderBrush = ModernTheme.Palette.CardBorder, BorderThickness = new Thickness(0, 0, 0, 1), Background = chartBg });

        var axis = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var last = new TextBlock { Text = Fmt.Date(days[^1].Day), Foreground = Muted, FontSize = 11 };
        DockPanel.SetDock(last, Dock.Right);
        axis.Children.Add(last);
        axis.Children.Add(new TextBlock { Text = Fmt.Date(days[0].Day), Foreground = Muted, FontSize = 11 });
        root.Children.Add(axis);
        return ModernTheme.CreateCard(root);
    }

    // Deletes what matches the current filter: with "Todo" (All) and "Todas las claves" (All keys) it is a full reset.
    private void Reset()
    {
        if (ReadFilter() is not { } filter) return;
        int count = _all.Count(filter.Matches);
        if (count == 0)
        {
            MessageBox.Show(this, Str.ResetNothing, Str.ResetTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, Str.ResetConfirm(count), Str.ResetTitle, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;

        try
        {
            int removed = _store.Remove(filter.Matches);
            Reload();
            Refresh();
            _status.Foreground = Brushes.SeaGreen;
            _status.Text = Str.ResetDone(removed);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not delete the usage records", ex);
            _status.Foreground = Brushes.Firebrick;
            _status.Text = Str.ResetFailed;
        }
    }
}
