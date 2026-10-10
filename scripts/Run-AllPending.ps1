#requires -Version 7
<#
.SYNOPSIS
  Runs the whole unattended battery (idle, desktop, load) in one go and leaves a short summary on disk. Do NOT use the computer while it runs.
.DESCRIPTION
  No prompts. Full output in artifacts/test-results/<date>/all.log and summary in summary.txt (the only thing that needs reading).
  When finished, or if interrupted, stops any load process and any published DikTap.exe left behind.
#>
[CmdletBinding()]
param([int]$Cycles = 60, [int]$LoadCycles = 30, [switch]$Full)

$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = Join-Path $root ("artifacts/test-results/" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $out -Force | Out-Null
$log = Join-Path $out 'all.log'
$exe = Join-Path $root 'artifacts/publish/win-x64/DikTap.exe'

function Stop-Leftovers {
    Get-Process DikTap -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name = 'pwsh.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessId -ne $PID -and ($_.CommandLine.Contains('Math]::Sqrt($x+1.5)') -or $_.CommandLine.Contains('[byte[]]::new(')) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

$steps = @()
if ($Full) {
    $steps += @{ Name = 'Reposo (memoria/handles/hilos/CPU)'; Script = 'Measure-Idle.ps1'; Args = @{} }
    $steps += @{ Name = 'Bateria de escritorio completa'; Script = 'Test-Desktop.ps1'; Args = @{ Yes = $true } }
}
else {
    $steps += @{ Name = "E2E sin carga ($Cycles ciclos)"; Script = 'Verify-Published.ps1'; Args = @{ Cycles = $Cycles } }
}
$steps += @{ Name = "E2E bajo carga ($LoadCycles ciclos)"; Script = 'Test-UnderLoad.ps1'; Args = @{ Yes = $true; Cycles = $LoadCycles } }

$results = @()
try {
    foreach ($s in $steps) {
        "`n##### $($s.Name) #####" | Tee-Object -FilePath $log -Append | Out-Host
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $stepArgs = $s.Args
        & (Join-Path $PSScriptRoot $s.Script) @stepArgs *>&1 | Tee-Object -FilePath $log -Append | Out-Host
        $code = $LASTEXITCODE
        $results += [pscustomobject]@{ Paso = $s.Name; Codigo = $code; Segundos = [int]$sw.Elapsed.TotalSeconds }
        Stop-Leftovers
    }
}
finally {
    Stop-Leftovers
    $left = @(Get-Process DikTap -ErrorAction SilentlyContinue).Count
    $summary = @()
    $summary += "Fecha: $(Get-Date -Format 's')"
    $summary += ($results | Format-Table -AutoSize | Out-String).TrimEnd()
    $summary += "Procesos DikTap sin cerrar: $left"
    $summary += '--- Lineas clave del log ---'
    $summary += @(Select-String -Path $log -Pattern 'latencia|mediana|p95|callback|FALLO|ABORT|FAIL|fall[oó]|Resumen|WS |no entregad|Preparando|ciclos' -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Line.Trim() } | Select-Object -First 80)
    $summary | Set-Content (Join-Path $out 'summary.txt') -Encoding UTF8
    'FIN' | Set-Content (Join-Path $out 'DONE') -Encoding ASCII
    Write-Host "`nResumen: $(Join-Path $out 'summary.txt')"
}
if ($results | Where-Object Codigo -ne 0) { exit 1 }
