using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using DikTap.Interop;

namespace DikTap.Views;

// Modern Fluent theme matching Windows 11: supports light and dark mode, rounded corners and low cost.
internal static class ModernTheme
{
    public static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val) return val == 0;
        }
        catch { }
        return false;
    }

    public sealed class ThemePalette
    {
        public bool IsDark { get; init; }
        public required Brush WindowBackground { get; init; }
        public required Brush CardBackground { get; init; }
        public required Brush CardBorder { get; init; }
        public required Brush TextPrimary { get; init; }
        public required Brush TextSecondary { get; init; }
        public required Brush TextMuted { get; init; }
        public required Brush ControlBackground { get; init; }
        public required Brush ControlBorder { get; init; }
        public required Brush ControlHover { get; init; }
        public required Brush ControlPressed { get; init; }
        public required Brush FocusBorder { get; init; }
        public required Brush Accent { get; init; }
        public required Brush AccentHover { get; init; }
        public required Brush AccentPressed { get; init; }
        public required Brush AccentText { get; init; }
        public required Brush DangerText { get; init; }
        public required Brush DangerBackground { get; init; }
        public required Brush DangerBorder { get; init; }
        public required Brush DangerHover { get; init; }
        public required Brush SuccessText { get; init; }
    }

    public static ThemePalette Palette { get; } = CreatePalette(IsDarkTheme());

    private static ThemePalette CreatePalette(bool dark)
    {
        Brush B(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
        return dark
            ? new ThemePalette
            {
                IsDark = true,
                WindowBackground = B(0x20, 0x20, 0x20),
                CardBackground = B(0x2C, 0x2C, 0x2C),
                CardBorder = B(0x38, 0x38, 0x38),
                TextPrimary = B(0xFF, 0xFF, 0xFF),
                TextSecondary = B(0xC5, 0xC5, 0xC5),
                TextMuted = B(0x8E, 0x8E, 0x8E),
                ControlBackground = B(0x25, 0x25, 0x25),
                ControlBorder = B(0x45, 0x45, 0x45),
                ControlHover = B(0x38, 0x38, 0x38),
                ControlPressed = B(0x2F, 0x2F, 0x2F),
                FocusBorder = B(0x4C, 0xC2, 0xFF),
                Accent = B(0x00, 0x78, 0xD4),
                AccentHover = B(0x19, 0x8A, 0xE6),
                AccentPressed = B(0x00, 0x6C, 0xBE),
                AccentText = B(0xFF, 0xFF, 0xFF),
                DangerText = B(0xFF, 0x73, 0x69),
                DangerBackground = B(0x32, 0x1F, 0x20),
                DangerBorder = B(0x70, 0x2A, 0x26),
                DangerHover = B(0x42, 0x23, 0x25),
                SuccessText = B(0x54, 0xB0, 0x54),
            }
            : new ThemePalette
            {
                IsDark = false,
                WindowBackground = B(0xF3, 0xF3, 0xF3),
                CardBackground = B(0xFF, 0xFF, 0xFF),
                CardBorder = B(0xE5, 0xE5, 0xE5),
                TextPrimary = B(0x1A, 0x1A, 0x1A),
                TextSecondary = B(0x5D, 0x5D, 0x5D),
                TextMuted = B(0x76, 0x76, 0x76),
                ControlBackground = B(0xFF, 0xFF, 0xFF),
                ControlBorder = B(0xD1, 0xD1, 0xD1),
                ControlHover = B(0xF5, 0xF5, 0xF5),
                ControlPressed = B(0xEB, 0xEB, 0xEB),
                FocusBorder = B(0x00, 0x67, 0xC0),
                Accent = B(0x00, 0x67, 0xC0),
                AccentHover = B(0x18, 0x73, 0xC8),
                AccentPressed = B(0x00, 0x5F, 0xB8),
                AccentText = B(0xFF, 0xFF, 0xFF),
                DangerText = B(0xC4, 0x2B, 0x1C),
                DangerBackground = B(0xFD, 0xF2, 0xF2),
                DangerBorder = B(0xF1, 0xB5, 0xB0),
                DangerHover = B(0xFC, 0xE4, 0xE4),
                SuccessText = B(0x0F, 0x7B, 0x0F),
            };
    }

    public static BitmapSource? GetAppIcon(int targetSize = 256)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DikTap.Assets.app.ico");
            if (stream is not null)
            {
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.Where(f => f.PixelWidth >= targetSize).OrderBy(f => f.PixelWidth).FirstOrDefault()
                    ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
                frame?.Freeze();
                return frame;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Could not load the embedded app.ico icon", ex);
        }
        return null;
    }

    public static void ApplyWindow(Window window)
    {
        var p = Palette;
        window.Background = p.WindowBackground;
        window.Foreground = p.TextPrimary;
        window.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        window.Icon = GetAppIcon(32);

        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int dark = p.IsDark ? 1 : 0;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                int round = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }
            catch { }
        };

        ApplyImplicitStyles(window.Resources, p);
    }

    public static Border CreateCard(UIElement content) => new()
    {
        Background = Palette.CardBackground,
        BorderBrush = Palette.CardBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 12, 16, 14),
        Margin = new Thickness(0, 0, 0, 10),
        Child = content,
    };

    public static StackPanel CardHeader(string title, string? subtitle = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13.5,
            Foreground = Palette.TextPrimary,
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            panel.Children.Add(new TextBlock
            {
                Text = subtitle,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Palette.TextSecondary,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        return panel;
    }

    public static void MakePrimary(Button button)
    {
        var p = Palette;
        button.Background = p.Accent;
        button.Foreground = p.AccentText;
        button.BorderBrush = p.Accent;
        button.FontWeight = FontWeights.SemiBold;
        button.Style = CreateAccentButtonStyle(p);
    }

    public static void MakeDanger(Button button)
    {
        var p = Palette;
        button.Background = p.DangerBackground;
        button.Foreground = p.DangerText;
        button.BorderBrush = p.DangerBorder;
        button.Style = CreateDangerButtonStyle(p);
    }

    private static void ApplyImplicitStyles(ResourceDictionary res, ThemePalette p)
    {
        res[typeof(Button)] = CreateDefaultButtonStyle(p);
        res[typeof(TextBox)] = CreateTextBoxStyle(p);
        res[typeof(PasswordBox)] = CreatePasswordBoxStyle(p);
        res[typeof(ComboBox)] = CreateComboBoxStyle(p);
        res[typeof(CheckBox)] = CreateCheckBoxStyle(p);
        res[typeof(RadioButton)] = CreateRadioButtonStyle(p);
    }

    private static Style CreateDefaultButtonStyle(ThemePalette p)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, p.ControlBackground));
        style.Setters.Add(new Setter(Control.ForegroundProperty, p.TextPrimary));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, p.ControlBorder));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 5, 12, 5)));

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "border");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, p.ControlHover, "border"));
        template.Triggers.Add(over);

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, p.ControlPressed, "border"));
        template.Triggers.Add(pressed);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style CreateAccentButtonStyle(ThemePalette p)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, p.Accent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, p.AccentText));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, p.Accent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 5, 14, 5)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "border");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, p.AccentHover, "border"));
        template.Triggers.Add(over);

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, p.AccentPressed, "border"));
        template.Triggers.Add(pressed);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style CreateDangerButtonStyle(ThemePalette p)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, p.DangerBackground));
        style.Setters.Add(new Setter(Control.ForegroundProperty, p.DangerText));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, p.DangerBorder));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 5, 12, 5)));

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "border");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, p.DangerHover, "border"));
        template.Triggers.Add(over);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style CreateTextBoxStyle(ThemePalette p)
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(Control.BackgroundProperty, p.ControlBackground));
        style.Setters.Add(new Setter(Control.ForegroundProperty, p.TextPrimary));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, p.ControlBorder));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));

        var template = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border), "border");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

        var scroll = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        scroll.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        scroll.SetValue(Control.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(scroll);
        template.VisualTree = border;

        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, p.FocusBorder, "border"));
        template.Triggers.Add(focus);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style CreatePasswordBoxStyle(ThemePalette p)
    {
        var style = new Style(typeof(PasswordBox));
        style.Setters.Add(new Setter(Control.BackgroundProperty, p.ControlBackground));
        style.Setters.Add(new Setter(Control.ForegroundProperty, p.TextPrimary));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, p.ControlBorder));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));

        var template = new ControlTemplate(typeof(PasswordBox));
        var border = new FrameworkElementFactory(typeof(Border), "border");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

        var scroll = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        scroll.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        scroll.SetValue(Control.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(scroll);
        template.VisualTree = border;

        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, p.FocusBorder, "border"));
        template.Triggers.Add(focus);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style CreateComboBoxStyle(ThemePalette p)
    {
        var style = new Style(typeof(ComboBox));
        style.Setters.Add(new Setter(Control.BackgroundProperty, p.ControlBackground));
        style.Setters.Add(new Setter(Control.ForegroundProperty, p.TextPrimary));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, p.ControlBorder));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 8, 5)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));

        var template = new ControlTemplate(typeof(ComboBox));
        var grid = new FrameworkElementFactory(typeof(Grid));

        var toggle = new FrameworkElementFactory(typeof(ToggleButton), "toggle");
        toggle.SetValue(FrameworkElement.FocusableProperty, false);
        toggle.SetValue(ButtonBase.ClickModeProperty, ClickMode.Press);
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("IsDropDownOpen") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });

        var toggleTemplate = new ControlTemplate(typeof(ToggleButton));
        var toggleBorder = new FrameworkElementFactory(typeof(Border), "btnBorder");
        toggleBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        toggleBorder.SetValue(Border.BackgroundProperty, p.ControlBackground);
        toggleBorder.SetValue(Border.BorderBrushProperty, p.ControlBorder);
        toggleBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));

        var toggleGrid = new FrameworkElementFactory(typeof(Grid));
        var arrow = new FrameworkElementFactory(typeof(TextBlock));
        arrow.SetValue(TextBlock.TextProperty, "\u2304"); // chevron
        arrow.SetValue(TextBlock.FontSizeProperty, 13.0);
        arrow.SetValue(TextBlock.ForegroundProperty, p.TextSecondary);
        arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 2));
        toggleGrid.AppendChild(arrow);
        toggleBorder.AppendChild(toggleGrid);
        toggleTemplate.VisualTree = toggleBorder;

        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, p.ControlHover, "btnBorder"));
        toggleTemplate.Triggers.Add(over);
        toggle.SetValue(Control.TemplateProperty, toggleTemplate);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter), "content");
        presenter.SetValue(FrameworkElement.IsHitTestVisibleProperty, false);
        presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 4, 28, 4));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
        presenter.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));

        var popup = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetBinding(Popup.IsOpenProperty, new System.Windows.Data.Binding("IsDropDownOpen") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });

        var popBorder = new FrameworkElementFactory(typeof(Border), "popBorder");
        popBorder.SetValue(Border.BackgroundProperty, p.CardBackground);
        popBorder.SetValue(Border.BorderBrushProperty, p.CardBorder);
        popBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        popBorder.SetValue(FrameworkElement.MinWidthProperty, new TemplateBindingExtension(FrameworkElement.ActualWidthProperty));
        popBorder.SetValue(FrameworkElement.MaxHeightProperty, 220.0);
        popBorder.SetValue(Border.PaddingProperty, new Thickness(4));

        var popScroll = new FrameworkElementFactory(typeof(ScrollViewer));
        popScroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        popScroll.AppendChild(items);
        popBorder.AppendChild(popScroll);
        popup.AppendChild(popBorder);

        grid.AppendChild(toggle);
        grid.AppendChild(presenter);
        grid.AppendChild(popup);
        template.VisualTree = grid;

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style CreateCheckBoxStyle(ThemePalette p)
    {
        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, p.TextPrimary));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));

        var template = new ControlTemplate(typeof(CheckBox));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var col0 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col0.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        grid.AppendChild(col0);
        grid.AppendChild(col1);

        var box = new FrameworkElementFactory(typeof(Border), "box");
        box.SetValue(FrameworkElement.WidthProperty, 18.0);
        box.SetValue(FrameworkElement.HeightProperty, 18.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        box.SetValue(Border.BackgroundProperty, p.ControlBackground);
        box.SetValue(Border.BorderBrushProperty, p.ControlBorder);
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1.2));
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        box.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        box.SetValue(Grid.ColumnProperty, 0);

        var check = new FrameworkElementFactory(typeof(TextBlock), "check");
        check.SetValue(TextBlock.TextProperty, "\u2713");
        check.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        check.SetValue(TextBlock.FontSizeProperty, 12.0);
        check.SetValue(TextBlock.ForegroundProperty, p.AccentText);
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        box.AppendChild(check);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(Grid.ColumnProperty, 1);

        grid.AppendChild(box);
        grid.AppendChild(content);
        template.VisualTree = grid;

        var checkedTrig = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrig.Setters.Add(new Setter(Border.BackgroundProperty, p.Accent, "box"));
        checkedTrig.Setters.Add(new Setter(Border.BorderBrushProperty, p.Accent, "box"));
        checkedTrig.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "check"));
        template.Triggers.Add(checkedTrig);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style CreateRadioButtonStyle(ThemePalette p)
    {
        var style = new Style(typeof(RadioButton));
        style.Setters.Add(new Setter(Control.ForegroundProperty, p.TextPrimary));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));

        var template = new ControlTemplate(typeof(RadioButton));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var col0 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col0.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        grid.AppendChild(col0);
        grid.AppendChild(col1);

        var circle = new FrameworkElementFactory(typeof(Border), "circle");
        circle.SetValue(FrameworkElement.WidthProperty, 18.0);
        circle.SetValue(FrameworkElement.HeightProperty, 18.0);
        circle.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        circle.SetValue(Border.BackgroundProperty, p.ControlBackground);
        circle.SetValue(Border.BorderBrushProperty, p.ControlBorder);
        circle.SetValue(Border.BorderThicknessProperty, new Thickness(1.2));
        circle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        circle.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        circle.SetValue(Grid.ColumnProperty, 0);

        var dot = new FrameworkElementFactory(typeof(Ellipse), "dot");
        dot.SetValue(FrameworkElement.WidthProperty, 8.0);
        dot.SetValue(FrameworkElement.HeightProperty, 8.0);
        dot.SetValue(Shape.FillProperty, p.Accent);
        dot.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        dot.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        circle.AppendChild(dot);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(Grid.ColumnProperty, 1);

        grid.AppendChild(circle);
        grid.AppendChild(content);
        template.VisualTree = grid;

        var checkedTrig = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrig.Setters.Add(new Setter(Border.BorderBrushProperty, p.Accent, "circle"));
        checkedTrig.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "dot"));
        template.Triggers.Add(checkedTrig);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}
