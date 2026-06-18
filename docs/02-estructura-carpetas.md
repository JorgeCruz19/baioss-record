# 02 · Estructura de carpetas

```
baioss-record/
├─ Directory.Build.props
├─ baioss-record.slnx
├─ README.md
├─ docs/
│
├─ src/
│  ├─ Baioss.Record.Domain/
│  │  ├─ Enums.cs                      # InputType, VideoCodec, RecordingState, …
│  │  ├─ ValueObjects/
│  │  │  ├─ MediaPrimitives.cs         # Resolution, FrameRate, Bitrate
│  │  │  └─ Timecode.cs                # SMPTE timecode (drop / non-drop)
│  │  ├─ Entities/
│  │  │  ├─ Channel.cs
│  │  │  ├─ InputSource.cs
│  │  │  ├─ RecordingProfile.cs        # + SegmentationPolicy, ProxyProfile, StreamTarget
│  │  │  ├─ RecordingSession.cs
│  │  │  ├─ Segment.cs
│  │  │  ├─ ScheduledJob.cs            # + RetentionPolicy
│  │  │  ├─ EventLogEntry.cs
│  │  │  └─ User.cs
│  │  └─ Events/DomainEvents.cs
│  │
│  ├─ Baioss.Record.Application/
│  │  ├─ Abstractions/                 # Cqrs, IClock, IEventBus, IFfmpegLocator
│  │  ├─ Capture/ICaptureSource.cs     # + ISignalMonitor, IDeviceEnumerator
│  │  ├─ Recording/IRecorderEngine.cs  # + ISegmenter, ISnapshotService, IProxyGenerator
│  │  ├─ Preview/IPreviewEngine.cs
│  │  ├─ Streaming/IStreamingPublisher.cs
│  │  ├─ Storage/IStorageManager.cs
│  │  ├─ Scheduling/ISchedulerService.cs
│  │  ├─ Monitoring/IPerformanceMonitor.cs
│  │  ├─ Metadata/IMetadataExporter.cs
│  │  ├─ Channels/IChannelEngine.cs    # + IChannelManager, ChannelStatus
│  │  ├─ Persistence/IRepositories.cs
│  │  ├─ Security/IAuthenticationService.cs
│  │  └─ UseCases/
│  │     ├─ Recording/StartRecording.cs, StopRecording.cs
│  │     └─ Queries/GetChannelStatus.cs
│  │
│  ├─ Baioss.Record.Engine.FFmpeg/
│  │  ├─ FfmpegCodecMap.cs             # enums dominio → encoders/muxers FFmpeg
│  │  ├─ FfmpegArgumentBuilder.cs      # argv: decode→split→tee(record+stream)→proxy
│  │  ├─ FfmpegProgressParser.cs       # -progress pipe:1 → RecorderStats
│  │  ├─ FfmpegProcessSupervisor.cs    # watchdog + reinicio con backoff (resiliencia 24/7)
│  │  └─ FfmpegRecorderEngine.cs       # IRecorderEngine
│  │
│  ├─ Baioss.Record.Infrastructure/
│  │  ├─ Channels/ChannelEngine.cs     # orquestador (centro del flujo de grabación)
│  │  ├─ Channels/ChannelManager.cs
│  │  ├─ Capture/DecklinkCaptureSource.cs   # (+ NDI, SRT, RTMP, File, DShow)
│  │  ├─ Storage/StorageManager.cs
│  │  ├─ Persistence/BaiossDbContext.cs
│  │  ├─ Messaging/                     # IEventBus in-process
│  │  ├─ Monitoring/ · Scheduling/ · Metadata/ · Security/ · Preview/
│  │  └─ DependencyInjection.cs        # AddInfrastructure(...)
│  │
│  ├─ Baioss.Record.Api/
│  │  └─ ApiEndpoints.cs               # MapBaiossApi(): REST + /ws/events
│  │
│  └─ Baioss.Record.App/
│     ├─ app.manifest                  # DPI PerMonitorV2 + long paths
│     ├─ App.xaml(.cs)                 # composition root: Host + Serilog + DI
│     ├─ MainWindow.xaml(.cs)          # shell de dos canales (docking)
│     ├─ ChannelViewModel.cs           # MVVM (CommunityToolkit.Mvvm)
│     ├─ Views/                        # ChannelView, PreviewControl, Scopes, Meters
│     ├─ Controls/                     # VU/Peak meter, waveform, vectorscope, histograma
│     └─ Theme/                        # DarkBroadcast.xaml (paletas, estilos)
│
└─ tests/
   ├─ Baioss.Record.UnitTests/         # value objects, builder de argumentos, parser
   └─ Baioss.Record.IntegrationTests/  # FFmpeg real con clips de prueba, EF Core SQLite
```

## Responsabilidad por proyecto

| Proyecto | Responsabilidad | Depende de |
|----------|-----------------|-----------|
| `Domain` | Modelo puro de negocio. Sin I/O ni frameworks. | — |
| `Application` | Casos de uso (CQRS) y puertos de cada módulo. | Domain |
| `Engine.FFmpeg` | Construcción y supervisión de procesos FFmpeg. | Application, Domain |
| `Infrastructure` | Persistencia, captura, storage, orquestación de canales, DI. | Application, Engine, Domain |
| `Api` | Superficie REST/WebSocket para automatización. | Application, Infrastructure |
| `App` | UI WPF y composition root del host. | todas |

## Por qué Engine.FFmpeg está separado de Infrastructure

Aislar el motor permite (a) testear el `FfmpegArgumentBuilder` sin tocar disco ni red,
(b) sustituir o versionar el motor (p. ej. una variante GStreamer o un wrapper nativo)
sin reescribir el resto de la infraestructura, y (c) mantener la lógica de resiliencia
del proceso en un único lugar reutilizable por grabación, proxy y re-streaming.
