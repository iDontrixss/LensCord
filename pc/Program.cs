using Avalonia;
using System;

namespace Lenscord;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Registrar("FATAL no controlado: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Registrar("Tarea no observada: " + e.Exception);
            e.SetObserved();
        };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Registrar("Lenscord no pudo arrancar: " + ex);
            Console.Error.WriteLine("Lenscord no pudo arrancar: " + ex);
            return 1;
        }
    }

    private static void Registrar(string texto)
    {
        try
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "PROGRESO.md"))) d = d.Parent;
            string dir = Path.Combine(d?.FullName ?? AppContext.BaseDirectory, "evidencia");
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