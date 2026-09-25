namespace Baioss.Record.App;

/// <summary>
/// Interruptores de la interfaz que se cambian EN CÓDIGO, no desde la aplicación: herramientas de diagnóstico que un
/// desarrollador puede activar en su build sin que el operador las vea en la de producción. Para activar una, poner su
/// valor a <c>true</c> y recompilar.
/// </summary>
public static class UiFeatures
{
    /// <summary>
    /// «Presets de grabación»: muestra la línea de comandos FFmpeg que genera el preset seleccionado. Oculta desde el
    /// 2026-09-24 (petición del cliente): es información técnica que no le sirve al operador.
    /// </summary>
    public static bool ShowFfmpegCommandLine { get; } = false;
}
