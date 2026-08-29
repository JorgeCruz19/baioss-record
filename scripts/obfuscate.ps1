#Requires -Version 5.1
<#
.SYNOPSIS
    Ofusca en «.\publish\» los tres ensamblados con lógica sensible (licenciamiento incluido).

.DESCRIPTION
    Ejecuta Obfuscar (dotnet tool local) con build\obfuscar.xml sobre la carpeta ya publicada, y REEMPLAZA
    in situ Application, Infrastructure y Engine.FFmpeg por sus versiones ofuscadas. El resto del paquete
    (App WPF, Domain, dependencias) se deja igual. Pensado para invocarse DESPUÉS de publicar y ANTES de
    compilar el instalador; el flujo normal de desarrollo NO ofusca.

    Requisitos: la publicación debe ser IL normal (sin ReadyToRun): el R2R precompila a nativo y chocaría con
    el IL reescrito. scripts\build-installer.ps1 -Obfuscate ya publica sin R2R.

.EXAMPLE
    .\scripts\obfuscate.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$publishDir= Join-Path $repoRoot 'publish'
$config    = Join-Path $repoRoot 'build\obfuscar.xml'
$outDir    = Join-Path $publishDir '_obfuscated'

$targets = @(
    'Baioss.Record.Application.dll',
    'Baioss.Record.Infrastructure.dll',
    'Baioss.Record.Engine.FFmpeg.dll'
)

Write-Host '== Baioss Record - ofuscación ==' -ForegroundColor Cyan

if (-not (Test-Path (Join-Path $publishDir 'Baioss.Record.App.exe'))) {
    Write-Error "No hay una publicación en '$publishDir'. Ejecuta primero scripts\publish.ps1."
}
foreach ($t in $targets) {
    if (-not (Test-Path (Join-Path $publishDir $t))) { Write-Error "Falta '$t' en '$publishDir'." }
}

# Obfuscar resuelve las rutas relativas del XML respecto al DIRECTORIO ACTUAL: se ejecuta desde la raíz del repo.
Push-Location $repoRoot
try {
    Write-Host 'Ejecutando Obfuscar...' -ForegroundColor Cyan
    & dotnet tool run obfuscar.console -- $config
    if ($LASTEXITCODE -ne 0) { Write-Error "Obfuscar falló con código $LASTEXITCODE." }
} finally { Pop-Location }

# Obfuscar deja los ensamblados ofuscados en publish\_obfuscated: se copian ENCIMA de los originales.
foreach ($t in $targets) {
    $obf = Join-Path $outDir $t
    if (-not (Test-Path $obf)) { Write-Error "Obfuscar no generó '$t' en '$outDir'." }
    Copy-Item $obf (Join-Path $publishDir $t) -Force
    Write-Host ("  ofuscado y reemplazado: {0}" -f $t) -ForegroundColor Green
}

# Verificación objetiva: la cadena de dominio de la licencia NO debe aparecer en claro en el DLL ofuscado.
$appDll = Join-Path $publishDir 'Baioss.Record.Application.dll'
$bytes  = [System.IO.File]::ReadAllBytes($appDll)
$needle = [System.Text.Encoding]::ASCII.GetBytes('BAIOSS-RECORD-LICENSE-v1')
$found  = $false
for ($i = 0; $i -le $bytes.Length - $needle.Length -and -not $found; $i++) {
    $match = $true
    for ($j = 0; $j -lt $needle.Length; $j++) { if ($bytes[$i + $j] -ne $needle[$j]) { $match = $false; break } }
    if ($match) { $found = $true }
}
if ($found) {
    Write-Warning "La cadena de dominio de la licencia AÚN aparece en claro en Application.dll: revisa HideStrings."
} else {
    Write-Host "  verificado: la cadena de dominio de la licencia ya NO aparece en claro." -ForegroundColor Green
}

# La carpeta intermedia no debe viajar en el instalador (el .iss ya excluye la subcarpeta, pero se limpia igual).
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'Ofuscación completada.' -ForegroundColor Cyan
