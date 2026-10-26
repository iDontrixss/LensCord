using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace Lenscord.Core;

public sealed class VirtualCameraPublisher : IDisposable
{
    public const int Ancho = 1280;
    public const int Alto = 720;
    public const int Fps = 30;
    private const int Cabecera = 4096;
    private const int TamanoFrame = Ancho * Alto * 3 / 2;
    private const uint Magia = 0x46534E4C;
    private const int LatidoMs = 500;
    private static readonly ParallelOptions OpcionesConversor = new()
    {
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 4)
    };

    private readonly object _candado = new();
    private readonly AutoResetEvent _aviso = new(false);
    private readonly byte[] _nv12 = new byte[TamanoFrame];

    private MemoryMappedFile? _mapa;
    private MemoryMappedViewAccessor? _vista;
    private FileStream? _archivo;
    private Process? _ffmpegV4l2;
    private Thread? _hilo;
    private Frame? _ultimo;
    private volatile bool _activo;
    private int _slot;
    private long _secuencia;
    private long _ultimaEscrituraMs;
    private int _ultimoError;

    public event Action<string>? Error;

    public static string RutaArchivo => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Lenscord",
        "frames.nv12");

    public bool Activo => _activo;

    public void Iniciar()
    {
        if (_activo) return;
        Interlocked.Exchange(ref _ultimoError, 0);

        if (OperatingSystem.IsLinux()) { IniciarLinux(); return; }
        if (!OperatingSystem.IsWindows())
        {
            Avisar(Loc.T("core.camaravirtual.no_disponible"));
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RutaArchivo)!);
            _archivo = new FileStream(RutaArchivo, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            _archivo.SetLength(Cabecera + 2L * TamanoFrame);
            _mapa = MemoryMappedFile.CreateFromFile(_archivo, null, 0,
                MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
            _vista = _mapa.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            _vista.Write(0, Magia);
            _vista.Write(4, 1);
            _vista.Write(8, Ancho);
            _vista.Write(12, Alto);
            _vista.Write(16, TamanoFrame);
            _vista.Write(20, 0);
            _vista.Write(24, 0L);
            _slot = 0;
            _secuencia = 0;
            ArrancarHilo();
        }
        catch (Exception e)
        {
            CerrarMapa();
            Avisar(Loc.T("core.camaravirtual.no_abrir", e.Message));
        }
    }

    private void ArrancarHilo()
    {
        _activo = true;
        _ultimaEscrituraMs = Environment.TickCount64;
        _hilo = new Thread(Bucle) { IsBackground = true, Name = "Lenscord camara virtual" };
        _hilo.Start();
    }

    public void Publicar(Frame frame)
    {
        if (!_activo) return;
        lock (_candado) _ultimo = frame;
        try { _aviso.Set(); } catch (ObjectDisposedException) { }
    }

    private void Bucle()
    {
        try
        {
            while (_activo)
            {
                _aviso.WaitOne(100);
                if (!_activo) break;

                Frame? frame;
                lock (_candado)
                {
                    frame = _ultimo;
                    _ultimo = null;
                }

                if (frame is null)
                {
                    Latido();
                    continue;
                }

                try
                {
                    Escribir(frame);
                    _ultimaEscrituraMs = Environment.TickCount64;
                }
                catch (Exception e)
                {
                    if (Interlocked.Exchange(ref _ultimoError, 1) == 0)
                        Avisar(Loc.T("core.camaravirtual.fallo_publicar", e.Message));
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception e)
        {
            Avisar(Loc.T("core.camaravirtual.se_detuvo", e.Message));
        }
    }

    private void Latido()
    {
        var vista = _vista;
        if (vista is null || _secuencia == 0) return;
        if (Environment.TickCount64 - _ultimaEscrituraMs < LatidoMs) return;
        try
        {
            vista.Write(24, ++_secuencia);
            _ultimaEscrituraMs = Environment.TickCount64;
        }
        catch (Exception) { }
    }

    private void IniciarLinux()
    {
        string? dispositivo = BuscarDispositivoV4l2();
        if (dispositivo is null)
        {
            Avisar(Loc.T("core.camaravirtual.sin_v4l2",
                "sudo modprobe v4l2loopback devices=1 video_nr=10 card_label=\"Lenscord\" exclusive_caps=1"));
            return;
        }

        string? ffmpeg = FfmpegH264Decoder.BuscarFfmpeg(Rutas.Raiz)
                         ?? FfmpegH264Decoder.BuscarFfmpeg(AppContext.BaseDirectory);
        if (ffmpeg is null)
        {
            Avisar(Loc.T("core.camaravirtual.sin_ffmpeg"));
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "rawvideo", "-pix_fmt", "nv12", "-video_size", $"{Ancho}x{Alto}",
                "-framerate", Fps.ToString(), "-i", "pipe:0",
                "-pix_fmt", "yuyv422", "-f", "v4l2", dispositivo,
            }) psi.ArgumentList.Add(a);

            var proc = Process.Start(psi);
            if (proc is null) { Avisar(Loc.T("core.camaravirtual.no_iniciar_ffmpeg")); return; }
            _ffmpegV4l2 = proc;
            _ = Task.Run(async () =>
            {
                try
                {
                    string err = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    if (_activo && !string.IsNullOrWhiteSpace(err)) Avisar(Loc.T("core.camaravirtual.error_ffmpeg", err.Trim()));
                }
                catch { }
            });

            _slot = 0;
            _secuencia = 0;
            ArrancarHilo();
        }
        catch (Exception e)
        {
            DetenerV4l2();
            Avisar(Loc.T("core.camaravirtual.no_abrir", e.Message));
        }
    }

    private static string? BuscarDispositivoV4l2()
    {
        try
        {
            const string sys = "/sys/class/video4linux";
            if (!Directory.Exists(sys)) return null;

            string? cualquiera = null;
            foreach (var dir in Directory.GetDirectories(sys, "video*").OrderBy(d => d, StringComparer.Ordinal))
            {
                string nombre = "";
                try { nombre = File.ReadAllText(Path.Combine(dir, "name")).Trim(); } catch { }
                string dev = "/dev/" + Path.GetFileName(dir);
                if (!File.Exists(dev)) continue;
                if (nombre.Contains("Lenscord", StringComparison.OrdinalIgnoreCase)) return dev;
                if (cualquiera is null && (nombre.Contains("loopback", StringComparison.OrdinalIgnoreCase) ||
                                           nombre.Contains("Dummy", StringComparison.OrdinalIgnoreCase)))
                    cualquiera = dev;
            }
            return cualquiera;
        }
        catch { return null; }
    }

    private void DetenerV4l2()
    {
        var p = _ffmpegV4l2;
        _ffmpegV4l2 = null;
        if (p is null) return;
        try { p.StandardInput.Close(); } catch { }
        try { if (!p.WaitForExit(1000)) p.Kill(true); } catch { }
        try { p.Dispose(); } catch { }
    }

    private void Escribir(Frame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Bgra.Length < frame.Width * frame.Height * 4)
            return;

        var v4l2 = _ffmpegV4l2;
        if (v4l2 is not null)
        {
            ConvertirNV12(frame);
            v4l2.StandardInput.BaseStream.Write(_nv12, 0, _nv12.Length);
            v4l2.StandardInput.BaseStream.Flush();
            return;
        }

        var vista = _vista;
        if (vista is null) return;

        ConvertirNV12(frame);

        _slot ^= 1;
        var destino = Cabecera + _slot * TamanoFrame;
        vista.WriteArray(destino, _nv12, 0, _nv12.Length);
        vista.Write(20, _slot);
        Thread.MemoryBarrier();
        vista.Write(24, ++_secuencia);
    }

    private void ConvertirNV12(Frame frame)
    {
        int w = frame.Width, h = frame.Height;
        double escala = Math.Min((double)Ancho / w, (double)Alto / h);
        int dw = Math.Max(1, (int)Math.Round(w * escala));
        int dh = Math.Max(1, (int)Math.Round(h * escala));
        int ox = (Ancho - dw) / 2;
        int oy = (Alto - dh) / 2;
        int yPlano = Ancho * Alto;
        var origen = frame.Bgra;

        Array.Fill(_nv12, (byte)16, 0, yPlano);
        Array.Fill(_nv12, (byte)128, yPlano, _nv12.Length - yPlano);

        Parallel.For(0, Alto, OpcionesConversor, y =>
        {
            if (y < oy || y >= oy + dh) return;
            int sy = (y - oy) * h / dh;
            int filaOrigen = sy * w * 4;
            int filaY = y * Ancho;
            for (int x = ox; x < ox + dw; x++)
            {
                int sx = (x - ox) * w / dw;
                int i = filaOrigen + sx * 4;
                int b = origen[i], g = origen[i + 1], r = origen[i + 2];
                _nv12[filaY + x] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
            }
        });

        int uv = yPlano;
        Parallel.For(0, Alto / 2, OpcionesConversor, fila =>
        {
            int y0 = fila * 2;
            int filaUV = fila * Ancho;
            for (int x = 0; x < Ancho; x += 2)
            {
                LeerPixel(origen, w, h, dw, dh, ox, oy, x, y0, out int r0, out int g0, out int b0);
                LeerPixel(origen, w, h, dw, dh, ox, oy, x + 1, y0, out int r1, out int g1, out int b1);
                LeerPixel(origen, w, h, dw, dh, ox, oy, x, y0 + 1, out int r2, out int g2, out int b2);
                LeerPixel(origen, w, h, dw, dh, ox, oy, x + 1, y0 + 1, out int r3, out int g3, out int b3);
                int r = (r0 + r1 + r2 + r3) / 4;
                int g = (g0 + g1 + g2 + g3) / 4;
                int b = (b0 + b1 + b2 + b3) / 4;
                _nv12[uv + filaUV + x] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                _nv12[uv + filaUV + x + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
            }
        });
    }

    private static void LeerPixel(byte[] bgra, int w, int h, int dw, int dh, int ox, int oy,
        int x, int y, out int r, out int g, out int b)
    {
        if (x < ox || x >= ox + dw || y < oy || y >= oy + dh)
        {
            r = g = b = 0;
            return;
        }

        int sx = (x - ox) * w / dw;
        int sy = (y - oy) * h / dh;
        int i = (sy * w + sx) * 4;
        b = bgra[i];
        g = bgra[i + 1];
        r = bgra[i + 2];
    }

    public void Detener()
    {
        if (!_activo) return;
        _activo = false;
        try { _aviso.Set(); } catch (ObjectDisposedException) { }
        try { _hilo?.Join(1500); } catch { }
        _hilo = null;
        lock (_candado) _ultimo = null;
        MarcarDetenida();
        CerrarMapa();
        DetenerV4l2();
    }

    private void MarcarDetenida()
    {
        try
        {
            var vista = _vista;
            if (vista is null) return;
            vista.Write(24, 0L);
            vista.Write(0, 0u);
        }
        catch { }
    }

    private void CerrarMapa()
    {
        try { _vista?.Dispose(); } catch { }
        try { _mapa?.Dispose(); } catch { }
        try { _archivo?.Dispose(); } catch { }
        _vista = null;
        _mapa = null;
        _archivo = null;
    }

    private void Avisar(string mensaje)
    {
        try { Error?.Invoke(mensaje); } catch { }
    }

    public void Dispose()
    {
        Detener();
        _aviso.Dispose();
    }
}
