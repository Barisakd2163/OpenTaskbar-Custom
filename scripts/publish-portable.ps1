# OpenTaskbar Portable Single-File Publisher
# Builds a self-contained single-file executable for Windows x64.
param (
    [string]$OutputDir = "$PSScriptRoot\..\release"
)

$ErrorActionPreference = "Stop"

# Gracefully request running instance to close, allowing it to restore Windows taskbar cleanly
$running = Get-Process -Name OpenTaskbar -ErrorAction SilentlyContinue
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

# Always start from an empty output directory so stale DLLs from a previous
# publish cannot trip check-portable.ps1 or end up in the release artifact.
# Resolve relative paths against the repository root, not the current location,
# so CI (D:\a\<repo>\<repo>) and local runs behave identically.
$repoRoot = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
$resolvedOutput = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir
} else {
    Join-Path $repoRoot $OutputDir
}
if (Test-Path -LiteralPath $resolvedOutput -ErrorAction SilentlyContinue) {
    # A stale locked file must not abort an otherwise valid publish; check-portable.ps1
    # is the authoritative guard against leftover sidecar DLLs.
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

# Show a repo-relative path in the logs instead of the runner's absolute
# workspace path (e.g. D:\a\opentaskbar\opentaskbar\release -> release\).
$displayOutput = $resolvedOutput
if ($displayOutput.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    $displayOutput = $displayOutput.Substring($repoRoot.Length).TrimStart('\', '/')
    if ([string]::IsNullOrWhiteSpace($displayOutput)) { $displayOutput = '.' }
}

$watchdogScript = "$PSScriptRoot\..\tools\watchdog\build.ps1"
if (Test-Path $watchdogScript) {
    & powershell -ExecutionPolicy Bypass -File $watchdogScript
}

$projectPath = "$PSScriptRoot\..\OpenTaskbar.csproj"

Write-Host "Publishing to '$displayOutput'..." -ForegroundColor Cyan

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=false `
    -p:EnableCompressionInSingleFile=false `
    -o $resolvedOutput

if ($LASTEXITCODE -eq 0) {
    Write-Host "Publish succeeded! Portable executable is ready at: $displayOutput\OpenTaskbar.exe" -ForegroundColor Green
} else {
    Write-Error "Publish failed with exit code $LASTEXITCODE"
}
