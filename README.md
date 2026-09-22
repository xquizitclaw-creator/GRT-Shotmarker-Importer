# ShotMarker import for Gordon's Reloading Tool

A plugin for [Gordon's Reloading Tool](https://gordonsreloadingtool.com/) (GRT) that imports
[ShotMarker](https://www.shotmarker.com/) electronic-target exports. Each shooting string in
the export becomes a shot-group tab in GRT: the scoring face drawn to scale, every hit placed
on it, a measurement of the shot velocities, and a note carrying ShotMarker's own group
statistics — group size, mean radius, extreme spread.

## Install

1. Install the **.NET 8 Desktop Runtime** if you don't already have it —
   [download](https://dotnet.microsoft.com/download/dotnet/8.0). The plugin is framework-dependent:
   it does not bundle its own copy of .NET, so GRT will list it but be unable to start it without
   the runtime present.
2. Build `dist\ShotMarker` (see **Build** below) and copy that folder into GRT's `plugins` folder.
3. Restart GRT. A **ShotMarker** button appears on the toolbar.

## Use

1. Open a load in GRT.
2. Click **ShotMarker** on the toolbar and choose the `.tar` or `.csv` file ShotMarker exported.
3. Each string in the export is listed with its distance, score and velocities. Tick the strings
   you want and set each one's charge weight (pre-filled from the load when GRT can determine it).
4. Import. GRT opens a new load — a sibling of the one you had open, named after it plus a
   timestamp — with one shot-group tab per string you ticked. Your original load file is never
   modified.

## Build

This repo is a normal .NET solution plus one packaging script:

```bash
dotnet test                              # ShotMarkerCore's tests; runs on any platform
powershell -File tools/build-plugin.ps1  # assembles dist/ShotMarker; needs Windows + the .NET 8 SDK
```

`tools/build-plugin.ps1` takes an optional `-GrtDir` to install straight into a GRT install's
`plugins` folder:

```powershell
powershell -File tools/build-plugin.ps1 -GrtDir "C:\Users\you\GordonsReloadingTool"
```

`tools/extract-targetfaces.js` regenerates `ShotMarkerCore/Faces/targetfaces.json` from the
archived ShotMarker web bundle in `fixtures/shotmarker/bundle`. The generated file is committed,
so a normal build needs no Node — this script only matters if ShotMarker adds or changes a
target face.

## Design

The design document behind this plugin — export formats, coordinate mapping, the plugin
architecture and how it talks to GRT — is at
[`docs/superpowers/specs/2026-09-21-shotmarker-import-design.md`](docs/superpowers/specs/2026-09-21-shotmarker-import-design.md).
