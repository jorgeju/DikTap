using System.Windows;
using System.Windows.Controls;
using DikTap.Interop;
using DikTap.Views;

namespace DikTap.Tests;

public class OverlayPlacementTests
{
    private static RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Fact]
    public void Without_a_shell_surface_the_overlay_goes_bottom_center()
    {
        var work = R(0, 0, 1920, 1032);
        var (x, y) = OverlayPlacement.Compute(work, 200, 42, 24, null);
        Assert.Equal((1920 - 200) / 2, x);
        Assert.Equal(1032 - 42 - 24, y);
    }

    [Fact]
    public void Start_menu_over_the_default_spot_moves_the_overlay_just_above_it()
    {
        var work = R(0, 0, 1920, 1032);
        var start = R(640, 300, 1280, 1020);
        var (x, y) = OverlayPlacement.Compute(work, 200, 42, 24, start);
        Assert.Equal((1920 - 200) / 2, x);
        Assert.Equal(300 - 42 - 24, y);
        Assert.True(y + 42 <= start.Top);
    }

    [Fact]
    public void Shell_surface_that_does_not_overlap_leaves_the_default_position()
    {
        var work = R(0, 0, 1920, 1032);
        var notifications = R(1500, 0, 1920, 600);
        var (_, y) = OverlayPlacement.Compute(work, 200, 42, 24, notifications);
        Assert.Equal(1032 - 42 - 24, y);
    }

    [Fact]
    public void When_above_is_blocked_moves_to_bottom_right()
    {
        var work = R(0, 0, 1920, 1032);
        // Tall vertical obstacle in the center reaching the top of the screen
        var obstacle = R(600, 0, 1300, 1032);
        var (x, y) = OverlayPlacement.Compute(work, 200, 42, 24, obstacle);
        Assert.Equal(1920 - 200 - 24, x);
        Assert.Equal(1032 - 42 - 24, y);
    }

    [Fact]
    public void When_above_and_bottom_right_are_blocked_moves_to_top_right()
    {
        var work = R(0, 0, 1920, 1032);
        // Obstacle covering the center and bottom-right corner
        var obstacle = R(600, 800, 1920, 1032);
        var (x, y) = OverlayPlacement.Compute(work, 200, 42, 24, obstacle);
        // Above 800 (y=800-42-24=734) is free, so Priority 1 places it just above
        Assert.Equal((1920 - 200) / 2, x);
        Assert.Equal(800 - 42 - 24, y);
    }

    [Fact]
    public void When_center_and_right_are_blocked_up_to_top_moves_to_left()
    {
        var work = R(0, 0, 1920, 1032);
        // Obstacle covering center and right from top to bottom
        var obstacle = R(600, 0, 1920, 1032);
        var (x, y) = OverlayPlacement.Compute(work, 200, 42, 24, obstacle);
        // Left side free: priority 4 (bottom left)
        Assert.Equal(24, x);
        Assert.Equal(1032 - 42 - 24, y);
    }

    [Fact]
    public void Shell_surface_that_fills_the_screen_falls_back_to_default_position()
    {
        var work = R(0, 0, 1920, 1032);
        var (x, y) = OverlayPlacement.Compute(work, 200, 42, 24, R(0, 0, 1920, 1032));
        Assert.Equal((1920 - 200) / 2, x);
        Assert.Equal(1032 - 42 - 24, y);
    }

    [Fact]
    public void Position_never_leaves_the_work_area()
    {
        var work = R(-1920, 0, 0, 1032);
        var (x, y) = OverlayPlacement.Compute(work, 4000, 42, 24, null);
        Assert.Equal(work.Left, x);
        Assert.InRange(y, work.Top, work.Bottom);
    }

    [Theory]
    [InlineData("StartMenuExperienceHost", "Windows.UI.Core.CoreWindow", true)]
    [InlineData("SearchHost", "Windows.UI.Core.CoreWindow", true)]
    [InlineData("ShellExperienceHost", "Windows.UI.Core.CoreWindow", true)]
    [InlineData("explorer", "XamlExplorerHostIslandWindow", true)]
    [InlineData("explorer", "Windows.UI.Core.CoreWindow", true)]
    [InlineData("explorer", "CabinetWClass", false)]
    [InlineData("notepad", "Notepad", false)]
    [InlineData("chrome", "Chrome_WidgetWin_1", false)]
    [InlineData(null, "Windows.UI.Core.CoreWindow", false)]
    public void Shell_surfaces_are_recognised_by_process_and_class(string? process, string? cls, bool expected)
    {
        Assert.Equal(expected, OverlayPlacement.IsShellSurface(process, cls));
    }
}

// Measures the Settings layout without showing any window (does not touch focus).
public class SettingsLayoutTests
{
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception("fallo en el hilo STA", error);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    [Fact]
    public void A_very_long_pasted_key_does_not_push_the_save_and_delete_buttons_out_of_view()
    {
        OnSta(() =>
        {
            var window = new SettingsWindow(null!, firstRun: true);
            var passwordBox = (PasswordBox)typeof(SettingsWindow)
                .GetField("_keyBox", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            passwordBox.Password = "sk-proj-" + new string('A', 2000);

            const double width = 560;
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(width, double.PositiveInfinity));
            content.Arrange(new Rect(0, 0, width, content.DesiredSize.Height));
            content.UpdateLayout();

            var buttons = Descendants<Button>(content).Where(b => b.Content is "Guardar clave" or "Eliminar").ToList();
            Assert.Equal(2, buttons.Count);
            foreach (var b in buttons)
            {
                double right = b.TranslatePoint(new Point(b.ActualWidth, 0), content).X;
                Assert.True(right <= width, $"el botón «{b.Content}» termina en x={right:F0}, fuera de los {width} px disponibles");
            }
            Assert.True(passwordBox.ActualWidth <= width, $"el campo mide {passwordBox.ActualWidth:F0} px");
        });
    }
}
