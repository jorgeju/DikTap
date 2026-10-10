using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using DikTap.Interop;
using DikTap.Services.Credentials;
using DikTap.Services.Paste;
using DikTap.Services.Settings;

namespace DikTap.LiveCheck;

// Own keyboard observer. Installed BEFORE launching the EXE: Windows calls the most recently installed hook first,
// so this one only sees what the application's hook lets through.
internal sealed class InputObserver : IDisposable
{
    private readonly LowLevelKeyboardProc _proc;
    private readonly IntPtr _hook;
    private readonly ConcurrentQueue<(uint Vk, bool Down)> _events = new();

    public InputObserver()
    {
        _proc = (n, w, l) =>
        {
            if (n >= 0)
            {
                int msg = (int)w;
                _events.Enqueue(((uint)Marshal.ReadInt32(l), msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN));
            }
            return NativeMethods.CallNextHookEx(IntPtr.Zero, n, w, l);
        };
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("No se pudo instalar el observador");
    }

    public void Reset() => _events.Clear();
    public int Count(ushort vk) => _events.Count(e => e.Vk == vk);
    public void Dispose() => NativeMethods.UnhookWindowsHookEx(_hook);

    // true if synthetic input reaches the system (rules out locks, UAC, secure desktop, etc.).
    public bool ProbePipeline()
    {
        Reset();
        Desktop.Key(NativeMethods.VK_UNASSIGNED_MASK, false);
        Desktop.Key(NativeMethods.VK_UNASSIGNED_MASK, true);
        Desktop.Pump(120);
        return Count(NativeMethods.VK_UNASSIGNED_MASK) >= 2;
    }
}

internal static class Profile
{
    public const string ApiKey = "sk-test-e2e-0123456789";
    public const string Expected = "texto de prueba ñandú";

    public sealed record Seen(long Bytes, double WavSeconds, string? Auth, string Model, bool HasFile);

    // Simulated OpenAI server on loopback.
    public sealed class Stub : IDisposable
    {
        private readonly HttpListener _l = new();
        public readonly ConcurrentQueue<Seen> Requests = new();
        public int Heads, HeadsWithCredentials;
        public volatile int DelayMs = 200;
        public volatile int Status = 200;
        public int Port { get; }

        public Stub()
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            Port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _l.Start();
            _ = Task.Run(Loop);
        }

        public string Url => $"http://127.0.0.1:{Port}/v1/audio/transcriptions";

        private async Task Loop()
        {
            while (_l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _l.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod == "HEAD")
            {
                // Preconnection: must carry neither key nor body.
                Interlocked.Increment(ref Heads);
                if (ctx.Request.Headers["Authorization"] is not null || ctx.Request.ContentLength64 > 0) Interlocked.Increment(ref HeadsWithCredentials);
                try { ctx.Response.StatusCode = 200; ctx.Response.Close(); } catch (Exception) { }
                return;
            }
            using var ms = new MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms);
            var body = Encoding.Latin1.GetString(ms.ToArray());
            int idx = body.IndexOf("name=\"file\"", StringComparison.Ordinal);
            if (idx < 0) idx = body.IndexOf("name=file", StringComparison.Ordinal);
            long fileBytes = 0;
            if (idx >= 0)
            {
                int start = body.IndexOf("\r\n\r\n", idx, StringComparison.Ordinal) + 4;
                int end = body.LastIndexOf("\r\n--", StringComparison.Ordinal);
                fileBytes = end - start;
            }
            string model = body.Contains("gpt-transcribe") ? "gpt-transcribe" : "?";
            Requests.Enqueue(new Seen(ms.Length, Math.Max(0, (fileBytes - 44) / 32000.0), ctx.Request.Headers["Authorization"], model, fileBytes > 44));
            await Task.Delay(DelayMs);
            try
            {
                ctx.Response.StatusCode = Status;
                byte[] reply = Encoding.UTF8.GetBytes(Status == 200 ? "{\"text\":\"" + Expected + "\",\"languages\":[{\"code\":\"es\"}]}" : "{\"error\":{\"code\":\"invalid_api_key\"}}");
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(reply);
                ctx.Response.Close();
            }
            catch (Exception) { }
        }

        public void Dispose() => _l.Close();
    }

    // Isolated profile: own settings + credential, distinct from the real ones.
    public static string Create(bool withCredential, HotkeyMode mode = HotkeyMode.WinH)
    {
        string data = Path.Combine(Path.GetTempPath(), "oaid-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("DIKTAP_DATA_DIR", data);
        Directory.CreateDirectory(data);
        if (withCredential)
        {
            new WindowsCredentialStore(AppInfo.CredentialTarget).Save(ApiKey);
            new JsonSettingsStore(AppPaths.SettingsFile).Save(new AppSettings { FirstRunCompleted = true, StartWithWindows = false, HotkeyMode = mode });
        }
        return data;
    }

    public static void Destroy(string data)
    {
        try { new WindowsCredentialStore(AppInfo.CredentialTarget).Delete(); } catch { }
        try { Directory.Delete(data, true); } catch { }
    }

    public static ProcessStartInfo Psi(string exe, string data, Stub? stub, string extract)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            Environment =
            {
                ["DIKTAP_DATA_DIR"] = data,
                ["DIKTAP_TEST_ENDPOINT"] = stub?.Url ?? "",
                ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extract,
                // The texts the harness looks for (UIA, overlay) are in Spanish even if Windows is in another language.
                ["DIKTAP_UI_LANGUAGE"] = "es",
            },
        };
        // Extra variables for experiments (e.g. DOTNET_GCgen0size=300000): LIVECHECK_ENV="K=V;K2=V2".
        foreach (var kv in (Environment.GetEnvironmentVariable("LIVECHECK_ENV") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=', 2);
            if (p.Length == 2) psi.Environment[p[0]] = p[1];
        }
        return psi;
    }

    public static string? ReadLog(string data)
    {
        try
        {
            string f = Path.Combine(data, "logs", "app.log");
            if (!File.Exists(f)) return null;
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch (IOException) { return null; }
    }

    public static int Count(string data, string pattern) => Regex.Matches(ReadLog(data) ?? "", pattern).Count;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? cls, string title);

    public static bool CloseGracefully(Process p, int waitMs = 8000)
    {
        IntPtr host = FindWindowW(null, AppInfo.Name + " Host");
        NativeMethods.PostMessageW(host, NativeMethods.WM_CLOSE, UIntPtr.Zero, IntPtr.Zero);
        bool exited = p.WaitForExit(waitMs);
        if (!exited) p.Kill(true);
        return exited && p.ExitCode == 0;
    }

    public static string Metrics(Process p)
    {
        p.Refresh();
        return $"WS={p.WorkingSet64 / 1048576.0,6:F1} MB  Privados={p.PrivateMemorySize64 / 1048576.0,6:F1} MB  Handles={p.HandleCount,4}  Hilos={p.Threads.Count,3}";
    }

    public static bool WaitFor(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            Desktop.Pump(40);
        }
        return cond();
    }

    public static DateTime LastHDown;

    // Timestamp (local time, ms) of the last "Capture started" line in the app log.
    public static DateTime? LastCaptureStart(string data)
    {
        var m = Regex.Matches(ReadLog(data) ?? "", @"(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) INFO Capture started");
        return m.Count == 0 ? null : DateTime.ParseExact(m[^1].Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Stats(IReadOnlyList<double> v) => v.Count == 0 ? "sin datos"
        : $"n={v.Count}, 1.ª={v[0]:F0} ms, mediana={v.OrderBy(x => x).ElementAt(v.Count / 2):F0} ms, p95={v.OrderBy(x => x).ElementAt((int)(v.Count * 0.95)):F0} ms, máx={v.Max():F0} ms";

    public static void Toggle()
    {
        Desktop.Key(NativeMethods.VK_LWIN, false, true);
        LastHDown = DateTime.Now;
        Desktop.Key(NativeMethods.VK_H, false);
        Desktop.Key(NativeMethods.VK_H, true);
        Desktop.Key(NativeMethods.VK_LWIN, true, true);
    }

    public static void Esc()
    {
        Desktop.Key(NativeMethods.VK_ESCAPE, false);
        Desktop.Key(NativeMethods.VK_ESCAPE, true);
    }
}

// E2E of the published EXE: real Win+H (synthetic input) > real microphone > WAV > simulated OpenAI on loopback > paste.
// Saves and restores the user's clipboard when done.
internal static class E2E
{
    public static int Run(string exe, int cycles)
    {
        string data = Profile.Create(true);
        string extract = Path.Combine(data, "extract");
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "OK" : "FALLO")}] {what}");
            if (!ok) failures++;
        }

        using var stub = new Profile.Stub();
        Process? proc = null;
        System.Windows.Window? win = null;
        Win32Clipboard? clip = null;
        ClipboardSnapshot? userClipboard = null;
        InputObserver? observer = null;
        try
        {
            _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            win = Desktop.MakeInputWindow(out var box);
            IntPtr owner = new WindowInteropHelper(win).Handle;
            clip = new Win32Clipboard(() => owner);
            var c = clip;
            userClipboard = Desktop.Await(() => c.TakeSnapshotAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
            Console.WriteLine($"Portapapeles del usuario guardado: {(userClipboard is null ? "NO recuperable (se vaciara al final)" : userClipboard.Formats.Count + " formatos")}");

            observer = new InputObserver();
            if (!Desktop.Foreground(win)) { Console.WriteLine("ABORTADO: la ventana de prueba no tiene el foco."); return 3; }
            if (!observer.ProbePipeline()) { Console.WriteLine("ABORTADO: la entrada sintética no llega al sistema (¿pantalla bloqueada, UAC o ventana elevada?)."); return 4; }
            Console.WriteLine("Preflight: foco correcto y entrada sintética entregada.");

            // --- Startup (first run: extraction) ---
            Console.WriteLine($"EXE: {exe} ({new FileInfo(exe).Length / 1048576.0:F1} MB)");
            var sw = Stopwatch.StartNew();
            proc = Process.Start(Profile.Psi(exe, data, stub, extract))!;
            bool ready = Profile.WaitFor(() => Profile.ReadLog(data)?.Contains("Ready") == true, 60000);
            long firstMs = sw.ElapsedMilliseconds;
            Check(ready, $"primera ejecución (con extracción): listo en bandeja a los {firstMs} ms");
            if (!ready) return 1;
            int extractedFiles = Directory.Exists(extract) ? Directory.EnumerateFiles(extract, "*", SearchOption.AllDirectories).Count() : 0;
            long extractedBytes = Directory.Exists(extract) ? Directory.EnumerateFiles(extract, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
            Console.WriteLine($"  extraído: {extractedFiles} archivos, {extractedBytes / 1048576.0:F1} MB");
            Console.WriteLine("  log: " + string.Join(" | ", (Profile.ReadLog(data) ?? "").Split('\n').Where(l => l.Contains("Shortcut") || l.Contains("Ready")).Select(l => l.Trim())));

            Desktop.Pump(5000);
            string idle0 = Profile.Metrics(proc);
            Console.WriteLine("Reposo tras 5 s:        " + idle0);
            proc.Refresh();
            int baseHandles = proc.HandleCount, baseThreads = proc.Threads.Count;

            // --- Idle CPU (30 s) ---
            var cpu0 = proc.TotalProcessorTime;
            var t0 = Stopwatch.StartNew();
            while (t0.Elapsed < TimeSpan.FromSeconds(30)) Desktop.Pump(1000);
            proc.Refresh();
            var cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            double perCore = cpuMs / t0.Elapsed.TotalMilliseconds * 100.0;
            Console.WriteLine($"CPU en reposo ({t0.Elapsed.TotalSeconds:F0} s): {cpuMs:F0} ms de CPU = {perCore:F3} % de un núcleo = {perCore / Environment.ProcessorCount:F4} % del total ({Environment.ProcessorCount} procesadores lógicos)");
            Check(perCore < 1.0, "CPU en reposo < 1 % de un núcleo");

            // --- Dictations ---
            const string userClip = "portapapeles-de-prueba";
            int ok = 0, escRec = 0, escHttp = 0, err = 0;
            var latencies = new List<double>();
            double plateauPriv = 0, lastPriv = 0;
            var kinds = new[] { 0, 0, 1, 0, 2, 0, 3 }; // 0=ok 1=esc while recording 2=esc during network 3=error 401
            Console.WriteLine($"Ejecutando {cycles} ciclos (ok / Escape grabando / Escape en red / error 401)...");
            for (int i = 0; i < cycles; i++)
            {
                int kind = kinds[i % kinds.Length];
                if (!Desktop.Foreground(win))
                {
                    win.Activate(); box.Focus(); Desktop.Pump(400);
                    if (!Desktop.Foreground(win)) { Console.WriteLine($"ABORTADO en el ciclo {i}: la ventana de prueba perdió el foco (¿estás usando el equipo?)."); return 3; }
                }
                if (!observer.ProbePipeline()) { Console.WriteLine($"ABORTADO en el ciclo {i}: la entrada sintética dejó de llegar."); return 4; }

                box.Clear();
                Clipboard.SetDataObject(userClip, true);
                Desktop.Pump(100);
                if (i < 8 || i % 4 == 0)
                {
                    proc.Refresh();
                    lastPriv = proc.PrivateMemorySize64 / 1048576.0;
                    if (i == 12) plateauPriv = lastPriv;
                    Console.WriteLine($"  antes del ciclo {i,3}: " + Profile.Metrics(proc));
                }
                int reqBefore = stub.Requests.Count;
                stub.Status = kind == 3 ? 401 : 200;
                stub.DelayMs = kind == 2 ? 2500 : 200;
                int authErrBefore = Profile.Count(data, "error Auth");

                Profile.Toggle();
                Desktop.Pump(kind == 1 ? 700 : 1400);
                if (Profile.LastCaptureStart(data) is { } cs && cs > Profile.LastHDown) latencies.Add((cs - Profile.LastHDown).TotalMilliseconds);
                if (kind == 1)
                {
                    Profile.Esc();
                    Desktop.Pump(900);
                    if (stub.Requests.Count != reqBefore || box.Text != "") Check(false, $"ciclo {i}: Escape grabando envió algo o pegó");
                    escRec++;
                    continue;
                }
                Profile.Toggle();
                if (kind == 2)
                {
                    Profile.WaitFor(() => stub.Requests.Count > reqBefore, 5000);
                    Desktop.Pump(300);
                    Profile.Esc();
                    Desktop.Pump(3500); // the late response arrives and must be ignored
                    if (box.Text != "") Check(false, $"ciclo {i}: respuesta tardía pegada tras Escape");
                    escHttp++;
                    continue;
                }
                if (kind == 3)
                {
                    Profile.WaitFor(() => Profile.Count(data, "error Auth") > authErrBefore, 6000);
                    Desktop.Pump(300);
                    if (box.Text != "") Check(false, $"ciclo {i}: se pegó texto tras un 401");
                    err++;
                    continue;
                }

                if (!Profile.WaitFor(() => box.Text == Profile.Expected, 8000))
                {
                    int vEvents = observer.Count(0x56), ctrlEvents = observer.Count(0xA2);
                    string pastedNow = box.Text;
                    Desktop.Pump(3000);
                    Check(false, $"ciclo {i}: no se pegó el texto (box='{pastedNow}', 3 s después='{box.Text}', Ctrl+V visto por el sistema: V={vEvents} Ctrl={ctrlEvents}, primer plano={Desktop.ForegroundProcess()}, ventana de prueba con foco={Desktop.Foreground(win)})");
                    Console.WriteLine("    log: " + string.Join(" | ", (Profile.ReadLog(data) ?? "").Split('\n').TakeLast(10).Select(l => l.Trim())));
                    continue;
                }
                Desktop.Pump(1800); // restore (800 ms) and possible retry
                string? clipText = null;
                try { clipText = Clipboard.GetText(); } catch (COMException) { }
                if (clipText != userClip)
                {
                    // Raw read (no OLE) to tell a real failure from a stale OLE cache in the harness.
                    var raw = Desktop.Await(() => clip!.TakeSnapshotAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
                    string? rawText = raw?.Formats.FirstOrDefault(f => f.Format == 13) is { } f13 ? System.Text.Encoding.Unicode.GetString(f13.Data).TrimEnd('\0') : null;
                    Desktop.Pump(1500);
                    string? later = null;
                    try { later = Clipboard.GetText(); } catch (COMException) { }
                    Check(rawText == userClip, $"ciclo {i}: portapapeles no restaurado (OLE='{clipText}', lectura cruda='{rawText}', OLE 1,5 s después='{later}')");
                    Console.WriteLine("    log: " + string.Join(" | ", (Profile.ReadLog(data) ?? "").Split('\n').TakeLast(8).Select(l => l.Trim())));
                }
                ok++;
            }
            Console.WriteLine($"Ciclos: {ok} ok, {escRec} Escape grabando, {escHttp} Escape en red, {err} errores 401");
            Console.WriteLine("Latencia Win+H (tecla H) > micrófono capturando: " + Profile.Stats(latencies));
            double maxLatency = double.TryParse(Environment.GetEnvironmentVariable("LIVECHECK_MAXLAT_MS"), out var lim) ? lim : 400;
            double worstAfterFirst = latencies.Skip(1).DefaultIfEmpty(0).Max();
            Check(latencies.Count > 0 && worstAfterFirst < maxLatency, $"el micrófono empieza a capturar en < {maxLatency:F0} ms tras la primera vez (peor: {worstAfterFirst:F0} ms)");
            Check(failures == 0, "todos los ciclos se comportaron como se esperaba");

            // --- Idle after the cycles: nothing must remain alive ---
            Console.WriteLine("Esperando 30 s en reposo tras los ciclos (sin forzar GC)...");
            Desktop.Pump(30000);
            string after = Profile.Metrics(proc);
            proc.Refresh();
            Console.WriteLine("Reposo tras los ciclos: " + after);
            Check(proc.HandleCount <= baseHandles + 220, $"handles estables (base {baseHandles} > ahora {proc.HandleCount})");
            Check(proc.Threads.Count <= baseThreads + 14, $"hilos recogidos (base {baseThreads} > ahora {proc.Threads.Count})");
            if (plateauPriv > 0) Check(proc.PrivateMemorySize64 / 1048576.0 - plateauPriv < 20, $"memoria privada sin crecimiento sostenido (ciclo 12: {plateauPriv:F1} MB > ahora {proc.PrivateMemorySize64 / 1048576.0:F1} MB)");

            var seen = stub.Requests.ToArray();
            Check(seen.All(s => s.Auth == "Bearer " + Profile.ApiKey && s.Model == "gpt-transcribe" && s.HasFile), $"todas las peticiones ({seen.Length}) llevaban Authorization, model=gpt-transcribe y el WAV");
            Check(stub.HeadsWithCredentials == 0, $"las conexiones por adelantado ({stub.Heads}) no llevaban clave ni datos");
            if (seen.Length > 0) Console.WriteLine($"  duración media del WAV enviado: {seen.Average(s => s.WavSeconds):F2} s, petición media {seen.Average(s => s.Bytes) / 1024:F0} KB");
            string log = Profile.ReadLog(data) ?? "";
            Check(!log.Contains(Profile.ApiKey) && !log.Contains(Profile.Expected), "el log no contiene la clave ni el texto dictado");

            // --- Orderly exit ---
            Check(Profile.CloseGracefully(proc), "salida ordenada con WM_CLOSE (código 0)");
            var hookLine = Regex.Match(Profile.ReadLog(data) ?? "", @"Win\+H hook: (\d+) events, max callback ([\d.,]+) ms");
            Console.WriteLine("  " + (hookLine.Success ? hookLine.Value : "(sin línea de diagnóstico del hook)"));
            if (hookLine.Success) Check(double.Parse(hookLine.Groups[2].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture) < 50, "callback máximo del hook < 50 ms");

            // --- Second startup and second instance ---
            sw.Restart();
            int lines = Profile.Count(data, "Ready");
            proc = Process.Start(Profile.Psi(exe, data, stub, extract))!;
            bool ready2 = Profile.WaitFor(() => Profile.Count(data, "Ready") > lines, 30000);
            Check(ready2, $"segundo arranque listo en {sw.ElapsedMilliseconds} ms (sin extraer)");
            var second = Process.Start(Profile.Psi(exe, data, stub, extract))!;
            Check(second.WaitForExit(15000) && second.ExitCode == 0, "la segunda instancia termina sola");
            Check(Profile.CloseGracefully(proc), "segunda salida ordenada");
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(true); } catch { }
            observer?.Dispose();
            if (clip is not null && win is not null && win.IsLoaded)
            {
                var c = clip;
                var snap = userClipboard;
                if (snap is not null) Desktop.Await(() => c.RestoreAsync(snap, CancellationToken.None));
                Console.WriteLine("Portapapeles del usuario devuelto.");
            }
            try { win?.Close(); } catch { }
            Profile.Destroy(data);
            Console.WriteLine(failures == 0 ? "TODO OK" : failures + " fallos");
        }
        return failures == 0 ? 0 : 1;
    }

    // Checks that the application leaves no hung sessions or stuck keys after many Win+H and Escape presses,
    // distinguishing "synthetic input did not arrive" (environment) from "the hook did not respond" (product).
    public static int Soak(string exe, int iterations)
    {
        string data = Profile.Create(true);
        string extract = Path.Combine(data, "extract");
        using var stub = new Profile.Stub();
        Process? proc = null;
        System.Windows.Window? win = null;
        InputObserver? observer = null;
        int ok = 0, inputLost = 0, hookMiss = 0, hLeak = 0, startMenu = 0, cancelMiss = 0;
        var startLike = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost" };
        try
        {
            _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            win = Desktop.MakeInputWindow(out var box);
            observer = new InputObserver();
            if (!observer.ProbePipeline()) { Console.WriteLine("ABORTADO: la entrada sintética no llega."); return 4; }

            proc = Process.Start(Profile.Psi(exe, data, stub, extract))!;
            if (!Profile.WaitFor(() => Profile.ReadLog(data)?.Contains("Ready") == true, 60000)) { Console.WriteLine("La app no arrancó."); return 1; }
            Desktop.Pump(1500);

            Console.WriteLine($"{iterations} iteraciones: Win+H > comprobar sesión > Escape > comprobar cancelación");
            for (int i = 1; i <= iterations; i++)
            {
                if (!Desktop.Foreground(win)) { win.Activate(); box.Focus(); Desktop.Pump(400); }
                if (!Desktop.Foreground(win)) { Console.WriteLine($"ABORTADO en {i}: la ventana de prueba perdió el foco."); return 3; }

                bool piped = false;
                for (int t = 0; t < 3 && !piped; t++)
                {
                    piped = observer.ProbePipeline();
                    if (!piped) Desktop.Pump(300);
                }
                if (!piped) { inputLost++; Console.WriteLine($"  {i,3}: la entrada sintética no llegó al sistema (entorno)"); continue; }

                box.Clear();
                observer.Reset();
                int started = Profile.Count(data, @"Session \d+: start");
                int cancelled = Profile.Count(data, @"cancelled \(User\)");
                Profile.Toggle();
                if (!Profile.WaitFor(() => Profile.Count(data, @"Session \d+: start") > started, 2500))
                {
                    hookMiss++;
                    Console.WriteLine($"  {i,3}: Win+H NO inició sesión aunque la entrada sí llegó (producto). H vista por el observador: {observer.Count(NativeMethods.VK_H)}");
                    Desktop.Pump(500);
                    continue;
                }
                if (observer.Count(NativeMethods.VK_H) != 0 || box.Text != "") { hLeak++; Console.WriteLine($"  {i,3}: la H se filtró"); }
                Desktop.Pump(300);
                if (!Desktop.Foreground(win) || startLike.Contains(Desktop.ForegroundProcess())) { startMenu++; Console.WriteLine($"  {i,3}: se abrió otra ventana ({Desktop.ForegroundProcess()})"); win.Activate(); Desktop.Pump(300); }

                Profile.Esc();
                if (Profile.WaitFor(() => Profile.Count(data, @"cancelled \(User\)") > cancelled, 2500)) ok++;
                else
                {
                    cancelMiss++;
                    Console.WriteLine($"  {i,3}: Escape NO canceló la sesión");
                    Profile.Toggle();                      // stops as the user would
                    Profile.WaitFor(() => box.Text != "", 6000);
                    Desktop.Pump(2000);
                }
                Desktop.Pump(200);
            }
        }
        finally
        {
            observer?.Dispose();
            if (proc is not null)
            {
                Console.WriteLine("Estado final: " + Profile.Metrics(proc));
                Profile.CloseGracefully(proc);
                var hookLine = Regex.Match(Profile.ReadLog(data) ?? "", @"Win\+H hook: (\d+) events, max callback ([\d.,]+) ms");
                Console.WriteLine("  " + (hookLine.Success ? hookLine.Value : "(sin línea de diagnóstico del hook)"));
            }
            try { win?.Close(); } catch { }
            Profile.Destroy(data);
        }
        Console.WriteLine($"Resultado: {ok}/{iterations} correctas | entrada no entregada (entorno): {inputLost} | Win+H sin respuesta (producto): {hookMiss} | H filtrada: {hLeak} | Inicio/otra ventana: {startMenu} | Escape sin efecto: {cancelMiss}");
        bool good = hookMiss == 0 && hLeak == 0 && startMenu == 0 && cancelMiss == 0;
        Console.WriteLine(good ? "TODO OK" : "HAY FALLOS DE PRODUCTO");
        return good ? 0 : 1;
    }

    // First use: opens Settings with an empty profile, captures the window and checks that closing it leaves the app alive.
    public static int FirstRun(string exe, string outDir)
    {
        string data = Profile.Create(false);
        string extract = Path.Combine(data, "extract");
        Process? proc = null;
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "OK" : "FALLO")}] {what}");
            if (!ok) failures++;
        }
        try
        {
            proc = Process.Start(Profile.Psi(exe, data, null, extract))!;
            bool shown = false;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 15 && !shown)
            {
                Thread.Sleep(100);
                proc.Refresh();
                shown = proc.MainWindowTitle.Contains("Configuración");
            }
            Check(shown, "se abre la Configuración en el primer uso");
            Thread.Sleep(2500);
            Console.WriteLine("Con la Configuración abierta: " + Profile.Metrics(proc));
            if (shown)
            {
                NativeMethods.GetWindowRect(proc.MainWindowHandle, out RECT r);
                using var bmp = new Bitmap(r.Width, r.Height);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
                string shot = Path.Combine(outDir, "settings-first-run.png");
                bmp.Save(shot, ImageFormat.Png);
                Console.WriteLine("  captura: " + shot);
                NativeMethods.PostMessageW(proc.MainWindowHandle, NativeMethods.WM_CLOSE, UIntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(1500);
                proc.Refresh();
                Check(!proc.HasExited, "cerrar la Configuración deja la aplicación en la bandeja");
                Console.WriteLine("Tras cerrar la Configuración: " + Profile.Metrics(proc));
            }
            Check(Profile.CloseGracefully(proc), "salida ordenada");
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(true); } catch { }
            Profile.Destroy(data);
        }
        Console.WriteLine(failures == 0 ? "TODO OK" : failures + " fallos");
        return failures == 0 ? 0 : 1;
    }
}
