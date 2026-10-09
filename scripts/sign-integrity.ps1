#Requires -Version 5.1
<#
.SYNOPSIS
    Genera (o solo comprueba) el manifiesto de integridad de la carpeta publicada.

.DESCRIPTION
    La app recalcula al arrancar el SHA-256 de sus ensamblados sensibles (App.exe, App.dll, Application,
    Infrastructure y Engine.FFmpeg) y lo compara con «baioss-record.integrity», un manifiesto FIRMADO con la
    clave privada del proveedor (la misma de las licencias). Una DLL parcheada se detecta y la licencia deja de
    aplicarse; sin manifiesto, o con un fallo al comprobar, la app arranca y graba igual («sin verificar»).

    Es el ÚLTIMO paso de la publicación: debe correr sobre los bytes FINALES (después de scripts\obfuscate.ps1 y,
    si se firman digitalmente los binarios, después de firmarlos; una firma posterior cambia los bytes y el
    manifiesto dejaría de cuadrar). Lo invocan obfuscate.ps1 y build-installer.ps1; también sirve a mano.

    Envuelve a la herramienta del proveedor: tools\Baioss.Record.LicenseTool («baioss-license integrity …»), que
    verifica el manifiesto al instante con la clave pública compilada en la app y no deja nada si no cuadra.

.PARAMETER PrivateKey
    Clave privada del proveedor en Base64 (la de «baioss-license keygen»). Si se omite se toma de la variable de
    entorno BAIOSS_LICENSE_PRIVATE_KEY (así no queda en el historial de la consola).

.PARAMETER VerifyOnly
    No firma: solo comprueba el manifiesto que ya haya (como lo hará la app) y falla si no está intacto.

.PARAMETER PublishDir
    Carpeta publicada. Por defecto .\publish\.

.EXAMPLE
    .\scripts\sign-integrity.ps1 -PrivateKey <clave>
    .\scripts\sign-integrity.ps1 -VerifyOnly
#>
[CmdletBinding()]
param(
    [string]$PrivateKey = $env:BAIOSS_LICENSE_PRIVATE_KEY,
    [switch]$VerifyOnly,
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PublishDir) { $PublishDir = Join-Path $repoRoot 'publish' }
$tool = Join-Path $repoRoot 'tools\Baioss.Record.LicenseTool\Baioss.Record.LicenseTool.csproj'

if (-not (Test-Path (Join-Path $PublishDir 'Baioss.Record.App.exe'))) {
    Write-Error "No hay una publicación en '$PublishDir'. Ejecuta primero scripts\publish.ps1."
}

if (-not $VerifyOnly) {
    if (-not $PrivateKey) {
        Write-Error 'Falta la clave privada: indica -PrivateKey <clave> o define $env:BAIOSS_LICENSE_PRIVATE_KEY (o usa -VerifyOnly).'
    }
    Write-Host '== Baioss Record - manifiesto de integridad: firma ==' -ForegroundColor Cyan
    # La herramienta se compila contra el MISMO código fuente de Application que lleva la app: mismo formato y la
    # misma clave pública con la que verifica al instante lo que acaba de firmar.
    # OJO: nada de «--nologo» aquí: «dotnet run» no lo reconoce y se lo pasaría a la herramienta como primer argumento.
    & dotnet run --project $tool -c Release -v q -- integrity sign --dir $PublishDir --private $PrivateKey
    if ($LASTEXITCODE -ne 0) { Write-Error "No se pudo generar el manifiesto de integridad (código $LASTEXITCODE)." }
}

Write-Host '== Baioss Record - manifiesto de integridad: verificación ==' -ForegroundColor Cyan
& dotnet run --project $tool -c Release -v q -- integrity verify --dir $PublishDir
switch ($LASTEXITCODE) {
    0 { Write-Host '  verificado: la carpeta publicada coincide con el manifiesto firmado.' -ForegroundColor Green }
    2 { Write-Error 'La carpeta publicada está MODIFICADA respecto al manifiesto: vuelve a firmarlo (sin -VerifyOnly) sobre los bytes finales.' }
    3 { Write-Error 'La carpeta publicada no tiene un manifiesto verificable: genéralo con -PrivateKey <clave> (o $env:BAIOSS_LICENSE_PRIVATE_KEY).' }
    default { Write-Error "La verificación del manifiesto falló (código $LASTEXITCODE)." }
}
