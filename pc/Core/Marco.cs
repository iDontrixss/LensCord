using System.Runtime.InteropServices;

namespace Lenscord.Core;

public enum Rotacion { Ninguna, Derecha90, Izquierda90 }
public enum Recorte { Original, DieciseisNueve, UnoAUno, NueveADieciseis }

public static class Marco
{
    public static Frame Aplicar(Frame f, bool espejo, Rotacion rot, Recorte recorte)
    {
        if (!espejo && rot == Rotacion.Ninguna && recorte == Recorte.Original) return f;

        int w = f.Width, h = f.Height;
        var src = f.Bgra;

        double aspecto = recorte switch
        {
            Recorte.DieciseisNueve => 16.0 / 9.0,
            Recorte.UnoAUno => 1.0,
            Recorte.NueveADieciseis => 9.0 / 16.0,
            _ => 0.0,
        };
        if (aspecto > 0)
        {
            int nw = w, nh = h;
            if ((double)w / h > aspecto) nw = (int)Math.Round(h * aspecto);
            else nh = (int)Math.Round(w / aspecto);
            int ox = (w - nw) / 2, oy = (h - nh) / 2;
            var dst = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                Buffer.BlockCopy(src, ((y + oy) * w + ox) * 4, dst, y * nw * 4, nw * 4);
            src = dst; w = nw; h = nh;
        }

        if (rot != Rotacion.Ninguna)
        {
            bool derecha = rot == Rotacion.Derecha90;
            int nw = h, nh = w;
            var dst = new byte[nw * nh * 4];
            var origen = MemoryMarshal.Cast<byte, uint>(src);
            var destino = MemoryMarshal.Cast<byte, uint>(dst);
            for (int y = 0; y < h; y++)
            {
                int fila = y * w;
                for (int x = 0; x < w; x++)
                {
                    int nx, ny;
                    if (derecha) { nx = h - 1 - y; ny = x; }
                    else { nx = y; ny = w - 1 - x; }
                    destino[ny * nw + nx] = origen[fila + x];
                }
            }
            src = dst; w = nw; h = nh;
        }

        if (espejo)
        {
            var dst = new byte[src.Length];
            var origen = MemoryMarshal.Cast<byte, uint>(src);
            var destino = MemoryMarshal.Cast<byte, uint>(dst);
            for (int y = 0; y < h; y++)
            {
                var fila = destino.Slice(y * w, w);
                origen.Slice(y * w, w).CopyTo(fila);
                fila.Reverse();
            }
            src = dst;
        }

        return new Frame { Bgra = src, Width = w, Height = h, RecibidoMs = f.RecibidoMs };
    }
}
