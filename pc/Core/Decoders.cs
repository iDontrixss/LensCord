using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;

namespace Lenscord.Core;

public sealed class Frame
{
    public byte[] Bgra = Array.Empty<byte>();
    public int Width;
    public int Height;
    /// <summary>Stopwatch en ms cuando llego el frame: permite medir latencia real de punta a punta.</summary>
    public long RecibidoMs;
}

/// <summary>Fuente de pixeles para la vista previa. Toda salida acaba en BGRA.</summary>
public interface IFuenteVideo : IAsyncDisposable
{
    string Nombre { get; }
    bool Activo { get; }
    int Ancho { get; }
    int Alto { get; }
    event Action<Frame>? FrameRecibido;
    event Action<string>? Error;

    Task IniciarAsync(Stream entradaVideo, int ancho, int alto, CancellationToken ct);
    Task DetenerAsync();
}

// =====================================================================
// 1) H.264 -> ffmpeg -> BGRA crudo. Via principal: la de menor latencia.
// =====================================================================
public sealed class FfmpegH264Decoder : IFuenteVideo
{
    public string Nombre => "H.264 (ffmpeg)";
    public bool Activo { get; private set; }
    public int Ancho { get; private set; }
    public int Alto { get; private set; }
    public event Action<Frame>? FrameRecibido;
    public event Action<string>? Error;

    private Process? _ff;
    private CancellationTokenSource? _cts;

    public static string? BuscarFfmpeg(string projectRoot)
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string rid = OperatingSystem.IsWindows() ? "win-x64"
                   : OperatingSystem.IsMacOS()
                       ? (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                       : "linux-x64";

        foreach (var cand in new[]
        {
            Path.Combine(projectRoot, "tools", "ffmpeg", rid, "bin", exe),
            Path.Combine(projectRoot, "tools", "ffmpeg", exe),
        })
            if (File.Exists(cand)) return cand;

        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            try { var f = Path.Combine(d.Trim(), exe); if (File.Exists(f)) return f; } catch { }
        }
        return null;
    }

    public Task IniciarAsync(Stream entradaVideo, int ancho, int alto, CancellationToken ct)
    {
        string? exe = BuscarFfmpeg(AppContext.BaseDirectory) ?? BuscarFfmpeg(RaizProyecto());
        if (exe is null)
            throw new InvalidOperationException("No se encontro ffmpeg en esta maquina");

        Ancho = ancho; Alto = alto;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-flags", "low_delay",
            "-f", "h264", "-i", "pipe:0",
            // Salida rawvideo: pipe:1 es el ARCHIVO de salida, con tamano y cadencia.
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-video_size", $"{ancho}x{alto}",
            "-framerate", "30",
            "pipe:1"
        }) psi.ArgumentList.Add(a);

        _ff = Process.Start(psi) ?? throw new InvalidOperationException("no se pudo iniciar ffmpeg");
        Activo = true;
        _ = Task.Run(() => AlimentarAsync(entradaVideo, token), token);
        _ = Task.Run(() => DrenarAsync(token), token);
        _ = Task.Run(async () =>
        {
            try
            {
                string err = await _ff!.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!token.IsCancellationRequested && !string.IsNullOrWhiteSpace(err))
                    Error?.Invoke("ffmpeg: " + err.Trim());
            }
            catch { }
        }, token);
        return Task.CompletedTask;
    }

    private static string RaizProyecto()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PROGRESO.md")))
            d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    private async Task AlimentarAsync(Stream entrada, CancellationToken token)
    {
        try
        {
            var stdin = _ff!.StandardInput.BaseStream;
            var buf = new byte[1 << 16];
            while (!token.IsCancellationRequested)
            {
                int n = await entrada.ReadAsync(buf.AsMemory(), token).ConfigureAwait(false);
                if (n <= 0) break;
                await stdin.WriteAsync(buf.AsMemory(0, n), token).ConfigureAwait(false);
                await stdin.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            try { _ff?.StandardInput.Close(); } catch { }
        }
    }

    private async Task DrenarAsync(CancellationToken token)
    {
        int bytesPorFrame = checked(Ancho * Alto * 4);
        var reloj = Stopwatch.StartNew();
        var salida = _ff!.StandardOutput.BaseStream;
        var destino = new byte[bytesPorFrame];
        try
        {
            while (!token.IsCancellationRequested)
            {
                await LeerExactoAsync(salida, destino, token).ConfigureAwait(false);
                // Copia: el bucle reutiliza el mismo buffer en el siguiente frame.
                FrameRecibido?.Invoke(new Frame
                {
                    Bgra = (byte[])destino.Clone(),
                    Width = Ancho,
                    Height = Alto,
                    RecibidoMs = reloj.ElapsedMilliseconds,
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (Exception e) { if (!token.IsCancellationRequested) Error?.Invoke("ffmpeg: " + e.Message); }
    }

    private static async Task LeerExactoAsync(Stream s, byte[] dest, CancellationToken ct)
    {
        int leidos = 0;
        while (leidos < dest.Length)
        {
            int n = await s.ReadAsync(dest.AsMemory(leidos), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException("ffmpeg cerro la salida de video");
            leidos += n;
        }
    }

    public async Task DetenerAsync()
    {
        Activo = false;
        try { _cts?.Cancel(); } catch { }
        try { if (_ff is { HasExited: false }) _ff.Kill(true); } catch { }
        try { if (_ff is not null) await _ff.WaitForExitAsync(new CancellationTokenSource(1500).Token); } catch { }
        _ff?.Dispose(); _ff = null;
        _cts?.Dispose(); _cts = null;
    }

    public async ValueTask DisposeAsync() => await DetenerAsync();
}

// =====================================================================
// 2) MJPEG -> Avalonia/Skia. Red de seguridad cuando no hay ffmpeg:
//    el celular codifica JPEG y aqui se abre con SkiaSharp, que ya viene
//    dentro de Avalonia. Sin dependencias externas ni licencias.
// =====================================================================
public sealed class MjpegDecoder : IFuenteVideo
{
    public string Nombre => "MJPEG (decodificador de Avalonia)";
    public bool Activo { get; private set; }
    public int Ancho { get; private set; }
    public int Alto { get; private set; }
    public event Action<Frame>? FrameRecibido;
    public event Action<string>? Error;

    private CancellationTokenSource? _cts;

    public Task IniciarAsync(Stream entradaVideo, int ancho, int alto, CancellationToken ct)
    {
        Ancho = ancho > 0 ? ancho : 640;
        Alto = alto > 0 ? alto : 480;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        Activo = true;
        _ = Task.Run(() => LeerAsync(entradaVideo, token), token);
        return Task.CompletedTask;
    }

    private async Task LeerAsync(Stream entrada, CancellationToken token)
    {
        var reloj = Stopwatch.StartNew();
        var acc = new List<byte>(1 << 16);
        var buf = new byte[1 << 16];
        bool dentro = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                int n = await entrada.ReadAsync(buf.AsMemory(), token).ConfigureAwait(false);
                if (n <= 0) break;

                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];

                    if (!dentro)
                    {
                        // SOI: 0xFF 0xD8
                        if (b == 0xFF && i + 1 < n && buf[i + 1] == 0xD8)
                        {
                            dentro = true;
                            acc.Clear();
                            acc.Add(0xFF); acc.Add(0xD8);
                            i++;
                        }
                        continue;
                    }

                    acc.Add(b);

                    // EOI: 0xFF 0xD9 -> imagen completa
                    if (b == 0xD9 && acc.Count >= 2 && acc[^2] == 0xFF)
                    {
                        var jpg = acc.ToArray();
                        dentro = false;
                        acc.Clear();
                        try
                        {
                            // Decodificamos con Avalonia Bitmap, que por debajo usa Skia.
                            // Asi no dependemos de la API de SkiaSharp, que cambia entre versiones.
                            using var ms = new MemoryStream(jpg);
                            using var bmp = new Bitmap(ms);
                            int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
                            int stride = w * 4;
                            var ptr = Marshal.AllocHGlobal(stride * h);
                            try
                            {
                                bmp.CopyPixels(new PixelRect(0, 0, w, h), ptr, stride * h, stride);
                                var datos = new byte[stride * h];
                                Marshal.Copy(ptr, datos, 0, datos.Length);
                                FrameRecibido?.Invoke(new Frame
                                {
                                    Bgra = datos,
                                    Width = w,
                                    Height = h,
                                    RecibidoMs = reloj.ElapsedMilliseconds,
                                });
                            }
                            finally { Marshal.FreeHGlobal(ptr); }
                        }
                        catch (Exception e) { Error?.Invoke("MJPEG: " + e.Message); }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!token.IsCancellationRequested) Error?.Invoke("MJPEG: " + e.Message); }
    }

    public Task DetenerAsync()
    {
        Activo = false;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose(); _cts = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DetenerAsync();
}

// =====================================================================
// 3) Fuente simulada. Sirve para construir y probar la INTERFAZ sin
//    celular. No finge ser una camara: dibuja algo que se mueve, para
//    poder medir escalado, espejo, rotacion, pausa y FPS de verdad.
// =====================================================================
public sealed class FuenteSimulada : IFuenteVideo
{
    public string Nombre => "Simulada (sin celular)";
    public bool Activo { get; private set; }
    public int Ancho { get; private set; } = 960;
public int Alto { get; private set; } = 540;
    public event Action<Frame>? FrameRecibido;
    public event Action<string>? Error;

    private CancellationTokenSource? _cts;

    public Task IniciarAsync(Stream entradaVideo, int ancho, int alto, CancellationToken ct)
    {
        if (ancho > 0) Ancho = ancho;
        if (alto > 0) Alto = alto;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        Activo = true;
        _ = Task.Run(async () =>
        {
            var reloj = Stopwatch.StartNew();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    FrameRecibido?.Invoke(Dibujar(reloj.ElapsedMilliseconds));
                    await Task.Delay(33, token).ConfigureAwait(false);
                }
            }
catch (OperationCanceledException) { }
            catch (Exception e) { Error?.Invoke("fuente simulada: " + e.Message); }
        }, token);
        return Task.CompletedTask;
    }

    private Frame Dibujar(long t)
    {
        int w = Ancho, h = Alto;
        var px = new byte[w * h * 4];

        // Fondo oscuro con una banda diagonal suave: el movimiento se ve y
        // permite comprobar espejo y rotacion sin dominar la interfaz.
        int off = (int)(t / 16) % (w + h);
        int cx = w / 2, cy = h / 2;
        int maxDist = (int)Math.Sqrt(cx * cx + cy * cy);
        for (int y = 0; y < h; y++)
        {
            int dy = y - cy;
            for (int x = 0; x < w; x++)
            {
                int dx = x - cx;
                int phase = x + y + off;
                int distance = Math.Abs(phase % (w + h) - (w + h) / 2);
                int glow = Math.Max(0, 180 - distance / 4);
                int vignette = Math.Max(0, 34 - (int)(Math.Sqrt(dx * dx + dy * dy) * 34 / maxDist));
                int i = (y * w + x) * 4;
                px[i + 0] = (byte)(28 + glow / 3 + vignette / 2);
                px[i + 1] = (byte)(30 + glow / 5 + vignette / 3);
                px[i + 2] = (byte)(42 + glow / 8 + vignette / 2);
                px[i + 3] = 255;
            }
        }

        // Cruz y anillos: referencias para el zoom y el encuadre.
        for (int d = -Math.Max(18, h / 14); d <= Math.Max(18, h / 14); d++)
        {
            Poner(px, w, h, cx + d, cy, 235, 235, 245);
            Poner(px, w, h, cx, cy + d, 235, 235, 245);
        }
        int radio = Math.Min(w, h) / 5;
        for (int a = 0; a < 360; a += 2)
        {
            double rad = a * Math.PI / 180;
            Poner(px, w, h, cx + (int)(Math.Cos(rad) * radio),
                             cy + (int)(Math.Sin(rad) * radio), 130, 135, 165);
        }

        // Esquinas rosas: con el espejo horizontal se ven invertidas.
        for (int d = 0; d < 8; d++)
        {
            Poner(px, w, h, 8 + d, 8, 240, 72, 155);
            Poner(px, w, h, 8, 8 + d, 240, 72, 155);
            Poner(px, w, h, w - 9 - d, 8, 240, 72, 155);
            Poner(px, w, h, w - 9, 8 + d, 240, 72, 155);
            Poner(px, w, h, 8 + d, h - 9, 240, 72, 155);
            Poner(px, w, h, 8, h - 9 - d, 240, 72, 155);
            Poner(px, w, h, w - 9 - d, h - 9, 240, 72, 155);
            Poner(px, w, h, w - 9, h - 9 - d, 240, 72, 155);
        }

        return new Frame { Bgra = px, Width = w, Height = h, RecibidoMs = t };
    }

    private static void Poner(byte[] px, int w, int h, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return;
        int i = (y * w + x) * 4;
        px[i + 0] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
    }

    public Task DetenerAsync()
    {
        Activo = false;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose(); _cts = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DetenerAsync();
}