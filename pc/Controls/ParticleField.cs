using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Lenscord.Controls;

public sealed class ParticleField : Control
{
    private const double RadioInfluencia = 175;
    private const double DesplazamientoMaximo = 46;

    private sealed class Punto
    {
        public double Angulo;        // posicion angular actual en el cinturon
        public double RadioFactor;   // 0..1: distancia al centro dentro del ovalo
        public double GeometriaX;    // jitter fijo por particula (aspecto organico)
        public double GeometriaY;
        public double Omega;         // velocidad angular (rad/s); los internos giran mas rapido
        public Point Hogar;
        public Point Posicion;
        public Vector Velocidad;
        public double Radio;
        public int Tono; // indice dentro de la paleta del tema (0 tenue .. 2 fuerte)
    }

    private readonly List<Punto> _puntos = new();
    private Point _centro;
    private Vector _semieje;
    private IBrush[]? _pinceles;
    private Color _tintaTema;
    private readonly DispatcherTimer _reloj;

    private Point? _puntero;
    private Size _tamano;

    public ParticleField()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _reloj = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _reloj.Tick += (_, _) => Avanzar(1.0 / 60.0);
    }

    public void MoverPuntero(Point posicion)
    {
        _puntero = posicion;
        if (!_reloj.IsEnabled) _reloj.Start();
    }

    public void OcultarPuntero()
    {
        _puntero = null;
        if (!_reloj.IsEnabled) _reloj.Start();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Math.Abs(finalSize.Width - _tamano.Width) > 1 ||
            Math.Abs(finalSize.Height - _tamano.Height) > 1)
        {
            _tamano = finalSize;
            Generar(finalSize);
        }
        if (_puntos.Count > 0 && !_reloj.IsEnabled) _reloj.Start();
        return base.ArrangeOverride(finalSize);
    }

    public override void Render(DrawingContext context)
    {
        var pinceles = Pinceles();
        foreach (var p in _puntos)
            context.DrawEllipse(pinceles[p.Tono], null, p.Posicion, p.Radio, p.Radio);
    }

    // Paleta de puntos segun el tema: usa la tinta del tema (casi negra en los
    // claros -> puntitos negros que resaltan; casi blanca en los oscuros ->
    // puntitos blancos brillantes). Tres niveles de opacidad para dar profundidad.
    private IBrush[] Pinceles()
    {
        var tinta = TintaTema();
        if (_pinceles is null || _tintaTema != tinta)
        {
            _tintaTema = tinta;
            // tinta clara => tema oscuro (fondo negro): el punto blanco pide algo mas de alfa
            // para destacar igual que el punto negro sobre fondo claro.
            bool oscuro = Luminancia(tinta) > 0.5;
            byte a1 = oscuro ? (byte)150 : (byte)230;
            byte a2 = oscuro ? (byte)185 : (byte)245;
            byte a3 = oscuro ? (byte)220 : (byte)255;
            _pinceles = new IBrush[]
            {
                new SolidColorBrush(Color.FromArgb(a1, tinta.R, tinta.G, tinta.B)),
                new SolidColorBrush(Color.FromArgb(a2, tinta.R, tinta.G, tinta.B)),
                new SolidColorBrush(Color.FromArgb(a3, tinta.R, tinta.G, tinta.B)),
            };
        }
        return _pinceles;
    }

    private static Color TintaTema()
    {
        if (Application.Current is { } app &&
            app.TryGetResource("Tinta", app.RequestedThemeVariant, out object? valor) &&
            valor is ISolidColorBrush brocha)
            return brocha.Color;
        return Color.FromArgb(255, 26, 26, 31);
    }

    private static double Luminancia(Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _reloj.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Generar(Size tamano)
    {
        _puntos.Clear();
        if (tamano.Width < 2 || tamano.Height < 2) return;

        var rng = new Random(7);
        _centro = new Point(tamano.Width * 0.5, tamano.Height * 0.48);
        _semieje = new Vector(Math.Max(80, tamano.Width * 0.47), Math.Max(60, tamano.Height * 0.43));
        int cantidad = Math.Clamp((int)(tamano.Width * tamano.Height / 2800), 150, 280);

        for (int i = 0; i < cantidad; i++)
        {
            double angulo = rng.NextDouble() * Math.PI * 2;
            double radioFactor = 0.56 + 0.44 * Math.Sqrt(rng.NextDouble());
            // giro en el mismo sentido; los asteroides internos van mas rapido (Kepler suavizado)
            double omega = 0.028 * (0.78 / radioFactor) * (0.85 + 0.3 * rng.NextDouble());
            double geometriaX = (rng.NextDouble() - 0.5) * 24;
            double geometriaY = (rng.NextDouble() - 0.5) * 24;
            double x = Math.Clamp(_centro.X + Math.Cos(angulo) * _semieje.X * radioFactor + geometriaX, 8, tamano.Width - 8);
            double y = Math.Clamp(_centro.Y + Math.Sin(angulo) * _semieje.Y * radioFactor + geometriaY, 8, tamano.Height - 8);

            _puntos.Add(new Punto
            {
                Angulo = angulo,
                RadioFactor = radioFactor,
                GeometriaX = geometriaX,
                GeometriaY = geometriaY,
                Omega = omega,
                Hogar = new Point(x, y),
                Posicion = new Point(x, y),
                Velocidad = default,
                Radio = rng.NextDouble() < 0.82 ? 1.35 : 2.15,
                Tono = rng.NextDouble() < 0.5 ? 1 : (rng.NextDouble() < 0.5 ? 0 : 2),
            });
        }
    }

    private void Avanzar(double dt)
    {
        foreach (var p in _puntos)
        {
            if (_puntero is { } mouse)
            {
                Vector haciaMouse = mouse - p.Posicion;
                double distancia = haciaMouse.Length;
                if (distancia > 0.001 && distancia < RadioInfluencia)
                {
                    double fuerza = 1 - distancia / RadioInfluencia;
                    p.Velocidad += haciaMouse / distancia * (980 * fuerza * dt);
                }
            }

            // el cinturon gira despacio: el hogar de cada punto avanza en su orbita
            p.Angulo += p.Omega * dt;
            p.Hogar = new Point(
                _centro.X + Math.Cos(p.Angulo) * _semieje.X * p.RadioFactor + p.GeometriaX,
                _centro.Y + Math.Sin(p.Angulo) * _semieje.Y * p.RadioFactor + p.GeometriaY);

            Vector haciaHogar = p.Hogar - p.Posicion;
            p.Velocidad += haciaHogar * (26 * dt);
            p.Velocidad *= Math.Pow(0.84, dt * 60);
            p.Posicion += p.Velocidad * dt;

            Vector desvio = p.Posicion - p.Hogar;
            if (desvio.Length > DesplazamientoMaximo)
                p.Posicion = p.Hogar + desvio.Normalize() * DesplazamientoMaximo;
        }

        InvalidateVisual();
    }
}
