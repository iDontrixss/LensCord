using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Lenscord.Core;

namespace Lenscord.ViewModels;

public sealed class OnboardingViewModel : INotifyPropertyChanged
{
    public const int TotalPasos = 5;

    private int _paso;
    private DispatcherTimer? _busqueda;

    public OnboardingViewModel(MainViewModel principal)
    {
        Principal = principal;
        Loc.CambioIdioma += AlCambiarIdioma;
    }

    public MainViewModel Principal { get; }
    public ObservableCollection<Chequeo> Chequeos { get; } = new();

    public int Paso
    {
        get => _paso;
        private set
        {
            _paso = value;
            OnChanged(nameof(Paso), nameof(EsPasoIdioma), nameof(EsPasoTema), nameof(EsPasoBienvenida),
                      nameof(EsPasoConexion), nameof(EsPasoChequeo), nameof(PuedeRetroceder), nameof(EsUltimo),
                      nameof(TextoContinuar), nameof(PasoTexto));
            ActualizarBusqueda();
        }
    }

    public bool EsPasoIdioma => Paso == 0;
    public bool EsPasoTema => Paso == 1;
    public bool EsPasoBienvenida => Paso == 2;
    public bool EsPasoConexion => Paso == 3;
    public bool EsPasoChequeo => Paso == 4;
    public bool PuedeRetroceder => Paso > 0;
    public bool EsUltimo => Paso == TotalPasos - 1;
    public string TextoContinuar => Loc.T(EsUltimo ? "onb.empezar" : "onb.continuar");
    public string PasoTexto => Loc.T("onb.paso", Paso + 1, TotalPasos);

    public bool ModoUsb => !Principal.ModoWifi;
    public bool ModoWifi => Principal.ModoWifi;

    public event Action? Terminado;

    public void Siguiente()
    {
        if (EsUltimo)
        {
            Finalizar();
            return;
        }
        Paso++;
        if (EsPasoChequeo) EjecutarChequeo();
    }

    public void Anterior()
    {
        if (Paso > 0) Paso--;
    }

    public void Saltar() => Finalizar();

    public void ElegirUsb()
    {
        Principal.ModoWifi = false;
        OnChanged(nameof(ModoUsb), nameof(ModoWifi));
        ActualizarBusqueda();
    }

    public void ElegirWifi()
    {
        Principal.ModoWifi = true;
        OnChanged(nameof(ModoUsb), nameof(ModoWifi));
        ActualizarBusqueda();
    }

    private async Task BuscarAsync()
    {
        if (Principal.HayCelular) return;
        try { await Principal.BuscarWifiSilenciosoAsync(); }
        catch { }
    }

    private void ActualizarBusqueda()
    {
        bool activa = EsPasoConexion && Principal.ModoWifi;
        if (activa && _busqueda is null)
        {
            _busqueda = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _busqueda.Tick += async (_, _) => await BuscarAsync();
            _busqueda.Start();
            _ = BuscarAsync();
        }
        else if (!activa && _busqueda is not null)
        {
            _busqueda.Stop();
            _busqueda = null;
        }
    }

    public void EjecutarChequeo()
    {
        var resultado = Entorno.Comprobar(Principal.ClienteAdb);
        Chequeos.Clear();
        foreach (var chequeo in resultado) Chequeos.Add(chequeo);
    }

    private void Finalizar()
    {
        Principal.Config.OnboardingCompletado = true;
        Principal.Config.Guardar();
        Loc.CambioIdioma -= AlCambiarIdioma;
        _busqueda?.Stop();
        _busqueda = null;
        Terminado?.Invoke();
    }

    private void AlCambiarIdioma()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OnChanged(nameof(TextoContinuar), nameof(PasoTexto));
            if (EsPasoChequeo) EjecutarChequeo();
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(params string[] props)
    {
        foreach (var p in props) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
