using System.Diagnostics;
using DikTap;
using DikTap.Services.Audio;

namespace DikTap.LiveCheck;

// Checks on the real desktop. Usage: LiveCheck <audio|audio-cycles N|winh|clipboard|paste|overlay>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "help";
        string outDir = Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(outDir);
        try
        {
            return cmd switch
            {
                "audio" => AudioChecks.Capture(outDir, args.Length > 1 && double.TryParse(args[1], out var s) ? s : 2.0).GetAwaiter().GetResult(),
                "audio-cycles" => AudioChecks.Cycles(args.Length > 1 ? int.Parse(args[1]) : 20).GetAwaiter().GetResult(),
                "threads" => Threads(),
                "winh" => WinHChecks.Run(),
                "clipboard" => Desktop.Await(ClipboardPasteChecks.RunClipboard),
                "paste" => Desktop.Await(ClipboardPasteChecks.RunPaste),
                "overlay" => OverlayChecks.Run(outDir),
                "e2e" => E2E.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 14),
                "winh-soak" => E2E.Soak(args[1], args.Length > 2 ? int.Parse(args[2]) : 60),
                "first-run" => E2E.FirstRun(args[1], outDir),
                "uninstall" => UninstallE2E.Run(args[1], args.Length > 2 && args[2] == "keep"),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine("FALLO: " + ex);
            return 1;
        }
    }

    private static int Threads()
    {
        Console.WriteLine("Inicio: " + Proc());
        for (int i = 0; i < 40; i++)
        {
            var t = new Thread(() => { }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
        }
        Console.WriteLine("Tras 40 hilos: " + Proc());
        for (int i = 0; i < 40; i++)
        {
            using var e = new ManualResetEvent(false);
            using var a = new AutoResetEvent(false);
        }
        Console.WriteLine("Tras 40 eventos: " + Proc());
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Console.WriteLine("Tras GC: " + Proc());
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("LiveCheck <audio [segundos]|audio-cycles N>");
        return 2;
    }

    public static string Proc()
    {
        var p = Process.GetCurrentProcess();
        return $"WS={p.WorkingSet64 / 1048576.0:F1}MB Priv={p.PrivateMemorySize64 / 1048576.0:F1}MB Handles={p.HandleCount} Threads={p.Threads.Count}";
    }
}

internal static class AudioChecks
{
    public static async Task<int> Capture(string outDir, double seconds)
    {
        var rec = new WasapiRecorder();
        var devices = await rec.ListCaptureDevicesAsync();
        Console.WriteLine($"Dispositivos de captura activos: {devices.Count}");
        foreach (var d in devices) Console.WriteLine($"  - {d.Name}  [{d.Id}]");

        var sw = Stopwatch.StartNew();
        using var session = await rec.StartAsync(null, CancellationToken.None);
        Console.WriteLine($"Captura abierta en {sw.ElapsedMilliseconds} ms (micrófono predeterminado)");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var result = await session.StopAsync();
        using var wav = result.Wav;
        Console.WriteLine($"WAV: {wav.Length} bytes, {wav.Duration.TotalSeconds:F2} s a {wav.SampleRate} Hz, sonido={result.HasSound}, bloques reservados={wav.AllocatedBytes / 1024} KB");

        string path = Path.Combine(outDir, "capture.wav");
        await using (var f = File.Create(path)) await wav.CopyToAsync(f);
        Console.WriteLine($"Guardado: {path}");

        // Validates samples: peak and RMS
        var bytes = await File.ReadAllBytesAsync(path);
        int n = (bytes.Length - 44) / 2;
        double sum = 0; int peak = 0;
        for (int i = 0; i < n; i++)
        {
            short s = BitConverter.ToInt16(bytes, 44 + i * 2);
            sum += (double)s * s;
            peak = Math.Max(peak, Math.Abs((int)s));
        }
        Console.WriteLine($"Muestras: {n}, pico={peak}, RMS={Math.Sqrt(sum / Math.Max(n, 1)):F1}");
        double expected = seconds * wav.SampleRate;
        Console.WriteLine($"Muestras esperadas ~{expected:F0} (desviación {(n - expected) / wav.SampleRate * 1000:F0} ms)");

        // With a specific device
        if (devices.Count > 0)
        {
            using var s2 = await rec.StartAsync(devices[0].Id, CancellationToken.None);
            await Task.Delay(300);
            var r2 = await s2.StopAsync();
            Console.WriteLine($"Dispositivo concreto: {r2.Wav.Duration.TotalSeconds:F2} s OK");
            r2.Wav.Dispose();
        }

        // Nonexistent device
        try
        {
            using var bad = await rec.StartAsync("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}", CancellationToken.None);
            Console.WriteLine("ERROR: se esperaba fallo con dispositivo inexistente");
            return 1;
        }
        catch (DictationException ex)
        {
            Console.WriteLine("Dispositivo inexistente -> " + ex.Message);
        }
        return 0;
    }

    public static async Task<int> Cycles(int count)
    {
        var rec = new WasapiRecorder();
        Console.WriteLine("Inicio: " + Program.Proc());
        int ok = 0, aborted = 0;
        for (int i = 0; i < count; i++)
        {
            using var s = await rec.StartAsync(null, CancellationToken.None);
            await Task.Delay(150);
            if (i % 3 == 2)
            {
                s.Abort();
                aborted++;
            }
            else
            {
                var r = await s.StopAsync();
                r.Wav.Dispose();
                ok++;
            }
            if ((i + 1) % 10 == 0) Console.WriteLine($"  tras {i + 1}: " + Program.Proc());
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Console.WriteLine($"Fin ({ok} paradas, {aborted} cancelaciones): " + Program.Proc());
        return 0;
    }
}
