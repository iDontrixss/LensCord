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
        public Point Hogar;
        public Point Posicion;
        public Vector Velocidad;
        public double Radio;
        public IBrush Pincel = Brushes.Transparent;
    }

    private readonly List<Punto> _puntos = new();
    private readonly DispatcherTimer _reloj;
    private readonly IBrush[] _pinceles =
    {
        new SolidColorBrush(Color.FromArgb(118, 26, 26, 31)),
        new SolidColorBrush(Color.FromArgb(110, 106, 90, 224)),
        new SolidColorBrush(Color.FromArgb(100, 240, 72, 155)),
    };

    private Point? _puntero;
    private Size _tamano;
    private bool _activo;

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
        _activo = true;
        if (!_reloj.IsEnabled) _reloj.Start();
    }

    public void OcultarPuntero()
    {
        _puntero = null;
        _activo = false;
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
        return base.ArrangeOverride(finalSize);
    }

    public override void Render(DrawingContext context)
    {
        foreach (var p in _puntos)
            context.DrawEllipse(p.Pincel, null, p.Posicion, p.Radio, p.Radio);
    }

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
        double cx = tamano.Width * 0.5;
        double cy = tamano.Height * 0.48;
        double rx = Math.Max(80, tamano.Width * 0.47);
        double ry = Math.Max(60, tamano.Height * 0.43);
        int cantidad = Math.Clamp((int)(tamano.Width * tamano.Height / 2800), 150, 280);

        for (int i = 0; i < cantidad; i++)
        {
            double angulo = rng.NextDouble() * Math.PI * 2;
            double radio = 0.56 + 0.44 * Math.Sqrt(rng.NextDouble());
            double x = cx + Math.Cos(angulo) * rx * radio + (rng.NextDouble() - 0.5) * 24;
            double y = cy + Math.Sin(angulo) * ry * radio + (rng.NextDouble() - 0.5) * 24;
            x = Math.Clamp(x, 8, tamano.Width - 8);
            y = Math.Clamp(y, 8, tamano.Height - 8);

            _puntos.Add(new Punto
            {
                Hogar = new Point(x, y),
                Posicion = new Point(x, y),
                Velocidad = default,
                Radio = rng.NextDouble() < 0.82 ? 1.35 : 2.15,
                Pincel = _pinceles[rng.Next(_pinceles.Length)],
            });
        }
    }

    private void Avanzar(double dt)
    {
        bool movimiento = false;
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
                    movimiento = true;
                }
            }

            Vector haciaHogar = p.Hogar - p.Posicion;
            p.Velocidad += haciaHogar * (26 * dt);
            p.Velocidad *= Math.Pow(0.84, dt * 60);
            p.Posicion += p.Velocidad * dt;

            Vector desvio = p.Posicion - p.Hogar;
            if (desvio.Length > DesplazamientoMaximo)
                p.Posicion = p.Hogar + desvio.Normalize() * DesplazamientoMaximo;

            if (p.Velocidad.Length > 0.08 || desvio.Length > 0.15)
                movimiento = true;
        }

        InvalidateVisual();
        if (!movimiento && !_activo) _reloj.Stop();
    }
}
