using System.Globalization;

namespace Baioss.Record.Application.Localization;

/// <summary>Idiomas que ofrece la aplicación.</summary>
public enum AppLanguage
{
    /// <summary>Español: idioma de origen del producto y RESPALDO si falta una traducción.</summary>
    Spanish,
    /// <summary>Inglés.</summary>
    English,
}

/// <summary>
/// Estado del idioma y traducción de cadenas. Vive aquí —y no en la capa de interfaz— porque hay textos de
/// usuario que se componen en esta capa (el resumen de la licencia, por ejemplo) y deben hablar el mismo
/// idioma que la ventana que los muestra; además así el catálogo se puede probar sin arrastrar WPF.
///
/// <para>La capa de interfaz envuelve esto en un objeto enlazable (<c>Loc</c>) para que el XAML se actualice
/// solo al cambiar de idioma.</para>
/// </summary>
public static class Localizer
{
    private static AppLanguage _language = AppLanguage.Spanish;

    /// <summary>Idioma vigente. Cambiarlo eleva <see cref="LanguageChanged"/>.</summary>
    public static AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Se eleva al cambiar el idioma: la interfaz refresca sus enlaces y los textos ya compuestos.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// Texto de una clave. Si falta en el idioma vigente se usa el ESPAÑOL y, si tampoco está, la propia
    /// clave: un texto sin traducir es un defecto cosmético, pero una excepción aquí rompería la ventana.
    /// </summary>
    public static string T(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        var table = _language == AppLanguage.English ? Strings.English : Strings.Spanish;
        if (table.TryGetValue(key, out var value)) return value;
        return Strings.Spanish.TryGetValue(key, out var fallback) ? fallback : key;
    }

    /// <summary>Texto con formato: <c>Localizer.F("Lic_Summary_TrialDays", 9)</c>.</summary>
    public static string F(string key, params object?[] args)
    {
        var format = T(key);
        try { return string.Format(CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return format; } // plantilla mal escrita: mejor el texto crudo que reventar
    }

    /// <summary>
    /// Elige entre la variante SINGULAR y la PLURAL según <paramref name="count"/>, y le pasa el número.
    /// Evita el clásico «1 canales» y deja la decisión en cada idioma (que puede pluralizar distinto).
    /// </summary>
    public static string Plural(string singularKey, string pluralKey, int count)
        => F(count == 1 ? singularKey : pluralKey, count);

    /// <summary>
    /// Idioma que corresponde a la configuración de Windows: español para CUALQUIER variante regional de
    /// español (es-ES, es-MX, es-GT…), inglés en los demás casos.
    /// </summary>
    public static AppLanguage FromSystem(CultureInfo? culture = null)
        => string.Equals((culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName, "es",
                         StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Spanish
            : AppLanguage.English;
}
