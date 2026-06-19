using System.Text.Json;
using System.Text.Json.Serialization;
using Baioss.Record.Application.Presets;

namespace Baioss.Record.Infrastructure.Presets;

/// <summary>
/// Almacén de presets respaldado por un archivo JSON. Combina los presets de fábrica
/// (<see cref="PresetCatalog"/>) con los personalizados del usuario; persiste estos últimos
/// y los favoritos marcados sobre built-ins. Es la fuente de import/export.
/// </summary>
public sealed class JsonPresetStore : IPresetStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<EncodingPreset> _builtIns;
    private StoreFile _file;

    public JsonPresetStore(string path)
    {
        _path = path;
        _builtIns = PresetCatalog.CreateBuiltIns().ToList();
        _file = Load(path);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<EncodingPreset> GetAll()
    {
        lock (_gate)
        {
            var favorites = _file.FavoriteBuiltIns.ToHashSet();
            foreach (var b in _builtIns) b.IsFavorite = favorites.Contains(b.Id);
            return _builtIns.Concat(_file.Custom).ToList();
        }
    }

    public void Save(EncodingPreset preset)
    {
        lock (_gate)
        {
            preset.IsBuiltIn = false;
            var i = _file.Custom.FindIndex(p => p.Id == preset.Id);
            if (i >= 0) _file.Custom[i] = preset; else _file.Custom.Add(preset);
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            _file.Custom.RemoveAll(p => p.Id == id); // los built-in no se borran
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public EncodingPreset Duplicate(Guid id)
    {
        EncodingPreset copy;
        lock (_gate)
        {
            var source = _builtIns.Concat(_file.Custom).FirstOrDefault(p => p.Id == id)
                ?? throw new KeyNotFoundException($"Preset {id} no encontrado.");
            copy = source.DeepCopy();
            copy.Name = $"{source.Name} (copia)";
            _file.Custom.Add(copy);
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return copy;
    }

    public void SetFavorite(Guid id, bool favorite)
    {
        lock (_gate)
        {
            if (_builtIns.Any(b => b.Id == id))
            {
                _file.FavoriteBuiltIns.Remove(id);
                if (favorite) _file.FavoriteBuiltIns.Add(id);
            }
            else
            {
                var custom = _file.Custom.FirstOrDefault(p => p.Id == id);
                if (custom is not null) custom.IsFavorite = favorite;
            }
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string ExportJson(IReadOnlyCollection<Guid> ids)
    {
        var all = GetAll();
        var selection = ids.Count == 0 ? all : all.Where(p => ids.Contains(p.Id)).ToList();
        return JsonSerializer.Serialize(selection, Json);
    }

    public IReadOnlyList<EncodingPreset> ImportJson(string json)
    {
        var items = JsonSerializer.Deserialize<List<EncodingPreset>>(json, Json) ?? new();
        lock (_gate)
        {
            foreach (var item in items)
            {
                item.Id = Guid.NewGuid();  // nueva identidad → evita colisión con existentes
                item.IsBuiltIn = false;
                _file.Custom.Add(item);
            }
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return items;
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_file, Json));
    }

    private static StoreFile Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(path), Json) ?? new StoreFile()
                : new StoreFile();
        }
        catch
        {
            return new StoreFile(); // JSON corrupto/incompatible → arranca limpio
        }
    }

    private sealed class StoreFile
    {
        public List<EncodingPreset> Custom { get; set; } = new();
        public List<Guid> FavoriteBuiltIns { get; set; } = new();
    }
}
