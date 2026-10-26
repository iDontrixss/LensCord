using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Lenscord.Core;

public sealed class Loc : INotifyPropertyChanged
{
    public static readonly string[] Idiomas = { "es", "en" };

    private static readonly Dictionary<string, Dictionary<string, string>> Tablas = new();
    private static string _idioma = "es";

    public static Loc Instancia { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public static event Action? CambioIdioma;

    public static string Idioma => _idioma;

    public static bool EsIdioma(string? id) => id is not null && Idiomas.Contains(id);

    public static void Cambiar(string id)
    {
        if (!EsIdioma(id) || id == _idioma) return;
        _idioma = id;
        foreach (var nombre in new[] { "Item[]", "Item", string.Empty })
            Instancia.PropertyChanged?.Invoke(Instancia, new PropertyChangedEventArgs(nombre));
        CambioIdioma?.Invoke();
    }

    public static void Iniciar(string id)
    {
        if (EsIdioma(id)) _idioma = id;
    }

    public string this[string clave] => T(clave);

    public static string T(string clave)
    {
        if (Tabla(_idioma).TryGetValue(clave, out var texto)) return texto;
        if (_idioma != "es" && Tabla("es").TryGetValue(clave, out texto)) return texto;
        return clave;
    }

    public static string T(string clave, params object?[] args)
    {
        try { return string.Format(T(clave), args); }
        catch (FormatException) { return T(clave); }
    }

    public static IReadOnlyDictionary<string, string> Claves(string idioma) => Tabla(idioma);

    private static Dictionary<string, string> Tabla(string idioma)
    {
        lock (Tablas)
        {
            if (Tablas.TryGetValue(idioma, out var tabla)) return tabla;
            tabla = new Dictionary<string, string>();
            try
            {
                using var flujo = typeof(Loc).Assembly.GetManifestResourceStream($"Lenscord.Idiomas.{idioma}.json");
                if (flujo is not null)
                {
                    var datos = JsonSerializer.Deserialize<Dictionary<string, string>>(flujo);
                    if (datos is not null) tabla = datos;
                }
            }
            catch { }
            Tablas[idioma] = tabla;
            return tabla;
        }
    }
}

public sealed class TExtension : MarkupExtension
{
    public string Clave { get; set; }

    public TExtension(string clave) => Clave = clave;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Clave}]") { Source = Loc.Instancia, Mode = BindingMode.OneWay };
}
