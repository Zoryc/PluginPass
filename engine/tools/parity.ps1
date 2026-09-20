<#
    Parity test: the BepInEx engine and the in-game script must decide identically.

    PluginPass now has two matching engines - RuleSet.cs out here, and the private
    statics inside Custom/Scripts/VexMech/PluginPass/PluginPass.cs. A rule has to mean
    exactly the same thing whichever one applies it, or the "engine on" and "engine
    off" behaviour of the same rules.json diverges, which is the worst possible bug in
    a tool whose entire job is deciding what loads.

    This compiles the script, loads both assemblies, and drives the same inputs
    through both implementations of every shared helper, asserting equality. It is a
    differential test: it does not care what the right answer is, only that there is
    one answer.

    Usage:  .\parity.ps1 [-VamPath D:\MeshedVR]
#>
[CmdletBinding()]
param([string]$VamPath = "D:\MeshedVR")

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # repo root
$managed = Join-Path $VamPath 'VaM_Data\Managed'
if (-not (Test-Path (Join-Path $managed 'Assembly-CSharp.dll'))) {
    Write-Warning "parity: no VaM at $VamPath - skipping."; exit 0
}

$engineDll = Join-Path $PSScriptRoot '..\bin\PluginPassEngine.dll'
$engineDll = [IO.Path]::GetFullPath($engineDll)
if (-not (Test-Path $engineDll)) { throw "parity: build the engine first ($engineDll)" }

# ---------------------------------------------------------------- compile the script
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { Write-Warning 'parity: csc.exe not found - skipping.'; exit 0 }

$scriptSources = @(Get-ChildItem -LiteralPath (Join-Path $root 'Custom') -Recurse -Filter *.cs |
                   ForEach-Object { $_.FullName })
$refNames = @(
    'Assembly-CSharp.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll',
    'UnityEngine.UI.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.TextRenderingModule.dll',
    'UnityEngine.IMGUIModule.dll', 'UnityEngine.InputModule.dll', 'UnityEngine.PhysicsModule.dll',
    'UnityEngine.AnimationModule.dll', 'UnityEngine.AudioModule.dll',
    'UnityEngine.JSONSerializeModule.dll', 'UnityEngine.SharedInternalsModule.dll'
)
$refArgs = @($refNames | ForEach-Object { '/r:' + (Join-Path $managed $_) })
$scriptDll = Join-Path ([IO.Path]::GetTempPath()) ('pluginpass-parity-' + [Guid]::NewGuid().ToString('N') + '.dll')

& $csc /nologo /target:library /langversion:5 "/out:$scriptDll" $refArgs $scriptSources | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'parity: the in-game script failed to compile.' }

try {
    # ------------------------------------------------------------------ load both
    $order = @('UnityEngine.SharedInternalsModule.dll','UnityEngine.CoreModule.dll',
               'UnityEngine.TextRenderingModule.dll','UnityEngine.IMGUIModule.dll',
               'UnityEngine.InputModule.dll','UnityEngine.PhysicsModule.dll',
               'UnityEngine.AnimationModule.dll','UnityEngine.AudioModule.dll',
               'UnityEngine.JSONSerializeModule.dll','UnityEngine.UIModule.dll',
               'UnityEngine.UI.dll','UnityEngine.dll')
    foreach ($f in $order) { try { [void][Reflection.Assembly]::LoadFrom((Join-Path $managed $f)) } catch {} }
    [void][Reflection.Assembly]::LoadFrom((Join-Path $managed 'Assembly-CSharp.dll'))

    $sAsm = [Reflection.Assembly]::LoadFrom($scriptDll)
    $eAsm = [Reflection.Assembly]::LoadFrom($engineDll)

    $S = $sAsm.GetType('VexMech.PluginPass')
    $SRule = $sAsm.GetType('VexMech.PluginPassRule')
    $E = $eAsm.GetType('PluginPassEngine.RuleSet')
    $ERule = $eAsm.GetType('PluginPassEngine.RuleSet+Rule')
    foreach ($pair in @(@($S,'VexMech.PluginPass'), @($SRule,'VexMech.PluginPassRule'),
                        @($E,'PluginPassEngine.RuleSet'), @($ERule,'PluginPassEngine.RuleSet+Rule'))) {
        if ($null -eq $pair[0]) { throw "parity: type $($pair[1]) not found" }
    }

    $BF = [Reflection.BindingFlags]'NonPublic,Static,Public'
    $BFI = [Reflection.BindingFlags]'NonPublic,Instance,Public'

    $script:pass = 0; $script:fail = 0
    function Same($what, $a, $b) {
        if ("$a" -ceq "$b") { $script:pass++ }
        else { $script:fail++; Write-Host ("    DIVERGES  {0}`n              script='{1}'  engine='{2}'" -f $what, $a, $b) }
    }

    function Both($name, [object[]]$argv) {
        $ms = $S.GetMethod($name, $BF)
        $me = $E.GetMethod($name, $BF)
        if ($null -eq $ms) { throw "parity: script has no static $name" }
        if ($null -eq $me) { throw "parity: engine has no static $name" }
        Same ("$name(" + (($argv | ForEach-Object { "'$_'" }) -join ',') + ")") `
             $ms.Invoke($null, $argv) $me.Invoke($null, $argv)
    }

    # ------------------------------------------------------------------ corpora
    $urls = @(
        'AcidBubbles.Embody.61:/Custom/Scripts/AcidBubbles/Embody/Embody.cslist'
        'AcidBubbles.EmbodyVR.latest:/Custom/Scripts/AcidBubbles/EmbodyVR/EmbodyVR.cslist'
        'everlaster.Naturalis.73:/Custom/Scripts/everlaster/Naturalis/Naturalis.cslist'
        'ToumeiHitsuji.DiviningRod.4:/Custom/Scripts/ToumeiHitsuji/DiviningRod/DiviningRod.cs'
        'VexMech.PlotFinder.0:/Custom/Scripts/VexMech/PlotFinder/PlotFinder.cs'
        'VexMech.PluginPass.18:/Custom/Scripts/VexMech/PluginPass/PluginPass.cs'
        '/Custom/Scripts/Loose/Thing.cs'
        'Custom/Scripts/Loose/Thing.cs'
        'SELF:/Custom/Scripts/Me/X.cs'
        'Author.Pkg.min3:/Custom/Scripts/A/B.cslist'
        'Author.Pkg.:/x.cs'
        'Author.Pkg:/x.cs'
        'A:/b'
        'C:/Windows/notepad.exe'
        ''
        'nocolonatall'
        'a.b.c.12:/deep/path/to/File.Name.cs'
        'Weird..Pkg.7:/x\y\Z.cs'
    )
    $patterns = @(
        '*', 'AcidBubbles.Embody', 'AcidBubbles.*', 'acidbubbles.embody',
        'AcidBubbles.Embody.61', '*.cs', '*.cslist', 'Embody.cslist',
        '*/Custom/Scripts/Old/*.cs', 'a?c', '', '**', 'Author.Pkg',
        'ToumeiHitsuji.DiviningRod', 'VexMech.PlotFinder', 'everlaster.Naturalis'
    )
    $texts = $urls + $patterns + @('abc','ac','aaa','xxxxy','aXbXc','SomeAuthor.SomePluginExtra')

    Write-Host '  Glob'
    foreach ($t in $texts) { foreach ($p in $patterns) { Both 'Glob' @([string]$t, [string]$p) } }

    Write-Host '  url helpers'
    foreach ($u in $urls) {
        Both 'PackageOf'  @([string]$u)
        Both 'PathOf'     @([string]$u)
        Both 'IsSelfUrl'  @([string]$u)
        Both 'PinPackageVersion' @([string]$u)
    }
    foreach ($u in $urls) {
        $sPath = $S.GetMethod('PathOf', $BF).Invoke($null, @([string]$u))
        Both 'FileNameOf' @([string]$sPath)
        $sPkg = $S.GetMethod('PackageOf', $BF).Invoke($null, @([string]$u))
        Both 'GroupId' @([string]$sPkg)
    }

    Write-Host '  rule matching'
    # Build the same rule on both sides and compare MatchesPackage / MatchesScript / BuildTarget.
    $actions = @(0, 1, 2)
    foreach ($pkgPat in @($null) + $patterns) {
        foreach ($scrPat in @($null, '*.cs', 'Embody.cslist', '*/Old/*.cs')) {
            $sr = [Activator]::CreateInstance($SRule)
            $er = [Activator]::CreateInstance($ERule)
            foreach ($obj in @($sr, $er)) {
                $ty = $obj.GetType()
                $ty.GetField('packagePattern', $BFI).SetValue($obj, $pkgPat)
                $ty.GetField('scriptPattern', $BFI).SetValue($obj, $scrPat)
                $isPath = ($null -ne $scrPat) -and ($scrPat.Contains('/') -or $scrPat.Contains('\'))
                $ty.GetField('scriptIsPath', $BFI).SetValue($obj, $isPath)
                $ty.GetField('to', $BFI).SetValue($obj, 'Me.Fork.latest:{path}')
            }
            foreach ($u in $urls) {
                $pkg = [string]$S.GetMethod('PackageOf', $BF).Invoke($null, @([string]$u))
                $path = [string]$S.GetMethod('PathOf', $BF).Invoke($null, @([string]$u))
                $file = [string]$S.GetMethod('FileNameOf', $BF).Invoke($null, @($path))
                $grp = [string]$S.GetMethod('GroupId', $BF).Invoke($null, @($pkg))

                $label = "pkgPat='$pkgPat' scrPat='$scrPat' url='$u'"
                Same "MatchesPackage $label" `
                     $SRule.GetMethod('MatchesPackage', $BFI).Invoke($sr, @($pkg, $grp)) `
                     $ERule.GetMethod('MatchesPackage', $BFI).Invoke($er, @($pkg, $grp))
                Same "MatchesScript $label" `
                     $SRule.GetMethod('MatchesScript', $BFI).Invoke($sr, @($path, $file)) `
                     $ERule.GetMethod('MatchesScript', $BFI).Invoke($er, @($path, $file))
                Same "BuildTarget $label" `
                     $SRule.GetMethod('BuildTarget', $BFI).Invoke($sr, @($pkg, $path, $file)) `
                     $ERule.GetMethod('BuildTarget', $BFI).Invoke($er, @($pkg, $path, $file))
            }
        }
    }

    Write-Host ''
    Write-Host ("  parity: {0} checks agreed, {1} diverged" -f $script:pass, $script:fail)
    if ($script:fail -gt 0) {
        throw "parity: the engine and the in-game script disagree on $($script:fail) input(s)."
    }
}
finally {
    Remove-Item -LiteralPath $scriptDll -Force -ErrorAction SilentlyContinue
}
