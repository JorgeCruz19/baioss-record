using System.Text.Json;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Storage;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>
/// <see cref="IStorageSettingsStore"/> sobre un archivo JSON (p. ej. <c>data/storage-settings.json</c>). Al
/// arrancar CARGA el archivo si existe; si no (o si es ilegible), SIEMBRA con los valores por defecto/config y
/// lo escribe. <see cref="Save"/> sanea, actualiza en memoria, persiste de forma ATÓMICA (temporal + move) y
/// avisa. Seguro ante fallos de E/S: si no puede escribir, mantiene el valor en memoria y lo registra. (Fase 4c.)
/// </summary>
public sealed class JsonStorageSettingsStore : IStorageSettingsStore
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly ILogger<JsonStorageSettingsStore> _log;
    private readonly object _lock = new();
    private StorageSettings _current;

    public JsonStorageSettingsStore(string path, StorageSettings seed, ILogger<JsonStorageSettingsStore> log)
    {
        _path = path;
        _log = log;
        _current = Load(path, seed.Sanitized(), log);
    }

    public StorageSettings Current { get { lock (_lock) return _current.Clone(); } }

    public event EventHandler? Changed;

    public void Save(StorageSettings settings)
    {
        var clean = settings.Sanitized();
        lock (_lock)
        {
            _current = clean;
            try { Persist(_path, clean); }
            catch (Exception ex) { _log.LogError(ex, "No se pudieron guardar los ajustes de almacenamiento en «{Path}» (se conservan en memoria).", _path); }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        _log.LogInformation("Ajustes de almacenamiento actualizados (retención: {Ret}, emergencia ≥ {Emg}%, auto-limpieza: {Clean}, bloquear: {Block}).",
            clean.RetentionEnabled, clean.EmergencyPercent, clean.AutoCleanupOnEmergency, clean.StopNewRecordingsOnEmergency);
    }

    private static StorageSettings Load(string path, StorageSettings seed, ILogger log)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<StorageSettings>(File.ReadAllText(path));
                if (s is not null) return s.Sanitized();
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Ajustes de almacenamiento ilegibles en «{Path}»; se usan los valores por defecto/config.", path);
        }
        // Primera vez (o archivo ilegible): siembra desde la config y PERSISTE para que el archivo exista.
        try { Persist(path, seed); }
        catch (Exception ex) { log.LogWarning(ex, "No se pudo sembrar «{Path}» con los ajustes por defecto.", path); }
        return seed;
    }

    private static void Persist(string path, StorageSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, WriteOpts));
        File.Move(tmp, path, overwrite: true); // atómico: nunca deja un JSON a medias
    }
}
