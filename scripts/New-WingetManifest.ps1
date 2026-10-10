#requires -Version 7
<#
.SYNOPSIS
  Generates the winget manifests for a release already published on GitHub.
.DESCRIPTION
  Fills in the packaging/winget templates with the version, date and SHA-256 of the published ZIP (downloaded from the release
  if -Sha256 is not given) and leaves the manifests/j/jorgeju/DikTap/<version> tree ready to copy to a fork of microsoft/winget-pkgs.
.EXAMPLE
  ./scripts/New-WingetManifest.ps1                # version from Directory.Build.props
  ./scripts/New-WingetManifest.ps1 -Version 1.0.1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Sha256
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $Version = $props.Project.PropertyGroup.Version
}

if (-not $Sha256) {
    $url = "https://github.com/jorgeju/DikTap/releases/download/v$Version/DikTap-$Version-win-x64.zip"
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "DikTap-$Version.zip"
    Write-Host "Descargando $url"
    Invoke-WebRequest $url -OutFile $tmp
    $Sha256 = (Get-FileHash $tmp -Algorithm SHA256).Hash
    Remove-Item $tmp
}
$Sha256 = $Sha256.ToUpperInvariant()

$out = Join-Path $root "artifacts/winget/manifests/j/jorgeju/DikTap/$Version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($template in Get-ChildItem (Join-Path $root 'packaging/winget') -Filter '*.yaml') {
    (Get-Content $template.FullName -Raw).
        Replace('{{VERSION}}', $Version).
        Replace('{{DATE}}', (Get-Date -Format 'yyyy-MM-dd')).
        Replace('{{SHA256}}', $Sha256) |
        Set-Content (Join-Path $out $template.Name) -Encoding utf8NoBOM
}
Write-Host "Manifiestos en $out (SHA-256 $Sha256)"

if (Get-Command winget -ErrorAction SilentlyContinue) { winget validate --manifest $out }
