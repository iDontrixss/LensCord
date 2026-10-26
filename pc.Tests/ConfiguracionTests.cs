using System.Text.Json.Nodes;
using Lenscord.Core;
using Xunit;

namespace Lenscord.Tests;

public class ConfiguracionTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "lenscord-config-" + Guid.NewGuid().ToString("N"));

    private string Ruta => Path.Combine(_carpeta, "config.json");

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, true); } catch { }
    }

    [Fact]
    public void Sin_archivo_es_primera_ejecucion()
    {
        var c = Configuracion.Cargar(Ruta);
        Assert.False(c.OnboardingCompletado);
        Assert.Equal("claro", c.Tema);
    }

    [Fact]
    public void Guardar_y_cargar_conserva_los_valores()
    {
        new Configuracion { Tema = "black", Idioma = "en", OnboardingCompletado = true }.Guardar(Ruta);
        var c = Configuracion.Cargar(Ruta);
        Assert.Equal("black", c.Tema);
        Assert.Equal("en", c.Idioma);
        Assert.True(c.OnboardingCompletado);
    }

    [Fact]
    public void Config_antigua_solo_con_tema_cuenta_como_primera_vez()
    {
        Directory.CreateDirectory(_carpeta);
        File.WriteAllText(Ruta, "{\"tema\":\"beige\"}");
        var c = Configuracion.Cargar(Ruta);
        Assert.Equal("beige", c.Tema);
        Assert.False(c.OnboardingCompletado);
    }

    [Fact]
    public void Archivo_corrupto_devuelve_valores_por_defecto()
    {
        Directory.CreateDirectory(_carpeta);
        File.WriteAllText(Ruta, "{ esto no es json");
        var c = Configuracion.Cargar(Ruta);
        Assert.False(c.OnboardingCompletado);
        Assert.Equal("claro", c.Tema);
    }

    [Fact]
    public void Idioma_no_valido_se_ignora()
    {
        Directory.CreateDirectory(_carpeta);
        File.WriteAllText(Ruta, "{\"idioma\":\"fr\"}");
        Assert.True(Loc.EsIdioma(Configuracion.Cargar(Ruta).Idioma));
    }

    [Fact]
    public void Guardar_conserva_campos_desconocidos()
    {
        Directory.CreateDirectory(_carpeta);
        File.WriteAllText(Ruta, "{\"otro\":42}");
        new Configuracion().Guardar(Ruta);
        var raiz = JsonNode.Parse(File.ReadAllText(Ruta))!.AsObject();
        Assert.Equal(42, (int)raiz["otro"]!);
    }
}
