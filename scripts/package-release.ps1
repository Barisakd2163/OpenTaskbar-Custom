# Packages built x64/x86 executables into the final release artifacts.
# Everything is written under release/: named EXEs, deterministic ZIPs, checksums,
# release manifest and release notes. Used by CI and usable locally for verification.
param (
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$RepoRoot = '',
    [string]$ReleaseDir = '',
    [string]$Commit = '',
    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue

if (-not $RepoRoot) { $RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path }
if (-not $ReleaseDir) { $ReleaseDir = Join-Path $RepoRoot 'release' }
if (-not (Test-Path -LiteralPath $ReleaseDir -PathType Container)) {
    throw "Release directory does not exist: $ReleaseDir"
}

if ($Tag -notmatch '^v(?<base>\d+\.\d+\.\d+)(?<pre>-[0-9A-Za-z.-]+)?$') {
    throw "Tag must look like v0.3.2 or v0.3.2-rc.1 (got: $Tag)."
}
$version = $Matches['base']

[xml]$project = Get-Content (Join-Path $RepoRoot 'OpenTaskbar.csproj') -Raw
$projectVersion = ($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).Trim()
if ("v$projectVersion" -ne "v$version") {
    throw "Tag $Tag does not match project version $projectVersion."
}

function Get-Sha256 {
    param ([byte[]]$Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        ([BitConverter]::ToString($sha.ComputeHash($Bytes)) -replace '-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}
function Get-FileSha256 {
    param ([string]$Path)
    Get-Sha256 -Bytes ([IO.File]::ReadAllBytes($Path))
}
function Write-Utf8NoBom {
    param ([string]$Path, [string[]]$Lines)
    [IO.File]::WriteAllText($Path, (($Lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
}

# ZIP entries get a fixed timestamp and fixed order so the bytes only depend on content.
function New-DeterministicZip {
    param ([string]$Destination, [System.Collections.Specialized.OrderedDictionary]$Entries)

    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    $fileStream = [IO.File]::Create($Destination)
    try {
        $zip = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $fixedTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            foreach ($name in $Entries.Keys) {
                $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTime
                $sourceStream = $entry.Open()
                try {
                    $bytes = [IO.File]::ReadAllBytes($Entries[$name])
                    $sourceStream.Write($bytes, 0, $bytes.Length)
                } finally {
                    $sourceStream.Dispose()
                }
            }
        } finally {
            $zip.Dispose()
        }
    } finally {
        $fileStream.Dispose()
    }
}

function Assert-ZipIntegrity {
    param ([string]$ZipPath, [string]$ExpectedExeHash)

    $stream = [IO.File]::OpenRead($ZipPath)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
        try {
            $names = @($zip.Entries | ForEach-Object FullName)
            $expected = @('OpenTaskbar.exe', 'LICENSE', 'README.md')
            if (($names -join ',') -ne ($expected -join ',')) {
                throw "Unexpected ZIP contents: $($names -join ', ')"
            }
            $memory = [IO.MemoryStream]::new()
            try {
                $zip.GetEntry('OpenTaskbar.exe').Open().CopyTo($memory)
                $actual = Get-Sha256 -Bytes $memory.ToArray()
                if ($actual -ne $ExpectedExeHash) {
                    throw "ZIP integrity check failed: packaged EXE hash $actual != $ExpectedExeHash"
                }
            } finally {
                $memory.Dispose()
            }
        } finally {
            $zip.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Get-ReleaseNotes {
    param ([string]$ChangelogPath, [string]$Version, [string]$TagName)

    if (Test-Path -LiteralPath $ChangelogPath) {
        $lines = @(Get-Content -LiteralPath $ChangelogPath)
        foreach ($heading in @("^##\s+$([regex]::Escape($Version))(\s|$)", '^##\s+Unreleased(\s|$)')) {
            $start = -1
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match $heading) { $start = $i; break }
            }
            if ($start -lt 0) { continue }

            $end = $lines.Count
            for ($j = $start + 1; $j -lt $lines.Count; $j++) {
                if ($lines[$j] -match '^##\s+') { $end = $j; break }
            }
            if ($end -le $start + 1) { continue }

            $section = @($lines[($start + 1)..($end - 1)])
            while ($section.Count -gt 0 -and [string]::IsNullOrWhiteSpace($section[0])) {
                $section = @($section[1..($section.Count - 1)])
            }
            while ($section.Count -gt 0 -and [string]::IsNullOrWhiteSpace($section[-1])) {
                $section = @($section[0..($section.Count - 2)])
            }
            if ($section.Count -gt 0) { return "## OpenTaskbar $TagName`n`n" + ($section -join "`n") }
        }
    }
    return "## OpenTaskbar $TagName`n`nNo changelog entry found for this version; see the commit history."
}

# Remove only artifacts this script owns, so repeated runs are reproducible.
Get-ChildItem -LiteralPath $ReleaseDir -File -Filter "OpenTaskbar-$Tag-*" | Remove-Item -Force
foreach ($name in @('checksums.txt', 'release-manifest.json', 'release-notes.md')) {
    $path = Join-Path $ReleaseDir $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}

$license = Join-Path $RepoRoot 'LICENSE'
$readme = Join-Path $RepoRoot 'README.md'
foreach ($path in @($license, $readme)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing packaging input: $path" }
}

$checksums = [System.Collections.Generic.List[string]]::new()
$assets = [System.Collections.Generic.List[object]]::new()

foreach ($arch in @('x64', 'x86')) {
    $builtExe = Join-Path $ReleaseDir "OpenTaskbar-$arch.exe"
    if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) {
        throw "Missing build output: $builtExe"
    }

    $exeHash = Get-FileSha256 -Path $builtExe
    $assetExeName = "OpenTaskbar-$Tag-win-$arch.exe"
    $assetZipName = "OpenTaskbar-$Tag-win-$arch.zip"
    $assetExe = Join-Path $ReleaseDir $assetExeName
    $assetZip = Join-Path $ReleaseDir $assetZipName

    $entries = [ordered]@{
        'OpenTaskbar.exe' = $builtExe
        'LICENSE'         = $license
        'README.md'       = $readme
    }
    New-DeterministicZip -Destination $assetZip -Entries $entries
    Assert-ZipIntegrity -ZipPath $assetZip -ExpectedExeHash $exeHash
    Copy-Item -LiteralPath $builtExe -Destination $assetExe -Force

    foreach ($item in @(
        @{ Path = $assetExe; Name = $assetExeName; Kind = 'executable' },
        @{ Path = $assetZip; Name = $assetZipName; Kind = 'archive' }
    )) {
        $hash = Get-FileSha256 -Path $item.Path
        $checksums.Add("$hash *$($item.Name)")
        $assets.Add([ordered]@{
            name          = $item.Name
            architecture  = $arch
            kind          = $item.Kind
            size          = (Get-Item -LiteralPath $item.Path).Length
            sha256        = $hash
        })
    }
}

Write-Utf8NoBom -Path (Join-Path $ReleaseDir 'checksums.txt') -Lines $checksums.ToArray()

$manifest = [ordered]@{
    schemaVersion  = 1
    tag            = $Tag
    version        = $version
    prerelease     = [bool]$Prerelease
    commit         = $Commit
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    minimumOS      = 'Windows 10 version 19041 (10.0.19041.0)'
    checksumsFile  = 'checksums.txt'
    assets         = @($assets)
}
$manifestJson = $manifest | ConvertTo-Json -Depth 6
Write-Utf8NoBom -Path (Join-Path $ReleaseDir 'release-manifest.json') -Lines @($manifestJson)

$notes = Get-ReleaseNotes -ChangelogPath (Join-Path $RepoRoot 'CHANGELOG.md') -Version $version -TagName $Tag
Write-Utf8NoBom -Path (Join-Path $ReleaseDir 'release-notes.md') -Lines @($notes)

Write-Host "Packaged $Tag ($($assets.Count) assets) into $ReleaseDir" -ForegroundColor Green
Get-ChildItem -LiteralPath $ReleaseDir -File | Where-Object Name -like "OpenTaskbar-$Tag-*" |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
