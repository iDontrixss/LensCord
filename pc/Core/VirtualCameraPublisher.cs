using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace Lenscord.Core;

/// <summary>
/// Publica los frames ya transformados en el archivo NV12 que consume la camara
/// virtual de Windows. La conversion ocurre en un hilo propio para no frenar la UI.
/// </summary>
public sealed class VirtualCameraPublisher : IDisposable
{
    public const int Ancho = 1280;
    public const int Alto = 720;
    public const int Fps = 30;
    private const int Cabecera = 4096;
    private const int TamanoFrame = Ancho * Alto * 3 / 2;
    private const uint Magia = 0x46534E4C; // "LNSF"
    private static readonly ParallelOptions OpcionesConversor = new()
    {
        // Deja un nucleo libre para ffmpeg, la interfaz y el resto del sistema.
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 4)
    };

    private readonly object _candado = new();
    private readonly AutoResetEvent _aviso = new(false);
    private readonly byte[] _nv12 = new byte[TamanoFrame];

    private MemoryMappedFile? _mapa;
    private MemoryMappedViewAccessor? _vista;
    private Thread? _hilo;
    private Frame? _ultimo;
    private bool _activo;
    private int _slot;
    private long _secuencia;
    private int _ultimoError;

    public event Action<string>? Error;

    public static string RutaArchivo => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Lenscord",
        "frames.nv12");

    public bool Activo => _activo;

    public void Iniciar()
    {
        if (!OperatingSystem.IsWindows() || _activo) return;

        try
        {
            var carpeta = Path.GetDirectoryName(RutaArchivo)!;
            Directory.CreateDirectory(carpeta);
            var archivo = new FileStream(RutaArchivo, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            archivo.SetLength(Cabecera + 2L * TamanoFrame);
            _mapa = MemoryMappedFile.CreateFromFile(archivo, null, 0,
                MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
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
            _activo = true;
            _hilo = new Thread(Bucle) { IsBackground = true, Name = "Lenscord camara virtual" };
            _hilo.Start();
        }
        catch (Exception e)
        {
            CerrarMapa();
            Avisar("No se pudo abrir la camara virtual: " + e.Message);
        }
    }

    public void Publicar(Frame frame)
    {
        if (!_activo) return;
        lock (_candado) _ultimo = frame;
        _aviso.Set();
    }

    private void Bucle()
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
            if (frame is null) continue;

            try { Escribir(frame); }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref _ultimoError, 1) == 0)
                    Avisar("Fallo al publicar en la camara virtual: " + e.Message);
            }
        }
    }

    private void Escribir(Frame frame)
    {
        var vista = _vista;
        if (vista is null) return;
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Bgra.Length < frame.Width * frame.Height * 4)
            return;

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

        // Antes se escalaba a otro BGRA de 3.7 MB y se recorria de nuevo entero
        // para convertirlo. Escribe NV12 directamente desde el frame y reparte
        // filas independientes entre nucleos; el consumidor sigue leyendo un
        // frame completo del mismo formato compartido.
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
        _aviso.Set();
        try { _hilo?.Join(1500); } catch { }
        _hilo = null;
        lock (_candado) _ultimo = null;
        CerrarMapa();
    }

    private void CerrarMapa()
    {
        try { _vista?.Dispose(); } catch { }
        try { _mapa?.Dispose(); } catch { }
        _vista = null;
        _mapa = null;
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
