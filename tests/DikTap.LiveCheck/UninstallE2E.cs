using System.Diagnostics;
using System.Windows.Automation;
using DikTap.Services.Credentials;
using DikTap.Services.Settings;

namespace DikTap.LiveCheck;

// Real uninstall from the published EXE's Settings, in an isolated folder and profile (does not touch the real installation).
// Opens windows and drives them with UI Automation: do not use the computer while it runs.
internal static class UninstallE2E
{
    public static int Run(string exe, bool keepSettings)
    {
        string root = Path.Combine(Path.GetTempPath(), "oaid-uninst-" + Guid.NewGuid().ToString("N")[..8]);
        string app = Path.Combine(root, "app");
        string extract = Path.Combine(root, "extract");
        Directory.CreateDirectory(app);
        string exeCopy = Path.Combine(app, "DikTap.exe");
        File.Copy(exe, exeCopy);
        File.WriteAllText(Path.Combine(app, "VERSION.txt"), "DikTap 1.0.0\r\n");
        File.WriteAllText(Path.Combine(app, "README.md"), "x");

        string data = Profile.Create(true);
        string credTarget = AppInfo.CredentialTarget;
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "OK" : "FALLO")}] {what}");
            if (!ok) failures++;
        }

        Process? proc = null;
        try
        {
            Console.WriteLine($"Variante: {(keepSettings ? "conservar configuración" : "eliminar todo")}");
            proc = Process.Start(Profile.Psi(exeCopy, data, null, extract))!;
            Check(Profile.WaitFor(() => Profile.ReadLog(data)?.Contains("Ready") == true, 60000), "la copia aislada arranca");
            Check(new WindowsCredentialStore(credTarget).HasKey(), "hay una clave guardada antes de desinstalar");
            int pid = proc.Id;

            // The second instance asks the first one to open Settings.
            Process.Start(Profile.Psi(exeCopy, data, null, extract))!.WaitForExit(15000);
            AutomationElement? settings = null;
            Profile.WaitFor(() => (settings = FindWindow(pid, "Configuración")) is not null, 15000);
            Check(settings is not null, "se abre la Configuración");
            if (settings is null) return 1;

            var uninstall = FindByName(settings, ControlType.Button, "Desinstalar…");
            Check(uninstall is not null, "el botón «Desinstalar…» existe");
            if (uninstall is null) return 1;
            ((InvokePattern)uninstall.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

            AutomationElement? dialog = null;
            Profile.WaitFor(() => (dialog = FindDialog(pid, settings)) is not null, 8000);
            Check(dialog is not null, "aparece la confirmación");
            if (dialog is null) return 1;

            // Cancel must change nothing.
            ((InvokePattern)FindByName(dialog, ControlType.Button, "Cancelar")!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Desktop.Pump(800);
            Check(!proc.HasExited && Directory.Exists(app) && new WindowsCredentialStore(credTarget).HasKey(), "Cancelar no desinstala nada");

            ((InvokePattern)FindByName(settings, ControlType.Button, "Desinstalar…")!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            dialog = null;
            Profile.WaitFor(() => (dialog = FindDialog(pid, settings)) is not null, 8000);
            var radios = dialog!.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
            ((SelectionItemPattern)radios[keepSettings ? 1 : 0].GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            ((InvokePattern)FindByName(dialog, ControlType.Button, "Desinstalar")!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

            Check(proc.WaitForExit(20000), "el proceso termina solo");
            Check(Profile.WaitFor(() => !Directory.Exists(app), 45000), "se borra la carpeta del programa (EXE y archivos del paquete)");
            Check(Profile.WaitFor(() => !Directory.Exists(Path.Combine(extract, "DikTap")), 10000), "se borran las bibliotecas extraídas");
            Check(!new WindowsCredentialStore(credTarget).HasKey(), "la clave se elimina del Administrador de credenciales");
            if (keepSettings)
            {
                // Data cleanup is the last step of the helper cmd: wait for it to finish.
                Profile.WaitFor(() => !Directory.Exists(data) || Directory.GetFileSystemEntries(data).Length <= 1, 15000);
                var left = Directory.Exists(data) ? Directory.GetFileSystemEntries(data).Select(Path.GetFileName).ToArray() : Array.Empty<string?>();
                Check(left.Length == 1 && left[0] == "settings.json", $"solo queda settings.json en los datos (queda: {string.Join(", ", left)})");
                var s = new JsonSettingsStore(Path.Combine(data, "settings.json")).Load();
                Check(s.FirstRunCompleted, "la configuración conservada es legible");
            }
            else
            {
                Check(Profile.WaitFor(() => !Directory.Exists(data), 10000), "se borran todos los datos");
            }
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(true); } catch { }
            try { new WindowsCredentialStore(credTarget).Delete(); } catch { }
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(data, true); } catch { }
            Console.WriteLine(failures == 0 ? "TODO OK" : failures + " fallos");
        }
        return failures == 0 ? 0 : 1;
    }

    // The modal dialog is a window owned by Settings: UI Automation hangs it under its owner.
    private static AutomationElement? FindDialog(int pid, AutomationElement owner)
    {
        foreach (AutomationElement w in owner.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)))
            if (w.Current.Name.StartsWith("Desinstalar", StringComparison.OrdinalIgnoreCase)) return w;
        return FindWindow(pid, "Desinstalar");
    }

    private static AutomationElement? FindWindow(int pid, string titlePart)
    {
        var all = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
        foreach (AutomationElement w in all)
            if (w.Current.Name.Contains(titlePart, StringComparison.OrdinalIgnoreCase)) return w;
        return null;
    }

    private static AutomationElement? FindByName(AutomationElement root, ControlType type, string name)
        => root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, type),
            new PropertyCondition(AutomationElement.NameProperty, name)));
}
