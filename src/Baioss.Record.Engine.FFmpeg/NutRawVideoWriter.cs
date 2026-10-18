using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Escritor NUT de UN flujo de vídeo en crudo con marcas de tiempo, para servirle a FFmpeg (<c>-f nut</c>) solo los cuadros que
/// llegan, cada uno con su periodo de la cadencia declarada, en vez de un <c>rawvideo</c> que FFmpeg marca por contador y al que
/// hay que darle un cuadro por periodo (repitiendo el último cuando la fuente no lo manda). Lo leen igual el FFmpeg empaquetado
/// y el 8.0 (verificado el 2026-10-17 con ffprobe y decodificando):
/// <list type="bullet">
///   <item><b>Modo tubería</b> (versión 4, <c>NUT_PIPE</c>): un solo punto de sincronía, delante del primer cuadro. El lector de
///   FFmpeg guarda en memoria cada punto de sincronía que lee: con uno por cuadro (lo que escribe su muxer para cuadros de varios
///   MB), un proceso de días acumularía cientos de MB.</item>
///   <item><b>Base de tiempo de medio periodo</b> (<see cref="TicksPerPeriod"/>): el cuadro del periodo <c>n</c> lleva la marca
///   <c>2n</c>. La tasa declarada va en la información del flujo (<c>r_frame_rate</c>), así que FFmpeg le da a cada cuadro la
///   duración de un periodo (2) y la tiene por fiable; con la base de un periodo, una duración de 1 tras un hueco la toma por
///   inventada y usa el hueco anterior, y el fin del archivo se alargaba lo que ese hueco.</item>
///   <item><b>Cuadros</b>: un único código de cuadro con todo codificado (clave, marca completa, tamaño y la suma de la cabecera,
///   que NUT exige en cuadros de más de 2·max_distance). Los datos no llevan suma: no hay nada que comprobar en un socket local.</item>
/// </list>
/// Puro: produce bytes; quien escribe el flujo pone cada cabecera de cuadro justo delante de sus datos.
/// </summary>
public sealed class NutRawVideoWriter
{
    /// <summary>Marcas por periodo de la cadencia declarada (la base de tiempo es medio periodo).</summary>
    public const int TicksPerPeriod = 2;

    /// <summary>Lo más que ocupa la cabecera de un cuadro (código, banderas, marca, tamaño y suma).</summary>
    public const int MaxFrameHeaderBytes = 1 + 2 + 10 + 5 + 4;

    private const int FlagKey = 1, FlagCodedPts = 8, FlagSizeMsb = 32, FlagChecksum = 64, FlagCoded = 4096;
    private const int NutPipe = 2;
    private const int MsbPtsShift = 7;
    private const int MaxDistance = 32768;
    private const int FrameFlags = FlagKey | FlagCodedPts | FlagSizeMsb | FlagChecksum;

    private static ReadOnlySpan<byte> FileId => "nut/multimedia container\0"u8;
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="width">Ancho del cuadro.</param>
    /// <param name="height">Alto del cuadro.</param>
    /// <param name="pixelFormat">Formato de FFmpeg de los datos: <c>uyvy422</c> o <c>bgra</c>.</param>
    /// <param name="rateN">Cadencia declarada (numerador), la de los periodos.</param>
    /// <param name="rateD">Cadencia declarada (denominador).</param>
    public NutRawVideoWriter(int width, int height, string pixelFormat, int rateN, int rateD)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Tamaño de cuadro no válido.");
        if (rateN <= 0 || rateD <= 0) throw new ArgumentOutOfRangeException(nameof(rateN), "Cadencia no válida.");
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        RateN = rateN;
        RateD = rateD;
        Preamble = BuildPreamble(FourCcFor(pixelFormat));
    }

    public int Width { get; }
    public int Height { get; }
    public string PixelFormat { get; }
    public int RateN { get; }
    public int RateD { get; }

    /// <summary>Lo que va al principio del flujo, antes del primer cuadro: la cadena de identificación, las cabeceras (principal,
    /// del flujo y su información, con la cadencia declarada) y el único punto de sincronía.</summary>
    public byte[] Preamble { get; }

    /// <summary>La etiqueta (FourCC) con la que FFmpeg reconoce el formato de los datos de un cuadro en crudo.</summary>
    public static string FourCcFor(string pixelFormat) => pixelFormat switch
    {
        "uyvy422" => "UYVY",
        "bgra" => "BGRA",
        _ => throw new ArgumentException($"Formato de píxel sin etiqueta NUT: «{pixelFormat}».", nameof(pixelFormat)),
    };

    /// <summary>
    /// Escribe en <paramref name="destination"/> la cabecera del cuadro del periodo <paramref name="period"/> (contado desde el
    /// primero del flujo, que es el 0) con <paramref name="size"/> bytes de datos detrás, y devuelve cuántos bytes ocupa. Los
    /// periodos de un flujo deben crecer: FFmpeg ordena por la marca.
    /// </summary>
    public int WriteFrameHeader(Span<byte> destination, long period, int size)
    {
        if (period < 0) throw new ArgumentOutOfRangeException(nameof(period), "La marca de un cuadro NUT no puede ser negativa.");
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (destination.Length < MaxFrameHeaderBytes) throw new ArgumentException("Destino demasiado corto.", nameof(destination));
        int p = 0;
        destination[p++] = 0; // código 0: el de la tabla, con todas las banderas codificadas en el cuadro
        p += PutVarint(destination[p..], FrameFlags); // las que el lector suma (xor) a las de la tabla, que solo tiene FLAG_CODED
        // Marca completa: la marca más 1<<msb_pts_shift (la forma que no depende de la del cuadro anterior).
        p += PutVarint(destination[p..], (ulong)(period * TicksPerPeriod + (1L << MsbPtsShift)));
        p += PutVarint(destination[p..], (ulong)size); // tamaño (size_mul 1, size_lsb 0)
        BinaryPrimitives.WriteUInt32BigEndian(destination[p..], Crc(destination[..p]));
        return p + 4;
    }

    private byte[] BuildPreamble(string fourCc)
    {
        var o = new List<byte>(256);
        o.AddRange(FileId.ToArray());

        // Base de tiempo: medio periodo de la cadencia declarada, reducida (FFmpeg rechaza una fracción no irreducible).
        long tbNum = RateD, tbDen = (long)RateN * TicksPerPeriod;
        long g = Gcd(tbNum, tbDen);
        tbNum /= g;
        tbDen /= g;
        if (tbNum >= 1L << 31 || tbDen >= 1L << 31) throw new ArgumentOutOfRangeException(nameof(RateN), "Cadencia fuera de rango para NUT.");

        var main = new List<byte>(64);
        Varint(main, 4);                    // versión 4: la que admite las banderas del flujo (modo tubería)
        Varint(main, 1);                    // versión menor
        Varint(main, 1);                    // un flujo
        Varint(main, MaxDistance);
        Varint(main, 1);                    // una base de tiempo
        Varint(main, (ulong)tbNum);
        Varint(main, (ulong)tbDen);
        // Tabla de códigos de cuadro: una sola entrada para los 255 (todos salvo 'N'), con todo codificado en el propio cuadro.
        Varint(main, FlagCoded);
        Varint(main, 6);                    // campos: pts_delta, size_mul, stream_id, size_lsb, reservados y cuántos
        Signed(main, 0);
        Varint(main, 1);
        Varint(main, 0);
        Varint(main, 0);
        Varint(main, 0);
        Varint(main, 255);
        Varint(main, 0);                    // cabeceras de elisión: ninguna (header_count - 1)
        Varint(main, NutPipe);              // banderas del flujo: sin puntos de sincronía tras el primero
        Packet(o, NutStreamReader.MainStartcode, main);

        var stream = new List<byte>(48);
        Varint(stream, 0);                  // flujo 0
        Varint(stream, 0);                  // clase: vídeo
        Varint(stream, 4);
        stream.AddRange(Encoding.ASCII.GetBytes(fourCc));
        Varint(stream, 0);                  // base de tiempo 0
        Varint(stream, MsbPtsShift);
        Varint(stream, 1u << 16);           // max_pts_distance (con la suma en cada cuadro no limita nada)
        Varint(stream, 0);                  // retardo de decodificación
        Varint(stream, 0);                  // banderas del flujo
        Varint(stream, 0);                  // sin datos extra
        Varint(stream, (ulong)Width);
        Varint(stream, (ulong)Height);
        Varint(stream, 0);                  // relación de aspecto del píxel desconocida (como el rawvideo de antes)
        Varint(stream, 0);
        Varint(stream, 0);                  // espacio de color desconocido
        Packet(o, NutStreamReader.StreamStartcode, stream);

        var info = new List<byte>(48);
        Varint(info, 1);                    // del flujo 0 (stream_id_plus1)
        Signed(info, 0);                    // sin capítulo
        Varint(info, 0);
        Varint(info, 0);
        Varint(info, 1);                    // una entrada
        Text(info, "r_frame_rate");
        Signed(info, -1);                   // de tipo texto UTF-8
        Text(info, string.Create(CultureInfo.InvariantCulture, $"{RateN}/{RateD}"));
        Packet(o, NutStreamReader.InfoStartcode, info);

        var sync = new List<byte>(8);
        Varint(sync, 0);                    // marca global del primer cuadro: 0
        Varint(sync, 0);                    // back_ptr_div16
        Packet(o, NutStreamReader.SyncpointStartcode, sync);
        return o.ToArray();
    }

    /// <summary>Paquete con código de inicio: código, forward_ptr (lo que sigue, suma incluida), carga y su suma.</summary>
    private static void Packet(List<byte> o, ulong startcode, List<byte> payload)
    {
        int start = o.Count;
        Span<byte> code = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(code, startcode);
        o.AddRange(code.ToArray());
        ulong forward = (ulong)payload.Count + 4;
        Varint(o, forward);
        if (forward > 4096)
        {
            uint headerCrc = Crc(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(o)[start..]);
            AddUInt32(o, headerCrc);
        }
        o.AddRange(payload);
        AddUInt32(o, Crc(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(payload)));
    }

    private static void AddUInt32(List<byte> o, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        o.AddRange(b.ToArray());
    }

    private static void Text(List<byte> o, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Varint(o, (ulong)bytes.Length);
        o.AddRange(bytes);
    }

    /// <summary>Entero con signo de NUT: 2|v| − (v &gt; 0).</summary>
    private static void Signed(List<byte> o, long value) => Varint(o, (ulong)(2 * Math.Abs(value) - (value > 0 ? 1 : 0)));

    private static void Varint(List<byte> o, ulong value)
    {
        Span<byte> b = stackalloc byte[10];
        int n = PutVarint(b, value);
        for (int i = 0; i < n; i++) o.Add(b[i]);
    }

    /// <summary>Varint NUT: big-endian, 7 bits por byte, el bit alto indica que sigue otro. Devuelve los bytes escritos.</summary>
    internal static int PutVarint(Span<byte> destination, ulong value)
    {
        int length = 1;
        for (ulong v = value >> 7; v != 0; v >>= 7) length++;
        for (int i = length - 1; i >= 0; i--)
        {
            byte b = (byte)((value >> (7 * i)) & 0x7F);
            destination[length - 1 - i] = i > 0 ? (byte)(b | 0x80) : b;
        }
        return length;
    }

    /// <summary>La suma de NUT: CRC-32 de polinomio 0x04C11DB7, de bit alto a bajo, sin reflejar, inicial 0 y sin xor final (la de
    /// <c>ff_crc04C11DB7_update</c>); se escribe en big-endian.</summary>
    internal static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data) crc = (crc << 8) ^ CrcTable[(byte)(crc >> 24) ^ b];
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int j = 0; j < 8; j++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            table[i] = c;
        }
        return table;
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return Math.Abs(a);
    }
}
