# Read-only shell compatibility smoke test for OpenTaskbar.
# Reports OS/build facts that the documented shell assumptions depend on. It never kills or restarts
# Explorer, never sends TaskbarCreated/AppBar messages, never writes the registry and needs no elevation.
# Run after each Windows feature/update to decide whether the manual checks in Section 6 still hold.

$ErrorActionPreference = 'Stop'

function Write-Fact {
    param([string]$Name, [string]$Value)
    Write-Host ('{0,-26} {1}' -f $Name, $Value)
}

Write-Host 'OpenTaskbar shell compatibility smoke test (read-only)'
Write-Host ("Collected: {0}" -f (Get-Date -Format o))
Write-Host ''

# --- OS build -------------------------------------------------------------------------------------
try {
    $os = Get-CimInstance Win32_OperatingSystem
    Write-Fact 'OS' ('{0} {1} (build {2}, {3})' -f $os.Caption, $os.Version, $os.BuildNumber, $os.OSArchitecture)
    Write-Fact 'Last boot' "$($os.LastBootUpTime)"
}
catch {
    Write-Fact 'OS' "unavailable: $($_.Exception.Message)"
}

# --- Explorer process and file version -------------------------------------------------------------
try {
    $explorers = @(Get-Process -Name explorer -ErrorAction SilentlyContinue)
    if ($explorers.Count -eq 0) {
        Write-Fact 'Explorer' 'NOT RUNNING (shell recovery paths would degrade)'
    }
    else {
        foreach ($process in $explorers) {
            $version = 'unknown'
            try {
                if ($process.Path) { $version = (Get-Item -LiteralPath $process.Path).VersionInfo.FileVersion }
            }
            catch {
                $version = "unavailable: $($_.Exception.Message)"
            }
            Write-Fact "Explorer[$($process.Id)]" "version=$version path=$($process.Path)"
        }
    }
}
catch {
    Write-Fact 'Explorer' "unavailable: $($_.Exception.Message)"
}

# --- TaskbarCreated registration and live shell tray windows ---------------------------------------
# Windows does not expose per-window registrations. RegisterWindowMessage returns the process-local id
# for "TaskbarCreated"; live Shell_TrayWnd windows are the practical proxy for who can receive it.
try {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShellCompatNative
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    public static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);
}
'@ -ErrorAction Stop

    $taskbarCreatedId = [ShellCompatNative]::RegisterWindowMessage('TaskbarCreated')
    Write-Fact 'TaskbarCreated id' ('0x{0:X8} (non-zero = registered in this process)' -f $taskbarCreatedId)

    $trayWindows = @()
    $candidate = [IntPtr]::Zero
    do {
        $candidate = [ShellCompatNative]::FindWindowEx([IntPtr]::Zero, $candidate, 'Shell_TrayWnd', $null)
        if ($candidate -ne [IntPtr]::Zero) {
            $ownerPid = [uint32]0
            [void][ShellCompatNative]::GetWindowThreadProcessId($candidate, [ref]$ownerPid)
            $trayWindows += ('hwnd=0x{0:X} pid={1}' -f $candidate.ToInt64(), $ownerPid)
        }
    } while ($candidate -ne [IntPtr]::Zero)

    if ($trayWindows.Count -eq 0) {
        Write-Fact 'Shell_TrayWnd' 'none found (no live receiver for TaskbarCreated)'
    }
    else {
        Write-Fact 'Shell_TrayWnd' ($trayWindows -join '; ')
    }
}
catch {
    Write-Fact 'Shell_TrayWnd' "unavailable: $($_.Exception.Message)"
}

# --- DPI scale and monitor count -------------------------------------------------------------------
try {
    Add-Type -AssemblyName System.Drawing
    $graphics = [System.Drawing.Graphics]::FromHwnd([IntPtr]::Zero)
    try {
        $dpiX = $graphics.DpiX
        $dpiY = $graphics.DpiY
        Write-Fact 'DPI' ('DpiX={0}, DpiY={1}, scale~{2:N2}x' -f $dpiX, $dpiY, ($dpiX / 96.0))
    }
    finally {
        $graphics.Dispose()
    }
}
catch {
    Write-Fact 'DPI' "unavailable: $($_.Exception.Message)"
}

try {
    $monitors = [ShellCompatNative]::GetSystemMetrics(80)  # SM_CMONITORS
    $primaryWidth = [ShellCompatNative]::GetSystemMetrics(0) # SM_CXSCREEN
    $primaryHeight = [ShellCompatNative]::GetSystemMetrics(1) # SM_CYSCREEN
    Write-Fact 'Monitors' ("count={0}, primary={1}x{2}" -f $monitors, $primaryWidth, $primaryHeight)
}
catch {
    Write-Fact 'Monitors' "unavailable: $($_.Exception.Message)"
}

# --- Process integrity (elevated vs standard is the practical integrity-level proxy) ---------------
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Fact 'Integrity' ($(if ($elevated) { 'High / elevated (UIPI: can message lower IL windows)' } else { 'Medium / standard user (no elevation)' }))
}
catch {
    Write-Fact 'Integrity' "unavailable: $($_.Exception.Message)"
}

# --- Manual checklist reminder ---------------------------------------------------------------------
Write-Host ''
Write-Host 'Manual verification after each Windows feature/update (docs/windows-taskbar-model.md Sections 1.1-1.9 and 6):'
$checklist = @(
    'Restart explorer.exe: TaskbarCreated re-registers all tray icons and the AppBar work area.',
    'Confirm no self-broadcast is mistaken for an Explorer restart (TaskbarCreated wParam/seq telemetry).',
    'Hide/show the native taskbar: journal write, verified restore and journal deletion all succeed.',
    'Kill the dock: watchdog or --restore leaves a visible taskbar and no stale Shell_TrayWnd.',
    'Move to a secondary monitor / change primary DPI: ABM_SETPOS lands on the correct monitor.',
    'Pin a GUID-bearing app by path (Steam): click activates the exe, not a synthetic tray message.',
    'Pin two icons from one exe and an app with a hidden tray icon: snapshot dedupe and fallback hold.',
    'Scroll the dock then let magnification/resize settle: the strip never jumps back to offset 0.',
    'Run the new headless/UI checks and this script; compare reported build/version with the last known-good run.'
)
foreach ($item in $checklist) {
    Write-Host ('  [ ] {0}' -f $item)
}
Write-Host ''
Write-Host 'This script is read-only. It does not stop processes, change taskbar state or write any settings.'
