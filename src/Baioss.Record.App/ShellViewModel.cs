using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Presets;
using Baioss.Record.Application.Scheduling;
using Baioss.Record.Application.Storage;
using Baioss.Record.App.Inputs;
using Baioss.Record.App.Preview;
using Baioss.Record.App.Presets;
using Baioss.Record.App.Recordings;
using Baioss.Record.App.Scheduling;
using Baioss.Record.App.Storage;

namespace Baioss.Record.App;

/// <summary>
/// ViewModel raíz del shell. Expone un <see cref="ChannelViewModel"/> por canal, abre el gestor de
/// presets de encoding y el de entradas (asignar tarjeta/cámara a un canal). Cuando el
/// <see cref="ChannelHost"/> reconstruye un canal en caliente, reemplaza su ViewModel para que la
/// vista re-enlace el preview de la nueva entrada.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ChannelHost _host;
    private readonly PreviewCatalog _previews;
    private readonly IPresetStore _presetStore;
    private readonly IDeviceEnumerator _devices;
    private readonly ISchedulerService _scheduler;
    private readonly IClock _clock;
    private readonly IRecordingSessionRepository _sessions;
    private readonly IStorageStatusProvider _storageStatus;
    private readonly IStorageSettingsStore _storageSettings;

    public ObservableCollection<ChannelViewModel> Channels { get; }

    /// <summary>Subtítulo de la barra superior, según el nº real de canales creados (1 canal / N canales).</summary>
    public string ChannelsSubtitle => $"· grabación broadcast de {Channels.Count} canal{(Channels.Count == 1 ? "" : "es")}";

    public ShellViewModel(ChannelHost host, PreviewCatalog previews, IPresetStore presetStore,
        IDeviceEnumerator devices, ISchedulerService scheduler, IClock clock, IRecordingSessionRepository sessions,
        IStorageStatusProvider storageStatus, IStorageSettingsStore storageSettings)
    {
        _host = host;
        _previews = previews;
        _presetStore = presetStore;
        _devices = devices;
        _scheduler = scheduler;
        _clock = clock;
        _sessions = sessions;
        _storageStatus = storageStatus;
        _storageSettings = storageSettings;

        Channels = new ObservableCollection<ChannelViewModel>(
            host.Channels
                .OrderBy(e => e.Status.Key, StringComparer.Ordinal)
                .Select(e => new ChannelViewModel(e, previews.For(e.ChannelId), SkipScheduledAsync)));

        _host.ChannelRebound += OnChannelRebound;
        _scheduler.ActiveChanged += OnScheduledActiveChanged;
        RefreshScheduledActive();
        RefreshTodayTasks();

        // Indicador GLOBAL de almacenamiento (Fase 4b): estado del volumen de grabación, visible TAMBIÉN en reposo
        // (la guarda por-canal solo informa grabando). El coordinador lo publica en un hilo de fondo → marshalar.
        ApplyStorage(_storageStatus.Current);
        _storageStatus.Changed += OnStorageChanged;

        // Refresco periódico de la tabla «HOY» (hora de las filas, altas/bajas de tareas, cambio de día).
        _todayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _todayTimer.Tick += (_, _) => RefreshTodayTasks();
        _todayTimer.Start();
    }

    private readonly DispatcherTimer _todayTimer;
    /// <summary>Guardia de reentrancia de <see cref="RefreshTodayTasks"/>: coalesce solapamientos sin bloquear. (#50.)</summary>
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    private Task SkipScheduledAsync(Guid channelId) => _scheduler.SkipCurrentAsync(channelId);

    private void OnScheduledActiveChanged(object? sender, EventArgs e)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { RefreshScheduledActive(); RefreshTodayTasks(); });

    /// <summary>Reparte entre los canales sus grabaciones programadas de HOY (resaltando la en curso).</summary>
    private async void RefreshTodayTasks()
    {
        // El DispatcherTimer (cada 30 s) y los eventos del scheduler pueden disparar este refresco solapado.
        // Si ya hay uno en curso, se descarta este (el siguiente intervalo refrescará): evita que dos pasadas
        // pisen la lista TodayTasks de cada canal (Clear + Add) a la vez. WaitAsync(0) no bloquea. (Auditoría #50.)
        if (!await _refreshing.WaitAsync(0)) return;
        try
        {
            var now = _clock.UtcNow;
            var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
            var jobs = await _scheduler.GetAllAsync();
            var active = _scheduler.ActiveScheduledChannels;
            var es = new CultureInfo("es-ES");
            foreach (var vm in Channels)
            {
                var rows = new List<(DateTimeOffset Slot, TodayTaskRow Row)>();
                // …y de paso la PRÓXIMA ocurrencia futura del canal (para el texto «hoy no hay»).
                ScheduledJob? nextJob = null; DateTimeOffset? nextSlot = null;

                foreach (var j in jobs)
                {
                    if (!j.Enabled || j.ChannelId != vm.ChannelId) continue;

                    if (ScheduleEvaluator.NextSlotAfter(j, now) is { } ns && (nextSlot is null || ns < nextSlot))
                    { nextSlot = ns; nextJob = j; }

                    if (ScheduleEvaluator.OccurrenceOnDate(j, today, requireAfterAnchor: false) is not { } slot) continue;
                    var end = j.Duration is { } d ? slot + d : (DateTimeOffset?)null;
                    bool running = active.Contains(vm.ChannelId) && now >= slot && (end is null || now < end);
                    rows.Add((slot, new TodayTaskRow
                    {
                        EntradaText = slot.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
                        SalidaText = end is { } e ? e.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "—",
                        Title = j.Title,
                        SegmentText = j.SegmentMinutes is { } m && m > 0 ? $"{m} min" : "",
                        IsRunning = running,
                    }));
                }

                // La tabla SIEMPRE se muestra (alto fijo, uniforme entre canales). Si hoy no hay nada,
                // se indica la próxima ocurrencia como pista, o «sin grabaciones» si tampoco hay futura.
                string emptyText = rows.Count > 0
                    ? ""
                    : nextSlot is { } np
                        ? $"Hoy no hay. Próxima: {np.ToLocalTime().ToString("ddd dd/MM · HH:mm", es)} · {nextJob!.Title}"
                        : "Sin grabaciones programadas hoy";
                vm.SetTodayTasks(rows.OrderBy(r => r.Slot).Select(r => r.Row).ToList(), emptyText);
            }
        }
        catch { /* refresco best-effort */ }
        finally { _refreshing.Release(); }
    }

    /// <summary>Marca en cada canal si tiene una grabación PROGRAMADA en curso (muestra el botón de saltar).</summary>
    private void RefreshScheduledActive()
    {
        var active = _scheduler.ActiveScheduledChannels;
        foreach (var vm in Channels) vm.IsScheduledRecording = active.Contains(vm.ChannelId);
    }

    // ---------------------------------------------------------------------
    //  Indicador global de almacenamiento (Fase 4b)
    // ---------------------------------------------------------------------

    /// <summary>Texto del indicador de almacenamiento de la barra superior («52 GB libres · 88%»).</summary>
    [ObservableProperty] private string _storageText = "—";
    /// <summary>Salud del volumen (Ok/Aviso/Crítico/Emergencia): la usa la barra para el color.</summary>
    [ObservableProperty] private StorageHealth _storageHealth = StorageHealth.Unknown;
    /// <summary>Muestra la franja roja de alerta (crítico/emergencia).</summary>
    [ObservableProperty] private bool _showStorageBanner;
    [ObservableProperty] private string _storageBannerText = "";
    [ObservableProperty] private string _storageTooltip = "Almacenamiento del volumen de grabación";

    /// <summary>El coordinador publica el estado en un hilo de fondo (sondeo cada 15 s): marshalar al hilo de UI.</summary>
    private void OnStorageChanged(object? sender, StorageSnapshot s)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ApplyStorage(s));

    private void ApplyStorage(StorageSnapshot s)
    {
        if (s.Health == StorageHealth.Unknown || s.TotalBytes <= 0)
        {
            StorageText = "—";
            StorageHealth = StorageHealth.Unknown;
            ShowStorageBanner = false;
            StorageBannerText = "";
            StorageTooltip = "Almacenamiento del volumen de grabación (midiendo…)";
            return;
        }
        StorageHealth = s.Health;
        StorageText = $"{StorageFormat.Bytes(s.FreeBytes)} libres · {s.UsedPercent:0}%";
        StorageTooltip = $"Volumen de grabación: {StorageFormat.Bytes(s.FreeBytes)} libres de {StorageFormat.Bytes(s.TotalBytes)} ({s.UsedPercent:0.#}% ocupado)";
        ShowStorageBanner = s.Health is StorageHealth.Critical or StorageHealth.Emergency;
        StorageBannerText = s.Health switch
        {
            StorageHealth.Emergency => "⚠  ALMACENAMIENTO EN EMERGENCIA  —  libera espacio para seguir grabando con normalidad",
            StorageHealth.Critical => "⚠  Almacenamiento crítico  —  queda muy poco espacio en el volumen de grabación",
            _ => "",
        };
    }

    [RelayCommand]
    private void OpenPresets()
    {
        // Snapshot para el desplegable (estable), + resolver para aplicar al VM VIGENTE por Id (por si hay un
        // rebind con la ventana abierta → el VM seleccionado quedaría dispuesto). (Auditoría N10.)
        var viewModel = new PresetManagerViewModel(_presetStore, Channels.ToList(), ResolveChannel);
        var window = new PresetManagerWindow
        {
            DataContext = viewModel,
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.Closed += (_, _) => viewModel.Dispose(); // desuscribe del store al cerrar (no fugar el VM). (#24)
        window.Show();
    }

    [RelayCommand]
    private void OpenInputs()
    {
        var viewModel = new InputsManagerViewModel(_devices, Channels.ToList(), _host.CanRebind, _host.DemoClipPath, RebindAsync);
        var window = new InputsManagerWindow
        {
            DataContext = viewModel,
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.Show();
    }

    [RelayCommand]
    private void OpenSchedule()
    {
        var viewModel = new ScheduleViewModel(_scheduler, Channels.ToList(), _clock);
        var window = new ScheduleWindow
        {
            DataContext = viewModel,
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.Closed += (_, _) => RefreshTodayTasks(); // refleja altas/bajas al cerrar
        window.Show();
    }

    [RelayCommand]
    private void OpenGeneralSettings()
    {
        // Colección VIVA (no snapshot): si un rebind reemplaza un ChannelViewModel, el ItemsControl re-enlaza sus
        // controles (carpeta / slate) al VM nuevo, en vez de editar el motor ya dispuesto. (Auditoría N10.)
        var viewModel = new GeneralSettingsViewModel(Channels);
        var window = new GeneralSettingsWindow
        {
            DataContext = viewModel,
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.Show();
    }

    [RelayCommand]
    private void OpenRecordings()
    {
        // Mapa estable ChannelId→Key (la clave A/B/C/D no cambia con un rebind): para etiquetar el canal de cada
        // grabación y poblar el filtro por canal. Se toma un snapshot al abrir (los canales no se crean/destruyen
        // en caliente, solo se reconstruyen conservando su Id/Key).
        var channelKeys = Channels.ToDictionary(c => c.ChannelId, c => c.Key);
        var viewModel = new RecordingsViewModel(_sessions, _clock, channelKeys);
        var window = new RecordingsWindow
        {
            DataContext = viewModel,
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.Show();
    }

    [RelayCommand]
    private void OpenStorageSettings()
    {
        var window = new StorageSettingsWindow
        {
            DataContext = new StorageSettingsViewModel(_storageSettings),
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.Show();
    }

    /// <summary>ViewModel VIGENTE de un canal por su Id (contra la colección viva): para que ventanas abiertas
    /// resuelvan el motor vivo tras un rebind, no el VM dispuesto que capturaron. (Auditoría N10.)</summary>
    private ChannelViewModel? ResolveChannel(Guid channelId) => Channels.FirstOrDefault(c => c.ChannelId == channelId);

    private Task RebindAsync(Guid channelId, InputSource def) => _host.RebindAsync(channelId, def);

    /// <summary>Tras reconstruir un canal, reemplaza su ViewModel (misma posición) para re-enlazar el preview.</summary>
    private void OnChannelRebound(Guid channelId)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            for (int i = 0; i < Channels.Count; i++)
            {
                if (Channels[i].ChannelId != channelId) continue;
                Channels[i].Dispose();
                Channels[i] = new ChannelViewModel(_host.Get(channelId), _previews.For(channelId), SkipScheduledAsync);
                break;
            }
            RefreshScheduledActive(); // el VM nuevo refleja si hay grabación programada activa
            RefreshTodayTasks();      // …y su tabla de tareas de hoy
        });
    }
}
