using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lenscord.Core;

public sealed class Configuracion
{
    public string Tema { get; set; } = "claro";
    public string Idioma { get; set; } = IdiomaDelSistema();
    public bool OnboardingCompletado { get; set; }

    public static string Ruta => Path.Combine(Rutas.Datos, "config.json");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetUserDefaultLocaleName(StringBuilder nombre, int largo);

    public static string IdiomaDelSistema() =>
        (NombreLocalDelSistema() ?? "").StartsWith("es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";

    private static string? NombreLocalDelSistema()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var nombre = new StringBuilder(85);
                if (GetUserDefaultLocaleName(nombre, nombre.Capacity) > 0) return nombre.ToString();
            }
            foreach (var variable in new[] { "LC_ALL", "LC_MESSAGES", "LANG", "LANGUAGE" })
            {
                var valor = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrWhiteSpace(valor)) return valor;
            }
        }
        catch { }
        return CultureInfo.CurrentUICulture.Name;
    }

    public static Configuracion Cargar(string? ruta = null)
    {
        var config = new Configuracion();
        try
        {
            ruta ??= Ruta;
            if (!File.Exists(ruta)) return config;
            if (JsonNode.Parse(File.ReadAllText(ruta)) is not JsonObject raiz) return config;
            if (raiz["tema"] is JsonValue t && t.TryGetValue(out string? tema) && !string.IsNullOrWhiteSpace(tema))
                config.Tema = tema;
            if (raiz["idioma"] is JsonValue i && i.TryGetValue(out string? idioma) && Loc.EsIdioma(idioma))
                config.Idioma = idioma!;
            if (raiz["onboardingCompletado"] is JsonValue o && o.TryGetValue(out bool hecho))
                config.OnboardingCompletado = hecho;
        }
        catch { }
        return config;
    }

    public void Guardar(string? ruta = null)
    {
        try
        {
            ruta ??= Ruta;
            Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
            JsonObject raiz = new();
            try
            {
                if (File.Exists(ruta) && JsonNode.Parse(File.ReadAllText(ruta)) is JsonObject previo) raiz = previo;
            }
            catch { }
            raiz["tema"] = Tema;
            raiz["idioma"] = Idioma;
            raiz["onboardingCompletado"] = OnboardingCompletado;
            File.WriteAllText(ruta, raiz.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
