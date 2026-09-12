<#
.SYNOPSIS
    Packages this repository into a VaM .var addon package.

.DESCRIPTION
    A .var is a zip with meta.json at the root and the content tree beside it.
    The repository is already laid out exactly like the inside of the package, so
    packaging is "zip meta.json + the content roots" with three guarantees the
    plain zip tools do not give you:

      * entry names always use forward slashes (VaM cannot read a package whose
        entries were written with Windows separators),
      * directory entries are emitted, matching what VaM's own packager writes,
      * meta.json's contentList is verified against what is actually on disk, in
        both directions, so a package can never ship a stale manifest.

    Runs on Windows PowerShell 5.1 and on PowerShell 7+ (including Linux CI).

.PARAMETER Version
    The VaM package version - the integer in Creator.Package.<Version>.var.
    0 means "untagged development build".

.PARAMETER OutDir
    Where to write the .var. Defaults to <repo>/dist.

.EXAMPLE
    ./build/build.ps1 -Version 1
#>
[CmdletBinding()]
param(
    [ValidateRange(0, 2147483647)]
    [int] $Version = 0,

    [string] $OutDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# .NET Framework needs these loaded explicitly, and they are two different
# assemblies: ZipArchive/ZipArchiveMode/CompressionLevel live in
# System.IO.Compression, ZipFile lives in System.IO.Compression.FileSystem.
# On .NET Core (pwsh) both are already in the shared framework.
foreach ($asm in @('System.IO.Compression', 'System.IO.Compression.FileSystem')) {
    try { Add-Type -AssemblyName $asm } catch { }
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'dist' }

# Top-level folders VaM serves out of a package. Only the ones present are used.
$ContentRoots = @('Custom', 'Saves')

# Never ship these, whatever the platform leaves lying around.
$ExcludedNames = @('.DS_Store', 'Thumbs.db', 'desktop.ini')

function Get-RelativePath {
    param([string] $Base, [string] $Full)

    $sep = [System.IO.Path]::DirectorySeparatorChar
    $b = [System.IO.Path]::GetFullPath($Base)
    if (-not $b.EndsWith([string]$sep)) { $b += $sep }
    $f = [System.IO.Path]::GetFullPath($Full)

    if (-not $f.StartsWith($b, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "'$Full' is not inside '$Base'."
    }
    return $f.Substring($b.Length)
}

function ConvertTo-EntryPath { param([string] $P) $P -replace '\\', '/' }
function ConvertTo-ManifestPath { param([string] $P) $P -replace '/', '\' }

# ---------------------------------------------------------------------------
# read and sanity-check meta.json
# ---------------------------------------------------------------------------

$metaPath = Join-Path $RepoRoot 'meta.json'
if (-not (Test-Path -LiteralPath $metaPath)) { throw "meta.json not found at $metaPath" }

$metaRaw = Get-Content -LiteralPath $metaPath -Raw
try { $meta = $metaRaw | ConvertFrom-Json }
catch { throw "meta.json is not valid JSON: $($_.Exception.Message)" }

foreach ($field in @('creatorName', 'packageName', 'licenseType', 'contentList')) {
    if (-not ($meta.PSObject.Properties.Name -contains $field)) {
        throw "meta.json is missing the required field '$field'."
    }
}

$creator = [string]$meta.creatorName
$package = [string]$meta.packageName

foreach ($pair in @(@('creatorName', $creator), @('packageName', $package))) {
    if ([string]::IsNullOrWhiteSpace($pair[1])) { throw "meta.json '$($pair[0])' is empty." }
    # VaM splits a package uid on dots as Creator.Package.Version, so neither half
    # may contain one or every version lookup for this package breaks.
    if ($pair[1].Contains('.')) { throw "meta.json '$($pair[0])' must not contain a dot (got '$($pair[1])')." }
}

if ($meta.licenseType -eq 'MISSING') { throw "meta.json licenseType is 'MISSING'." }

# ---------------------------------------------------------------------------
# collect content and reconcile it with the manifest
# ---------------------------------------------------------------------------

$files = @()
foreach ($root in $ContentRoots) {
    $full = Join-Path $RepoRoot $root
    if (-not (Test-Path -LiteralPath $full)) { continue }
    $files += @(Get-ChildItem -LiteralPath $full -Recurse -File |
                Where-Object { $ExcludedNames -notcontains $_.Name })
}

if ($files.Count -eq 0) {
    throw "No content found under: $($ContentRoots -join ', '). Nothing to package."
}

$actual = @($files | ForEach-Object { ConvertTo-ManifestPath (Get-RelativePath $RepoRoot $_.FullName) })
$declared = @(@($meta.contentList) | ForEach-Object { ConvertTo-ManifestPath ([string]$_) })

$cmp = [System.StringComparer]::OrdinalIgnoreCase
$actualSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$actual, $cmp)
$declaredSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$declared, $cmp)

$missing = @($actual | Where-Object { -not $declaredSet.Contains($_) })
$orphaned = @($declared | Where-Object { -not $actualSet.Contains($_) })

if ($missing.Count -or $orphaned.Count) {
    foreach ($m in $missing) { Write-Host "  on disk but not in meta.json contentList : $m" }
    foreach ($o in $orphaned) { Write-Host "  in meta.json contentList but not on disk : $o" }
    throw "meta.json contentList does not match the repository content."
}

# ---------------------------------------------------------------------------
# build the package
# ---------------------------------------------------------------------------

# Compile and sandbox-check against a real VaM install when one is available.
# Skips itself (with a warning) on CI, where VaM's assemblies cannot be shipped.
& (Join-Path $PSScriptRoot 'verify.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Verification failed - not packaging.' }

if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$varName = '{0}.{1}.{2}.var' -f $creator, $package, $Version
$outPath = Join-Path $OutDir $varName
if (Test-Path -LiteralPath $outPath) { Remove-Item -LiteralPath $outPath -Force }

$entries = @($files | ForEach-Object {
    [PSCustomObject]@{
        Source = $_.FullName
        Entry  = ConvertTo-EntryPath (Get-RelativePath $RepoRoot $_.FullName)
        Stamp  = $_.LastWriteTime
    }
} | Sort-Object Entry)

# Parent directories, parents before children, mirroring VaM's own packager.
$dirs = New-Object 'System.Collections.Generic.List[string]'
foreach ($e in $entries) {
    $parts = $e.Entry.Split('/')
    $acc = ''
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        $acc += $parts[$i] + '/'
        if (-not $dirs.Contains($acc)) { $dirs.Add($acc) }
    }
}

$zip = [System.IO.Compression.ZipFile]::Open($outPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($d in $dirs) { [void]$zip.CreateEntry($d) }

    foreach ($e in $entries) {
        $entry = $zip.CreateEntry($e.Entry, [System.IO.Compression.CompressionLevel]::Optimal)
        # Zip cannot represent timestamps before 1980.
        if ($e.Stamp.Year -ge 1980) { $entry.LastWriteTime = [System.DateTimeOffset]$e.Stamp }
        $out = $entry.Open()
        try {
            $in = [System.IO.File]::OpenRead($e.Source)
            try { $in.CopyTo($out) } finally { $in.Dispose() }
        } finally { $out.Dispose() }
    }

    # meta.json goes in last, byte for byte as it is on disk.
    $metaEntry = $zip.CreateEntry('meta.json', [System.IO.Compression.CompressionLevel]::Optimal)
    $out = $metaEntry.Open()
    try {
        $bytes = [System.IO.File]::ReadAllBytes($metaPath)
        $out.Write($bytes, 0, $bytes.Length)
    } finally { $out.Dispose() }
}
finally { $zip.Dispose() }

$size = (Get-Item -LiteralPath $outPath).Length
Write-Host ''
Write-Host "  package : $varName"
Write-Host "  files   : $($entries.Count) ($($dirs.Count) directory entries)"
Write-Host "  size    : $([math]::Round($size / 1KB, 1)) KB"
Write-Host "  path    : $outPath"
if ($Version -eq 0) { Write-Host '  note    : version 0 - development build, not for release' }
Write-Host ''

# Hand the path back to whatever called us.
$outPath
