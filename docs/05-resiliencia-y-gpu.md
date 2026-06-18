# 05 · Resiliencia 24/7 y estrategia de GPU

## Estrategia de recuperación ante fallos

El diseño asume que **fallar es normal**. Cada modo de fallo tiene una defensa explícita:

| Fallo | Detección | Respuesta |
|-------|-----------|-----------|
| Caída del encoder | exit inesperado del proceso | respawn con backoff exponencial (≤30 s), estado `Recovering` |
| Encoder colgado | watchdog: sin `-progress` por `StallTimeout` (10 s) | kill árbol de procesos + respawn |
| Pérdida de señal | `ISignalMonitor` → `SignalLost` | alarma visual/sonora; en 24/7 se sigue intentando, se marca hueco en el índice |
| Reinicio del host / corte de energía | arranque del servicio | `ContinuousRecordingHostedService` re-arma canales `ContinuousMode` |
| Disco lleno | `IStorageManager` estima tiempo restante | `StorageLow` → alarma; opción de rotar a volumen secundario |
| Corrupción de archivo en corte abrupto | contenedor sin finalizar | preferir MXF/TS (recuperables); reparación con `ffmpeg -i partial -c copy` |
| Fallo de destino de streaming | rama `tee` con `onfail=ignore` | la grabación continúa; el streaming se reintenta aparte |

### Capas de defensa

1. **Proceso (Supervisor)** — respawn + watchdog por canal. Es la primera línea y la más rápida.
2. **Canal (ChannelEngine)** — máquina de estados; publica `EncoderFailed`/`RecordingRecovered`;
   persiste el progreso para poder reconstruir la sesión.
3. **Host (HostedServices)** — sobrevive a reinicios del SO. Al arrancar, reconcilia el estado
   deseado (canales que debían estar grabando) con el real y re-arma lo necesario.
4. **Datos (índice + sidecars)** — la verdad de continuidad vive en disco junto a los archivos,
   no solo en memoria; un crash no pierde el mapa de segmentos ya cerrados.

### Garantías de durabilidad

- Contenedores **fragmentados/streamables** (MXF, MPEG-TS, fMP4) para que un corte abrupto
  deje material reproducible hasta el último GOP escrito.
- **Segmentos cortos** (15 min por defecto) acotan la pérdida máxima a un segmento.
- Política **copy-then-delete** en retención/archivado: nunca se borra el origen antes de
  confirmar la copia.
- **Idempotencia** en los HostedServices: re-armar una sesión ya activa no la duplica.

### Watchdog (resumen del código)

`FfmpegProcessSupervisor` ejecuta dos lazos concurrentes:

- `RunWithRestartAsync` — relanza el proceso ante salida inesperada con backoff `min(30s, 0.5s·2^n)`.
- `WatchdogAsync` — si `now - lastProgress > StallTimeout`, mata el árbol de procesos para forzar respawn.

`MaxRestarts = int.MaxValue` en modo 24/7 (reintento indefinido); en modo manual se acota y
se eleva el fallo al operador.

## Estrategia de rendimiento GPU

Objetivo: **dos canales 4K/HEVC concurrentes con CPU bajo** manteniendo baja latencia.

### Principios

1. **Todo en GPU, de extremo a extremo.** Decode (NVDEC) → filtros (`scale_cuda`, `overlay_cuda`)
   → encode (NVENC) sin bajar los frames a memoria de CPU. Las banderas
   `-hwaccel cuda -hwaccel_output_format cuda` mantienen los surfaces en VRAM.
2. **Un encode, múltiples salidas.** El muxer `tee` reparte el stream ya codificado a grabación
   y a cada destino de streaming → se paga NVENC **una sola vez** por canal.
3. **Proxy con escalado en GPU.** `split` + `scale_cuda` genera el proxy sin recodificar el máster
   ni tocar la CPU.
4. **Evitar round-trips innecesarios.** `drawtext`/burn-in de timecode requiere CPU; se aplica solo
   cuando se solicita y solo a la rama que lo necesita (no al proxy).
5. **Sesiones NVENC bajo control.** Las GPUs de consumo limitan sesiones NVENC simultáneas;
   se planifica nº de canales × (grabación + proxy + stream que recodifica) contra ese límite y
   se prefieren GPUs profesionales (sin límite) o reparto entre tarjetas.

### Selección de aceleración por hardware

| Vendor | Decode | Encode | Notas |
|--------|--------|--------|-------|
| NVIDIA | NVDEC | NVENC (H.264/HEVC/AV1) | preferido; AV1 en Ada+; métricas vía NVML |
| Intel | QuickSync | QuickSync | `-hwaccel qsv`; útil como segunda ruta |
| AMD | D3D11VA | AMF | `-hwaccel d3d11va` + encoder AMF |
| CPU (fallback) | sw | x264/x265 | solo si no hay GPU; mayor latencia y CPU |

`HwAccel` en el perfil decide las banderas (`FfmpegCodecMap.HwAccelInput`). Si el encoder
solicitado no está disponible (validado por `IFfmpegLocator.GetAvailableEncodersAsync`), se
degrada de forma controlada y se avisa.

### Preview sin penalizar la grabación

El preview usa una **ruta independiente** de baja latencia y render con textura D3D11
compartida hacia `D3DImage` (cero copias CPU↔GPU↔CPU). Así su cadencia (y un eventual
fullscreen) nunca introduce drops en el encoder de grabación, que es la ruta crítica.

### Presupuesto y monitoreo

`IPerformanceMonitor` publica CPU/RAM/**GPU/VRAM**/Disco/Red y, por canal, fps entrada/salida,
dropped/duplicated frames y buffer health. `PerformanceDegraded` se eleva al cruzar umbrales
(p. ej. VRAM > 90 % o drops sostenidos) para que el operador o la automatización reaccionen
antes de comprometer la grabación.
