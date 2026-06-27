# Auditoría 24/7 — Baioss Record

> Auditoría de robustez para operación continua (meses sin reinicio) grabando hasta 4 canales simultáneos (objetivo: escalar a 8). Fecha: 2026-06-27.

## Método

13 dimensiones auditadas en paralelo **leyendo el código real**; **cada hallazgo verificado por un revisor adversarial independiente** que reabrió el `file:line` citado para confirmar la evidencia y descartar exageraciones o problemas ya mitigados.

- **60 hallazgos brutos → 59 confirmados, 1 refutado** (una supuesta fuga del WebSocket que `using var sub` ya mitiga).
- Distribución: **3 Críticos · 13 Altos · 17 Medios · 26 Bajos**.

Subsistemas del enunciado original que **no existen** en el código (omitidos): no hay captura dedicada de **SRT/RTMP/UDP/RTP/MPEG-TS/HLS/NDI-HX/MediaFoundation** — son valores del enum `InputType` sin fábrica (eso *en sí* es el hallazgo #15). No es JS/React: el "event loop" es el **Dispatcher de WPF**; la BD es **EF Core + SQLite**.

---

## 🔴 CRÍTICOS (3)

### C1 — Sin handlers globales de excepción: una excepción no observada mata TODAS las grabaciones
- **Archivo:** `App.xaml.cs:46` (`OnStartup`) · **Prob:** Media
- **Evidencia:** grep de `UnhandledException|DispatcherUnhandledException|UnobservedTaskException` → **0 coincidencias**. `OnStartup` es `async void`.
- **Explicación:** Todos los canales viven en **un proceso, un Dispatcher, un IHost**. Una excepción no capturada en el hilo de UI (un binding, un callback de canal) o en un `_ = Task.Run(...)` fire-and-forget (`StandaloneChannelEngine:349/:396`) termina el proceso.
- **Impacto:** Un fallo puntual en un canal o en la UI cierra la app y detiene la grabación de los 4–8 canales a la vez. Sin log de causa.
- **Solución:** Cablear en `OnStartup` los tres handlers globales volcando a Serilog; `DispatcherUnhandledException` con `e.Handled=true` para fallos de UI no fatales.
- **Estado:** ✅ **IMPLEMENTADO** (`WireGlobalExceptionHandlers()`).

### C2 — Procesos FFmpeg quedan HUÉRFANOS si la app muere de forma anormal
- **Archivo:** `FfmpegProcessSupervisor.cs:76-105` · **Prob:** Media
- **Evidencia:** `Process.Start()` normal; grep `JobObject/AssignProcessToJobObject/ProcessExit` → **0**.
- **Explicación:** En Windows no hay vínculo de vida padre-hijo. Si la app se cuelga, la mata el operador o crashea, cada FFmpeg sigue vivo **reteniendo el dispositivo exclusivo** (DeckLink/dshow) y el puerto NDI. `CloseOrphanedAsync` solo arregla la BD.
- **Impacto:** Hasta 8 FFmpeg zombis bloquean dispositivos y archivos; la app reiniciada no puede reabrir esas entradas → **pérdida total de captura** hasta matarlos a mano.
- **Solución:** Asociar cada FFmpeg a un **Windows Job Object con `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`**.
- **Estado:** ✅ **IMPLEMENTADO** (`ChildProcessTracker` + `Track()` tras `Start()`).

### C3 — Pérdida de señal NDI durante grabación NO dispara slate ni reconexión
- **Archivo:** `NdiReceiver.cs:151-208` · **Prob:** Media
- **Evidencia:** `ReceiveLoop` no tiene `case frame_type_none`; `OnVideo` fija formato solo `if (Width==0)`.
- **Explicación:** Cuando la fuente NDI desaparece, FFmpeg (cliente del socket loopback) **no recibe EOF** → se queda leyendo un socket mudo. La carta de ajuste (slate) la dispara `blackdetect`, que **nunca se activa sin frames**; el único fallback es el watchdog a los 15 s, y `CurrentSignal` queda `Locked` para siempre (no hay `SignalLost`).
- **Impacto:** El canal graba vacío hasta 15 s con cortes en bucle en vez de una carta de ajuste limpia, sin alarma.
- **Solución:** Detectar la ausencia de frames en el receptor (~3 s), propagar un evento de presencia → `SignalChanged` → (a) `SignalMonitor` publica `SignalLost`, (b) el engine entra en slate proactivamente y sale al recuperar la señal.
- **Estado:** ✅ **IMPLEMENTADO** (`NdiReceiver.PresenceChanged` → `NdiCaptureSource` → `FfmpegChannelEngine.OnSourceSignalChanged`). Resuelve también #16.

---

## 🟠 ALTOS (13)

| # | Archivo:línea | Problema | Solución |
|---|---|---|---|
| A1 (#4/#12) | `ApiEndpoints.cs:54-59` | `SendAsync` concurrente sobre el mismo WebSocket → `InvalidOperationException`, el feed de eventos se corrompe | `SemaphoreSlim(1,1)` por conexión, o cola + writer-loop |
| A2 (#5) | `App.xaml.cs:46-140` | `OnStartup` `async void` sin try/catch: puerto ocupado / fallo de DI cierra sin log | try/catch + `MessageBox` + `Mutex` de instancia única |
| A3 (#6) | `NdiReceiver.cs:39,206` | Drift A/V permanente: cola de vídeo `DropOldest`(4), PTS por contador | Duplicar último frame; contador de drops a la alarma |
| A4 (#7) | `NdiReceiver.cs:41,228` | Descarte de audio `DropOldest`(16) → audio adelantado | El audio nunca debe descartarse: `FullMode.Wait` |
| A5 (#8) | `StandaloneChannelEngine.cs:129-189` | Start/Stop sin guarda de reentrada (UI, scheduler, auto-stop disco) | `SemaphoreSlim(1,1)` propio |
| A6 (#9) | `FfmpegArgumentBuilder.cs:316-337` | Segmentos MP4/MOV sin flags robustos: corte = segmento (15 min) ilegible | `-segment_format_options movflags=+frag_keyframe+empty_moov` |
| A7 (#10) | `DiskSpaceGuard.cs:33,90` | Auto-stop por disco por canal, no agregado → disco lleno antes del cierre | Una guarda por volumen sumando el bitrate de todos los canales |
| A8 (#11/#29/#37) | `NdiReceiver.cs:112-126` | Fuga de sockets: `TcpClient` anterior no dispuesto al reconectar | `Dispose()` el cliente anterior con `Interlocked.Exchange` |
| A9 (#13) | `StandaloneChannelEngine.cs:129-170` | Doble START huérfana sesión y archivo | `if (State is Recording) throw` → API 409 |
| A10 (#14) | `ApiEndpoints.cs:25-35` | API REST sin autenticación (mitiga: loopback) | API key en header con `FixedTimeEquals` |
| A11 (#15) | `ChannelHost.cs:100-107` | InputType sin fábrica → canal a simulado mudo | `CanHandle(type)` + estado de error visible |
| A12 (#16) | `NdiCaptureSource.cs:30-64` | NDI nunca reporta pérdida de señal | Monitorización continua de presencia (resuelto con C3) |

> **Implementados (2026-06-27):** A1/#27/#28 (serialización + timeout + keep-alive del WebSocket), A5/A9/#31 (guarda de reentrada + rechazo de doble START → **409**), #49 (`-hwaccel cuda` sin `-hwaccel_output_format`). Validados en vivo: doble START → 409; stop idempotente sobre canal Idle. Pendientes: A3/A4/A6/A7/A8/A10/A11 + #20/#23/#32/#55.

---

## 1) Top 20 riesgos

C1, C2, C3, A1…A12 (arriba), y: #49 (`-hwaccel_output_format cuda` rompe File/DeckLink con perfil Nvenc por defecto), #20 (auto-stop scheduler vs rename manual), #23 (último segmento no persistido ante corte), #32 (reconexión NDI con cambio de formato → vídeo corrupto), #55 (watchdog mira progreso, no crecimiento de archivo).

## 2) Top 20 optimizaciones de rendimiento

#47 (conversión `format=` NDI por CPU cada frame → GPU), #21/#25/#26 (tormenta de UI por audio: no llamar `Raise()` desde `OnAudioLevels`), #41 (scheduler relee SQLite cada 1 s), #22 (sin índices en `RecordingSession`), #46 (`ebur128=peak=true` innecesario), #48 (filtros de análisis siempre activos), #40 (`D3DImagePreview` copia fila a fila + `Flush` por frame), #17 (arranque bloqueante en serie), #38 (event bus en serie), #45 (proxy `scale_cuda` sobre frames SW), #49, #27 (`SendAsync` sin timeout), #28 (WS sin keep-alive), #44 (remux sin comprobar espacio), #43 (temporal `.faststart` huérfano), #42 (`EventLog` cableado a medias), #53 (CTS de slate sin disponer), #50 (`RefreshTodayTasks` reentrante), #39 (probe 2º FFmpeg cada 5 s), #51 (VMs de ventana sin disponer).

## 3) Memory leaks

- **Sockets NDI** (#11/#29/#37, Alta) — `TcpClient`/`NetworkStream` no dispuestos en cada reconexión.
- **`PresetManagerViewModel`** (#24, Media) — suscrito a `IPresetStore.Changed` (singleton) sin desuscribir.
- **CTS de slate** (#53, Baja) — `_recoveryCts`/`_awaitCts` se cancelan pero no se disponen.
- **Suscripciones WS zombi** (#28, Media) — half-open sin `KeepAliveInterval`.
- **VMs de ventanas** (#51, Baja) — patrón `.Show()` sin disponer el VM.

## 4) Puntos de pérdida de grabación

C2 (huérfano bloquea dispositivo), C3/#16 (caída NDI), #49 (Nvenc + File/DeckLink aborta), A9/#13 (doble START), A11/#15 (tipo no soportado), #23 (último segmento), #55 (atasco de escritura), A7/#10 (disco lleno).

## 5) Puntos de corrupción de archivos

A6/#9 (segmento MP4 sin moov), A7/#10 (ENOSPC a mitad), #32 (reconexión NDI con otro formato), #20 (carrera rename/remux), #54 (`FfmpegRecorderEngine` sin verificación ffprobe — eliminar si es código muerto).

## 6) Recomendaciones operación 24/7

Handlers globales (C1) ✅ · Job Object + barrido de huérfanos al arrancar (C2) ✅ · detección de pérdida NDI → slate (C3) ✅ · guardas de reentrada (A5/#13/#20/#31) · flags robustos en segmentos (A6) · cablear `EventLog` (#42) · cerrar fugas (A8/#53) · recovery de segmentos huérfanos (#23) · watchdog por crecimiento de archivo (#55).

## 7) Recomendaciones 4 canales estables

Serializar envíos WS (A1) · sacar el audio de `Sync()` (#21/#25/#26) · guarda de disco agregada (A7) · bus de eventos no bloqueante (#27/#38) · índices BD + caché del scheduler (#22/#41).

## 8) Escalar a 8+ canales

Conversión/escala NDI en GPU (#47) · recortar `ebur128`/`freezedetect` (#46/#48) · fugas en cero (A8/#53) · disco agregado (A7) + auditoría en lote (#42) · aislamiento por canal o, mínimo, handlers globales (C1) · arranque paralelo con timeout (#17).

---

## Anexo — Índice completo de los 59 hallazgos confirmados

> Severidad = corregida tras la verificación adversarial. Refutado (1): supuesta fuga de suscripciones WS (`using var sub` ya la mitiga; el caso half-open sí es #28).

| # | Sev | Dim | Archivo:línea | Hallazgo | Fix |
|--:|-----|-----|---------------|----------|-----|
| 1 | Crítica | architecture-spof | `src/Baioss.Record.App/App.xaml.cs + src/Baioss.Record.App/App.xaml:App.xaml.cs:46 (OnStartup) y todo App.xaml (sin Startup/DispatcherUnhandledException)` | Sin handlers globales de excepción: una excepción no observada tumba el proceso y mata TODAS las grabaciones | Cablear handlers globales en OnStartup: DispatcherUnhandledException (e.Handled=true tras loguear, para no tumbar la UI por un fal… |
| 2 | Crítica | recording-engine | `src/Baioss.Record.Engine.FFmpeg/FfmpegProcessSupervisor.cs:76-105` | Los procesos FFmpeg hijos quedan HUÉRFANOS grabando si la app muere de forma anormal (crash, kill, corte eléct… | Asociar cada FFmpeg hijo a un Windows Job Object con JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE en cuanto arranca, de modo que el SO los m… |
| 3 | Crítica | sources-reconnect | `src/Baioss.Record.Infrastructure/Capture/NdiReceiver.cs:151-171, 173-208` | La pérdida de señal NDI durante la grabación NO dispara la carta de ajuste ni la reconexión: el receptor manti… | Detectar la ausencia de frames NDI en el receptor y propagarla: medir tiempo desde el último frame en ReceiveLoop; si supera un um… |
| 4 | Alta | architecture-spof | `src/Baioss.Record.Api/ApiEndpoints.cs:50-61` | El WebSocket de eventos se suscribe al bus y hace SendAsync concurrente desde múltiples canales → excepción de… | Serializar los envíos por socket con un SemaphoreSlim(1,1) propio de cada conexión; idealmente desacoplar con una Channel<byte[]> … |
| 5 | Alta | architecture-spof | `src/Baioss.Record.App/App.xaml.cs:46-140` | OnStartup es `async void` sin try/catch: cualquier fallo de arranque cierra la app sin que arranque ningún can… | Envolver el cuerpo de OnStartup en try/catch; ante fallo, loguear a Serilog, mostrar un MessageBox con la causa y cerrar de forma … |
| 6 | Alta | av-sync | `src/Baioss.Record.Infrastructure/Capture/NdiReceiver.cs:39-40, 206-207` | Drift A/V permanente: descarte de vídeo (cola DropOldest) con PTS derivado por contador, sin compensación | No confiar en el contador puro cuando puede haber descartes. Dos vías reales: (a) en vez de DropOldest, DUPLICAR el último frame p… |
| 7 | Alta | av-sync | `src/Baioss.Record.Infrastructure/Capture/NdiReceiver.cs:41-42, 228-229` | Descarte de audio (DropOldest) con PTS por muestra: desfase A/V silencioso y permanente | El audio NO debe descartarse jamás en un grabador: convertir la cola de audio a FullMode.Wait (bloqueante) en lugar de DropOldest,… |
| 8 | Alta | concurrency | `src/Baioss.Record.Infrastructure/Channels/StandaloneChannelEngine.cs:129-189, 250-283` | StartRecordingAsync/StopRecordingAsync de StandaloneChannelEngine sin guarda de reentrada: el estado de sesión… | Añadir un SemaphoreSlim(1,1) propio en StandaloneChannelEngine y envolver TODO el cuerpo de StartRecordingAsync y StopRecordingAsy… |
| 9 | Alta | disk | `src/Baioss.Record.Engine.FFmpeg/FfmpegArgumentBuilder.cs:316-337` | Los segmentos MP4/MOV se escriben SIN flags de robustez (moov al final): un corte eléctrico deja el segmento e… | Aplicar las mismas flags de robustez al muxer del segmento cuando el contenedor es MP4/MOV, pasándolas como opciones del segment_f… |
| 10 | Alta | disk | `src/Baioss.Record.Infrastructure/Storage/DiskSpaceGuard.cs:33, 90-99` | Auto-stop por disco lleno actúa demasiado tarde y no protege con varios canales: el piso de 2 GiB y los 3 min … | Centralizar UNA guarda de disco por volumen que sume el bytesPerSecond de TODOS los canales que graban en él, o como mínimo escala… |
| 11 | Alta | memory-leaks | `src/Baioss.Record.Infrastructure/Capture/NdiReceiver.cs:112-126 (AcceptAsync), 234-245 (DisposeAsync)` | NdiReceiver: el TcpClient aceptado nunca se libera; cada reconexión de FFmpeg fuga socket + NetworkStream | Guardar el TcpClient aceptado y disponer el anterior al reasignar; disponer todos los clientes vivos en DisposeAsync. |
| 12 | Alta | network | `src/Baioss.Record.Api/ApiEndpoints.cs:54-59` | Envíos concurrentes sobre el mismo WebSocket (/ws/events) → InvalidOperationException y caída del stream de ev… | Serializar los envíos por socket con un SemaphoreSlim(1,1) propio de cada conexión WS, y envolver el SendAsync en try/catch para c… |
| 13 | Alta | recording-engine | `src/Baioss.Record.Infrastructure/Channels/StandaloneChannelEngine.cs:129-170` | Doble START de grabación (API/scheduler) huérfana la sesión y el archivo anterior sin avisar | Hacer StartRecordingAsync idempotente/rechazante: si el estado del motor ya es Recording/Starting/Paused, lanzar InvalidOperationE… |
| 14 | Alta | security | `src/Baioss.Record.Api/ApiEndpoints.cs:25-35` | API REST de automatización sin autenticación: cualquier proceso local puede iniciar/detener grabaciones | Implementar IAuthenticationService y exigir un token/clave en /api/v1 antes de salir a producción. Como mínimo inmediato y barato:… |
| 15 | Alta | sources-reconnect | `src/Baioss.Record.App/ChannelHost.cs:100-107` | Elegir/persistir un InputType sin fábrica de captura (SRT/RTMP/UDP/RTP/MpegTs/MediaFoundation) tira el canal a… | Diferenciar NotSupportedException de 'sin señal' y avisar explícitamente: si CaptureSourceResolver.CanHandle(def.Type) es false, r… |
| 16 | Alta | sources-reconnect | `src/Baioss.Record.Infrastructure/Capture/NdiCaptureSource.cs:30-31, 58-62, 64` | NDI nunca reporta pérdida de señal al SignalMonitor: CurrentSignal queda 'Locked' para siempre y no se publica… | Implementar monitorización continua de la señal NDI: el receptor debe poder reportar transiciones (frames llegando / dejando de ll… |
| 17 | Media | architecture-spof | `src/Baioss.Record.App/ChannelHost.cs:190-215` | La construcción de cada canal hace I/O bloqueante (FFmpeg/EF) con .GetAwaiter().GetResult() en el hilo de arra… | Construir los canales en paralelo y de forma asíncrona (Task.WhenAll sobre los keys) fuera del hilo de UI, con un timeout por Open… |
| 18 | Media | av-sync | `src/Baioss.Record.Engine.FFmpeg/FfmpegArgumentBuilder.cs:221, 489-490` | Timecode quemado con rate=25 fijo: deriva en fuentes 29.97/30/50/60 | Derivar rate del drawtext de la tasa real: usar la tasa nominal de salida/fuente (Math.Round del FrameRate, igual que ResolveNomin… |
| 19 | Media | av-sync | `src/Baioss.Record.Infrastructure/Capture/NdiCaptureSource.cs:87-99` | Las dos entradas NDI (vídeo y audio) no comparten origen temporal: offset A/V inicial indeterminado | Alinear el origen temporal de ambas entradas. Opción simple: NO empezar a servir audio en el receptor hasta haber servido el prime… |
| 20 | Media | concurrency | `src/Baioss.Record.Infrastructure/Scheduling/SchedulerService.cs:143-159` | Auto-stop del scheduler compite con el Stop+Rename manual de la UI sobre el mismo canal | Serializar Start/Stop por canal con el guard del hallazgo anterior y, en el scheduler, comprobar Status.RecordingState != Recordin… |
| 21 | Media | cpu-blocking | `src/Baioss.Record.Infrastructure/Channels/StandaloneChannelEngine.cs:416-426 (OnAudioLevels → Raise) y 109-117 (Status) ; consumo redundante en ChannelViewModel.cs:344-362` | OnAudioLevels reconstruye ChannelStatus y despacha a la UI en CADA línea FTPK del medidor (varias veces por se… | No llamar a Raise() desde OnAudioLevels: el audio del preview real ya llega al VM por Preview.AudioPeaksUpdated. Bastaría con actu… |
| 22 | Media | database | `src/Baioss.Record.Infrastructure/Persistence/BaiossDbContext.cs:63-73` | Sin índices en las columnas consultadas de RecordingSession (StartedAt, ChannelId, EndedAt): escaneos completo… | Añadir índices a RecordingSession sobre las columnas consultadas: un índice compuesto (ChannelId, StartedAt) cubre tanto el histor… |
| 23 | Media | disk | `src/Baioss.Record.Infrastructure/Preview/FfmpegChannelEngine.cs:460-470` | En modo segmentado, el último segmento (y el segmento en curso) no se persisten ante un corte eléctrico: se re… | En el arranque/recovery, reconciliar el directorio de cada canal con la BD: por cada archivo de segmento presente que no tenga reg… |
| 24 | Media | frontend-wpf | `src/Baioss.Record.App/Presets/PresetManagerViewModel.cs:69` | PresetManagerViewModel se suscribe a IPresetStore.Changed y NUNCA se desuscribe (fuga de VM y de la lista de c… | Implementar IDisposable en PresetManagerViewModel guardando el handler en un campo (no lambda anónima) para poder hacer `_store.Ch… |
| 25 | Media | frontend-wpf | `src/Baioss.Record.Infrastructure/Channels/StandaloneChannelEngine.cs:416-426` | Tormenta de PropertyChanged: cada actualización de audio re-ejecuta Sync() completo marshalado al Dispatcher | Separar el canal de audio (alta frecuencia) del canal de estado (baja frecuencia). No llamar Raise() desde OnAudioLevels para refr… |
| 26 | Media | frontend-wpf | `src/Baioss.Record.App/ChannelViewModel.cs:271-272` | Los picos de audio se entregan por DOS caminos: OnPreviewAudio y, redundantemente, dentro de Sync() | Unificar: que el motor NO eleve StatusChanged por audio (dejar el audio solo en AudioPeaksUpdated→ApplyAudio). Sync queda reservad… |
| 27 | Media | network | `src/Baioss.Record.Api/ApiEndpoints.cs:57-58` | SendAsync del WebSocket con CancellationToken.None: un cliente lento bloquea la entrega de eventos de dominio … | Acotar cada SendAsync con un CancellationTokenSource con timeout (p. ej. 5 s); ante TimeoutException/excepción, abortar y descarta… |
| 28 | Media | network | `src/Baioss.Record.Api/ApiEndpoints.cs:60-75` | Suscripción WS no se limpia si el cliente desaparece sin handshake de cierre (half-open / cable cortado) | Activar KeepAliveInterval en UseWebSockets y/o aplicar un timeout de inactividad al ReceiveAsync; ante excepción/timeout, salir de… |
| 29 | Media | network | `src/Baioss.Record.Infrastructure/Capture/NdiReceiver.cs:112-126` | NdiReceiver re-acepta conexiones de FFmpeg sin cerrar el TcpClient/NetworkStream anterior: fuga de sockets en … | Guardar el TcpClient activo por flujo y cerrarlo/disponerlo antes de asignar el nuevo; también disponerlos en DisposeAsync. |
| 30 | Media | network | `src/Baioss.Record.Infrastructure/Capture/NdiReceiver.cs:39-42` | Cola de vídeo NDI (DropOldest, profundidad 4) descarta frames bajo backpressure del socket TCP → desync A/V ir… | Contabilizar los frames descartados por la cola (incrementar un contador en el branch DropOldest) y publicar una alarma cuando sup… |
| 31 | Media | recording-engine | `src/Baioss.Record.Infrastructure/Channels/StandaloneChannelEngine.cs:392-401` | DiskCritical auto-stop puede solaparse con un Stop manual/scheduler concurrente (Stop reentrante sin guard de … | Serializar Start/Stop del StandaloneChannelEngine con un SemaphoreSlim propio y/o un guard de estado al inicio de StopRecordingAsy… |
| 32 | Media | sources-reconnect | `src/Baioss.Record.Infrastructure/Capture/NdiCaptureSource.cs:66-99` | Tras recuperar la señal NDI, el pipeline se reconstruye con resolución/pixel del PRIMER frame histórico: si la… | Al reconectar NDI, permitir re-detectar el formato: en el receptor, resetear Width=0 (y demás) cuando se detecte una pérdida prolo… |
| 33 | Media | sources-reconnect | `src/Baioss.Record.Infrastructure/Capture/DecklinkCaptureSource.cs:18-35, 49-53` | La señal DeckLink es un 'lock optimista' fijo: nunca detecta ausencia de señal ni pérdida en caliente (y Raise… | Implementar (o documentar como limitación conocida) un sondeo de presencia de señal DeckLink. Como el dispositivo es exclusivo y n… |
| 34 | Baja | architecture-spof | `src/Baioss.Record.App/App.xaml.cs:103-105` | RetentionService se registra como hosted service pero no hay garantía de aislamiento de fallos frente al resto… | Confirmar que ExecuteAsync de ambos servicios no lanza fuera de su bucle protegido y que cualquier inicialización pesada ocurre de… |
| 35 | Baja | av-sync | `src/Baioss.Record.Engine.FFmpeg/FfmpegArgumentBuilder.cs:275-284` | Conversión de frame rate con -r sobre rawvideo sin timestamps: duplica/descarta frames sin control de campo | Evitar el cambio de tasa sobre entradas entrelazadas sin desentrelazar primero; o restringir OutputFrameRate a múltiplos compatibl… |
| 36 | Baja | concurrency | `src/Baioss.Record.Infrastructure/Channels/StandaloneChannelEngine.cs:341-351, 409-414` | OnEngineAlarm y OnStats leen/usan _session desde el hilo de stderr de FFmpeg mientras StopRecordingAsync lo po… | Capturar _session en una variable local al inicio del handler bajo el mismo guard que protege Start/Stop, o marcar el campo como v… |
| 37 | Baja | concurrency | `src/Baioss.Record.Infrastructure/Capture/NdiReceiver.cs:132-149` | La cola de vídeo NDI (capacidad 4, DropOldest) no garantiza orden de descarte entre productor y consumidor; OK… | En AcceptAsync, al asignar un nuevo stream guardar y Dispose() el TcpClient/NetworkStream anterior antes de reemplazarlo, o manten… |
| 38 | Baja | concurrency | `src/Baioss.Record.Infrastructure/Messaging/InProcessEventBus.cs:20-34` | InProcessEventBus.PublishAsync invoca handlers EN SERIE: un suscriptor lento bloquea a todos los canales | Despachar los handlers en paralelo (Task.WhenAll) o, para notificaciones (UI/WebSocket), encolar y entregar fire-and-forget en un … |
| 39 | Baja | cpu-blocking | `src/Baioss.Record.Infrastructure/Preview/FfmpegChannelEngine.cs:663-713 (RecoveryLoopAsync cada 5 s → ProbeDeviceAsync, que arranca un proceso FFmpeg con BuildInputArguments())` | ProbeDeviceAsync lanza un SEGUNDO FFmpeg que abre la misma fuente cada 5 s durante el slate; en NDI compite co… | En NDI no sondear con un FFmpeg que se conecta a los mismos puertos del receptor; comprobar la recuperación consultando el estado … |
| 40 | Baja | cpu-blocking | `src/Baioss.Record.App/Preview/D3DImagePreview.cs:64-100 (bucle de Marshal.Copy por fila, líneas 75-77)` | D3DImagePreview.Update copia el frame BGRA fila a fila en el hilo de UI en cada cuadro | Evitar el Flush() por frame (dejar que el siguiente AddDirtyRect/Present sincronice) y, si se observa coste, mover la copia stagin… |
| 41 | Baja | database | `src/Baioss.Record.Infrastructure/Scheduling/SchedulerService.cs:138-162` | El scheduler relee toda la tabla ScheduledJobs abriendo una conexión SQLite nueva cada 1 s (86 400 veces/día) … | Cachear los trabajos en memoria e invalidar la caché en las operaciones CRUD del propio SchedulerService (Schedule/Update/Cancel/S… |
| 42 | Baja | database | `src/Baioss.Record.Infrastructure/Persistence/EfRepositories.cs:127-134` | EventLog tiene tabla, entidad e índice por Timestamp, pero AppendAsync nunca se invoca: no se persiste el regi… | Añadir un suscriptor único al IEventBus (al componer la app) que mapee cada IDomainEvent a un EventLogEntry (Category, Severity, C… |
| 43 | Baja | disk | `src/Baioss.Record.Engine.FFmpeg/FfmpegLocator.cs:150-169` | El temporal del remux faststart (.faststart.ext) queda huérfano si la app cae durante el remux, y no hay limpi… | Al arrancar la app (o al iniciar cada canal), barrer las carpetas de grabación y borrar *.faststart.* huérfanos. El temporal nunca… |
| 44 | Baja | disk | `src/Baioss.Record.Engine.FFmpeg/FfmpegLocator.cs:155-162` | RemuxFaststartAsync reescribe todo el archivo al detener sin comprobar espacio libre: en disco casi lleno el r… | Antes de remuxar, comprobar que el espacio libre del volumen supera el tamaño del archivo (más margen); si no cabe, saltar el remu… |
| 45 | Baja | ffmpeg-config | `src/Baioss.Record.Engine.FFmpeg/FfmpegArgumentBuilder.cs:466-468` | Proxy con scale_cuda sobre frames de software: rama de proxy inconsistente y propensa a fallo | Usar `scale` (software) también para el proxy, coherente con la rama principal, o construir una cadena CUDA completa (hwupload_cud… |
| 46 | Baja | ffmpeg-config | `src/Baioss.Record.Engine.FFmpeg/FfmpegArgumentBuilder.cs:255, 153, 614, 437` | ebur128=peak=true duplicado (medidores) por canal sin necesidad cuando solo se requiere el nivel para VU | Quitar `peak=true` si el VU solo muestra nivel/loudness; o usar `ebur128=metadata=1` sin true-peak. Reservar el true-peak para una… |
| 47 | Baja | ffmpeg-config | `src/Baioss.Record.Engine.FFmpeg/FfmpegArgumentBuilder.cs:212-236` | Re-escalado y conversión format= permanentes en grabación NDI aun cuando no hay cambio de resolución | Cuando exista pista NDI con vídeo en uyvy422 y se grabe con NVENC, considerar `-hwaccel_output_format` nativo o subir los frames a… |
| 48 | Baja | ffmpeg-config | `src/Baioss.Record.Engine.FFmpeg/FfmpegArgumentBuilder.cs:37-39, 207, 255` | Filtros de análisis (blackdetect/freezedetect/silencedetect) por CPU activos siempre y sin posibilidad real de… | Como está sobre la rama de preview a 360p la mitigación ya existe (resolución reducida). Conviene aun así exponer WithSignalAnalys… |
| 49 | Baja | ffmpeg-config | `src/Baioss.Record.Engine.FFmpeg/FfmpegCodecMap.cs:74-80 (HwAccelInput) y FfmpegArgumentBuilder.cs:202,207-235` | -hwaccel_output_format cuda con filtros por software (split/scale/format) en la misma cadena: FFmpeg aborta en… | No emitir `-hwaccel_output_format cuda` cuando el grafo de filtros es de software (que es siempre en BuildLive). Basta con `-hwacc… |
| 50 | Baja | frontend-wpf | `src/Baioss.Record.App/ShellViewModel.cs:72-118` | RefreshTodayTasks es async void disparado por un DispatcherTimer y por eventos, sin guardia de reentrancia | Añadir una guardia de reentrancia (un bool _refreshing o un SemaphoreSlim(1,1) con TryWait) para coalescer refrescos solapados; op… |
| 51 | Baja | frontend-wpf | `src/Baioss.Record.App/ShellViewModel.cs:139-174` | Ventanas hijas abiertas con .Show() sin disponer su ViewModel: retienen snapshots de los canales | Estandarizar: cuando un VM de ventana se suscribe a un servicio singleton, hacerlo IDisposable y disponerlo en window.Closed. Para… |
| 52 | Baja | frontend-wpf | `src/Baioss.Record.App/ChannelView.xaml.cs:69-80` | Carrera entre hilos en el patrón «último frame» del preview: puede CONGELARSE de forma permanente | Marcar `_renderQueued` y `_pending` como `volatile` (o usar Interlocked.Exchange para el flag) y/o serializar el acceso con un loc… |
| 53 | Baja | memory-leaks | `src/Baioss.Record.Infrastructure/Preview/FfmpegChannelEngine.cs:652-661 (Start/StopRecoveryProbe), 165-207 (await probe), 853-874 (DisposeAsync)` | FfmpegChannelEngine: _recoveryCts y _awaitCts se reasignan/cancelan pero nunca se disponen (fuga de Cancellati… | Disponer el CTS al detener cada probe y al rotar, igual que ya se hace con _segScanCts. |
| 54 | Baja | recording-engine | `src/Baioss.Record.Engine.FFmpeg/FfmpegRecorderEngine.cs:87-94` | FfmpegRecorderEngine.StopAsync cierra el proceso con kill abrupto (sin enviar 'q'), corrompiendo el contenedor | Si FfmpegRecorderEngine es código muerto, eliminarlo para evitar que se resucite con la deuda. Si se mantiene, replicar la verific… |
| 55 | Baja | recording-engine | `src/Baioss.Record.Infrastructure/Preview/FfmpegChannelEngine.cs:402-408` | El watchdog cuenta el progreso de stdout, pero ese progreso existe también en preview; un encoder de salida co… | Complementar el watchdog de progreso con un chequeo periódico de que el tamaño del archivo de salida (o el último segmento) crece … |
| 56 | Baja | security | `src/Baioss.Record.Engine.FFmpeg/FfmpegArgumentBuilder.cs:709-724` | StreamTarget.Url se concatena crudo en el target del muxer tee (inyección de ramas/opciones FFmpeg) | Validar/escapar StreamTarget.Url al construir el target tee: rechazar URLs que contengan `/`, `[` o `]`, y exigir que el esquema c… |
| 57 | Baja | security | `src/Baioss.Record.Api/ApiEndpoints.cs:44-45` | Endpoint /storage acepta un volume arbitrario del llamador y consulta cualquier ruta del sistema | Validar `volume` contra una lista blanca de volúmenes/raíces de grabación configurados, y envolver la consulta en try/catch devolv… |
| 58 | Baja | security | `src/Baioss.Record.Api/ApiEndpoints.cs:50-61` | WebSocket /ws/events emite TODOS los eventos de dominio sin autenticación a cualquier cliente local | Aplicar la misma autenticación que la REST al WebSocket y, opcionalmente, filtrar/serializar solo los campos necesarios para la UI… |
| 59 | Baja | sources-reconnect | `src/Baioss.Record.Infrastructure/Preview/FfmpegChannelEngine.cs:687-713` | El sondeo de recuperación lanza un FFmpeg que se reconecta al receptor NDI y puede robar el stream al pipeline… | Documentar/forzar la invariante de un único cliente por puerto: rechazar (cerrar) un segundo cliente entrante mientras haya uno ac… |

