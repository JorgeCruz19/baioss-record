using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Domain;
using Baioss.Record.Application.Storage;

namespace Baioss.Record.App.Storage;

/// <summary>
/// ViewModel de la ventana «Almacenamiento»: edita el <see cref="IStorageSettingsStore"/> (retención + alertas
/// por % + modo emergencia). Al guardar, el almacén SANEA los valores y los servicios de fondo los aplican en
/// su próximo ciclo (sin reiniciar). Tras guardar se RECARGAN los valores saneados para que el operador vea lo
/// que quedó realmente aplicado (p. ej. si un umbral se ajustó al orden coherente). (Gestión de almacenamiento — Fase 4c.)
/// </summary>
public sealed partial class StorageSettingsViewModel : ObservableObject
{
    private readonly IStorageSettingsStore _store;

    // --- Retención ---
    [ObservableProperty] private bool _retentionEnabled;
    [ObservableProperty] private int _retentionDays;
    [ObservableProperty] private double _minFreeGB;
    [ObservableProperty] private int _minFreePercent;
    [ObservableProperty] private int _intervalMinutes;
    [ObservableProperty] private bool _archiveInsteadOfDelete;
    [ObservableProperty] private string _archivePath = "";

    // --- Alertas por % y emergencia ---
    [ObservableProperty] private int _warnPercent;
    [ObservableProperty] private int _criticalPercent;
    [ObservableProperty] private int _emergencyPercent;
    [ObservableProperty] private bool _autoCleanupOnEmergency;
    [ObservableProperty] private bool _stopNewRecordingsOnEmergency;

    [ObservableProperty] private string _statusMessage = "";

    public StorageSettingsViewModel(IStorageSettingsStore store)
    {
        _store = store;
        Load(store.Current);
    }

    private void Load(StorageSettings s)
    {
        RetentionEnabled = s.RetentionEnabled;
        RetentionDays = s.RetentionDays;
        MinFreeGB = s.MinFreeGB;
        MinFreePercent = s.MinFreePercent;
        IntervalMinutes = s.IntervalMinutes;
        ArchiveInsteadOfDelete = s.Action == RetentionAction.Archive;
        ArchivePath = s.ArchivePath ?? "";
        WarnPercent = s.WarnPercent;
        CriticalPercent = s.CriticalPercent;
        EmergencyPercent = s.EmergencyPercent;
        AutoCleanupOnEmergency = s.AutoCleanupOnEmergency;
        StopNewRecordingsOnEmergency = s.StopNewRecordingsOnEmergency;
    }

    [RelayCommand]
    private void BrowseArchive()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta de archivado de grabaciones" };
        if (Directory.Exists(ArchivePath)) dlg.InitialDirectory = ArchivePath;
        if (dlg.ShowDialog() == true) ArchivePath = dlg.FolderName;
    }

    [RelayCommand]
    private void Save()
    {
        var s = new StorageSettings
        {
            RetentionEnabled = RetentionEnabled,
            RetentionDays = RetentionDays,
            MinFreeGB = MinFreeGB,
            MinFreePercent = MinFreePercent,
            IntervalMinutes = IntervalMinutes,
            Action = ArchiveInsteadOfDelete ? RetentionAction.Archive : RetentionAction.Delete,
            ArchivePath = string.IsNullOrWhiteSpace(ArchivePath) ? null : ArchivePath.Trim(),
            WarnPercent = WarnPercent,
            CriticalPercent = CriticalPercent,
            EmergencyPercent = EmergencyPercent,
            AutoCleanupOnEmergency = AutoCleanupOnEmergency,
            StopNewRecordingsOnEmergency = StopNewRecordingsOnEmergency,
        };
        _store.Save(s);
        Load(_store.Current); // refleja los valores SANEADOS realmente aplicados
        StatusMessage = "✔ Ajustes guardados y aplicados (los servicios los usarán en su próximo ciclo).";
    }
}
