#requires -Version 7
<#
.SYNOPSIS
  Verifies the published EXE end to end with a simulated OpenAI server on loopback (no paid calls).
.DESCRIPTION
  Uses the real microphone and synthetic input (Win+H, Escape) on the real desktop; opens a test window
  that receives the paste. Isolates settings, mutex and credential with DIKTAP_DATA_DIR. Prints startup, memory,
  handles, idle CPU and stabilization after N dictations/cancellations/errors.
.EXAMPLE
  ./scripts/Verify-Published.ps1 -Cycles 28
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Cycles = 28
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Exe) { $Exe = Join-Path $root 'artifacts/publish/win-x64/DikTap.exe' }
if (-not (Test-Path $Exe)) { throw "No existe $Exe. Ejecuta ./scripts/Publish.ps1 primero." }

$live = Join-Path $root 'tests/DikTap.LiveCheck/DikTap.LiveCheck.csproj'
dotnet build $live -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'no compila LiveCheck' }
$dll = Join-Path $root 'tests/DikTap.LiveCheck/bin/Release/net10.0-windows/DikTap.LiveCheck.dll'
dotnet $dll e2e (Resolve-Path $Exe).Path $Cycles
exit $LASTEXITCODE
