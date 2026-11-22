# OpenTaskbar Portable Single-File Publisher
# Builds self-contained single-file executables for Windows x64 and x86.
param (
    [ValidateSet('win-x64', 'win-x86')]
    [string]$Runtime = 'win-x64',
    # Both architectures share one release folder; each build is renamed to OpenTaskbar-<arch>.exe.
    [string]$OutputDir = "$PSScriptRoot\..\release"
)

$ErrorActionPreference = "Stop"

$arch = ($Runtime -split '-')[-1]
$OutputDir = (Resolve-Path -LiteralPath $OutputDir -ErrorAction SilentlyContinue).Path
if (-not $OutputDir) {
    New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot '..\release') -Force | Out-Null
    $OutputDir = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\release')).Path
}

# Gracefully request running instance to close, allowing it to restore Windows taskbar cleanly
$running = Get-Process -Name OpenTaskbar, OpenTaskbar-x64, OpenTaskbar-x86 -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Closing running OpenTaskbar instance gracefully..." -ForegroundColor Yellow
    foreach ($proc in $running) {
        $proc.CloseMainWindow() | Out-Null
    }
    $closed = $running | ForEach-Object { $_.WaitForExit(2500) }
    $stillRunning = Get-Process -Name OpenTaskbar -ErrorAction SilentlyContinue
    if ($stillRunning) {
        $stillRunning | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
    }

    # Ensure Windows native taskbar is restored if any instance was running
    $nativeTaskbarRestorer = @"
using System;
using System.Runtime.InteropServices;
public class PortableRestorer {
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string title);
    public static void ShowAll() {
        var p = FindWindow("Shell_TrayWnd", null);
        if (p != IntPtr.Zero) ShowWindow(p, 5);
        var s = IntPtr.Zero;
        while ((s = FindWindowEx(IntPtr.Zero, s, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) {
            ShowWindow(s, 5);
        }
    }
}
"@
    Add-Type -TypeDefinition $nativeTaskbarRestorer -ErrorAction SilentlyContinue
    [PortableRestorer]::ShowAll()
}

Write-Host "Publishing OpenTaskbar as self-contained portable executable..." -ForegroundColor Cyan

$watchdogScript = "$PSScriptRoot\..\tools\watchdog\build.ps1"
# Build the native helper outside the release folder: it is embedded into the EXE and must never ship as a sidecar.
$watchdogDir = Join-Path $env:TEMP "opentaskbar-watchdog-$arch"
if (Test-Path $watchdogScript) {
    & powershell -ExecutionPolicy Bypass -File $watchdogScript -Architecture $arch -OutputDir $watchdogDir
    if ($LASTEXITCODE -ne 0) { throw "Watchdog build failed for $arch." }
}
$watchdogBinary = Join-Path $watchdogDir "OpenTaskbarWatchdog.exe"
if (-not (Test-Path -LiteralPath $watchdogBinary -PathType Leaf)) {
    throw "Watchdog binary is missing: $watchdogBinary"
}

$projectPath = "$PSScriptRoot\..\OpenTaskbar.csproj"

dotnet publish $projectPath `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PlatformTarget=$arch `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=false `
    -p:EnableCompressionInSingleFile=false `
    -p:DebugType=None `
    "-p:WatchdogBinary=$watchdogBinary" `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# Keep both architectures side by side in the same folder with distinct names.
$published = Join-Path $OutputDir 'OpenTaskbar.exe'
$finalExe = Join-Path $OutputDir "OpenTaskbar-$arch.exe"
if (Test-Path -LiteralPath $finalExe) { Remove-Item -LiteralPath $finalExe -Force }
Move-Item -LiteralPath $published -Destination $finalExe -Force
Write-Host "Publish succeeded ($Runtime)! Portable executable is ready at: $finalExe" -ForegroundColor Green
