# Baioss Record

Software profesional de grabación broadcast de **2 canales independientes** para Windows,
construido para entornos 24/7 (master control, productoras, ingest, estudios).

- **.NET 8 · WPF (MVVM) · Clean Architecture · CQRS · DI**
- **FFmpeg** como motor (NVENC/NVDEC/AV1, AMF, QuickSync, DeckLink, SRT, NDI)
- **SQLite** local / **PostgreSQL** empresarial · **Serilog** · API REST + WebSocket

> Canal A y Canal B son totalmente independientes: cada uno con su propia fuente,
> su propio proceso FFmpeg, su propio watchdog y su propia sesión. La caída de uno
> nunca afecta al otro.

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
