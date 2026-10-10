#requires -Version 7
<#
.SYNOPSIS
  Repeats the published EXE's E2E with the machine under CPU and memory load. Do NOT use the computer while it runs.
.DESCRIPTION
  Launches processes that occupy all cores (normal priority) and reserve memory (by default 60 % of free RAM,
  with a cap), and runs the E2E (synthetic Win+H, real audio, simulated server, paste) measuring the capture
  start latency and failures. When done (or on Ctrl+C) it always stops the load processes.
  Takes control of the keyboard and clipboard like Test-Desktop.ps1: asks for confirmation.
.EXAMPLE
  ./scripts/Test-UnderLoad.ps1
  ./scripts/Test-UnderLoad.ps1 -CpuProcesses 6 -MemoryMB 4000 -Cycles 20
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$CpuProcesses = [Environment]::ProcessorCount,
    [int]$MemoryMB = 0,
    [int]$Cycles = 20,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Exe) { $Exe = Join-Path $root 'artifacts/publish/win-x64/DikTap.exe' }
if (-not (Test-Path $Exe)) { throw "No existe $Exe. Ejecuta ./scripts/Publish.ps1 primero." }
$Exe = (Resolve-Path $Exe).Path

if ($MemoryMB -le 0) {
    $freeMB = [int]((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1024)
    $MemoryMB = [Math]::Max(256, [Math]::Min([int]($freeMB * 0.6), 8192))
}

Write-Host ''
Write-Host "ATENCION: ocupa $CpuProcesses procesos al 100 % de CPU y $MemoryMB MB de RAM, y ademas toma el control del teclado," -ForegroundColor Yellow
Write-Host 'el foco y el portapapeles durante varios minutos. El equipo ira lento. NO lo uses hasta que termine.' -ForegroundColor Yellow
if (-not $Yes) {
    $answer = Read-Host 'Escribe SI para empezar'
    if ($answer -ne 'SI') { Write-Host 'Cancelado.'; exit 2 }
}

$live = Join-Path $root 'tests/DikTap.LiveCheck/DikTap.LiveCheck.csproj'
dotnet build $live -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'no compila LiveCheck' }
$dll = Join-Path $root 'tests/DikTap.LiveCheck/bin/Release/net10.0-windows/DikTap.LiveCheck.dll'

$load = @()
$code = 1
try {
    $pwsh = (Get-Process -Id $PID).Path
    $burn = '$x=1.0; while($true){ $x=[Math]::Sqrt($x+1.5) * 1.0001 }'
    for ($i = 0; $i -lt $CpuProcesses; $i++) {
        $load += Start-Process $pwsh -ArgumentList '-NoProfile', '-Command', $burn -WindowStyle Hidden -PassThru
    }
    # Allocates and touches every page so it counts as memory actually used.
    $hog = "`$b=[byte[]]::new(${MemoryMB}MB); for(`$i=0;`$i -lt `$b.Length;`$i+=4096){`$b[`$i]=1}; Start-Sleep -Seconds 3600"
    $load += Start-Process $pwsh -ArgumentList '-NoProfile', '-Command', $hog -WindowStyle Hidden -PassThru
    Write-Host "Carga en marcha: $($load.Count) procesos. Esperando a que la memoria se reserve..."
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        $load[-1].Refresh()
        if ($load[-1].PrivateMemorySize64 / 1MB -ge ($MemoryMB * 0.9)) { break }
        Start-Sleep -Milliseconds 500
    }

    Write-Host ''
    Write-Host "=== E2E bajo carga ($Cycles ciclos) ===" -ForegroundColor Cyan
    # With the machine saturated a larger peak is tolerated; the real latency is printed anyway.
    $env:LIVECHECK_MAXLAT_MS = '1000'
    dotnet $dll e2e $Exe $Cycles
    $code = $LASTEXITCODE
}
finally {
    Remove-Item Env:LIVECHECK_MAXLAT_MS -ErrorAction SilentlyContinue
    foreach ($p in $load) { try { if (-not $p.HasExited) { $p.Kill() } } catch { } }
    Write-Host 'Procesos de carga detenidos.'
}
exit $code
