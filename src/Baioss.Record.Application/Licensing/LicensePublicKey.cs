namespace Baioss.Record.Application.Licensing;

/// <summary>
/// Clave PÚBLICA del proveedor con la que se verifican las licencias.
///
/// <para><b>Por qué es una constante de compilación y NO configuración.</b> Si saliera de <c>appsettings.json</c>
/// o de una variable de entorno, cualquiera podría sustituirla por la suya, firmarse licencias con su propia
/// privada y activar el producto sin pagar. Estando compilada, cambiarla exige modificar y recompilar el binario.</para>
///
/// <para><b>Cómo se rellena (una sola vez, en el lado del proveedor).</b> Ejecuta la herramienta
/// <c>baioss-license keygen</c>: imprime un par de claves. La PRIVADA se guarda a buen recaudo (con ella se emiten
/// las licencias y NUNCA debe entrar en el repositorio); la PÚBLICA se pega aquí abajo.</para>
///
/// <para><b>Mientras esté vacía</b>, ninguna licencia se dará por válida: la app funcionará con el periodo de
/// prueba y rechazará cualquier clave. Es el comportamiento seguro por defecto (mejor no validar nada que
/// aceptarlo todo).</para>
/// </summary>
public static class LicensePublicKey
{
    /// <summary>
    /// SubjectPublicKeyInfo (ECDSA P-256) del proveedor, en Base64. Pegar aquí la salida de «keygen».
    /// <para><b>Es <c>static readonly</c> y NO <c>const</c> a propósito.</b> Un <c>const</c> de C# se INCRUSTA en
    /// tiempo de compilación dentro de cada ensamblado que lo usa: al cambiar la clave habría que recompilar
    /// TODOS los proyectos, y desplegar solo alguno dejaría silenciosamente la clave antigua dentro del binario
    /// (con licencias que no validan y ninguna pista de por qué). Con <c>static readonly</c> el valor se lee
    /// siempre de este ensamblado.</para>
    /// </summary>
    public static readonly string ProviderPublicKeyBase64 = "";

    /// <summary>¿Está la build preparada para validar licencias? Si no, solo hay periodo de prueba.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ProviderPublicKeyBase64);
}
