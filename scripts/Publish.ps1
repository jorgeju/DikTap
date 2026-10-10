#requires -Version 7
<#
.SYNOPSIS
  Publishes DikTap as a self-contained single-file EXE for Windows x64 and, optionally, creates the ZIP.
.DESCRIPTION
  Fixed options (see the comments in DikTap.csproj):
    SelfContained + PublishSingleFile + IncludeNativeLibrariesForSelfExtract  -> does not require .NET installed.
    PublishTrimmed=false                                                      -> WPF does not support safe trimming.
    PublishReadyToRun=true (DikTap.dll only, see the .csproj)                  -> startup 500-700 ms -> 165-315 ms for +0.3 MB; ReadyToRun of the whole framework is not worth it.
    EnableCompressionInSingleFile (default false)                         -> with true the EXE shrinks from ~125 to ~59 MB, but idle RAM
                                                                                 doubles (measured: ~111 MB vs ~215 MB working set); the ZIP already compresses.
    SatelliteResourceLanguages=es;en (en el .csproj)                          -> only Spanish and English satellite resources.
.EXAMPLE
  ./scripts/Publish.ps1 -Package
  ./scripts/Publish.ps1 -Compress $false -Output ./artifacts/trial/base
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [bool]$Compress = $false,
    [string]$Output,
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src/DikTap/DikTap.csproj'
if (-not $Output) { $Output = Join-Path $root "artifacts/publish/$Runtime" }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

$compressValue = $Compress.ToString().ToLowerInvariant()
dotnet publish $project -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=$compressValue `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $Output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish fallo' }

$exe = Join-Path $Output 'DikTap.exe'
if (-not (Test-Path $exe)) { throw "No se genero $exe" }
$files = Get-ChildItem $Output -Recurse -File
Write-Host ("Publicado: {0} ({1:N1} MB, {2} archivo(s) en la carpeta)" -f $exe, ((Get-Item $exe).Length / 1MB), $files.Count)

if (-not $Package) { return }

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$name = "DikTap-$version-$Runtime"
$stage = Join-Path $root "artifacts/package/$name"
$zip = Join-Path $root "artifacts/package/$name.zip"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item $exe $stage
Copy-Item (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $root 'README.es.md') $stage
Copy-Item (Join-Path $root 'LICENSE') $stage

# Licenses of the .NET / WPF runtime included in the self-contained EXE (MIT and third-party notices).
$dotnetRoot = Split-Path (Get-Command dotnet).Source
$licenses = Join-Path $stage 'LICENSES'
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $dotnetRoot 'LICENSE.txt') (Join-Path $licenses 'DOTNET-LICENSE.txt')
Copy-Item (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') (Join-Path $licenses 'DOTNET-ThirdPartyNotices.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $stage

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
@(
    "DikTap $version"
    "Plataforma: Windows $Runtime (autocontenido, runtime .NET incluido)"
    "Compilado: $(Get-Date -Format 'yyyy-MM-dd')"
    "SHA-256 de DikTap.exe: $hash"
) | Set-Content (Join-Path $stage 'VERSION.txt') -Encoding utf8

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$($zipHash.ToLowerInvariant()) *$name.zip" | Set-Content "$zip.sha256" -Encoding ascii
Write-Host ("Paquete: {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
Write-Host "SHA-256 ZIP: $zipHash"
