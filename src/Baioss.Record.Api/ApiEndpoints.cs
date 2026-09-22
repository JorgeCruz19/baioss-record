using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;
using Baioss.Record.Application.UseCases.Queries;
using Baioss.Record.Application.UseCases.Recording;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Baioss.Record.Api;

/// <summary>
/// Mapea la REST API de automatización y el WebSocket de eventos.
/// Llamar desde el host de la app: <c>app.MapBaiossApi();</c>.
/// </summary>
public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapBaiossApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1"); // .RequireAuthorization() en producción

        // --- Grabación ---
        api.MapPost("/channels/{id:guid}/recording/start", async (Guid id, StartBody body, IDispatcher d, CancellationToken ct) =>
        {
            try { return Results.Ok(await d.SendAsync(new StartRecordingCommand(id, body.ProfileId, body.Operator), ct)); }
            // El canal ya está grabando (doble START): conflicto claro en vez de un 500. (Auditoría 24/7, A9.)
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // Detener. SIN cuerpo (lo de siempre): 204. Con { "name": "…" } además le pone ese nombre al archivo recién
        // terminado —lo que hace el diálogo de la aplicación al detener una grabación manual— y contesta 200 con lo que
        // pasó: { renamed, pending, fileName, detail }. «pending» = el archivo aún se está optimizando y se renombrará
        // solo al acabar (queda en la auditoría como RecordingRenamed). Una grabación PROGRAMADA ya tiene su nombre: se
        // detiene igual y el nombre se ignora (detail = "scheduled"). El cuerpo se lee A MANO y con tolerancia (ver
        // ReadStopBodyAsync): los clientes que ya llamaban —sin cuerpo o con cualquier cosa— no notan nada.
        api.MapPost("/channels/{id:guid}/recording/stop", async (Guid id, HttpContext http, IDispatcher d, CancellationToken ct) =>
        {
            var body = await ReadStopBodyAsync(http.Request, ct);
            var result = await d.SendAsync(new StopRecordingCommand(id, body?.Name, body?.Operator), ct);
            if (result.Name == RecordingNameOutcome.NotRequested) return Results.NoContent();
            return Results.Ok(new
            {
                renamed = result.Name == RecordingNameOutcome.Renamed,
                pending = result.Name == RecordingNameOutcome.Pending,
                fileName = result.FileName,
                detail = result.Detail,
            });
        });

        // --- Estado / consultas ---
        api.MapGet("/channels/{id:guid}/status", async (Guid id, IDispatcher d, CancellationToken ct) =>
            Results.Ok(await d.QueryAsync(new GetChannelStatusQuery(id), ct)));

        api.MapGet("/channels", (IChannelManager m) =>
            Results.Ok(m.Channels.Select(c => c.Status)));

        // Preview de BAJA RESOLUCIÓN para clientes remotos (el panel web): una instantánea JPEG del último frame, de
        // `w` píxeles de ancho (160–640; 320 por defecto ≈ 10–20 KB). El cliente la pide a su ritmo (1 por segundo);
        // la aplicación captura y codifica SOLO cuando alguien pregunta, así que sin panel abierto no cuesta nada.
        // 404 si el canal no tiene preview (canal simulado, entrada reasignándose) o el host no ofrece instantáneas.
        api.MapGet("/channels/{id:guid}/preview.jpg", async (Guid id, int? w, HttpContext http, CancellationToken ct) =>
        {
            var snapshots = http.RequestServices.GetService<IChannelSnapshotProvider>();
            var jpeg = snapshots is null ? null : await snapshots.GetJpegAsync(id, w is > 0 ? w.Value : 320, ct: ct);
            if (jpeg is null) return Results.NotFound(new { error = "El canal no tiene preview disponible." });
            http.Response.Headers.CacheControl = "no-store"; // cada petición es un frame nuevo: que nadie lo cachee
            return Results.Bytes(jpeg, "image/jpeg");
        });

        api.MapGet("/storage", async (string? volume, IStorageManager s, CancellationToken ct) =>
        {
            // Seguridad: solo se permite consultar el VOLUMEN donde corre la app (donde se graba), no una ruta
            // arbitraria del sistema que un proceso local cualquiera pase por el parámetro. La consulta es por
            // volumen (DriveInfo), así que basta comparar la raíz; si se omite, se usa la del propio proceso.
            // (Auditoría 24/7, #57.)
            var appVolume = Path.GetPathRoot(AppContext.BaseDirectory);
            string? requested;
            try { requested = string.IsNullOrWhiteSpace(volume) ? appVolume : Path.GetPathRoot(Path.GetFullPath(volume)); }
            catch (Exception ex) { return Results.BadRequest(new { error = "Volumen inválido: " + ex.Message }); }

            if (appVolume is null || !string.Equals(requested, appVolume, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Solo se permite consultar el volumen de grabación." });

            try { return Results.Ok(await s.GetStatusAsync(requested!, ct)); } // no-nulo tras la guarda (== appVolume)
            catch (Exception ex) { return Results.Problem("No se pudo consultar el volumen: " + ex.Message, statusCode: 404); }
        });

        // --- Grabaciones / retención (gestión de almacenamiento — Fase 1) ---
        // Lista el historial reciente con su estado de PROTECCIÓN, para que el operador (o una UI/automatización)
        // sepa qué hay y pueda marcarlo. `channel` filtra por canal; `days` acota la ventana (30 por defecto).
        api.MapGet("/recordings", async (Guid? channel, int? days, [FromServices] IRecordingSessionRepository sessions, CancellationToken ct) =>
        {
            var to = DateTimeOffset.UtcNow;
            var from = to - TimeSpan.FromDays(days is > 0 ? days.Value : 30);
            var list = await sessions.GetHistoryAsync(channel, from, to, skip: 0, take: 500, ct);
            return Results.Ok(list.Select(s => new
            {
                s.Id, s.ChannelId, s.StartedAt, s.EndedAt,
                DurationSeconds = (int)s.Duration.TotalSeconds,
                s.TotalBytes, Files = s.Segments.Count,
                Protection = s.Protection.ToString(), s.Operator,
                // Auditoría: cómo se puso en marcha (Manual/Scheduled/Api), por qué terminó y, si fue
                // programada, qué tarea la disparó.
                Trigger = s.Trigger.ToString(), StopReason = s.StopReason.ToString(), s.ScheduledJobId,
            }));
        });

        // AUDITORÍA: el registro de eventos del equipo (inicios y paradas de grabación con su motivo, fallos
        // de arranque, ocurrencias programadas omitidas, pérdidas de señal, disco…). Es la traza que responde a
        // «quién grabó qué, cuándo y por qué se cortó». Solo lectura: las entradas las escribe el sistema.
        //   days      ventana hacia atrás (por defecto 7)
        //   channel   filtra por canal
        //   category  nombre del evento (RecordingStarted, RecordingStopped, RecordingStartFailed…)
        //   severity  Info | Warning | Error | Critical  (devuelve esa severidad Y las superiores)
        //   take      máximo de entradas (por defecto 500, tope 5000)
        api.MapGet("/events", async (int? days, Guid? channel, string? category, string? severity, int? take,
            [FromServices] IEventLogRepository events, CancellationToken ct) =>
        {
            EventSeverity? min = null;
            if (!string.IsNullOrWhiteSpace(severity))
            {
                if (!Enum.TryParse<EventSeverity>(severity, ignoreCase: true, out var parsed))
                    return Results.BadRequest(new { error = "Severidad inválida. Usa Info, Warning, Error o Critical." });
                min = parsed;
            }

            var to = DateTimeOffset.UtcNow;
            var from = to - TimeSpan.FromDays(days is > 0 ? days.Value : 7);
            // Se pide de más al repositorio porque los filtros de categoría/severidad se aplican aquí: si se
            // pidiera justo «take», filtrar después devolvería menos de lo pedido teniendo más disponible.
            bool filtering = min is not null || !string.IsNullOrWhiteSpace(category);
            int limit = Math.Clamp(take is > 0 ? take.Value : 500, 1, 5000);
            var list = await events.QueryAsync(channel, from, to, filtering ? Math.Min(limit * 10, 20_000) : limit, ct);

            IEnumerable<EventLogEntry> filtered = list;
            if (min is { } sev) filtered = filtered.Where(e => e.Severity >= sev);
            if (!string.IsNullOrWhiteSpace(category))
                filtered = filtered.Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase));

            return Results.Ok(filtered.Take(limit).Select(e => new
            {
                e.Id, e.Timestamp, Severity = e.Severity.ToString(), e.Category,
                e.ChannelId, e.Operator, e.Message, e.PayloadJson,
            }));
        });

        // Marca (o quita) la protección de una grabación frente a la limpieza automática. Body: {"level":"Protected"}
        // (None | Important | Protected). Una grabación protegida NUNCA se borra ni archiva automáticamente.
        api.MapPost("/recordings/{id:guid}/protection", async (Guid id, ProtectionBody body, [FromServices] IRecordingSessionRepository sessions, CancellationToken ct) =>
        {
            if (!Enum.TryParse<RecordingProtection>(body.Level, ignoreCase: true, out var level))
                return Results.BadRequest(new { error = "Nivel inválido. Usa None, Important o Protected." });
            var ok = await sessions.SetProtectionAsync(id, level, ct);
            return ok ? Results.Ok(new { id, protection = level.ToString() }) : Results.NotFound();
        });

        // --- Ajustes de almacenamiento (Fase 4c): retención + alertas por % + modo emergencia, editables en
        //     caliente. GET devuelve los vigentes; PUT los guarda (se SANEAN) y los servicios de fondo los aplican
        //     en su próximo ciclo SIN reiniciar. El body de PUT es un objeto StorageSettings completo. ---
        // --- Licencia: estado y activación. [FromServices] EXPLÍCITO (si no, ASP.NET infiere el servicio como
        //     cuerpo de la petición y rompe TODA la API al construir los endpoints). ---
        api.MapGet("/license", ([FromServices] Baioss.Record.Application.Licensing.ILicenseService lic) =>
            Results.Ok(new { lic.Current.State, lic.Current.DaysRemaining, lic.Current.Summary, lic.MachineCode, lic.Current.CanStartRecording, lic.Current.LicensedChannels }));

        api.MapPost("/license/activate", (ActivateBody body, [FromServices] Baioss.Record.Application.Licensing.ILicenseService lic) =>
        {
            var result = lic.Activate(body.Key ?? "");
            // 422 y no 500: una clave incorrecta es una entrada inválida del usuario, no un fallo del servidor.
            return result.Success
                ? Results.Ok(new { result.Message, lic.Current.State, lic.MachineCode })
                : Results.UnprocessableEntity(new { error = result.Message, reason = result.Rejection.ToString() });
        });

        // Estado GLOBAL del almacenamiento tal como lo ve el indicador de la UI (MULTI-DISCO): peor disco +
        // nº de discos vigilados + desglose por disco + emergencia. Lo publica el coordinador de emergencia.
        api.MapGet("/storage/status", ([FromServices] IStorageStatusProvider provider) => Results.Ok(provider.Current));
        api.MapGet("/storage/settings", ([FromServices] IStorageSettingsStore store) => Results.Ok(store.Current));
        // [FromServices] EXPLÍCITO en `store`: sin él, ASP.NET lo infiere como un 2º body (junto a `body`) y falla
        // al construir el endpoint —rompiendo TODA la API— si el test/DI no lo tiene registrado. (Como en /recordings.)
        api.MapPut("/storage/settings", (StorageSettings body, [FromServices] IStorageSettingsStore store) =>
        {
            store.Save(body);
            return Results.Ok(store.Current); // la versión SANEADA realmente aplicada
        });

        // --- WebSocket de eventos ---
        app.Map("/ws/events", async (HttpContext ctx, IEventBus bus, IHostApplicationLifetime lifetime) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            // AL CERRAR LA APLICACIÓN NO SE ESPERA AL CLIENTE. Este bucle solo terminaba cuando el cliente cerraba; con
            // un panel web abierto, el apagado de Kestrel se quedaba esperando (30 s), el cierre de la aplicación
            // agotaba su tope de 20 s ANTES de llegar a finalizar las grabaciones y acababa en salida forzada: sesión
            // sin cerrar en BD y archivo sin finalizar (medido). Al empezar el apagado se corta el socket; el panel
            // reconecta solo cuando la aplicación vuelve.
            using var onStopping = lifetime.ApplicationStopping.Register(() => { try { socket.Abort(); } catch { /* ya cerrado */ } });
            // Un WebSocket NO admite envíos solapados: con varios canales publicando a la vez, dos SendAsync
            // concurrentes lanzan InvalidOperationException y dejan el stream de eventos en estado Aborted. Se
            // serializan con un semáforo por conexión, y cada envío lleva timeout para que un cliente lento no
            // bloquee el bus de eventos (que está en la ruta de algunos start/stop). (Auditoría 24/7, A1/#27.)
            using var sendGate = new SemaphoreSlim(1, 1);
            using var sub = bus.Subscribe<IDomainEvent>(async (e, _) =>
            {
                if (socket.State != WebSocketState.Open) return;
                var json = JsonSerializer.SerializeToUtf8Bytes(e, e.GetType());
                await sendGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.SendAsync(json, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                }
                catch { try { socket.Abort(); } catch { /* cliente caído: WaitUntilClosed cerrará la suscripción */ } }
                finally { sendGate.Release(); }
            });
            await WaitUntilClosedAsync(socket);
        });

        // --- WebSocket de preview de baja resolución (panel web) ---
        // El SERVIDOR marca el ritmo: empuja un JPEG de `w` píxeles cada 1/fps segundos (fps 1–15, 5 por defecto). Cada
        // cuadro se captura cuando toca enviarlo y el siguiente no se pide hasta que el anterior SALIÓ, así que nunca hay
        // más de uno en vuelo por cliente: a uno lento (o con mala red) simplemente le llegan menos cuadros, sin colas ni
        // retraso acumulado, y FFmpeg ni se entera. Mensajes: binario = un JPEG completo; texto «unavailable» = el canal no
        // tiene imagen ahora mismo (canal simulado, entrada reasignándose) y se sigue intentando.
        app.Map("/ws/preview/{id:guid}", async (Guid id, int? w, int? fps, HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var snapshots = ctx.RequestServices.GetService<IChannelSnapshotProvider>();
            if (snapshots is null) { ctx.Response.StatusCode = 404; return; }
            // Tope global: cada conexión es una captura periódica; un cliente defectuoso que abra cientos no debe poder
            // cargar la máquina que graba.
            if (Interlocked.Increment(ref _previewSockets) > MaxPreviewSockets)
            {
                Interlocked.Decrement(ref _previewSockets);
                ctx.Response.StatusCode = 503;
                return;
            }
            try
            {
                using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
                // Igual que en /ws/events: el cierre de la aplicación corta el envío (RequestAborted no se dispara
                // hasta que Kestrel agota su plazo de apagado).
                var stopping = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
                using var ends = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, stopping);
                await StreamPreviewAsync(socket, snapshots, id, w is > 0 ? w.Value : 320, Math.Clamp(fps ?? 5, 1, MaxPreviewFps), ends.Token);
            }
            finally { Interlocked.Decrement(ref _previewSockets); }
        });

        return app;
    }

    /// <summary>
    /// Cuerpo OPCIONAL de «detener», leído con tolerancia. Este endpoint nunca tuvo cuerpo, así que aceptaba CUALQUIER
    /// POST; con el enlace automático de ASP.NET (<c>StopBody? body</c>) un formulario vacío —lo que envían
    /// <c>curl -d ''</c> o <c>Invoke-WebRequest -Method Post</c>— pasaba a dar 415 y un JSON mal formado 400: un cliente
    /// que llevaba años deteniendo así dejaría de poder DETENER una grabación (medido). Solo se atiende un JSON válido y
    /// pequeño; todo lo demás se ignora y la grabación se detiene como siempre.
    /// </summary>
    private static async Task<StopBody?> ReadStopBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType() || request.ContentLength is > 8 * 1024) return null;
        try { return await request.ReadFromJsonAsync<StopBody>(ct); }
        catch (JsonException) { return null; }            // cuerpo vacío o JSON mal formado
        catch (BadHttpRequestException) { return null; }  // cuerpo cortado a medias
    }

    private const int MaxPreviewSockets = 32;
    private const int MaxPreviewFps = 15;
    private static readonly TimeSpan PreviewSendTimeout = TimeSpan.FromSeconds(5);
    private static int _previewSockets;

    private static async Task StreamPreviewAsync(WebSocket socket, IChannelSnapshotProvider snapshots, Guid channelId,
        int width, int fps, CancellationToken aborted)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(aborted);

        // El cliente no envía datos: esta lectura solo existe para VER su Close (o su caída) y cortar el envío. No
        // responde al Close aquí: el cierre lo completa el bucle de envío al salir, porque un WebSocket no admite dos
        // envíos a la vez (el Close de respuesta podría solaparse con un cuadro en vuelo).
        var reader = Task.Run(async () =>
        {
            var buffer = new byte[256];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var r = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { /* cliente caído o socket abortado */ }
            finally { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        });

        var period = TimeSpan.FromMilliseconds(1000.0 / fps);
        int maxAgeMs = Math.Max(1, (int)(period.TotalMilliseconds / 2)); // nunca dos veces la misma imagen a este ritmo
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var next = TimeSpan.Zero;
        bool toldUnavailable = false;
        try
        {
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, stop.Token).ConfigureAwait(false);
                // El siguiente cuadro se programa desde AHORA, no desde el anterior: si la captura o el envío tardaron
                // más que el periodo, se PIERDEN cuadros en vez de acumular retraso o soltar una ráfaga después.
                next = clock.Elapsed + period;

                var jpeg = await snapshots.GetJpegAsync(channelId, width, maxAgeMs, stop.Token).ConfigureAwait(false);
                if (jpeg is null)
                {
                    if (!toldUnavailable) { await SendAsync(socket, "unavailable"u8.ToArray(), WebSocketMessageType.Text, stop.Token).ConfigureAwait(false); toldUnavailable = true; }
                    next = clock.Elapsed + TimeSpan.FromSeconds(1); // sin imagen no tiene sentido insistir al ritmo de vídeo
                    continue;
                }
                toldUnavailable = false;
                await SendAsync(socket, jpeg, WebSocketMessageType.Binary, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* el cliente se fue, o un envío superó su plazo */ }
        catch (WebSocketException) { /* conexión rota a mitad de un envío */ }
        finally
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closing.Token).ConfigureAwait(false);
                }
            }
            catch { /* ya estaba roto */ }
            try { if (socket.State is not (WebSocketState.Closed or WebSocketState.Aborted)) socket.Abort(); } catch { /* ídem */ }
            try { await reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* la lectura muere con el socket */ }
        }
    }

    /// <summary>Un envío con plazo: un cliente que no lee (pestaña congelada, red colgada) no retiene la conexión para
    /// siempre; al vencer, el token cancela el envío y el WebSocket queda abortado.</summary>
    private static async Task SendAsync(WebSocket socket, byte[] data, WebSocketMessageType type, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(PreviewSendTimeout);
        await socket.SendAsync(data, type, endOfMessage: true, timeout.Token).ConfigureAwait(false);
    }

    private static async Task WaitUntilClosedAsync(WebSocket socket)
    {
        var buffer = new byte[256];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closing.Token);
                }
            }
        }
        // Cliente caído sin Close, o socket cortado por el cierre de la aplicación: es un final normal, no un error.
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public sealed record StartBody(Guid ProfileId, string? Operator);
    /// <summary>Cuerpo OPCIONAL de «detener»: el nombre (sin extensión) con el que guardar la grabación y quién lo pone.</summary>
    public sealed record StopBody(string? Name, string? Operator);
    public sealed record ProtectionBody(string Level);
    /// <summary>Cuerpo de la activación de licencia: la clave tal como la recibió el cliente.</summary>
    public sealed record ActivateBody(string? Key);
}
