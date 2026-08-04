#Requires -Version 5.1
<#
.SYNOPSIS
    Genera el instalador de Baioss Record (un único .exe con asistente por pasos).

.DESCRIPTION
    Hace las dos cosas en orden:
      1. Publica la aplicación en .\publish\ (self-contained: el equipo destino NO
         necesita instalar ningún runtime), salvo que se indique -SkipPublish.
      2. Compila installer\baioss-record.iss con Inno Setup y deja el instalador
         en .\dist\BaiossRecord-<version>-Setup.exe

    El asistente pregunta al cliente si quiere el periodo de prueba de 14 días o
    activar una licencia, y ofrece iniciar el programa con Windows.

.PARAMETER SkipPublish
    Reutiliza lo que ya haya en .\publish\ en vez de volver a publicar (más rápido
    cuando solo se está ajustando el instalador).

.PARAMETER Version
    Versión que se estampa en el instalador y en el nombre del archivo.

.EXAMPLE
    .\scripts\build-installer.ps1
    .\scripts\build-installer.ps1 -SkipPublish -Version 1.0.1
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$issFile   = Join-Path $repoRoot 'installer\baioss-record.iss'
$publishDir= Join-Path $repoRoot 'publish'
$distDir   = Join-Path $repoRoot 'dist'

Write-Host '== Baioss Record - construcción del instalador ==' -ForegroundColor Cyan

# --- Localiza el compilador de Inno Setup (ISCC) ---
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Error @'
No se encontró Inno Setup (ISCC.exe). Instálalo con:

    winget install --id JRSoftware.InnoSetup

y vuelve a ejecutar este script.
'@
}
Write-Host "Inno Setup: $iscc"

# --- 1) Publicar la aplicación ---
if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $publishDir 'Baioss.Record.App.exe'))) {
        Write-Error "Se indicó -SkipPublish pero no hay una publicación en '$publishDir'."
    }
    Write-Host 'Publicación: se reutiliza la existente (-SkipPublish).' -ForegroundColor Yellow
} else {
    Write-Host 'Publicando la aplicación...' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'publish.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Error 'Falló la publicación de la aplicación.' }
}

# Aviso útil: sin los binarios de FFmpeg el producto instalado arrancaría en modo simulado.
if (-not (Test-Path (Join-Path $publishDir 'tools\ffmpeg\ffmpeg.exe'))) {
    Write-Warning 'No hay ffmpeg.exe en publish\tools\ffmpeg: el instalador se generará, pero el producto instalado NO grabaría (modo simulado).'
}

# --- 2) Compilar el instalador ---
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Write-Host 'Compilando el instalador...' -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" $issFile
if ($LASTEXITCODE -ne 0) { Write-Error 'Falló la compilación del instalador.' }

$setup = Join-Path $distDir "BaiossRecord-$Version-Setup.exe"
if (Test-Path $setup) {
    $mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host ''
    Write-Host "Instalador generado: $setup ($mb MB)" -ForegroundColor Green
    Write-Host ''
    Write-Host 'Antes de distribuirlo conviene FIRMARLO digitalmente; si no, Windows SmartScreen'
    Write-Host 'mostrará una advertencia al cliente al ejecutarlo.'
} else {
    Write-Error "El instalador no apareció en '$setup'."
}
