using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lenscord.Core;

/// <summary>
/// Coordina todo lo que ocurre en el telefono por adb, sin instalar nada:
/// sube el .dex a /data/local/tmp, lo ejecuta con app_process, abre los tuneles
/// y habla con el canal de control. Al detener, borra absolutamente todo lo suyo.
/// </summary>
public sealed class LensSession : IAsyncDisposable
{
    public const int PuertoVideoDispositivo = 38700;
    public const int PuertoControlDispositivo = 38701;
    public const string RutaDex = "/data/local/tmp/lenscord.dex";
    private const string PaqueteSmartFace = "com.samsung.android.smartface";

    private readonly Adb _adb;
    private readonly string _dexLocal;
    private readonly SemaphoreSlim _parada = new(1, 1);
    private int? _uidSmartFace;
    private bool _smartFacePausado;
    private string _modoCamaraOriginal = "";
    private string? _serieSmartFace;
    private string _stayOnOriginal = "";
    private string _timeoutOriginal = "";
    private bool _pantallaPreparada;
    private bool _wifiDirecto;
    private readonly List<(string Serie, int Local)> _forwards = new();
    private readonly SemaphoreSlim _envio = new(1, 1);
    private readonly object _zoomLock = new();
    private double? _zoomPendiente;
    private bool _zoomEnviando;
    private CancellationTokenSource? _limiteLimpieza;

    public string? Serie { get; private set; }
    public bool LimpiezaOk { get; private set; } = true;
    public bool Activa { get; private set; }
    public EstadoTelefono Estado { get; } = new();
    public IFuenteVideo? Fuente { get; set; }

    public event Action<string>? Log;
    public event Action<EstadoTelefono>? EstadoCambiado;
    public event Action<string>? Error;
    public event Action? ProcesoTerminado;

    private Process? _proceso;
    private TcpClient? _ctrl, _video;
    private StreamWriter? _ctrlWriter;
    private Stream? _videoStream;

    public LensSession(Adb adb, string dexLocal)
    {
        _adb = adb;
        _dexLocal = dexLocal;
    }

    private void Log_(string s) => Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {s}");

    // ------------------------------------------------------------ sondeo
    /// <summary>Ejecuta el payload en modo --probe: solo enumera lo que el telefono
    /// REALMENTE reporta. No abre la camara ni deja nada corriendo.</summary>
    public async Task<string> SondearAsync(string serie, CancellationToken ct)
    {
        await SubirDexAsync(serie, ct).ConfigureAwait(false);
        var r = await _adb.RunAsync(
            new[] { "-s", serie, "shell", $"CLASSPATH={RutaDex} app_process /data/local/tmp com.lenscord.LenscordDevice --probe" },
            30000).ConfigureAwait(false);
        Log_(Loc.T("core.sesion.sondeo", r.All));
        return r.All;
    }

    private async Task SubirDexAsync(string serie, CancellationToken ct)
    {
        if (!File.Exists(_dexLocal))
            throw new FileNotFoundException(Loc.T("core.sesion.dex_no_existe", Rutas.ComandoBuildDex), _dexLocal);
        await _adb.ShellAsync(serie, $"rm -f {RutaDex}", ct: ct).ConfigureAwait(false);
        var push = await _adb.PushAsync(serie, _dexLocal, RutaDex, ct).ConfigureAwait(false);
        if (!push.Ok) throw new InvalidOperationException(Loc.T("core.sesion.dex_no_subido", push.All));
        Log_(Loc.T("core.sesion.dex_subido", new FileInfo(_dexLocal).Length / 1024));
    }

    // ------------------------------------------------------------ inicio
    public async Task IniciarAsync(string serie, CancellationToken ct, bool optimizarWifi = false)
    {
        await _parada.WaitAsync(ct).ConfigureAwait(false);
        try { await IniciarInternoAsync(serie, ct, optimizarWifi).ConfigureAwait(false); }
        finally { _parada.Release(); }
    }

    private async Task IniciarInternoAsync(string serie, CancellationToken ct, bool optimizarWifi)
    {
        Serie = serie;
        Activa = false;
        Estado.Activa = false;
        string? hostWifi = optimizarWifi ? ObtenerHostWifi(serie) : null;
        _wifiDirecto = hostWifi is not null;
        await PausarSmartFaceAsync(serie, ct).ConfigureAwait(false);
        await PrepararPantallaAsync(serie, ct).ConfigureAwait(false);
        if (!_wifiDirecto)
            await _adb.ForwardRemoveRemotosAsync(serie, PuertoVideoDispositivo, PuertoControlDispositivo).ConfigureAwait(false);

        await SubirDexAsync(serie, ct).ConfigureAwait(false);

        int puertoVideo = 0;
        int puertoControl = 0;
        if (!_wifiDirecto)
        {
            puertoVideo = await CrearForwardLocalAsync(serie, PuertoVideoDispositivo).ConfigureAwait(false);
            puertoControl = await CrearForwardLocalAsync(serie, PuertoControlDispositivo).ConfigureAwait(false);
            Log_(Loc.T("core.sesion.tuneles", puertoVideo, puertoControl));
        }
        else
        {
            Log_(Loc.T("core.sesion.wifi_directo", hostWifi));
        }

        var escuchando = new TaskCompletionSource<(int Video, int Control)>(TaskCreationOptions.RunContinuationsAsynchronously);
        string perfilVideo = optimizarWifi ? " 640 480 30" : "";
        string tokenTransporte = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        int puertoVideoDispositivo = _wifiDirecto ? 0 : PuertoVideoDispositivo;
        int puertoControlDispositivo = _wifiDirecto ? 0 : PuertoControlDispositivo;
        string hostEscucha = _wifiDirecto ? "0.0.0.0" : "127.0.0.1";
        string argumentosExtra = $" {hostEscucha}";
        var proceso = _adb.StartShell(new[]
        {
            "-s", serie, "shell",
            $"read -r LENSCORD_TOKEN; export LENSCORD_TOKEN; CLASSPATH={RutaDex} app_process /data/local/tmp com.lenscord.LenscordDevice {puertoVideoDispositivo} {puertoControlDispositivo}{perfilVideo}{argumentosExtra}"
        }, linea =>
        {
            Log_(Loc.T("core.sesion.linea_celular", linea));
            var match = Regex.Match(linea, @"escuchando video=(\d+) control=(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int video) &&
                int.TryParse(match.Groups[2].Value, out int control) && video > 0 && control > 0)
                escuchando.TrySetResult((video, control));
            if (linea.Contains("TRANSMITIENDO")) Estado.Activa = true;
        }, tokenTransporte);
        if (proceso is null) throw new InvalidOperationException(Loc.T("core.sesion.app_process_no_lanzado"));
        _proceso = proceso;

        proceso.EnableRaisingEvents = true;
        proceso.Exited += (_, _) =>
        {
            escuchando.TrySetException(new InvalidOperationException(Loc.T("core.sesion.proceso_termino_antes")));
            if (!Activa) return;
            int codigo = -1;
            try { codigo = proceso.ExitCode; } catch { }
            Error?.Invoke(Loc.T("core.sesion.proceso_termino", codigo));
            ProcesoTerminado?.Invoke();
        };

        (int Video, int Control) puertosDispositivo;
        try
        {
            puertosDispositivo = await escuchando.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(Loc.T("core.sesion.puertos_timeout"));
        }

        string hostConexion = _wifiDirecto ? hostWifi! : "127.0.0.1";
        int puertoControlConexion = _wifiDirecto ? puertosDispositivo.Control : puertoControl;
        int puertoVideoConexion = _wifiDirecto ? puertosDispositivo.Video : puertoVideo;
        Log_(_wifiDirecto
            ? Loc.T("core.sesion.puertos_directos", puertoVideoConexion, puertoControlConexion)
            : Loc.T("core.sesion.puertos_locales", puertoVideoConexion, puertoControlConexion));

        if (_wifiDirecto)
        {
            try
            {
                _ctrl = await ConectarAutenticadoAsync(hostConexion, puertoControlConexion, tokenTransporte, ct,
                    TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is (SocketException or OperationCanceledException))
            {
                Log_(Loc.T("core.sesion.wifi_no_alcanzable", e.Message));
                puertoControlConexion = await CrearForwardLocalAsync(serie, puertosDispositivo.Control).ConfigureAwait(false);
                puertoVideoConexion = await CrearForwardLocalAsync(serie, puertosDispositivo.Video).ConfigureAwait(false);
                hostConexion = "127.0.0.1";
                _wifiDirecto = false;
                _ctrl = await ConectarAutenticadoAsync(hostConexion, puertoControlConexion, tokenTransporte, ct).ConfigureAwait(false);
            }
        }
        else
        {
            _ctrl = await ConectarAutenticadoAsync(hostConexion, puertoControlConexion, tokenTransporte, ct).ConfigureAwait(false);
        }

        var nsCtrl = _ctrl.GetStream();
        _ctrlWriter = new StreamWriter(nsCtrl, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _ = LeerControlAsync(nsCtrl, ct);

        if (_wifiDirecto)
        {
            try
            {
                _video = await ConectarAutenticadoAsync(hostConexion, puertoVideoConexion, tokenTransporte, ct,
                    TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is (SocketException or OperationCanceledException))
            {
                Log_(Loc.T("core.sesion.video_directo_no_alcanzable", e.Message));
                puertoVideoConexion = await CrearForwardLocalAsync(serie, puertosDispositivo.Video).ConfigureAwait(false);
                _video = await ConectarAutenticadoAsync("127.0.0.1", puertoVideoConexion, tokenTransporte, ct).ConfigureAwait(false);
                _wifiDirecto = false;
            }
        }
        else
        {
            _video = await ConectarAutenticadoAsync(hostConexion, puertoVideoConexion, tokenTransporte, ct).ConfigureAwait(false);
        }

        _videoStream = _video.GetStream();
        Log_(_wifiDirecto ? Loc.T("core.sesion.video_control_wifi") : Loc.T("core.sesion.conexiones_abiertas"));

        Activa = true;
        Estado.Activa = true;
    }

    private static string? ObtenerHostWifi(string serie)
    {
        int separador = serie.LastIndexOf(':');
        if (separador <= 0) return null;
        string host = serie[..separador].Trim('[', ']');
        return IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            ? host
            : null;
    }

    private async Task<int> CrearForwardLocalAsync(string serie, int puertoDispositivo)
    {
        int? puertoLocal = await _adb.ForwardDinamicoAsync(serie, puertoDispositivo).ConfigureAwait(false);
        if (puertoLocal is null)
            throw new InvalidOperationException(Loc.T("core.sesion.tunel_no_creado", puertoDispositivo));
        _forwards.Add((serie, puertoLocal.Value));
        return puertoLocal.Value;
    }

    private static async Task<TcpClient> ConectarAutenticadoAsync(
        string host, int puerto, string token, CancellationToken ct, TimeSpan? timeoutMaximo = null)
    {
        var cliente = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMaximo ?? TimeSpan.FromSeconds(15));
            await cliente.ConnectAsync(host, puerto, timeout.Token).ConfigureAwait(false);
            var flujo = cliente.GetStream();
            byte[] nonce = new byte[16];
            await flujo.ReadExactlyAsync(nonce, timeout.Token).ConfigureAwait(false);
            byte[] respuesta = HMACSHA256.HashData(Encoding.ASCII.GetBytes(token), nonce);
            await flujo.WriteAsync(respuesta, timeout.Token).ConfigureAwait(false);
            return cliente;
        }
        catch
        {
            cliente.Dispose();
            throw;
        }
    }

    private async Task LeerControlAsync(Stream s, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(s, new UTF8Encoding(false));
            while (!ct.IsCancellationRequested)
            {
                var linea = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (linea is null) break;
                if (linea.Length == 0) continue;
                Aplicar(linea);
            }
        }
        catch (Exception e) { if (Activa) Error?.Invoke(Loc.T("core.sesion.canal_control_error", e.Message)); }
    }

    private void Aplicar(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("t", out var tp)) return;
            switch (tp.GetString())
            {
                case "hello":
                    Estado.Modelo = Get(r, "model");
                    Estado.SdkInt = GetI(r, "sdk");
                    Estado.VersionAndroid = Get(r, "release");
                    Estado.ComoObtuvoContext = Get(r, "ctxHow");
                    Estado.CamaraActual = Get(r, "camera");
                    Estado.Torch = GetB(r, "torch");
                    Estado.FlashDisponible = GetB(r, "flashAvailable");
                    Estado.TipoZoom = Get(r, "zoomKind");
                    Estado.Ancho = GetI(r, "w");
                    Estado.Alto = GetI(r, "h");
                    Estado.Fps = GetI(r, "fps");
                    var camaras = new List<CamaraInfo>();
                    if (r.TryGetProperty("cameras", out var cams) && cams.ValueKind == JsonValueKind.Array)
                        foreach (var c in cams.EnumerateArray())
                            camaras.Add(DeserializeCamara(c));
                    Estado.Camaras = camaras;
                    Log_(Loc.T("core.sesion.telefono_info", Estado.Modelo, Estado.VersionAndroid, Estado.SdkInt,
                       Estado.Camaras.Count, Estado.ComoObtuvoContext));
                    EstadoCambiado?.Invoke(Estado);
                    break;

                case "state":
                    Estado.CamaraActual = Get(r, "camera");
                    Estado.Torch = GetB(r, "torch");
                    Estado.FlashDisponible = GetB(r, "flashAvailable");
                    Estado.TipoZoom = Get(r, "zoomKind");
                    Estado.Pausado = GetB(r, "paused");
                    Estado.ZoomRatio = GetD(r, "zoomRatio");
                    Estado.ZoomRatioMin = GetD(r, "zoomRatioMin");
                    Estado.ZoomRatioMax = GetD(r, "zoomRatioMax");
                    Estado.Ancho = GetI(r, "w"); Estado.Alto = GetI(r, "h"); Estado.Fps = GetI(r, "fps");
                    EstadoCambiado?.Invoke(Estado);
                    break;

                case "stats":
                    Estado.FpsMedidos = GetD(r, "fps");
                    EstadoCambiado?.Invoke(Estado);
                    break;

                case "error":
                case "nack":
                    Error?.Invoke(Get(r, "m"));
                    break;

                case "busy":
                    Error?.Invoke(Get(r, "m"));
                    break;
            }
        }
        catch (Exception e) { Log_(Loc.T("core.sesion.json_invalido", json, e.Message)); }
    }

    private static CamaraInfo DeserializeCamara(JsonElement c) => new()
    {
        Id = Get(c, "id"),
        Facing = Get(c, "facing"),
        Sizes = c.TryGetProperty("sizes", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>(),
        MaxDigitalZoom = GetD(c, "maxDigitalZoom"),
        RatioMin = GetD(c, "ratioMin"),
        RatioMax = GetD(c, "ratioMax"),
        HasRatio = GetB(c, "hasRatio"),
        Flash = GetB(c, "flash"),
        HwLevel = GetI(c, "hwLevel"),
        Physical = c.TryGetProperty("physical", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(),
    };

    private static string Get(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) ? v.ToString() : "";
    private static bool GetB(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    private static int GetI(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : 0;
    private static double GetD(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    // ------------------------------------------------------------ control
    public async Task EnviarAsync(string json, bool registrar = true)
    {
        var escritor = _ctrlWriter;
        if (escritor is null) { Error?.Invoke(Loc.T("core.sesion.sin_conexion_control")); return; }
        await _envio.WaitAsync().ConfigureAwait(false);
        try
        {
            await escritor.WriteLineAsync(json).ConfigureAwait(false);
            if (registrar) Log_(Loc.T("core.sesion.orden", json));
        }
        catch (Exception e)
        {
            Log_(Loc.T("core.sesion.orden_no_enviada", e.Message));
        }
        finally
        {
            _envio.Release();
        }
    }

    public async Task ZoomAsync(double fraccion)
    {
        lock (_zoomLock)
        {
            _zoomPendiente = fraccion;
            if (_zoomEnviando) return;
            _zoomEnviando = true;
        }
        while (true)
        {
            double valor;
            lock (_zoomLock)
            {
                if (_zoomPendiente is null) { _zoomEnviando = false; return; }
                valor = _zoomPendiente.Value;
                _zoomPendiente = null;
            }
            string v = valor.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            await EnviarAsync($"{{\"cmd\":\"zoom\",\"value\":{v}}}", false).ConfigureAwait(false);
        }
    }

    public Task TorchAsync(bool on) => EnviarAsync($"{{\"cmd\":\"torch\",\"on\":{(on ? "true" : "false")}}}");
    public Task CamaraAsync(string id) => EnviarAsync($"{{\"cmd\":\"camera\",\"id\":{JsonSerializer.Serialize(id)}}}");
    public Task PausaAsync(bool on) => EnviarAsync($"{{\"cmd\":\"pause\",\"on\":{(on ? "true" : "false")}}}");
    public Task FormatoAsync(int w, int h, int fps) => EnviarAsync($"{{\"cmd\":\"format\",\"w\":{w},\"h\":{h},\"fps\":{fps}}}");
    public Task PedirCamarasAsync() => EnviarAsync("{\"cmd\":\"cameras\"}");

    public Stream? FlujoVideo => _videoStream;

    private async Task<string?> TransporteAlternoAsync(string serie)
    {
        var ds = await _adb.DevicesAsync().ConfigureAwait(false);
        if (ds.Any(d => d.State == "device" && d.Serial == serie)) return null;

        var candidatos = ds.Where(d => d.State == "device" && d.Serial != serie).ToList();
        if (candidatos.Count == 0) return null;

        string modelo = Normalizar(Estado.Modelo);
        if (modelo.Length == 0) return null;
        var mismos = candidatos.Where(d => Normalizar(d.Model) == modelo).ToList();
        return mismos.Count == 1 ? mismos[0].Serial : null;
    }

    private static string Normalizar(string s) => s.Replace("-", "").Replace(" ", "").ToLowerInvariant();

    // ------------------------------------------------------------ ajustes de pantalla en disco
    private const string TimeoutPantallaSesion = "2147483647";

    private sealed record PantallaGuardada(string SerialFisico, string StayOn, string Timeout);

    private static string RutaPantallaGuardada => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lenscord", "estado-telefono.json");

    private void GuardarPantalla(PantallaGuardada p)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RutaPantallaGuardada)!);
            File.WriteAllText(RutaPantallaGuardada, JsonSerializer.Serialize(p));
        }
        catch (Exception e) { Log_(Loc.T("core.sesion.guardar_pantalla_error", e.Message)); }
    }

    private static PantallaGuardada? CargarPantallaGuardada()
    {
        try
        {
            return File.Exists(RutaPantallaGuardada)
                ? JsonSerializer.Deserialize<PantallaGuardada>(File.ReadAllText(RutaPantallaGuardada))
                : null;
        }
        catch { return null; }
    }

    private static void BorrarPantallaGuardada()
    {
        try { File.Delete(RutaPantallaGuardada); } catch { }
    }

    private async Task<Adb.Result> ShellConRespaldoAsync(string serie, string command, int timeoutMs = 20000)
    {
        var limite = _limiteLimpieza?.Token ?? default;
        var r = await _adb.ShellAsync(serie, command, timeoutMs, limite).ConfigureAwait(false);
        if (r.Ok || limite.IsCancellationRequested) return r;
        var alterno = await TransporteAlternoAsync(serie).ConfigureAwait(false);
        if (alterno is null) return r;
        Log_(Loc.T("core.sesion.transporte_alterno", serie, alterno));
        return await _adb.ShellAsync(alterno, command, timeoutMs, limite).ConfigureAwait(false);
    }

    private async Task<bool> AppopsCoincideAsync(string serie, int uid, string modo)
    {
        string esperado = modo == "allow" ? "(?:allow|foreground)" : Regex.Escape(modo);
        var r = await ShellConRespaldoAsync(serie, $"cmd appops get --uid {uid} CAMERA", 10000).ConfigureAwait(false);
        return r.Ok && Regex.IsMatch(r.All, $@"Uid mode:\s*CAMERA:\s*{esperado}");
    }

    private async Task<bool> FijarAppopsAsync(string serie, int uid, string modo)
    {
        await ShellConRespaldoAsync(serie, $"cmd appops set --uid {uid} CAMERA {modo}", 10000).ConfigureAwait(false);
        return await AppopsCoincideAsync(serie, uid, modo).ConfigureAwait(false);
    }

    private async Task PrepararPantallaAsync(string serie, CancellationToken ct)
    {
        _pantallaPreparada = false;
        _stayOnOriginal = "";
        _timeoutOriginal = "";
        ct.ThrowIfCancellationRequested();

        var id = await ShellConRespaldoAsync(serie, "getprop ro.serialno", 10000).ConfigureAwait(false);
        string serialFisico = id.Ok ? id.StdOut.Trim() : "";

        var guardado = CargarPantallaGuardada();
        if (guardado is not null && serialFisico.Length > 0 && guardado.SerialFisico == serialFisico)
        {
            _stayOnOriginal = guardado.StayOn;
            _timeoutOriginal = guardado.Timeout;
            Log_(Loc.T("core.sesion.ajustes_recuperados"));
        }
        else
        {
            var stay = await ShellConRespaldoAsync(
                serie, "settings get global stay_on_while_plugged_in", 10000).ConfigureAwait(false);
            var timeout = await ShellConRespaldoAsync(
                serie, "settings get system screen_off_timeout", 10000).ConfigureAwait(false);
            _stayOnOriginal = stay.Ok ? stay.All.Trim() : "";
            _timeoutOriginal = timeout.Ok ? timeout.All.Trim() : "";
            if (_timeoutOriginal == TimeoutPantallaSesion) _timeoutOriginal = "";
            if (serialFisico.Length > 0)
                GuardarPantalla(new PantallaGuardada(serialFisico, _stayOnOriginal, _timeoutOriginal));
        }

        var s = await ShellConRespaldoAsync(
            serie, "settings put global stay_on_while_plugged_in 7", 10000).ConfigureAwait(false);
        var t = await ShellConRespaldoAsync(
            serie, $"settings put system screen_off_timeout {TimeoutPantallaSesion}", 10000).ConfigureAwait(false);
        await ShellConRespaldoAsync(serie, "input keyevent KEYCODE_WAKEUP", 10000).ConfigureAwait(false);

        _pantallaPreparada = s.Ok && t.Ok;
        if (_pantallaPreparada)
            Log_(Loc.T("core.sesion.pantalla_encendida"));
        else
            Log_(Loc.T("core.sesion.pantalla_no_mantenida"));
    }

    private async Task RestaurarPantallaAsync()
    {
        if (!_pantallaPreparada || _serieSmartFace is null) return;

        string stay = Regex.IsMatch(_stayOnOriginal, @"^\d+$") ? _stayOnOriginal : "0";
        string timeout = Regex.IsMatch(_timeoutOriginal, @"^\d+$") ? _timeoutOriginal : "600000";
        var s = await ShellConRespaldoAsync(
            _serieSmartFace, $"settings put global stay_on_while_plugged_in {stay}", 10000).ConfigureAwait(false);
        var t = await ShellConRespaldoAsync(
            _serieSmartFace, $"settings put system screen_off_timeout {timeout}", 10000).ConfigureAwait(false);
        if (s.Ok && t.Ok)
        {
            _pantallaPreparada = false;
            BorrarPantallaGuardada();
            Log_(Loc.T("core.sesion.pantalla_restaurada"));
        }
        else
        {
            Log_(Loc.T("core.sesion.pantalla_no_restaurada"));
        }
    }

    public async Task ReintentarRestauracionAsync()
    {
        if (!_smartFacePausado && !_pantallaPreparada) return;
        if (!await _parada.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            await RestaurarSmartFaceAsync().ConfigureAwait(false);
            await RestaurarPantallaAsync().ConfigureAwait(false);
        }
        finally
        {
            _parada.Release();
        }
    }

    // ------------------------------------------------------------ desbloqueo facial
    private async Task PausarSmartFaceAsync(string serie, CancellationToken ct)
    {
        _uidSmartFace = null;
        _smartFacePausado = false;
        _modoCamaraOriginal = "";
        _serieSmartFace = serie;
        ct.ThrowIfCancellationRequested();

        var paquetes = await ShellConRespaldoAsync(
            serie, $"cmd package list packages -U {PaqueteSmartFace}", 10000).ConfigureAwait(false);
        if (!paquetes.Ok)
        {
            Log_(Loc.T("core.sesion.smartface_paquete_error", paquetes.All));
            return;
        }

        var encontrado = Regex.Match(paquetes.StdOut,
            @"^package:" + Regex.Escape(PaqueteSmartFace) + @" uid:(\d+)\r?$",
            RegexOptions.Multiline);
        if (!encontrado.Success || !int.TryParse(encontrado.Groups[1].Value, out int uid))
        {
            Log_(Loc.T("core.sesion.smartface_ausente"));
            return;
        }
        _uidSmartFace = uid;

        var lectura = await ShellConRespaldoAsync(
            serie, $"cmd appops get --uid {uid} CAMERA", 10000).ConfigureAwait(false);
        var modo = Regex.Match(lectura.All, @"Uid mode:\s*CAMERA:\s*(\w+)");
        _modoCamaraOriginal = modo.Success ? modo.Groups[1].Value : "";

        if (_modoCamaraOriginal == "deny")
        {
            Log_(Loc.T("core.sesion.smartface_bloqueada"));
            return;
        }
        if (_modoCamaraOriginal == "ignore")
        {
            if (await FijarAppopsAsync(serie, uid, "allow").ConfigureAwait(false))
                Log_(Loc.T("core.sesion.smartface_pausa_restaurada"));
            else
                Log_(Loc.T("core.sesion.smartface_limpieza_fallida"));
            _modoCamaraOriginal = "allow";
        }

        _smartFacePausado = true;
        if (await FijarAppopsAsync(serie, uid, "ignore").ConfigureAwait(false))
            Log_(Loc.T("core.sesion.smartface_pausado", uid));
        else
            Log_(Loc.T("core.sesion.smartface_no_pausado"));
    }

    private async Task RestaurarSmartFaceAsync()
    {
        if (!_smartFacePausado || _uidSmartFace is null || _serieSmartFace is null) return;
        string modo = _modoCamaraOriginal == "deny" ? "deny" : "allow";

        for (int intento = 0; intento < 3; intento++)
        {
            if (await FijarAppopsAsync(_serieSmartFace, _uidSmartFace.Value, modo).ConfigureAwait(false))
            {
                _smartFacePausado = false;
                Log_(Loc.T("core.sesion.smartface_restaurado", _uidSmartFace, modo));
                return;
            }
            if (intento < 2) await Task.Delay(500).ConfigureAwait(false);
        }

        Log_(Loc.T("core.sesion.smartface_no_confirmado"));
    }

    // ------------------------------------------------------------ parada
    public async Task DetenerAsync()
    {
        Activa = false;
        Estado.Activa = false;
        await _parada.WaitAsync().ConfigureAwait(false);
        _limiteLimpieza = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            LimpiezaOk = true;
            var escritor = _ctrlWriter;
            _ctrlWriter = null;
            try
            {
                if (escritor is not null && await _envio.WaitAsync(500).ConfigureAwait(false))
                {
                    try { await escritor.WriteLineAsync("{\"cmd\":\"stop\"}").ConfigureAwait(false); }
                    finally { _envio.Release(); }
                }
            }
            catch { }

            try { if (_videoStream is not null) _videoStream.Dispose(); } catch { }
            try { if (_ctrl is not null) _ctrl.Dispose(); } catch { }

            await DetenerProcesoAsync().ConfigureAwait(false);

            try { await RestaurarSmartFaceAsync().ConfigureAwait(false); }
            catch (Exception e) { Log_(Loc.T("core.sesion.smartface_restaurar_error", e.Message)); }

            try { await RestaurarPantallaAsync().ConfigureAwait(false); }
            catch (Exception e) { Log_(Loc.T("core.sesion.pantalla_restaurar_error", e.Message)); }

            try
            {
                if (Fuente is not null) { await Fuente.DetenerAsync().ConfigureAwait(false); Fuente = null; }
            }
            catch (Exception e) { Log_(Loc.T("core.sesion.decodificador_error", e.Message)); }

            if (Serie is not null)
            {
                await QuitarForwardsAsync().ConfigureAwait(false);

                LimpiezaOk = await LimpiarTelefonoAsync(Serie).ConfigureAwait(false);
                if (!LimpiezaOk)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    LimpiezaOk = await LimpiarTelefonoAsync(Serie).ConfigureAwait(false);
                }
                if (!LimpiezaOk) Log_(Loc.T("core.sesion.limpieza_no_confirmada"));
            }
        }
        finally
        {
            Serie = null;
            _wifiDirecto = false;
            _limiteLimpieza?.Dispose();
            _limiteLimpieza = null;
            _parada.Release();
        }
    }

    private async Task QuitarForwardsAsync()
    {
        var pendientes = _forwards.ToList();
        _forwards.Clear();
        foreach (var (serie, local) in pendientes)
        {
            var r = await _adb.ForwardRemoveAsync(serie, local).ConfigureAwait(false);
            if (r.Ok) continue;
            var alterno = await TransporteAlternoAsync(serie).ConfigureAwait(false);
            if (alterno is not null) await _adb.ForwardRemoveAsync(alterno, local).ConfigureAwait(false);
        }
    }

    private async Task DetenerProcesoAsync()
    {
        if (_proceso is null) return;
        try
        {
            if (Serie is not null)
            {
                // app_process no responde a senales desde Windows: se mata por nombre.
                // El patron [c]om evita que pkill se mate a si mismo (el comando
                // del propio shell tambien contiene el texto buscado).
                await ShellConRespaldoAsync(
                    Serie, "pkill -f '[c]om.lenscord.LenscordDevice'", 8000).ConfigureAwait(false);
            }
        }
        catch { }
        try { if (!_proceso.HasExited) { _proceso.Kill(true); await _proceso.WaitForExitAsync(new CancellationTokenSource(3000).Token); } } catch { }
        try { _proceso.Dispose(); } catch { }
        _proceso = null;
    }

    /// <summary>Borra del celular TODO lo nuestro y comprueba que no queda nada.</summary>
    public async Task<bool> LimpiarTelefonoAsync(string serie)
    {
        var r = await ShellConRespaldoAsync(serie,
            $"rm -f {RutaDex}; pkill -f '[c]om.lenscord.LenscordDevice'; rm -f {RutaDex}; " +
            "rm -f /data/local/tmp/lenscord 2>/dev/null; " +
            "ls /data/local/tmp/ | grep -i lenscord || echo 'LIMPIO: sin rastros de lenscord en /data/local/tmp'",
            15000).ConfigureAwait(false);
        Log_(Loc.T("core.sesion.limpieza_celular", r.All));
        EstadoCambiado?.Invoke(Estado);
        return r.Ok && r.All.Contains("LIMPIO", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync() => await DetenerAsync();

}
