#requires -Version 5.1
# WARNO Modding Suite - pre-launch diagnostic.
# Captures everything we need to understand silent process exits BEFORE
# the .NET runtime starts (antivirus, MoTW, missing OS build, unwritable
# self-extract cache, etc).

$ErrorActionPreference = ''Continue''
$ProgressPreference    = ''SilentlyContinue''

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dump = Join-Path $here ("diagnostic_dump_{0}.txt" -f (Get-Date -Format ''yyyy-MM-dd_HH-mm-ss''))

function W   ($t) { Add-Content -LiteralPath $dump -Value $t }
function Sec ($t) { W ''''; W "=== $t ===" }

W "WARNO Modding Suite - pre-launch diagnostic"
W "Generated: $(Get-Date -Format ''yyyy-MM-dd HH:mm:ss zzz'')"
W "Folder:    $here"
W "User:      $env:USERNAME"
W "Computer:  $env:COMPUTERNAME"

Sec ''Operating system''
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    W "  Caption: $($os.Caption)"
    W "  Version: $($os.Version)"
    W "  Build:   $($os.BuildNumber)   (this build targets Windows 10 build >= 19041)"
    W "  Arch:    $($os.OSArchitecture)"
    if ([int]$os.BuildNumber -lt 19041) {
        W "  *** WARNING: this Windows build is OLDER than what the .exe targets. ***"
    }
} catch { W "  ERROR: $($_.Exception.Message)" }

Sec ''CPU''
try {
    $cpu = Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1
    W "  Name:         $($cpu.Name.Trim())"
    W "  Manufacturer: $($cpu.Manufacturer)"
    W "  Cores:        $($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads"
    W "  Architecture: $($cpu.Architecture)"
} catch { W "  ERROR: $($_.Exception.Message)" }

Sec ''Antivirus products (SecurityCenter2)''
try {
    $av = Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop
    if ($av) { $av | ForEach-Object { W ("  {0}    state=0x{1:X8}" -f $_.displayName, $_.productState) } }
    else     { W "  (none enumerated)" }
} catch { W "  ERROR: $($_.Exception.Message)" }

Sec ''Windows Defender''
try {
    $mp = Get-MpComputerStatus -ErrorAction Stop
    W "  RealTimeProtectionEnabled: $($mp.RealTimeProtectionEnabled)"
    W "  AntivirusEnabled:          $($mp.AntivirusEnabled)"
    W "  TamperProtectionEnabled:   $($mp.IsTamperProtected)"
} catch { W "  Get-MpComputerStatus failed: $($_.Exception.Message)" }
try {
    $threats = Get-MpThreatDetection -ErrorAction Stop |
               Where-Object { $_.InitialDetectionTime -gt (Get-Date).AddDays(-1) }
    if ($threats) {
        foreach ($t in $threats) {
            W ("  Recent threat: {0}  detected {1}" -f ($t.Resources -join '', ''), $t.InitialDetectionTime)
        }
    } else { W "  No threat detections in the last 24h." }
} catch { W "  Get-MpThreatDetection failed: $($_.Exception.Message)" }

Sec ''Files and Mark-of-the-Web''
$exes = @(''moddingSuite.exe'')
foreach ($name in $exes) {
    $p = Join-Path $here $name
    if (-not (Test-Path $p)) { W "  [MISSING] $name"; continue }
    $size = (Get-Item $p).Length
    $motw = $null
    try { $motw = Get-Item $p -Stream Zone.Identifier -ErrorAction Stop } catch {}
    $motwStr = if ($motw) { ''YES (right-click .exe -> Properties -> Unblock)'' } else { ''no'' }
    W ("  {0,-30}  {1,12:N0} bytes  MoTW={2}" -f $name, $size, $motwStr)
}

Sec ''Self-extract cache (%LOCALAPPDATA%\Temp\.net)''
$cache = Join-Path $env:LOCALAPPDATA ''Temp\.net''
W "  Cache root: $cache"
W "  Exists:     $(Test-Path $cache)"
try {
    New-Item -ItemType Directory -Force -Path $cache -ErrorAction Stop | Out-Null
    $probe = Join-Path $cache ''modding_suite_probe.tmp''
    [IO.File]::WriteAllText($probe, ''ok'')
    Remove-Item -Force $probe
    W "  Writable:   yes"
} catch { W "  Writable:   NO - $($_.Exception.Message)" }

Sec ''.NET runtimes installed (informational - self-contained build does NOT need them)''
$dotnet = "$env:ProgramFiles\dotnet\dotnet.exe"
if (Test-Path $dotnet) {
    try { (& $dotnet --list-runtimes 2>&1) | ForEach-Object { W "  $_" } }
    catch { W "  failed: $($_.Exception.Message)" }
} else { W "  $dotnet not present" }

Sec ''Launch attempt: moddingSuite.exe (max 12s)''
$exe = Join-Path $here ''moddingSuite.exe''
$launchTime = Get-Date
if (-not (Test-Path $exe)) {
    W "  ABORT: $exe not found."
} else {
    try {
        $proc = Start-Process -FilePath $exe -PassThru -ErrorAction Stop
        W "  PID: $($proc.Id)"
        $exited = $proc.WaitForExit(12000)
        if (-not $exited) {
            W "  Still running after 12s - looks OK. Killing for clean shutdown..."
            try { $proc.Kill(); $proc.WaitForExit(3000) | Out-Null } catch {}
        } else {
            W "  Exited within 12s. ExitCode: $($proc.ExitCode)"
        }
    } catch {
        W "  Start-Process failed: $($_.Exception.Message)"
    }
}

Sec ''Application event log since launch (errors/warnings)''
try {
    $events = Get-WinEvent -LogName Application -MaxEvents 80 -ErrorAction Stop |
              Where-Object { $_.TimeCreated -ge $launchTime.AddSeconds(-2) -and
                             $_.LevelDisplayName -in @(''Error'',''Warning'',''Critical'') }
    if (-not $events) { W "  (no error/warning entries in this window)" }
    foreach ($e in $events) {
        W ""
        W ("  --- {0} | {1} | id={2} | {3} ---" -f $e.TimeCreated, $e.LevelDisplayName, $e.Id, $e.ProviderName)
        foreach ($line in ($e.Message -split "`n")) { W "  $line" }
    }
} catch { W "  Get-WinEvent failed: $($_.Exception.Message)" }

Sec ''Recent .NET runtime crashes (provider .NET Runtime / Application Error)''
try {
    $crashes = Get-WinEvent -FilterHashtable @{
        LogName=''Application'';
        ProviderName=''.NET Runtime'',''Application Error'',''Windows Error Reporting'';
        StartTime=$launchTime.AddSeconds(-2)
    } -ErrorAction Stop
    foreach ($e in $crashes) {
        W ""
        W ("  --- {0} | id={1} | {2} ---" -f $e.TimeCreated, $e.Id, $e.ProviderName)
        foreach ($line in ($e.Message -split "`n")) { W "  $line" }
    }
    if (-not $crashes) { W "  (none in this window)" }
} catch { W "  Get-WinEvent failed: $($_.Exception.Message)" }

Sec ''Done''
W ""
W "Dump written: $dump"

Write-Host ""
Write-Host "Diagnostic complete." -ForegroundColor Green
Write-Host "Dump file: $dump"
Write-Host ""
Write-Host "Please send this file to the developer."