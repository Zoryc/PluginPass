<#
.SYNOPSIS
    Generates docs/VAM_PLUGIN_API.md from a real VaM install.

.DESCRIPTION
    The API reference half of the document is read straight out of
    VaM_Data/Managed/Assembly-CSharp.dll with Mono.Cecil, so it cannot drift from
    the game. The guide half is prose maintained in this script.

    Read-only: this script only ever reads from the VaM install.

.PARAMETER VamPath
    VaM install directory. Defaults to $env:VAM_PATH, then the usual suspects.
#>
[CmdletBinding()]
param(
    [string] $VamPath,
    [string] $OutFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutFile) { $OutFile = Join-Path $RepoRoot 'docs\VAM_PLUGIN_API.md' }

if (-not $VamPath) { $VamPath = $env:VAM_PATH }
if (-not $VamPath) {
    foreach ($c in @('D:\MeshedVR', 'C:\MeshedVR', 'D:\VaM', 'C:\VaM')) {
        if (Test-Path -LiteralPath (Join-Path $c 'VaM_Data\Managed\Assembly-CSharp.dll')) { $VamPath = $c; break }
    }
}
$managed = Join-Path $VamPath 'VaM_Data\Managed'
if (-not (Test-Path -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll'))) {
    throw "No VaM install found. Set VAM_PATH."
}

Add-Type -Path (Join-Path $managed 'Mono.Cecil.dll')
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($managed)
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'Assembly-CSharp.dll'), $rp)

# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

$primitives = @{
    'Void' = 'void'; 'Boolean' = 'bool'; 'String' = 'string'; 'Single' = 'float'
    'Double' = 'double'; 'Int32' = 'int'; 'Int64' = 'long'; 'UInt32' = 'uint'
    'Byte' = 'byte'; 'Char' = 'char'; 'Object' = 'object'; 'Decimal' = 'decimal'
}

# Renders a Cecil TypeReference the way a C# author would write it, so generic
# arguments survive: List`1<String> -> List<string>.
function TypeName($tr) {
    if ($null -eq $tr) { return 'void' }
    if ($tr -is [Mono.Cecil.GenericInstanceType]) {
        $bare = ($tr.Name -replace '`\d+', '')
        $args = @()
        foreach ($ga in $tr.GenericArguments) { $args += (TypeName $ga) }
        return $bare + '<' + ($args -join ', ') + '>'
    }
    if ($tr.IsArray) { return (TypeName $tr.ElementType) + '[]' }
    if ($tr.IsByReference) { return (TypeName $tr.ElementType) }
    $n = $tr.Name
    if ($primitives.ContainsKey($n)) { return $primitives[$n] }
    return ($n -replace '`\d+', '')
}

function ParamList($m) {
    $ps = @()
    foreach ($p in $m.Parameters) {
        $t = TypeName $p.ParameterType
        if ($p.IsOut) { $t = 'out ' + $t }
        elseif ($p.ParameterType.IsByReference) { $t = 'ref ' + $t }
        $ps += ($t + ' ' + $p.Name)
    }
    return ($ps -join ', ')
}

# 'protected' matters here: plugins subclass MVRScript, so its protected surface is
# part of the contract you are coding against.
function AccessOf($m) {
    if ($m.IsFamily -or $m.IsFamilyOrAssembly) { return 'protected ' }
    return ''
}

function SigOf($m) {
    $prefix = AccessOf $m
    if ($m.IsStatic) { $prefix += 'static ' }
    return $prefix + (TypeName $m.ReturnType) + ' ' + $m.Name + '(' + (ParamList $m) + ')'
}

function DumpType([string] $typeName, [System.Text.StringBuilder] $sb) {
    $t = $asm.MainModule.GetType($typeName)
    if ($null -eq $t) {
        foreach ($candidate in $asm.MainModule.GetTypes()) {
            if ($candidate.FullName -eq $typeName -or $candidate.Name -eq $typeName) { $t = $candidate; break }
        }
    }
    if ($null -eq $t) { [void]$sb.AppendLine("### $typeName`n`n_not found in this build_`n"); return }

    $baseName = if ($t.BaseType) { TypeName $t.BaseType } else { '-' }
    [void]$sb.AppendLine("### $($t.FullName)")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("extends ``$baseName``")

    $ifaces = @()
    foreach ($i in $t.Interfaces) {
        $it = if ($i.PSObject.Properties.Name -contains 'InterfaceType') { $i.InterfaceType } else { $i }
        $ifaces += (TypeName $it)
    }
    if ($ifaces.Count -gt 0) {
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("implements ``" + (($ifaces | Sort-Object) -join '`, `') + "``")
    }
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine('```csharp')

    # Constructors first - for the JSONStorable* family these are the signatures you
    # reach for constantly, and they are easy to get wrong from memory.
    $ctors = @($t.Methods | Where-Object {
        $_.IsConstructor -and -not $_.IsStatic -and ($_.IsPublic -or $_.IsFamily)
    } | Sort-Object { $_.Parameters.Count })
    if ($ctors.Count -gt 0) {
        [void]$sb.AppendLine("// constructors")
        foreach ($c in $ctors) {
            [void]$sb.AppendLine((AccessOf $c) + 'new ' + (TypeName $t) + '(' + (ParamList $c) + ')')
        }
        [void]$sb.AppendLine("")
    }

    $fields = @($t.Fields | Where-Object { $_.IsPublic -or $_.IsFamily } | Sort-Object Name)
    if ($fields.Count -gt 0) {
        [void]$sb.AppendLine("// fields")
        foreach ($f in $fields) {
            $st = ''
            if ($f.IsFamily) { $st += 'protected ' }
            if ($f.IsStatic) { $st += 'static ' }
            [void]$sb.AppendLine("$st$(TypeName $f.FieldType) $($f.Name)")
        }
        [void]$sb.AppendLine("")
    }

    $props = @($t.Properties | Sort-Object Name)
    if ($props.Count -gt 0) {
        $lines = @()
        foreach ($p in $props) {
            $get = $p.GetMethod; $set = $p.SetMethod
            $vis = ''
            $acc = ''
            if ($get -and ($get.IsPublic -or $get.IsFamily)) {
                $acc += 'get;'
                if ($get.IsFamily) { $vis = 'protected ' }
            }
            if ($set -and ($set.IsPublic -or $set.IsFamily)) {
                $acc += ' set;'
                if ($set.IsFamily -and $vis -eq '') { $vis = 'protected ' }
            }
            if ($acc -eq '') { continue }
            $lines += "$vis$(TypeName $p.PropertyType) $($p.Name) { $($acc.Trim()) }"
        }
        if ($lines.Count -gt 0) {
            [void]$sb.AppendLine("// properties")
            foreach ($l in $lines) { [void]$sb.AppendLine($l) }
            [void]$sb.AppendLine("")
        }
    }

    $methods = @($t.Methods | Where-Object {
        ($_.IsPublic -or $_.IsFamily) -and -not $_.IsGetter -and -not $_.IsSetter -and -not $_.IsConstructor
    } | Sort-Object Name)
    if ($methods.Count -gt 0) {
        [void]$sb.AppendLine("// methods")
        foreach ($m in $methods) { [void]$sb.AppendLine((SigOf $m)) }
        [void]$sb.AppendLine("")
    }

    $nested = @($t.NestedTypes | Where-Object { $_.IsNestedPublic })
    if ($nested.Count -gt 0) {
        [void]$sb.AppendLine("// nested types")
        foreach ($n in $nested) {
            $kind = if ($n.IsEnum) { 'enum' } elseif ($n.BaseType -and $n.BaseType.Name -eq 'MulticastDelegate') { 'delegate' } else { 'class' }
            $extra = ''
            if ($n.IsEnum) {
                $vals = @($n.Fields | Where-Object { $_.HasConstant } | ForEach-Object { $_.Name })
                $extra = ' { ' + ($vals -join ', ') + ' }'
            }
            elseif ($kind -eq 'delegate') {
                $inv = $n.Methods | Where-Object { $_.Name -eq 'Invoke' } | Select-Object -First 1
                if ($inv) {
                    $ps = @($inv.Parameters | ForEach-Object { (TypeName $_.ParameterType) + ' ' + $_.Name })
                    $extra = '(' + ($ps -join ', ') + ')'
                }
            }
            [void]$sb.AppendLine("$kind $($n.Name)$extra")
        }
        [void]$sb.AppendLine("")
    }

    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine("")
}

# ---------------------------------------------------------------------------
# which types to document, in the order a plugin author meets them
# ---------------------------------------------------------------------------

$groups = [ordered]@{
    'Your plugin' = @('MVRScript', 'JSONStorable', 'MVRScriptAbstract')
    # NOTE: there is no JSONStorableInt - use JSONStorableFloat, which carries an
    # isStorable/isRestorable pair and can be constrained to whole numbers.
    'Storable parameters (the plugin data model)' = @(
        'JSONStorableParam', 'JSONStorableBool', 'JSONStorableFloat',
        'JSONStorableString', 'JSONStorableStringChooser', 'JSONStorableUrl',
        'JSONStorableColor', 'JSONStorableVector3', 'JSONStorableAngle',
        'JSONStorableAction', 'JSONStorableActionStringChooser',
        'JSONStorableActionAudioClip', 'JSONStorableTriggerHandler',
        'JSONStorableDynamic'
    )
    # CreateSpacer returns a plain UIDynamic - there is no UIDynamicSpacer.
    'UI widgets' = @(
        'UIDynamic', 'UIDynamicButton', 'UIDynamicToggle', 'UIDynamicSlider',
        'UIDynamicTextField', 'UIDynamicPopup', 'UIDynamicColorPicker'
    )
    'The world' = @('SuperController', 'Atom', 'SubScene', 'FreeControllerV3')
    'Plugin hosting internals' = @('MVRPluginManager', 'MVRPlugin', 'MVRScriptController')
    'Files (the only sanctioned IO)' = @('MVR.FileManagementSecure.FileManagerSecure')
    'JSON' = @('SimpleJSON.JSONNode', 'SimpleJSON.JSONClass', 'SimpleJSON.JSONArray',
               'SimpleJSON.JSONData', 'SimpleJSON.JSON')
    'Triggers' = @('Trigger', 'TriggerHandler', 'TriggerAction', 'TriggerActionDiscrete', 'TriggerActionTransition')
    'Characters and morphs' = @('DAZCharacterSelector', 'DAZMorph', 'DAZMorphBank', 'GenerateDAZMorphsControlUI', 'DAZSkinV2', 'DAZBone')
    'Audio' = @('AudioSourceControl', 'NamedAudioClip', 'URLAudioClip')
    'Colour' = @('HSVColor', 'HSVColorPicker')
    'Assets, images, presets, preferences' = @(
        'AssetLoader', 'AssetBundleManager', 'AssetBundleLoadAssetOperation',
        'ImageLoaderThreaded', 'PresetManager', 'PresetManagerControl',
        'UserPreferences', 'DAZClothingItem', 'DAZHairGroup', 'CameraTarget',
        'AsyncFlag', 'ShortCut', 'UITabSelector', 'MotionAnimationControl'
    )
}

# ---------------------------------------------------------------------------
# build the document
# ---------------------------------------------------------------------------

$guide = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'api-doc-guide.md') -Raw

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append($guide)
[void]$sb.AppendLine("")
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("# Part 2 - Generated API reference")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Read straight out of ``Assembly-CSharp.dll``. Public members only.")
[void]$sb.AppendLine("Regenerate with ``pwsh ./build/gen-api-doc.ps1``.")
[void]$sb.AppendLine("")

foreach ($g in $groups.Keys) {
    [void]$sb.AppendLine("## $g")
    [void]$sb.AppendLine("")
    foreach ($tn in $groups[$g]) { DumpType $tn $sb }
}

# appendix: every public type name, so anything not documented above can be found
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Appendix - every public type in Assembly-CSharp")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Not documented in full above. To dump one, add it to ``\$groups`` in")
[void]$sb.AppendLine("``build/gen-api-doc.ps1`` and regenerate.")
[void]$sb.AppendLine("")

$byNs = @{}
foreach ($t in $asm.MainModule.GetTypes()) {
    if (-not $t.IsPublic) { continue }
    $ns = if ([string]::IsNullOrEmpty($t.Namespace)) { '(global)' } else { $t.Namespace }
    if (-not $byNs.ContainsKey($ns)) { $byNs[$ns] = New-Object System.Collections.ArrayList }
    [void]$byNs[$ns].Add($t.Name)
}
foreach ($ns in ($byNs.Keys | Sort-Object)) {
    [void]$sb.AppendLine("**$ns** (" + $byNs[$ns].Count + ")")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine('```')
    # -CaseSensitive matters: the assembly contains pairs that differ only in case
    # (OvrAvatarComponent / ovrAvatarComponent). Plain -Unique would drop one.
    $names = @($byNs[$ns] | Sort-Object -Unique -CaseSensitive)
    for ($i = 0; $i -lt $names.Count; $i += 4) {
        $row = @()
        for ($k = $i; $k -lt [Math]::Min($i + 4, $names.Count); $k++) { $row += $names[$k].PadRight(34) }
        [void]$sb.AppendLine((($row -join '') -replace '\s+$', ''))
    }
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine("")
}

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllText($OutFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

$size = (Get-Item -LiteralPath $OutFile).Length
Write-Host ("  wrote {0} ({1:N0} bytes) from VaM at {2}" -f $OutFile, $size, $VamPath)
