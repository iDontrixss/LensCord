using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Text.Json;
using Avalonia.Media;
using Avalonia;
using Avalonia.Styling;
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
    private Frame? _previewFramePendiente;
    private int _previewPendiente;
    private long _ultimoFrameRealMs;
    private int _vigilando;
    private bool _primerFrameReal;
    private bool _disposed;
    private string? _resolucionesCamaraId;

    // ---------------------------------------------------------------- UI
    private EstadoDispositivo _estado = EstadoDispositivo.SinCable;
    public EstadoDispositivo Estado { get => _estado; private set => _estado = value; }
    private bool _modoWifi;
    // Ventana de gracia: tras elegir USB manualmente, el sondeo no re-vierte a Wi-Fi.
    private long _usbPreferidoHasta;
    private const int VentanaGraciaUsbMs = 8000;
    private int _sondeoEnVuelo;
    private bool _configuracionVisible;
    private string _temaActual = "claro";
    private string _wifiIp = "", _wifiPuerto = "", _wifiPuertoEmparejamiento = "", _wifiCodigo = "", _wifiEstado = "Sin buscar dispositivos";
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
    private long _fpsFramesVentana;
    private long _fpsUltimaMedicionMs = Environment.TickCount64;
    private int _fpsCapturaIniciada;
    private int _anchoFrameReal, _altoFrameReal;
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
        CargarTemaGuardado();

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

    private void RelojTick() => Dispatcher.UIThread.Post(() =>
    {
        long ahora = Environment.TickCount64;
        long anterior = Interlocked.Exchange(ref _fpsUltimaMedicionMs, ahora);
        long frames = Interlocked.Exchange(ref _fpsFramesVentana, 0);
        double segundos = Math.Max(1, ahora - anterior) / 1000d;
        FpsMedidos = Transmitiendo && !Pausado ? frames / segundos : 0;
        _reloj = DateTime.Now.ToString("HH:mm:ss");
        OnChanged(nameof(FpsTexto), nameof(ResolucionTexto), nameof(Reloj));
    });

    // ------------------------------------------------------------- sondeo
private async Task SondearAsync()
    {
        // Un sondeo por vez: el timer, el setter y los metodos de Wi-Fi pueden
        // dispararlo a la vez y los resultados se pisarian entre si.
        if (Interlocked.CompareExchange(ref _sondeoEnVuelo, 1, 0) != 0) return;
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
        finally
        {
            Interlocked.Exchange(ref _sondeoEnVuelo, 0);
        }
    }

    private async Task SondearInternoAsync()
    {
        if (!_adb.Available) return;

        var dispositivos = await _adb.DevicesAsync().ConfigureAwait(false);
        var ordenados = dispositivos
            .OrderBy(x => x.State == "device" ? 0 : x.State == "unauthorized" ? 1 : 2)
            .ToList();

        // En USB se prefiere un transporte USB; el transporte Wi-Fi solo entra
        // como respaldo si de verdad esta conectado (estado "device").
        var d = ModoWifi
            ? ordenados.FirstOrDefault(x => x.Serial.Contains(':'))
            : ordenados.FirstOrDefault(x => !x.Serial.Contains(':'))
              ?? ordenados.FirstOrDefault(x => x.Serial.Contains(':') && x.State == "device");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Rescate a Wi-Fi solo cuando el USB se cayo y hay un transporte
            // Wi-Fi vivo. Una eleccion manual reciente manda: durante la
            // ventana de gracia (por ejemplo, mientras `adb usb` re-ennumera)
            // el modo no se revierte solo.
            if (!ModoWifi && Environment.TickCount64 >= _usbPreferidoHasta
                && d is not null && d.Serial.Contains(':')
                && !dispositivos.Any(x => x.State == "device" && !x.Serial.Contains(':')))
            {
                ModoWifi = true;
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
                else if (d.State == "unauthorized") CambiarEstado(EstadoDispositivo.SinAutorizar, "Sin autorizar", "");
                else if (d.State == "offline") CambiarEstado(EstadoDispositivo.SinDepuracion, "Sin depuración", "");
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
    public ObservableCollection<TemaInfo> Temas { get; } = CrearTemas();
    public bool MostrarPanelConfiguracion => _configuracionVisible;
    public string TituloSeccion => _configuracionVisible ? "Configuración" : "Espacio de cámara";
    public string TextoBotonConfiguracion => _configuracionVisible ? "Volver a cámara" : "Configuración";
    public bool MostrarPanelCamara => !_configuracionVisible;
    public string IconoMarca => Temas.FirstOrDefault(t => t.Id == _temaActual)?.EsOscuro == true
        ? "/Assets/Icons/lenscord-icon-dark.jpg" : "/Assets/Icons/lenscord-app.jpg";

    public void AlternarConfiguracion()
    {
        _configuracionVisible = !_configuracionVisible;
        OnChanged(nameof(MostrarPanelConfiguracion));
        OnChanged(nameof(MostrarPanelCamara));
        OnChanged(nameof(TituloSeccion));
        OnChanged(nameof(TextoBotonConfiguracion));
    }

    public void SeleccionarTema(TemaInfo? tema)
    {
        if (tema is null) return;
        _temaActual = tema.Id;
        foreach (var opcion in Temas) opcion.Seleccionado = opcion.Id == tema.Id;
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = tema.EsOscuro ? ThemeVariant.Dark : ThemeVariant.Light;
            foreach (var (key, color) in tema.Paleta)
                app.Resources[key] = new SolidColorBrush(Color.Parse(color));
        }
        OnChanged(nameof(IconoMarca));
        try
        {
            var ruta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lenscord", "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            File.WriteAllText(ruta, JsonSerializer.Serialize(new { tema = tema.Id }));
        }
        catch (Exception ex) { Escribir("No se pudo guardar el tema: " + ex.Message); }
    }

    private void CargarTemaGuardado()
    {
        try
        {
            var ruta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lenscord", "config.json");
            if (!File.Exists(ruta)) { SeleccionarTema(Temas.FirstOrDefault(t => t.Id == "claro")); return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(ruta));
            var id = doc.RootElement.TryGetProperty("tema", out var valor) ? valor.GetString() : "claro";
            SeleccionarTema(Temas.FirstOrDefault(t => t.Id == id) ?? Temas.First(t => t.Id == "claro"));
        }
        catch { SeleccionarTema(Temas.First(t => t.Id == "claro")); }
    }

    private static ObservableCollection<TemaInfo> CrearTemas() => new()
    {
        new("claro", "Claro", "Lenscord clásico", false, "#ECECEF", "#FBFBFB", "#4C5BD4",
            "#ECECEF", "#FBFBFB", "#F7F7F8", "#F1F1F2", "#EAEAEC", "#111111", "#5C5C63", "#8E8E96", "#E5E5E9", "#4C5BD4", "#111111", "#2A2A2E", "#FFFFFF"),
        new("black", "Black", "Negro profundo", true, "#08090B", "#111318", "#8798FF",
            "#08090B", "#111318", "#0D0F13", "#1A1D23", "#242831", "#F5F6F8", "#C0C3CB", "#969AA4", "#2A2E38", "#8798FF", "#8798FF", "#7184F2", "#FFFFFF"),
        new("gris", "Gris", "Neutro y suave", false, "#E6E8EB", "#F5F6F7", "#64748B",
            "#E6E8EB", "#F5F6F7", "#F0F1F3", "#E9EAED", "#DFE1E5", "#25282E", "#555A63", "#787D86", "#D9DCE1", "#586C91", "#25282E", "#41454D", "#FFFFFF"),
        new("beige", "Beige", "Cálido y tranquilo", false, "#F2EDE4", "#FFFCF7", "#A27648",
            "#F2EDE4", "#FFFCF7", "#FAF6EE", "#F1EADF", "#E8DFD1", "#302A24", "#665B4F", "#8B8175", "#E7DED0", "#9B7044", "#302A24", "#4B4035", "#FFFFFF"),
        new("celeste", "Celeste", "Casi blanco, fresco", false, "#EAF6FB", "#FCFEFF", "#4F9FC4",
            "#EAF6FB", "#FCFEFF", "#F4FAFD", "#E8F3F8", "#DCEBF2", "#1D3039", "#4E6570", "#758A93", "#D9EAF1", "#438FB5", "#1D3039", "#347A9D", "#FFFFFF")
    };
    public ObservableCollection<string> Registro { get; } = new();
    public ObservableCollection<CamaraInfo> Camaras { get; } = new();
    public ObservableCollection<ResolucionInfo> Resoluciones { get; } = new();
    public ObservableCollection<WifiDispositivoEncontrado> WifiDispositivos { get; } = new();
    public bool HayDispositivosWifi => WifiDispositivos.Count > 0;

    /// <summary>Solo se muestra el aviso rosa cuando hay un error que bloquea el inicio.</summary>
    public bool EsPeligro => !Transmitiendo && (Estado == EstadoDispositivo.SinAutorizar
                                             || Estado == EstadoDispositivo.SinDepuracion
                                             || Estado == EstadoDispositivo.Error);

    public bool MostrarIniciar => !Transmitiendo;
    public bool MostrarDetener => Transmitiendo;

    public string TextoModo => ModoWifi ? "Wi-Fi" : "USB";
    public string IconoModoSvg => ModoWifi
        ? "/Assets/Icons/wifi.svg"
        : "/Assets/Icons/usb.svg";

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
        OnChanged(nameof(IconoModoSvg));
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
    public string WifiPuertoEmparejamiento { get => _wifiPuertoEmparejamiento; set { _wifiPuertoEmparejamiento = value; OnChanged(); } }
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
            Interlocked.Exchange(ref _fpsCapturaIniciada, 0);
            Interlocked.Exchange(ref _fpsFramesVentana, 0);
            Interlocked.Exchange(ref _anchoFrameReal, 0);
            Interlocked.Exchange(ref _altoFrameReal, 0);
            if (!value) FpsMedidos = 0;
            RefrescarVisibilidad();
            OnChanged(nameof(TorchPosible));
            OnChanged(nameof(FlashTooltip));
            OnChanged(nameof(Titular));
            OnChanged(nameof(Subtitular));
            OnChanged(nameof(FpsTexto), nameof(ResolucionTexto));
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
        ? ""
        : (Serie.Contains(':') ? "Wi-Fi · " + Serie : "USB · " + Serie);
    public bool HayTransporte => !string.IsNullOrEmpty(Serie);
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
            ? ""
            : FueraDeLinea
                ? ""
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
        set
        {
            _camara = value;
            _flashOk = value?.Flash ?? false;
            OnChanged();
            OnChanged(nameof(FlashOk));
            OnChanged(nameof(TorchPosible));
            OnChanged(nameof(FlashTooltip));
            OnChanged(nameof(Subtitular));
            OnChanged(nameof(LenteTexto));
        }
    }

    public string LenteTexto => Camara?.Nombre ?? "Sin cámara";

    public double Zoom
    {
        get => _zoom;
        set { _zoom = Math.Clamp(value, ZoomMinimo, 1); OnChanged(); OnChanged(nameof(IndicadorZoom)); _ = EnviarZoom(); }
    }

    private double ZoomMinimo => Camara?.HasRatio == true && Camara.RatioMin < 1
        ? -1 : 0;

    public string IndicadorZoom
    {
        get
        {
            double maximo = _tipoZoom == "sensor"
                ? Math.Max(1, Camara?.RatioMax ?? Camara?.MaxDigitalZoom ?? 20)
                : Math.Max(1, Camara?.MaxDigitalZoom ?? 20);
            double ratioMinimo = Camara?.HasRatio == true ? Camara.RatioMin : 1;
            double nivel = _zoom < 0
                ? Math.Max(ratioMinimo, 1 + (ratioMinimo - 1) * -_zoom)
                : 1 + _zoom * (maximo - 1);
            return $"Zoom {nivel:0.0}x · {_tipoZoom}";
        }
        private set => _indicadorZoom = value;
    }

    public bool Torch
    {
        get => _torch;
        set
        {
            if (_torch == value) return;
            ActualizarTorch(value);
            _ = EnviarTorch();
        }
    }
    public bool TorchPosible => _flashOk && Transmitiendo;
    public string FlashIconSvg => Torch ? "/Assets/Icons/flash-on.svg" : "/Assets/Icons/flash.svg";
    public string FlashTooltip => !_flashOk ? "El celular o cámara seleccionada no tiene flash"
        : !Transmitiendo ? "Inicia la cámara para usar el flash"
        : Torch ? "Apagar flash" : "Encender flash";

    private void ActualizarTorch(bool value)
    {
        if (_torch == value) return;
        _torch = value;
        OnChanged(nameof(Torch));
        OnChanged(nameof(FlashIconSvg));
        OnChanged(nameof(FlashTooltip));
    }

    public bool Espejo { get => _espejo; set { _espejo = value; OnChanged(); } }
    public bool Pausado { get => _pausado; set { _pausado = value; OnChanged(); } }
    public bool FlashOk
    {
        get => _flashOk;
        private set
        {
            if (_flashOk == value) return;
            _flashOk = value;
            OnChanged();
            OnChanged(nameof(TorchPosible));
            OnChanged(nameof(FlashTooltip));
        }
    }
    public string TipoZoom { get => _tipoZoom; private set { _tipoZoom = value; OnChanged(); OnChanged(nameof(IndicadorZoom)); } }

    public int Ancho { get => _ancho; private set { _ancho = value; OnChanged(); OnChanged(nameof(ResolucionTexto)); } }
    public int Alto { get => _alto; private set { _alto = value; OnChanged(); OnChanged(nameof(ResolucionTexto)); } }
    public int Fps { get => _fps; private set { _fps = value; OnChanged(); } }
    public double FpsMedidos
    {
        get => _fpsMedidos;
        private set
        {
            if (Math.Abs(_fpsMedidos - value) < 0.05) return;
            _fpsMedidos = value;
            OnChanged(nameof(FpsMedidos), nameof(FpsTexto));
        }
    }
    public string FpsTexto => Transmitiendo ? $"{_fpsMedidos:0.0} FPS" : "-- FPS";
    public string ResolucionTexto => !Transmitiendo ? "-- × --" :
        $"{(Volatile.Read(ref _anchoFrameReal) > 0 ? Volatile.Read(ref _anchoFrameReal) : Ancho)} × " +
        $"{(Volatile.Read(ref _altoFrameReal) > 0 ? Volatile.Read(ref _altoFrameReal) : Alto)}";
    public string? CamaraSeleccionada { get; set; }

    // ------------------------------------------------------------- acciones
    public async Task IniciarAsync()
    {
        if (_sesion is null) { Escribir("No hay .dex compilado."); return; }
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

            bool optimizarWifi = Serie.Contains(":");
            await _sesion.IniciarAsync(Serie, token, optimizarWifi).ConfigureAwait(true);
            if (optimizarWifi)
                Escribir($"Perfil Wi-Fi: solicitado 640x480 @ 30; negociado {Ancho}x{Alto} @ {Fps} fps.");
            token.ThrowIfCancellationRequested();
            await _sesion.ZoomAsync(Zoom).ConfigureAwait(true);
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

            CambiarEstado(EstadoDispositivo.Transmitiendo, "Transmitiendo", "");
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
    public Task ZoomMenosAsync() { Zoom = Math.Max(ZoomMinimo, Zoom - 0.1); return Task.CompletedTask; }
    public void ZoomMas() => Zoom = Math.Min(1, Zoom + 0.1);
    public void ZoomMenos() => Zoom = Math.Max(ZoomMinimo, Zoom - 0.1);

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
    public async Task EmparejarWifiAsync(WifiDispositivoEncontrado? dispositivo = null)
    {
        string ip = dispositivo?.Ip ?? WifiIp;
        string puerto = dispositivo?.PuertoEmparejamiento ?? WifiPuertoEmparejamiento;
        if (dispositivo is not null)
        {
            WifiIp = ip;
            WifiPuertoEmparejamiento = puerto;
        }
        if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(puerto) || string.IsNullOrWhiteSpace(WifiCodigo))
        { WifiEstado = "Falta la IP, el puerto de emparejamiento o el código de 6 dígitos."; return; }

        WifiEstado = $"Emparejando {ip}:{puerto}...";
        var r = await _adb.PairAsync($"{ip}:{puerto}", WifiCodigo);
        Escribir("adb pair: " + r.All);
        if (r.All.Contains("Successfully", StringComparison.OrdinalIgnoreCase))
        {
            WifiEstado = "Emparejado. Buscando el puerto de conexión...";
            await DetectarWifiAsync();
        }
        else WifiEstado = r.All;
    }

    public Task ConectarWifiAsync()
    {
        if (string.IsNullOrWhiteSpace(WifiIp) || string.IsNullOrWhiteSpace(WifiPuerto))
        { WifiEstado = "Falta la IP o el puerto de conexión."; return Task.CompletedTask; }
        return ConectarWifiEndpointAsync(WifiIp, WifiPuerto);
    }

    public async Task ConectarWifiAsync(WifiDispositivoEncontrado dispositivo)
    {
        WifiIp = dispositivo.Ip;
        if (!dispositivo.TieneConexion)
        {
            WifiPuertoEmparejamiento = dispositivo.PuertoEmparejamiento ?? "";
            WifiEstado = $"{dispositivo.Nombre} requiere emparejamiento. Ingresa el código del teléfono y pulsa Emparejar.";
            return;
        }

        WifiPuerto = dispositivo.PuertoConexion!;
        await ConectarWifiEndpointAsync(WifiIp, WifiPuerto);
    }

    private async Task ConectarWifiEndpointAsync(string ip, string puerto)
    {
        string endpoint = $"{ip}:{puerto}";
        WifiEstado = $"Conectando a {endpoint}...";
        var r = await _adb.ConnectAsync(endpoint);
        Escribir("adb connect: " + r.All);
        bool conectado = r.All.Contains("connected", StringComparison.OrdinalIgnoreCase)
                      && !r.All.Contains("cannot", StringComparison.OrdinalIgnoreCase)
                      && !r.All.Contains("failed", StringComparison.OrdinalIgnoreCase);
        if (!conectado)
        {
            WifiEstado = r.All;
            return;
        }

        var prueba = await _adb.ShellAsync(endpoint, "echo wifi_ok", 5000);
        if (!prueba.All.Contains("wifi_ok"))
        {
            await _adb.DisconnectAsync(endpoint);
            WifiEstado = "ADB conectó, pero no respondió el teléfono. Empareja la depuración inalámbrica.";
            Escribir("adb shell Wi-Fi: " + prueba.All);
            return;
        }

        WifiIp = ip;
        WifiPuerto = puerto;
        WifiEstado = $"Conectado a {endpoint}.";
        ModoWifi = true;
        await SondearInternoAsync();
    }

    public async Task DetectarWifiAsync()
    {
        if (Transmitiendo) { WifiEstado = "Detén la cámara antes de buscar Wi-Fi."; return; }

        WifiDispositivos.Clear();
        OnChanged(nameof(HayDispositivosWifi));
        WifiEstado = "Buscando dispositivos en la red local...";
        var servicios = await _adb.MdnsServicesAsync();
        var encontrados = new Dictionary<string, (string Nombre, string? Conexion, string? Emparejamiento)>(StringComparer.OrdinalIgnoreCase);

        foreach (var servicio in servicios)
        {
            bool esConexion = string.Equals(servicio.Type, "_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase);
            bool esEmparejamiento = string.Equals(servicio.Type, "_adb-tls-pairing._tcp", StringComparison.OrdinalIgnoreCase);
            if (!esConexion && !esEmparejamiento) continue;

            int dosPuntos = servicio.Endpoint.LastIndexOf(':');
            if (dosPuntos <= 0 || dosPuntos == servicio.Endpoint.Length - 1) continue;
            string ip = servicio.Endpoint[..dosPuntos].Trim('[', ']');
            string puerto = servicio.Endpoint[(dosPuntos + 1)..];
            if (!encontrados.TryGetValue(ip, out var hallado))
                hallado = (servicio.Name, null, null);

            encontrados[ip] = (
                string.IsNullOrWhiteSpace(hallado.Nombre) ? servicio.Name : hallado.Nombre,
                esConexion ? puerto : hallado.Conexion,
                esEmparejamiento ? puerto : hallado.Emparejamiento);
        }

        foreach (var (ip, datos) in encontrados.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            WifiDispositivos.Add(new WifiDispositivoEncontrado(datos.Nombre, ip, datos.Conexion, datos.Emparejamiento));
        OnChanged(nameof(HayDispositivosWifi));

        WifiEstado = WifiDispositivos.Count == 0
            ? "No encontré dispositivos. Activa Depuración inalámbrica y verifica que ambos estén en la misma red. También puedes usar la IP manual."
            : $"Encontré {WifiDispositivos.Count} dispositivo(s). Elige uno para conectar o emparejarlo.";
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
            MarcarPreferenciaUsb();
            ModoWifi = false;
            WifiEstado = "No habia Wi-Fi activo; se usa USB.";
            await SondearInternoAsync();
            return;
        }

        WifiEstado = "Volviendo a USB...";
        var r = await _adb.RunAsync(new[] { "-s", wifi.Serial, "usb" }, 10000);
        Escribir("adb usb: " + r.All);
        await _adb.DisconnectAsync(wifi.Serial);
        MarcarPreferenciaUsb();
        ModoWifi = false;
        WifiEstado = "ADB volvio a USB.";
        await SondearInternoAsync();
    }

    /// <summary>El usuario pidio USB: bloquea el rescate automatico a Wi-Fi durante la ventana de gracia.</summary>
    private void MarcarPreferenciaUsb() =>
        _usbPreferidoHasta = Environment.TickCount64 + VentanaGraciaUsbMs;

    // ------------------------------------------------------------- video
    private void RealFrame(Frame f)
    {
        if (Interlocked.CompareExchange(ref _fpsCapturaIniciada, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _fpsUltimaMedicionMs, Environment.TickCount64);
            Interlocked.Exchange(ref _fpsFramesVentana, 0);
        }
        Interlocked.Increment(ref _fpsFramesVentana);
        bool cambioAncho = Interlocked.Exchange(ref _anchoFrameReal, f.Width) != f.Width;
        bool cambioAlto = Interlocked.Exchange(ref _altoFrameReal, f.Height) != f.Height;
        if (cambioAncho || cambioAlto)
            Dispatcher.UIThread.Post(() => OnChanged(nameof(ResolucionTexto)));
        Mostrar(f, true);
    }
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
            ThreadPool.QueueUserWorkItem(_ => DrenarFrame());
    }

    private void DrenarFrame()
    {
        try
        {
            var f = Interlocked.Exchange(ref _framePendiente, null);
            if (f is null) return;
            var ajustado = Marco.Aplicar(f, Espejo, RotacionActual, RecorteActual);
            // Publicar para Discord no espera a que la interfaz pinte el preview.
            _camaraVirtual.Publicar(ajustado);
            Interlocked.Exchange(ref _previewFramePendiente, ajustado);
            if (Interlocked.Exchange(ref _previewPendiente, 1) == 0)
                Dispatcher.UIThread.Post(DrenarPreview);
        }
        catch (Exception e) { Dispatcher.UIThread.Post(() => Escribir("video: " + e.Message)); }
        finally
        {
            Volatile.Write(ref _pintadoPendiente, 0);
            if (Volatile.Read(ref _framePendiente) is not null &&
                Interlocked.CompareExchange(ref _pintadoPendiente, 1, 0) == 0)
            {
                try { ThreadPool.QueueUserWorkItem(_ => DrenarFrame()); } catch { }
            }
        }
    }

    private void DrenarPreview()
    {
        try
        {
            var frame = Interlocked.Exchange(ref _previewFramePendiente, null);
            if (frame is not null) Pintar(frame);
        }
        finally
        {
            Volatile.Write(ref _previewPendiente, 0);
            if (Volatile.Read(ref _previewFramePendiente) is not null &&
                Interlocked.CompareExchange(ref _previewPendiente, 1, 0) == 0)
            {
                try { Dispatcher.UIThread.Post(DrenarPreview); } catch { }
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
        TipoZoom = e.TipoZoom; FlashOk = e.FlashDisponible;
        ActualizarTorch(e.Torch);
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
            CambiarEstado(EstadoDispositivo.Transmitiendo, "Transmitiendo", "");
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


public sealed class TemaInfo : INotifyPropertyChanged
{
    private bool _seleccionado;
    public string Id { get; }
    public string Nombre { get; }
    public string Descripcion { get; }
    public bool EsOscuro { get; }
    public string MuestraFondo { get; }
    public string MuestraSuperficie { get; }
    public string MuestraAcento { get; }
    public string Fondo { get; }
    public string Marco { get; }
    public string Lienzo { get; }
    public string Tarjeta { get; }
    public string TarjetaHover { get; }
    public string Tinta { get; }
    public string TintaSuave { get; }
    public string TintaTenue { get; }
    public string Linea { get; }
    public string Indigo { get; }
    public string BotonPrincipal { get; }
    public string BotonPrincipalHover { get; }
    public string BotonPrincipalTexto { get; }
    public Dictionary<string, string> Paleta { get; }
    public bool Seleccionado { get => _seleccionado; set { _seleccionado = value; PropertyChanged?.Invoke(this, new(nameof(Seleccionado))); PropertyChanged?.Invoke(this, new(nameof(ColorBorde))); } }
    public IBrush ColorBorde => new SolidColorBrush(Color.Parse(Seleccionado ? Indigo : Linea));
    public IBrush BrochaFondo => new SolidColorBrush(Color.Parse(MuestraFondo));
    public IBrush BrochaSuperficie => new SolidColorBrush(Color.Parse(MuestraSuperficie));
    public IBrush BrochaAcento => new SolidColorBrush(Color.Parse(MuestraAcento));
    public event PropertyChangedEventHandler? PropertyChanged;

    public TemaInfo(string id, string nombre, string descripcion, bool oscuro, string muestraFondo, string muestraSuperficie, string muestraAcento,
        string fondo, string marco, string lienzo, string tarjeta, string tarjetaHover, string tinta, string tintaSuave, string tintaTenue, string linea, string indigo,
        string boton, string botonHover, string botonTexto)
    {
        Id=id; Nombre=nombre; Descripcion=descripcion; EsOscuro=oscuro; MuestraFondo=muestraFondo; MuestraSuperficie=muestraSuperficie; MuestraAcento=muestraAcento;
        Fondo=fondo; Marco=marco; Lienzo=lienzo; Tarjeta=tarjeta; TarjetaHover=tarjetaHover; Tinta=tinta; TintaSuave=tintaSuave; TintaTenue=tintaTenue; Linea=linea; Indigo=indigo;
        BotonPrincipal=boton; BotonPrincipalHover=botonHover; BotonPrincipalTexto=botonTexto;
        Paleta = new() {
            ["Fondo"]=fondo,["Marco"]=marco,["Lienzo"]=lienzo,["Tarjeta"]=tarjeta,["TarjetaHover"]=tarjetaHover,
            ["PastelGris"]=tarjetaHover,["PastelLavanda"]=muestraAcento,["PastelMenta"]=tarjetaHover,["RosaFondo"]=tarjetaHover,
            ["Insignia"]=tarjetaHover,["Tinta"]=tinta,["TintaSuave"]=tintaSuave,["TintaTenue"]=tintaTenue,["Linea"]=linea,
            ["Indigo"]=indigo,["Rosa"]="#F24D9D",["RosaTexto"]=EsOscuro ? "#FF9FC4" : "#A32E69",["Verde"]="#1E9E63",["PuntoNegro"]=tinta,["PuntoVioleta"]=indigo,["PuntoRosa"]="#F0489B",
            ["BotonPrincipal"]=boton,["BotonPrincipalHover"]=botonHover,["BotonPrincipalTexto"]=botonTexto,["Blanco"]=marco,
            // Popups de FluentTheme (dropdowns y tooltips) con la elevacion del tema.
            ["ComboBoxDropDownBackground"]=EsOscuro ? "#141821" : marco,["ComboBoxDropDownBorderBrush"]=linea,
            ["ComboBoxDropDownGlyphForeground"]=tintaSuave,["ComboBoxDropDownGlyphForegroundFocused"]=tinta,
            ["ComboBoxPlaceHolderForeground"]=tintaTenue,
            ["ComboBoxItemForeground"]=tinta,["ComboBoxItemForegroundPointerOver"]=tinta,["ComboBoxItemForegroundSelected"]=tinta,
            ["ComboBoxItemBackgroundPointerOver"]=tarjetaHover,["ComboBoxItemBackgroundSelected"]=tarjetaHover,
            ["ComboBoxItemBackgroundPressed"]=tarjetaHover,["ComboBoxItemBackgroundSelectedPointerOver"]=tarjetaHover,
            ["ComboBoxItemBackgroundSelectedPressed"]=tarjetaHover,
            ["ToolTipBackground"]=EsOscuro ? "#141821" : marco,["ToolTipBorderBrush"]=linea,["ToolTipForeground"]=tinta,
            ["TextControlBorderBrushFocused"]=indigo,["TextControlBorderBrushPointerOver"]=linea,
            ["AvisoFondo"]=EsOscuro ? "#252016" : "#FFF4E6",["AvisoTexto"]=EsOscuro ? "#E8C072" : "#8A5B12"
        };
    }
}
