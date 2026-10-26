using Lenscord.Core;
using Xunit;

namespace Lenscord.Tests;

public class MarcoTests
{
    private static Frame Crear(int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) bgra[i * 4] = (byte)i;
        return new Frame { Bgra = bgra, Width = w, Height = h };
    }

    private static byte Pixel(Frame f, int x, int y) => f.Bgra[(y * f.Width + x) * 4];

    [Fact]
    public void SinTransformaciones_DevuelveElMismoFrame()
    {
        var f = Crear(4, 2);

        Assert.Same(f, Marco.Aplicar(f, false, Rotacion.Ninguna, Recorte.Original));
    }

    [Fact]
    public void Espejo_InvierteCadaFila()
    {
        var f = Crear(4, 2);

        var r = Marco.Aplicar(f, true, Rotacion.Ninguna, Recorte.Original);

        Assert.Equal(Pixel(f, 3, 0), Pixel(r, 0, 0));
        Assert.Equal(Pixel(f, 0, 1), Pixel(r, 3, 1));
    }

    [Fact]
    public void RotarDerecha_IntercambiaDimensionesYPosiciones()
    {
        var f = Crear(4, 2);

        var r = Marco.Aplicar(f, false, Rotacion.Derecha90, Recorte.Original);

        Assert.Equal(2, r.Width);
        Assert.Equal(4, r.Height);
        Assert.Equal(Pixel(f, 0, 0), Pixel(r, 1, 0));
        Assert.Equal(Pixel(f, 0, 1), Pixel(r, 0, 0));
    }

    [Fact]
    public void RotarIzquierda_IntercambiaDimensionesYPosiciones()
    {
        var f = Crear(4, 2);

        var r = Marco.Aplicar(f, false, Rotacion.Izquierda90, Recorte.Original);

        Assert.Equal(2, r.Width);
        Assert.Equal(4, r.Height);
        Assert.Equal(Pixel(f, 0, 0), Pixel(r, 0, 3));
        Assert.Equal(Pixel(f, 3, 0), Pixel(r, 0, 0));
    }

    [Fact]
    public void RecorteUnoAUno_CentraElCuadrado()
    {
        var f = Crear(4, 2);

        var r = Marco.Aplicar(f, false, Rotacion.Ninguna, Recorte.UnoAUno);

        Assert.Equal(2, r.Width);
        Assert.Equal(2, r.Height);
        Assert.Equal(Pixel(f, 1, 0), Pixel(r, 0, 0));
    }
}
