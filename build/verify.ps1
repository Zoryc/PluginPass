<#
.SYNOPSIS
    Compiles the plugin against a real VaM install and checks it against the rules
    VaM enforces at runtime.

.DESCRIPTION
    VaM does not just compile a plugin - it compiles it and then runs the assembly
    through DynamicCSharp's security checker. Both stages can reject code that a
    normal C# compiler is perfectly happy with, and both only report at load time,
    inside the game. This script moves those failures to the desk.

    Three checks:

    1. It compiles at all, against the real Assembly-CSharp and UnityEngine modules,
       with the language version pinned below VaM's C# 6 ceiling.

    2. No restricted namespace is referenced. DynamicCSharp compares
       TypeReference.Namespace against a restriction list with String.Compare - an
       exact match, so System.Reflection is banned but MVR.FileManagementSecure is
       fine.

    3. No enum declared anywhere in the plugin. VaM's runtime (Unity 2018's legacy
       Mono) crashes while laying out any type that has a field of a plugin-defined
       enum - a mono.dll access violation, not an error message - and the installed
       plugin corpus (7,762 files) contains no enum used that way. Nested or not,
       declared first or last: the only safe count is zero.

    4. For a single-file plugin (no .cslist under Custom/), the first type in the
       assembly derives from MVRScript. VaM loads a lone .cs through DynamicCSharp's
       MainType, which is Assembly.GetTypes()[0] - the first type declared in the
       file - and logs "Script loaded at ... must inherit from MVRScript" without
       creating the plugin when that type is a helper.

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

# Paths are built with [IO.Path]::Combine, not Join-Path. Join-Path resolves the drive
# and THROWS when it does not exist - on a Linux CI runner there is no D: at all, and
# on a Windows machine without that drive the same thing happens - and with
# ErrorActionPreference = Stop that killed the build before it could reach the
# "no VaM install found - skipping" path below. Combine is pure string work, and
# Test-Path just answers False for a drive that is not there.
if (-not $VamPath) {
    foreach ($candidate in @('D:\MeshedVR', 'C:\MeshedVR', 'D:\VaM', 'C:\VaM')) {
        if (Test-Path -LiteralPath ([IO.Path]::Combine($candidate, 'VaM_Data\Managed\Assembly-CSharp.dll'))) {
            $VamPath = $candidate
            break
        }
    }
}

$managed = if ($VamPath) { [IO.Path]::Combine($VamPath, 'VaM_Data\Managed') } else { $null }

if (-not $managed -or -not (Test-Path -LiteralPath ([IO.Path]::Combine($managed, 'Assembly-CSharp.dll')))) {
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

$outDll = Join-Path ([System.IO.Path]::GetTempPath()) ('diviningrod-verify-' + [System.Guid]::NewGuid().ToString('N') + '.dll')

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
        'System.Runtime.InteropServices.ComVisibleAttribute',
        # csc writes a dangling TypeRef for the [Out]/[In] pseudo-attributes on
        # out/ref parameters - a metadata flag, no CustomAttribute row. VaM's own
        # compiler (mcs) does not, and plugins with 'out' parameters load fine.
        'System.Runtime.InteropServices.OutAttribute',
        'System.Runtime.InteropServices.InAttribute'
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

    # Nested types in general are fine - the installed corpus is full of them. Enums
    # are not: a field whose type is an enum defined in the plugin makes VaM's Mono
    # runtime read a null enum base type while laying out the holding class
    # (mono_type_size on NULL inside TypeBuilder.CreateType). An earlier version of
    # this check only rejected an enum nested in the MVRScript class; a namespace-level
    # enum declared before the class that used it crashed VaM just the same, so every
    # enum declaration is rejected now. Use int constants.
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($outDll)

    $fatal = @(); $noted = @()
    foreach ($t in $asm.MainModule.GetTypes()) {
        if ($t.Name -eq '<Module>') { continue }
        if ($t.IsEnum) { $fatal += $t.FullName; continue }
        if ($t.IsNested) { $noted += $t.FullName }
    }

    foreach ($n in $noted) { Write-Host "    nested type (allowed, noted): $n" }
    if ($fatal.Count -gt 0) {
        foreach ($n in $fatal) { Write-Host "    enum declared in the plugin: $n" }
        throw 'verify: the plugin declares an enum - VaM crashes laying out a type that uses one. Use int constants.'
    }
    Write-Host '  no enums          OK'

    # -----------------------------------------------------------------------
    # 4. single .cs: the first type must be the MVRScript
    # -----------------------------------------------------------------------

    # VaM loads a lone .cs with ScriptDomain.CompileAndLoadScriptSource, which returns
    # ScriptAssembly.MainType = Assembly.GetTypes()[0], and requires that one type to
    # inherit MVRScript. A .cslist goes through FindAllSubtypesOf<MVRScript>() instead,
    # so the rule only applies when there is no .cslist. csc, like mcs, emits types in
    # declaration order, so the first type here is the first type in the file.
    $cslists = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'Custom') -Recurse -Filter *.cslist)
    if ($cslists.Count -eq 0) {
        $resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
        $resolver.AddSearchDirectory($managed)
        $rp = New-Object Mono.Cecil.ReaderParameters
        $rp.AssemblyResolver = $resolver
        $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($outDll, $rp)

        function DerivesFromMVRScript($type) {
            $b = $type.BaseType
            for ($i = 0; $i -lt 8 -and $null -ne $b; $i++) {
                if ($b.Name -eq 'MVRScript') { return $true }
                try { $b = $b.Resolve().BaseType } catch { return $false }
            }
            return $false
        }

        $first = $asm.MainModule.Types | Where-Object { $_.Name -ne '<Module>' } | Select-Object -First 1
        if ($null -eq $first) { throw 'verify: the assembly contains no types.' }
        if (-not (DerivesFromMVRScript $first)) {
            Write-Host "    first type in the assembly: $($first.FullName)"
            throw "verify: '$($first.FullName)' is the first type declared but is not an MVRScript - VaM would refuse the plugin ('must inherit from MVRScript'). Declare the MVRScript class first; helpers after it or nested."
        }
        Write-Host "  first type        OK ($($first.FullName))"
    } else {
        Write-Host '  first type        skipped (.cslist plugin: VaM uses FindAllSubtypesOf<MVRScript>)'
    }
}
finally {
    Remove-Item -LiteralPath $outDll -Force -ErrorAction SilentlyContinue
}

Write-Host '  verified against VaM at' $VamPath
