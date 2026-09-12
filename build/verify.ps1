<#
.SYNOPSIS
    Compiles the plugin against a real VaM install and checks it against the rules
    VaM enforces at runtime.

.DESCRIPTION
    VaM does not just compile a plugin - it compiles it and then runs the assembly
    through DynamicCSharp's security checker. Both stages can reject code that a
    normal C# compiler is perfectly happy with, and both only report at load time,
    inside the game. This script moves those failures to the desk.

    Three checks, each one earned by an actual failure:

    1. It compiles at all, against the real Assembly-CSharp and UnityEngine modules.

    2. No restricted namespace is referenced. DynamicCSharp compares
       TypeReference.Namespace against a restriction list with String.Compare - an
       exact match, so System.Reflection is banned but MVR.FileManagementSecure is
       fine. Reflection being banned is why this plugin cannot use Harmony.
       This also catches indirect references: an 'out' parameter compiles to a
       System.Runtime.InteropServices.OutAttribute, which is restricted.

    3. Zero nested types. VaM's runtime compiler can fault inside
       TypeBuilder.CreateType() while closing a nested type container, taking the
       whole game with it - a hard crash, not an error message.

    Skipped with a warning when no VaM install is found, so CI still runs.

.PARAMETER VamPath
    VaM install directory. Defaults to $env:VAM_PATH, then a few usual suspects.
#>
[CmdletBinding()]
param(
    [string] $VamPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------------------
# locate VaM
# ---------------------------------------------------------------------------

if (-not $VamPath) { $VamPath = $env:VAM_PATH }
if (-not $VamPath) {
    foreach ($candidate in @('D:\MeshedVR', 'C:\MeshedVR', 'D:\VaM', 'C:\VaM')) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'VaM_Data\Managed\Assembly-CSharp.dll')) {
            $VamPath = $candidate
            break
        }
    }
}

$managed = if ($VamPath) { Join-Path $VamPath 'VaM_Data\Managed' } else { $null }

if (-not $managed -or -not (Test-Path -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll'))) {
    Write-Warning 'verify: no VaM install found - skipping. Set VAM_PATH to enable.'
    exit 0
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    Write-Warning 'verify: csc.exe not found - skipping.'
    exit 0
}

# ---------------------------------------------------------------------------
# 1. compile
# ---------------------------------------------------------------------------

$sources = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'Custom') -Recurse -Filter *.cs |
             ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw 'verify: no sources under Custom/.' }

$refNames = @(
    'Assembly-CSharp.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll',
    'UnityEngine.UI.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.TextRenderingModule.dll',
    'UnityEngine.IMGUIModule.dll', 'UnityEngine.InputModule.dll', 'UnityEngine.PhysicsModule.dll',
    'UnityEngine.AnimationModule.dll', 'UnityEngine.AudioModule.dll',
    'UnityEngine.JSONSerializeModule.dll', 'UnityEngine.SharedInternalsModule.dll'
)
$refArgs = @($refNames | ForEach-Object { '/r:' + (Join-Path $managed $_) })

$outDll = Join-Path ([System.IO.Path]::GetTempPath()) ('pluginpass-verify-' + [System.Guid]::NewGuid().ToString('N') + '.dll')

# VaM's mcs tops out at C# 6; /langversion:5 keeps a margin.
& $csc /nologo /target:library /langversion:5 /warn:4 "/out:$outDll" $refArgs $sources
if ($LASTEXITCODE -ne 0) { throw 'verify: compilation failed.' }
Write-Host '  compile           OK'

try {
    # -----------------------------------------------------------------------
    # 2 + 3. sandbox and nesting
    # -----------------------------------------------------------------------

    Add-Type -Path (Join-Path $managed 'Mono.Cecil.dll')
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($outDll)

    # DynamicCSharp namespaceRestrictions, read out of VaM_Data/resources.assets.
    $restricted = @(
        'System.IO', 'System.Reflection', 'MVR.FileManagement', 'DynamicCSharp',
        'System.Configuration', 'System.Data', 'System.Deployment', 'System.Dynamic',
        'System.Management', 'System.Resources', 'System.Security', 'System.Runtime',
        'System.Runtime.InteropServices', 'System.Net.Mail',
        'Battlehub.RTSaveLoad', 'ZenFulcrum.EmbeddedBrowser'
    )
    $allowed = @(
        'System.IO.Stream', 'System.IO.IOException',
        'System.Runtime.InteropServices.GuidAttribute',
        'System.Runtime.InteropServices.ComVisibleAttribute'
    )

    $violations = @()
    foreach ($tr in $asm.MainModule.GetTypeReferences()) {
        if ($restricted -contains $tr.Namespace -and $allowed -notcontains $tr.FullName) {
            $violations += $tr.FullName
        }
    }

    if ($violations.Count -gt 0) {
        foreach ($v in ($violations | Sort-Object -Unique)) {
            Write-Host "    restricted reference: $v"
        }
        throw 'verify: the assembly references a namespace VaM forbids - it would be rejected at load time.'
    }
    Write-Host '  sandbox           OK'

    $nested = @()
    foreach ($t in $asm.MainModule.Types) {
        if ($t.Name -eq '<Module>') { continue }
        foreach ($n in $t.NestedTypes) { $nested += ($t.FullName + '/' + $n.Name) }
    }

    if ($nested.Count -gt 0) {
        foreach ($n in $nested) { Write-Host "    nested type: $n" }
        throw 'verify: nested types present - VaM''s compiler can hard-crash closing these.'
    }
    Write-Host '  no nested types   OK'
}
finally {
    Remove-Item -LiteralPath $outDll -Force -ErrorAction SilentlyContinue
}

Write-Host '  verified against VaM at' $VamPath
