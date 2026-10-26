using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lenscord.Controls;
using Lenscord.Core;
using Lenscord.ViewModels;

namespace Lenscord.Views;

public partial class OnboardingWindow : Window
{
    private readonly OnboardingViewModel? _vm;
    private bool _terminado;
    private ParticleField? _particulas;

    public OnboardingWindow()
    {
        InitializeComponent();
    }

    public OnboardingWindow(OnboardingViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        vm.Terminado += () =>
        {
            _terminado = true;
            Close();
        };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _particulas = this.FindControl<ParticleField>("Particulas");
        AddHandler(InputElement.PointerMovedEvent, OnPointerMoved,
                   RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerExitedEvent, OnPointerExited,
                   RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => _particulas?.OcultarPuntero();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _particulas?.MoverPuntero(e.GetPosition(_particulas));
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_particulas is null) return;
        var pos = e.GetPosition(_particulas);
        if (pos.X < 0 || pos.Y < 0 ||
            pos.X > _particulas.Bounds.Width || pos.Y > _particulas.Bounds.Height)
            _particulas.OcultarPuntero();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!_terminado)
        {
            _terminado = true;
            _vm?.Saltar();
        }
    }

    private void OnIdiomaEs(object? s, RoutedEventArgs e) => _vm?.Principal.CambiarIdioma("es");
    private void OnIdiomaEn(object? s, RoutedEventArgs e) => _vm?.Principal.CambiarIdioma("en");

    private void OnSeleccionarTema(object? s, RoutedEventArgs e)
    {
        if (s is Control { DataContext: TemaInfo tema }) _vm?.Principal.SeleccionarTema(tema);
    }

    private async void OnEmparejarDispositivoWifi(object? s, RoutedEventArgs e)
    {
        if (_vm is null || s is not Control { DataContext: WifiDispositivoEncontrado dispositivo }) return;
        try { await _vm.Principal.EmparejarWifiAsync(dispositivo); }
        catch (Exception ex) { _vm.Principal.Escribir(Loc.T("log.error_ui", ex)); }
    }

    private async void OnConectarDispositivoWifi(object? s, RoutedEventArgs e)
    {
        if (_vm is null || s is not Control { DataContext: WifiDispositivoEncontrado dispositivo }) return;
        try { await _vm.Principal.ConectarWifiAsync(dispositivo); }
        catch (Exception ex) { _vm.Principal.Escribir(Loc.T("log.error_ui", ex)); }
    }

    private void OnUsb(object? s, RoutedEventArgs e) => _vm?.ElegirUsb();
    private void OnWifi(object? s, RoutedEventArgs e) => _vm?.ElegirWifi();
    private void OnSiguiente(object? s, RoutedEventArgs e) => _vm?.Siguiente();
    private void OnAnterior(object? s, RoutedEventArgs e) => _vm?.Anterior();
    private void OnSaltar(object? s, RoutedEventArgs e) => _vm?.Saltar();
}
