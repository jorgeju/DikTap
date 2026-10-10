using DikTap.Services.Settings;

namespace DikTap.Localization;

// UI language without relying on .NET culture data (the app uses invariant globalization):
// the Windows language is read with a native call and the texts live in Str, each with its Spanish and English version.
internal static class Loc
{
    private const string OverrideVariable = "DIKTAP_UI_LANGUAGE";
    private const int PrimaryLangSpanish = 0x0A;

    private static volatile bool _spanish = true;

    public static bool IsSpanish => _spanish;

    public static string Pick(string spanish, string english) => _spanish ? spanish : english;

    public static void Configure(UiLanguage setting)
        => _spanish = ResolveSpanish(setting, NativeMethodsLanguage(), Environment.GetEnvironmentVariable(OverrideVariable));

    // Automatic: Spanish if Windows is in Spanish, English otherwise. The environment variable (es/en) is only used by tests.
    internal static bool ResolveSpanish(UiLanguage setting, int windowsLangId, string? environmentOverride)
    {
        if (setting == UiLanguage.Spanish) return true;
        if (setting == UiLanguage.English) return false;
        if (string.Equals(environmentOverride, "es", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(environmentOverride, "en", StringComparison.OrdinalIgnoreCase)) return false;
        return (windowsLangId & 0x3FF) == PrimaryLangSpanish;
    }

    private static int NativeMethodsLanguage() => Interop.NativeMethods.GetUserDefaultUILanguage();
}
