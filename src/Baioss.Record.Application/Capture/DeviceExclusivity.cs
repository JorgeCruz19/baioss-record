using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Capture;

/// <summary>
/// Determina si dos entradas comparten un dispositivo de captura EXCLUSIVO. Una cámara/capturadora
/// DirectShow y una tarjeta DeckLink (SDI) no admiten dos dueños: asignar la misma a dos canales hace que
/// el segundo falle al abrir FFmpeg (se queda sin preview ni grabación). Los archivos y el audio DirectShow
/// SÍ se comparten entre procesos, así que no cuentan como conflicto.
/// </summary>
public static class DeviceExclusivity
{
    /// <summary>True si el tipo de entrada usa un dispositivo de hardware que no admite dos aperturas a la vez.</summary>
    public static bool IsExclusive(InputType type) => type is InputType.DirectShow or InputType.DecklinkSdi;

    /// <summary>
    /// True si <paramref name="a"/> y <paramref name="b"/> usan el MISMO dispositivo exclusivo (mismo tipo y
    /// mismo <see cref="InputSource.Uri"/>). El audio DirectShow va aparte (en Parameters) y se comparte, por
    /// lo que no entra en la comparación.
    /// </summary>
    public static bool Conflicts(InputSource a, InputSource b)
        => a.Type == b.Type
           && IsExclusive(a.Type)
           && !string.IsNullOrEmpty(a.Uri)
           && string.Equals(a.Uri, b.Uri, StringComparison.OrdinalIgnoreCase);
}
