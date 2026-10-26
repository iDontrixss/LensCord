using Avalonia;
using Avalonia.Controls;
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

            bool forzarIntro = desktop.Args?.Contains("--onboarding") == true;
            if (vm.Config.OnboardingCompletado && !forzarIntro)
            {
                desktop.MainWindow = win;
            }
            else
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var intro = new Views.OnboardingWindow(new ViewModels.OnboardingViewModel(vm));
                desktop.MainWindow = intro;
                intro.Closed += (_, _) =>
                {
                    desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                    desktop.MainWindow = win;
                    win.Show();
                };
            }

            vm.SolicitarOnboarding += () =>
            {
                if (!win.IsVisible) return;
                var intro = new Views.OnboardingWindow(new ViewModels.OnboardingViewModel(vm));
                _ = intro.ShowDialog(win);
            };

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
            Console.Error.WriteLine(Core.Loc.T("log.error_cerrar_app", e));
        }
        finally
        {
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        }
    }
}