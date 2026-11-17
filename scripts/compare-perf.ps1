<#
.SYNOPSIS
    Compares two measure-baseline.ps1 runs and reports the magnification cost delta.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\compare-perf.ps1 -Before baseline -After after-p0
#>
param(
    [Parameter(Mandatory = $true)][string]$Before,
    [Parameter(Mandatory = $true)][string]$After
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Load-Summary([string]$label) {
    $path = Join-Path $repoRoot "artifacts\perf\$label\summary.json"
    if (-not (Test-Path $path)) { throw "No measurement found at $path" }
    return Get-Content $path -Raw | ConvertFrom-Json
}

function Num([object]$value) {
    if ($null -eq $value) { return 0.0 }
    return [double]$value
}

function Pct([object]$old, [object]$new) {
    $oldNum = Num $old
    if ($oldNum -eq 0) { return 'n/a' }
    $change = ((Num $new) - $oldNum) / $oldNum
    return ('{0:P1}' -f $change)
}

function Row([string]$name, [object]$old, [object]$new) {
    $oldNum = Num $old
    $newNum = Num $new
    [pscustomobject]@{
        Metric = $name
        Before = ('{0:N2}' -f $oldNum)
        After  = ('{0:N2}' -f $newNum)
        Change = (Pct $old $new)
    }
}

$a = Load-Summary $Before
$b = Load-Summary $After

function MagnificationCost($summary) {
    [pscustomobject]@{
        CpuPercentOfOneCore = (Num $summary.Sweep.Process.CpuPercentOfOneCore) - (Num $summary.Idle.Process.CpuPercentOfOneCore)
        AllocBytesPerSec    = (Num $summary.Sweep.Counters.AllocationBytesPerSec) - (Num $summary.Idle.Counters.AllocationBytesPerSec)
        WorkingSetDelta     = (Num $summary.Sweep.Process.WorkingSetDeltaBytes) - (Num $summary.Idle.Process.WorkingSetDeltaBytes)
    }
}

$costA = MagnificationCost $a
$costB = MagnificationCost $b

$rows = @(
    Row 'Idle: CPU % of one core' $a.Idle.Process.CpuPercentOfOneCore $b.Idle.Process.CpuPercentOfOneCore
    Row 'Idle: allocation bytes/s' $a.Idle.Counters.AllocationBytesPerSec $b.Idle.Counters.AllocationBytesPerSec
    Row 'Sweep: CPU % of one core' $a.Sweep.Process.CpuPercentOfOneCore $b.Sweep.Process.CpuPercentOfOneCore
    Row 'Sweep: allocation bytes/s' $a.Sweep.Counters.AllocationBytesPerSec $b.Sweep.Counters.AllocationBytesPerSec
    Row 'Magnification CPU cost (one core)' $costA.CpuPercentOfOneCore $costB.CpuPercentOfOneCore
    Row 'Magnification allocation cost bytes/s' $costA.AllocBytesPerSec $costB.AllocBytesPerSec
    Row 'Sweep working-set delta' $costA.WorkingSetDelta $costB.WorkingSetDelta
)

$rows | Format-Table -AutoSize

$perMoveA = 0.0
$perMoveB = 0.0
if ($a.PointerMoves -gt 0) { $perMoveA = $costA.AllocBytesPerSec * $a.DurationSeconds.Sweep / $a.PointerMoves }
if ($b.PointerMoves -gt 0) { $perMoveB = $costB.AllocBytesPerSec * $b.DurationSeconds.Sweep / $b.PointerMoves }
Write-Host ''
Write-Host ("Allocation per pointer move: {0:N0} B -> {1:N0} B ({2})" -f $perMoveA, $perMoveB, (Pct $perMoveA $perMoveB))
Write-Host ("Pointer moves: {0} -> {1}" -f $a.PointerMoves, $b.PointerMoves)
