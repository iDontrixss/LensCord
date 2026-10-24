using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Lenscord;

public class App : Application
{
    private bool _cerrando;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new ViewModels.MainViewModel();
            var win = new Views.MainWindow { DataContext = vm };
            desktop.MainWindow = win;

            desktop.ShutdownRequested += (_, e) =>
            {
                if (_cerrando) return;
                _cerrando = true;
                e.Cancel = true;
                if (desktop.MainWindow is { } w) w.Hide();
                _ = CerrarAsync(desktop, vm);
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task CerrarAsync(IClassicDesktopStyleApplicationLifetime desktop,
                                           ViewModels.MainViewModel vm)
    {
        try
        {
            await vm.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Error al cerrar Lenscord: " + e);
        }
        finally
        {
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        }
    }
}