# ShotMarker import for Gordon's Reloading Tool

A plugin for [Gordon's Reloading Tool](https://gordonsreloadingtool.com/) (GRT) that imports
[ShotMarker](https://www.shotmarker.com/) electronic-target exports. Each shooting string in
the export becomes a shot-group tab in GRT: the scoring face drawn to scale, every hit placed
on it, a measurement of the shot velocities, and a note carrying ShotMarker's own group
statistics — group size, mean radius, centre-to-centre, and the velocity average, standard
deviation and extreme spread.

## Install

1. Download a zip from
   [Releases](https://github.com/xquizitclaw-creator/GRT-Shotmarker-Importer/releases/latest).
2. Unzip it, and put the `ShotMarker` folder inside GRT's `plugins` folder.
3. Restart GRT. A **ShotMarker** button appears on the toolbar.

Windows, 64-bit. Each release carries two zips of the same plugin, installed the same way:

- `GRT-ShotMarker-<version>-win-x64.zip` is self-contained — it brings its own copy of .NET and
  needs nothing installed. Nearly all of its size is that copy.
- `GRT-ShotMarker-<version>-win-x64-needs-dotnet8.zip` leaves the runtime out, so it is a
  fifteenth the size and needs the
  [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) on the GRT machine —
  without it GRT lists the plugin but cannot start it.

A build you make yourself is the second kind unless you pass `-SelfContained`.

## Use

1. Open a load in GRT.
2. Click **ShotMarker** on the toolbar and choose the `.tar` or `.csv` file ShotMarker exported.
3. Each string in the export is listed with its distance, score and velocities. Tick the strings
   you want and set each one's charge weight (pre-filled from the load when GRT can determine it).
4. Import. GRT opens a new load — a sibling of the one you had open, named after it plus a
   timestamp — with one shot-group tab per string you ticked. Your original load file is never
   modified.

Only the **three most recent** imports made from a given load are kept: a fourth import deletes
the oldest of the three. This is how GRT plugins keep generated siblings from piling up, and it
only ever touches files this plugin itself wrote from that same load — never your originals, and
never another plugin's output. If you want to keep an import permanently, rename it so it no
longer carries the `_shotmarker_<timestamp>` suffix.

## Build

This repo is a normal .NET solution plus one packaging script:

```bash
dotnet test ShotMarkerCore.Tests         # the core's tests; runs on any platform
powershell -File tools/build-plugin.ps1  # assembles dist/ShotMarker; needs Windows + the .NET 8 SDK
```

Name the test project explicitly. A bare `dotnet test` builds the whole solution, which includes
the `net8.0-windows` WinForms shell — that needs the Windows Desktop targeting pack, and on a
.NET SDK that ships without it (Homebrew's `dotnet@8`, some distro packages) the build fails with
`MSB4019` before any test runs. The core library and all its tests are plain `net8.0` and are
green on macOS, Linux and Windows alike.

`tools/build-plugin.ps1` takes an optional `-GrtDir` to install straight into a GRT install's
`plugins` folder, and `-SelfContained` to build the variant the releases ship:

```powershell
powershell -File tools/build-plugin.ps1 -GrtDir "C:\Users\you\GordonsReloadingTool"
powershell -File tools/build-plugin.ps1 -SelfContained
```

Releases are built by `.github/workflows/release.yml`, not by hand: pushing a tag whose version
matches `Directory.Build.props` runs both builds on a Windows runner, zips each, and publishes the
pair with their SHA-256s in the notes. The release page's text is
`.github/release-notes.md`, including its “what's new” section — update it in the commit that
bumps the version.

`tools/extract-targetfaces.js` regenerates `ShotMarkerCore/Faces/targetfaces.json` — the 208
target faces — from ShotMarker's own web bundle. That bundle is ShotMarker's copyrighted code
and is **not** redistributed here; point the script at your own copy if you need to rerun it.
The generated `targetfaces.json` is committed, so a normal build needs neither Node nor the
bundle. This only matters if ShotMarker adds or changes a target face.

## Design

The design document behind this plugin — export formats, coordinate mapping, the plugin
architecture and how it talks to GRT — is at
[`docs/superpowers/specs/2026-09-21-shotmarker-import-design.md`](docs/superpowers/specs/2026-09-21-shotmarker-import-design.md).

## Licence

MIT — see [`LICENSE`](LICENSE).

The licence covers this plugin's own code only. ShotMarker's web bundle is not redistributed
here (see above), and Gordon's Reloading Tool is a separate product under its own terms.
