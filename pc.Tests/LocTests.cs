using System.Text.RegularExpressions;
using Lenscord.Core;
using Xunit;

namespace Lenscord.Tests;

public class LocTests
{
    private static readonly Regex Marcador = new(@"\{(\d+)\}");

    [Fact]
    public void Ambos_idiomas_tienen_las_mismas_claves()
    {
        var es = Loc.Claves("es").Keys.ToHashSet();
        var en = Loc.Claves("en").Keys.ToHashSet();
        Assert.NotEmpty(es);
        Assert.Empty(es.Except(en));
        Assert.Empty(en.Except(es));
    }

    [Fact]
    public void Los_marcadores_coinciden_entre_idiomas()
    {
        var es = Loc.Claves("es");
        var en = Loc.Claves("en");
        foreach (var (clave, texto) in es)
        {
            var a = Marcador.Matches(texto).Select(m => m.Value).Distinct().OrderBy(x => x);
            var b = Marcador.Matches(en[clave]).Select(m => m.Value).Distinct().OrderBy(x => x);
            Assert.True(a.SequenceEqual(b), $"Marcadores distintos en {clave}");
        }
    }

    [Fact]
    public void Ningun_texto_esta_vacio()
    {
        foreach (var idioma in Loc.Idiomas)
            foreach (var (clave, texto) in Loc.Claves(idioma))
                Assert.False(string.IsNullOrWhiteSpace(texto), $"{idioma}:{clave} vacio");
    }

    [Fact]
    public void Todas_las_claves_usadas_en_el_codigo_existen()
    {
        string? pc = BuscarCarpetaPc();
        if (pc is null) return;
        var claves = Loc.Claves("es");
        var uso = new Regex("Loc\\.T\\(\"([a-z0-9_.]*[a-z0-9_])\"|\\{loc:T ([A-Za-z0-9_.]+)\\}");
        var faltan = new List<string>();
        foreach (var archivo in Directory.EnumerateFiles(pc, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".axaml"))
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            foreach (Match m in uso.Matches(File.ReadAllText(archivo)))
            {
                string clave = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!claves.ContainsKey(clave)) faltan.Add($"{Path.GetFileName(archivo)}: {clave}");
            }
        }
        Assert.Empty(faltan);
    }

    [Fact]
    public void T_formatea_argumentos_y_cae_en_la_clave()
    {
        Loc.Iniciar("es");
        Assert.Equal("Paso 2 de 5", Loc.T("onb.paso", 2, 5));
        Assert.Equal("clave.que.no.existe", Loc.T("clave.que.no.existe"));
        Loc.Cambiar("en");
        try { Assert.Equal("Step 2 of 5", Loc.T("onb.paso", 2, 5)); }
        finally { Loc.Cambiar("es"); }
    }

    private static string? BuscarCarpetaPc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidato = Path.Combine(dir.FullName, "pc");
            if (File.Exists(Path.Combine(candidato, "Lenscord.csproj"))) return candidato;
            dir = dir.Parent;
        }
        return null;
    }
}
