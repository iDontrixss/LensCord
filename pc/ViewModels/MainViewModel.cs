using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Lenscord.Core;

namespace Lenscord.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly string _raiz = RaizProyecto();
    private readonly Adb _adb;
    private readonly LensSession? _sesion;
    private WriteableBitmap? _lienzo;

    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _ctsSesion;
    private System.Threading.Timer? _sonda;
    private System.Threading.Timer? _vigilante;

    private readonly SemaphoreSlim _operacion = new(1, 1);
    private readonly object _demoLock = new();
    private readonly object _logLock = new();
    private readonly List<WriteableBitmap> _lienzosViejos = new();
    private readonly VirtualCameraPublisher _camaraVirtual = new();
    private FuenteSimulada? _demo;

    private Frame? _framePendiente;
    private int _pintadoPendiente;
    private long _ultimoFrameRealMs;
    private int _vigilando;
    private bool _primerFrameReal;
    private bool _disposed;
    private string? _resolucionesCamaraId;

    // ---------------------------------------------------------------- UI
    private EstadoDispositivo _estado = EstadoDispositivo.SinCable;
    public EstadoDispositivo Estado { get => _estado; private set => _estado = value; }
    private bool _modoWifi;
    private string _wifiIp = "", _wifiPuerto = "", _wifiCodigo = "", _wifiEstado = "Sin emparejar";
    private string _estadoTexto = "Buscando el celular...";
    private string _estadoAccion = "Conecta el cable USB y activa Depuración USB en el teléfono.";
    private bool _puedeIniciar, _transmitiendo, _fueraDeLinea;
    private string _modelo = "", _android = "", _serie = "", _ctx = "";
    private bool _hayCelular;

    private CamaraInfo? _camara;
    private double _zoom;
    private bool _torch, _espejo, _pausado, _flashOk;
    private string _tipoZoom = "digital";
    private int _ancho = 1280, _alto = 720, _fps = 30;
    private double _fpsMedidos;
private string _indicadorZoom = "Zoom 1.0x";
    private string _reloj = "--:--:--";
    private double _altoVideo = 430;
    public double AltoVideo
    {
        get => _altoVideo;
        set { if (Math.Abs(_altoVideo - value) > 0.5) { _altoVideo = value; OnChanged(); } }
    }

    public MainViewModel()
    {
        _adb = new Adb(_raiz);

        string dex = Path.Combine(_raiz, "dispositivo", "out", "lenscord.dex");
        if (File.Exists(dex)) _sesion = new LensSession(_adb, dex);
        _sesionNoHayDex = _sesion is null;

        _camaraVirtual.Error += s => Dispatcher.UIThread.Post(() => Escribir("! " + s));


if (!_adb.Available)
        {
            _estadoTexto = "Falta adb";
            _estadoAccion = "No se encontró adb. Descarga platform-tools a tools\\android-sdk\\platform-tools.";
        }

        try
        {
            File.WriteAllText(Path.Combine(_raiz, "evidencia", "entorno.txt"),
                $"raiz      = {_raiz}\r\n" +
                $"base      = {AppContext.BaseDirectory}\r\n" +
                $"adbPath   = {_adb.AdbPath}\r\n" +
                $"adb existe= {_adb.Available}\r\n" +
                $"dex       = {(_sesion is null ? "no compilado" : "presente")}\r\n" +
                $"utc       = {DateTime.UtcNow:O}\r\n");
        }
        catch { }

        if (_sesion is not null)
        {
            _sesion.Log += s => Dispatcher.UIThread.Post(() => Escribir(s));
            _sesion.Error += s => Dispatcher.UIThread.Post(() => Escribir("! " + s));
            _sesion.EstadoCambiado += e => Dispatcher.UIThread.Post(() => AplicarEstado(e));
            _sesion.ProcesoTerminado += () =>
                Dispatcher.UIThread.Post(() => _ = DetenerPorProcesoTerminadoAsync());
        }

        _ = Task.Run(ProbarDemoSiNoHayTelefono);
        _sonda = new System.Threading.Timer(async _ => await SondearAsync().ConfigureAwait(false),
                                            null, 1200, 1500);
        _relojTicker = new System.Threading.Timer(_ => RelojTick(), null, 0, 1000);
        _vigilante = new System.Threading.Timer(_ => Vigilar(), null, 1000, 1000);
    }

    private bool _sesionNoHayDex;
    public bool SesionNoHayDex => _sesionNoHayDex;

    private System.Threading.Timer? _relojTicker;

    private static string RaizProyecto()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PROGRESO.md"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    private void RelojTick() => Dispatcher.UIThread.Post(() => { _reloj = DateTime.Now.ToString("HH:mm:ss"); OnChanged(nameof(Reloj)); });

    // ------------------------------------------------------------- sondeo
private async Task SondearAsync()
    {
        try
        {
            await SondearInternoAsync();
        }
        catch (Exception e)
        {
            try { File.AppendAllText(Path.Combine(_raiz, "evidencia", "sondeo-error.txt"),
                                    $"{DateTime.Now:HH:mm:ss}  {e}\r\n"); }
            catch { }
        }
    }

    private async Task SondearInternoAsync()
    {
        if (!_adb.Available) return;

        var dispositivos = await _adb.DevicesAsync().ConfigureAwait(false);
        var ordenados = dispositivos
            .OrderBy(x => x.State == "device" ? 0 : x.State == "unauthorized" ? 1 : 2)
            .ToList();

        var d = ModoWifi
            ? ordenados.FirstOrDefault(x => x.Serial.Contains(':'))
            : ordenados.FirstOrDefault(x => !x.Serial.Contains(':')) ?? ordenados.FirstOrDefault();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ModoWifi && d is not null && d.Serial.Contains(':')
                && !dispositivos.Any(x => x.State == "device" && !x.Serial.Contains(':')))
            {
                _modoWifi = true;
                OnChanged(nameof(ModoWifi));
                OnChanged(nameof(MostrarPanelWifi));
            }

            _hayCelular = d is not null;
            _serie = d?.Serial ?? "";
            _puedeIniciar = d is not null && d.State == "device";

            // Mientras transmitimos (o tras un error) el sondeo no debe pisar
            // el estado de la sesion: el mensaje de error debe quedar visible.
            if (!Transmitiendo && Estado != EstadoDispositivo.Error)
            {
                if (d is null && ModoWifi)
                    CambiarEstado(EstadoDispositivo.SinCable, "Wi-Fi no conectado",
                        "Activa Depuración inalámbrica y usa Detectar Wi-Fi, o vuelve a USB.");
                else if (d is null)
                    CambiarEstado(EstadoDispositivo.SinCable, "Sin cable",
                        "Conecta el celular por USB y activa Depuración USB en Ajustes > Ajustes del desarrollador.");
                else if (d.State == "unauthorized") CambiarEstado(EstadoDispositivo.SinAutorizar, "Sin autorizar", "Acepta en el celular el aviso de depuración. En Samsung, el Bloqueador automático puede impedirlo: Ajustes > Seguridad y privacidad > Más ajustes de seguridad > Bloqueador automático > Conexiones USB directas: permitir.");
                else if (d.State == "offline") CambiarEstado(EstadoDispositivo.SinDepuracion, "Sin depuración", "El transporte está offline. Reconecta Wi-Fi o USB.");
                else CambiarEstado(EstadoDispositivo.Listo, "Listo", "Conexión " + (d.Serial.Contains(':') ? "Wi-Fi" : "USB") + ". Puedes iniciar la cámara.");
            }

            OnChanged(nameof(Serie));
            OnChanged(nameof(TransporteTexto));
            OnChanged(nameof(PuedeIniciar));
            OnChanged(nameof(HayCelular));
        });

        if (!Transmitiendo && _puedeIniciar && _sesion is not null)
        {
            try { await _sesion.ReintentarRestauracionAsync().ConfigureAwait(false); }
            catch (Exception e)
            {
                try { File.AppendAllText(Path.Combine(_raiz, "evidencia", "sondeo-error.txt"),
                                        $"{DateTime.Now:HH:mm:ss}  restauracion: {e.Message}\r\n"); }
                catch { }
            }
        }
    }

private void CambiarEstado(EstadoDispositivo e, string texto, string accion)
    {
        Estado = e;
        _estadoTexto = texto;
        _estadoAccion = accion;
        RefrescarVisibilidad();
        // Los campos se escriben directo: hay que avisar a estos dos a mano.
        OnChanged(nameof(EstadoTexto));
        OnChanged(nameof(EstadoAccion));
        OnChanged(nameof(Titular));
        OnChanged(nameof(Subtitular));
    }

    // ------------------------------------------------------------- propiedades
    public ObservableCollection<string> Registro { get; } = new();
    public ObservableCollection<CamaraInfo> Camaras { get; } = new();
    public ObservableCollection<ResolucionInfo> Resoluciones { get; } = new();

    /// <summary>Solo se muestra el aviso rosa cuando hay un error que bloquea el inicio.</summary>
    public bool EsPeligro => !Transmitiendo && (Estado == EstadoDispositivo.SinAutorizar
                                             || Estado == EstadoDispositivo.SinDepuracion
                                             || Estado == EstadoDispositivo.Error);

    public bool MostrarIniciar => !Transmitiendo;
    public bool MostrarDetener => Transmitiendo;

    public string TextoModo => ModoWifi ? "Wi-Fi" : "USB";
    public string IconoModo => ModoWifi
        ? "M4 9a12 12 0 0 1 16 0 M7 13a8 8 0 0 1 10 0 M10 17a4 4 0 0 1 4 0"
        : "M12 3v6 M12 9l-3 3 M12 9l3 3 M9 12v9 M15 12v9";

    public ResolucionInfo? ResolucionSeleccionada
    {
        get; set;
    }

    public void RefrescarVisibilidad()
    {
        OnChanged(nameof(EsPeligro));
        OnChanged(nameof(MostrarIniciar));
        OnChanged(nameof(MostrarDetener));
        OnChanged(nameof(TextoModo));
        OnChanged(nameof(IconoModo));
    }

    public bool ModoWifi
    {
        get => _modoWifi;
        set
        {
            if (_modoWifi == value) return;
            _modoWifi = value;
            RefrescarVisibilidad();
            OnChanged(nameof(MostrarPanelWifi));
            _ = SondearAsync();
        }
    }
    public bool MostrarPanelWifi => ModoWifi;

    public string WifiIp { get => _wifiIp; set { _wifiIp = value; OnChanged(); } }
    public string WifiPuerto { get => _wifiPuerto; set { _wifiPuerto = value; OnChanged(); } }
    public string WifiCodigo { get => _wifiCodigo; set { _wifiCodigo = value; OnChanged(); } }
    public string WifiEstado { get => _wifiEstado; set { _wifiEstado = value; OnChanged(); } }

    public bool HayCelular { get => _hayCelular; private set { _hayCelular = value; OnChanged(); } }
    public bool PuedeIniciar { get => _puedeIniciar; private set { _puedeIniciar = value; OnChanged(); } }
    public bool Transmitiendo
    {
        get => _transmitiendo;
        private set
        {
            if (_transmitiendo == value) return;
            _transmitiendo = value;
            RefrescarVisibilidad();
            OnChanged(nameof(Titular));
            OnChanged(nameof(Subtitular));
        }
    }
    public bool FueraDeLinea
    {
        get => _fueraDeLinea;
        private set
        {
            if (_fueraDeLinea == value) return;
            _fueraDeLinea = value;
            OnChanged();
            OnChanged(nameof(Titular));
            OnChanged(nameof(Subtitular));
        }
    }
    public string EstadoTexto { get => _estadoTexto; private set { _estadoTexto = value; OnChanged(); } }
    public string EstadoAccion { get => _estadoAccion; private set { _estadoAccion = value; OnChanged(); } }
    public string Modelo { get => _modelo; private set { _modelo = value; OnChanged(); } }
    public string Android { get => _android; private set { _android = value; OnChanged(); } }
    public string Serie
    {
        get => _serie;
        private set
        {
            _serie = value;
            OnChanged();
            OnChanged(nameof(TransporteTexto));
        }
    }
    public string TransporteTexto => string.IsNullOrEmpty(Serie)
        ? "Sin transporte"
        : (Serie.Contains(':') ? "Wi-Fi · " + Serie : "USB · " + Serie);
    public string Contexto { get => _ctx; private set { _ctx = value; OnChanged(); } }
    public string Reloj { get => _reloj; private set { _reloj = value; OnChanged(); } }

    public WriteableBitmap? Lienzo { get => _lienzo; private set { _lienzo = value; OnChanged(); } }

    public string Titular => Transmitiendo && _primerFrameReal ? "Tu cámara está lista"
        : Transmitiendo ? "Esperando imagen del celular..."
        : FueraDeLinea ? "Sin celular conectado"
        : Estado == EstadoDispositivo.Error ? "La cámara no arrancó"
        : HayCelular ? "Tu celular está listo"
        : "Conecta el cable y empieza";
    public string Subtitular => Transmitiendo && _primerFrameReal
        ? $"{Modelo} - Android {Android} - {Camara?.Nombre ?? "Cámara"}"
        : Transmitiendo
            ? "El túnel está abierto, esperando el primer frame del celular."
            : FueraDeLinea
                ? "Señal simulada para probar la interfaz."
                : Estado is EstadoDispositivo.SinAutorizar
                    or EstadoDispositivo.SinDepuracion
                    or EstadoDispositivo.Error
                    ? "Revisa el aviso y el Registro para continuar."
                    : HayCelular
                        ? "Conexión lista. Inicia cuando quieras."
                        : "Conecta el celular por USB y activa Depuración USB en el teléfono.";

    public CamaraInfo? Camara
    {
        get => _camara;
        set { _camara = value; _flashOk = value?.Flash ?? false; OnChanged(); OnChanged(nameof(FlashOk)); OnChanged(nameof(Subtitular)); OnChanged(nameof(LenteTexto)); }
    }

    public string LenteTexto => Camara?.Nombre ?? "Sin cámara";

    public double Zoom
    {
        get => _zoom;
        set { _zoom = Math.Clamp(value, 0, 1); OnChanged(); OnChanged(nameof(IndicadorZoom)); _ = EnviarZoom(); }
    }

    public string IndicadorZoom
    {
        get => _tipoZoom == "sensor"
                ? $"Zoom {(_zoom <= 0.001 ? "0.5x (ultra)" : (1 + _zoom * 19).ToString("0.0") + "x")} · sensor"
                : $"Zoom {(_zoom <= 0.001 ? "1.0x" : (1 + _zoom * 19).ToString("0.0") + "x")} · digital";
private set => _indicadorZoom = value;
    }

    public bool Torch
    {
        get => _torch;
        set { _torch = value; OnChanged(); OnChanged(nameof(TorchPosible)); _ = EnviarTorch(); }
    }
    public bool TorchPosible => _flashOk;

    public bool Espejo { get => _espejo; set { _espejo = value; OnChanged(); } }
    public bool Pausado { get => _pausado; set { _pausado = value; OnChanged(); } }
    public bool FlashOk { get => _flashOk; private set { _flashOk = value; OnChanged(); } }
    public string TipoZoom { get => _tipoZoom; private set { _tipoZoom = value; OnChanged(); OnChanged(nameof(IndicadorZoom)); } }

    public int Ancho { get => _ancho; private set { _ancho = value; OnChanged(); } }
    public int Alto { get => _alto; private set { _alto = value; OnChanged(); } }
    public int Fps { get => _fps; private set { _fps = value; OnChanged(); } }
    public double FpsMedidos { get => _fpsMedidos; private set { _fpsMedidos = value; OnChanged(); } }
    public string FpsTexto => Transmitiendo ? $"{_fpsMedidos:0.0} FPS" : "-- FPS";
    public string? CamaraSeleccionada { get; set; }

    // ------------------------------------------------------------- acciones
    public async Task IniciarAsync()
    {
        if (_sesion is null) { Escribir("No hay .dex compilado. Ejecuta dispositivo\\build-dex.ps1"); return; }
        if (!PuedeIniciar) { Escribir("Todavia no hay un celular autorizado."); return; }
        if (!await _operacion.WaitAsync(0))
        {
            Escribir("Ya hay una operacion en curso. Espera a que termine.");
            return;
        }

        try
        {
            await DetenerDemoAsync();

            _ctsSesion?.Cancel();
            _ctsSesion?.Dispose();
            _ctsSesion = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var token = _ctsSesion.Token;

            _primerFrameReal = false;
            Interlocked.Exchange(ref _ultimoFrameRealMs, 0);
            Interlocked.Exchange(ref _framePendiente, null);

            Transmitiendo = true;
            FueraDeLinea = false;
            _camaraVirtual.Iniciar();
            Escribir($"Transporte: {(Serie.Contains(':') ? "Wi-Fi" : "USB")} {Serie}");
            CambiarEstado(EstadoDispositivo.Transmitiendo, "Iniciando...", "Subiendo el programa y abriendo la cámara.");

            await _sesion.IniciarAsync(Serie, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            var flujo = _sesion.FlujoVideo;
            if (flujo is null) throw new InvalidOperationException("No se abrio el flujo de video");

            var ffmpeg = await Task.Run(() => FfmpegH264Decoder.BuscarFfmpeg(_raiz), token).ConfigureAwait(true);
            IFuenteVideo fuente = ffmpeg is null ? new MjpegDecoder() : new FfmpegH264Decoder();

            fuente.FrameRecibido += RealFrame;
            fuente.Error += s => Dispatcher.UIThread.Post(() => Escribir("! " + s));
            await fuente.IniciarAsync(flujo, Ancho, Alto, token).ConfigureAwait(true);
            _sesion.Fuente = fuente;

            Escribir($"Fuente de video: {fuente.Nombre}");

            // Abrir el tunel no es tener imagen. Esperamos el primer frame real:
            // si el telefono no puede abrir la camara, el proceso muere y aqui se ve.
            bool llegoImagen = await EsperarPrimerFrameAsync(TimeSpan.FromSeconds(20), token);
            if (!llegoImagen)
            {
                Escribir("! No llego ningun frame del celular en 20 s.");
                throw new TimeoutException("El tunel funciona pero el telefono no envio ningun frame. Revisa el Registro.");
            }

            CambiarEstado(EstadoDispositivo.Transmitiendo, "Transmitiendo", "Todo funcionando.");
        }
        catch (OperationCanceledException)
        {
            Escribir("Inicio cancelado.");
            Transmitiendo = false;
            CambiarEstado(_hayCelular ? EstadoDispositivo.Listo : EstadoDispositivo.SinCable,
                          _hayCelular ? "Listo" : "Sin cable",
                          _hayCelular ? "Camara detenida. Puedes volver a iniciar." : "Conecta el celular por USB.");
        }
        catch (Exception e)
        {
            Transmitiendo = false;
            CambiarEstado(EstadoDispositivo.Error, "Error al iniciar", e.Message);
            Escribir("ERROR: " + e);
        }
        finally
        {
            if (!Transmitiendo)
            {
                _camaraVirtual.Detener();
                try { await _sesion.DetenerAsync().ConfigureAwait(true); }
                catch (Exception e) { Escribir("ERROR al limpiar tras el inicio: " + e.Message); }
            }
            _operacion.Release();
            OnChanged();
        }
    }

    public async Task DetenerAsync()
    {
        // Cancelar primero permite abortar un IniciarAsync que este esperando imagen.
        try { _ctsSesion?.Cancel(); } catch { }
        Transmitiendo = false;
        _camaraVirtual.Detener();
        Escribir("Deteniendo...");

        await _operacion.WaitAsync();
        try
        {
            if (_sesion is not null) await _sesion.DetenerAsync().ConfigureAwait(true);
            _primerFrameReal = false;
            Interlocked.Exchange(ref _ultimoFrameRealMs, 0);
            Interlocked.Exchange(ref _framePendiente, null);
            if (_sesion is null || _sesion.LimpiezaOk)
                Escribir("Telefono limpio. No queda ningun proceso ni archivo nuestro.");
            else
                Escribir("! No se pudo confirmar la limpieza del telefono. Revisa el Registro.");
            CambiarEstado(_hayCelular ? EstadoDispositivo.Listo : EstadoDispositivo.SinCable,
                          _hayCelular ? "Listo" : "Sin cable",
                          _hayCelular ? "Camara detenida. Puedes volver a iniciar." : "Conecta el celular por USB.");
        }
        catch (Exception e)
        {
            Escribir("ERROR al detener: " + e);
            CambiarEstado(EstadoDispositivo.Error, "Error al detener", e.Message);
        }
        finally
        {
            _operacion.Release();
            OnChanged();
        }
    }

    public Task ZoomMasAsync() { Zoom = Math.Min(1, Zoom + 0.1); return Task.CompletedTask; }
    public Task ZoomMenosAsync() { Zoom = Math.Max(0, Zoom - 0.1); return Task.CompletedTask; }
    public void ZoomMas() => Zoom = Math.Min(1, Zoom + 0.1);
    public void ZoomMenos() => Zoom = Math.Max(0, Zoom - 0.1);

    private Task EnviarZoom() => _sesion is null || !Transmitiendo ? Task.CompletedTask : _sesion.ZoomAsync(Zoom);
    private Task EnviarTorch() => _sesion is null || !Transmitiendo ? Task.CompletedTask : _sesion.TorchAsync(Torch);

    public async Task AlternarPausaAsync()
    {
        Pausado = !Pausado;
        if (_sesion is not null && Transmitiendo) await _sesion.PausaAsync(Pausado);
    }

    public async Task CambiarCamaraAsync()
    {
        if (_sesion is null || !Transmitiendo) return;
        if (Camaras.Count < 2) { Escribir("El telefono solo reporta una camara."); return; }
        int i = Camaras.IndexOf(Camara!) ;
        var siguiente = Camaras[(i + 1) % Camaras.Count];
        await _sesion.CamaraAsync(siguiente.Id);
    }

    public async Task AplicarResolucionAsync()
    {
        if (ResolucionSeleccionada is null || _sesion is null || !Transmitiendo) return;
        var r = ResolucionSeleccionada;
        await _sesion.FormatoAsync(r.W, r.H, r.Fps);
    }

    // ------------------------------------------------------------- wifi
    public async Task EmparejarWifiAsync()
    {
        if (string.IsNullOrWhiteSpace(WifiIp) || string.IsNullOrWhiteSpace(WifiPuerto) || string.IsNullOrWhiteSpace(WifiCodigo))
        { WifiEstado = "Falta la IP, el puerto o el codigo de 6 digitos."; return; }
        WifiEstado = "Emparejando...";
        var r = await _adb.PairAsync($"{WifiIp}:{WifiPuerto}", WifiCodigo);
        WifiEstado = r.All.Contains("Successfully", StringComparison.OrdinalIgnoreCase)
            ? "Emparejado. Ahora conecta con el puerto de conexion (no el de emparejamiento)."
            : r.All;
        Escribir("adb pair: " + r.All);
    }

    public async Task ConectarWifiAsync()
    {
        if (string.IsNullOrWhiteSpace(WifiIp) || string.IsNullOrWhiteSpace(WifiPuerto))
        { WifiEstado = "Falta la IP o el puerto de conexion."; return; }
        WifiEstado = "Conectando...";
        var r = await _adb.ConnectAsync($"{WifiIp}:{WifiPuerto}");
        bool ok = r.All.Contains("connected", StringComparison.OrdinalIgnoreCase)
               && !r.All.Contains("cannot", StringComparison.OrdinalIgnoreCase)
               && !r.All.Contains("failed", StringComparison.OrdinalIgnoreCase);
        WifiEstado = ok ? $"Conectado a {WifiIp}:{WifiPuerto}." : r.All;
        Escribir("adb connect: " + r.All);
    }

    public async Task DetectarWifiAsync()
    {
        if (Transmitiendo) { WifiEstado = "Deten la camara antes de buscar Wi-Fi."; return; }

        WifiEstado = "Buscando Wi-Fi (mDNS)...";
        var servicios = await _adb.MdnsServicesAsync();
        var endpoints = servicios
            .Where(s => string.Equals(s.Type, "_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Endpoint)
            .Distinct()
            .ToList();

        if (endpoints.Count == 0)
        {
            WifiEstado = "No se encontro Wi-Fi. Activa Depuracion inalambrica y que PC y celular esten en la misma red.";
            return;
        }

        string ultimoError = "";
        foreach (var endpoint in endpoints)
        {
            var r = await _adb.ConnectAsync(endpoint);
            Escribir("adb connect: " + r.All);
            if (!r.Ok || !r.All.Contains("connected", StringComparison.OrdinalIgnoreCase)) continue;

            var prueba = await _adb.ShellAsync(endpoint, "echo wifi_ok", 5000);
            if (!prueba.All.Contains("wifi_ok"))
            {
                ultimoError = prueba.All;
                await _adb.DisconnectAsync(endpoint);
                continue;
            }

            int dosPuntos = endpoint.LastIndexOf(':');
            WifiIp = dosPuntos > 0 ? endpoint[..dosPuntos] : endpoint;
            WifiPuerto = dosPuntos > 0 ? endpoint[(dosPuntos + 1)..] : "5555";
            WifiEstado = $"Wi-Fi detectado en {endpoint}.";
            ModoWifi = true;
            Escribir("Wi-Fi detectado: " + endpoint);
            await SondearInternoAsync();
            return;
        }

        WifiEstado = string.IsNullOrWhiteSpace(ultimoError)
            ? "Wi-Fi encontrado, pero no se pudo conectar. Empareja la depuracion inalambrica."
            : "Wi-Fi encontrado, pero el shell fallo: " + ultimoError;
    }

    public async Task ActivarWifiAsync()
    {
        if (Transmitiendo) { WifiEstado = "Deten la camara antes de activar Wi-Fi."; return; }

        var dispositivos = await _adb.DevicesAsync();
        var usb = dispositivos.FirstOrDefault(x => x.State == "device" && !x.Serial.Contains(':'));
        if (usb is null)
        {
            WifiEstado = "Conecta el USB una vez para activar Wi-Fi.";
            return;
        }

        WifiEstado = "Leyendo la IP del celular...";
        var ipr = await _adb.ShellAsync(usb.Serial, "ip -4 addr show wlan0", 10000);
        var ipm = Regex.Match(ipr.All, @"inet\s+(\d+\.\d+\.\d+\.\d+)");
        if (!ipm.Success)
        {
            WifiEstado = "El celular no tiene IP Wi-Fi. Activa Wi-Fi y vuelve a intentar.";
            return;
        }
        string ip = ipm.Groups[1].Value;

        WifiEstado = "Activando ADB por Wi-Fi...";
        var tcp = await _adb.RunAsync(new[] { "-s", usb.Serial, "tcpip", "5555" }, 10000);
        Escribir("adb tcpip: " + tcp.All);
        await Task.Delay(1500);

        string serial = $"{ip}:5555";
        bool conectado = false;
        for (int i = 0; i < 3 && !conectado; i++)
        {
            var r = await _adb.ConnectAsync(serial);
            Escribir("adb connect: " + r.All);
            conectado = r.All.Contains("connected", StringComparison.OrdinalIgnoreCase)
                     && !r.All.Contains("cannot", StringComparison.OrdinalIgnoreCase)
                     && !r.All.Contains("failed", StringComparison.OrdinalIgnoreCase);
            if (!conectado) await Task.Delay(1000);
        }
        if (!conectado)
        {
            WifiEstado = $"No se pudo conectar a {serial}. Revisa que PC y celular esten en la misma red.";
            return;
        }

        var prueba = await _adb.ShellAsync(serial, "echo wifi_ok", 5000);
        if (!prueba.All.Contains("wifi_ok"))
        {
            WifiEstado = "ADB respondio pero el shell no. Revisa el aviso de depuracion en el celular.";
            return;
        }

        WifiIp = ip;
        WifiPuerto = "5555";
        WifiEstado = $"Wi-Fi activo en {serial}. Ya puedes desconectar el USB.";
        ModoWifi = true;
        Escribir("Wi-Fi activo: " + serial);
        await SondearInternoAsync();
    }

    public async Task VolverUsbAsync()
    {
        if (Transmitiendo) { WifiEstado = "Deten la camara antes de volver a USB."; return; }

        var dispositivos = await _adb.DevicesAsync();
        var usb = dispositivos.FirstOrDefault(x => x.State == "device" && !x.Serial.Contains(':'));
        var wifi = dispositivos.FirstOrDefault(x => x.State == "device" && x.Serial.Contains(':'));
        if (usb is null)
        {
            WifiEstado = "Conecta el USB antes de volver a ese modo.";
            return;
        }
        if (wifi is null)
        {
            ModoWifi = false;
            WifiEstado = "No habia Wi-Fi activo; se usa USB.";
            await SondearInternoAsync();
            return;
        }

        WifiEstado = "Volviendo a USB...";
        var r = await _adb.RunAsync(new[] { "-s", wifi.Serial, "usb" }, 10000);
        Escribir("adb usb: " + r.All);
        await _adb.DisconnectAsync(wifi.Serial);
        ModoWifi = false;
        WifiEstado = "ADB volvio a USB.";
        await SondearInternoAsync();
    }

    // ------------------------------------------------------------- video
    private void RealFrame(Frame f) => Mostrar(f, true);
    private void DemoFrame(Frame f) => Mostrar(f, false);

    /// <summary>
    /// Un solo frame pendiente. Si el celular produce mas rapido de lo que la
    /// interfaz dibuja, se descarta el viejo. Nunca se encola un frame por cada
    /// uno recibido: eso era lo que llenaba la memoria y bloqueaba el hilo UI.
    /// </summary>
    private void Mostrar(Frame f, bool real)
    {
        if (real)
        {
            if (!Transmitiendo) return;
            Interlocked.Exchange(ref _ultimoFrameRealMs, Environment.TickCount64);
            if (!_primerFrameReal)
            {
                _primerFrameReal = true;
                Dispatcher.UIThread.Post(() =>
                {
                    OnChanged(nameof(Titular));
                    OnChanged(nameof(Subtitular));
                });
            }
        }
        else if (Transmitiendo) return;

        Interlocked.Exchange(ref _framePendiente, f);
        if (Interlocked.Exchange(ref _pintadoPendiente, 1) == 0)
            Dispatcher.UIThread.Post(DrenarFrame);
    }

    private void DrenarFrame()
    {
        try
        {
            var f = Interlocked.Exchange(ref _framePendiente, null);
            if (f is null) return;
            var ajustado = Marco.Aplicar(f, Espejo, RotacionActual, RecorteActual);
            Pintar(ajustado);
            _camaraVirtual.Publicar(ajustado);
        }
        catch (Exception e) { Escribir("pintado: " + e.Message); }
        finally
        {
            Volatile.Write(ref _pintadoPendiente, 0);
            if (Volatile.Read(ref _framePendiente) is not null &&
                Interlocked.CompareExchange(ref _pintadoPendiente, 1, 0) == 0)
            {
                try { Dispatcher.UIThread.Post(DrenarFrame); } catch { }
            }
        }
    }

    /// <summary>Espera a que llegue el primer frame real del celular.</summary>
    private async Task<bool> EsperarPrimerFrameAsync(TimeSpan limite, CancellationToken ct)
    {
        var fin = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < fin)
        {
            if (_primerFrameReal) return true;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return _primerFrameReal;
    }

    public Rotacion RotacionActual { get; set; } = Rotacion.Ninguna;
    public Recorte RecorteActual { get; set; } = Recorte.Original;

    public void Rotar() { RotacionActual = RotacionActual == Rotacion.Ninguna ? Rotacion.Derecha90 : Rotacion.Ninguna; }
    public void Voltear() { Espejo = !Espejo; }
    public void CicloRecorte() => RecorteActual = RecorteActual switch
    {
        Recorte.Original => Recorte.DieciseisNueve,
        Recorte.DieciseisNueve => Recorte.UnoAUno,
        Recorte.UnoAUno => Recorte.NueveADieciseis,
        _ => Recorte.Original
    };

    private void Pintar(Frame f)
    {
        try
        {
            int w = f.Width, h = f.Height;
            if (w <= 0 || h <= 0) return;

            var bitmap = _lienzo;
            if (bitmap is null || bitmap.PixelSize.Width != w || bitmap.PixelSize.Height != h)
            {
                var nuevo = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                                PixelFormat.Bgra8888, AlphaFormat.Premul);
                // El bitmap anterior puede seguir enlazado al control: no se dispone
                // aqui. Se guarda y se libera al cerrar la aplicacion.
                if (bitmap is not null) _lienzosViejos.Add(bitmap);
                Lienzo = nuevo;
                bitmap = nuevo;
            }

            int filaOrigen = w * 4;
            int copiar = Math.Min(filaOrigen, f.Bgra.Length);
            using (var buf = bitmap.Lock())
            {
                if (buf.RowBytes == filaOrigen)
                {
                    Marshal.Copy(f.Bgra, 0, buf.Address, Math.Min(copiar * h, f.Bgra.Length));
                }
                else
                {
                    int filaDestino = Math.Min(filaOrigen, buf.RowBytes);
                    for (int y = 0; y < h && (y + 1) * filaOrigen <= f.Bgra.Length; y++)
                        Marshal.Copy(f.Bgra, y * filaOrigen,
                                     IntPtr.Add(buf.Address, y * buf.RowBytes), filaDestino);
                }
            }

            LienzoCambiado?.Invoke();
        }
        catch (Exception e) { Escribir("pintado: " + e.Message); }
    }

    public event Action? LienzoCambiado;

    private void AplicarEstado(EstadoTelefono e)
    {
        Modelo = e.Modelo; Android = e.VersionAndroid; Contexto = e.ComoObtuvoContext;
        Ancho = e.Ancho; Alto = e.Alto; Fps = e.Fps;
        FpsMedidos = e.FpsMedidos; TipoZoom = e.TipoZoom; FlashOk = e.FlashDisponible;
        Pausado = e.Pausado;

        // No reconstruir las listas en cada "stats": eso hacia parpadear los
        // desplegables y disparaba SelectionChanged sin parar.
        bool mismasCamaras = Camaras.Count == e.Camaras.Count;
        if (mismasCamaras)
        {
            for (int i = 0; i < e.Camaras.Count; i++)
                if (Camaras[i].Id != e.Camaras[i].Id) { mismasCamaras = false; break; }
        }
        if (!mismasCamaras)
        {
            Camaras.Clear();
            foreach (var c in e.Camaras) Camaras.Add(c);
            _resolucionesCamaraId = null;
        }

        var nuevaCamara = Camaras.FirstOrDefault(c => c.Id == e.CamaraActual) ?? Camaras.FirstOrDefault();
        if (nuevaCamara is not null && (Camara is null || Camara.Id != nuevaCamara.Id))
            Camara = nuevaCamara;

        if (Camara is not null && _resolucionesCamaraId != Camara.Id)
        {
            _resolucionesCamaraId = Camara.Id;
            Resoluciones.Clear();
            foreach (var (w, h) in Camara.Resoluciones().Take(8))
                foreach (var fps in new[] { 15, 30 })
                    Resoluciones.Add(new ResolucionInfo { W = w, H = h, Fps = fps });
        }

        if (Transmitiendo && Estado != EstadoDispositivo.Transmitiendo)
        {
            CambiarEstado(EstadoDispositivo.Transmitiendo, "Transmitiendo",
                $"{Camaras.Count} cámara(s) reportada(s). Flash: {(e.FlashDisponible ? "sí" : "no")}. Zoom del {e.TipoZoom}.");
        }
        OnChanged();
    }

    private async Task ProbarDemoSiNoHayTelefono()
    {
        for (int i = 0; i < 10 && !HayCelular; i++) await Task.Delay(600);
        if (HayCelular || Transmitiendo) return;

        var demo = new FuenteSimulada();
        demo.FrameRecibido += DemoFrame;
        lock (_demoLock) { _demo = demo; }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            FueraDeLinea = true;
            Escribir("No hay ningún celular en adb. Vista con señal simulada.");
        });

        await demo.IniciarAsync(Stream.Null, 960, 540, CancellationToken.None);

        // Si el usuario inicio la camara mientras se preparaba la demo, no dejarla viva.
        if (Transmitiendo) await DetenerDemoAsync();
    }

    private async Task DetenerDemoAsync()
    {
        FuenteSimulada? demo;
        lock (_demoLock)
        {
            demo = _demo;
            _demo = null;
        }
        if (demo is null) return;

        demo.FrameRecibido -= DemoFrame;
        try { await demo.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    private void Vigilar()
    {
        if (!Transmitiendo || Pausado) return;
        long ultimo = Volatile.Read(ref _ultimoFrameRealMs);
        if (ultimo == 0) return;
        if (Environment.TickCount64 - ultimo < 8000) return;
        if (Interlocked.Exchange(ref _vigilando, 1) != 0) return;

        Dispatcher.UIThread.Post(async () =>
        {
            try { await DetenerPorFaltaDeFramesAsync(); }
            catch (Exception e) { Escribir("ERROR del vigilante: " + e.Message); }
            finally { Volatile.Write(ref _vigilando, 0); }
        });
    }

    private async Task DetenerPorFaltaDeFramesAsync()
    {
        if (!Transmitiendo) return;
        Escribir("! No llegaron frames reales en 8 s; deteniendo la vista.");
        await DetenerAsync();
        CambiarEstado(EstadoDispositivo.Error, "Sin imagen",
            "El celular dejó de enviar imagen. Revisa el Registro y vuelve a iniciar.");
    }

    private async Task DetenerPorProcesoTerminadoAsync()
    {
        if (!Transmitiendo) return;
        Escribir("! El proceso del celular terminó; deteniendo la vista.");
        try { await DetenerAsync(); }
        catch (Exception e) { Escribir("ERROR al detener tras la caida: " + e.Message); }
        CambiarEstado(EstadoDispositivo.Error, "Sin imagen",
            "El proceso del celular terminó. Revisa el Registro.");
    }

    public void Escribir(string s)
    {
        string linea = $"{DateTime.Now:HH:mm:ss}  {s}";

        if (Dispatcher.UIThread.CheckAccess())
        {
            AgregarRegistro(linea);
        }
        else
        {
            Dispatcher.UIThread.Post(() => AgregarRegistro(linea));
        }

        try
        {
            lock (_logLock)
            {
                string dir = Path.Combine(_raiz, "evidencia");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "app.log"),
                    $"{DateTime.Now:O}  {s}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private void AgregarRegistro(string linea)
    {
        Registro.Add(linea);
        while (Registro.Count > 300) Registro.RemoveAt(0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    private void OnChanged(params string[] props)
    {
        foreach (var p in props) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public async Task DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, true)) return;

        try { _sonda?.Dispose(); } catch { }
        try { _relojTicker?.Dispose(); } catch { }
        try { _vigilante?.Dispose(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { _ctsSesion?.Cancel(); } catch { }

        _camaraVirtual.Dispose();
        await DetenerDemoAsync().ConfigureAwait(false);

        bool tomoOperacion = false;
        try { tomoOperacion = await _operacion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { }

        if (_sesion is not null)
        {
            try { await _sesion.DetenerAsync().ConfigureAwait(false); }
            catch (Exception e) { Escribir("ERROR al cerrar: " + e.Message); }
        }

        if (tomoOperacion) _operacion.Release();
        try { _ctsSesion?.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }

        // Los bitmaps pertenecen al hilo de UI; si ya no estamos en el, dejar que
        // el proceso termine de liberarlos. En el hilo correcto, liberarlos.
        if (Dispatcher.UIThread.CheckAccess())
        {
            foreach (var b in _lienzosViejos) { try { b.Dispose(); } catch { } }
            _lienzosViejos.Clear();
            try { _lienzo?.Dispose(); } catch { }
        }
    }

    public void Dispose() { }
}

