namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Alinea el reloj del AUDIO con el del VÍDEO dentro del MPEG-TS que el receptor entrega al relé, cuando el emisor los
/// trae con relojes DISTINTOS, y aplica el retardo de audio manual de la fuente. Medido en un servidor RTMP real
/// (2026-09-23): según sus marcas de tiempo el audio iba 12 317 s (3,4 h) «por delante» del vídeo; FFmpeg respeta esas
/// marcas, así que el MP4 salía con el audio inaudible (3,4 h desplazado) tanto grabando directo como por el relé. Ningún
/// reproductor puede saber la relación real entre dos relojes ajenos: lo único fiable es que el servidor entrega audio y
/// vídeo ENTRELAZADOS en tiempo real, así que un PES de audio corresponde al vídeo que llegó justo antes.
/// <list type="bullet">
///   <item>Lee el PTS del primer PES de vídeo (PID 256) y del primer PES de audio (PID 257, los PIDs que el muxer
///   mpegts de FFmpeg asigna a <c>-map 0:v:0 -map 0:a:0</c>). Si difieren más de <see cref="MaxSkewSeconds"/>, son
///   relojes distintos: el audio se RETIENE durante <see cref="SettleSeconds"/> de reloj de pared y en el último
///   segundo se mide, por llegada, cuánto va cada PES de audio por delante del último DTS de vídeo (mediana); ese
///   desfase menos el adelanto PTS-DTS del vídeo se resta al audio desde el primer PES retenido. NO vale anclar por el
///   primer par: al conectar, el servidor real vuelca una ráfaga de caché con audio ANTERIOR al vídeo (1,2 s medidos),
///   y con el primer par el audio quedaba 1,2 s tarde en toda la sesión.</item>
///   <item>Después, un servo lento (≤ <see cref="MaxServoStepTicks"/> por PES de audio, error filtrado, banda muerta
///   de 40 ms) mantiene esa relación por si los relojes derivan; el audio nunca retrocede.</item>
///   <item>Con relojes coherentes (a menos de <see cref="MaxSkewSeconds"/>: lo normal, incluida la caché de GOP de los
///   servidores) no toca las marcas: ahí son la verdad y la llegada no.</item>
///   <item><see cref="AudioDelayTicks"/> (retardo manual de la fuente, positivo = audio más tarde) se suma siempre, con o
///   sin relojes distintos: es el ajuste fino de labios que solo un operador puede juzgar.</item>
///   <item>Trabaja sobre paquetes TS de 188 bytes, reensamblando los partidos entre lecturas y resincronizando en 0x47;
///   el PES arranca siempre en el primer paquete del PES (así lo escribe FFmpeg), así que la cabecera cabe entera.
///   PCR (en el PID de vídeo), PAT/PMT y contadores de continuidad no se tocan.</item>
///   <item><see cref="Reset"/> al cambiar de flujo (el receptor se relanzó): el desfase se mide de nuevo.</item>
/// </list>
/// Puro y sin E/S (el reloj de pared se le pasa): testeable con paquetes sintéticos.
/// </summary>
public sealed class TsAudioClockAligner
{
    public const int PacketSize = 188;
    public const int VideoPid = 0x100;
    public const int AudioPid = 0x101;
    private const long PtsModulo = 1L << 33;
    private const long TicksPerSecond = 90_000;

    /// <summary>Cuánto se retiene el audio, en reloj de pared, para medir la relación audio-vídeo en régimen (pasada la ráfaga inicial).</summary>
    public const double SettleSeconds = 2.5;
    /// <summary>Ventana final del asentamiento cuyas muestras (mediana) fijan el desfase.</summary>
    public const double MeasureWindowSeconds = 1.0;
    /// <summary>Muestras mínimas en la ventana; si no llegan, se espera hasta <see cref="MaxSettleSeconds"/>.</summary>
    public const int MinSettleSamples = 10;
    /// <summary>Tope del asentamiento: pasado, se decide con lo que haya (o con el primer par si no hay nada). Por debajo del
    /// análisis de entrada del proceso del canal (5 s), que así siempre ve audio si el flujo lo trae.</summary>
    public const double MaxSettleSeconds = 4.0;

    /// <summary>Servo: banda muerta (40 ms) del error FILTRADO por debajo de la cual no se ajusta nada.</summary>
    public const long ServoDeadbandTicks = 3600;
    /// <summary>Servo: ajuste máximo del desfase por PES de audio (2 ms): corrige hasta ~9 % de deriva sin que el audio retroceda.</summary>
    public const long MaxServoStepTicks = 180;
    private const double ServoGain = 0.05;
    // El error se filtra (media móvil exponencial, ~0,7 s) para que el servo siga solo derivas sostenidas y no el vaivén del
    // entrelazado paquete a paquete (un vídeo grande o una ráfaga de red cambian el orden de llegada unos milisegundos).
    private const double ServoSmoothing = 1.0 / 32;

    private readonly byte[] _carry = new byte[PacketSize];
    private int _carryLength;
    private readonly List<byte[]> _heldAudio = new();
    private long _heldBytes;
    private long? _firstVideoPts;
    private long? _firstVideoDts;
    private long _lastVideoDts;
    private long? _firstAudioPts;
    private bool _decided;
    private long _offsetTicks;
    private bool _servo;
    private long _servoBase;
    private long _servoTotalTicks;
    private double _servoError; // error filtrado (ticks)
    // Asentamiento (relojes distintos): muestras (instante de llegada, audio_pts - último_dts_vídeo) mientras se retiene.
    private bool _settling;
    private double _settleStart;
    private long _firstPairSkew;
    private readonly List<(double At, long Lead)> _settleSamples = new();
    private double _firstPairSkewSeconds;

    /// <summary>A partir de esta diferencia entre el primer PTS de audio y el de vídeo se consideran relojes distintos.
    /// Una caché de GOP legítima adelanta el vídeo unos segundos como mucho; 3,4 h no es una caché.</summary>
    public double MaxSkewSeconds { get; init; } = 10;

    /// <summary>Tope de audio retenido a la espera del primer PES de vídeo; superado, se suelta sin corregir.</summary>
    public int MaxHeldBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Retardo manual del audio en ticks de 90 kHz (positivo = el audio suena más tarde). Ajuste fino de labios
    /// de la fuente; se aplica siempre, con o sin relojes distintos. Cambiarlo en caliente vale desde el PES siguiente.</summary>
    public long AudioDelayTicks { get; set; }

    /// <summary>Desfase restado al audio (segundos, positivo = el audio iba por delante), sin contar el retardo manual;
    /// 0 si los relojes eran coherentes; null mientras no se ha decidido (incluido el asentamiento).</summary>
    public double? CorrectionSeconds => _decided ? _offsetTicks / (double)TicksPerSecond : null;

    /// <summary>Lo que daba el primer par (audio − vídeo) cuando hubo relojes distintos; la diferencia con
    /// <see cref="CorrectionSeconds"/> es la ráfaga de caché que el asentamiento evitó. Null si no hubo relojes distintos.</summary>
    public double? FirstPairSkewSeconds => _servo ? _firstPairSkewSeconds : null;

    /// <summary>Deriva acumulada (s) que el servo ha compensado desde el inicio del flujo (positivo = el reloj del audio iba
    /// más lento que el del vídeo y hubo que adelantarlo); 0 con relojes coherentes.</summary>
    public double DriftCompensatedSeconds => -_servoTotalTicks / (double)TicksPerSecond;

    /// <summary>¿Se está reteniendo el audio para medir la relación en régimen?</summary>
    public bool IsSettling => _settling;

    /// <summary>Se eleva UNA vez por flujo cuando se detectan relojes distintos, con el desfase aplicado en segundos.</summary>
    public event Action<double>? SkewCorrected;

    /// <summary>Procesa un trozo del flujo llegado en <paramref name="nowSeconds"/> (reloj de pared, monótono) y devuelve los
    /// bytes a reenviar (paquetes TS completos; puede ser vacío mientras se retiene audio o se espera el resto de un paquete).</summary>
    public byte[] Process(ReadOnlySpan<byte> data, double nowSeconds)
    {
        var output = new List<byte>(data.Length + PacketSize);
        int offset = 0;

        // Completa el paquete partido en la lectura anterior.
        if (_carryLength > 0)
        {
            int needed = PacketSize - _carryLength;
            if (data.Length < needed)
            {
                data.CopyTo(_carry.AsSpan(_carryLength));
                _carryLength += data.Length;
                return Array.Empty<byte>();
            }
            data[..needed].CopyTo(_carry.AsSpan(_carryLength));
            _carryLength = 0;
            offset = needed;
            HandlePacket(_carry.AsSpan(), output, nowSeconds);
        }

        while (offset < data.Length)
        {
            if (data[offset] != 0x47)
            {
                // Fuera de sincronía (no debería pasar con un TS de FFmpeg): avanza hasta el siguiente byte de sincronía.
                offset++;
                continue;
            }
            if (data.Length - offset < PacketSize)
            {
                data[offset..].CopyTo(_carry);
                _carryLength = data.Length - offset;
                break;
            }
            HandlePacket(data.Slice(offset, PacketSize), output, nowSeconds);
            offset += PacketSize;
        }
        // Sin paquetes de audio nuevos, el asentamiento también puede vencer por reloj (flujo con audio a ráfagas).
        if (_settling) FinishSettlingIfDue(output, nowSeconds, force: false);
        return output.ToArray();
    }

    /// <summary>Olvida el flujo anterior: desfase, audio retenido, asentamiento y paquete a medias (el retardo manual se conserva).</summary>
    public void Reset()
    {
        _carryLength = 0;
        _heldAudio.Clear();
        _heldBytes = 0;
        _firstVideoPts = null;
        _firstVideoDts = null;
        _lastVideoDts = 0;
        _firstAudioPts = null;
        _decided = false;
        _offsetTicks = 0;
        _servo = false;
        _servoBase = 0;
        _servoTotalTicks = 0;
        _servoError = 0;
        _settling = false;
        _settleStart = 0;
        _firstPairSkew = 0;
        _firstPairSkewSeconds = 0;
        _settleSamples.Clear();
    }

    private void HandlePacket(ReadOnlySpan<byte> packet, List<byte> output, double now)
    {
        int pid = ((packet[1] & 0x1F) << 8) | packet[2];
        bool payloadStart = (packet[1] & 0x40) != 0;

        if (pid == VideoPid)
        {
            if (payloadStart && TryReadPesPts(packet, out int ptsIndex, out long pts, out bool hasDts))
            {
                // El DTS de vídeo es monótono (el PTS no, con B-frames): es la referencia del asentamiento y del servo.
                _lastVideoDts = hasDts ? ReadTimestamp(packet, ptsIndex + 5) : pts;
                if (_firstVideoPts is null)
                {
                    _firstVideoPts = pts;
                    _firstVideoDts = _lastVideoDts;
                    DecideIfPossible(output, now);
                }
            }
            output.AddRange(packet.ToArray());
            return;
        }

        if (pid != AudioPid)
        {
            output.AddRange(packet.ToArray());
            return;
        }

        var copy = packet.ToArray();
        if (!_decided)
        {
            if (payloadStart && TryReadPesPts(copy, out _, out long pts, out _))
            {
                _firstAudioPts ??= pts;
                if (_settling && _firstVideoPts is not null) _settleSamples.Add((now, Signed33(pts - _lastVideoDts)));
            }
            _heldAudio.Add(copy);
            _heldBytes += copy.Length;
            if (_settling) FinishSettlingIfDue(output, now, force: false);
            else DecideIfPossible(output, now);
            if (!_decided && _heldBytes > MaxHeldBytes)
            {
                // Sin vídeo con el que comparar (¿flujo solo audio?) o asentamiento imposible: no se retiene más.
                if (_settling) FinishSettlingIfDue(output, now, force: true);
                else { _decided = true; _offsetTicks = 0; FlushHeld(output); }
            }
            return;
        }

        if (payloadStart && (_offsetTicks != 0 || _servo || AudioDelayTicks != 0)) Shift(copy);
        output.AddRange(copy);
    }

    private void DecideIfPossible(List<byte> output, double now)
    {
        if (_decided || _settling || _firstVideoPts is null || _firstAudioPts is null) return;
        long skew = Signed33(_firstAudioPts.Value - _firstVideoPts.Value);
        if (Math.Abs(skew) <= MaxSkewSeconds * TicksPerSecond)
        {
            // Relojes coherentes: las marcas son la verdad. Se suelta lo retenido tal cual (con el retardo manual, si lo hay).
            _decided = true;
            _offsetTicks = 0;
            FlushHeld(output);
            return;
        }
        // Relojes distintos: se retiene el audio y se mide en régimen antes de decidir.
        _settling = true;
        _settleStart = now;
        _firstPairSkew = skew;
        _firstPairSkewSeconds = skew / (double)TicksPerSecond;
        _settleSamples.Clear();
    }

    /// <summary>Cierra el asentamiento cuando ha pasado el tiempo y hay muestras (o al vencer el tope / por fuerza).</summary>
    private void FinishSettlingIfDue(List<byte> output, double now, bool force)
    {
        double elapsed = now - _settleStart;
        var window = _settleSamples.Where(s => now - s.At <= MeasureWindowSeconds).Select(s => s.Lead).ToList();
        bool due = elapsed >= SettleSeconds && window.Count >= MinSettleSamples;
        bool expired = elapsed >= MaxSettleSeconds || force;
        if (!due && !expired) return;

        if (window.Count == 0) window = _settleSamples.Select(s => s.Lead).ToList();
        long reorderLead = Signed33(_firstVideoPts!.Value - _firstVideoDts!.Value);
        if (window.Count > 0)
        {
            window.Sort();
            long median = window[window.Count / 2];
            // El audio que llega tras un vídeo corresponde a ese vídeo: su PTS debe quedar donde el PTS de ese vídeo.
            _offsetTicks = median - reorderLead;
        }
        else _offsetTicks = _firstPairSkew; // sin muestras (ningún audio tras el primer vídeo): lo único que hay

        _settling = false;
        _decided = true;
        _servo = true;
        _servoBase = reorderLead;
        _servoError = 0;
        FlushHeld(output);
        SkewCorrected?.Invoke(_offsetTicks / (double)TicksPerSecond);
    }

    /// <summary>Diferencia con signo entre dos marcas de 33 bits (dan la vuelta cada 26,5 h).</summary>
    private static long Signed33(long difference)
    {
        long d = difference & (PtsModulo - 1);
        return d >= PtsModulo / 2 ? d - PtsModulo : d;
    }

    private void FlushHeld(List<byte> output)
    {
        foreach (var packet in _heldAudio)
        {
            // El audio retenido se suelta con el desfase recién medido, SIN servo: son PES antiguos (hasta 2,5 s más la
            // ráfaga) y compararlos con el último vídeo llegado los haría parecer atrasados y desviaría el desfase.
            if ((packet[1] & 0x40) != 0 && (_offsetTicks != 0 || _servo || AudioDelayTicks != 0)) Shift(packet, servo: false);
            output.AddRange(packet);
        }
        _heldAudio.Clear();
        _heldBytes = 0;
    }

    /// <summary>Reescribe el PTS (y el DTS si lo hay) del PES de audio que empieza en este paquete: menos el desfase de
    /// relojes (con el servo, salvo en el vaciado del audio retenido) y más el retardo manual.</summary>
    private void Shift(byte[] packet, bool servo = true)
    {
        if (!TryReadPesPts(packet, out int ptsIndex, out long pts, out bool hasDts)) return;
        if (_servo && servo)
        {
            // Cuánto se ha separado el audio corregido del último DTS de vídeo respecto a la relación medida; se
            // corrige poco a poco (como mucho 2 ms por PES) y solo fuera de la banda muerta.
            long error = Signed33((pts - _offsetTicks) - _lastVideoDts) - _servoBase;
            _servoError += (error - _servoError) * ServoSmoothing;
            if (Math.Abs(_servoError) > ServoDeadbandTicks)
            {
                long step = (long)Math.Clamp(_servoError * ServoGain, -MaxServoStepTicks, MaxServoStepTicks);
                _offsetTicks += step;
                _servoTotalTicks += step;
                _servoError -= step; // el ajuste ya reduce el error real: que el filtro no lo cuente dos veces
            }
        }
        long shift = -_offsetTicks + AudioDelayTicks;
        WriteTimestamp(packet, ptsIndex, (pts + shift) & (PtsModulo - 1));
        if (hasDts)
        {
            long dts = ReadTimestamp(packet, ptsIndex + 5);
            WriteTimestamp(packet, ptsIndex + 5, (dts + shift) & (PtsModulo - 1));
        }
    }

    /// <summary>Localiza la cabecera PES al inicio de la carga útil de un paquete con payload_unit_start y lee su PTS.</summary>
    private static bool TryReadPesPts(ReadOnlySpan<byte> packet, out int ptsIndex, out long pts, out bool hasDts)
    {
        ptsIndex = 0; pts = 0; hasDts = false;
        if ((packet[1] & 0x40) == 0) return false;
        int adaptation = (packet[3] >> 4) & 0x3;
        if (adaptation == 0 || adaptation == 2) return false; // sin carga útil
        int payload = 4;
        if (adaptation == 3) payload += 1 + packet[4];
        // Cabecera PES: 00 00 01, stream_id, longitud (2), '10xxxxxx', flags, header_length, PTS[5] [DTS[5]]
        if (payload + 14 > packet.Length) return false;
        if (packet[payload] != 0 || packet[payload + 1] != 0 || packet[payload + 2] != 1) return false;
        if ((packet[payload + 6] & 0xC0) != 0x80) return false;
        int flags = packet[payload + 7] >> 6;
        if ((flags & 0x2) == 0) return false; // sin PTS
        hasDts = flags == 0x3;
        if (hasDts && payload + 19 > packet.Length) return false;
        ptsIndex = payload + 9;
        pts = ReadTimestamp(packet, ptsIndex);
        return true;
    }

    private static long ReadTimestamp(ReadOnlySpan<byte> p, int i)
        => ((long)(p[i] & 0x0E) << 29) | ((long)p[i + 1] << 22) | ((long)(p[i + 2] & 0xFE) << 14) | ((long)p[i + 3] << 7) | ((long)p[i + 4] >> 1);

    private static void WriteTimestamp(byte[] p, int i, long ts)
    {
        p[i] = (byte)((p[i] & 0xF0) | (int)((ts >> 29) & 0x0E) | 0x01); // conserva los 4 bits de marca ('0010'/'0011'/'0001')
        p[i + 1] = (byte)((ts >> 22) & 0xFF);
        p[i + 2] = (byte)(((ts >> 14) & 0xFE) | 0x01);
        p[i + 3] = (byte)((ts >> 7) & 0xFF);
        p[i + 4] = (byte)(((ts << 1) & 0xFE) | 0x01);
    }
}
