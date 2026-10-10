using System.Runtime.InteropServices;
using Microsoft.Win32;
using DikTap;
using DikTap.Interop;
using DikTap.Services.Credentials;
using DikTap.Services.Hotkeys;
using DikTap.Services.Settings;
using DikTap.Services.Startup;

namespace DikTap.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oai-dict-tests-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Missing_file_returns_defaults_with_agreed_values()
    {
        var s = new JsonSettingsStore(File_).Load();
        Assert.Equal(HotkeyMode.WinH, s.HotkeyMode);
        Assert.Equal(ClipboardMode.Restore, s.ClipboardMode);
        Assert.True(s.StartWithWindows);
        Assert.False(s.FirstRunCompleted);
        Assert.Null(s.MicrophoneId);
        Assert.Equal(string.Empty, s.Languages);
        Assert.Equal(string.Empty, s.Keywords);
        Assert.Equal(string.Empty, s.Context);
    }

    [Fact]
    public void Round_trip_preserves_values_and_never_contains_secrets()
    {
        var store = new JsonSettingsStore(File_);
        var s = new AppSettings { HotkeyMode = HotkeyMode.WinH, MicrophoneId = "{0.0.1.00000000}.{abc}", Keywords = "a, b", ClipboardMode = ClipboardMode.KeepWithHistory, FirstRunCompleted = true };
        store.Save(s);
        Assert.Equal(s, store.Load());
        string json = System.IO.File.ReadAllText(File_);
        Assert.Contains("\"WinH\"", json);
        Assert.Contains("\"KeepWithHistory\"", json);
        Assert.DoesNotContain("sk-", json);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Settings_written_by_the_previous_reflection_serializer_still_load()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File_, """
            {
              "Version": 1,
              "HotkeyMode": "CtrlAltSpace",
              "MicrophoneId": null,
              "Languages": "es",
              "Keywords": "Kafka",
              "Context": "x",
              "ClipboardMode": "Keep",
              "StartWithWindows": false,
              "FirstRunCompleted": true,
              "RestoreClipboard": true
            }
            """);
        var s = new JsonSettingsStore(File_).Load();
        Assert.Equal(HotkeyMode.CtrlAltSpace, s.HotkeyMode);
        Assert.Equal(ClipboardMode.Keep, s.ClipboardMode);
        Assert.Equal("es", s.Languages);
        Assert.False(s.StartWithWindows);
        Assert.True(s.FirstRunCompleted);
    }

    [Fact]
    public void Corrupt_file_is_set_aside_and_defaults_returned()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File_, "{ esto no es json");
        var s = new JsonSettingsStore(File_).Load();
        Assert.Equal(new AppSettings(), s);
        Assert.True(System.IO.File.Exists(File_ + ".bad"));
        Assert.False(System.IO.File.Exists(File_));
    }

    [Fact]
    public void Repeated_saves_replace_atomically()
    {
        var store = new JsonSettingsStore(File_);
        for (int i = 0; i < 20; i++) store.Save(new AppSettings { Languages = "l" + i });
        Assert.Equal("l19", store.Load().Languages);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}

public class StartupRegistrationTests : IDisposable
{
    private readonly string _root = @"Software\DikTapTests\" + Guid.NewGuid().ToString("N");
    private string Run => _root + @"\Run";
    private string Approved => _root + @"\Approved";
    private const string Exe = @"C:\Program Files\Mi Carpeta\DikTap.exe";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, false);

    private RunKeyStartupRegistration Make(string exe = Exe) => new(exe, "DikTap Test", Run, Approved);

    [Fact]
    public void Enable_writes_quoted_absolute_path_with_background_flag_in_hkcu()
    {
        var r = Make();
        Assert.Equal(StartupState.NotRegistered, r.GetState());
        r.Enable();
        Assert.Equal(StartupState.Enabled, r.GetState());
        using var k = Registry.CurrentUser.OpenSubKey(Run)!;
        Assert.Equal("\"C:\\Program Files\\Mi Carpeta\\DikTap.exe\" --background", (string)k.GetValue("DikTap Test")!);
    }

    [Fact]
    public void Disable_removes_entry_and_is_idempotent()
    {
        var r = Make();
        r.Enable();
        r.Disable();
        r.Disable();
        Assert.Equal(StartupState.NotRegistered, r.GetState());
    }

    [Fact]
    public void Windows_side_disable_is_detected_and_not_silently_reverted()
    {
        var r = Make();
        r.Enable();
        using (var k = Registry.CurrentUser.CreateSubKey(Approved))
            k.SetValue("DikTap Test", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        Assert.Equal(StartupState.DisabledByWindows, r.GetState());
        Assert.Equal(StartupState.DisabledByWindows, r.GetState()); // querying does not re-enable anything

        r.Enable(); // explicit user action
        Assert.Equal(StartupState.Enabled, r.GetState());
    }

    [Fact]
    public void Approved_flag_with_even_first_byte_means_enabled()
    {
        var r = Make();
        r.Enable();
        using var k = Registry.CurrentUser.CreateSubKey(Approved);
        k.SetValue("DikTap Test", new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        Assert.Equal(StartupState.Enabled, r.GetState());
    }

    [Fact]
    public void Moved_executable_is_reported_as_stale_and_can_be_updated()
    {
        Make(@"D:\Viejo\DikTap.exe").Enable();
        var now = Make(@"E:\Nuevo\DikTap.exe");
        Assert.Equal(StartupState.StalePath, now.GetState());
        now.Enable();
        Assert.Equal(StartupState.Enabled, now.GetState());
    }

    [Fact]
    public void Relative_or_overlong_paths_are_rejected()
    {
        Assert.Throws<DictationException>(() => Make("DikTap.exe").Enable());
        Assert.Throws<DictationException>(() => Make(@"C:\" + new string('a', 300) + ".exe").Enable());
    }

    [Fact]
    public void Does_not_write_outside_the_per_user_run_key()
    {
        Make().Enable();
        Assert.Null(Registry.LocalMachine.OpenSubKey(Run));
    }
}

public class CredentialStoreTests
{
    [Fact]
    public void Round_trip_replace_and_delete_in_windows_credential_manager()
    {
        var store = new WindowsCredentialStore("DikTap Tests/" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(store.HasKey());
            Assert.Null(store.Read());

            store.Save("  sk-proj-ABCDEF1234567890  ");
            Assert.True(store.HasKey());
            Assert.Equal("sk-proj-ABCDEF1234567890", store.Read());

            store.Save("sk-proj-REPLACED0987654321");
            Assert.Equal("sk-proj-REPLACED0987654321", store.Read());

            store.Delete();
            Assert.False(store.HasKey());
            store.Delete(); // idempotent
        }
        finally
        {
            store.Delete();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("sk-has space-inside-0000")]
    [InlineData("sk-ñandú-000000000")]
    public void Invalid_keys_are_rejected_without_touching_the_store(string key)
    {
        var store = new WindowsCredentialStore("DikTap Tests/" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DictationException>(() => store.Save(key));
        Assert.False(store.HasKey());
    }
}

public class InteropLayoutTests
{
    [Fact]
    public void Structs_have_x64_sizes_expected_by_win32()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(40, Marshal.SizeOf<INPUT>());
        Assert.Equal(24, Marshal.SizeOf<KEYBDINPUT>());
        Assert.Equal(24, Marshal.SizeOf<KBDLLHOOKSTRUCT>());
        Assert.Equal(72, Marshal.SizeOf<GUITHREADINFO>());
        Assert.Equal(40, Marshal.SizeOf<MONITORINFO>());
        Assert.Equal(24, Marshal.SizeOf<PROPVARIANT>());
        Assert.Equal(48, Marshal.SizeOf<MSG>());
        Assert.Equal(976, Marshal.SizeOf<NOTIFYICONDATAW>());
    }
}

public class WinHShortcutFilterTests
{
    private const ushort LWin = NativeMethods.VK_LWIN, RWin = NativeMethods.VK_RWIN, H = NativeMethods.VK_H;

    private static KeyDecision Down(WinHShortcutFilter f, ushort vk) => f.OnKey(vk, true, false);
    private static KeyDecision Up(WinHShortcutFilter f, ushort vk) => f.OnKey(vk, false, false);

    [Theory]
    [InlineData(LWin)]
    [InlineData(RWin)]
    public void Win_plus_H_toggles_once_swallows_h_and_masks_start_menu(ushort win)
    {
        var f = new WinHShortcutFilter();
        Assert.False(Down(f, win).Swallow);                 // Win passes to the system
        var d = Down(f, H);
        Assert.True(d.Swallow);
        Assert.True(d.Toggle);
        Assert.True(d.InjectMask);
        // H auto-repeat: swallowed without new toggles
        for (int i = 0; i < 5; i++)
        {
            var r = Down(f, H);
            Assert.True(r.Swallow);
            Assert.False(r.Toggle);
            Assert.False(r.InjectMask);
        }
        var hu = Up(f, H);
        Assert.True(hu.Swallow);                            // no stray H left behind
        Assert.False(hu.Toggle);
        Assert.False(Up(f, win).Swallow);                   // Win up passes (after the mask was already injected)
    }

    [Fact]
    public void Second_press_in_same_win_hold_toggles_again_without_second_mask()
    {
        var f = new WinHShortcutFilter();
        Down(f, LWin);
        Assert.True(Down(f, H).InjectMask);
        Up(f, H);
        var d = Down(f, H);
        Assert.True(d.Toggle);
        Assert.False(d.InjectMask);
        Up(f, H);
        Up(f, LWin);
        Down(f, LWin);
        Assert.True(Down(f, H).InjectMask); // new Win press: new mask
    }

    [Theory]
    [InlineData(NativeMethods.VK_LSHIFT)]
    [InlineData(NativeMethods.VK_RSHIFT)]
    [InlineData(NativeMethods.VK_LCONTROL)]
    [InlineData(NativeMethods.VK_RCONTROL)]
    [InlineData(NativeMethods.VK_LMENU)]
    [InlineData(NativeMethods.VK_RMENU)]
    public void Win_plus_other_modifier_plus_H_is_left_alone(ushort modifier)
    {
        var f = new WinHShortcutFilter();
        Down(f, LWin);
        Down(f, modifier);
        Assert.Equal(KeyDecision.Pass, Down(f, H));
        Assert.Equal(KeyDecision.Pass, Up(f, H));
    }

    [Fact]
    public void Plain_H_and_other_keys_are_untouched()
    {
        var f = new WinHShortcutFilter();
        Assert.Equal(KeyDecision.Pass, Down(f, H));
        Assert.Equal(KeyDecision.Pass, Up(f, H));
        Down(f, LWin);
        Assert.Equal(KeyDecision.Pass, Down(f, 0x45)); // Win+E
        Assert.Equal(KeyDecision.Pass, Up(f, 0x45));
        Assert.Equal(KeyDecision.Pass, Up(f, LWin));
    }

    [Fact]
    public void Releasing_win_before_h_still_swallows_h_release_and_repeats()
    {
        var f = new WinHShortcutFilter();
        Down(f, LWin);
        Assert.True(Down(f, H).Toggle);
        Up(f, LWin);
        var rep = Down(f, H);
        Assert.True(rep.Swallow);
        Assert.False(rep.Toggle);
        Assert.True(Up(f, H).Swallow);
        Assert.Equal(KeyDecision.Pass, Up(f, H)); // an unpaired keyup is no longer swallowed
    }

    [Fact]
    public void Own_injected_events_are_ignored_even_for_h()
    {
        var f = new WinHShortcutFilter();
        Assert.Equal(KeyDecision.Pass, f.OnKey(LWin, true, true));
        Assert.Equal(KeyDecision.Pass, f.OnKey(H, true, true));
    }

    [Fact]
    public void Async_probe_clears_stale_modifier_state()
    {
        bool shift = true;
        var f = new WinHShortcutFilter(vk => vk switch
        {
            LWin => true,
            NativeMethods.VK_LSHIFT => shift,
            _ => false,
        });
        Assert.Equal(KeyDecision.Pass, Down(f, H));        // real Win+Shift+H
        Up(f, H);
        shift = false;                                      // Shift was released without the hook seeing the event
        Assert.True(Down(f, H).Toggle);
    }
}
