using System.Text.Json;
using System.Text.Json.Serialization;

namespace DikTap.Services.Settings;

internal enum HotkeyMode
{
    WinH,
    CtrlAltSpace,
}

// What remains on the clipboard after pasting the dictation.
internal enum ClipboardMode
{
    // What was there before is returned; the dictation stays neither on the clipboard nor in the history (Win+V).
    Restore,
    // The dictation stays as current content (Ctrl+V repeats it) but does not enter the history.
    Keep,
    // The dictation stays as current content and also enters the history (Win+V).
    KeepWithHistory,
}

// UI language: Auto follows Windows.
internal enum UiLanguage
{
    Auto,
    Spanish,
    English,
}

internal sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public HotkeyMode HotkeyMode { get; init; } = HotkeyMode.WinH;
    // null = Windows default; otherwise, stable endpoint identifier (not the index).
    public string? MicrophoneId { get; init; }
    // Empty by default: without hints, the API detects the language by itself.
    public string Languages { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string Context { get; init; } = string.Empty;
    public ClipboardMode ClipboardMode { get; init; } = ClipboardMode.Restore;
    public UiLanguage Language { get; init; } = UiLanguage.Auto;
    public bool StartWithWindows { get; init; } = true;
    public bool SingleClickCopiesLast { get; init; } = true;
    public bool FirstRunCompleted { get; init; }
}

internal interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

// Serializer generated at compile time: no reflection or runtime-emitted code at startup.
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

internal sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;

    public JsonSettingsStore(string path) => _path = path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            Logger.Error("Invalid settings.json; using defaults", ex);
            try { File.Move(_path, _path + ".bad", overwrite: true); } catch (IOException) { }
            return new AppSettings();
        }
        catch (IOException ex)
        {
            Logger.Error("Could not read settings.json", ex);
            return new AppSettings();
        }
    }

    // Atomic write: temp file in the same directory, then replace.
    public void Save(AppSettings settings)
    {
        string dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, $"settings.{Environment.ProcessId}.tmp");
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
