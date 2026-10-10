using System.Diagnostics;
using Microsoft.Win32;

namespace DikTap.Services.Uninstall;

internal sealed record UninstallPaths(string ExePath, string DataDirectory, string ExtractDirectory);

// Uninstall from within the application itself: the credential and auto-start are removed in-process; what stays
// locked while it runs (the EXE, its extracted libraries and the data directory the log reopens) is deleted by
// a hidden cmd.exe once the process has already exited. It leaves no auxiliary file behind.
internal static class SelfUninstaller
{
    public const string SettingsFileName = "settings.json";
    public const string WingetPackageId = "jorgeju.DikTap";
    public const string NotifyIconsKey = @"Control Panel\NotifyIconSettings";
    public const string MicrophoneConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged";

    // With DOTNET_BUNDLE_EXTRACT_BASE_DIR (tests or machines that define it) the self-extracting EXE uses another base folder.
    public static UninstallPaths CurrentPaths(string exePath)
    {
        string baseDir = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Path.GetTempPath(), ".net");
        return new(exePath, AppPaths.DataDirectory, Path.Combine(baseDir, "DikTap"));
    }

    // winget extracts portable packages to ...\WinGet\Packages\<PackageId>_<source>\ (user or machine scope).
    public static bool IsWingetInstall(string exePath)
        => exePath.Contains($"\\WinGet\\Packages\\{WingetPackageId}_", StringComparison.OrdinalIgnoreCase);

    // Returns null if any path cannot be safely passed to cmd.exe (for example, it contains %).
    internal static string? BuildCleanupCommand(UninstallPaths p, bool keepSettings)
    {
        if (string.IsNullOrWhiteSpace(p.ExePath) || !Path.IsPathFullyQualified(p.ExePath)) return null;
        if (new[] { p.ExePath, p.DataDirectory, p.ExtractDirectory }.Any(x => x.Contains('%') || x.Contains('"'))) return null;

        string exe = $"\"{p.ExePath}\"";
        string dir = Path.GetDirectoryName(p.ExePath)!.TrimEnd('\\');
        var cleanup = new List<string>();

        // Installed by winget: let it drop the package, its alias and its Apps entry too (--force: the EXE is already gone).
        if (IsWingetInstall(p.ExePath))
            cleanup.Add($"winget uninstall --id {WingetPackageId} --exact --source winget --silent --disable-interactivity --force >nul 2>&1");

        // Only if the EXE folder is the distribution ZIP's (this application's VERSION.txt) are the files it ships deleted.
        string version = $"\"{dir}\\VERSION.txt\"";
        cleanup.Add($"findstr /b /c:\"{AppInfo.Name}\" {version} >nul 2>&1 && (del /f /q \"{dir}\\README.md\" \"{dir}\\README.es.md\" \"{dir}\\LICENSE\" {version} \"{dir}\\THIRD-PARTY-NOTICES.md\" >nul 2>&1 & rd /s /q \"{dir}\\LICENSES\" >nul 2>&1 & rd \"{dir}\" >nul 2>&1)");

        cleanup.Add($"rd /s /q \"{p.ExtractDirectory}\" >nul 2>&1");

        string data = p.DataDirectory.TrimEnd('\\');
        if (keepSettings)
        {
            // In parentheses: without them, `if`/`for` swallow the commands chained with & that follow.
            cleanup.Add($"(for %f in (\"{data}\\*\") do @if /i not \"%~nxf\"==\"{SettingsFileName}\" del /f /q \"%f\" >nul 2>&1)");
            cleanup.Add($"rd /s /q \"{data}\\logs\" >nul 2>&1");
        }
        else
        {
            cleanup.Add($"rd /s /q \"{data}\" >nul 2>&1");
        }

        // Waits (up to 60 s) for the EXE to become deletable; if that fails, touches nothing else.
        return $"for /l %n in (1,1,60) do @if exist {exe} (del /f /q {exe} >nul 2>&1 & ping -n 2 127.0.0.1 >nul) else ({string.Join(" & ", cleanup)} & exit)";
    }

    public static bool LaunchCleanup(UninstallPaths p, bool keepSettings)
    {
        string? command = BuildCleanupCommand(p, keepSettings);
        if (command is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/d /s /c \"{command}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                // No application directory must remain in use by the helper process.
                WorkingDirectory = Environment.SystemDirectory,
            })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not launch the final uninstall cleanup", ex);
            return false;
        }
    }

    // Windows 11 remembers every tray icon and every application that has used the microphone; these are HKCU traces that can be deleted.
    public static int RemoveWindowsTraces(string exePath, string notifyIconsKey = NotifyIconsKey, string microphoneKey = MicrophoneConsentKey)
    {
        int removed = 0;
        try
        {
            using var icons = Registry.CurrentUser.OpenSubKey(notifyIconsKey, writable: true);
            if (icons is not null)
            {
                foreach (string name in icons.GetSubKeyNames())
                {
                    string? path;
                    using (var sub = icons.OpenSubKey(name)) path = sub?.GetValue("ExecutablePath") as string;
                    if (string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        icons.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                        removed++;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Logger.Error("Could not clean the tray icon registry", ex);
        }

        try
        {
            using var consent = Registry.CurrentUser.OpenSubKey(microphoneKey, writable: true);
            string wanted = exePath.Replace('\\', '#');
            if (consent is not null)
            {
                foreach (string name in consent.GetSubKeyNames())
                {
                    if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    consent.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                    removed++;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Logger.Error("Could not clean the microphone usage registry", ex);
        }
        return removed;
    }
}
