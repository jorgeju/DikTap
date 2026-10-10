using System.Diagnostics;
using Microsoft.Win32;
using DikTap.Services.Uninstall;

namespace DikTap.Tests;

// Runs the real cleanup command (hidden cmd.exe) on temporary folders with a fake EXE; does not touch the real installation.
public class SelfUninstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oai-uninstall-" + Guid.NewGuid().ToString("N"));
    private string App => Path.Combine(_root, "app");
    private string Exe => Path.Combine(App, "DikTap.exe");
    private string Data => Path.Combine(_root, "data");
    private string Extract => Path.Combine(_root, "extract");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private void Populate(bool packaged)
    {
        Directory.CreateDirectory(App);
        File.WriteAllText(Exe, "fake");
        if (packaged)
        {
            File.WriteAllText(Path.Combine(App, "VERSION.txt"), "DikTap 1.0.0\r\n");
            File.WriteAllText(Path.Combine(App, "README.md"), "x");
            File.WriteAllText(Path.Combine(App, "README.es.md"), "x");
            File.WriteAllText(Path.Combine(App, "LICENSE"), "x");
            File.WriteAllText(Path.Combine(App, "THIRD-PARTY-NOTICES.md"), "x");
            Directory.CreateDirectory(Path.Combine(App, "LICENSES"));
            File.WriteAllText(Path.Combine(App, "LICENSES", "a.txt"), "x");
        }
        else
        {
            File.WriteAllText(Path.Combine(App, "notas-del-usuario.txt"), "no tocar");
        }
        Directory.CreateDirectory(Path.Combine(Data, "logs"));
        File.WriteAllText(Path.Combine(Data, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(Data, "settings.json.bad"), "x");
        File.WriteAllText(Path.Combine(Data, "logs", "app.log"), "x");
        Directory.CreateDirectory(Extract);
        File.WriteAllText(Path.Combine(Extract, "wpfgfx.dll"), "x");
    }

    private bool RunCleanup(bool keepSettings, int timeoutSeconds = 40)
    {
        string? command = SelfUninstaller.BuildCleanupCommand(new UninstallPaths(Exe, Data, Extract), keepSettings);
        Assert.NotNull(command);
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /s /c \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        })!;
        return p.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds));
    }

    [Fact]
    public void Winget_installs_are_detected_and_run_winget_uninstall_in_the_cleanup()
    {
        string wingetExe = @"C:\Users\x\AppData\Local\Microsoft\WinGet\Packages\jorgeju.DikTap_Microsoft.Winget.Source_8wekyb3d8bbwe\DikTap.exe";
        Assert.True(SelfUninstaller.IsWingetInstall(wingetExe));
        Assert.False(SelfUninstaller.IsWingetInstall(@"C:\Tools\DikTap\DikTap.exe"));
        Assert.False(SelfUninstaller.IsWingetInstall(@"C:\Users\x\AppData\Local\Microsoft\WinGet\Packages\other.App_Source\DikTap.exe"));

        string? winget = SelfUninstaller.BuildCleanupCommand(new UninstallPaths(wingetExe, Data, Extract), keepSettings: false);
        string? manual = SelfUninstaller.BuildCleanupCommand(new UninstallPaths(Exe, Data, Extract), keepSettings: false);
        Assert.Contains("winget uninstall --id jorgeju.DikTap", winget);
        Assert.DoesNotContain("winget", manual);
    }

    [Fact]
    public void Full_uninstall_removes_exe_package_files_extracted_libraries_and_all_data()
    {
        Populate(packaged: true);
        Assert.True(RunCleanup(keepSettings: false));

        Assert.False(Directory.Exists(App));
        Assert.False(Directory.Exists(Data));
        Assert.False(Directory.Exists(Extract));
    }

    [Fact]
    public void Keep_settings_variant_leaves_only_settings_json()
    {
        Populate(packaged: true);
        Assert.True(RunCleanup(keepSettings: true));

        Assert.False(Directory.Exists(App));
        Assert.False(Directory.Exists(Extract));
        Assert.Equal(new[] { Path.Combine(Data, "settings.json") }, Directory.GetFileSystemEntries(Data));
    }

    [Fact]
    public void Keep_settings_variant_still_cleans_logs_when_settings_json_is_the_only_file()
    {
        Populate(packaged: true);
        File.Delete(Path.Combine(Data, "settings.json.bad"));
        Assert.True(RunCleanup(keepSettings: true));

        Assert.Equal(new[] { Path.Combine(Data, "settings.json") }, Directory.GetFileSystemEntries(Data));
        Assert.False(Directory.Exists(App));
    }

    [Fact]
    public void Folder_that_is_not_the_distribution_package_keeps_the_users_files()
    {
        Populate(packaged: false);
        Assert.True(RunCleanup(keepSettings: false));

        Assert.False(File.Exists(Exe));
        Assert.True(File.Exists(Path.Combine(App, "notas-del-usuario.txt")));
        Assert.False(Directory.Exists(Data));
        Assert.False(Directory.Exists(Extract));
    }

    [Fact]
    public void Waits_for_the_exe_to_be_released_before_deleting_anything_else()
    {
        Populate(packaged: true);
        // A handle opened without delete sharing simulates the process still shutting down.
        var locked = new FileStream(Exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        string? command = SelfUninstaller.BuildCleanupCommand(new UninstallPaths(Exe, Data, Extract), keepSettings: false);
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /s /c \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        })!;

        Thread.Sleep(2500);
        Assert.False(p.HasExited);
        Assert.True(File.Exists(Path.Combine(Data, "settings.json"))); // nothing has been touched yet
        locked.Dispose();

        Assert.True(p.WaitForExit(TimeSpan.FromSeconds(30)));
        Assert.False(Directory.Exists(App));
        Assert.False(Directory.Exists(Data));
    }

    [Theory]
    [InlineData(@"C:\Users\%USERNAME%\app\DikTap.exe")]
    [InlineData(@"relativa\DikTap.exe")]
    public void Unsafe_or_relative_exe_paths_are_rejected(string exe)
    {
        Assert.Null(SelfUninstaller.BuildCleanupCommand(new UninstallPaths(exe, Data, Extract), false));
    }

    [Fact]
    public void Paths_with_spaces_and_parentheses_are_supported()
    {
        string exe = Path.Combine(_root, "Program Files (x86)", "Mi App & Co", "DikTap.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "fake");
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, "settings.json"), "{}");

        string command = SelfUninstaller.BuildCleanupCommand(new UninstallPaths(exe, Data, Extract), false)!;
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /s /c \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        })!;
        Assert.True(p.WaitForExit(TimeSpan.FromSeconds(30)));
        Assert.False(File.Exists(exe));
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public void Windows_traces_are_removed_only_for_this_executable()
    {
        string sandbox = @"Software\DikTap.Tests\" + Guid.NewGuid().ToString("N");
        string icons = sandbox + @"\Icons", mic = sandbox + @"\Mic";
        try
        {
            using (var i = Registry.CurrentUser.CreateSubKey(icons))
            {
                i.CreateSubKey("111")!.SetValue("ExecutablePath", Exe);
                i.CreateSubKey("222")!.SetValue("ExecutablePath", @"C:\otra\app.exe");
            }
            using (var m = Registry.CurrentUser.CreateSubKey(mic))
            {
                m.CreateSubKey(Exe.Replace('\\', '#'))!.Close();
                m.CreateSubKey(@"C:#otra#app.exe")!.Close();
            }

            Assert.Equal(2, SelfUninstaller.RemoveWindowsTraces(Exe, icons, mic));

            using var i2 = Registry.CurrentUser.OpenSubKey(icons)!;
            using var m2 = Registry.CurrentUser.OpenSubKey(mic)!;
            Assert.Equal(new[] { "222" }, i2.GetSubKeyNames());
            Assert.Equal(new[] { "C:#otra#app.exe" }, m2.GetSubKeyNames());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\DikTap.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Missing_registry_keys_are_not_an_error()
    {
        Assert.Equal(0, SelfUninstaller.RemoveWindowsTraces(Exe, @"Software\DikTap.Tests\no-existe-a", @"Software\DikTap.Tests\no-existe-b"));
    }
}
