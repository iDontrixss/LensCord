using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Lenscord.Core;
using Lenscord.Controls;
using Lenscord.ViewModels;

namespace Lenscord.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private ParticleField? _particulas;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        var pantalla = Screens.ScreenFromWindow(this);
        if (pantalla is { Scaling: > 0 })
        {
            double anchoMax = pantalla.WorkingArea.Width / pantalla.Scaling - 24;
            double altoMax = pantalla.WorkingArea.Height / pantalla.Scaling - 24;
            Width = Math.Min(Width, Math.Max(MinWidth, anchoMax));
            Height = Math.Min(Height, Math.Max(MinHeight, altoMax));
        }

        _vm = DataContext as MainViewModel;
        _particulas = this.FindControl<ParticleField>("Particulas");
        Dispatcher.UIThread.Post(
            () => this.FindControl<ScrollViewer>("LienzoScroll")?.ScrollToHome(),
            DispatcherPriority.Loaded);

        if (_vm is not null)
        {
            _vm.LienzoCambiado += () => Dispatcher.UIThread.Post(() =>
                this.FindControl<Image>("VistaPrevia")?.InvalidateVisual());
            _vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.MostrarPanelWifi)) MarcarPanelesAbiertos();
            };
        }

        AddHandler(InputElement.PointerMovedEvent, OnPointerMoved,
                   RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerExitedEvent, OnPointerExited,
                   RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += OnDeactivated;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_particulas is null) return;
        _particulas.MoverPuntero(e.GetPosition(_particulas));
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_particulas is null) return;
        var pos = e.GetPosition(_particulas);
        if (pos.X < 0 || pos.Y < 0 ||
            pos.X > _particulas.Bounds.Width || pos.Y > _particulas.Bounds.Height)
            _particulas.OcultarPuntero();
    }

    private void OnDeactivated(object? sender, EventArgs e) => _particulas?.OcultarPuntero();

    private async void OnIniciar(object? s, RoutedEventArgs e) => await EjecutarAsync(() => _vm?.IniciarAsync() ?? Task.CompletedTask);
    private async void OnDetener(object? s, RoutedEventArgs e) => await EjecutarAsync(() => _vm?.DetenerAsync() ?? Task.CompletedTask);
    private void OnZoomMas(object? s, RoutedEventArgs e) => _vm?.ZoomMas();
    private void OnZoomMenos(object? s, RoutedEventArgs e) => _vm?.ZoomMenos();
    private void OnFlash(object? s, RoutedEventArgs e) { if (_vm is not null) _vm.Torch = !_vm.Torch; }
    private void OnEspejo(object? s, RoutedEventArgs e) => _vm?.Voltear();
    private void OnRotar(object? s, RoutedEventArgs e) => _vm?.Rotar();
    private void OnRecorte(object? s, RoutedEventArgs e) => _vm?.CicloRecorte();
    private async void OnPausa(object? s, RoutedEventArgs e) => await EjecutarAsync(() => _vm?.AlternarPausaAsync() ?? Task.CompletedTask);
    private async void OnCamara(object? s, RoutedEventArgs e) => await EjecutarAsync(() => _vm?.CambiarCamaraAsync() ?? Task.CompletedTask);
    private async void OnResolucion(object? s, SelectionChangedEventArgs e) => await EjecutarAsync(() => _vm?.AplicarResolucionAsync() ?? Task.CompletedTask);
    private void OnConfiguracion(object? s, RoutedEventArgs e) => _vm?.AlternarConfiguracion();

    private void OnPanelFicha(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton ficha && ficha.IsChecked == true)
        {
            foreach (string nombre in new[] { "FichaCaptura", "FichaWifi", "FichaRegistro" })
            {
                var otra = this.FindControl<ToggleButton>(nombre);
                if (otra is not null && otra != ficha) otra.IsChecked = false;
            }
        }
        MarcarPanelesAbiertos();
    }

    private void MarcarPanelesAbiertos()
    {
        var captura = this.FindControl<ToggleButton>("FichaCaptura");
        var wifi = this.FindControl<ToggleButton>("FichaWifi");
        var registro = this.FindControl<ToggleButton>("FichaRegistro");
        var panelCaptura = this.FindControl<Border>("PanelCapturaContenido");
        var panelWifi = this.FindControl<Border>("PanelWifiContenido");
        var panelRegistro = this.FindControl<Border>("PanelRegistroContenido");
        if (captura is not null && panelCaptura is not null)
            panelCaptura.IsVisible = captura.IsChecked == true;
        if (registro is not null && panelRegistro is not null)
            panelRegistro.IsVisible = registro.IsChecked == true;
        if (wifi is not null && panelWifi is not null)
            panelWifi.IsVisible = wifi.IsChecked == true && (_vm?.MostrarPanelWifi ?? false);
    }

    private void OnSeleccionarTema(object? s, RoutedEventArgs e)
    {
        if (s is Control { DataContext: TemaInfo tema }) _vm?.SeleccionarTema(tema);
    }    private async void OnCambiarModo(object? s, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.ModoWifi)
        {
            // A USB: regreso real (adb usb + disconnect), no solo el flag.
            await EjecutarAsync(() => _vm.VolverUsbAsync());
        }
        else
        {
            _vm.ModoWifi = true;
            await EjecutarAsync(() => _vm.DetectarWifiAsync());
        }
    }
    private async void OnEmparejar(object? s, RoutedEventArgs e) => await EjecutarAsync(() => _vm?.EmparejarWifiAsync() ?? Task.CompletedTask);
    private async void OnConectarWifi(object? s, RoutedEventArgs e) => await EjecutarAsync(() => _vm?.ConectarWifiAsync() ?? Task.CompletedTask);
    private async void OnDetectarWifi(object? s, RoutedEventArgs e) => await EjecutarAsync(() => _vm?.DetectarWifiAsync() ?? Task.CompletedTask);
    private async void OnEmparejarDispositivoWifi(object? s, RoutedEventArgs e)
    {
        if (s is Control { DataContext: WifiDispositivoEncontrado dispositivo })
            await EjecutarAsync(() => _vm?.EmparejarWifiAsync(dispositivo) ?? Task.CompletedTask);
    }
    private async void OnConectarDispositivoWifi(object? s, RoutedEventArgs e)
    {
        if (s is Control { DataContext: WifiDispositivoEncontrado dispositivo })
            await EjecutarAsync(() => _vm?.ConectarWifiAsync(dispositivo) ?? Task.CompletedTask);
    }
    private async void OnActivarWifi(object? s, RoutedEventArgs e) => await EjecutarAsync(() => _vm?.ActivarWifiAsync() ?? Task.CompletedTask);
    private async void OnVolverUsb(object? s, RoutedEventArgs e) => await EjecutarAsync(() => _vm?.VolverUsbAsync() ?? Task.CompletedTask);

    private async Task EjecutarAsync(Func<Task> accion)
    {
        try { await accion(); }
        catch (Exception ex) { _vm?.Escribir("ERROR no controlado en la interfaz: " + ex); }
    }
}
