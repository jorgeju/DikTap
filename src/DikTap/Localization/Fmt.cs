using System.Globalization;

namespace DikTap.Localization;

// The app uses invariant globalization, so numbers are formatted by hand with the decimal comma or point of the chosen language.
internal static class Fmt
{
    private static string Number(double value, string format)
    {
        string s = value.ToString(format, CultureInfo.InvariantCulture);
        return Loc.IsSpanish ? s.Replace('.', ',') : s;
    }

    public static string Usd(double value) => "$" + Number(value, "0.0000");

    public static string Minutes(double seconds) => Number(seconds / 60.0, "0.00");

    public static string Integer(long value) => value.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', Loc.IsSpanish ? '.' : ',');

    public static string Date(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryParseDate(string text, out DateOnly day)
        => DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
}
