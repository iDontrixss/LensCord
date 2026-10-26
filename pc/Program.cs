using Avalonia;
using Lenscord.Core;
using System;

namespace Lenscord;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Registrar(Loc.T("core.programa.fatal_no_controlado", e.ExceptionObject));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Registrar(Loc.T("core.programa.tarea_no_observada", e.Exception));
            e.SetObserved();
        };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Registrar(Loc.T("core.programa.no_pudo_arrancar", ex));
            Console.Error.WriteLine(Loc.T("core.programa.no_pudo_arrancar", ex));
            return 1;
        }
    }

    private static void Registrar(string texto)
    {
        try
        {
            string dir = Rutas.Logs;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app.log"),
                $"{DateTime.Now:O}  {texto}{Environment.NewLine}");
        }
        catch { }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Software },
                CompositionMode = new[] { Win32CompositionMode.RedirectionSurface }
            });
        }

        return builder;
    }
}