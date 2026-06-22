namespace Baioss.Record.Application.Channels;

/// <summary>Tipo de alarma operativa de un canal (broadcast 24/7).</summary>
public enum AlarmType
{
    /// <summary>Pérdida de señal de entrada (el dispositivo dejó de entregar vídeo).</summary>
    SignalLoss,
    /// <summary>Imagen en negro sostenida (blackdetect).</summary>
    VideoBlack,
    /// <summary>Imagen congelada / sin movimiento (freezedetect).</summary>
    VideoFreeze,
    /// <summary>Silencio de audio sostenido (silencedetect).</summary>
    AudioSilence,
    /// <summary>Espacio en disco bajo: la grabación cabe, pero queda poco margen.</summary>
    DiskLow,
    /// <summary>Espacio en disco crítico: se detendrá (o se detuvo) la grabación para no corromper el archivo.</summary>
    DiskCritical,
    /// <summary>El canal está rellenando con barras/slate porque perdió la señal pero sigue grabando.</summary>
    Slate,
}

/// <summary>Una alarma activa de un canal, con desde cuándo está activa y un mensaje legible.</summary>
public sealed record ChannelAlarm(AlarmType Type, string Message, DateTimeOffset Since)
{
    /// <summary>True para alarmas que exigen acción inmediata del operador (rojas).</summary>
    public bool IsCritical => Type is AlarmType.SignalLoss or AlarmType.DiskCritical;
}

/// <summary>
/// Estado del almacenamiento de destino de un canal: espacio libre y, durante la grabación, el
/// tiempo restante estimado al ritmo de datos actual. Alimenta la guarda de disco y la UI.
/// </summary>
public sealed record StorageInfo(long FreeBytes, long TotalBytes, TimeSpan? EstimatedRemaining)
{
    public double FreeGiB => FreeBytes / 1_073_741_824d;
    public double TotalGiB => TotalBytes / 1_073_741_824d;

    public static readonly StorageInfo Unknown = new(0, 0, null);
}
