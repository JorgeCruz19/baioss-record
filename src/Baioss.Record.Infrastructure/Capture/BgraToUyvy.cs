namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// BGRA → UYVY (YUV 4:2:2 de 8 bits, rango limitado) para las fuentes NDI con alfa, que el SDK entrega en BGRA: así el proceso del
/// canal recibe lo mismo que de cualquier otra fuente NDI (uyvy422, la mitad de bytes) y no convierte él cada cuadro que se le sirve.
/// Con BGRA, FFmpeg pasaba a NV12 para el codificador y reducía el preview desde RGB en cada cuadro, repeticiones de la cadencia
/// declarada incluidas: con un grafismo 1080 que declara 60 y manda ~4, el proceso del canal (preview y grabación NVENC) gastaba
/// 3,9 núcleos frente a 1,9 con UYVY (2026-10-17). Aquí se convierte una vez por cuadro RECIBIDO (~6 ms un 1080).
/// <para>Matriz BT.709 (HD y más) o BT.601 (SD, menos de 720 líneas), como la que se supone a un vídeo sin etiquetar de ese tamaño;
/// el croma de cada par de píxeles es la media de los dos (lo que hace swscale por defecto). El alfa se descarta: ninguna salida lo
/// graba (el NV12 de antes tampoco lo llevaba) y NDI transmite 4:2:2, así que pasar a UYVY no quita resolución de color. Pura.</para>
/// </summary>
internal static class BgraToUyvy
{
    // Coeficientes ×65536 (R, G, B) para BGRA de rango completo → YUV de rango limitado (Y 16–235, U/V 16–240). Los de U y V suman
    // 0, para que un gris quede en 128 exacto.
    private static readonly int[] Bt709 = { 11966, 40254, 4064, -6596, -22188, 28784, 28784, -26145, -2639 };
    private static readonly int[] Bt601 = { 16829, 33039, 6416, -9714, -19070, 28784, 28784, -24103, -4681 };

    /// <summary>Por debajo de esta altura, SD: BT.601. Desde 720 líneas, BT.709.</summary>
    public const int HdHeight = 720;

    /// <summary>¿Se puede servir en UYVY? Hace falta un ancho par (un par de píxeles por cada U/V).</summary>
    public static bool Supports(int width) => width > 0 && width % 2 == 0;

    /// <summary>
    /// Convierte una línea de <paramref name="width"/> píxeles BGRA (4 bytes por píxel) en UYVY (2 bytes por píxel, U Y V Y por
    /// cada par). <paramref name="frameHeight"/> es la altura del cuadro entero, que decide la matriz. El ancho debe ser par.
    /// </summary>
    public static void ConvertLine(ReadOnlySpan<byte> bgra, Span<byte> uyvy, int width, int frameHeight)
    {
        if (!Supports(width)) throw new ArgumentOutOfRangeException(nameof(width), width, "UYVY necesita un ancho par.");
        if (bgra.Length < width * 4 || uyvy.Length < width * 2) throw new ArgumentException("Línea más corta que el ancho.");
        var k = frameHeight >= HdHeight ? Bt709 : Bt601;
        int yr = k[0], yg = k[1], yb = k[2], ur = k[3], ug = k[4], ub = k[5], vr = k[6], vg = k[7], vb = k[8];
        const int yOffset = (16 << 16) + (1 << 15);    // +16 y el redondeo
        const int cOffset = (128 << 17) + (1 << 16);   // +128 y el redondeo, sobre la SUMA de los dos píxeles (de ahí el 17)
        for (int x = 0, s = 0, d = 0; x < width; x += 2, s += 8, d += 4)
        {
            int b0 = bgra[s], g0 = bgra[s + 1], r0 = bgra[s + 2];
            int b1 = bgra[s + 4], g1 = bgra[s + 5], r1 = bgra[s + 6];
            int r = r0 + r1, g = g0 + g1, b = b0 + b1;
            uyvy[d] = (byte)((ur * r + ug * g + ub * b + cOffset) >> 17);
            uyvy[d + 1] = (byte)((yr * r0 + yg * g0 + yb * b0 + yOffset) >> 16);
            uyvy[d + 2] = (byte)((vr * r + vg * g + vb * b + cOffset) >> 17);
            uyvy[d + 3] = (byte)((yr * r1 + yg * g1 + yb * b1 + yOffset) >> 16);
        }
    }
}
