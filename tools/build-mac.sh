#!/usr/bin/env bash
# The macOS (bash) counterpart of tools/build-plugin.ps1: publishes the plugin and assembles the
# drop-in GRT plugin folder for one runtime, at ./dist/<rid>/ShotMarker/.
#
#   tools/build-mac.sh                   osx-arm64, self-contained
#   tools/build-mac.sh osx-x64           Intel Mac
#   tools/build-mac.sh win-x64           Windows, cross-published from the Mac
#   tools/build-mac.sh all               osx-arm64, osx-x64 and win-x64
#   FRAMEWORK_DEPENDENT=1 tools/build-mac.sh win-x64
#                                        leaves the runtime out (needs the .NET 8 Runtime)
#
# Self-contained by default here, unlike build-plugin.ps1: a Mac has no ".NET Desktop Runtime"
# a shooter would already have installed, so the bundled runtime is the only form that starts.
# Never trimmed: GRT's plugin host and Avalonia's XAML loader both use reflection a trimmer
# cannot follow. Needs the .NET 8 SDK. Nothing here launches the plugin.
set -euo pipefail

# pwd -P: on this Mac /Users is also reachable under /Volumes, and a publish whose project paths
# mix the two spellings loses its project references.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
csproj="$root/ShotMarkerPlugin/ShotMarkerPlugin.csproj"
config="${CONFIGURATION:-Release}"
self_contained=true
[[ "${FRAMEWORK_DEPENDENT:-}" == "1" ]] && self_contained=false

# The version lives in Directory.Build.props and nowhere else; the shipped manifest is stamped
# with it, so an unstamped copy can never announce an old version.
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -1 | tr -d '[:space:]')"
[[ -n "$version" ]] || { echo "no <Version> in Directory.Build.props" >&2; exit 1; }
echo "==> version $version"

build() {
  local rid="$1"
  local exe="GRT_ShotMarker"
  [[ "$rid" == win-* ]] && exe="GRT_ShotMarker.exe"

  local publish="$root/artifacts/publish/$rid"
  # Wiped rather than published over: publish never removes, so leftovers from an earlier run
  # would be raked into dist as if they belonged to this build.
  rm -rf "$publish"
  echo "==> publish $rid (self-contained: $self_contained)"
  dotnet publish "$csproj" -c "$config" -r "$rid" --self-contained "$self_contained" \
    -p:PublishSingleFile=false -p:DebugType=none -p:SatelliteResourceLanguages=en \
    -o "$publish" --nologo

  local dist="$root/dist/$rid/ShotMarker"
  rm -rf "$dist"
  mkdir -p "$dist"
  cp -R "$publish"/. "$dist"/
  # The csproj copies plugin/** into the output so the manifest and icons travel with the build,
  # but GRT reads com.grt.plugin.xml and media/ from the plugin folder's root.
  rm -rf "$dist/plugin"
  cp "$root/ShotMarkerPlugin/plugin/com.grt.plugin.xml" "$dist/"
  cp -R "$root/ShotMarkerPlugin/plugin/media" "$dist/"

  # Stamp the manifest's version attribute (line-anchored, so the <?xml version?> declaration
  # is left alone) and check it took.
  sed -i.bak -E "s/^([[:space:]]*version[[:space:]]*=[[:space:]]*\")[^\"]*(\")/\1$version\2/" "$dist/com.grt.plugin.xml"
  rm -f "$dist/com.grt.plugin.xml.bak"
  grep -Eq "^[[:space:]]*version[[:space:]]*=[[:space:]]*\"$version\"" "$dist/com.grt.plugin.xml" \
    || { echo "manifest version not stamped to $version" >&2; exit 1; }

  [[ -f "$dist/$exe" ]] || { echo "missing $exe in $dist" >&2; exit 1; }
  if [[ "$rid" != win-* ]]; then
    chmod +x "$dist/$exe"
    [[ -x "$dist/$exe" ]] || { echo "$exe is not executable" >&2; exit 1; }
  fi

  echo "==> dist/$rid/ShotMarker $(du -sh "$dist" | cut -f1) ($(find "$dist" -type f | wc -l | tr -d ' ') files)"
}

target="${1:-osx-arm64}"
if [[ "$target" == "all" ]]; then
  for rid in osx-arm64 osx-x64 win-x64; do build "$rid"; done
else
  build "$target"
fi
