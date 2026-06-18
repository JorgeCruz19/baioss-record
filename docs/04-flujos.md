# 04 · Flujos y casos de uso

## Casos de uso principales

| Actor | Caso de uso | Comando/Query (CQRS) |
|-------|-------------|----------------------|
| Operador | Vincular fuente a un canal | `BindSource` |
| Operador | Iniciar / detener grabación | `StartRecordingCommand` / `StopRecordingCommand` |
| Operador | Pausar / reanudar | `PauseRecording` / `ResumeRecording` |
| Operador | Capturar screenshot | `CaptureSnapshotCommand` |
| Supervisor | Programar grabación | `ScheduleJobCommand` |
| Supervisor | Cambiar perfil/fuente en vivo | `SwitchProfile` / `SwitchSource` |
| Administrador | Definir retención / usuarios | `SetRetentionPolicy` / `ManageUsers` |
| Automatización | Consultar estado / historial | `GetChannelStatusQuery` / `GetRecordingHistoryQuery` |
| Sistema | Recuperar tras fallo | watchdog + `ContinuousRecordingHostedService` |

## Flujo de grabación

```mermaid
sequenceDiagram
    participant UI as WPF / API
    participant CE as ChannelEngine
    participant SRC as ICaptureSource
    participant RE as FfmpegRecorderEngine
    participant SUP as ProcessSupervisor
    participant FF as FFmpeg
    participant BUS as IEventBus / DB

    UI->>CE: StartRecordingAsync(profileId, operator)
    CE->>SRC: (ya vinculada) CurrentSignal
    alt sin lock de señal
        CE-->>BUS: warning "sin lock"
    end
    CE->>CE: crear RecordingSession (StartedAt, StartTimecode)
    CE->>RE: StartAsync(session, profile, source)
    RE->>RE: FfmpegArgumentBuilder.Build()
    RE->>SUP: StartAsync(argv)
    SUP->>FF: spawn (decode GPU → tee → proxy)
    FF-->>SUP: stdout -progress (frame, fps, drop, dup, bitrate)
    SUP-->>RE: ProgressLine
    RE->>RE: parser → RecorderStats
    RE-->>CE: StatsUpdated / StateChanged(Recording)
    CE-->>BUS: RecordingStarted + persistir sesión
    CE-->>UI: StatusChanged(Recording, stats)
```

`Stop` invierte el flujo: `Supervisor` envía `q` por stdin (cierre ordenado que finaliza
los contenedores), se sella `EndedAt`/`EndTimecode`, se persiste la sesión y se publica
`RecordingStopped`.

## Flujo de preview

```mermaid
sequenceDiagram
    participant UI as ChannelView (WPF)
    participant CE as ChannelEngine
    participant PV as IPreviewEngine
    participant GPU as D3D11 / FFmpeg scopes

    UI->>CE: StartPreviewAsync()
    CE->>PV: StartAsync(source)
    PV->>GPU: decode baja latencia + filtros (waveform/vectorscope/histograma)
    GPU-->>PV: textura D3D11 compartida (shared handle)
    PV-->>UI: SharedTextureHandle → D3DImage
    loop por frame
        GPU-->>PV: AudioLevels (peak/rms/clip)
        PV-->>UI: AudioLevelsUpdated (VU/Peak meter)
    end
```

Claves de baja latencia: el preview usa su **propia ruta** de decode (no depende del encoder
de grabación), render por GPU con textura compartida (cero copia a CPU) y buffers mínimos.
Modos: `Preview`, `Program`, `Fullscreen`. Overlays: safe area, timecode, frame counter.

## Flujo de segmentación

```mermaid
flowchart TD
    A["FFmpeg muxer segment dentro de tee"] -->|segment_time / strftime| B{"¿llegó el umbral?<br/>duración · tamaño · borde horario"}
    B -- sí --> C["cierra archivo N<br/>reset_timestamps"]
    C --> D["abre archivo N+1<br/>en keyframe (GOP cerrado)"]
    C --> E["log 'Opening … for writing'"]
    E --> F["RecorderEngine emite SegmentClosed"]
    F --> G["materializa Segment (Index, path, tc, size)"]
    G --> H["actualiza índice .index.json<br/>continuidad"]
    H --> I["IEventBus: SegmentCompleted"]
    B -- no --> A
```

Disparadores soportados (`SegmentationPolicy.Trigger`):

- **Duration** — `-segment_time` (p. ej. 900 s = 15 min, o 3600 s = 1 h).
- **Size** — corte por tamaño (p. ej. 5 GB) vía monitorización + `segment` con límite.
- **WallClock** — alineado a bordes de reloj con `strftime` (archivo nuevo en cada hora en punto).
- **Manual / Event** — corte forzado por operador o por la API/automatización.

El **GOP cerrado** garantiza que cada segmento empiece en keyframe → archivos independientes,
editables y sin dependencia del segmento anterior. `reset_timestamps=1` deja cada archivo con
timeline propia desde 00:00.

## Flujo de pausa / reanudación

FFmpeg no soporta pausa nativa. Semántica adoptada (file recording):

```mermaid
flowchart LR
    P["Pause"] --> C1["cierra segmento actual<br/>(flush + finalize)"]
    C1 --> S["estado = Paused<br/>(preview sigue activo)"]
    S --> R["Resume"]
    R --> N["nuevo segmento<br/>timecode continuo"]
```

La continuidad de timecode se preserva escribiendo el TC de reanudación a partir del último
frame grabado, de modo que el índice de segmentos sigue siendo monotónico.

## Flujo 24/7 con auto-recuperación

```mermaid
sequenceDiagram
    participant SUP as ProcessSupervisor (watchdog)
    participant FF as FFmpeg
    participant CE as ChannelEngine
    participant HS as ContinuousRecordingHostedService

    Note over SUP,FF: caída de encoder / pérdida de señal
    FF--xSUP: exit inesperado o sin progreso > StallTimeout
    SUP->>SUP: backoff exponencial (≤30 s)
    SUP->>FF: respawn
    SUP-->>CE: Restarted(n) → estado Recovering
    FF-->>SUP: progreso → estado Recording
    Note over HS: reinicio del host (corte de energía)
    HS->>HS: al arrancar, lee canales con ContinuousMode
    HS->>CE: re-arma fuente + StartRecording (nuevo segmento)
```

Detalles de la estrategia completa en [05 · Resiliencia y GPU](05-resiliencia-y-gpu.md).
