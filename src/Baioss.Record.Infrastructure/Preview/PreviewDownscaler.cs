namespace Baioss.Record.Infrastructure.Preview;

/// <summary>
/// Reduce un frame BGRA de preview por PROMEDIO DE ÁREA (cada píxel destino es la media del bloque de origen que cubre):
/// barato (una pasada sobre el origen, sin dependencias) y sin el aliasing del vecino más cercano en textos y rótulos.
/// Puro y testeable; lo usa el servicio de instantáneas del panel web.
/// </summary>
public static class PreviewDownscaler
{
    /// <summary>
    /// Tamaño destino para un ancho pedido: nunca mayor que el origen (no se amplía), alto proporcional, y ambos PARES
    /// y ≥ 2 (los codificadores de imagen trabajan mejor con dimensiones pares).
    /// </summary>
    public static (int Width, int Height) TargetSize(int srcWidth, int srcHeight, int requestedWidth)
    {
        if (srcWidth <= 0 || srcHeight <= 0) throw new ArgumentOutOfRangeException(nameof(srcWidth), "El frame de origen no tiene tamaño.");
        int w = Math.Clamp(requestedWidth, 2, Math.Max(2, srcWidth)) & ~1;
        w = Math.Max(2, w);
        int h = (int)Math.Round((double)srcHeight * w / srcWidth) & ~1;
        return (w, Math.Max(2, h));
    }

    /// <summary>Reduce <paramref name="src"/> (BGRA, <paramref name="srcStride"/> bytes por fila) a <paramref name="dst"/>
    /// (BGRA compacto, <c>dstWidth × 4</c> bytes por fila). El alfa de salida es opaco.</summary>
    public static void Downscale(ReadOnlySpan<byte> src, int srcWidth, int srcHeight, int srcStride,
        Span<byte> dst, int dstWidth, int dstHeight)
    {
        if (dstWidth <= 0 || dstHeight <= 0) throw new ArgumentOutOfRangeException(nameof(dstWidth));
        if (src.Length < srcStride * (srcHeight - 1) + srcWidth * 4) throw new ArgumentException("El buffer de origen es más corto que el frame.", nameof(src));
        if (dst.Length < dstWidth * dstHeight * 4) throw new ArgumentException("El buffer de destino es más corto que el frame.", nameof(dst));

        // Límites de columna precalculados: [x0, x1) del origen para cada columna destino (al menos 1 píxel).
        Span<int> x0 = dstWidth <= 1024 ? stackalloc int[dstWidth] : new int[dstWidth];
        Span<int> x1 = dstWidth <= 1024 ? stackalloc int[dstWidth] : new int[dstWidth];
        for (int dx = 0; dx < dstWidth; dx++)
        {
            x0[dx] = (int)((long)dx * srcWidth / dstWidth);
            x1[dx] = Math.Min(srcWidth, Math.Max(x0[dx] + 1, (int)((long)(dx + 1) * srcWidth / dstWidth)));
        }

        for (int dy = 0; dy < dstHeight; dy++)
        {
            int y0 = (int)((long)dy * srcHeight / dstHeight);
            int y1 = Math.Min(srcHeight, Math.Max(y0 + 1, (int)((long)(dy + 1) * srcHeight / dstHeight)));
            int o = dy * dstWidth * 4;
            for (int dx = 0; dx < dstWidth; dx++, o += 4)
            {
                int b = 0, g = 0, r = 0, n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = y * srcStride + x0[dx] * 4;
                    for (int x = x0[dx]; x < x1[dx]; x++, i += 4)
                    {
                        b += src[i]; g += src[i + 1]; r += src[i + 2];
                        n++;
                    }
                }
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = 255;
            }
        }
    }
}
