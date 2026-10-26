using System.Runtime.InteropServices;

namespace Lenscord.Core;

public static class Rutas
{
    private static readonly Lazy<string?> _raizDesarrollo = new(BuscarRaizDesarrollo);

    public static string? RaizDesarrollo => _raizDesarrollo.Value;

    public static string Raiz => RaizDesarrollo ?? AppContext.BaseDirectory;

    public static string Datos { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lenscord");

    public static string Logs => RaizDesarrollo is not null
        ? Path.Combine(RaizDesarrollo, "evidencia")
        : Path.Combine(Datos, "logs");

    public static string NombreExe(string nombre) => OperatingSystem.IsWindows() ? nombre + ".exe" : nombre;

    public static string Rid
    {
        get
        {
            var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            if (OperatingSystem.IsWindows()) return arm ? "win-arm64" : "win-x64";
            if (OperatingSystem.IsMacOS()) return arm ? "osx-arm64" : "osx-x64";
            return arm ? "linux-arm64" : "linux-x64";
        }
    }

    public static string? AsegurarCarpeta(string ruta)
    {
        try { Directory.CreateDirectory(ruta); return ruta; }
        catch { return null; }
    }

    public static string? BuscarDex()
    {
        var candidatos = new List<string>();
        if (RaizDesarrollo is not null)
            candidatos.Add(Path.Combine(RaizDesarrollo, "dispositivo", "out", "lenscord.dex"));
        candidatos.Add(Path.Combine(AppContext.BaseDirectory, "lenscord.dex"));
        candidatos.Add(Path.Combine(AppContext.BaseDirectory, "dispositivo", "out", "lenscord.dex"));
        candidatos.Add(Path.Combine(Datos, "lenscord.dex"));
        return candidatos.FirstOrDefault(File.Exists);
    }

    public static string ComandoBuildDex => OperatingSystem.IsWindows()
        ? Path.Combine("dispositivo", "build-dex.ps1")
        : "dispositivo/build-dex.sh";

    public static IEnumerable<string> DirectoriosDeBusqueda()
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(d)) yield return d.Trim();

        if (OperatingSystem.IsWindows()) yield break;

        foreach (var d in new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/snap/bin" })
            yield return d;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, "Library", "Android", "sdk", "platform-tools");
            yield return Path.Combine(home, "Android", "Sdk", "platform-tools");
        }
    }

    public static string? BuscarEnPath(string exe)
    {
        foreach (var dir in DirectoriosDeBusqueda())
        {
            try
            {
                var completo = Path.Combine(dir, exe);
                if (File.Exists(completo)) return completo;
            }
            catch { }
        }
        return null;
    }

    private static string? BuscarRaizDesarrollo()
    {
        try
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "PROGRESO.md"))) d = d.Parent;
            return d?.FullName;
        }
        catch { return null; }
    }
}
