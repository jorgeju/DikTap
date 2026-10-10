using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DikTap.Interop;
using DikTap.Services.Paste;
using DikTap.Services.Settings;

namespace DikTap.LiveCheck;

internal static class ClipboardPasteChecks
{
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "OK" : "FALLO")}] {what}");
        if (!ok) _failures++;
    }

    private static T Sta<T>(Func<T> f) => f();

    private static void SetRichClipboard(string text, byte[] custom)
    {
        var bmp = new WriteableBitmap(32, 32, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[32 * 32 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 100; pixels[i + 2] = 50; pixels[i + 3] = 255; }
        bmp.WritePixels(new Int32Rect(0, 0, 32, 32), pixels, 32 * 4, 0);
        var data = new DataObject();
        data.SetText(text);
        data.SetImage(bmp);
        data.SetData("OAID.Custom", new MemoryStream(custom));
        for (int i = 0; i < 20; i++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch (COMException) { Thread.Sleep(30); }
        }
        throw new InvalidOperationException("No se pudo preparar el portapapeles de prueba");
    }

    private static string[] Formats()
    {
        for (int i = 0; i < 20; i++)
        {
            try { return Clipboard.GetDataObject()?.GetFormats(false) ?? Array.Empty<string>(); }
            catch (COMException) { Thread.Sleep(30); }
        }
        return Array.Empty<string>();
    }

    private static string? GetText()
    {
        // Other processes (clipboard history, etc.) may hold it open for a moment: retry.
        // OLE invalidates its cache while processing messages: pump the dispatcher before reading from the same process.
        Desktop.Pump(60);
        for (int i = 0; i < 20; i++)
        {
            try
            {
                if (Clipboard.ContainsText()) return Clipboard.GetText();
            }
            catch (COMException) { }
            Thread.Sleep(30);
        }
        return null;
    }

    public static async Task<int> RunClipboard()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var win = Desktop.MakeInputWindow(out _);
        IntPtr owner = new WindowInteropHelper(win).Handle;
        var clip = new Win32Clipboard(() => owner);

        Console.WriteLine("Guardando el portapapeles actual del usuario para devolverlo al final...");
        var original = await clip.TakeSnapshotAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        Console.WriteLine($"  original: {(original is null ? "no recuperable" : original.Formats.Count + " formatos, " + original.TotalBytes + " bytes")}");

        try
        {
            var custom = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
            SetRichClipboard("previo ñandú 日本語", custom);
            uint seq0 = clip.SequenceNumber;
            Console.WriteLine("Formatos de prueba: " + string.Join(", ", Formats()));

            var snap = await clip.TakeSnapshotAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            Check(snap is not null, "snapshot recuperado");
            Console.WriteLine($"  snapshot: {snap?.Formats.Count} formatos ({string.Join(",", snap!.Formats.Select(f => f.Format))}), omitidos={snap.SkippedFormats}, {snap.TotalBytes} bytes");
            Check(snap.Formats.Any(f => f.Format == 13), "incluye CF_UNICODETEXT");
            Check(snap.Formats.Any(f => f.Format == 8 || f.Format == 17), "incluye CF_DIB/CF_DIBV5 (formato no textual)");
            Check(clip.SequenceNumber == seq0, "tomar el snapshot no altera el portapapeles");

            uint? seq = await clip.SetTextAsync("dictado 😀 ñandú", excludeFromHistory: true, CancellationToken.None);
            Check(seq is not null && seq != seq0 && clip.SequenceNumber == seq, "escritura de texto (secuencia coherente)");
            Check(GetText() == "dictado 😀 ñandú", $"el texto Unicode llega intacto (len={GetText()?.Length}, hex={string.Join(' ', (GetText() ?? "").Select(c => ((int)c).ToString("X")))})");
            var f1 = Formats();
            Check(f1.Contains("CanIncludeInClipboardHistory") && f1.Contains("ExcludeClipboardContentFromMonitorProcessing"), "marcado para excluir del historial/nube");
            Check(!Formats().Contains("OAID.Custom"), "los formatos previos se sustituyen mientras dura el pegado");

            Check(await clip.RestoreAsync(snap, CancellationToken.None), "restauración");
            Check(GetText() == "previo ñandú 日本語", $"texto restaurado (obtenido: '{GetText()}')");
            var f2 = Formats();
            Check(f2.Contains("OAID.Custom"), "formato personalizado restaurado");
            Check(Clipboard.ContainsImage(), "imagen restaurada");
            try
            {
                var restored = Clipboard.GetData("OAID.Custom") as MemoryStream;
                Check(restored is not null && restored.ToArray().SequenceEqual(custom), "bytes del formato personalizado idénticos");
            }
            catch (Exception ex) { Check(false, "leer formato personalizado: " + ex.Message); }
        }
        finally
        {
            if (original is not null) await clip.RestoreAsync(original, CancellationToken.None);
            win.Close();
        }
        Console.WriteLine(_failures == 0 ? "TODO OK" : _failures + " fallos");
        return _failures == 0 ? 0 : 1;
    }

    public static async Task<int> RunPaste()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var win = Desktop.MakeInputWindow(out var box);
        IntPtr owner = new WindowInteropHelper(win).Handle;
        var dest = new Win32DestinationService();
        var clip = new Win32Clipboard(() => owner);
        var paste = new PasteService(clip, new SendInputKeyboardInjector(), dest);

        var original = await clip.TakeSnapshotAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        try
        {
            var custom = new byte[] { 9, 8, 7, 6, 5 };
            const string text = "Hola, ñandú — 日本語 😀 línea";

            Console.WriteLine("1) Pegado en un campo de la propia aplicación (destino válido)");
            SetRichClipboard("contenido previo del usuario", custom);
            var snapshot = dest.Capture();
            Check(snapshot is not null && snapshot.Window == owner, "destino capturado = ventana en primer plano");
            {
                Check(dest.Validate(snapshot!) == DestinationStatus.Valid, "destino válido antes de pegar");
                var r = await paste.PasteAsync(new PasteRequest(text, snapshot, ClipboardMode.Restore), CancellationToken.None);
                Desktop.Pump(300);
                Check(r.Outcome == PasteOutcome.Pasted, "resultado Pasted");
                Check(box.Text == text, $"el campo contiene el texto Unicode exacto ('{box.Text}')");
                Check(!box.Text.Contains('\n') && !box.Text.Contains('\r'), "no se pulsó Enter");
                await paste.FlushPendingRestoreAsync();
                Check(GetText() == "contenido previo del usuario", $"portapapeles previo (texto) restaurado (obtenido: '{GetText()}', formatos: {string.Join(",", Formats())}, restauraciones omitidas por log: {File.Exists(Path.Combine(AppPaths.LogDirectory, "app.log")) && File.ReadAllText(Path.Combine(AppPaths.LogDirectory, "app.log")).Contains("modified")})");
                Check(Clipboard.ContainsImage() && Formats().Contains("OAID.Custom"), "formatos no textuales restaurados");
            }

            Console.WriteLine("2) El usuario copia algo durante el retardo: no se pisa");
            box.Clear();
            SetRichClipboard("previo 2", custom);
            snapshot = dest.Capture();
            {
                var r = await paste.PasteAsync(new PasteRequest("otro texto", snapshot, ClipboardMode.Restore), CancellationToken.None);
                Desktop.Pump(250);
                Check(box.Text == "otro texto", "pegado correcto");
                Clipboard.SetDataObject("copiado por el usuario", true);
                await paste.FlushPendingRestoreAsync();
                Check(GetText() == "copiado por el usuario", "la copia del usuario sobrevive (no se restauró encima)");
            }

            Console.WriteLine("3) Cambio temporal de ventana y vuelta a la original antes de terminar: se pega");
            box.Clear();
            snapshot = dest.Capture();
            {
                var other = new Window { Title = "Otra ventana", Width = 300, Height = 120, Topmost = true, Content = new TextBox() };
                other.Show(); other.Activate();
                Desktop.Pump(500);
                Check(dest.Validate(snapshot!) == DestinationStatus.ForegroundChanged, "con otra ventana delante el destino no es válido");
                other.Close();
                win.Activate(); box.Focus(); Desktop.Pump(500);
                Check(dest.Validate(snapshot!) == DestinationStatus.Valid, "al volver a la ventana original vuelve a ser válido");
                var r = await paste.PasteAsync(new PasteRequest("pegado tras volver", snapshot, ClipboardMode.Restore), CancellationToken.None);
                Desktop.Pump(300);
                Check(r.Outcome == PasteOutcome.Pasted, "resultado Pasted");
                Check(box.Text == "pegado tras volver", $"el campo recibió el texto ('{box.Text}')");
                await paste.FlushPendingRestoreAsync();
            }

            Console.WriteLine("3b) La ventana original no está activa al terminar: sin autopegado, texto en el portapapeles");
            box.Clear();
            snapshot = dest.Capture();
            {
                var other = new Window { Title = "Otra ventana", Width = 300, Height = 120, Topmost = true, Content = new TextBox() };
                other.Show(); other.Activate();
                Desktop.Pump(500);
                var r = await paste.PasteAsync(new PasteRequest("no debe pegarse", snapshot, ClipboardMode.Restore), CancellationToken.None);
                Desktop.Pump(200);
                other.Close();
                win.Activate(); box.Focus(); Desktop.Pump(300);
                Check(r.Outcome == PasteOutcome.CopiedManual, "resultado CopiedManual");
                Check(box.Text == "", "el campo no recibió nada");
                Check(GetText() == "no debe pegarse", "el texto queda en el portapapeles para pegarlo a mano");
            }

            Console.WriteLine("4) Ventana de destino cerrada");
            var tmp = new Window { Title = "Efímera", Width = 300, Height = 120, Topmost = true, Content = new TextBox() };
            tmp.Show(); tmp.Activate(); Desktop.Pump(400);
            var s2 = dest.Capture();
            tmp.Close(); Desktop.Pump(300);
            Check(dest.Validate(s2!) == DestinationStatus.WindowClosed, "Validate detecta ventana cerrada");

            Console.WriteLine("5) Destino elevado (UIPI)");
            Console.WriteLine($"  proceso actual elevado: {ProcessElevation.IsElevated((uint)Environment.ProcessId)}");
        }
        finally
        {
            if (original is not null) await clip.RestoreAsync(original, CancellationToken.None);
            win.Close();
        }
        Console.WriteLine(_failures == 0 ? "TODO OK" : _failures + " fallos");
        return _failures == 0 ? 0 : 1;
    }
}
