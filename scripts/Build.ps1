#requires -Version 7
<#
.SYNOPSIS
  Restores, builds in Release and runs the automated tests.
.EXAMPLE
  ./scripts/Build.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$solution = Join-Path $root 'DikTap.slnx'

dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore fallo' }

dotnet build $solution -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet build fallo' }

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'tests/DikTap.Tests/DikTap.Tests.csproj') -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'las pruebas fallaron' }
}
