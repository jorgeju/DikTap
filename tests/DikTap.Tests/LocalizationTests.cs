using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DikTap.Localization;
using DikTap.Services.Hotkeys;
using DikTap.Services.Settings;
using DikTap.Views;

// Loc is global state: these tests cannot run in parallel with others that read texts.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DikTap.Tests;

// Sets Spanish as the language for the existing tests, which check Spanish texts.
internal static class TestLanguageInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => Loc.Configure(UiLanguage.Spanish);
}

public class LocalizationTests : IDisposable
{
    public LocalizationTests() => Loc.Configure(UiLanguage.Spanish);
    public void Dispose() => Loc.Configure(UiLanguage.Spanish);

    // English and Spanish with the same calls, to cover all Str texts.
    private static IEnumerable<(string Name, string Value)> AllTexts()
    {
        foreach (var p in typeof(Str).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string)))
            yield return (p.Name, (string)p.GetValue(null)!);
        foreach (var m in typeof(Str).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.ReturnType == typeof(string) && !m.IsSpecialName))
        {
            var args = m.GetParameters().Select(p => p.ParameterType == typeof(string) ? "x"
                : p.ParameterType == typeof(int) ? (object)1
                : p.ParameterType == typeof(double) ? 1.0
                : p.ParameterType == typeof(bool) ? true
                : "x").ToArray();
            yield return (m.Name, (string)m.Invoke(null, args)!);
        }
    }

    // Language names and bilingual labels that display the same in both languages.
    private static readonly HashSet<string> SameInBothLanguages = ["LanguageSpanish", "LanguageEnglish", "HdrLanguage"];

    [Fact]
    public void English_texts_never_contain_Spanish_punctuation_or_accents()
    {
        Loc.Configure(UiLanguage.English);
        foreach (var (name, value) in AllTexts().Where(t => !SameInBothLanguages.Contains(t.Name)))
        {
            Assert.False(value.Any(c => "áéíóúñÁÉÍÓÚÑ¿¡«»".Contains(c)), $"{name}: «{value}» parece estar en español");
            Assert.False(string.IsNullOrWhiteSpace(value), $"{name} está vacío");
        }
    }

    [Fact]
    public void Every_text_differs_between_languages_except_language_names()
    {
        Loc.Configure(UiLanguage.Spanish);
        var es = AllTexts().ToDictionary(t => t.Name, t => t.Value);
        Loc.Configure(UiLanguage.English);
        var en = AllTexts().ToDictionary(t => t.Name, t => t.Value);

        Assert.Equal(es.Keys.OrderBy(k => k), en.Keys.OrderBy(k => k));
        foreach (var name in es.Keys.Where(k => !SameInBothLanguages.Contains(k)))
        {
            // plain "Win+H" (main shortcut with no fallback) is inherently the same in both languages.
            if (name == nameof(Str.HotkeyDescription)) continue;
            Assert.NotEqual(es[name], en[name]);
        }
    }

    [Theory]
    [InlineData("Spanish", 0x0409, null, true)]
    [InlineData("English", 0x0C0A, null, false)]
    [InlineData("Auto", 0x0C0A, null, true)]   // es-ES
    [InlineData("Auto", 0x080A, null, true)]   // es-MX
    [InlineData("Auto", 0x0409, null, false)]  // en-US
    [InlineData("Auto", 0x040C, null, false)]  // fr-FR: any other language uses English
    [InlineData("Auto", 0x0409, "es", true)]
    [InlineData("Auto", 0x0C0A, "EN", false)]
    [InlineData("English", 0x0C0A, "es", false)] // the setting takes precedence over the environment
    public void Language_resolution(string setting, int langId, string? env, bool expectSpanish)
        => Assert.Equal(expectSpanish, Loc.ResolveSpanish(Enum.Parse<UiLanguage>(setting), langId, env));

    [Fact]
    public void Hotkey_description_follows_the_current_language_without_reapplying_the_hotkey()
    {
        var result = new HotkeyApplyResult(HotkeyMode.CtrlAltSpace, false, true, null);
        Loc.Configure(UiLanguage.Spanish);
        Assert.Equal("Ctrl+Alt+Espacio", result.Description);
        Loc.Configure(UiLanguage.English);
        Assert.Equal("Ctrl+Alt+Space", result.Description);
    }

    [Fact]
    public void Language_setting_round_trips_through_json_and_defaults_to_auto()
    {
        Assert.Equal(UiLanguage.Auto, new AppSettings().Language);
        var path = Path.Combine(Path.GetTempPath(), "diktap-lang-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonSettingsStore(path);
            store.Save(new AppSettings { Language = UiLanguage.English });
            Assert.Equal(UiLanguage.English, new JsonSettingsStore(path).Load().Language);
            Assert.Contains("English", File.ReadAllText(path));

            // A file from before this version (without the field) still loads with Auto.
            File.WriteAllText(path, "{\"FirstRunCompleted\":true}");
            Assert.Equal(UiLanguage.Auto, new JsonSettingsStore(path).Load().Language);
        }
        finally { File.Delete(path); }
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception("fallo en el hilo STA", error);
    }

    private static IEnumerable<string> VisibleTexts(DependencyObject root)
    {
        if (root is TextBlock tb && !string.IsNullOrEmpty(tb.Text)) yield return tb.Text;
        if (root is ContentControl { Content: string s } && s.Length > 0) yield return s;
        if (root is ComboBox cb) foreach (var item in cb.Items.OfType<string>()) yield return item;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var text in VisibleTexts(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return text;
    }

    private static UIElement Build(SettingsWindow window)
        => (UIElement)typeof(SettingsWindow).GetMethod("BuildContent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;

    private static void Layout(UIElement content)
    {
        var fe = (FrameworkElement)content;
        fe.Measure(new Size(560, double.PositiveInfinity));
        fe.Arrange(new Rect(0, 0, 560, fe.DesiredSize.Height));
        fe.UpdateLayout();
    }

    [Fact]
    public void Settings_window_is_fully_English_and_can_be_rebuilt_in_another_language()
    {
        OnSta(() =>
        {
            Loc.Configure(UiLanguage.Spanish);
            var window = new SettingsWindow(null!, firstRun: true);
            Assert.Contains("Configuración", window.Title);

            // Second construction in another language: stateful controls are reused and must not fail by already having a parent.
            Loc.Configure(UiLanguage.English);
            var content = Build(window);
            window.Content = content;
            Layout(content);

            Assert.Contains("Settings", Str.SettingsTitle);
            var texts = VisibleTexts(content).ToList();
            Assert.Contains("Save", texts);
            Assert.Contains(Str.HdrLanguage, texts);
            Assert.Contains("English", texts);
            foreach (var text in texts.Where(t => t != "Español" && t != Str.HdrLanguage && !t.Contains(AppInfo.Author)))
                Assert.False(text.Any(c => "áéíóúñÁÉÍÓÚÑ¿¡«»".Contains(c)), $"texto en español en la ventana: «{text}»");
        });
    }
}
