# Auditoría de estabilidad 24/7 en grabación — 2026-07-01

Segunda auditoría profunda, enfocada en **estabilidad al grabar 24/7** (la primera es `AUDITORIA-24x7.md`, 59 hallazgos, la mayoría corregidos). Metodología: lectura directa y verificada de la ruta crítica de grabación (`FfmpegChannelEngine`, `FfmpegProcessSupervisor`, `StandaloneChannelEngine`, `ChannelHost`, `SchedulerService`, `ScheduleEvaluator`, `NdiReceiver`, `DiskSpaceGuard`, `App.xaml.cs`) + tres barridos paralelos (persistencia/servicios de fondo, capa WPF, motor FFmpeg/scheduling) cuyas afirmaciones clave se re-verificaron contra el código antes de incluirse. **Todo hallazgo listado tiene escenario de fallo concreto; los no verificados al 100 % se marcan como «plausible».**

Estado: **SIN CORREGIR** (informe). Los IDs son N1…N30 para no chocar con los #NN de la auditoría anterior.

---

## 🔴 CRÍTICOS — pérdida de material grabado

### N1. El reinicio interno del supervisor relanza FFmpeg con el MISMO argv: `-y` + ruta fija ⇒ TRUNCA el archivo en curso
- `FfmpegProcessSupervisor.cs:50-74` — `RunWithRestartAsync` relanza con los `arguments` capturados al arrancar.
- `FfmpegArgumentBuilder.cs:364,372` — el argv fija `-y` + `OutputFilePath` (único) o `-segment_start_number` viejo (segmentado); `ResolvedBase()` sella `DateTime.Now` una sola vez.
- El motor solo intercepta reinicios si `SlateOnSignalLoss == true` (`FfmpegChannelEngine.cs:659`); **con slate OFF el respawn del supervisor es la ruta «normal» de recuperación** y reabre el mismo archivo.
- **Escenarios**: (a) *NDI sin señal + slate OFF*: ffmpeg conecta al receptor (TCP vivo) → abre la salida (**trunca**) → sin datos → stall 30 s → kill → relanza → trunca otra vez… el archivo termina en ~0 bytes. (b) *DShow/DeckLink que pierde y recupera señal*: mientras la entrada está muerta ffmpeg sale sin abrir la salida (sin daño), pero **al volver la señal el relanzamiento abre con `-y` y borra todo lo grabado antes del corte**. (c) *Stall > 30 s con entrada viva* (hipo de disco): watchdog-kill → relanza → trunca. (d) Segmentado con nombre: renumera desde el `start_number` viejo y **pisa los segmentos ya escritos**.
- **Amplificador grave**: `SlateOnSignalLoss` **no se persiste** (`BaiossDbContext.cs:111` `Ignore`) → tras CADA reinicio de la app vuelve a `false` (default) aunque el operador lo active → la única protección contra este escenario casi nunca está activa. Incluso con slate ON hay una carrera estrecha (el supervisor relanza tras ~1 s de backoff; si `EnterSlateAsync` no gana el `_gate` antes, el relanzamiento truncador ocurre primero).
- **Fix**: el supervisor debe recibir una *factory* de args re-invocada por intento (nombre único / `NextSegmentNumber` recalculado), o `MaxRestarts = 0` en el motor unificado y dirigir TODO respawn por `ReplaceProcessAsync` (que ya resuelve nombres únicos). Persistir (o re-sembrar) `SlateOnSignalLoss`.

### N2. Cerrar la app con una grabación en curso corrompe el archivo (config actual MP4 estándar)
- `MainWindow.xaml.cs` — **sin** handler de `Closing` (ni confirmación ni stop previo).
- `App.xaml.cs:300` — `OnExit` es `async void`: WPF no espera; al primer `await` real del desmontaje el proceso sigue su cierre y las continuaciones (cierre ordenado «q» + flush de N canales + `StopAsync` del host) no llegan a ejecutarse.
- El Job Object (C2, `ChildProcessTracker`) entonces **mata todos los FFmpeg al instante** → con `Recording:FragmentedMp4=false` (la elección actual para máquina con UPS) el MP4 queda **sin `moov` = ilegible**. La sesión queda abierta en BD (se recupera como error al siguiente arranque). Nota: con fMP4 esto era benigno (cualquier prefijo era reproducible) — el cambio a MP4 estándar lo expuso.
- **Fix**: interceptar `MainWindow.Closing` → si hay grabaciones, confirmar → `e.Cancel = true` → stop-all asíncrono → `Shutdown()`. Complementario: bloquear en `OnExit` con `GetAwaiter().GetResult()` (seguro: la cadena interna usa `ConfigureAwait(false)`).

### N3. Un fallo a mitad de Start/Stop/salida-de-slate deja el canal MUERTO y BLOQUEADO (sin rollback ni auto-recuperación)
- `FfmpegChannelEngine.ReplaceProcessAsync` (`:306-314`) dispone el supervisor viejo ANTES de construir el nuevo; si algo lanza después (crear carpeta, builder — p. ej. `NdiCaptureSource.BuildInputArguments` lanza si el receptor no abrió —, `Process.Start`), queda `_supervisor = null` (**preview muerto**) y `_state` atascado en `Starting`/`Stopping`.
- Consecuencias en cadena, todas verificadas: el guard anti-doble-START (`StandaloneChannelEngine.cs:148`) responde **«ya está grabando» para siempre**; el botón Detener de la UI queda deshabilitado (solo cuenta `Recording/Paused`); el scheduler martillea el start **cada 1 s** llenando el log; la sesión insertada en BD queda huérfana (State=Recording) hasta el próximo arranque; y en NDI sin emisor el await-loop **fuga un `NdiReceiver` entero (2 listeners TCP + handle nativo) cada 5 s** (`NdiCaptureSource.cs:44` sobreescribe `_receiver` sin disponer el anterior; el loop nunca sale porque exige `_state == Idle`).
- Misma familia: `ExitSlateAsync` pone `_slate=false` ANTES de `ReplaceProcessAsync` y su `catch` solo loguea → si falla, canal «grabando» sin proceso, sonda de recuperación muerta (el `RecoveryLoopAsync` hace `return` incondicional tras un probe OK, dispare o no la salida real). `EnterSlateAsync` simétrico.
- **Fix**: try/catch en `StartRecordingAsync`/`StopRecordingAsync`/`ExitSlateAsync` del motor que restaure un estado coherente (reconstruir el pipeline de preview o volver a slate; resetear `_state`); en `StandaloneChannelEngine`, cerrar/marcar la sesión recién insertada si el motor falla; en el await-loop NDI, disponer el receptor anterior antes de crear otro y no exigir `Idle` eterno.

---

## 🟠 ALTOS

### N4. Programación con offset UTC FIJO: tras un cambio de horario (DST) todas las diarias/semanales disparan 1 hora corridas
- `ScheduleEvaluator.cs:126-131` — `SlotOnDate` construye la franja con `job.RunAt.Offset` (el offset capturado al CREAR el trabajo); `ScheduleViewModel.cs:351/371/378` y `ScheduleValidator` capturan `DateTimeOffset.Now.Offset` de hoy incluso para fechas futuras. Un offset NO es una zona horaria.
- **Escenario**: máquina en zona con DST (Canadá sí; CDMX ya no): una diaria de las 20:00 creada en invierno (−05:00) sigue disparando a las 20:00 **−05:00** = 21:00 hora local en verano — graba la hora equivocada todos los días, sin ningún error, hasta que alguien re-guarda el trabajo.
- **Fix**: guardar hora-del-día local + `TimeZoneInfo` (o asumir `TimeZoneInfo.Local`) y construir cada franja con `tz.GetUtcOffset(fechaLocal)`.

### N5. Si FFmpeg no puede ARRANCAR, el supervisor muere en silencio (sin reintento, sin alarma)
- `FfmpegProcessSupervisor.cs:103` — `_process.Start()` está FUERA del try; una excepción (antivirus/cuarentena, binario bloqueado, agotamiento de handles) mata el `_runLoop` (la faulted task se traga en Dispose) **y también el watchdog** (`HasExited` sobre un proceso no arrancado lanza `InvalidOperationException` dentro de su bucle sin try). El canal cree que graba; nadie reintenta ni alarma.
- **Fix**: envolver `RunOnceAsync` en try/catch dentro de `RunWithRestartAsync` (convertir el fallo de lanzamiento en reintento-con-backoff) y proteger el cuerpo del watchdog.

### N6. Salida limpia (exit 0) de FFmpeg durante la grabación no se maneja: fin silencioso con estado «Grabando»
- `FfmpegProcessSupervisor.cs:57-63` — exit 0 → `Completed` y NO reinicia (correcto para stop); pero `FfmpegChannelEngine` **no suscribe `Completed`/`Exited`** → si el emisor NDI cierra limpio su TCP (EOF) u otra fuente finita termina, ffmpeg finaliza y sale 0: sin reinicio, sin slate, sin alarma; `State` queda `Recording` con stats congeladas. Aire muerto hasta que un operador lo note.
- **Fix**: suscribir `Completed` y, si `_state is Recording/Starting` y no es un stop, tratarlo como `OnSupervisorRestarted` (slate o reconstrucción). Relacionado con el pendiente #55 (watchdog no vigila que el ARCHIVO crezca — cubrirlo mataría ambos pájaros).

---

## 🟡 MEDIOS

### N7. Tick del scheduler 100 % secuencial: cadenas de stop/start a la misma hora se retrasan entre sí y pueden SALTARSE franjas
`SchedulerService.TickAsync:159-199` — los auto-stops y starts se `await`ean uno a uno; cada stop puede tardar hasta 30 s (flush). Con N canales terminando a la misma hora, el inicio del último llega tarde (pérdida de cabecera de programa) y, si el retraso acumulado supera la gracia de 2 min, los starts sin duración y los stops programados **se saltan en silencio**. Fix: despachar por canal en paralelo (el `_transition` por canal ya serializa correctamente) o capturar `now` por trabajo.

### N8. Reasignación de entradas: diccionarios no concurrentes + rebinds concurrentes posibles desde la UI
`ChannelHost._engines/_keys/_sources` y `PreviewCatalog._previews` son `Dictionary` planos, mutados en hilos de pool durante el rebind y leídos por el scheduler (1 Hz), la API (Kestrel) y el health monitor (15 s) → lectura-durante-escritura indefinida (un tick puede abortar o, corrompido el bucket, fallar para siempre). Además cada fila tiene SU `ApplyCommand` (AsyncRelayCommand solo bloquea reentrada de su propia instancia), nada liga `IsEnabled` a `IsBusy` y pueden abrirse varias ventanas de Entradas → **dos rebinds a la vez**: TOCTOU en la exclusividad (dos canales al mismo DeckLink) y, en el mismo canal, el motor perdedor queda huérfano reteniendo el dispositivo. Fix: `SemaphoreSlim(1,1)` global en `RebindAsync`, `ConcurrentDictionary`, `IsEnabled="{Binding !IsBusy}"`.

### N9. El diálogo «nombrar al detener» corre en carrera con el scheduler
`ChannelViewModel.StopAsync:213-230` — el diálogo modal no bloquea al scheduler; si una programada arranca en ese canal con el diálogo abierto: caso único → el nombre elegido se descarta en silencio; caso segmentado → **renombra los segmentos de la NUEVA sesión** en caliente (y corrige sus rutas en BD), y `_sessionFiles` (List sin lock) se muta desde dos hilos. Fix: snapshot de archivos+SessionId ANTES de mostrar el diálogo; `RenameLastRecordingAsync(name, sessionId)`.

### N10. Ventanas de Presets/Configuración general con VMs viejos tras un rebind
`ShellViewModel.OpenPresets/OpenGeneralSettings` pasan `Channels.ToList()` (snapshot); `OnChannelRebound` solo reemplaza en la colección del shell. Con la ventana abierta y un rebind de por medio, «Aplicar preset»/cambiar carpeta escriben en el **motor dispuesto**: la UI dice «aplicado», el motor vivo sigue con el perfil viejo → grabaciones siguientes con códec/bitrate/carpeta equivocados. Fix: resolver por `ChannelId` contra el host en el momento de aplicar, o refrescar esas ventanas en `ChannelRebound`.

### N11. Tabla EventLog sin poda + `StorageLow` publicado cada 5 s (no por transición)
`StandaloneChannelEngine.OnDiskUpdated:444` publica en CADA poll de la guarda mientras el nivel no sea Ok → ~17 280 filas/día/canal en estado Low sostenido (que con el piso ×N canales puede ser «32 GiB libres» en un volumen compartido por 8) + un broadcast WS por evento; nadie poda EventLog jamás. Fix: publicar solo transiciones + poda por edad en `RetentionService`.

### N12. Un `%` en el título de una programada segmentada = bucle de reinicio infinito y CERO grabación
`SanitizeBaseName` solo quita `Path.GetInvalidFileNameChars()` (el `%` es válido en nombres); el patrón del muxer `{base}_%d.mp4` con un `%` extra es una conversión inválida → ffmpeg sale nonzero → el supervisor relanza para siempre; la ocurrencia no graba nada (cada día) y solo hay rastro en el log. Fix: filtrar `%` (o escapar `%%` solo al componer el patrón de segmentos).

### N13. Salto de reloj hacia atrás (corrección NTP) re-dispara una ocurrencia con duración ya completada
`SchedulerService.TryStartAsync:209-216` — el dedupe por `LastRunAt` solo aplica a trabajos SIN duración; los con duración dependen del `_active` en memoria, que se limpia en el auto-stop. Reloj atrasado 3 min tras el auto-stop (o reinicio de la app dentro de la ventana con reloj corregido) → segunda sesión duplicada. Fix: persistir la finalización (p. ej. `LastRunAt = occ` también al auto-stop).

### N14. Archivado de retención puede DESTRUIR grabaciones por colisión de nombres entre canales
`StorageManager.cs:78` — `File.Move(…, Path.Combine(ArchivePath, Path.GetFileName(...)), overwrite: true)`: los nombres programados son `dd-MM-yyyy_Título` (sin canal); dos canales con la misma programada el mismo día → al archivar, el segundo **pisa** el archivo ya archivado del primero, y la fila de sesión se borra → pérdida permanente e invisible. (Solo con Retention+Archive activados — hoy opt-in.) Fix: subcarpeta por canal + nunca `overwrite: true`.

### N15. Guarda de disco MUDA con carpetas de grabación UNC/NAS
`DiskSpaceGuard.ReadDrive:80-87` — `DriveInfo` lanza con rutas UNC → el catch la degrada a log Debug → sin `Updated`, sin niveles, sin auto-stop: grabar a un NAS queda SIN protección de disco y sin aviso. Fix: `GetDiskFreeSpaceEx` (soporta UNC) o alarma explícita «no puedo vigilar este volumen».

### N16. Escaneos O(nº de archivos) en el hot path, con retención OFF por defecto
`SampleRealBitrate`→`DirBytes` (cada ~2 s, EN el hilo del lector de progreso de ffmpeg), `ScanSegments` (cada 2 s, glob amplio `{canal}_*` con nombres por defecto) y `ChannelHealthMonitor.FolderBytes` (15 s) recorren TODA la carpeta del canal. Con meses de 24/7 son decenas de miles de archivos → coste creciente; extremo plausible (no verificado): un walk frío multi-segundo en HDD retrasa el procesado de progreso. Fix: medir solo los archivos de la sesión en curso (contador alimentado por `EmitSegmentFile` + archivo activo).

### N17. El reconciliador de huérfanos corre tras `StartAsync` y puede registrar como huérfano el segmento que la reanudación está ESCRIBIENDO
Orden real: `app.StartAsync()` (scheduler a los +5 s) → `CloseOrphanedAsync` → `ReconcileAsync` (walk recursivo que en bibliotecas grandes tarda > 5 s). La reanudación-en-ventana crea `base_N+1.mp4`; el reconciliador (snapshot viejo) lo da de alta como Completed de la sesión ESTRELLADA; al cerrarse de verdad se emite otra vez → fila duplicada con metadatos inconsistentes. Fix: ignorar archivos con `LastWriteTimeUtc` reciente (~60 s) o reconciliar antes de `StartAsync`.

### N18. Riesgo residual del MP4 estándar elegido: toda muerte abrupta de ffmpeg deja la pieza actual sin `moov`
Con `FragmentedMp4=false`, el stall-kill del watchdog (30 s), un crash del encoder o N1/N2 dejan el trozo en curso ilegible (con fMP4 no pasaba; la UPS solo cubre cortes eléctricos). `VerifyRecordingAsync` lo detecta y alarma post-stop (bien), pero el material se pierde. Mitigación: watchdog que intente `q` 3-5 s antes de `Kill`; para 24/7 largos, preset MKV o segmentación.

### N19. `CanRebind` exige que TODOS los canales sean reales: un canal caído a simulado bloquea el gestor de entradas ENTERO
`ChannelHost.cs:65` — y justo la vía de recuperación de ese canal es reasignarle la entrada… que está deshabilitada. Única salida: reiniciar la app. Fix: permitir rebind por canal (o siempre que el host sea real).

### N20. NDI: cambio de formato sin hueco de presencia (o con slate OFF / solo-preview) = imagen corrupta persistente
El #32 solo re-detecta si hubo pérdida ≥ 5 s Y hay un ReplaceProcess posterior (salida de slate). Si la fuente cambia de resolución al vuelo (vMix 720→1080) o no hay slate, el ffmpeg en marcha —y sus reinicios del supervisor, que reutilizan el MISMO argv— sigue leyendo con el `-video_size` viejo → basura hasta rebind manual.

---

## 🟢 BAJOS

- **N21.** `ChannelHost.RestoreChannelAsync` (código de hoy): al caer a simulado registra `_keys[b.ChannelId]` (Guid nuevo del sim) en vez de `channelId` → VM con id equivocado → tabla de programación vacía y skip a canal inexistente. Fix trivial: `_keys[channelId] = key`.
- **N22.** Scheduler reintenta cada 1 s toda la ventana ante un start que falla persistentemente (perfil inválido, ruta > 240) → miles de errores en log, sin alarma de UI. Fix: N fallos → marcar ocurrencia fallida + alarma.
- **N23.** `FfmpegLocator.RunAsync` (probe/remux) sin timeout ni `Kill` al cancelar → un ffprobe colgado bloquea `_pendingOptimize` hasta el tope de 10 min del rename. (El barrido de `.faststart.*` al arrancar SÍ existe — #43 —, así que los temporales huérfanos se limpian al siguiente inicio.)
- **N24.** El backoff del supervisor nunca se resetea tras un período sano: acumulados 6+ incidentes en semanas, cada reinicio de preview espera 30 s fijos. Fix: resetear `_restartCount` tras X min de progreso continuo.
- **N25.** Tras un rebind, un frame en vuelo de la fuente VIEJA puede empujarse a la superficie nueva (resoluciones distintas) → churn innecesario del dispositivo D3D / excepción tragada; un glitch por rebind. Fix: `if (!ReferenceEquals(sender, _preview)) return;`.
- **N26.** `ChannelViewModel.StopAsync` sin try/catch (asimétrico con `StartAsync`): un fallo al detener/renombrar es invisible para el operador (el handler global C1 lo traga y solo loguea).
- **N27.** El reconciliador solo escanea `*.mp4/*.mov`: huérfanos MKV/TS/MXF (¡y MKV es el contenedor recomendado para largos!) nunca se reconcilian. Fix: derivar patrones de `FfmpegCodecMap`.
- **N28.** La retención borra la fila de sesión de grabaciones single-file estrelladas (0 segmentos en BD) y el ARCHIVO queda en disco para siempre: invisible al historial y a la propia retención. Fix: conservar la sesión-lápida o barrido por edad de no-asociados.
- **N29.** `_pendingOptimize` solo recuerda el ÚLTIMO remux: en multi-pieza fMP4 el rename puede correr en carrera con el remux de una pieza anterior (latente: hoy `FragmentedMp4=false`). Fix: lista de tareas pendientes.
- **N30.** Carrera Stop-durante-EnterSlate: puede quedar un recovery-loop sondeando cada 5 s en Idle (spawn de ffmpeg de sonda) y levantar una alarma SignalLoss espuria; acotado (muere al primer probe OK o al dispose).

---

## Pendientes ya conocidos (auditoría anterior, sin cambios)
A3/A4 (drift A/V NDI bajo CPU), A10 (API sin auth), #19 (offset A/V inicial NDI), #35 (`-r` sobre rawvideo), #39/#59 (la sonda de recuperación NDI compite con el receptor), #47/#48 (optimizaciones ffmpeg), #55 (el watchdog no vigila que el ARCHIVO crezca — implementarlo complementa N1/N6).

## Orden de ataque recomendado
1. **Lote 1 — pérdida de material (urgente)**: N1 (+ persistir/re-sembrar slate), N2, N3 (+ N21 trivial de paso).
2. **Lote 2 — programación fiable**: N7, N4, N13, N22, N12.
3. **Lote 3 — robustez del proceso**: N5, N6 (+ #55), N18 (watchdog con `q` previo), N15.
4. **Lote 4 — concurrencia UI/host**: N8, N9, N10, N26, N25.
5. **Lote 5 — housekeeping de largo plazo**: N11, N16, N17, N14, N19, N20, N23, N24, N27, N28, N29, N30.
