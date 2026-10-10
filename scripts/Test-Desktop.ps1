#requires -Version 7
<#
.SYNOPSIS
  Desktop test battery with synthetic input against the published EXE. Do NOT use the computer while it runs.
.DESCRIPTION
  Takes control of the keyboard (Win+H, Escape), focus, clipboard and microphone for several minutes and opens
  test windows. Saves the user's clipboard and restores it at the end of each test that touches it.
  Asks for explicit confirmation before starting. With -Quick it runs a short version.
.EXAMPLE
  ./scripts/Test-Desktop.ps1
  ./scripts/Test-Desktop.ps1 -Quick
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [switch]$Quick,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Exe) { $Exe = Join-Path $root 'artifacts/publish/win-x64/DikTap.exe' }
if (-not (Test-Path $Exe)) { throw "No existe $Exe. Ejecuta ./scripts/Publish.ps1 primero." }
$Exe = (Resolve-Path $Exe).Path

Write-Host ''
Write-Host 'ATENCION: esta bateria toma el control del equipo (teclado sintetico Win+H/Escape, foco, portapapeles, microfono)' -ForegroundColor Yellow
Write-Host 'y abre ventanas de prueba. Cierra o deja quietas tus aplicaciones y NO toques teclado ni raton hasta que termine.' -ForegroundColor Yellow
Write-Host 'Si tocas algo, los resultados no sirven y la prueba se aborta sola al detectar que pierde el foco.' -ForegroundColor Yellow
if (-not $Yes) {
    $answer = Read-Host 'Escribe SI para empezar'
    if ($answer -ne 'SI') { Write-Host 'Cancelado.'; exit 2 }
}

$live = Join-Path $root 'tests/DikTap.LiveCheck/DikTap.LiveCheck.csproj'
dotnet build $live -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'no compila LiveCheck' }
$dll = Join-Path $root 'tests/DikTap.LiveCheck/bin/Release/net10.0-windows/DikTap.LiveCheck.dll'

$cycles = if ($Quick) { 14 } else { 40 }
$soak = if ($Quick) { 20 } else { 80 }
$steps = @(
    @{ Name = 'Captura de audio (ciclos)'; Args = @('audio-cycles', '30') }
    @{ Name = 'Overlay (foco, click-through, posicion)'; Args = @('overlay') }
    @{ Name = 'Portapapeles (snapshot/restauracion)'; Args = @('clipboard') }
    @{ Name = 'Pegado real'; Args = @('paste') }
    @{ Name = 'Win+H en proceso de prueba'; Args = @('winh') }
    @{ Name = 'Primer uso (Configuracion)'; Args = @('first-run', $Exe) }
    @{ Name = 'Desinstalar desde la app: eliminar todo'; Args = @('uninstall', $Exe, 'all') }
    @{ Name = 'Desinstalar desde la app: conservar configuracion'; Args = @('uninstall', $Exe, 'keep') }
    @{ Name = "Soak Win+H/Escape ($soak iteraciones)"; Args = @('winh-soak', $Exe, "$soak") }
    @{ Name = "E2E del EXE publicado ($cycles ciclos)"; Args = @('e2e', $Exe, "$cycles") }
)

$results = @()
foreach ($s in $steps) {
    Write-Host ''
    Write-Host ("=== {0} ===" -f $s.Name) -ForegroundColor Cyan
    dotnet $dll @($s.Args)
    $results += [pscustomobject]@{ Prueba = $s.Name; Codigo = $LASTEXITCODE; Resultado = if ($LASTEXITCODE -eq 0) { 'OK' } else { 'FALLO/ABORTADO' } }
}

Write-Host ''
Write-Host '=== Resumen ===' -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-Host
Write-Host 'Capturas en tests/DikTap.LiveCheck/bin/Release/net10.0-windows/out/'
if ($results | Where-Object Codigo -ne 0) { exit 1 }
