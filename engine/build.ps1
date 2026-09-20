<#
    PluginPass Engine build script.

    Compiles against Virt-a-Mate's OWN managed assemblies so the output binds to
    mscorlib 2.0.0.0 (Mono legacy / .NET 3.5 profile), which is what VaM's runtime
    actually loads. Building against .NET 4.x references produces a DLL that
    BepInEx will load and then fail to bind at first call.

    This is the out-of-sandbox half of PluginPass. The in-game script
    (Custom/Scripts/VexMech/PluginPass/PluginPass.cs) is built by ../build/build.ps1
    and is unrelated to this - different compiler settings, different rules.

    Usage:
        .\build.ps1                       # build to .\bin
        .\build.ps1 -Deploy               # build, then copy to BepInEx\plugins
        .\build.ps1 -VamPath "D:\MeshedVR"
#>
[CmdletBinding()]
param(
    [string]$VamPath = "D:\MeshedVR",
    [switch]$Deploy,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not reliably populated in param() defaults across hosts.
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Definition }
if (-not $OutDir) { $OutDir = Join-Path $root "bin" }

$managed = Join-Path $VamPath "VaM_Data\Managed"
$core    = Join-Path $VamPath "BepInEx\core"
$csc     = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

function Need($path, $what) {
    if (-not (Test-Path $path)) { throw "$what not found: $path" }
}

Need $csc     "C# compiler (.NET Framework csc.exe)"
Need $managed "VaM managed assembly folder"
Need $core    "BepInEx core folder"

$refs = @(
    "mscorlib.dll"
    "System.dll"
    "System.Core.dll"
    "UnityEngine.dll"
    "UnityEngine.CoreModule.dll"
    "Assembly-CSharp.dll"        # MVRPluginManager, Atom, JSONStorable, SuperController,
                                 # SimpleJSON, MVR.FileManagementSecure
) | ForEach-Object {
    $p = Join-Path $managed $_
    Need $p "reference assembly $_"
    "/r:$p"
}

$refs += @("BepInEx.dll", "0Harmony.dll") | ForEach-Object {
    $p = Join-Path $core $_
    Need $p "reference assembly $_"
    "/r:$p"
}

$sources = Get-ChildItem (Join-Path $root "src") -Filter *.cs -Recurse |
           ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { throw "no sources found under src\" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$outDll = Join-Path $OutDir "PluginPassEngine.dll"

Write-Host "Compiling $($sources.Count) source file(s) -> $outDll"

$cscArgs = @(
    "/nologo"
    "/noconfig"        # do not drag in the .NET 4 default references
    "/nostdlib+"       # we supply mscorlib ourselves (VaM's 2.0.0.0)
    "/target:library"
    "/platform:AnyCPU"
    "/langversion:5"   # Mono legacy runtime: keep to C# 5 constructs
    "/optimize+"
    "/warn:4"
    "/out:$outDll"
) + $refs + $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "compilation failed with exit code $LASTEXITCODE" }

Write-Host "Build OK: $outDll"
$asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($outDll)
Write-Host "Bound references:"
$asm.GetReferencedAssemblies() | ForEach-Object { Write-Host ("   {0} {1}" -f $_.Name, $_.Version) }

$mscorlib = $asm.GetReferencedAssemblies() | Where-Object { $_.Name -eq 'mscorlib' }
if ($mscorlib -and $mscorlib.Version.Major -ne 2) {
    Write-Warning "mscorlib is $($mscorlib.Version) - expected 2.0.0.0. This DLL will NOT bind inside VaM."
}

if ($Deploy) {
    $dest = Join-Path $VamPath "BepInEx\plugins"
    Need $dest "BepInEx plugins folder"
    Copy-Item $outDll (Join-Path $dest "PluginPassEngine.dll") -Force
    Write-Host "Deployed to $dest\PluginPassEngine.dll"
}
