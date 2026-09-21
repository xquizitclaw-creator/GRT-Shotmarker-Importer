# ShotMarker Import Plugin for Gordon's Reloading Tool — Design

Date: 2026-09-21
Status: approved design, pending one open question (see [Open questions](#open-questions))

## Purpose

Import ShotMarker electronic target data into GRT so a load's real downrange
performance lives in the same document as its simulation. Each imported string
becomes a GRT **Shot group analysis** tab carrying a faithful rendering of the
ShotMarker target face, plus a velocity **Measurement** and a stats note.

Today this data is stranded: ShotMarker exports it, GRT can hold it, and nothing
connects the two.

## Scope

In scope:

- Reading ShotMarker `.tar` archive exports and `.csv` shot-log exports.
- Rendering a target picture per string: true-scale scoring face, numbered shot
  discs, group bounding box, stats overlay.
- Writing a native GRT `<ShotGroup>` tab, a `<Measurement>` of per-shot
  velocities, and a note with ShotMarker's own group statistics.
- Distributing as a standalone GRT plugin, `com.grt.plugin.shotmarker`.

Out of scope:

- Any network connection to the ShotMarker device at runtime. The plugin reads
  files only, so it works at home with no device present. The device was read
  once during design to extract target-face geometry (see [Target faces](#target-faces)).
- Ladder/OCW analysis. GRT and the existing GRT-Reloading-Toolkit already do
  this, and they read shot-group tabs — which is what this plugin produces.
- Editing the open load in place. The GRT plugin interface does not allow it.

## Source data

Both export formats come from the same device; the `.tar` is richer and is the
preferred source.

### `.tar` archive export

A plain tar containing:

- `archive.txt` — JSON index, one entry per string, keyed by string id:
  `name`, `ts`, `count`, `group_text`, `face_id`, `distance`, `time`.
- `string-<id>.z` — zlib-compressed JSON, one per string.

Each string JSON holds:

| Field | Meaning |
|---|---|
| `width`, `height` | target frame size, mm |
| `dist`, `dist_unit` | shooting distance and unit (`y`/`m`) |
| `face_id` | target face identifier, e.g. `NRA_LRFC` |
| `bullet` | bullet diameter, mm |
| `name`, `ts`, `label` | string name, timestamp, target label |
| `score_string` | per-shot scores as `id:score,` pairs |
| `shots` | encoded shot strings (redundant with `groups`) |
| `shots_invalid` | shots the device rejected |
| `groups` | one entry per ShotMarker group — the authoritative record |

Each group carries per-shot records (`ts`, `x`, `y` in mm from centre, `v` in
**m/s**, `score`, `temp`, `display`, `display_text`) and ShotMarker's own
computed statistics: `mr`, `xsd`, `ysd`, `rsd`, `cep`, `ctc`, `size`, `v_avg`,
`v_sd`, `v_es`, plus the group box `x0/y0/x1/y1`.

### `.csv` shot log export

Header lines carry the date, string name, target id, frame size, face name and
distance, and score totals. Then one row per shot: `time`, `tags`, `id`,
`score`, `temp C`, `x mm`, `y mm`, `v fps`, `yaw deg`, `pitch deg`, `quality`,
`xy_err`. Velocity here is **fps**, not m/s. The `tags` column carries
`sighter` and `incomplete` markers.

Coordinates in both formats are millimetres from target centre, **y up**.

## Architecture

Four projects. The logic lives in a cross-platform library so it can be built
and tested on any machine; only the thin UI shell is Windows-bound.

| Project | Target | Role |
|---|---|---|
| `ShotMarkerCore` | `net8.0` | Export readers, face model, renderer, GRT writer |
| `ShotMarkerPlugin` | `net8.0-windows` | WinForms shell (`GRT_ShotMarker.exe`), picker grid, log pane |
| `ShotMarkerCore.Tests` | `net8.0` | xUnit |
| `GrtPluginKit` | `net8.0` | Existing shared kit, consumed as a git submodule of `GRT-Reloading-Toolkit` |

Dependencies: **SkiaSharp** for rendering, and nothing else. .NET 8 supplies
`System.Formats.Tar` and `System.IO.Compression.ZLibStream`, so the archive
format needs no third-party code.

### Why the logic is not in the WinForms project

`System.Drawing` is Windows-only in .NET 8. A renderer built on it cannot be
executed on a development Mac or in Linux CI, which would make the plugin's
central deliverable — a faithful picture — unverifiable except by manual
inspection on a Windows box. SkiaSharp in a `net8.0` library renders identically
everywhere, so golden-image tests run in CI.

### Change to GrtPluginKit

`GrtLoadDoc` reads `<ShotGroup>` tabs but writes only notes, galleries and
measurements. Writing shot groups belongs beside `AddGalleryPicture` in the kit,
not in this plugin, so this project adds:

```csharp
public void AddShotGroup(string title, byte[] png, ShotGroupGeometry geom,
                         IEnumerable<GrtShotGroupSet> groups)

/// The calibration GRT needs to recover real-world scale from the picture:
/// reference points as image fractions, their separation, and the shooting
/// distance. Units as stored by GRT (see "Coordinate mapping").
public sealed record ShotGroupGeometry(
    double RefP1X, double RefP1Y, double RefP2X, double RefP2Y,
    double RefDistance, double ShootDistance);
```

It reuses the kit's existing `GrtShotGroupSet`/`GrtShotPoint` records, so the
same types describe a shot group whether the kit is reading one or writing one.
This ships as its own small pull request against `GRT-Reloading-Toolkit`.

## Components

### `SmExportReader`

`.tar` and `.csv` in, a list of `SmString` out. One shape regardless of source,
so everything downstream is format-blind:

```csharp
record SmString(string Id, string Name, DateTimeOffset Timestamp,
                string FaceId, double DistanceValue, string DistanceUnit,
                double FrameWidthMm, double FrameHeightMm,
                double? BulletDiameterMm, string? ScoreText,
                IReadOnlyList<SmShot> Shots, SmGroupStats? Stats);

record SmShot(int Number, double XMm, double YMm, double? VelocityMps,
              string? Score, double? TempC, bool IsSighter, bool IsInvalid);
```

Velocity is normalised to m/s at the boundary — the CSV's fps is converted once,
here, so no code downstream has to know which format it came from.

### `TargetFaceLibrary`

Loads `targetfaces.json`, a generated resource holding every ShotMarker face.
Lookup by `face_id`, with a generic fallback for unknown ids.

### `TargetRenderer`

An `SmString` plus a `TargetFace` in, a PNG plus a `ShotGroupGeometry` out. Draws
scoring rings at true scale, ring value text, numbered shot discs, the group
bounding box and the stats overlay. Returns the geometry alongside the image
because the caller needs the exact mm-to-pixel mapping used, and recomputing it
independently would be a second source of truth.

GRT draws its own furniture over the picture — group box, extreme spread, SD
rings, flyer marks — as its `color_shotgroup_*` palette shows. The ShotMarker
group box and stats overlay therefore sit underneath a second set of the same
markings. The renderer keeps them, per the approved design, but behind a
`DrawFurniture` flag so a clean scoring face is one setting away if the doubled
overlay reads badly in practice.

### `GrtShotGroupWriter`

Takes rendered strings and an open load, writes the sibling `.grtload`: one
`<ShotGroup>` per string, one `<Measurement>` of velocities, one note of stats.

## Coordinate mapping

This is the part most likely to be silently wrong, so it is stated explicitly.

ShotMarker gives millimetres from target centre with **y increasing upward**.
GRT stores points as **image fractions in 0..1 with y increasing downward**, and
recovers real-world scale from two reference points a known distance apart.

The renderer draws at a fixed scale `s` px/mm on a canvas covering the face board
plus a margin, giving a canvas of `Wmm × Hmm` whose top-left corner sits at
`(leftMm, topMm)` in target coordinates. Then, for a shot at `(x, y)` mm:

```
fracX = (x - leftMm) / Wmm
fracY = (topMm - y) / Hmm
```

The two reference points are placed on the horizontal centre line a known
distance `R` apart, and `refDistance` is written as `R`.

**Storage units.** Evidence from a real GRT-written tab (`shootDistance="914.4"`
on a 1000 yd string, with `range=yard` configured; `refDistance="150.0124"`,
exactly 5.906 in × 25.4, with `refdistance=in` configured) indicates GRT stores
these attributes in **SI — millimetres and metres — regardless of display unit**.
The writer therefore emits mm and metres. This is confirmed by sample B before
implementation begins (see [Open questions](#open-questions)).

Note that this contradicts `GrtShotGroups.ShootToM()` in GRT-Reloading-Toolkit,
which converts yards to metres on read when the config says yards. If storage is
SI, that reader double-converts and every group distance it produces from an
imperial install is 0.914× too small. That is a bug in a different project and is
recorded here, not fixed here.

## Target faces

The ShotMarker web application defines **206 built-in target faces**. Its
`custom_targetfaces.js` documents the schema: a `board` (`w`, `h`, `line`), an
array of `rings` (`diam` in mm, `color` from `w`/`b`/`g`/`wl`/`bl`/`gl`, `line`
thickness, `score`), optional `poly` shapes, and `score` giving the point value
of the lettered ring. The constant `INCH = .03937`, so `60/INCH` is 1524 mm.

Face data is extracted **once, at build time, not at runtime**: a script
evaluates the face object out of an archived copy of the application bundle and
writes `targetfaces.json`, which is committed as a generated resource together
with the bundle snapshot it came from, so the extraction is reproducible.
Evaluating the JavaScript rather than pattern-matching it matters — a regex pass
during design parsed only 144 of 205 faces, because some carry expressions and
polygon arrays.

Spot check: `NRA_LRFC` extracts as X=5″, 10=10″, 9=20″, 8=30″, 7=44″, 6=60″ on a
72″ board, matching the published NRA Long Range F-Class specification and the
user's confirmation that the X ring measures 5 inches.

An unrecognised `face_id` renders as a plain plot — shots, centre cross and scale
bar, no scoring rings — and logs a warning. It never aborts the import.

## User flow

1. The user clicks the ShotMarker toolbar button in GRT. The plugin launches
   `onDemand` and attaches over IPC via `GrtClient`, learning the active load path.
2. The user picks a `.tar` or `.csv` export.
3. The picker grid lists every string found: name, date, distance, face, shot
   count, score, mean velocity, SD, ES, and an **editable charge weight**
   pre-filled from the open load's propellant charge.
4. The user ticks strings and imports. Each selected string becomes its own
   shot-group tab. One charge per string is the normal case; the column is
   editable so a ladder shot across several strings still resolves correctly.
5. The plugin writes a timestamped sibling `.grtload`, then sends `Load_File`
   so GRT opens it immediately; the reported path is the fallback if that
   command fails. The open load is never modified — matching the existing
   toolkit's behaviour and the GRT plugin interface's constraints.

### Sighters, flyers and invalid shots

Every shot is imported, so the picture matches what ShotMarker displays. Shots
tagged `sighter`, listed in `shots_invalid`, or carrying `display: false` are
written with `flyer="true"`, so GRT's group statistics and the toolkit's ladder
analyzer exclude them automatically while the shooter can still see them and
un-flag any that were mis-tagged.

## Error handling

| Condition | Behaviour |
|---|---|
| Undecodable or truncated string entry | Skip that string, name it in the log pane, continue |
| Unknown `face_id` | Generic plot, warning in log |
| No active load | Offer `GrtLoadDoc.CreateMinimal` |
| Zero shots in a string | Skip with a warning; never write an empty tab |
| Missing velocities | Import positions; omit the Measurement for that string |
| Output path exists | `SaveSibling` timestamps it; never overwrite |

The import is best-effort per string: one bad string never fails the batch.

## Testing

The user's real exports (`SM_export_Sep_21.tar`, `SM_shotslog_Sep_21.csv`) and
the GRT-written sample loads are committed as fixtures.

**The load-bearing test is a round-trip invariant.** Write a `.grtload`, read it
back with the toolkit's existing `GrtShotGroups.FromDoc()`, and assert every shot
returns to its original millimetre position within tolerance. This uses an
independently-written, already-working reader as the oracle, so an error in the
writer's fraction maths cannot pass unnoticed. It runs under both metric and
imperial GRT configurations.

Also covered:

- Tar and zlib decoding against the real archive.
- CSV parsing: multi-string files, sighter tags, score letters (`X`), blank cells.
- Unit normalisation: fps→m/s, yards→metres, inches→mm.
- Cross-format agreement: the strings common to both fixtures produce matching
  shot positions and velocities.
- Face extraction: all 206 faces parse; `NRA_LRFC` matches published dimensions.
- Golden-image test of the renderer.
- Writer output parses as well-formed XML and GRT's own schema expectations.

## Open questions

**A calibration sample is the first task of implementation.** Every `.grtload`
on the GRT machine has been scanned: exactly one contains a `<ShotGroup>`, and
it has zero `<group>` and zero `<point>` elements. The shot-group documentation
explains why — points exist only after the **"(+) Group"** and **"(+) Shot"**
buttons are used; clicking the picture alone records nothing. No file to learn
the format from therefore exists yet, and none can be derived: it has to be
authored in GRT by hand, once.

The sample needs two reference points placed across a ring of known width, a
few marked shots, one flagged as a flyer and one as point of aim. It resolves:

1. **Storage units.** For a 10-inch reference, `refDistance="254"` confirms
   millimetres and `refDistance="10"` means display units — in which case the
   writer, and the unit handling throughout this design, change accordingly.
   The surrounding evidence points at millimetres: the same file already stores
   `shootDistance="914.4"` for 1000 yards under a `range=yard` configuration.
2. **Point serialisation.** The exact `<group>`/`<point>` attribute spelling,
   currently known only from the toolkit's reader rather than from a file GRT
   wrote.

Until it exists, the writer cannot be finished — so the plan front-loads the
capture, and the work that does not depend on it (readers, faces, renderer)
proceeds in parallel.

## Known GRT tab attributes

From a real GRT 2021.2030-NIGHTLY load:

```xml
<ShotGroup index="2" hasfocus="false" title="Shot%20group"
           zoom="0.25" scrollPositionX="0" scrollPositionY="0"
           refPoint1X="0" refPoint1Y="0" refPoint2X="0" refPoint2Y="0"
           refDistance="150.01239999999999" shootDistance="914.39999999999998"
           pointSize="0.032027209602376" statisticSize="1"
           darkenImageAlpha="0.9" showQuickHelp="true">
  <picture name="GordonsReloadingTool.3f5cf1c03dd7a700.jpg" type="jpeg"
           width="1600" height="722" data="..." />
</ShotGroup>
```

Titles are URL-encoded. The picture element accepts `jpeg`; the kit's existing
gallery writer uses `png`, which this plugin also emits.
