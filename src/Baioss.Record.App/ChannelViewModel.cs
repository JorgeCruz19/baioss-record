using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Presets;
using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App;

/// <summary>
/// ViewModel de un canal (A/B). Enlaza el estado del <see cref="IChannelEngine"/> con la UI:
/// transporte, señal, telemetría y medidores VU. La codificación (formato, tamaño, códec,
/// bitrate, audio…) se elige aplicando un <see cref="EncodingPreset"/> desde el gestor de
/// presets; aquí solo se fija la CARPETA DE DESTINO y se muestra el resumen del perfil vigente.
/// </summary>
public sealed partial class ChannelViewModel : ObservableObject, IDisposable
{
    private readonly IChannelEngine _engine;
    private readonly IConfigurableRecording? _config;
    private readonly IPostRecordingRename? _renamer;
    private readonly Func<Guid, Task>? _skipScheduled;

    public ChannelViewModel(IChannelEngine engine, IChannelPreviewSource? preview = null, Func<Guid, Task>? skipScheduled = null)
    {
        _engine = engine;
        _config = engine as IConfigurableRecording;
        _renamer = engine as IPostRecordingRename;
        _skipScheduled = skipScheduled;
        IsConfigurable = _config is not null;
        Preview = preview;

        InitFromProfile();

        // Cronómetro de grabación a 1 Hz por reloj de pared (contador hh:mm:ss suave, sin saltos).
        _recTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recTimer.Tick += (_, _) => UpdateRecTimer();
        _recTimer.Start();

        _engine.StatusChanged += OnStatusChanged;
        if (Preview is not null) Preview.AudioPeaksUpdated += OnPreviewAudio;
        Sync(engine.Status);
    }

    /// <summary>Refresca el contador de grabación (hh:mm:ss) por reloj de pared. Lo llama el timer de 1 Hz y
    /// también <see cref="Sync"/> al cambiar de estado, para que el valor sea inmediato.</summary>
    private void UpdateRecTimer()
    {
        if (_recStartUtc is { } start)
        {
            var e = DateTimeOffset.UtcNow - start;
            if (e < TimeSpan.Zero) e = TimeSpan.Zero;
            Timecode = $"{(int)e.TotalHours:00}:{e.Minutes:00}:{e.Seconds:00}";
        }
        else Timecode = "00:00:00";
    }

    public IChannelPreviewSource? Preview { get; }
    public string Key => _engine.Status.Key;
    public Guid ChannelId => _engine.ChannelId;

    /// <summary>Nombre descriptivo para la cabecera del panel, derivado del Key (A/B).</summary>
    public string DisplayName => Key switch
    {
        "A" => "Canal Principal",
        "B" => "Canal Secundario",
        _ => $"Canal {Key}",
    };

    private double _peakHoldL = -60, _peakHoldR = -60;

    // Cronómetro de grabación: cuenta hh:mm:ss por RELOJ DE PARED a 1 Hz, independiente del out_time de FFmpeg
    // (que llega irregular por -progress y hacía saltar el contador 2 s). Se ancla al entrar en REC.
    private readonly DispatcherTimer _recTimer;
    private DateTimeOffset? _recStartUtc;

    [ObservableProperty] private RecordingState _recordingState;
    [ObservableProperty] private SignalState _signalState;
    [ObservableProperty] private string _signalText = "SIN SEÑAL";
    [ObservableProperty] private string _formatText = "—";
    /// <summary>Nombre de la ENTRADA (fuente) asignada al canal, para mostrarla en el preview.</summary>
    [ObservableProperty] private string _inputText = "—";
    [ObservableProperty] private string _timecode = "00:00:00";
    [ObservableProperty] private long _frameCount;
    [ObservableProperty] private double _outputFps;
    [ObservableProperty] private long _droppedFrames;
    [ObservableProperty] private string _bitrateText = "—";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigure))]
    [NotifyPropertyChangedFor(nameof(NotRecording))]
    private bool _isRecording;

    /// <summary>Inverso de <see cref="IsRecording"/> (preview muestra el formato en reposo, «frame N» al grabar).</summary>
    public bool NotRecording => !IsRecording;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private string _profileText = "—";

    [ObservableProperty] private double _leftLevel;
    [ObservableProperty] private double _rightLevel;
    [ObservableProperty] private double _leftPeak;
    [ObservableProperty] private double _rightPeak;
    [ObservableProperty] private string _leftPeakDb = "-∞";
    [ObservableProperty] private string _rightPeakDb = "-∞";
    [ObservableProperty] private bool _clipping;

    /// <summary>Resumen del audio de entrada para la franja bajo el preview (p. ej. «2 canales · PCM»).</summary>
    [ObservableProperty] private string _audioFormatText = "—";

    // Alarmas operativas (negro/congelado/silencio/slate/disco) y estado del almacenamiento.
    [ObservableProperty] private bool _hasAlarms;
    [ObservableProperty] private string _alarmsText = "";
    [ObservableProperty] private bool _isSlate;
    [ObservableProperty] private string _diskText = "—";
    [ObservableProperty] private bool _diskWarning;
    [ObservableProperty] private bool _diskCritical;

    // Resiliencia de señal POR CANAL: carta de ajuste al perder señal. (La segmentación del vídeo se
    // configura por grabación programada, en «🕒 Programación», no aquí.)
    [ObservableProperty] private bool _slateOnSignalLoss;

    partial void OnSlateOnSignalLossChanged(bool value)
    {
        if (_config is not null && !IsRecording) _config.Profile.SlateOnSignalLoss = value;
    }

    // ---------------------------------------------------------------------
    //  Destino y perfil activo
    // ---------------------------------------------------------------------

    public bool IsConfigurable { get; }
    public bool CanConfigure => IsConfigurable && !IsRecording;

    /// <summary>Carpeta de destino elegida por el operador (se edita con el botón Examinar).</summary>
    [ObservableProperty] private string _outputDirectory = "";

    partial void OnOutputDirectoryChanged(string value)
    {
        if (_config is not null && !string.IsNullOrWhiteSpace(value)) _config.OutputDirectory = value;
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta de destino de las grabaciones" };
        if (Directory.Exists(OutputDirectory)) dialog.InitialDirectory = OutputDirectory;
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    private void InitFromProfile()
    {
        if (_config is null) return;
        OutputDirectory = _config.OutputDirectory;
        SlateOnSignalLoss = _config.Profile.SlateOnSignalLoss;
        RefreshProfileText();
    }

    /// <summary>
    /// Aplica un preset de encoding al canal: fija el perfil completo en el motor (conservando la
    /// identidad persistida del canal) y refresca el resumen. La carpeta de destino no la toca un
    /// preset; es una propiedad operativa por canal.
    /// </summary>
    public void ApplyPreset(EncodingPreset preset)
    {
        if (_config is null || IsRecording) return;
        var current = _config.Profile;
        _config.Profile = preset.ToProfile(current.Id, current.Name); // conserva Id/Name persistidos
        // El slate es operativo por canal, no del preset: se reaplica al nuevo perfil.
        _config.Profile.SlateOnSignalLoss = SlateOnSignalLoss;
        RefreshProfileText();
    }

    /// <summary>Resumen legible del perfil vigente (lo que se grabará): códec · tasa · tamaño · contenedor.</summary>
    private void RefreshProfileText()
    {
        if (_config is null) { ProfileText = "—"; return; }
        var p = _config.Profile;
        if (p.AudioOnly)
        {
            ProfileText = $"Solo audio · {p.AudioCodec} · {p.Container}";
            return;
        }
        string rate = p.RateControl == RateControlMode.ConstantQuality
            ? $"CRF {p.Quality}"
            : p.VideoBitrate.ToString();
        string res = p.TargetResolution?.ToString() ?? "nativa";
        ProfileText = $"{p.VideoCodec} · {rate} · {res} · {p.Container}";
    }

    // ---------------------------------------------------------------------
    //  Transporte
    // ---------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        // Grabación MANUAL: arranca YA con un nombre temporal ({canal}_{fecha_hora}); el nombre real se
        // pide al DETENER y se renombra el archivo entonces.
        try { await _engine.StartRecordingAsync(Guid.Empty, Environment.UserName); }
        catch (Exception ex)
        {
            // Pre-vuelo fallido (perfil inválido, carpeta de destino no escribible, …): avisa al operador
            // sin tumbar la app ni dejar una sesión a medias (la validación corre antes de crear nada).
            System.Windows.MessageBox.Show(ex.Message, "No se pudo iniciar la grabación",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
    private bool CanStart() => !IsRecording && IsLocked;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        // ¿Era manual? Las programadas ya tienen nombre y las gestiona el scheduler. Se decide ANTES de
        // parar, porque al detener pueden cambiar los indicadores del canal.
        bool manual = !IsScheduledRecording && _renamer is not null;
        await _engine.StopRecordingAsync();
        if (!manual) return;

        // Pide el nombre al terminar y renombra el archivo recién grabado (dedupe « 1», « 2»… si choca).
        // Si el operador cancela, la grabación queda con el nombre temporal (no se pierde).
        var dialog = new RecordingNameWindow(Key, $"Grabación {DateTime.Now:dd-MM-yyyy}")
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        if (dialog.ShowDialog() == true)
            await _renamer!.RenameLastRecordingAsync(dialog.RecordingName);
    }
    private bool CanStop() => IsRecording;

    /// <summary>True cuando una grabación PROGRAMADA está corriendo en este canal (lo fija el shell desde el scheduler).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStopButton))]
    private bool _isScheduledRecording;

    /// <summary>El botón «Detener» SOLO aplica a grabaciones manuales; en las programadas se usa «saltar».</summary>
    public bool ShowStopButton => !IsScheduledRecording;

    /// <summary>Salta la grabación programada en curso (solo esta ocurrencia; las siguientes siguen).</summary>
    [RelayCommand]
    private Task SkipScheduledRecording() => _skipScheduled?.Invoke(ChannelId) ?? Task.CompletedTask;

    // ---------------------------------------------------------------------
    //  Tareas programadas de HOY para este canal (tabla bajo el preview)
    // ---------------------------------------------------------------------

    /// <summary>Grabaciones programadas de hoy en este canal (lista completa para «Mostrar programación»). La rellena el shell.</summary>
    public ObservableCollection<TodayTaskRow> TodayTasks { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoTodayTasks))]
    private bool _hasTodayTasks;

    /// <summary>Inverso de <see cref="HasTodayTasks"/> (texto «hoy no hay» en la ventana de programación).</summary>
    public bool NoTodayTasks => !HasTodayTasks;

    /// <summary>Texto cuando hoy no hay grabaciones (p. ej. «Hoy no hay. Próxima: lun 22/06 · 20:00 · …»
    /// o «Sin grabaciones programadas hoy»). Se muestra en la ventana de programación.</summary>
    [ObservableProperty] private string _todayEmptyText = "";

    /// <summary>La grabación programada EN CURSO ahora mismo en este canal: es la ÚNICA que se muestra en el
    /// panel (alto fijo). null si ninguna corre; el resto se consultan en «Mostrar programación».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveTask))]
    [NotifyPropertyChangedFor(nameof(NoActiveTask))]
    private TodayTaskRow? _activeTask;

    public bool HasActiveTask => ActiveTask is not null;
    public bool NoActiveTask => ActiveTask is null;

    /// <summary>El shell sustituye la lista de tareas de hoy (refresco periódico / al cambiar el estado activo).</summary>
    public void SetTodayTasks(IReadOnlyList<TodayTaskRow> rows, string emptyText)
    {
        TodayTasks.Clear();
        foreach (var r in rows) TodayTasks.Add(r);
        HasTodayTasks = TodayTasks.Count > 0;
        TodayEmptyText = emptyText;
        ActiveTask = rows.FirstOrDefault(r => r.IsRunning); // en el panel solo se muestra la en curso
    }

    /// <summary>Abre una ventana con toda la programación de HOY del canal (Entrada · Salida · Título · Segmento).</summary>
    [RelayCommand]
    private void ShowSchedule()
    {
        var window = new ChannelScheduleWindow(Key, TodayTasks.ToList())
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.Show();
    }

    private void OnStatusChanged(object? sender, ChannelStatus status)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Sync(status));

    private void OnPreviewAudio(object? sender, (double Left, double Right) lr)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ApplyAudio(lr.Left, lr.Right));

    private void ApplyAudio(double l, double r)
    {
        _peakHoldL = Math.Max(l, _peakHoldL - 1.2); // peak-hold con decaimiento
        _peakHoldR = Math.Max(r, _peakHoldR - 1.2);
        LeftLevel = Norm(l); LeftPeak = Norm(_peakHoldL);
        RightLevel = Norm(r); RightPeak = Norm(_peakHoldR);
        LeftPeakDb = Fmt(_peakHoldL); RightPeakDb = Fmt(_peakHoldR);
        Clipping = _peakHoldL > -1 || _peakHoldR > -1;
    }

    private void Sync(ChannelStatus status)
    {
        RecordingState = status.RecordingState;
        SignalState = status.Signal.State;
        IsLocked = status.Signal.State == SignalState.Locked;
        SignalText = status.Signal.State switch
        {
            SignalState.Locked => "SEÑAL OK",
            SignalState.Unstable => "INESTABLE",
            _ => "SIN SEÑAL"
        };
        // Preferimos la etiqueta legible del modo (DeckLink: "1920×1080 · 59.94i"); si no, resolución·fps.
        FormatText = status.Signal.FormatLabel
                     ?? (status.Signal is { Resolution: { } r, FrameRate: { } f } ? $"{r} · {f}" : "—");

        // Nombre de la ENTRADA activa (fuente asignada): se muestra en el preview.
        InputText = string.IsNullOrWhiteSpace(status.InputName) ? "—" : status.InputName;

        // Resumen de audio bajo el preview: «N canales · PCM» con señal y audio; si no, «Sin audio».
        AudioFormatText = status.Signal is { HasAudio: true, AudioLayout: { } layout }
            ? $"{ChannelCountText(layout)} · PCM"
            : "Sin audio";

        IsRecording = status.RecordingState is RecordingState.Recording or RecordingState.Paused;

        // FPS de salida es del preview en vivo (el motor unificado siempre tiene un FFmpeg de preview
        // activo): legítimo mostrarlo en cuanto hay señal.
        OutputFps = status.Stats.OutputFps;

        // Timer (timecode), bitrate, cuadros y dropped pertenecen a la GRABACIÓN. Como ese mismo
        // proceso de preview corre con -progress, avanzarían en reposo aunque no se grabe; se muestran
        // solo durante REC/Pausa y en reposo se dejan en valores idle (el timer arranca de 0 al grabar).
        if (IsRecording)
        {
            _recStartUtc ??= DateTimeOffset.UtcNow; // ancla el cronómetro al primer instante en REC
            FrameCount = status.Stats.FrameCount;
            DroppedFrames = status.Stats.DroppedFrames;
            BitrateText = status.Stats.Bitrate.BitsPerSecond > 0 ? status.Stats.Bitrate.ToString() : "—";
        }
        else
        {
            _recStartUtc = null;
            FrameCount = 0;
            DroppedFrames = 0;
            BitrateText = "—";
        }
        UpdateRecTimer(); // refleja de inmediato el arranque/parada del contador (sin esperar al próximo tick)

        // Alarmas operativas: negro/congelado/silencio/slate/disco. Se muestran como una franja sobre el
        // preview; el slate (sin señal pero grabando barras) se resalta aparte.
        var alarms = status.Alarms ?? Array.Empty<ChannelAlarm>();
        HasAlarms = alarms.Count > 0;
        AlarmsText = HasAlarms ? string.Join("   •   ", alarms.Select(a => a.Message)) : "";
        IsSlate = alarms.Any(a => a.Type == AlarmType.Slate);

        // Disco: tiempo restante estimado durante la grabación; colores por severidad.
        if (IsRecording && status.Storage is { } st)
        {
            DiskText = FormatDisk(st);
            DiskCritical = alarms.Any(a => a.Type == AlarmType.DiskCritical);
            DiskWarning = alarms.Any(a => a.Type == AlarmType.DiskLow);
        }
        else { DiskText = "—"; DiskWarning = false; DiskCritical = false; }

        // Medidores: en modo real los conduce el audio del preview (ver OnPreviewAudio); aquí solo
        // se actualizan desde el status cuando NO hay preview (canal simulado).
        if (Preview is null)
        {
            var audio = status.Audio;
            if (audio is { Count: >= 2 })
            {
                LeftLevel = Norm(audio[0].RmsDb); LeftPeak = Norm(audio[0].PeakDb);
                RightLevel = Norm(audio[1].RmsDb); RightPeak = Norm(audio[1].PeakDb);
                LeftPeakDb = Fmt(audio[0].PeakDb); RightPeakDb = Fmt(audio[1].PeakDb);
                Clipping = audio[0].Clipping || audio[1].Clipping;
            }
            else
            {
                LeftLevel = RightLevel = LeftPeak = RightPeak = 0;
                LeftPeakDb = RightPeakDb = "-∞";
                Clipping = false;
            }
        }

        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private static double Norm(double db) => Math.Clamp((db + 60) / 60.0, 0, 1);
    private static string Fmt(double db) => db <= -60 ? "-∞" : $"{db:0.0}";

    /// <summary>Nº de canales de audio legible según el layout (para «N canales · PCM»).</summary>
    private static string ChannelCountText(AudioLayout layout) => layout switch
    {
        AudioLayout.Mono => "1 canal",
        AudioLayout.Stereo => "2 canales",
        AudioLayout.Surround51 => "6 canales",
        AudioLayout.Surround71 => "8 canales",
        _ => "—",
    };

    /// <summary>"820 GB libres · ~1 h 12 min" — espacio libre y tiempo de grabación restante estimado.</summary>
    private static string FormatDisk(StorageInfo s)
    {
        string free = s.FreeGiB >= 1 ? $"{s.FreeGiB:0.0} GB" : $"{s.FreeBytes / 1_048_576d:0} MB";
        if (s.EstimatedRemaining is { } r)
        {
            string t = r.TotalHours >= 1 ? $"{(int)r.TotalHours} h {r.Minutes:00} min" : $"{r.Minutes} min";
            return $"{free} · ~{t}";
        }
        return $"{free} libres";
    }

    public void Dispose()
    {
        _recTimer.Stop();
        _engine.StatusChanged -= OnStatusChanged;
        if (Preview is not null) Preview.AudioPeaksUpdated -= OnPreviewAudio;
    }
}
