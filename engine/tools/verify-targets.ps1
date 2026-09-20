<#
    Checks that every method PluginPassEngine hooks still exists in the shipped
    Assembly-CSharp, with the exact signature AccessTools will be asked for.

    A Harmony patch against a method that has moved or changed shape does not fail
    loudly - AccessTools.Method returns null, harmony.Patch throws, the plugin logs an
    error and VaM carries on as if the engine were not installed. That is a silent
    downgrade to "no engine", so it is worth catching at the desk.

    Also re-checks the two behaviours the patches depend on, straight out of the IL,
    so a VaM update that changes them is noticed here rather than in a broken scene:

      * MVRPluginManager.LateRestoreFromJSON reads jc["plugins"] and iterates its keys
      * Atom.RestoreFromLast has a no-match branch that calls RestoreFromJSON with
        setMissingToDefault = true

    Usage:  .\verify-targets.ps1 [-VamPath D:\MeshedVR]
#>
[CmdletBinding()]
param([string]$VamPath = "D:\MeshedVR")

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$managed = Join-Path $VamPath 'VaM_Data\Managed'
$dll = Join-Path $managed 'Assembly-CSharp.dll'
if (-not (Test-Path $dll)) { Write-Warning "verify-targets: no VaM at $VamPath - skipping."; exit 0 }

Add-Type -Path (Join-Path $managed 'Mono.Cecil.dll')
$res = New-Object Mono.Cecil.DefaultAssemblyResolver
$res.AddSearchDirectory($managed)
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $res
$rp.ReadingMode = [Mono.Cecil.ReadingMode]::Immediate
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll, $rp)

$fail = 0
function Fail($msg) { Write-Host "    FAIL  $msg"; $script:fail++ }
function Ok($msg)   { Write-Host "    ok    $msg" }

function Find-Method($typeName, $methodName, [string[]]$paramTypes) {
    $t = $asm.MainModule.GetType($typeName)
    if ($null -eq $t) { Fail "type $typeName not found"; return $null }
    foreach ($m in $t.Methods) {
        if ($m.Name -ne $methodName) { continue }
        if ($m.Parameters.Count -ne $paramTypes.Count) { continue }
        $match = $true
        for ($i = 0; $i -lt $paramTypes.Count; $i++) {
            if ($m.Parameters[$i].ParameterType.FullName -ne $paramTypes[$i]) { $match = $false; break }
        }
        if ($match) { return $m }
    }
    Fail "$typeName.$methodName($($paramTypes -join ', ')) not found"
    return $null
}

Write-Host '  patch targets'

# ---- 1. MVRPluginManager.LateRestoreFromJSON(JSONClass, bool, bool, bool) ----
$late = Find-Method 'MVRPluginManager' 'LateRestoreFromJSON' @(
    'SimpleJSON.JSONClass', 'System.Boolean', 'System.Boolean', 'System.Boolean')
if ($late) {
    if (-not $late.IsPublic) { Fail 'LateRestoreFromJSON is no longer public' }
    else { Ok 'MVRPluginManager.LateRestoreFromJSON(JSONClass,bool,bool,bool)' }

    $ils = @($late.Body.Instructions)
    $readsPlugins = @($ils | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'plugins' }).Count
    $getsKeys = @($ils | Where-Object { "$($_.Operand)" -like '*JSONClass::get_Keys*' }).Count
    $createsPlugin = @($ils | Where-Object { "$($_.Operand)" -like '*CreatePluginWithId*' }).Count
    if ($readsPlugins -lt 1) { Fail 'LateRestoreFromJSON no longer reads jc["plugins"]' } else { Ok '  reads jc["plugins"]' }
    if ($getsKeys -lt 1)     { Fail 'LateRestoreFromJSON no longer enumerates the plugins object' } else { Ok '  enumerates its keys' }
    if ($createsPlugin -lt 1){ Fail 'LateRestoreFromJSON no longer calls CreatePluginWithId' } else { Ok '  creates a plugin per key' }
}

# ---- 2. Atom.RestoreFromLast(JSONStorable) ----
$rfl = Find-Method 'Atom' 'RestoreFromLast' @('JSONStorable')
if ($rfl) {
    if (-not $rfl.IsPublic) { Fail 'Atom.RestoreFromLast is no longer public' }
    else { Ok 'Atom.RestoreFromLast(JSONStorable)' }

    $ils = @($rfl.Body.Instructions)
    $newEmpty = @($ils | Where-Object { "$($_.Operand)" -like '*JSONClass::.ctor*' }).Count
    $restores = @($ils | Where-Object { "$($_.Operand)" -like '*JSONStorable::RestoreFromJSON*' }).Count
    if ($newEmpty -lt 1) {
        Write-Host '    note  the destructive no-match branch is gone - KeepMySettings may no longer be needed'
    } else { Ok '  still has the empty-JSONClass no-match branch (why KeepMySettings exists)' }
    if ($restores -lt 1) { Fail 'RestoreFromLast no longer calls RestoreFromJSON' }
}

# ---- 3. types and members the engine binds by name ----
Write-Host '  bound members'
foreach ($pair in @(
    @('MVRPluginManager', 'containingAtom'),
    @('Atom', 'uid')
)) {
    $t = $asm.MainModule.GetType($pair[0])
    $hasField = $t -and @($t.Fields | Where-Object { $_.Name -eq $pair[1] }).Count -gt 0
    $hasProp  = $t -and @($t.Properties | Where-Object { $_.Name -eq $pair[1] }).Count -gt 0
    # containingAtom is declared on JSONStorable, uid on Atom - walk the base chain.
    if (-not ($hasField -or $hasProp) -and $t) {
        $b = $t.BaseType
        for ($i = 0; $i -lt 6 -and $b -and -not ($hasField -or $hasProp); $i++) {
            try { $bt = $b.Resolve() } catch { break }
            if (-not $bt) { break }
            $hasField = @($bt.Fields | Where-Object { $_.Name -eq $pair[1] }).Count -gt 0
            $hasProp  = @($bt.Properties | Where-Object { $_.Name -eq $pair[1] }).Count -gt 0
            $b = $bt.BaseType
        }
    }
    if ($hasField -or $hasProp) { Ok "$($pair[0]).$($pair[1])" } else { Fail "$($pair[0]).$($pair[1]) not found" }
}

# ---- 4. SimpleJSON mutation API the url patch uses ----
$jc = $asm.MainModule.GetType('SimpleJSON.JSONClass')
if ($null -eq $jc) { Fail 'SimpleJSON.JSONClass not found' }
else {
    foreach ($n in @('set_Item', 'Remove', 'get_Keys', 'get_Count')) {
        if (@($jc.Methods | Where-Object { $_.Name -eq $n -and $_.IsPublic }).Count -gt 0) { Ok "JSONClass.$n" }
        else { Fail "JSONClass.$n is missing or not public" }
    }
}

if ($fail -gt 0) { throw "verify-targets: $fail check(s) failed - the engine would silently not apply." }
Write-Host "  all patch targets verified against $dll"
