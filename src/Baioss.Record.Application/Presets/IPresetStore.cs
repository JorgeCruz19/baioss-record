namespace Baioss.Record.Application.Presets;

/// <summary>
/// Almacén de presets: combina los presets de fábrica (built-in, solo lectura) con los
/// personalizados del usuario (persistidos en JSON). Soporta CRUD de los custom, favoritos
/// (de cualquiera) e import/export en JSON.
/// </summary>
public interface IPresetStore
{
    /// <summary>Built-in + custom, listos para mostrar/filtrar en la UI.</summary>
    IReadOnlyList<EncodingPreset> GetAll();

    /// <summary>Crea o actualiza un preset personalizado.</summary>
    void Save(EncodingPreset preset);

    /// <summary>Elimina un preset personalizado (los built-in no se pueden borrar).</summary>
    void Delete(Guid id);

    /// <summary>Duplica cualquier preset como uno personalizado editable y lo devuelve.</summary>
    EncodingPreset Duplicate(Guid id);

    /// <summary>Marca/desmarca favorito (se permite también en built-in).</summary>
    void SetFavorite(Guid id, bool favorite);

    /// <summary>Exporta los presets indicados (o todos si está vacío) a JSON.</summary>
    string ExportJson(IReadOnlyCollection<Guid> ids);

    /// <summary>Importa presets desde JSON como personalizados; devuelve los añadidos.</summary>
    IReadOnlyList<EncodingPreset> ImportJson(string json);

    /// <summary>Se dispara cuando cambia el conjunto (alta/baja/edición/favorito/import).</summary>
    event EventHandler? Changed;
}
