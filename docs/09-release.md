# 09 · Publicación / Release

Cómo generar un paquete de **Baioss Record** listo para llevar a un equipo de emisión y que
funcione sin instalar nada más.

## Modelo de distribución

**Carpeta portable, self-contained (win-x64).** El paquete incluye el runtime de .NET 8, WPF y
ASP.NET Core, así que **no requiere instalar ningún runtime** en el equipo destino (Windows 10/11
x64). Los binarios de FFmpeg (`ffmpeg.exe` + `ffprobe.exe`; `ffplay.exe` no se usa) y el clip demo
se copian dentro de la carpeta, en `tools\`, junto al ejecutable.

```
publish\
├─ Baioss.Record.App.exe        ← ejecutable
├─ *.dll                        ← .NET 8 + WPF + ASP.NET Core (self-contained) + dependencias
├─ tools\
│  ├─ ffmpeg\ffmpeg.exe
│  ├─ ffmpeg\ffprobe.exe
│  └─ test\clip.mp4             ← fuente demo / fallback
├─ data\        (se crea al arrancar)  ← SQLite (baioss.db) + presets.json
├─ logs\        (se crea al arrancar)  ← Serilog (baioss-AAAAMMDD.log)
└─ recordings\  (se crea al grabar)    ← salidas por canal (A\, B\)
```

Al arrancar, la app sube desde el `.exe` buscando `tools\` (`App.FindUpwards`); la carpeta que lo
contiene es la **raíz**, y ahí ancla `data\`, `logs\` y `recordings\`.

## Pasos

### Opción A — Script (CLI)

Desde la raíz del repo:

```powershell
.\scripts\publish.ps1
```

Limpia `publish\`, publica self-contained win-x64 con ReadyToRun, empaqueta FFmpeg + clip y verifica
el resultado. Variantes:

```powershell
.\scripts\publish.ps1 -FrameworkDependent   # paquete ligero (requiere runtimes instalados, ver abajo)
.\scripts\publish.ps1 -NoReadyToRun          # publish más rápido, arranque algo más lento
```

### Opción B — Visual Studio

Clic derecho en **Baioss.Record.App** → **Publicar** → perfil **win-x64**
(`Properties\PublishProfiles\win-x64.pubxml`). Genera la misma carpeta `publish\`.

### Opción C — comando manual

```powershell
dotnet publish src\Baioss.Record.App -c Release -r win-x64 --self-contained=true -o publish -p:PublishReadyToRun=true
```

> El target `BundleToolsOnPublish` del `.csproj` copia FFmpeg y el clip **solo al publicar** (no afecta
> a `dotnet build`). Si `tools\ffmpeg\ffmpeg.exe` no está presente (está en `.gitignore`, se distribuye
> aparte), el publish **avisa** pero no falla: el release quedaría en modo simulado hasta copiarlos.

## Instalación en el equipo destino

1. Copia/extrae la carpeta `publish\` completa a una ubicación **con permiso de escritura** —por
   ejemplo `C:\Baioss\`—. **No** la pongas en `C:\Program Files\`: la app corre como `asInvoker`
   (sin privilegios de administrador) y escribe `data\`, `logs\` y `recordings\` junto al `.exe`.
2. Ejecuta `Baioss.Record.App.exe`.
3. Comprueba en `logs\baioss-*.log` la línea `API REST + WebSocket escuchando en http://127.0.0.1:5005`
   y, si hay GPU NVIDIA, que el encoder seleccionado es NVENC (si no, cae a libx264 automáticamente).

> La API de automatización escucha en `127.0.0.1:5005` (solo loopback). Es una app de **instancia
> única**: no abras dos copias a la vez o la segunda fallará al enlazar ese puerto.

## Alternativa: framework-dependent

Paquete mucho más pequeño (sin runtime embebido), pero el equipo destino debe tener instalados el
**.NET 8 Desktop Runtime** y el **ASP.NET Core 8 Runtime** (win-x64). Útil si controlas el parque de
máquinas. Genéralo con `.\scripts\publish.ps1 -FrameworkDependent`.

## Versionado

La versión sale de `Directory.Build.props` (`<Version>`). Para etiquetar un release, súbela ahí (y, si
quieres, en `src\Baioss.Record.App\app.manifest`) antes de publicar.

## Notas

- **Trimming desactivado** a propósito: WPF usa reflexión y un publish *trimmed* se rompería.
- **ReadyToRun activado** por defecto: precompila a código nativo para un arranque más rápido (relevante
  en un equipo de emisión); no cambia el comportamiento.
- La carpeta `publish\` está en `.gitignore`.
