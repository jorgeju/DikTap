using Microsoft.Win32;
using DikTap.Localization;

namespace DikTap.Services.Startup;

internal enum StartupState
{
    NotRegistered,
    Enabled,
    // Registered in Run but disabled from Startup apps / Task Manager.
    DisabledByWindows,
    // Registered with the path of another executable (e.g. the portable folder was moved).
    StalePath,
}

internal interface IStartupRegistration
{
    StartupState GetState();
    void Enable();
    void Disable();
}

// Startup with the user session via HKCU\...\Run. Does not use the Startup folder or scheduled tasks.
internal sealed class RunKeyStartupRegistration : IStartupRegistration
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const int MaxCommandLength = 260;

    private readonly string _runKey, _approvedKey, _valueName, _exePath;

    public RunKeyStartupRegistration(string exePath, string? valueName = null, string runKey = DefaultRunKey, string approvedKey = DefaultApprovedKey)
    {
        _exePath = exePath;
        _valueName = valueName ?? AppInfo.RunValueName;
        _runKey = runKey;
        _approvedKey = approvedKey;
    }

    public string Command => $"\"{_exePath}\" --background";

    public StartupState GetState()
    {
        using var run = Registry.CurrentUser.OpenSubKey(_runKey);
        if (run?.GetValue(_valueName) is not string current || current.Length == 0) return StartupState.NotRegistered;
        if (!string.Equals(current, Command, StringComparison.OrdinalIgnoreCase)) return StartupState.StalePath;

        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey);
        // Windows stores 12 bytes: even first byte = enabled (02/06), odd = disabled by the user (03/07).
        if (approved?.GetValue(_valueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1)
            return StartupState.DisabledByWindows;
        return StartupState.Enabled;
    }

    public void Enable()
    {
        if (string.IsNullOrWhiteSpace(_exePath) || !Path.IsPathFullyQualified(_exePath))
            throw new DictationException(Str.ExePathUnknown, ErrorKind.Configuration);
        if (Command.Length > MaxCommandLength)
            throw new DictationException(Str.ExePathTooLong, ErrorKind.Configuration);

        using (var run = Registry.CurrentUser.CreateSubKey(_runKey, writable: true))
            run.SetValue(_valueName, Command, RegistryValueKind.String);

        // Explicit user action: removes a previous disabling by Windows.
        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey, writable: true);
        approved?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    public void Disable()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(_runKey, writable: true))
            run?.DeleteValue(_valueName, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey, writable: true);
        approved?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
