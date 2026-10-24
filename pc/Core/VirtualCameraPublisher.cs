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

    private readonly object _candado = new();
    private readonly AutoResetEvent _aviso = new(false);
    private readonly byte[] _escalado = new byte[Ancho * Alto * 4];
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

        Escalar(frame);
        ConvertirNV12();

        _slot ^= 1;
        var destino = Cabecera + _slot * TamanoFrame;
        vista.WriteArray(destino, _nv12, 0, _nv12.Length);
        vista.Write(20, _slot);
        Thread.MemoryBarrier();
        vista.Write(24, ++_secuencia);
    }

    private void Escalar(Frame frame)
    {
        int w = frame.Width, h = frame.Height;
        double escala = Math.Min((double)Ancho / w, (double)Alto / h);
        int dw = Math.Max(1, (int)Math.Round(w * escala));
        int dh = Math.Max(1, (int)Math.Round(h * escala));
        int ox = (Ancho - dw) / 2;
        int oy = (Alto - dh) / 2;

        Array.Clear(_escalado);
        var origen = frame.Bgra;
        for (int y = 0; y < dh; y++)
        {
            int sy = y * h / dh;
            int filaOrigen = sy * w * 4;
            int filaDestino = ((y + oy) * Ancho + ox) * 4;
            for (int x = 0; x < dw; x++)
            {
                int sx = x * w / dw;
                int si = filaOrigen + sx * 4;
                int di = filaDestino + x * 4;
                _escalado[di] = origen[si];
                _escalado[di + 1] = origen[si + 1];
                _escalado[di + 2] = origen[si + 2];
            }
        }
    }

    private void ConvertirNV12()
    {
        int yPlano = Ancho * Alto;
        for (int y = 0; y < Alto; y++)
        {
            int fila = y * Ancho;
            for (int x = 0; x < Ancho; x++)
            {
                int i = (fila + x) * 4;
                int b = _escalado[i], g = _escalado[i + 1], r = _escalado[i + 2];
                _nv12[fila + x] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
            }
        }

        int uv = yPlano;
        for (int y = 0; y < Alto; y += 2)
        {
            int fila = y * Ancho;
            int filaUV = (y / 2) * Ancho;
            for (int x = 0; x < Ancho; x += 2)
            {
                int i0 = (fila + x) * 4;
                int i1 = i0 + 4;
                int i2 = i0 + Ancho * 4;
                int i3 = i2 + 4;
                int r = (_escalado[i0 + 2] + _escalado[i1 + 2] + _escalado[i2 + 2] + _escalado[i3 + 2]) / 4;
                int g = (_escalado[i0 + 1] + _escalado[i1 + 1] + _escalado[i2 + 1] + _escalado[i3 + 1]) / 4;
                int b = (_escalado[i0] + _escalado[i1] + _escalado[i2] + _escalado[i3]) / 4;
                _nv12[uv + filaUV + x] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                _nv12[uv + filaUV + x + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
            }
        }
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
