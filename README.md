# Baioss Record

Software profesional de grabación broadcast de **N canales independientes** (configurable, 1–8)
para Windows, construido para entornos 24/7 (master control, productoras, ingest, estudios).

- **.NET 8 · WPF (MVVM) · Clean Architecture · CQRS · DI**
- **FFmpeg** como motor (NVENC/NVDEC/AV1, AMF, QuickSync, DeckLink, SRT, NDI)
- **SQLite** local / **PostgreSQL** empresarial · **Serilog** · API REST + WebSocket

> Los canales (A, B, C, …) son totalmente independientes: cada uno con su propia fuente,
> su propio proceso FFmpeg, su propio watchdog y su propia sesión. La caída de uno
> nunca afecta al otro.

---

## Robustez y operación 24/7

Capacidades de fiabilidad y validación integradas en el motor de grabación:

**Grabación a prueba de fallos**
- **Archivos siempre reproducibles**: MP4/MOV se graban como **fMP4 fragmentado** (`+frag_keyframe+empty_moov` + `-frag_duration`); un corte abrupto nunca deja un archivo «sin códecs» (el problema clásico de `+faststart` + kill). Cierre de proceso endurecido: espera el flush del contenedor en vez de matar a mitad.
- **Verificación post-cierre**: cada archivo/segmento se sondea con `ffprobe` al cerrarse; si no es reproducible (sin pistas/duración), se eleva una alarma crítica.
- **Fallback de codificador por canal**: si el codificador por GPU no abre en caliente (p. ej. sesiones NVENC agotadas con varios canales), ese canal degrada solo **NVENC → QuickSync → AMF → CPU (libx264)** y sigue grabando, sin afectar a los demás.

**Validaciones pre-vuelo (antes de grabar)**
- Perfil coherente (bitrate/GOP/resolución par/calidad/audio), carpeta de destino escribible, margen de disco, señal presente y **longitud de ruta** (límite ~260 de Windows).

**Vigilancia en caliente**
- **Frames perdidos**: alarma por saturación sostenida (CPU/GPU/disco).
- **Disco**: estimación de tiempo restante y **auto-stop** antes de llenarlo (sin corromper el archivo).
- **Pérdida de señal**: carta de ajuste (barras SMPTE + silencio) automática para no romper la base de tiempo; reanuda al volver la señal y **escala a alarma crítica** si se prolonga.

**Captura de dispositivos (DirectShow / DeckLink / NDI)**
- Nombres de dispositivo en **UTF-8** (acentos correctos; antes el mojibake hacía fallar a dshow con error -5).
- **Sincronización A/V**: vídeo + audio de dispositivos distintos se sellan con reloj de pared (`-use_wallclock_as_timestamps`) para que el preview no se congele.
- **Exclusividad**: una misma cámara/tarjeta no puede asignarse a dos canales a la vez (aviso claro en vez de fallo silencioso).
- **NDI** (NewTek): captura nativa de fuentes NDI de la red mediante el **SDK NDI** (paquete `NDILibDotNetCoreBase`). La app recibe el vídeo+audio NDI y los pasa a FFmpeg por sockets locales (vídeo `rawvideo` + audio `f32le`), con descubrimiento automático de fuentes en el gestor de entradas. Requiere el **NDI Runtime** instalado (incluido en NDI Tools); su `.dll` nativo (`Processing.NDI.Lib.x64.dll`) se empaqueta junto al `.exe`. Si el runtime no está, NDI no se ofrece (degrada limpio).

**Persistencia y recuperación**
- **SQLite en modo WAL** + espera ante locks: lecturas del scheduler (cada 1 s) y escrituras de N canales sin «database is locked».
- **Recovery tras crash**: al arrancar se cierran las sesiones que quedaron «grabando» de un cierre abrupto.
- **Retención automática** (opt-in): borra/archiva grabaciones más antiguas que N días mediante un servicio de fondo.
- **Programación**: inmune a cambios de hora/DST (`DateTimeOffset`); reanuda una grabación si se reinicia dentro de su ventana y avisa de las franjas perdidas mientras la app estuvo apagada.

## Configuración (`appsettings.json`)

```jsonc
{
  "Channels": { "Count": 4 },     // nº de canales A, B, C, … (1–8)
  "Retention": {                  // retención automática (opcional, DESACTIVADA por defecto)
    "Enabled": false,
    "Days": 30,
    "Action": "Delete",           // o "Archive" + "ArchivePath": "D:\\Archivo"
    "IntervalHours": 6
  }
}
```

> En el binario publicado, `Channels:Count` va **fijo** (no editable junto al `.exe`). En desarrollo (Debug) puede sobrescribirse con un `appsettings.json` junto al ejecutable.

---

## Documentación de diseño

| # | Documento | Contenido |
|---|-----------|-----------|
| 01 | [Arquitectura](docs/01-arquitectura.md) | Capas, módulos, regla de dependencias, mapa tecnológico |
| 02 | [Estructura de carpetas](docs/02-estructura-carpetas.md) | Árbol de la solución y responsabilidad de cada proyecto |
| 03 | [Modelo de datos](docs/03-modelo-datos.md) | Entidades, ERD, esquema SQLite/PostgreSQL, metadata |
| 04 | [Flujos](docs/04-flujos.md) | Grabación, preview, segmentación, pausa, 24/7 |
| 05 | [Resiliencia y GPU](docs/05-resiliencia-y-gpu.md) | Recuperación ante fallos, watchdog, estrategia GPU |
| 06 | [API y seguridad](docs/06-api-seguridad.md) | REST, WebSocket, roles y auditoría |
| 07 | [Roadmap](docs/07-roadmap.md) | MVP → Enterprise por fases |
| 08 | [Presets de grabación](docs/08-presets.md) | Presets de encoding (Marsis-style): UI 3 paneles, catálogo, JSON |
| 09 | [Publicación / Release](docs/09-release.md) | Empaquetado portable self-contained (win-x64), FFmpeg incluido |

---

## Estructura de la solución

```
baioss-record/
├─ Directory.Build.props          # Propiedades compartidas (net8.0, nullable, analyzers)
├─ baioss-record.slnx
├─ docs/                          # Documentación de diseño
└─ src/
   ├─ Baioss.Record.Domain        # Entidades, value objects, enums, eventos (sin dependencias)
   ├─ Baioss.Record.Application    # Casos de uso (CQRS) y PUERTOS (interfaces de módulos)
   ├─ Baioss.Record.Engine.FFmpeg  # Motor FFmpeg: builder de argumentos + supervisor de proceso
   ├─ Baioss.Record.Infrastructure # Persistencia (EF Core), captura, almacenamiento, orquestación
   ├─ Baioss.Record.Api            # REST + WebSocket (automatización)
   └─ Baioss.Record.App            # WPF (MVVM) — composition root + UI broadcast
```

La regla de dependencias apunta **siempre hacia el dominio**:
`App / Api → Infrastructure / Engine → Application → Domain`.
El dominio no conoce a nadie; la infraestructura implementa los puertos de Application.

---

## Requisitos

- Windows 10/11 x64
- .NET 8 SDK
- GPU NVIDIA (recomendado) con drivers recientes para NVENC/NVDEC/AV1
- FFmpeg compilado con NVENC/NVDEC/AMF/QuickSync/DeckLink/SRT/NDI en `tools/ffmpeg/`
- (Opcional) DeckLink Desktop Video; NDI Tools/Runtime

## Compilar y ejecutar

```powershell
dotnet restore
dotnet build -c Release
dotnet run --project src/Baioss.Record.App
```

> El scaffold incluye `PackageReference` que requieren `dotnet restore` con acceso a NuGet.
> Algunos cuerpos de método están marcados con `TODO` donde la integración con SDKs
> nativos (DeckLink, NDI, NVML) es específica del hardware de destino.

## Nota sobre el scaffold WinForms inicial

La carpeta `baioss-record/` contiene el proyecto WinForms por defecto que venía en la
solución. Ya no forma parte de `baioss-record.slnx` y puede eliminarse: el producto usa WPF.
