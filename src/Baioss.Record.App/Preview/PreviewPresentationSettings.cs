namespace Baioss.Record.App.Preview;

/// <summary>
/// Ajustes de la presentación del preview que la aplicación lee al arrancar (las vistas de canal se crean desde XAML y no
/// tienen la configuración a mano).
/// </summary>
public static class PreviewPresentationSettings
{
    /// <summary>
    /// Cada cuántos segundos escribe cada canal en el registro cómo se está viendo su preview (cadencia de la fuente,
    /// refrescos de la pantalla, cuadros con una duración irregular, retardo). El primero sale a los 30 s de enlazar el
    /// preview. 0 = nunca. Configuración <c>Preview:DiagnosticsIntervalSeconds</c> (no está en el appsettings embebido: en un
    /// equipo con problemas se puede bajar con la variable de entorno <c>Preview__DiagnosticsIntervalSeconds</c>).
    /// </summary>
    public static int DiagnosticsIntervalSeconds { get; set; } = 300;
}
