using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Application.Clips;
using Baioss.Record.Application.Localization;
using Baioss.Record.Application.Storage;
using Baioss.Record.App.Localization;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Infrastructure.Review;

namespace Baioss.Record.App.Review;

/// <summary>Opción del codificador del modo exacto. ToString = Label (el ComboBox oscuro muestra el elemento cerrado por
/// ToString).</summary>
public sealed record EncoderOption(string Label, ClipVideoEncoder Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// ViewModel de la ventana «Revisar»: la grabación terminada en un reproductor (búsqueda exacta, cuadro a cuadro, reproducción
/// con sonido), las marcas de entrada y salida con la duración del clip, y la exportación en rápido o exacto
/// (<see cref="IClipExportService"/>). La SALIDA se marca en el último cuadro que entra (como en los editores): el clip va de la
/// entrada a la salida, las dos incluidas.
/// </summary>
public sealed partial class ReviewViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>Lo que se deja el aviso de «la CPU no da abasto» después del último salto del vídeo.</summary>
    private static readonly TimeSpan BehindNoteTime = TimeSpan.FromSeconds(5);

    private readonly IClipExportService _clips;
    private readonly RecordingSession _session;
    private readonly Func<RecordingTimeline, ReviewPlayback?> _playerFactory;
    private readonly object _frameLock = new();
    private RecordingTimeline? _timeline;
    private ReviewPlayer? _player;
    private ReviewSound _sound;
    private string? _soundOutput;
    /// <summary>Cuándo saltó el vídeo por última vez para seguir al audio (null: no salta).</summary>
    private DateTime? _fellBehindAt;
    private byte[] _back = Array.Empty<byte>();
    private long _backFrame = -1;
    private bool _renderQueued;
    private bool _positionFromPlayer;
    /// <summary>El cuadro que pidió el operador (barra, botones, teclas) y aún no ha llegado; −1 si no hay ninguno pendiente.</summary>
    private long _requested = -1;
    private CancellationTokenSource? _export;

    /// <summary><paramref name="playerFactory"/> crea el reproductor de la línea de tiempo y dice por dónde suena (null = sin imagen:
    /// en las pruebas).</summary>
    public ReviewViewModel(IClipExportService clips, RecordingSession session, string channelKey,
        Func<RecordingTimeline, ReviewPlayback?> playerFactory)
    {
        _clips = clips;
        _session = session;
        _playerFactory = playerFactory;
        ChannelKey = channelKey;
        var first = session.Segments.OrderBy(s => s.Index).Select(s => s.FilePath).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        RecordingName = first is null ? "—" : Path.GetFileName(first);
        var local = session.StartedAt.ToLocalTime();
        DateText = local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        _encoders = BuildEncoders();
        _selectedEncoder = _encoders[0];
        // Los textos que se componen aquí (codificadores, ayuda del modo, duración, avisos) se recomponen al cambiar de idioma.
        Localizer.LanguageChanged += OnLanguageChanged;
        Loading = LoadAsync();
    }

    private static EncoderOption[] BuildEncoders() => new[]
    {
        new EncoderOption(Loc.T("Rev_Enc_Same"), ClipVideoEncoder.SameAsRecording),
        new EncoderOption(Loc.T("Rev_Enc_H264"), ClipVideoEncoder.H264),
        new EncoderOption(Loc.T("Rev_Enc_Hevc"), ClipVideoEncoder.Hevc),
        new EncoderOption(Loc.T("Rev_Enc_ProRes"), ClipVideoEncoder.ProRes),
    };

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            var chosen = SelectedEncoder.Value;
            Encoders = BuildEncoders();
            SelectedEncoder = Encoders.First(o => o.Value == chosen);
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ModeHint));
            OnPropertyChanged(nameof(ClipDurationText));
            if (_timeline is { } t) { ApplyTimelineTexts(t); UpdatePositionTexts(Current); }
            UpdatePlaybackNote();
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) Apply(); else dispatcher.BeginInvoke(Apply);
    }

    public string ChannelKey { get; }
    /// <summary>Primer archivo de la grabación.</summary>
    public string RecordingName { get; }
    public string DateText { get; }
    public string Title => Loc.F("Rev_Title", ChannelKey, RecordingName);
    [ObservableProperty] private IReadOnlyList<EncoderOption> _encoders;
    public string ClipsFolder => _clips.ClipsFolder;

    /// <summary>La carga de la línea de tiempo (las pruebas la esperan).</summary>
    public Task Loading { get; }

    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _loadMessage = Loc.T("Rev_Loading");
    /// <summary>Archivos de la grabación que no se pueden ver (faltan, están dañados…); vacío si están todos.</summary>
    [ObservableProperty] private string _warning = "";
    /// <summary>Por dónde suena (si no es por la salida por defecto) y si el vídeo va a saltos porque la CPU no da abasto; vacío si
    /// todo es normal.</summary>
    [ObservableProperty] private string _playbackNote = "";
    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty] private bool _hasVideo = true;

    [ObservableProperty] private double _maximum;
    /// <summary>Cuadro de la barra (lo mueve el operador y lo sigue la reproducción).</summary>
    [ObservableProperty] private double _position;
    [ObservableProperty] private string _positionText = "00:00:00:00";
    [ObservableProperty] private string _wallClockText = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private bool _isPlaying;

    /// <summary>Primer cuadro del clip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InText), nameof(ClipDurationText), nameof(HasClip))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(GoToInCommand))]
    private long? _inFrame;

    /// <summary>Último cuadro del clip (incluido).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutText), nameof(ClipDurationText), nameof(HasClip))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(GoToOutCommand))]
    private long? _outFrame;

    public string InText => InFrame is { } f && _timeline is { } t ? t.Timecode(f) : "—";
    public string OutText => OutFrame is { } f && _timeline is { } t ? t.Timecode(f) : "—";
    public bool HasClip => InFrame is { } a && OutFrame is { } b && b >= a;

    /// <summary>«00:00:30:00 · 750 cuadros», o por qué no hay clip todavía.</summary>
    public string ClipDurationText
    {
        get
        {
            if (_timeline is not { } t || InFrame is not { } a || OutFrame is not { } b) return Loc.T("Rev_NoMarks");
            if (b < a) return Loc.T("Rev_OutBeforeIn");
            long frames = b - a + 1;
            return Loc.F(frames == 1 ? "Rev_Duration_One" : "Rev_Duration_Many", Duration(t.TimeOf(frames)), frames);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeHint))]
    private bool _exact;
    public bool Fast { get => !Exact; set => Exact = !value; }
    partial void OnExactChanged(bool value) => OnPropertyChanged(nameof(Fast));
    public string ModeHint => Loc.T(Exact ? "Rev_Mode_Exact_Hint" : "Rev_Mode_Fast_Hint");

    [ObservableProperty] private EncoderOption _selectedEncoder;
    [ObservableProperty] private string _clipName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(CancelExportCommand))]
    private bool _exporting;
    [ObservableProperty] private double _exportProgress;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastClip))]
    [NotifyCanExecuteChangedFor(nameof(ShowClipCommand))]
    private string? _lastClipPath;
    /// <summary>Hay un clip recién exportado que enseñar en su carpeta.</summary>
    public bool HasLastClip => LastClipPath is not null;

    private async Task LoadAsync()
    {
        try
        {
            var timeline = await Task.Run(() => _clips.LoadTimelineAsync(_session.Id));
            _timeline = timeline;
            Maximum = Math.Max(0, timeline.TotalFrames - 1);
            TotalText = timeline.Timecode(timeline.TotalFrames);
            HasVideo = timeline.HasVideo;
            ApplyTimelineTexts(timeline);
            var playback = _playerFactory(timeline);
            _player = playback?.Player;
            (_sound, _soundOutput) = (playback?.Sound ?? ReviewSound.Default, playback?.Output);
            UpdatePlaybackNote();
            if (_player is { } player)
            {
                if (player.FrameWidth > 0)
                {
                    _back = new byte[player.FrameWidth * player.FrameHeight * 4];
                    Image = new WriteableBitmap(player.FrameWidth, player.FrameHeight, 96, 96, PixelFormats.Bgra32, null);
                }
                player.FrameShown += OnFrameShown;
                player.PlayingChanged += OnPlayingChanged;
                player.FellBehind += OnFellBehind;
                player.Seek(0);
            }
            IsLoaded = true;
            LoadMessage = "";
            UpdatePositionTexts(0);
            OnPropertyChanged(nameof(InText));
            OnPropertyChanged(nameof(OutText));
            OnPropertyChanged(nameof(ClipDurationText));
        }
        catch (ClipException ex) { LoadMessage = ErrorText(ex); }
        catch (Exception ex) { LoadMessage = Loc.F("Rev_Err_Load", ex.Message); }
    }

    /// <summary>Los textos de la grabación que se componen en el idioma vigente: la cadencia y el aviso de archivos fuera.</summary>
    private void ApplyTimelineTexts(RecordingTimeline timeline)
    {
        RateText = Loc.F("Rev_Rate", timeline.Rate.Value.ToString("0.###", CultureInfo.CurrentCulture));
        Warning = timeline.Skipped.Count == 0 ? "" : Loc.F("Rev_Skipped", timeline.Skipped.Count,
            string.Join(", ", timeline.Skipped.Take(3).Select(s => $"{Path.GetFileName(s.FilePath)} ({Loc.T("Rev_Skip_" + s.Reason)})")));
    }

    // ---------------------------------------------------------------------
    //  Imagen y posición
    // ---------------------------------------------------------------------

    /// <summary>Cuadro nuevo (hilo del reproductor): se copia aquí —su búfer se reutiliza— y se pinta en el hilo de la ventana
    /// con el último que haya (si la ventana va atrasada, los intermedios no se pintan).</summary>
    private void OnFrameShown(object? sender, ReviewFrame frame)
    {
        lock (_frameLock)
        {
            if (frame.Bgra.Length == _back.Length && _back.Length > 0) Buffer.BlockCopy(frame.Bgra, 0, _back, 0, _back.Length);
            _backFrame = frame.Frame;
            if (_renderQueued) return;
            _renderQueued = true;
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) { Render(); return; }
        dispatcher.BeginInvoke(Render);
    }

    private void Render()
    {
        long frame;
        lock (_frameLock)
        {
            _renderQueued = false;
            frame = _backFrame;
            if (Image is WriteableBitmap bmp && _back.Length > 0)
                bmp.WritePixels(new Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight), _back, bmp.PixelWidth * 4, 0);
        }
        if (frame < 0) return;
        // Mientras una búsqueda está en marcha llegan cuadros anteriores a lo último que se pidió (→ pulsada tres veces seguidas,
        // o un cuadro de la reproducción que se acaba de parar): la barra y el contador no vuelven atrás. Si volvieran, el siguiente
        // → contaría desde ahí y se perderían pulsaciones (medido en la copia publicada: cinco → dejaban la posición en el 3).
        if (_requested >= 0)
        {
            if (frame != _requested) return;
            _requested = -1;
        }
        _positionFromPlayer = true;
        try { Position = frame; }
        finally { _positionFromPlayer = false; }
        UpdatePositionTexts(frame);
        if (_fellBehindAt is { } at && DateTime.UtcNow - at > BehindNoteTime) { _fellBehindAt = null; UpdatePlaybackNote(); }
    }

    private void OnPlayingChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Apply()
        {
            IsPlaying = _player?.IsPlaying ?? false;
            if (!IsPlaying && _fellBehindAt is not null) { _fellBehindAt = null; UpdatePlaybackNote(); }
        }
        if (dispatcher is null) Apply(); else dispatcher.BeginInvoke(Apply);
    }

    /// <summary>El vídeo saltó para seguir al audio (hilo del reproductor): la CPU no da abasto y se le dice al operador.</summary>
    private void OnFellBehind(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Apply()
        {
            _fellBehindAt = DateTime.UtcNow;
            UpdatePlaybackNote();
        }
        if (dispatcher is null) Apply(); else dispatcher.BeginInvoke(Apply);
    }

    /// <summary>Compone el aviso del sonido y de la CPU en el idioma vigente.</summary>
    private void UpdatePlaybackNote()
    {
        string sound = _sound switch
        {
            ReviewSound.OtherOutput => Loc.F("Rev_Sound_Other", _soundOutput ?? "?"),
            ReviewSound.OnlyDeckLink => Loc.T("Rev_Sound_OnlyDeckLink"),
            ReviewSound.NoOutput => Loc.T("Rev_Sound_None"),
            _ => "",
        };
        string behind = _fellBehindAt is null ? "" : Loc.T("Rev_Behind");
        PlaybackNote = string.Join("\n", new[] { sound, behind }.Where(s => s.Length > 0));
    }

    partial void OnPositionChanged(double value)
    {
        if (_positionFromPlayer) return;
        long frame = (long)Math.Round(value);
        _requested = frame;
        UpdatePositionTexts(frame);
        _player?.Seek(frame);
    }

    private void UpdatePositionTexts(long frame)
    {
        if (_timeline is not { } t) return;
        PositionText = t.Timecode(frame);
        WallClockText = t.WallClockOf(frame) is { } wall ? Loc.F("Rev_WallClock", wall.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)) : "";
    }

    private long Current => (long)Math.Round(Position);

    private void SeekTo(long frame)
    {
        if (_timeline is not { } t) return;
        Position = Math.Clamp(frame, 0, Math.Max(0, t.TotalFrames - 1)); // OnPositionChanged busca
    }

    // ---------------------------------------------------------------------
    //  Transporte
    // ---------------------------------------------------------------------

    [RelayCommand]
    private void PlayPause()
    {
        _requested = -1; // la reproducción manda en la barra (y si una búsqueda no llegó a su cuadro, no la deja parada)
        _player?.TogglePlay();
    }

    [RelayCommand] private void StepBack() => Step(-1);
    [RelayCommand] private void StepForward() => Step(1);
    [RelayCommand] private void SecondBack() => Step(-SecondFrames());
    [RelayCommand] private void SecondForward() => Step(SecondFrames());
    [RelayCommand] private void GoToStart() => SeekTo(0);
    [RelayCommand] private void GoToEnd() => SeekTo(long.MaxValue / 2);

    private int SecondFrames() => Math.Max(1, (int)Math.Round(_timeline?.Rate.Value ?? 25));

    /// <summary>Un cuadro adelante usa el proceso que ya decodifica (instantáneo); por eso no pasa por la barra.</summary>
    private void Step(int frames)
    {
        if (_timeline is not { } t) return;
        long target = Math.Clamp(Current + frames, 0, Math.Max(0, t.TotalFrames - 1));
        if (_player is { } p)
        {
            _requested = target;
            _positionFromPlayer = true;
            try { Position = target; }
            finally { _positionFromPlayer = false; }
            UpdatePositionTexts(target);
            p.Seek(target);
        }
        else SeekTo(target);
    }

    // ---------------------------------------------------------------------
    //  Marcas
    // ---------------------------------------------------------------------

    [RelayCommand]
    private void MarkIn()
    {
        if (_timeline is null) return;
        InFrame = Current;
        if (OutFrame is { } o && o < Current) OutFrame = null; // una salida anterior a la entrada no sirve
    }

    [RelayCommand]
    private void MarkOut()
    {
        if (_timeline is null) return;
        OutFrame = Current;
        if (InFrame is { } i && i > Current) InFrame = null;
    }

    [RelayCommand(CanExecute = nameof(CanGoToIn))] private void GoToIn() { if (InFrame is { } f) SeekTo(f); }
    private bool CanGoToIn() => InFrame is not null;
    [RelayCommand(CanExecute = nameof(CanGoToOut))] private void GoToOut() { if (OutFrame is { } f) SeekTo(f); }
    private bool CanGoToOut() => OutFrame is not null;

    [RelayCommand]
    private void ClearMarks()
    {
        InFrame = null;
        OutFrame = null;
    }

    // ---------------------------------------------------------------------
    //  Exportar
    // ---------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        if (_timeline is null || InFrame is not { } a || OutFrame is not { } b || b < a) return;
        var request = new ClipRequest(_session.Id, a, b + 1, Exact ? ClipMode.Exact : ClipMode.Fast)
        {
            Encoder = SelectedEncoder.Value,
            Name = string.IsNullOrWhiteSpace(ClipName) ? null : ClipName.Trim(),
            Operator = Environment.UserName,
        };
        using var cts = new CancellationTokenSource();
        _export = cts;
        Exporting = true;
        ExportProgress = 0;
        StatusIsError = false;
        StatusMessage = Loc.T("Rev_Exporting");
        LastClipPath = null;
        var progress = new Progress<double>(p => ExportProgress = Math.Round(p * 100));
        try
        {
            var result = await Task.Run(() => _clips.ExportAsync(request, progress, cts.Token));
            LastClipPath = result.FilePath;
            StatusMessage = ResultText(result);
        }
        catch (OperationCanceledException) { StatusIsError = true; StatusMessage = Loc.T("Rev_Export_Canceled"); }
        catch (ClipException ex) { StatusIsError = true; StatusMessage = ErrorText(ex); }
        catch (Exception ex) { StatusIsError = true; StatusMessage = Loc.F("Rev_Err_Export", ex.Message); }
        finally
        {
            Exporting = false;
            _export = null;
        }
    }
    private bool CanExport() => !Exporting && HasClip;

    [RelayCommand(CanExecute = nameof(Exporting))]
    private void CancelExport() => _export?.Cancel();

    /// <summary>Abre el Explorador con el clip seleccionado.</summary>
    [RelayCommand(CanExecute = nameof(CanShowClip))]
    private void ShowClip()
    {
        if (LastClipPath is not { } path || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* best-effort */ }
    }
    private bool CanShowClip() => LastClipPath is not null;

    [RelayCommand]
    private void OpenClipsFolder()
    {
        var folder = _clips.ClipsFolder;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    private string ResultText(ClipResult r)
    {
        var parts = new List<string>
        {
            Loc.F("Rev_Done", Path.GetFileName(r.FilePath), StorageFormat.Bytes(r.SizeBytes), Duration(r.Duration)),
        };
        if (r.Mode == ClipMode.Exact && r.VideoEncoder is { } e) parts.Add(Loc.F("Rev_Done_Encoder", e));
        if (r.PreRoll > TimeSpan.Zero) parts.Add(Loc.F("Rev_Done_PreRoll", r.PreRoll.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture)));
        if (r.ExtraFrames > 0) parts.Add(Loc.Plural("Rev_Done_Extra_One", "Rev_Done_Extra_Many", r.ExtraFrames));
        return string.Join(" · ", parts);
    }

    /// <summary>Lo que se le dice al operador de un fallo, en su idioma, con el detalle (el archivo, lo que dijo FFmpeg).</summary>
    internal static string ErrorText(ClipException ex) => Loc.F("Rev_Err_" + ex.Error, ex.Detail ?? "");

    /// <summary>«1 h 05 min 10 s», «30 s», «0,48 s» (menos de un segundo: con centésimas).</summary>
    private static string Duration(TimeSpan d)
    {
        if (d.TotalSeconds < 1) return d.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture) + " s";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours} h {d.Minutes:00} min {d.Seconds:00} s";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes} min {d.Seconds:00} s";
        return d.TotalSeconds.ToString(d.TotalSeconds < 10 ? "0.00" : "0", CultureInfo.CurrentCulture) + " s";
    }

    /// <summary>Cierra el reproductor (sus FFmpeg) y cancela una exportación en curso.</summary>
    public async ValueTask DisposeAsync()
    {
        Localizer.LanguageChanged -= OnLanguageChanged;
        _export?.Cancel();
        if (_player is { } p)
        {
            p.FrameShown -= OnFrameShown;
            p.PlayingChanged -= OnPlayingChanged;
            p.FellBehind -= OnFellBehind;
            await p.DisposeAsync();
        }
    }
}
