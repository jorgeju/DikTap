#requires -Version 7
<#
.SYNOPSIS
  Measures the EXE at idle (tray icon only; uses no keyboard, mouse or focus, and opens no windows).
.DESCRIPTION
  Isolated profile (DIKTAP_DATA_DIR), fake credential via cmdkey and preconfigured settings.
  Prints startup time until "Ready", memory, handles, threads, idle CPU and the largest native modules.
.EXAMPLE
  ./scripts/Measure-Idle.ps1 -Exe ./artifacts/publish/win-x64/DikTap.exe
  ./scripts/Measure-Idle.ps1 -Exe ... -Env @{ DOTNET_gcConcurrent = '0' }
#>
[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '../artifacts/publish/win-x64/DikTap.exe'),
    [int]$SettleSeconds = 8,
    [int]$IdleSeconds = 20,
    [hashtable]$Env = @{},
    [switch]$Modules,
    [switch]$Threads
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
Add-Type -Namespace Win -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr FindWindowW(string cls, string title);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool PostMessageW(System.IntPtr h, uint m, System.UIntPtr w, System.IntPtr l);
'@

$data = Join-Path ([IO.Path]::GetTempPath()) ('diktap-idle-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $data | Out-Null
@{ Version = 1; HotkeyMode = 'WinH'; FirstRunCompleted = $true; StartWithWindows = $false } | ConvertTo-Json | Set-Content (Join-Path $data 'settings.json')

# Same suffix as AppInfo: "." + first 8 hex chars (uppercase) of SHA-256(data directory in UTF-8).
$sha = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($data))
$target = 'DikTap/OpenAI API key.' + ([BitConverter]::ToString($sha) -replace '-', '').Substring(0, 8)
cmd /c "cmdkey /generic:`"$target`" /user:test /pass:sk-test-idle-0123456789" | Out-Null
if (-not (cmdkey /list | Select-String -SimpleMatch $target)) { throw "No se pudo crear la credencial de prueba: $target" }

$saved = @{}
$vars = @{ DIKTAP_DATA_DIR = $data } + $Env
foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
$proc = $null
try {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process $Exe -PassThru
    $log = Join-Path $data 'logs/app.log'
    while ($sw.Elapsed.TotalSeconds -lt 30) {
        if ((Test-Path $log) -and ((Get-Content $log -Raw -ErrorAction SilentlyContinue) -match 'Ready')) { break }
        Start-Sleep -Milliseconds 20
    }
    $ready = $sw.ElapsedMilliseconds
    Start-Sleep -Seconds $SettleSeconds
    function Snap { $proc.Refresh(); [pscustomobject]@{ WS = [math]::Round($proc.WorkingSet64 / 1MB, 1); Private = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1); Handles = $proc.HandleCount; Threads = $proc.Threads.Count } }
    $a = Snap
    $cpu0 = $proc.TotalProcessorTime
    Start-Sleep -Seconds $IdleSeconds
    $proc.Refresh()
    "  ventana principal: '{0}'" -f $proc.MainWindowTitle
    "  log: " + ((Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Last 3) -join ' | ')
    $cpuMs = ($proc.TotalProcessorTime - $cpu0).TotalMilliseconds
    $b = Snap
    "{0}  (listo en {1} ms)" -f (Split-Path $Exe -Leaf), $ready
    "  tras {0} s : WS {1} MB | privados {2} MB | handles {3} | hilos {4}" -f $SettleSeconds, $a.WS, $a.Private, $a.Handles, $a.Threads
    "  tras +{0} s: WS {1} MB | privados {2} MB | CPU en reposo {3:N0} ms" -f $IdleSeconds, $b.WS, $b.Private, $cpuMs
    if ($Modules) {
        $proc.Modules | Sort-Object ModuleMemorySize -Descending | Select-Object -First 15 ModuleName, @{n = 'MB'; e = { [math]::Round($_.ModuleMemorySize / 1MB, 1) } } | Format-Table -AutoSize | Out-Host
        "  modulos cargados: {0}" -f $proc.Modules.Count
        $groups = $proc.Modules | Group-Object {
            switch -Regex ($_.ModuleName) {
                '^(PresentationCore|PresentationFramework|WindowsBase|System\.Xaml|UIAutomation|wpfgfx|PresentationNative|PenImc|D3DCompiler)' { 'WPF'; break }
                '^(ig|media_bin|nv|amd|ati|d3d|dxgi|dxcore)' { 'GPU/DirectX'; break }
                '^(coreclr|clrjit|System\.|Microsoft\.|mscor|hostpolicy|clrgc)' { '.NET runtime'; break }
                default { 'Windows/otros' }
            }
        }
        $groups | ForEach-Object { '  {0,-14} {1,3} modulos, {2,7:N1} MB mapeados' -f $_.Name, $_.Count, (($_.Group | Measure-Object ModuleMemorySize -Sum).Sum / 1MB) }
    }
    if ($Threads) {
        # Module containing each thread's start address: shows who created it (runtime, Windows pool, GDI+, audio...).
        $mods = $proc.Modules | ForEach-Object { [pscustomobject]@{ Name = $_.ModuleName; Start = $_.BaseAddress.ToInt64(); End = $_.BaseAddress.ToInt64() + $_.ModuleMemorySize } }
        $proc.Threads | ForEach-Object {
            $addr = $_.StartAddress.ToInt64()
            $m = $mods | Where-Object { $addr -ge $_.Start -and $addr -lt $_.End } | Select-Object -First 1
            [pscustomobject]@{ Modulo = if ($m) { $m.Name } else { '(EXE/otro)' }; Estado = $_.ThreadState; Espera = if ($_.ThreadState -eq 'Wait') { $_.WaitReason } else { '' } }
        } | Group-Object Modulo, Espera | Sort-Object Count -Descending | ForEach-Object { '  hilos {0,2}: {1}' -f $_.Count, $_.Name }
    }
    $host_ = [Win.Native]::FindWindowW([NullString]::Value, 'DikTap Host')
    [void][Win.Native]::PostMessageW($host_, 0x10, [UIntPtr]::Zero, [IntPtr]::Zero)
    if (-not $proc.WaitForExit(8000)) { 'AVISO: no salio con WM_CLOSE; se fuerza'; $proc.Kill($true) }
}
finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill($true) }
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    cmd /c "cmdkey /delete:`"$target`"" | Out-Null
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}
