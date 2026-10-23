using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Clips;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Engine.FFmpeg.Clips;

namespace Baioss.Record.Infrastructure.Clips;

/// <summary>
/// <see cref="IClipExportService"/> con el FFmpeg de la aplicación. Lee los archivos de la grabación tal como están (no cambia
/// nada de cómo se graba) y escribe cada clip primero como «nombre.ext.part» y, cuando FFmpeg acaba bien y el archivo se lee,
/// con su nombre: en la carpeta nunca hay un clip a medias con nombre de clip. Los procesos van con prioridad baja y como
/// mucho <see cref="MaxConcurrentExports"/> a la vez (la máquina está grabando).
/// </summary>
public sealed class ClipExportService : IClipExportService
{
    /// <summary>Exportaciones a la vez; las demás esperan su turno.</summary>
    public const int MaxConcurrentExports = 2;

    private readonly IRecordingSessionRepository _sessions;
    private readonly IFfmpegLocator? _locator;
    private readonly IStorageSettingsStore? _settings;
    private readonly string _defaultFolder;
    private readonly ILogger<ClipExportService> _log;
    private readonly IEventBus? _bus;
    private readonly IRecordingProfileRepository? _profiles;
    private readonly Func<IReadOnlyCollection<Guid>>? _recordingNow;
    private readonly SemaphoreSlim _exports = new(MaxConcurrentExports, MaxConcurrentExports);
    private readonly ConcurrentDictionary<Guid, (string Stamp, RecordingTimeline Timeline)> _timelines = new();
    private Task<ClipEncoderSupport>? _support;

    /// <summary><paramref name="defaultFolder"/>: la carpeta de clips cuando no se eligió ninguna en «Almacenamiento».
    /// <paramref name="recordingNow"/>: las sesiones que algún canal está escribiendo ahora (null = solo cuenta el fin en la BD).
    /// <paramref name="support"/>: los codificadores por GPU utilizables (null = se sondean la primera vez que hacen falta).</summary>
    public ClipExportService(IRecordingSessionRepository sessions, IFfmpegLocator? locator, IStorageSettingsStore? settings,
        string defaultFolder, ILogger<ClipExportService> log, IEventBus? bus = null, IRecordingProfileRepository? profiles = null,
        Func<IReadOnlyCollection<Guid>>? recordingNow = null, ClipEncoderSupport? support = null)
    {
        _sessions = sessions;
        _locator = locator;
        _settings = settings;
        _defaultFolder = defaultFolder;
        _log = log;
        _bus = bus;
        _profiles = profiles;
        _recordingNow = recordingNow;
        if (support is not null) _support = Task.FromResult(support);
    }

    /// <summary>La elegida en «Almacenamiento» (una ruta relativa, junto a la de por defecto) o la de por defecto.</summary>
    public string ClipsFolder => _settings?.Current.ClipsFolder is { Length: > 0 } chosen
        ? Path.IsPathRooted(chosen) ? chosen : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_defaultFolder) ?? ".", chosen))
        : _defaultFolder;

    public string DefaultClipsFolder => _defaultFolder;

    public async Task<RecordingTimeline> LoadTimelineAsync(Guid sessionId, CancellationToken ct = default)
    {
        var locator = _locator ?? throw new ClipException(ClipError.Unavailable);
        var session = await _sessions.GetWithSegmentsAsync(sessionId, ct).ConfigureAwait(false)
                      ?? throw new ClipException(ClipError.NotFound);
        if (session.EndedAt is null || RecordingNow().Contains(session.Id)) throw new ClipException(ClipError.InProgress);
        var segments = session.Segments.Where(s => !string.IsNullOrWhiteSpace(s.FilePath)).OrderBy(s => s.Index).ToList();
        if (segments.Count == 0) throw new ClipException(ClipError.NoFiles);

        // Se vuelve a sondear solo si cambió algún archivo (un renombrado, la verificación que reescribe al cerrar…).
        string stamp = Stamp(segments);
        if (_timelines.TryGetValue(sessionId, out var cached) && cached.Stamp == stamp) return cached.Timeline;

        var described = new MediaDescription?[segments.Count];
        using (var gate = new SemaphoreSlim(4))
            await Task.WhenAll(segments.Select(async (seg, i) =>
            {
                if (!File.Exists(seg.FilePath)) return;
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { described[i] = await FfprobeMedia.DescribeAsync(locator.FfprobePath, seg.FilePath, ct).ConfigureAwait(false); }
                finally { gate.Release(); }
            })).ConfigureAwait(false);

        var pieces = new List<TimelinePiece>();
        var skipped = new List<SkippedPiece>();
        Domain.ValueObjects.FrameRate? rate = null;
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (!File.Exists(seg.FilePath)) { skipped.Add(new SkippedPiece(seg.FilePath, SkipReason.Missing)); continue; }
            if (described[i] is not { } media) { skipped.Add(new SkippedPiece(seg.FilePath, SkipReason.Unreadable)); continue; }
            var pieceRate = media.Video?.FrameRate ?? RecordingTimeline.AudioOnlyRate;
            // Una rejilla de cuadros para toda la grabación: una pieza con otra cadencia (la señal cambió de formato) no cabe.
            if (rate is { } r && (r != pieceRate || (media.Video is null) != (pieces[0].Media.Video is null)))
            { skipped.Add(new SkippedPiece(seg.FilePath, SkipReason.OtherFrameRate)); continue; }
            long frames = FfprobeMedia.FrameCount(media, RecordingTimeline.AudioOnlyRate);
            if (frames <= 0) { skipped.Add(new SkippedPiece(seg.FilePath, SkipReason.Empty)); continue; }
            rate ??= pieceRate;
            pieces.Add(new TimelinePiece
            {
                SegmentId = seg.Id, Index = seg.Index, FilePath = seg.FilePath, Frames = frames, Media = media,
                StartedAt = seg.StartedAt == default ? null : seg.StartedAt,
            });
        }
        if (pieces.Count == 0)
            throw new ClipException(skipped.All(s => s.Reason == SkipReason.Missing) ? ClipError.FileMissing : ClipError.Unreadable,
                Path.GetFileName(skipped[0].FilePath));

        var timeline = new RecordingTimeline(session.Id, session.ChannelId, rate!.Value, pieces, skipped);
        _timelines[sessionId] = (stamp, timeline);
        if (skipped.Count > 0)
            _log.LogWarning("Revisión de {Session}: {Skipped} archivo(s) fuera de la línea de tiempo: {Files}.", sessionId, skipped.Count,
                string.Join(", ", skipped.Select(s => $"{Path.GetFileName(s.FilePath)} ({s.Reason})")));
        return timeline;
    }

    public async Task<ClipResult> ExportAsync(ClipRequest request, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var locator = _locator ?? throw new ClipException(ClipError.Unavailable);
        var timeline = await LoadTimelineAsync(request.SessionId, ct).ConfigureAwait(false);
        var plan = ClipPlanner.Plan(timeline, request.InFrame, request.OutFrame);
        var session = await _sessions.GetWithSegmentsAsync(request.SessionId, ct).ConfigureAwait(false)
                      ?? throw new ClipException(ClipError.NotFound);

        var folder = ClipsFolder;
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) { throw new ClipException(ClipError.OutputFailed, folder, ex); }

        await _exports.WaitAsync(ct).ConfigureAwait(false);
        string? list = null;
        try
        {
            var firstFile = session.Segments.OrderBy(s => s.Index).Select(s => s.FilePath).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            var media = timeline.Media;
            var rate = timeline.Rate;
            string input = plan.Input.SingleFile ?? (list = await WriteListAsync(plan.Input, ct).ConfigureAwait(false));

            ClipEncoding? encoding = null;
            ContainerFormat container;
            if (request.Mode == ClipMode.Exact)
            {
                var profile = _profiles is null ? null : await SafeProfileAsync(session.ProfileId, ct).ConfigureAwait(false);
                encoding = ClipEncoding.Resolve(request.Encoder, media, profile, await SupportAsync(locator, ct).ConfigureAwait(false));
                container = encoding.Container;
            }
            else container = ClipCommandBuilder.FastContainer(media, timeline.Pieces[0].FilePath);

            var inTime = timeline.TimeOf(plan.InFrame);
            var outTime = timeline.TimeOf(plan.OutFrame);
            string name = ClipNaming.Sanitize(request.Name)
                          ?? ClipNaming.ClipName(ClipNaming.RecordingName(firstFile, session.Segments.Count), inTime, outTime);
            string ext = ClipCommandBuilder.Extension(container);
            var (final, part) = ReserveName(folder, name, ext);

            int extra = 0;
            TimeSpan preRoll = TimeSpan.Zero;
            try
            {
                if (encoding is null)
                {
                    double end = plan.End - ClipCommandBuilder.Margin(rate);
                    if (media.Video is { HasBFrames: true }) (end, extra) = await CopyEndAsync(locator, timeline, plan, ct).ConfigureAwait(false);
                    var args = ClipCommandBuilder.Fast(plan, input, end, container, part, rate);
                    await RunAsync(locator, args, plan, timeline, progress, ct).ConfigureAwait(false);
                    preRoll = await PreRollAsync(locator, timeline, plan, ct).ConfigureAwait(false);
                }
                else
                {
                    var args = ClipCommandBuilder.Exact(plan, input, encoding, part, rate);
                    try { await RunAsync(locator, args, plan, timeline, progress, ct).ConfigureAwait(false); }
                    catch (ClipException ex) when (ex.Error == ClipError.FfmpegFailed && encoding.UsesGpu)
                    {
                        // NVENC no abrió (sus sesiones las tienen las grabaciones, o el driver): el clip sale igual, en la CPU.
                        _log.LogWarning("Clip de {Session}: {Encoder} falló ({Detail}); se repite en la CPU.", request.SessionId,
                            encoding.VideoEncoder, ex.Detail);
                        var profile = _profiles is null ? null : await SafeProfileAsync(session.ProfileId, ct).ConfigureAwait(false);
                        encoding = ClipEncoding.Resolve(request.Encoder, media, profile, ClipEncoderSupport.CpuOnly, forceCpu: true);
                        args = ClipCommandBuilder.Exact(plan, input, encoding, part, rate);
                        await RunAsync(locator, args, plan, timeline, progress, ct).ConfigureAwait(false);
                    }
                }

                if (!File.Exists(part) || new FileInfo(part).Length == 0)
                    throw new ClipException(ClipError.FfmpegFailed, "el archivo quedó vacío");
                if (await FfprobeMedia.DescribeAsync(locator.FfprobePath, part, ct).ConfigureAwait(false) is null)
                    throw new ClipException(ClipError.FfmpegFailed, "el archivo no se puede leer");
                // Entre que se eligió el nombre y ahora pudo aparecer otro igual (un archivo copiado a mano a la carpeta).
                if (File.Exists(final)) final = ClipNaming.UniquePath(folder, name, ext, p => File.Exists(p));
                try { File.Move(part, final); }
                catch (IOException ex) { throw new ClipException(ClipError.OutputFailed, ex.Message, ex); }
                catch (UnauthorizedAccessException ex) { throw new ClipException(ClipError.OutputFailed, ex.Message, ex); }
            }
            catch
            {
                TryDelete(part);
                throw;
            }

            long size = new FileInfo(final).Length;
            var result = new ClipResult(final, plan.InFrame, plan.OutFrame, inTime, outTime, request.Mode, size)
            {
                VideoEncoder = encoding?.VideoEncoder,
                PreRoll = preRoll,
                ExtraFrames = extra,
            };
            _log.LogInformation("Clip exportado de {Session} ({Mode}{Encoder}): {In}–{Out} (último cuadro; {Frames} cuadros) → {File} ({Bytes:N0} bytes){PreRoll}.",
                request.SessionId, request.Mode, encoding?.VideoEncoder is { } e ? ", " + e : "", timeline.Timecode(plan.InFrame),
                timeline.Timecode(plan.OutFrame - 1), plan.Frames, final, size,
                preRoll > TimeSpan.Zero ? $"; el archivo empieza {preRoll.TotalSeconds:0.000} s antes (fotograma clave)" : "");
            await PublishAsync(new ClipExported(session.ChannelId, session.Id, Path.GetFileName(final),
                firstFile is null ? null : Path.GetFileName(firstFile), inTime, outTime, request.Mode, encoding?.VideoEncoder, size,
                request.Operator)).ConfigureAwait(false);
            progress?.Report(1);
            return result;
        }
        finally
        {
            _exports.Release();
            if (list is not null) TryDelete(list);
        }
    }

    /// <summary>Lanza FFmpeg y espera; el avance sale del <c>out_time_us</c> de <c>-progress</c>. Un fallo, con lo que dijo FFmpeg.</summary>
    private async Task RunAsync(IFfmpegLocator locator, IReadOnlyList<string> args, ClipPlan plan, RecordingTimeline timeline,
        IProgress<double>? progress, CancellationToken ct)
    {
        double total = Math.Max(1e-3, timeline.SecondsOf(plan.Frames));
        _log.LogDebug("Clip: ffmpeg {Args}", string.Join(' ', args));
        (string Out, string Err, int Exit) run;
        try
        {
            run = await ClipProcess.RunAsync(locator.FfmpegPath, args, TimeSpan.FromHours(6), ct, line =>
            {
                if (progress is null || !line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
                if (long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                    progress.Report(Math.Clamp(us / 1e6 / total, 0, 0.99));
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ClipException(ClipError.FfmpegFailed, ex.Message, ex); // no arrancó (FFmpeg borrado, sin permiso…)
        }
        var (_, err, exit) = run;
        if (exit != 0)
            throw new ClipException(ClipError.FfmpegFailed, exit == ClipProcess.TimedOut ? "tiempo agotado" : ClipProcess.Tail(err));
    }

    /// <summary>Fin de la copia con B-frames (ver <see cref="ClipCommandBuilder.CopyEnd"/>): los paquetes alrededor de la salida, del
    /// archivo donde cae, pasados a la escala de la entrada.</summary>
    private static async Task<(double End, int Extra)> CopyEndAsync(IFfmpegLocator locator, RecordingTimeline timeline, ClipPlan plan,
        CancellationToken ct)
    {
        var (piece, _) = timeline.Locate(plan.OutFrame - 1);
        var p = timeline.Pieces[piece];
        // Marca del final en la escala del archivo de esa pieza.
        double inFile = plan.End - plan.Input.ToInputTime(piece, 0);
        var packets = await FfprobeMedia.VideoPacketsAsync(locator.FfprobePath, p.FilePath, inFile - 4, inFile + 4, ct).ConfigureAwait(false);
        var converted = packets.Select(x => (plan.Input.ToInputTime(piece, x.Pts), plan.Input.ToInputTime(piece, x.Dts))).ToList();
        return ClipCommandBuilder.CopyEnd(converted, plan.End, timeline.Rate);
    }

    /// <summary>Cuánto antes de la entrada empieza la copia: hasta su fotograma clave.</summary>
    private static async Task<TimeSpan> PreRollAsync(IFfmpegLocator locator, RecordingTimeline timeline, ClipPlan plan, CancellationToken ct)
    {
        if (!timeline.HasVideo) return TimeSpan.Zero;
        var (piece, _) = timeline.Locate(plan.InFrame);
        double startInFile = plan.Start - plan.Input.ToInputTime(piece, 0);
        var key = await FfprobeMedia.KeyframeAtOrBeforeAsync(locator.FfprobePath, timeline.Pieces[piece].FilePath,
            startInFile - ClipCommandBuilder.Margin(timeline.Rate), ct).ConfigureAwait(false);
        if (key is not { } k) return TimeSpan.Zero;
        double seconds = startInFile - k;
        return seconds > 1e-4 ? TimeSpan.FromSeconds(Math.Round(seconds, 6)) : TimeSpan.Zero;
    }

    /// <summary>
    /// El nombre del clip y su «.part», RESERVADO: el «.part» se crea vacío aquí (FFmpeg lo sobrescribe), así dos exportaciones
    /// del mismo tramo a la vez (la ventana y la API) no eligen el mismo nombre ni escriben en el mismo archivo.
    /// </summary>
    private static (string Final, string Part) ReserveName(string folder, string name, string ext)
    {
        for (int attempt = 0; ; attempt++)
        {
            string final = ClipNaming.UniquePath(folder, name, ext);
            string part = final + ClipNaming.PartSuffix;
            try
            {
                using (new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { }
                return (final, part);
            }
            catch (IOException) when (attempt < 20 && File.Exists(part)) { /* lo tomó otra exportación: el siguiente nombre */ }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ClipException(ClipError.OutputFailed, ex.Message, ex);
            }
        }
    }

    private static async Task<string> WriteListAsync(TimelineInput input, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"baioss-clip-{Guid.NewGuid():N}.ffconcat");
        // Sin BOM: la lista tiene que empezar por «ffconcat version 1.0».
        await File.WriteAllTextAsync(path, input.ConcatList(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        return path;
    }

    private async Task<ClipEncoderSupport> SupportAsync(IFfmpegLocator locator, CancellationToken ct)
    {
        if (locator is not FfmpegLocator probe) return ClipEncoderSupport.CpuOnly;
        return await (_support ??= Task.Run(async () =>
        {
            async Task<bool> Opens(string encoder)
            {
                try { return await probe.IsVideoEncoderUsableAsync(encoder, CancellationToken.None).ConfigureAwait(false); }
                catch { return false; }
            }
            var support = new ClipEncoderSupport(await Opens("h264_nvenc").ConfigureAwait(false),
                await Opens("hevc_nvenc").ConfigureAwait(false), await Opens("av1_nvenc").ConfigureAwait(false));
            _log.LogInformation("Clips: NVENC H.264 {H264}, HEVC {Hevc}, AV1 {Av1}.", support.H264Nvenc, support.HevcNvenc, support.Av1Nvenc);
            return support;
        }, CancellationToken.None)).WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<RecordingProfile?> SafeProfileAsync(Guid id, CancellationToken ct)
    {
        try { return await _profiles!.GetAsync(id, ct).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "Clips: no se pudo leer el perfil {Id}.", id); return null; }
    }

    private IReadOnlySet<Guid> RecordingNow()
    {
        try { return _recordingNow?.Invoke().ToHashSet() ?? new HashSet<Guid>(); }
        catch { return new HashSet<Guid>(); }
    }

    private static string Stamp(IEnumerable<Segment> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in segments)
        {
            var fi = new FileInfo(s.FilePath);
            sb.Append(s.FilePath).Append('|').Append(fi.Exists ? fi.Length : -1).Append('|')
              .Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0).Append(';');
        }
        return sb.ToString();
    }

    private async Task PublishAsync(IDomainEvent e)
    {
        if (_bus is null) return;
        try { await _bus.PublishAsync(e, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogError(ex, "Clips: no se pudo dejar constancia en la auditoría."); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
