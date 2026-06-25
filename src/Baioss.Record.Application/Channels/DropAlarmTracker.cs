namespace Baioss.Record.Application.Channels;

/// <summary>
/// Decide cuándo encender/apagar la alarma de FRAMES PERDIDOS a partir de la telemetría ACUMULADA del
/// encoder. Exige una racha para no parpadear: enciende tras varias muestras consecutivas con nuevos
/// descartes (saturación sostenida de CPU/GPU/disco) y apaga tras varias sin descartes. Puro y testeable
/// (una muestra ≈ 1 s, el ritmo de <c>-stats_period</c>).
/// </summary>
public sealed class DropAlarmTracker
{
    private readonly int _onAfter;
    private readonly int _offAfter;
    private long _last = -1;
    private int _withDrops;
    private int _withoutDrops;
    private bool _active;

    public DropAlarmTracker(int onAfter = 3, int offAfter = 5)
    {
        _onAfter = onAfter;
        _offAfter = offAfter;
    }

    /// <summary>Procesa el total ACUMULADO de frames perdidos; devuelve si la alarma debe estar activa.</summary>
    public bool Update(long cumulativeDropped)
    {
        if (_last < 0) { _last = cumulativeDropped; return _active; } // primera muestra: solo fija la línea base
        bool grew = cumulativeDropped > _last;
        _last = cumulativeDropped;

        if (grew)
        {
            _withoutDrops = 0;
            if (!_active && ++_withDrops >= _onAfter) { _active = true; _withDrops = 0; }
        }
        else
        {
            _withDrops = 0;
            if (_active && ++_withoutDrops >= _offAfter) { _active = false; _withoutDrops = 0; }
        }
        return _active;
    }

    public bool Active => _active;

    public void Reset() { _last = -1; _withDrops = 0; _withoutDrops = 0; _active = false; }
}
