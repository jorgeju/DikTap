namespace DikTap;

internal static class AppInfo
{
    public const string Name = "DikTap";
    public const string TranscriptionModel = "gpt-transcribe";
    public const string Author = "Jorge Juan Cantón";

    public const string RepositoryUrl = "https://github.com/jorgeju/DikTap";
    public const string BugReportUrl = RepositoryUrl + "/issues/new?template=bug_report.yml";
    public const string FeatureRequestUrl = RepositoryUrl + "/issues/new?template=feature_request.yml";
    public const string LicenseUrl = RepositoryUrl + "/blob/main/LICENSE";

    // Without the "+commit" suffix the SDK appends to the informational version.
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    // With an alternate data directory (tests), mutex, event, credential and startup entry are isolated from the real installation.
    private static readonly string Suffix = AppPaths.IsCustomDataDirectory
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.DataDirectory)))[..8]
        : string.Empty;

    public static string RunValueName { get; } = "DikTap" + Suffix;

    public static string MutexName { get; } = @"Local\DikTap.SingleInstance" + Suffix;
    public static string ShowSettingsEventName { get; } = @"Local\DikTap.ShowSettings" + Suffix;
    public static string CredentialTarget { get; } = "DikTap/OpenAI API key" + Suffix;
}

internal static class AppPaths
{
    // The environment variable isolates data in tests and measurements.
    public static bool IsCustomDataDirectory { get; } =
        Environment.GetEnvironmentVariable("DIKTAP_DATA_DIR") is { Length: > 0 };

    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("DIKTAP_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string UsageFile => Path.Combine(DataDirectory, "usage.jsonl");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}

// Local technical log: no dictated content, keys or responses. Bounded size (2 MB + 1 rotated copy).
internal static class Logger
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} [{ex.GetType().Name} 0x{ex.HResult:X8}]");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                string file = Path.Combine(AppPaths.LogDirectory, "app.log");
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                {
                    string old = file + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(file, old);
                }
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the flow.
        }
    }
}
