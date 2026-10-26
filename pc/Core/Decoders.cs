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
        string exe = Rutas.NombreExe("ffmpeg");
        string rid = Rutas.Rid;

        foreach (var cand in new[]
        {
            Path.Combine(projectRoot, "tools", "ffmpeg", rid, "bin", exe),
            Path.Combine(projectRoot, "tools", "ffmpeg", exe),
            Path.Combine(projectRoot, "ffmpeg", exe),
        })
            if (File.Exists(cand)) return cand;

        return Rutas.BuscarEnPath(exe);
    }

    public Task IniciarAsync(Stream entradaVideo, int ancho, int alto, CancellationToken ct)
    {
        string? exe = BuscarFfmpeg(AppContext.BaseDirectory) ?? BuscarFfmpeg(Rutas.Raiz);
        if (exe is null)
            throw new InvalidOperationException(Loc.T("core.decoders.ffmpeg_no_encontrado"));

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
            "-hide_banner", "-loglevel", "error", "-nostdin", "-fflags", "nobuffer",
            "-flags", "low_delay",
            "-f", "h264", "-i", "pipe:0",
            // La salida raw debe tener exactamente el tamano que DrenarAsync lee.
            // video_size/framerate son opciones de entrada rawvideo y no fijan
            // dimensiones de un stream H.264; la escala explicita evita que un
            // formato negociado distinto se convierta en mosaicos de frames.
            // Llena el cuadro sin barras: escala hasta cubrirlo y recorta el excedente al centro.
            "-vf", $"scale={Ancho}:{Alto}:force_original_aspect_ratio=increase:flags=fast_bilinear,crop={Ancho}:{Alto}",
            "-fps_mode", "passthrough",
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "pipe:1"
        }) psi.ArgumentList.Add(a);

        _ff = Process.Start(psi) ?? throw new InvalidOperationException(Loc.T("core.decoders.no_se_pudo_iniciar_ffmpeg"));
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
            if (n <= 0) throw new EndOfStreamException(Loc.T("core.decoders.ffmpeg_salida_cerrada"));
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
// 3) Fuente simulada. Sirve para construir y probar la INTERFAZ sin
//    celular. No finge ser una camara: dibuja algo que se mueve, para
//    poder medir escalado, espejo, rotacion, pausa y FPS de verdad.
// =====================================================================
public sealed class FuenteSimulada : IFuenteVideo
{
    public string Nombre => Loc.T("core.decoders.nombre_simulada");
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
            catch (Exception e) { Error?.Invoke(Loc.T("core.decoders.fuente_simulada_error", e.Message)); }
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
