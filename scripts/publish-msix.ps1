# OpenTaskbar Microsoft Store MSIX Publisher
# Builds a self-contained x64 MSIX package ready for Microsoft Partner Center upload.
param (
    [string]$OutputDir = "$PSScriptRoot\..\release\msix",
    [switch]$SignForLocalTest
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$projectPath = Join-Path $repoRoot "OpenTaskbar.csproj"
$manifestTemplate = Join-Path $repoRoot "packaging\msix\AppxManifest.xml"
$assetsDir = Join-Path $repoRoot "packaging\msix\Assets"

# Read <Version> from OpenTaskbar.csproj and format as 4-part MSIX version (X.Y.Z.0)
[xml]$csproj = Get-Content $projectPath
$rawVersion = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).Trim()
if (-not $rawVersion) { throw "Could not read <Version> from OpenTaskbar.csproj." }
$parts = $rawVersion.Split('.')
while ($parts.Length -lt 4) { $parts += '0' }
$msixVersion = ($parts[0..3]) -join '.'

# Locate makeappx.exe from Windows 10/11 SDK
$makeAppx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like '*\x64\*' } |
    Sort-Object FullName |
    Select-Object -Last 1 -ExpandProperty FullName
if (-not $makeAppx) { throw "makeappx.exe not found. Install the Windows 10/11 SDK." }

# Build native watchdog binary first
$watchdogScript = Join-Path $repoRoot "tools\watchdog\build.ps1"
if (Test-Path $watchdogScript) {
    & powershell -ExecutionPolicy Bypass -File $watchdogScript
}

$stagingDir = Join-Path $OutputDir "staging"
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

Write-Host "Publishing OpenTaskbar v$msixVersion for MSIX (self-contained x64)..." -ForegroundColor Cyan
dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -o $stagingDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Include signed-package-local watchdog binary next to OpenTaskbar.exe
$watchdogExe = Join-Path $repoRoot "tools\watchdog\OpenTaskbarWatchdog.exe"
if (Test-Path $watchdogExe) {
    Copy-Item $watchdogExe -Destination (Join-Path $stagingDir "OpenTaskbarWatchdog.exe") -Force
}

# Remove debug symbols (.pdb) from Store package
Get-ChildItem $stagingDir -Filter "*.pdb" -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

# Copy MSIX visual assets
$stagingAssets = Join-Path $stagingDir "Assets"
Copy-Item $assetsDir -Destination $stagingAssets -Recurse -Force

# Copy AppxManifest.xml with synced 4-part version
[xml]$manifest = Get-Content $manifestTemplate -Raw
$manifest.Package.Identity.Version = $msixVersion
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText((Join-Path $stagingDir "AppxManifest.xml"), $manifest.OuterXml, $utf8NoBom)

$msixPath = Join-Path $OutputDir "OpenTaskbar_${msixVersion}_x64.msix"
Write-Host "Packing MSIX with $makeAppx..." -ForegroundColor Cyan
& $makeAppx pack /d $stagingDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed with exit code $LASTEXITCODE" }

if ($SignForLocalTest) {
    $signTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\x64\*' } |
        Sort-Object FullName |
        Select-Object -Last 1 -ExpandProperty FullName
    if (-not $signTool) { throw "signtool.exe not found for local test signing." }

    $publisher = $manifest.Package.Identity.Publisher
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher } | Select-Object -First 1
    if (-not $cert) {
        Write-Host "Creating self-signed local test certificate for $publisher..." -ForegroundColor Yellow
        $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
            -FriendlyName "OpenTaskbar Local MSIX Test" -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    }
    & $signTool sign /fd SHA256 /sha1 $cert.Thumbprint $msixPath
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
    Write-Host "Signed local test MSIX: $msixPath" -ForegroundColor Green
} else {
    Write-Host "Unsigned Store-ready MSIX created: $msixPath" -ForegroundColor Green
}
