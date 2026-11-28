# Runs Vistoria.exe end to end on this machine (meant for a VM or CI runner) and checks the output.
# Usage: pwsh tests/smoke.ps1 -Exe <path to Vistoria.exe> -Out <empty folder> [-Runs 3]
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Out, [int]$Runs = 3)
$ErrorActionPreference = 'Stop'
function Fail($msg) { Write-Error "SMOKE FAIL: $msg"; exit 1 }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
[string[]]$exeArgs = '--saida', $Out, '--loja', 'Smoke', '--tecnico', 'CI'
if (-not $isAdmin) { $exeArgs += '--sem-admin' }   # never pop a UAC prompt from a script
$times = @()
for ($i = 1; $i -le $Runs; $i++) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $log = '' | & $Exe @exeArgs 2>&1   # stdin redirected: no "press Enter"
    $sw.Stop()
    if ($LASTEXITCODE -ne 0) { $log; Fail "exit code $LASTEXITCODE on run $i" }
    $times += $sw.Elapsed.TotalSeconds
    if ($i -eq 1) { $log }
}

$dirs = @(Get-ChildItem $Out -Directory)
if ($dirs.Count -ne $Runs) { Fail "expected $Runs output folders, found $($dirs.Count) (a run overwrote another)" }

foreach ($d in $dirs) {
    $json = Join-Path $d.FullName 'evidencias.json'
    $html = Join-Path $d.FullName 'relatorio.html'
    $man = Get-Content (Join-Path $d.FullName 'manifesto.txt') -Raw
    foreach ($f in $json, $html) {
        $h = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $man.Contains("$h  $(Split-Path $f -Leaf)")) { Fail "manifest hash mismatch for $f" }
    }
    $ev = Get-Content $json -Raw | ConvertFrom-Json
    if (-not (Get-Content $html -Raw).Contains($ev.Run.Version)) { Fail 'report does not carry the tool version' }
    if ($ev.Checks.Count -lt 12) { Fail "only $($ev.Checks.Count) checks" }
    if ($ev.Checks | Where-Object { -not $_.Sources -or $_.Sources.Count -eq 0 }) { Fail 'a check has no source' }
}

$ev = Get-Content (Join-Path $dirs[0].FullName 'evidencias.json') -Raw | ConvertFrom-Json
"admin: $($ev.Run.IsAdmin)  disks: $($ev.Disks.Count)  event categories: $($ev.Events.Categories.Count)  devices: $($ev.Devices.Count)  dumps: $($ev.Dumps.Count)"
"collection errors:"; $ev.Errors.PSObject.Properties | ForEach-Object { "  $($_.Name): $($_.Value)" }
"checks:"; $ev.Checks | ForEach-Object { "  [$($_.Status)] $($_.Title)" }
"run times (s): " + (($times | ForEach-Object { $_.ToString('0.00', [Globalization.CultureInfo]::InvariantCulture) }) -join ', ')
"SMOKE OK"
