namespace Baioss.Record.App.Recording;

/// <summary>Opción seleccionable en la UI: una etiqueta legible y el valor de dominio asociado.</summary>
public sealed record NamedOption<T>(string Label, T Value)
{
    public override string ToString() => Label; // lo que muestra el ComboBox
}
