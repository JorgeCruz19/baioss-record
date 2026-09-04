using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using Baioss.Record.Application.Localization;

namespace Baioss.Record.App.Localization;

/// <summary>
/// Adaptador ENLAZABLE del <see cref="Localizer"/> para el XAML: un singleton con indexador al que apunta la
/// extensión de marcado <c>{loc:T Clave}</c>.
///
/// <para>Es lo que hace que el idioma se pueda cambiar EN CALIENTE: al cambiarlo se notifica el indexador y
/// WPF vuelve a pedir el valor de todos los enlaces, sin reiniciar la aplicación.</para>
///
/// <para>También decide el idioma INICIAL: la elección guardada del operador si la hay y, si no, el idioma de
/// Windows.</para>
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>Instancia única a la que se enlaza el XAML.</summary>
    public static Loc Instance { get; } = new();

    private string? _persistPath;

    private Loc() => Localizer.LanguageChanged += (_, _) => RaiseAll();

    /// <summary>Idioma vigente. Al cambiarlo se refresca la interfaz entera y se recuerda la elección.</summary>
    public AppLanguage Language
    {
        get => Localizer.Language;
        set
        {
            if (Localizer.Language == value) return;
            Localizer.Language = value; // eleva LanguageChanged → RaiseAll()
            Persist(value);
        }
    }

    /// <summary>Para enlazar las opciones del selector de idioma (RadioButton).</summary>
    public bool IsSpanish
    {
        get => Language == AppLanguage.Spanish;
        set { if (value) Language = AppLanguage.Spanish; }
    }

    public bool IsEnglish
    {
        get => Language == AppLanguage.English;
        set { if (value) Language = AppLanguage.English; }
    }

    /// <summary>Texto de una clave en el idioma vigente (lo consume el enlace del XAML).</summary>
    public string this[string key] => Localizer.T(key);

    /// <summary>Atajos para el código de la capa de interfaz (delegan en <see cref="Localizer"/>).</summary>
    public static string T(string key) => Localizer.T(key);
    public static string F(string key, params object?[] args) => Localizer.F(key, args);
    public static string Plural(string singularKey, string pluralKey, int count) => Localizer.Plural(singularKey, pluralKey, count);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseAll()
    {
        // Binding.IndexerName ("Item[]") le dice a WPF que TODOS los enlaces al indexador cambiaron.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpanish)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnglish)));
    }

    /// <summary>
    /// Fija el idioma inicial: la elección GUARDADA si existe y, si no, el de Windows. <paramref name="path"/>
    /// es el archivo donde se recuerda (se escribe al cambiar de idioma).
    /// </summary>
    public void Initialize(string path)
    {
        _persistPath = path;
        AppLanguage initial;
        try
        {
            initial = File.Exists(path)
                      && JsonSerializer.Deserialize<Preference>(File.ReadAllText(path)) is { } p
                      && System.Enum.TryParse<AppLanguage>(p.Language, ignoreCase: true, out var saved)
                ? saved
                : Localizer.FromSystem();
        }
        catch { initial = Localizer.FromSystem(); } // preferencia ilegible: manda el sistema y la app arranca igual
        Localizer.Language = initial;
    }

    private void Persist(AppLanguage language)
    {
        if (_persistPath is null) return; // sin Initialize: solo en memoria
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_persistPath)!);
            File.WriteAllText(_persistPath,
                JsonSerializer.Serialize(new Preference(language.ToString()), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (System.Exception ex)
        {
            // No poder recordarlo no puede impedir cambiarlo: se aplica igual y queda en el registro.
            Serilog.Log.Warning(ex, "No se pudo guardar la preferencia de idioma en «{Path}».", _persistPath);
        }
    }

    private sealed record Preference(string Language);
}
