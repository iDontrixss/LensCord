using System.Diagnostics;
using System.Net.Sockets;
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
        Log_("sondeo: " + r.All);
        return r.All;
    }

    private async Task SubirDexAsync(string serie, CancellationToken ct)
    {
        if (!File.Exists(_dexLocal))
            throw new FileNotFoundException("No existe el .dex compilado. Ejecuta: dispositivo\\build-dex.ps1", _dexLocal);
        await _adb.ShellAsync(serie, $"rm -f {RutaDex}").ConfigureAwait(false);
        var push = await _adb.PushAsync(serie, _dexLocal, RutaDex).ConfigureAwait(false);
        if (!push.Ok) throw new InvalidOperationException("No se pudo subir el .dex: " + push.All);
        Log_($"dex subido ({new FileInfo(_dexLocal).Length / 1024} KB)");
    }

    // ------------------------------------------------------------ inicio
    public async Task IniciarAsync(string serie, CancellationToken ct)
    {
        Serie = serie;
        Activa = false;
        Estado.Activa = false;
        await PausarSmartFaceAsync(serie, ct).ConfigureAwait(false);
        await PrepararPantallaAsync(serie, ct).ConfigureAwait(false);
        await _adb.ForwardRemoveAllAsync(serie).ConfigureAwait(false);

        await SubirDexAsync(serie, ct).ConfigureAwait(false);

        int puertoVideo = PuertoLocalLibre();
        int puertoControl = PuertoLocalLibre();
        if (puertoControl == puertoVideo) puertoControl++;

        var fv = await _adb.ForwardAsync(serie, $"tcp:{puertoVideo}", $"tcp:{PuertoVideoDispositivo}").ConfigureAwait(false);
        var fc = await _adb.ForwardAsync(serie, $"tcp:{puertoControl}", $"tcp:{PuertoControlDispositivo}").ConfigureAwait(false);
        if (!fv.Ok || !fc.Ok)
            throw new InvalidOperationException("No se pudieron abrir los tuneles: " + fv.All + " " + fc.All);
        Log_($"tuneles: video {puertoVideo}, control {puertoControl}");

        // adb forward acepta la conexion local aunque el otro extremo todavia no
        // escuche; si conectamos antes, el tunel se cierra y el telefono espera
        // para siempre. Esperamos su aviso "escuchando".
        var escuchando = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var proceso = _adb.StartShell(new[]
        {
            "-s", serie, "shell",
            $"CLASSPATH={RutaDex} app_process /data/local/tmp com.lenscord.LenscordDevice {PuertoVideoDispositivo} {PuertoControlDispositivo}"
        }, linea =>
        {
            Log_("celular: " + linea);
            if (linea.Contains("escuchando")) escuchando.TrySetResult(true);
            if (linea.Contains("TRANSMITIENDO")) Estado.Activa = true;
        });
        if (proceso is null) throw new InvalidOperationException("No se pudo lanzar app_process en el celular");
        _proceso = proceso;

        // Si el proceso del celular muere (error de camara, kill remoto), la PC
        // debe enterarse en vez de quedarse diciendo "Transmitiendo" para siempre.
        proceso.EnableRaisingEvents = true;
        proceso.Exited += (_, _) =>
        {
            escuchando.TrySetException(new InvalidOperationException("El proceso del celular termino antes de abrir los puertos."));
            if (!Activa) return;
            int codigo = -1;
            try { codigo = proceso.ExitCode; } catch { }
            Error?.Invoke($"El proceso del celular termino (codigo {codigo}). No hay video.");
            ProcesoTerminado?.Invoke();
        };

        try
        {
            await escuchando.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("El telefono no abrio los puertos de video/control a tiempo.");
        }

        // El control se conecta primero: ahi llega el inventario real de camaras.
        _ctrl = new TcpClient();
        await _ctrl.ConnectAsync("127.0.0.1", puertoControl, ct).ConfigureAwait(false);
        var nsCtrl = _ctrl.GetStream();
        _ctrlWriter = new StreamWriter(nsCtrl, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _ = LeerControlAsync(nsCtrl, ct);

        // El video: esperamos a que el telefono acepte la conexion.
        _video = new TcpClient();
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            await _video.ConnectAsync("127.0.0.1", puertoVideo, cts.Token).ConfigureAwait(false);
        }
        _videoStream = _video.GetStream();
        Log_("conexiones de video y control abiertas");

        Activa = true;
        Estado.Activa = true;
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
        catch (Exception e) { if (Activa) Error?.Invoke("canal de control: " + e.Message); }
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
                    Estado.FlashDisponible = GetB(r, "flashAvailable");
                    Estado.TipoZoom = Get(r, "zoomKind");
                    Estado.Ancho = GetI(r, "w");
                    Estado.Alto = GetI(r, "h");
                    Estado.Fps = GetI(r, "fps");
                    Estado.Camaras.Clear();
                    if (r.TryGetProperty("cameras", out var cams) && cams.ValueKind == JsonValueKind.Array)
                        foreach (var c in cams.EnumerateArray())
                            Estado.Camaras.Add(DeserializeCamara(c));
                    Log_($"telefono: {Estado.Modelo} Android {Estado.VersionAndroid} (API {Estado.SdkInt}), "
                       + $"{Estado.Camaras.Count} camara(s); context={Estado.ComoObtuvoContext}");
                    EstadoCambiado?.Invoke(Estado);
                    break;

                case "state":
                    Estado.CamaraActual = Get(r, "camera");
                    Estado.FlashDisponible = GetB(r, "flashAvailable");
                    Estado.TipoZoom = Get(r, "zoomKind");
                    Estado.Pausado = GetB(r, "paused");
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
        catch (Exception e) { Log_("json de control invalido: " + json + " (" + e.Message + ")"); }
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
    public async Task EnviarAsync(string json)
    {
        if (_ctrlWriter is null) { Error?.Invoke("No hay conexion de control activa."); return; }
        await _ctrlWriter.WriteLineAsync(json).ConfigureAwait(false);
        Log_("orden: " + json);
    }

    public Task ZoomAsync(double fraccion) => EnviarAsync($"{{\"cmd\":\"zoom\",\"value\":{fraccion.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}}}");
    public Task TorchAsync(bool on) => EnviarAsync($"{{\"cmd\":\"torch\",\"on\":{(on ? "true" : "false")}}}");
    public Task CamaraAsync(string id) => EnviarAsync($"{{\"cmd\":\"camera\",\"id\":\"{id}\"}}");
    public Task PausaAsync(bool on) => EnviarAsync($"{{\"cmd\":\"pause\",\"on\":{(on ? "true" : "false")}}}");
    public Task FormatoAsync(int w, int h, int fps) => EnviarAsync($"{{\"cmd\":\"format\",\"w\":{w},\"h\":{h},\"fps\":{fps}}}");
    public Task PedirCamarasAsync() => EnviarAsync("{\"cmd\":\"cameras\"}");

    public Stream? FlujoVideo => _videoStream;

    private async Task<string?> TransporteAlternoAsync(string serie)
    {
        var ds = await _adb.DevicesAsync().ConfigureAwait(false);
        var candidatos = ds.Where(d => d.State == "device" && d.Serial != serie).ToList();
        if (candidatos.Count == 0) return null;

        string modelo = Estado.Modelo.Replace("-", "").Replace(" ", "").ToLowerInvariant();
        if (modelo.Length > 0)
        {
            var mismo = candidatos.FirstOrDefault(d =>
                d.Model.Replace("-", "").Replace(" ", "").ToLowerInvariant() == modelo);
            if (mismo is not null) return mismo.Serial;
        }
        return candidatos[0].Serial;
    }

    private async Task<Adb.Result> ShellConRespaldoAsync(string serie, string command, int timeoutMs = 20000)
    {
        var r = await _adb.ShellAsync(serie, command, timeoutMs).ConfigureAwait(false);
        if (r.Ok) return r;
        var alterno = await TransporteAlternoAsync(serie).ConfigureAwait(false);
        if (alterno is null) return r;
        Log_($"transporte {serie} no respondio; reintentando por {alterno}.");
        return await _adb.ShellAsync(alterno, command, timeoutMs).ConfigureAwait(false);
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

        var stay = await ShellConRespaldoAsync(
            serie, "settings get global stay_on_while_plugged_in", 10000).ConfigureAwait(false);
        var timeout = await ShellConRespaldoAsync(
            serie, "settings get system screen_off_timeout", 10000).ConfigureAwait(false);
        _stayOnOriginal = stay.Ok ? stay.All.Trim() : "";
        _timeoutOriginal = timeout.Ok ? timeout.All.Trim() : "";

        var s = await ShellConRespaldoAsync(
            serie, "settings put global stay_on_while_plugged_in 7", 10000).ConfigureAwait(false);
        var t = await ShellConRespaldoAsync(
            serie, "settings put system screen_off_timeout 2147483647", 10000).ConfigureAwait(false);
        await ShellConRespaldoAsync(serie, "input keyevent KEYCODE_WAKEUP", 10000).ConfigureAwait(false);

        _pantallaPreparada = s.Ok && t.Ok;
        if (_pantallaPreparada)
            Log_("pantalla encendida durante la sesion; la camara se apaga si el telefono duerme.");
        else
            Log_("No se pudo mantener la pantalla encendida; no dejes que el telefono duerma.");
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
            Log_("pantalla restaurada.");
        }
        else
        {
            Log_("No se pudo restaurar la pantalla; se reintentara.");
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
            Log_("SmartFace: no se pudo consultar el paquete (" + paquetes.All + ")");
            return;
        }

        var encontrado = Regex.Match(paquetes.StdOut,
            @"^package:" + Regex.Escape(PaqueteSmartFace) + @" uid:(\d+)\r?$",
            RegexOptions.Multiline);
        if (!encontrado.Success || !int.TryParse(encontrado.Groups[1].Value, out int uid))
        {
            Log_("SmartFace no esta presente; no hace falta pausar el desbloqueo facial.");
            return;
        }
        _uidSmartFace = uid;

        var lectura = await ShellConRespaldoAsync(
            serie, $"cmd appops get --uid {uid} CAMERA", 10000).ConfigureAwait(false);
        var modo = Regex.Match(lectura.All, @"Uid mode:\s*CAMERA:\s*(\w+)");
        _modoCamaraOriginal = modo.Success ? modo.Groups[1].Value : "";

        if (_modoCamaraOriginal == "deny")
        {
            Log_("SmartFace ya tiene la camara bloqueada; no se modifica.");
            return;
        }
        if (_modoCamaraOriginal == "ignore")
        {
            if (await FijarAppopsAsync(serie, uid, "allow").ConfigureAwait(false))
                Log_("pausa anterior de SmartFace restaurada antes de iniciar.");
            else
                Log_("No se pudo limpiar la pausa anterior de SmartFace; se intentara al detener.");
            _modoCamaraOriginal = "allow";
        }

        _smartFacePausado = true;
        if (await FijarAppopsAsync(serie, uid, "ignore").ConfigureAwait(false))
            Log_($"SmartFace pausado (UID {uid}); el desbloqueo facial vuelve al detener.");
        else
            Log_("SmartFace no quedo pausado; se restaurara de todos modos al detener.");
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
                Log_($"SmartFace restaurado (UID {_uidSmartFace}, CAMERA={modo}).");
                return;
            }
            if (intento < 2) await Task.Delay(500).ConfigureAwait(false);
        }

        Log_("No se pudo confirmar la restauracion de SmartFace; se reintentara en la proxima detencion.");
    }

    // ------------------------------------------------------------ parada
    public async Task DetenerAsync()
    {
        Activa = false;
        Estado.Activa = false;
        await _parada.WaitAsync().ConfigureAwait(false);
        try
        {
            LimpiezaOk = true;
            try { if (_ctrlWriter is not null) await _ctrlWriter.WriteLineAsync("{\"cmd\":\"stop\"}").ConfigureAwait(false); } catch { }

            try { if (_videoStream is not null) _videoStream.Dispose(); } catch { }
            try { if (_ctrl is not null) _ctrl.Dispose(); } catch { }

            await DetenerProcesoAsync().ConfigureAwait(false);

            try { await RestaurarSmartFaceAsync().ConfigureAwait(false); }
            catch (Exception e) { Log_("No se pudo restaurar SmartFace: " + e.Message); }

            try { await RestaurarPantallaAsync().ConfigureAwait(false); }
            catch (Exception e) { Log_("No se pudo restaurar la pantalla: " + e.Message); }

            try
            {
                if (Fuente is not null) { await Fuente.DetenerAsync().ConfigureAwait(false); Fuente = null; }
            }
            catch (Exception e) { Log_("No se pudo detener el decodificador: " + e.Message); }

            if (Serie is not null)
            {
                var fw = await _adb.ForwardRemoveAllAsync(Serie).ConfigureAwait(false);
                if (!fw.Ok)
                {
                    var alterno = await TransporteAlternoAsync(Serie).ConfigureAwait(false);
                    if (alterno is not null) await _adb.ForwardRemoveAllAsync(alterno).ConfigureAwait(false);
                    await _adb.ForwardRemoveAllGlobalAsync().ConfigureAwait(false);
                    Log_("tuneles: se limpio la lista global tras fallar el transporte principal.");
                }

                LimpiezaOk = await LimpiarTelefonoAsync(Serie).ConfigureAwait(false);
                if (!LimpiezaOk)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    LimpiezaOk = await LimpiarTelefonoAsync(Serie).ConfigureAwait(false);
                }
                if (!LimpiezaOk) Log_("No se pudo confirmar la limpieza del telefono.");
            }
        }
        finally
        {
            Serie = null;
            _parada.Release();
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
        Log_("limpieza del celular: " + r.All);
        EstadoCambiado?.Invoke(Estado);
        return r.Ok && r.All.Contains("LIMPIO", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync() => await DetenerAsync();

    // ------------------------------------------------------------ utiles
    private static int PuertoLocalLibre()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}