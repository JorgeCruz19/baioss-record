using System.Buffers;
using System.Buffers.Binary;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>Tipo de unidad de un flujo NUT: los paquetes con código de inicio y los frames (que no lo llevan).</summary>
public enum NutUnitKind
{
    /// <summary>La cadena de identificación con la que empieza todo flujo («nut/multimedia container»).</summary>
    FileId,
    MainHeader,
    StreamHeader,
    Info,
    Index,
    /// <summary>Punto de sincronía: donde un lector puede empezar a leer el flujo (siempre precede a los frames).</summary>
    Syncpoint,
    Frame,
}

/// <summary>
/// Una unidad del flujo NUT tal cual viaja por el cable (bytes íntegros, sin interpretar), con lo que el relé necesita
/// saber de ella: su tipo y, si es un frame, de qué flujo es. El búfer sale de un pool y se comparte por REFERENCIAS
/// (<see cref="Retain"/>/<see cref="Release"/>): un mismo frame de vídeo en crudo (4 MB a 1080p) se entrega a varios
/// consumidores sin copiarlo y vuelve al pool cuando el último lo suelta, en vez de reservar 120 MB/s en el heap grande.
/// </summary>
public sealed class NutUnit
{
    private byte[]? _buffer;
    private readonly bool _pooled;
    private int _refs = 1;

    internal NutUnit(byte[] buffer, int length, bool pooled, NutUnitKind kind, int streamId, bool isVideo, bool isAudio)
    {
        _buffer = buffer;
        _pooled = pooled;
        Length = length;
        Kind = kind;
        StreamId = streamId;
        IsVideoFrame = isVideo;
        IsAudioFrame = isAudio;
    }

    public NutUnitKind Kind { get; }

    /// <summary>Bytes de la unidad en el cable.</summary>
    public int Length { get; }

    /// <summary>Flujo NUT del frame (−1 en los paquetes).</summary>
    public int StreamId { get; }

    public bool IsVideoFrame { get; }
    public bool IsAudioFrame { get; }

    /// <summary>Las unidades que un lector necesita ANTES del primer punto de sincronía para entender el flujo.</summary>
    public bool IsHeader => Kind is NutUnitKind.FileId or NutUnitKind.MainHeader or NutUnitKind.StreamHeader or NutUnitKind.Info;

    public ReadOnlyMemory<byte> Bytes => (_buffer ?? throw new ObjectDisposedException(nameof(NutUnit))).AsMemory(0, Length);

    /// <summary>Una referencia más (otro consumidor la va a escribir). Cada una exige su <see cref="Release"/>.</summary>
    public void Retain() => Interlocked.Increment(ref _refs);

    /// <summary>Suelta una referencia; con la última, el búfer vuelve al pool.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) != 0) return;
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null && _pooled) ArrayPool<byte>.Shared.Return(buffer);
    }
}

/// <summary>
/// Lector INCREMENTAL de un flujo NUT (el contenedor de FFmpeg para vídeo y audio en crudo con marcas de tiempo) que
/// lo trocea en unidades ENTERAS sin copiarlas de más: paquetes con código de inicio (cabeceras, puntos de sincronía,
/// info, índice) y frames. Es lo que permite al relé de una captura en crudo (DeckLink) repartir el flujo a varios
/// procesos, servir a un consumidor nuevo las cabeceras y arrancarlo en un punto de sincronía, y descartar frames
/// enteros a un consumidor atascado sin romperle el flujo. Sigue la especificación NUT tal como la escribe el muxer de
/// FFmpeg (versión 3; verificado byte a byte con el FFmpeg empaquetado): cabecera principal con la tabla de códigos de
/// frame y las cabeceras de elisión, cabeceras de flujo (clase: vídeo/audio) y frames con su tamaño en la propia
/// cabecera. No interpreta marcas de tiempo ni datos: solo delimita.
/// </summary>
public sealed class NutStreamReader
{
    public const ulong MainStartcode = 0x4E4D7A561F5F04ADUL;
    public const ulong StreamStartcode = 0x4E5311405BF2F9DBUL;
    public const ulong SyncpointStartcode = 0x4E4BE4ADEECA4569UL;
    public const ulong InfoStartcode = 0x4E49AB68B596BA78UL;
    public const ulong IndexStartcode = 0x4E58DD672F23E64EUL;

    private const int FlagCodedPts = 8, FlagStreamId = 16, FlagSizeMsb = 32, FlagChecksum = 64, FlagReserved = 128,
        FlagSmData = 256, FlagHeaderIdx = 1024, FlagMatchTime = 2048, FlagCoded = 4096, FlagInvalid = 8192;

    /// <summary>Tope de un paquete con código de inicio (las cabeceras son de cientos de bytes; el índice, de KB).</summary>
    public const int MaxPacketBytes = 64 * 1024 * 1024;

    /// <summary>Tope de un frame (un 4K en crudo de 10 bits ronda los 33 MB).</summary>
    public const int MaxFrameBytes = 256 * 1024 * 1024;

    private static ReadOnlySpan<byte> FileIdString => "nut/multimedia container\0"u8;

    private readonly Stream _stream;
    private readonly bool _pooled;
    private byte[] _buf = new byte[64 * 1024];
    private int _pos, _end;
    private bool _sawFileId, _sawMain;
    private readonly FrameCode[] _codes = new FrameCode[256];
    private int[] _headerLen = new int[1];
    private readonly Dictionary<int, int> _streamClass = new();

    private struct FrameCode
    {
        public int Flags, StreamId, SizeMul, SizeLsb, ReservedCount, HeaderIdx;
    }

    /// <param name="stream">El flujo NUT (un socket, una tubería, un archivo).</param>
    /// <param name="pooled">Búferes del pool compartido (producción) o del heap (tests, donde no hace falta soltarlos).</param>
    public NutStreamReader(Stream stream, bool pooled = true)
    {
        _stream = stream;
        _pooled = pooled;
    }

    /// <summary>Versión NUT declarada en la cabecera principal (0 hasta leerla).</summary>
    public int Version { get; private set; }

    /// <summary>Flujos declarados en la cabecera principal (0 hasta leerla).</summary>
    public int StreamCount { get; private set; }

    /// <summary>Unidades entregadas desde el arranque (diagnóstico).</summary>
    public long UnitsRead { get; private set; }

    /// <summary>
    /// La siguiente unidad entera, o null al terminar el flujo limpiamente (fin justo entre dos unidades). Un fin de
    /// flujo a mitad de una unidad, un código de inicio desconocido o un frame que no cuadra con la tabla lanzan
    /// <see cref="InvalidDataException"/> / <see cref="EndOfStreamException"/>: el flujo ya no es de fiar y quien lo
    /// sirve debe cortarlo y volver a empezar. Quien recibe la unidad es dueño de una referencia (<see cref="NutUnit.Release"/>).
    /// </summary>
    public async ValueTask<NutUnit?> ReadUnitAsync(CancellationToken ct = default)
    {
        if (!await EnsureAsync(1, ct).ConfigureAwait(false)) return null;
        byte first = _buf[_pos];

        if (!_sawFileId)
        {
            if (first == (byte)'N') throw new InvalidDataException("El flujo NUT no empieza por su cadena de identificación.");
            if (!await EnsureAsync(FileIdString.Length, ct).ConfigureAwait(false))
                throw new EndOfStreamException("Flujo NUT truncado en la cadena de identificación.");
            if (!_buf.AsSpan(_pos, FileIdString.Length).SequenceEqual(FileIdString))
                throw new InvalidDataException("El flujo no es NUT (cadena de identificación distinta).");
            _sawFileId = true;
            return await TakeUnitAsync(FileIdString.Length, NutUnitKind.FileId, -1, ct).ConfigureAwait(false);
        }

        if (first == (byte)'N') return await ReadPacketAsync(ct).ConfigureAwait(false);
        if (!_sawMain) throw new InvalidDataException("Frame NUT antes de la cabecera principal.");
        return await ReadFrameAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask<NutUnit> ReadPacketAsync(CancellationToken ct)
    {
        // Código de inicio (8 bytes) + forward_ptr (varint) [+ checksum de cabecera si forward_ptr > 4096] + carga útil
        // + checksum (4 bytes); forward_ptr cuenta desde después de la cabecera hasta el final del checksum.
        int have = 9;
        ulong forward;
        int varintLength;
        while (true)
        {
            if (!await EnsureAsync(have, ct).ConfigureAwait(false)) throw new EndOfStreamException("Flujo NUT truncado en una cabecera de paquete.");
            int p = 8;
            if (TryReadVarint(_buf.AsSpan(_pos, have), ref p, out forward)) { varintLength = p - 8; break; }
            if (have >= 8 + 10) throw new InvalidDataException("forward_ptr NUT inválido.");
            have++;
        }
        ulong startcode = BinaryPrimitives.ReadUInt64BigEndian(_buf.AsSpan(_pos, 8));
        var kind = startcode switch
        {
            MainStartcode => NutUnitKind.MainHeader,
            StreamStartcode => NutUnitKind.StreamHeader,
            SyncpointStartcode => NutUnitKind.Syncpoint,
            InfoStartcode => NutUnitKind.Info,
            IndexStartcode => NutUnitKind.Index,
            _ => throw new InvalidDataException($"Código de inicio NUT desconocido: 0x{startcode:X16}."),
        };
        if (forward < 4 || forward > MaxPacketBytes) throw new InvalidDataException($"Paquete NUT de {forward} bytes: fuera de rango.");
        int headerBytes = 8 + varintLength + (forward > 4096 ? 4 : 0);
        int total = checked(headerBytes + (int)forward);

        var unit = await TakeUnitAsync(total, kind, -1, ct).ConfigureAwait(false);
        // Si la cabecera no se entiende, la unidad (del pool) se devuelve antes de propagar: nadie más la tiene.
        try { ParsePacket(unit, kind, headerBytes, (int)forward - 4); }
        catch { unit.Release(); throw; }
        return unit;
    }

    /// <summary>Interpreta la carga útil (sin el checksum final) de las cabeceras que el lector necesita entender.</summary>
    private void ParsePacket(NutUnit unit, NutUnitKind kind, int headerBytes, int payloadLength)
    {
        var payload = unit.Bytes.Span.Slice(headerBytes, payloadLength);
        if (kind == NutUnitKind.MainHeader) { ParseMainHeader(payload); _sawMain = true; }
        else if (kind == NutUnitKind.StreamHeader) ParseStreamHeader(payload);
    }

    private async ValueTask<NutUnit> ReadFrameAsync(CancellationToken ct)
    {
        int have = Math.Max(1, _end - _pos);
        while (true)
        {
            if (!await EnsureAsync(have, ct).ConfigureAwait(false)) throw new EndOfStreamException("Flujo NUT truncado en una cabecera de frame.");
            have = _end - _pos;
            if (TryParseFrameHeader(_buf.AsSpan(_pos, have), out int headerBytes, out long dataBytes, out int streamId))
            {
                if (dataBytes > MaxFrameBytes) throw new InvalidDataException($"Frame NUT de {dataBytes} bytes: fuera de rango.");
                int total = checked(headerBytes + (int)dataBytes);
                return await TakeUnitAsync(total, NutUnitKind.Frame, streamId, ct).ConfigureAwait(false);
            }
            if (have >= 4096) throw new InvalidDataException("Cabecera de frame NUT demasiado larga.");
            have += 16; // pedir más bytes y volver a intentar
        }
    }

    /// <summary>Saca del flujo una unidad de <paramref name="total"/> bytes: lo ya leído en el búfer se copia y el resto se
    /// lee DIRECTAMENTE en el búfer de la unidad (un frame de vídeo en crudo no pasa dos veces por memoria).</summary>
    private async ValueTask<NutUnit> TakeUnitAsync(int total, NutUnitKind kind, int streamId, CancellationToken ct)
    {
        var buffer = _pooled ? ArrayPool<byte>.Shared.Rent(total) : new byte[total];
        try
        {
            int have = Math.Min(_end - _pos, total);
            _buf.AsSpan(_pos, have).CopyTo(buffer);
            _pos += have;
            int filled = have;
            while (filled < total)
            {
                int n = await _stream.ReadAsync(buffer.AsMemory(filled, total - filled), ct).ConfigureAwait(false);
                if (n <= 0) throw new EndOfStreamException($"Flujo NUT truncado: faltan {total - filled} bytes de una unidad.");
                filled += n;
            }
        }
        catch
        {
            if (_pooled) ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        bool video = false, audio = false;
        if (kind == NutUnitKind.Frame && _streamClass.TryGetValue(streamId, out int cls)) { video = cls == 0; audio = cls == 1; }
        UnitsRead++;
        return new NutUnit(buffer, total, _pooled, kind, streamId, video, audio);
    }

    /// <summary>Garantiza <paramref name="need"/> bytes en el búfer; false solo en un fin de flujo LIMPIO (nada pendiente).</summary>
    private async ValueTask<bool> EnsureAsync(int need, CancellationToken ct)
    {
        if (_end - _pos >= need) return true;
        if (_pos > 0)
        {
            _buf.AsSpan(_pos, _end - _pos).CopyTo(_buf);
            _end -= _pos;
            _pos = 0;
        }
        if (_buf.Length < need) Array.Resize(ref _buf, Math.Max(need, _buf.Length * 2));
        while (_end < need)
        {
            int n = await _stream.ReadAsync(_buf.AsMemory(_end), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                if (_end == 0) return false;
                throw new EndOfStreamException("Flujo NUT truncado.");
            }
            _end += n;
        }
        return true;
    }

    /// <summary>Varint NUT: big-endian, 7 bits por byte, el bit alto indica continuación. False si faltan bytes.</summary>
    internal static bool TryReadVarint(ReadOnlySpan<byte> span, ref int pos, out ulong value)
    {
        value = 0;
        for (int i = 0; i < 10; i++)
        {
            if (pos >= span.Length) return false;
            byte b = span[pos++];
            value = (value << 7) | (byte)(b & 0x7F);
            if ((b & 0x80) == 0) return true;
        }
        throw new InvalidDataException("Varint NUT de más de 10 bytes.");
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> span, ref int pos)
        => TryReadVarint(span, ref pos, out var v) ? v : throw new InvalidDataException("Cabecera NUT truncada.");

    /// <summary>La tabla de códigos de frame (256 entradas, 'N' siempre inválido) y las cabeceras de elisión: lo único de
    /// la cabecera principal que hace falta para saber cuánto ocupa cada frame. Misma lógica que nutdec.c.</summary>
    private void ParseMainHeader(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        Version = (int)ReadVarint(payload, ref pos);
        if (Version is < 2 or > 4) throw new InvalidDataException($"Versión NUT {Version} no soportada.");
        if (Version > 3) ReadVarint(payload, ref pos); // minor_version
        StreamCount = (int)ReadVarint(payload, ref pos);
        if (StreamCount is <= 0 or > 256) throw new InvalidDataException($"NUT con {StreamCount} flujos.");
        ReadVarint(payload, ref pos); // max_distance
        int timeBases = (int)ReadVarint(payload, ref pos);
        if (timeBases is <= 0 or > 4096) throw new InvalidDataException("Bases de tiempo NUT fuera de rango.");
        for (int i = 0; i < timeBases; i++) { ReadVarint(payload, ref pos); ReadVarint(payload, ref pos); }

        int tmpMul = 1, tmpStream = 0, tmpHeadIdx = 0;
        for (int i = 0; i < 256;)
        {
            int flags = (int)ReadVarint(payload, ref pos);
            int fields = (int)ReadVarint(payload, ref pos);
            if (fields > 0) ReadVarint(payload, ref pos);            // pts_delta (con signo; no se usa)
            if (fields > 1) tmpMul = (int)ReadVarint(payload, ref pos);
            if (fields > 2) tmpStream = (int)ReadVarint(payload, ref pos);
            int tmpSize = fields > 3 ? (int)ReadVarint(payload, ref pos) : 0;
            int tmpRes = fields > 4 ? (int)ReadVarint(payload, ref pos) : 0;
            int count = fields > 5 ? (int)ReadVarint(payload, ref pos) : tmpMul - tmpSize;
            if (fields > 6) ReadVarint(payload, ref pos);            // match_time_delta
            if (fields > 7) tmpHeadIdx = (int)ReadVarint(payload, ref pos);
            for (int j = 8; j < fields; j++) ReadVarint(payload, ref pos);
            if (count <= 0 || count > 256 - i - (i <= 'N' ? 1 : 0))
                throw new InvalidDataException("Tabla de códigos de frame NUT inconsistente.");
            for (int j = 0; j < count; j++, i++)
            {
                if (i == 'N') { _codes[i] = new FrameCode { Flags = FlagInvalid }; j--; continue; }
                _codes[i] = new FrameCode
                {
                    Flags = flags, StreamId = tmpStream, SizeMul = tmpMul, SizeLsb = tmpSize + j,
                    ReservedCount = tmpRes, HeaderIdx = tmpHeadIdx,
                };
            }
        }

        // Cabeceras de elisión (FFmpeg escribe siempre 6): su longitud se descuenta del tamaño del frame en el cable.
        if (pos < payload.Length)
        {
            ulong rawCount = ReadVarint(payload, ref pos);
            if (rawCount >= 128) throw new InvalidDataException("Demasiadas cabeceras de elisión NUT.");
            int headerCount = (int)rawCount + 1;
            var lengths = new int[headerCount];
            for (int i = 1; i < headerCount; i++)
            {
                int len = (int)ReadVarint(payload, ref pos);
                if (len is <= 0 or >= 256 || pos + len > payload.Length) throw new InvalidDataException("Cabecera de elisión NUT inválida.");
                lengths[i] = len;
                pos += len;
            }
            _headerLen = lengths;
        }
        else _headerLen = new int[1];
    }

    private void ParseStreamHeader(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        int streamId = (int)ReadVarint(payload, ref pos);
        int streamClass = (int)ReadVarint(payload, ref pos);
        _streamClass[streamId] = streamClass;
    }

    /// <summary>Cabecera de un frame según su código: cuántos bytes ocupa la cabecera y cuántos los datos en el cable.
    /// False si el trozo no contiene la cabecera entera (hay que leer más).</summary>
    private bool TryParseFrameHeader(ReadOnlySpan<byte> span, out int headerBytes, out long dataBytes, out int streamId)
    {
        headerBytes = 0; dataBytes = 0; streamId = -1;
        int pos = 0;
        var fc = _codes[span[pos++]];
        int flags = fc.Flags;
        if ((flags & FlagInvalid) != 0) throw new InvalidDataException("Código de frame NUT inválido (flujo desincronizado).");
        if ((flags & FlagCoded) != 0)
        {
            if (!TryReadVarint(span, ref pos, out var coded)) return false;
            flags ^= (int)coded;
        }
        streamId = fc.StreamId;
        if ((flags & FlagStreamId) != 0) { if (!TryReadVarint(span, ref pos, out var s)) return false; streamId = (int)s; }
        if ((flags & FlagCodedPts) != 0 && !TryReadVarint(span, ref pos, out _)) return false;
        long size = fc.SizeLsb;
        if ((flags & FlagSizeMsb) != 0)
        {
            if (!TryReadVarint(span, ref pos, out var msb)) return false;
            // Valores imposibles (flujo corrupto) como InvalidDataException, no como desbordamiento o índice fuera de rango.
            if (msb > int.MaxValue || fc.SizeMul <= 0) throw new InvalidDataException("Tamaño de frame NUT fuera de rango.");
            try { size = checked(size + (long)msb * fc.SizeMul); }
            catch (OverflowException) { throw new InvalidDataException("Tamaño de frame NUT fuera de rango."); }
        }
        if ((flags & FlagMatchTime) != 0 && !TryReadVarint(span, ref pos, out _)) return false;
        int headerIdx = fc.HeaderIdx;
        if ((flags & FlagHeaderIdx) != 0)
        {
            if (!TryReadVarint(span, ref pos, out var h)) return false;
            if (h >= (ulong)_headerLen.Length) throw new InvalidDataException("Índice de cabecera de elisión NUT fuera de rango.");
            headerIdx = (int)h;
        }
        int reserved = fc.ReservedCount;
        if ((flags & FlagReserved) != 0) { if (!TryReadVarint(span, ref pos, out var r)) return false; reserved = (int)r; }
        for (int i = 0; i < reserved; i++) if (!TryReadVarint(span, ref pos, out _)) return false;
        if ((flags & FlagChecksum) != 0) { if (pos + 4 > span.Length) return false; pos += 4; }
        if ((flags & FlagSmData) != 0) throw new InvalidDataException("Frame NUT con datos laterales (versión 4): no soportado.");
        if (headerIdx < 0 || headerIdx >= _headerLen.Length) throw new InvalidDataException("Índice de cabecera de elisión NUT fuera de rango.");
        if (size > 4096) headerIdx = 0;
        dataBytes = size - _headerLen[headerIdx];
        if (dataBytes < 0) throw new InvalidDataException("Tamaño de frame NUT negativo.");
        headerBytes = pos;
        return true;
    }
}
