<#
  Adapted from GRT Reloading Toolkit's build-plugin.ps1 (MIT, Copyright (c) 2026 FREESHOTER)
  — https://github.com/FREESHOTER/GRT-Reloading-Toolkit

  Publishes the plugin and assembles the drop-in GRT plugin folder:

    ./dist/ShotMarker/   framework-dependent - needs the .NET 8 Desktop Runtime on the GRT machine

  This plugin has no self-contained variant, no zip and no MANUAL.md/docs copy — see the
  toolkit's version if a future build needs those; this script only builds what
  ShotMarkerPlugin actually ships.

  Usage:
    ./tools/build-plugin.ps1
    ./tools/build-plugin.ps1 -GrtDir "C:\...\GordonsReloadingTool"
#>
param(
    [string]$Configuration = "Release",
    [string]$GrtDir = ""
)

$ErrorActionPreference = "Stop"
# Unlike the toolkit's copy, this script lives in tools\ rather than beside the csproj, so the
# repo root is one level up from $PSScriptRoot, not $PSScriptRoot itself.
$root = Split-Path -Parent $PSScriptRoot

# A `dotnet.exe` with no SDK registered (a bare host - e.g. a 32-bit stub left behind by some
# other product's install, sitting on PATH ahead of the real one) loads fine as a command but
# can't run `publish`. Take the first PATH match that actually reports an SDK, not just the
# first one PATH happens to list first.
function HasSdk($path) {
    try { return [bool](& $path --list-sdks 2>$null) } catch { return $false }
}
# dotnet off PATH: a hardcoded "C:\Program Files\dotnet\dotnet.exe" breaks every install that
# isn't the default x64 machine-wide one - winget, per-user, ARM64, side-by-side.
$dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue |
          Select-Object -ExpandProperty Source -Unique |
          Where-Object { HasSdk $_ } | Select-Object -First 1
if (-not $dotnet) {
    $dotnet = @("$env:ProgramFiles\dotnet\dotnet.exe",
                "${env:ProgramFiles(x86)}\dotnet\dotnet.exe",
                "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") |
              Where-Object { $_ -and (Test-Path $_) -and (HasSdk $_) } | Select-Object -First 1
}
if (-not $dotnet) { throw "no dotnet install with an SDK found on PATH or in the usual install locations. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0" }
$csproj = Join-Path $root "ShotMarkerPlugin\ShotMarkerPlugin.csproj"

# The version lives in Directory.Build.props and nowhere else: the assembly gets it from the
# compiler and the shipped manifest is stamped with it below. GRT reads com.grt.plugin.xml, so
# an unstamped copy is how a build ends up announcing an old version.
#
# Found by walking up from $root rather than a fixed relative path, for the same reason as the
# toolkit's copy: a script this closely adapted should survive being moved without silently
# picking up the wrong props file (or none).
$propsDir = $root
while ($propsDir -and -not (Test-Path (Join-Path $propsDir "Directory.Build.props"))) {
    $propsDir = Split-Path $propsDir -Parent
}
if (-not $propsDir) { throw "no Directory.Build.props found above $root" }
$propsPath = Join-Path $propsDir "Directory.Build.props"
$version = ([xml](Get-Content $propsPath -Raw -Encoding UTF8)).SelectSingleNode("/Project/PropertyGroup/Version").InnerText.Trim()
if (-not $version) { throw "no <Version> in $propsPath" }
Write-Host "==> version $version"

# Rewrites the manifest's version attribute in place. Line-anchored so it can't hit the
# `<?xml version="1.0"?>` declaration too (the brief's naive `version\s*=\s*"[^"]*"` regex did,
# producing a manifest that no longer parses) - and the result is re-parsed and checked, because
# a silently unstamped or corrupted manifest is exactly the drift this exists to stop.
function Stamp($manifest) {
    $manifest = (Resolve-Path $manifest).Path
    $txt = [regex]::Replace((Get-Content $manifest -Raw -Encoding UTF8), '(?m)^(\s*version\s*=\s*")[^"]*(")', "`${1}$version`${2}")
    [System.IO.File]::WriteAllText($manifest, $txt, (New-Object System.Text.UTF8Encoding $false))
    $got = ([xml](Get-Content $manifest -Raw -Encoding UTF8)).SelectSingleNode("/GordonsReloadingTool/plugin").GetAttribute("version")
    if ($got -ne $version) { throw "manifest version is '$got', expected '$version' - check the version attribute in ShotMarkerPlugin\plugin\com.grt.plugin.xml" }
}

Write-Host "==> publish: framework-dependent"
$publishDir = Join-Path $root "artifacts\publish"
& $dotnet publish $csproj -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=false -o $publishDir | Out-Host
# `& dotnet.exe` is a native command: $ErrorActionPreference = "Stop" does not see its exit code,
# only PowerShell's own terminating errors - a failed publish would otherwise fall through to the
# assembly step below, which happily re-packages whatever is already sitting in $publishDir from a
# previous run and reports success for a build that never happened.
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$dist = Join-Path $root "dist\ShotMarker"
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $publishDir "*") $dist -Recurse
# The csproj's `<None Include="plugin\**\*" CopyToOutputDirectory="PreserveNewest" />` leaves a
# plugin\ subfolder inside the publish output (that's how the manifest and icons get built at
# all) - but GRT reads com.grt.plugin.xml and media\ from the plugin folder's root, not from a
# plugin\ subfolder inside it, so that copy is promoted up and the now-empty wrapper removed.
Remove-Item -Recurse -Force (Join-Path $dist "plugin") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "ShotMarkerPlugin\plugin\com.grt.plugin.xml") $dist -Force
Stamp (Join-Path $dist "com.grt.plugin.xml")
Copy-Item (Join-Path $root "ShotMarkerPlugin\plugin\media") $dist -Recurse

Write-Host "==> dist\ShotMarker $('{0:N0}' -f ((Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum/1KB)) KB"

if ($GrtDir -ne "") {
    # Guard against a mistyped -GrtDir scattering a plugin folder somewhere nobody will look for
    # it: a real GRT install has a plugins\ folder, so require one rather than creating it.
    $pluginsDir = Join-Path $GrtDir "plugins"
    if (-not (Test-Path $pluginsDir)) { throw "no plugins folder found under $GrtDir - check -GrtDir; this script never creates a plugins folder, only replaces plugins\ShotMarker inside one that already exists" }
    $target = Join-Path $pluginsDir "ShotMarker"
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Copy-Item $dist $target -Recurse
    Write-Host "==> installed into $target"
}
