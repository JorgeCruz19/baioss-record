#Requires -Version 5.1
<#
.SYNOPSIS
    Publica Baioss Record como aplicación de escritorio portable y self-contained (win-x64).

.DESCRIPTION
    Genera en .\publish\ una carpeta autónoma que NO requiere instalar ningún runtime
    en el equipo destino (incluye .NET 8 + WPF + ASP.NET Core). El target
    BundleToolsOnPublish del .csproj copia ffmpeg.exe, ffprobe.exe y el clip demo en
    publish\tools\ junto al .exe, de modo que la app los localiza al arrancar.

    La app escribe data\, logs\ y recordings\ JUNTO al .exe (corre como asInvoker, sin
    admin): instala/extrae la carpeta en una ubicación con permiso de escritura
    (p. ej. C:\Baioss\), NO dentro de C:\Program Files.

.PARAMETER FrameworkDependent
    Publica una variante ligera que SÍ requiere instalar en el destino el .NET 8 Desktop
    Runtime y el ASP.NET Core 8 Runtime. Por defecto se publica self-contained.

.PARAMETER NoReadyToRun
    Desactiva la precompilación ReadyToRun (publish más rápido, arranque algo más lento).

.EXAMPLE
    .\scripts\publish.ps1
    .\scripts\publish.ps1 -FrameworkDependent
#>
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [switch]$NoReadyToRun
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\Baioss.Record.App\Baioss.Record.App.csproj'
$outDir     = Join-Path $repoRoot 'publish'

Write-Host "== Baioss Record · publicación de release ==" -ForegroundColor Cyan
Write-Host "Proyecto: $appProject"
Write-Host "Salida  : $outDir"

if (-not (Test-Path (Join-Path $repoRoot 'tools\ffmpeg\ffmpeg.exe'))) {
    Write-Warning "No se encontró tools\ffmpeg\ffmpeg.exe. El release quedará SIN FFmpeg (modo simulado)."
    Write-Warning "Copia ffmpeg.exe y ffprobe.exe en tools\ffmpeg\ y vuelve a ejecutar."
}

# Limpieza de la salida anterior para un paquete reproducible.
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$selfContained = (-not $FrameworkDependent).ToString().ToLowerInvariant()
$readyToRun    = (-not $NoReadyToRun).ToString().ToLowerInvariant()

$dotnetArgs = @(
    'publish', $appProject,
    '-c', 'Release',
    '-r', 'win-x64',
    "--self-contained=$selfContained",
    '-o', $outDir,
    "-p:PublishReadyToRun=$readyToRun",
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '--nologo'
)

Write-Host "`n> dotnet $($dotnetArgs -join ' ')`n" -ForegroundColor DarkGray
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló con código $LASTEXITCODE." }

# --- Verificación del paquete ---
$exe       = Join-Path $outDir 'Baioss.Record.App.exe'
$ffmpeg    = Join-Path $outDir 'tools\ffmpeg\ffmpeg.exe'
$ffprobe   = Join-Path $outDir 'tools\ffmpeg\ffprobe.exe'
$clip      = Join-Path $outDir 'tools\test\clip.mp4'

function Assert-Exists($path, $label) {
    if (Test-Path $path) { Write-Host ("  [OK]  {0}" -f $label) -ForegroundColor Green }
    else                 { Write-Host ("  [--]  {0} (FALTA)" -f $label) -ForegroundColor Yellow }
}

Write-Host "`n== Verificación del paquete ==" -ForegroundColor Cyan
Assert-Exists $exe     'Baioss.Record.App.exe'
Assert-Exists $ffmpeg  'tools\ffmpeg\ffmpeg.exe'
Assert-Exists $ffprobe 'tools\ffmpeg\ffprobe.exe'
Assert-Exists $clip    'tools\test\clip.mp4'

if (-not (Test-Path $exe)) { throw "El ejecutable no se generó: revisa la salida de publish." }

$sizeMB = [math]::Round(((Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ("`nPaquete listo: {0}  ({1} MB)" -f $outDir, $sizeMB) -ForegroundColor Cyan
Write-Host "Cópialo a una carpeta con permiso de escritura (no Program Files) y ejecuta Baioss.Record.App.exe." -ForegroundColor Cyan
