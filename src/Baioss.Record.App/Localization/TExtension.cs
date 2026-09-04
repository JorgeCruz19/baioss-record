using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace Baioss.Record.App.Localization;

/// <summary>
/// Extensión de marcado para traducir en el XAML: <c>Text="{loc:T Main_Settings}"</c>.
///
/// <para>Devuelve un ENLACE al indexador de <see cref="Loc"/>, no el texto suelto. Es la diferencia entre que
/// el idioma se pueda cambiar en caliente o haya que reiniciar: al cambiarlo, <see cref="Loc"/> notifica el
/// indexador y WPF vuelve a pedir el valor de todos estos enlaces.</para>
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    /// <summary>Clave de la cadena (ver <see cref="Strings"/>).</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
}
