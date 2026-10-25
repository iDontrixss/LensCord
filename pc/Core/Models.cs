using System.Text.Json.Serialization;

namespace Lenscord.Core;

/// <summary>Estado visible del telefono. Cada uno lleva consigo que debe hacer el usuario.</summary>
public enum EstadoDispositivo
{
    SinCable,
    SinDepuracion,
    SinAutorizar,
    SinEmparejarWifi,
    RedDistinta,
    PuertoCambio,
    Listo,
    Transmitiendo,
    Error,
}

/// <summary>Origen de los pixeles que se ven en pantalla.</summary>
public enum FuenteVideo
{
    Nada,
    Telefono,
    Simulada,
}

public sealed class CamaraInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("facing")] public string Facing { get; set; } = "UNKNOWN";
    /// <summary>Pares [ancho, alto, ancho, alto, ...] tal como los reporta el telefono.</summary>
    [JsonPropertyName("sizes")] public int[] Sizes { get; set; } = Array.Empty<int>();
    [JsonPropertyName("maxDigitalZoom")] public double MaxDigitalZoom { get; set; } = 1;
    [JsonPropertyName("ratioMin")] public double RatioMin { get; set; } = 1;
    [JsonPropertyName("ratioMax")] public double RatioMax { get; set; } = 1;
    [JsonPropertyName("hasRatio")] public bool HasRatio { get; set; }
    [JsonPropertyName("flash")] public bool Flash { get; set; }
    [JsonPropertyName("hwLevel")] public int HwLevel { get; set; }
    [JsonPropertyName("physical")] public string[] Physical { get; set; } = Array.Empty<string>();

    public string Nombre =>
        Facing switch { "BACK" => "Trasera", "FRONT" => "Frontal", "EXTERNAL" => "Externa", _ => "Camara" };

    public bool EsOptica => Physical.Length > 0;

    public IEnumerable<(int W, int H)> Resoluciones()
    {
        for (int i = 0; i + 1 < Sizes.Length; i += 2)
            yield return (Sizes[i], Sizes[i + 1]);
    }
}

public sealed class EstadoTelefono
{
    public string? Serie { get; set; }
    public string Modelo { get; set; } = "";
    public string VersionAndroid { get; set; } = "";
    public int SdkInt { get; set; }
    public string ComoObtuvoContext { get; set; } = "";
    public List<CamaraInfo> Camaras { get; set; } = new();
    public string CamaraActual { get; set; } = "";
    public bool FlashDisponible { get; set; }
    public bool Torch { get; set; }
    public string TipoZoom { get; set; } = "digital";     // "sensor" (puede ser optico) | "digital"
    public int Ancho { get; set; }
    public int Alto { get; set; }
    public int Fps { get; set; }
    public double FpsMedidos { get; set; }
    public bool Pausado { get; set; }
    public bool Activa { get; set; }
}

public sealed record WifiDispositivoEncontrado(
    string Nombre,
    string Ip,
    string? PuertoConexion,
    string? PuertoEmparejamiento)
{
    public bool TieneConexion => !string.IsNullOrWhiteSpace(PuertoConexion);
    public bool TieneEmparejamiento => !string.IsNullOrWhiteSpace(PuertoEmparejamiento);
    public string Resumen => TieneConexion
        ? $"{Ip}:{PuertoConexion} · listo para conectar"
        : $"{Ip}:{PuertoEmparejamiento} · requiere emparejamiento";
}

public sealed record PresetsVideo(int Ancho, int Alto, int Fps);

/// <summary>Opcion de resolucion tal cual la reporta el telefono (mas cadencias).</summary>
public sealed class ResolucionInfo
{
    public int W { get; init; }
    public int H { get; init; }
    public int Fps { get; init; }
    public string Texto => $"{W}x{H}  ·  {Fps} fps";
    public override string ToString() => Texto;
}
