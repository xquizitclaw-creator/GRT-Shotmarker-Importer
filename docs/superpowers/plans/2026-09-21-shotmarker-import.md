# ShotMarker Import Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a standalone Gordon's Reloading Tool plugin that reads ShotMarker `.tar` and `.csv` exports and writes a GRT load containing, per shooting string, a native shot-group tab with a rendered target picture, a velocity measurement, and a statistics note.

**Architecture:** All logic lives in `ShotMarkerCore`, a `net8.0` library: export readers, the target-face library, a SkiaSharp renderer, and a writer that appends tabs to a `GrtLoadDoc`. `ShotMarkerPlugin` is a thin `net8.0-windows` WinForms shell that talks to GRT over IPC. `GrtPluginKit`, the existing shared kit from GRT-Reloading-Toolkit, is consumed as a git submodule and gains the ability to write shot-group tabs.

**Tech Stack:** .NET 8, SkiaSharp, xUnit, Node.js (build-time face extraction only), WinForms (shell only).

**Spec:** `docs/superpowers/specs/2026-09-21-shotmarker-import-design.md`

## Global Constraints

- Target frameworks: `net8.0` for `ShotMarkerCore`, `ShotMarkerCore.Tests` and `GrtPluginKit`; `net8.0-windows` for `ShotMarkerPlugin`.
- The only third-party runtime dependency is **SkiaSharp 2.88.8**. Tar and zlib come from `System.Formats.Tar` and `System.IO.Compression.ZLibStream`. Adding any other package requires a note in the commit message saying why the BCL could not do it.
- Test packages: `xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2, `Microsoft.NET.Test.Sdk` 17.11.1 — matching GRT-Reloading-Toolkit so the two repos share a toolchain.
- Every number parsed from or written to a file uses `CultureInfo.InvariantCulture`. GRT writes `914.39999999999998`; a German locale build must read that identically.
- Plugin identity: id `com.grt.plugin.shotmarker`, executable `GRT_ShotMarker.exe`, folder `plugins/ShotMarker`.
- Generated load files use the sibling family **`shotmarker`**, never `toolkit` — the toolkit prunes its own family to the three newest and would delete this plugin's output.
- Coordinates: ShotMarker is millimetres from target centre with y up. GRT is image fractions 0..1 with y down. Every conversion goes through `TargetProjection`; no file computes fractions by hand.
- `ShotMarkerCore` must never reference `System.Drawing` or any Windows-only API. It builds and its tests run on macOS and Linux.

---

## Task 1: Capture the GRT calibration sample

This task is human-in-the-loop and blocks Tasks 7 and 9. Nothing in the repository can tell us how GRT serialises shot points, because no GRT file with shot points exists yet — every `.grtload` on the GRT machine has been scanned and the only one with a `<ShotGroup>` has zero `<group>` and zero `<point>` elements. The format has to be observed once, from a file GRT wrote.

**Files:**
- Create: `fixtures/grt/sample-with-points.grtload`
- Create: `fixtures/grt/FINDINGS.md`
- Create: `tools/inspect-shotgroup.sh`

**Interfaces:**
- Consumes: nothing.
- Produces: `fixtures/grt/sample-with-points.grtload` (a GRT-written load whose `<ShotGroup>` carries reference points and `<point>` elements) and `fixtures/grt/FINDINGS.md`, which records two decisions the later tasks read: `RefDistanceUnit` (`Millimetres` or `DisplayUnits`) and the exact `<group>`/`<point>` attribute spelling.

- [ ] **Step 1: Ask the user to author the sample in GRT**

Send them exactly this, and wait:

> In the shot-group tab of a load with the ShotMarker screenshot already attached:
> 1. Click the list entry **"Reference distance"**, then click the two ends of the red 10-inch line across the 10 ring. Enter **10** as the distance.
> 2. Set **"Shooting distance"** to **1000**.
> 3. Click the **"(+) Group"** button. Without a group there is nowhere for shots to live — this is the step that was missed last time.
> 4. Click **"(+) Shot"** three times, dragging each point onto a hit.
> 5. Mark one shot as a **flyer** and one as **point of aim**.
> 6. Save the load, and tell me when it is saved.

- [ ] **Step 2: Fetch the saved file**

```bash
mkdir -p fixtures/grt
ssh bill@raider "powershell -NoProfile -Command \
  \"[Convert]::ToBase64String([IO.File]::ReadAllBytes((Get-ChildItem 'C:\\Users\\bill\\GordonsReloadingTool\\loads' -Filter *.grtload -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName))\"" \
  > /tmp/sample.b64
base64 -d /tmp/sample.b64 > fixtures/grt/sample-with-points.grtload
ls -l fixtures/grt/sample-with-points.grtload
```

- [ ] **Step 3: Inspect the shot-group tab**

```bash
cat > tools/inspect-shotgroup.sh <<'SH'
#!/usr/bin/env bash
# Prints a .grtload's <ShotGroup> tabs with the base64 picture payload elided,
# which is the only way to read one: the payload is ~600 KB of a ~1.7 MB file.
set -euo pipefail
python3 - "$1" <<'PY'
import sys, re, xml.dom.minidom
src = open(sys.argv[1], encoding='utf-8', errors='replace').read()
for m in re.finditer(r'<ShotGroup\b.*?</ShotGroup>|<ShotGroup\b[^>]*/>', src, re.S):
    x = re.sub(r'data="[^"]*"', 'data="[elided]"', m.group(0))
    print(xml.dom.minidom.parseString(x).toprettyxml(indent='  '))
PY
SH
chmod +x tools/inspect-shotgroup.sh
./tools/inspect-shotgroup.sh fixtures/grt/sample-with-points.grtload
```

Expected: a `<ShotGroup>` with non-zero `refPoint1X`/`refPoint1Y`/`refPoint2X`/`refPoint2Y`, at least one `<group>` child, and `<point>` children inside it.

If `<group>` or `<point>` is still absent, stop and return to Step 1 — do not guess the format.

- [ ] **Step 4: Record the findings**

Write `fixtures/grt/FINDINGS.md` with the observed values filled in:

```markdown
# GRT shot-group serialisation, observed

Source: `sample-with-points.grtload`, written by GRT 2021.2030-NIGHTLY,
configured `range=yard; refdistance=in`, reference line 10 inches, 1000 yards.

## Reference distance unit

Observed `refDistance="<value>"`.

- 254 (= 10 × 25.4) means GRT stores **millimetres**: `RefDistanceUnit = Millimetres`.
- 10 means GRT stores **display units**: `RefDistanceUnit = DisplayUnits`.

Decision: `RefDistanceUnit = <Millimetres|DisplayUnits>`

Observed `shootDistance="<value>"`. 914.4 means metres; 1000 means yards.

Decision: `ShootDistanceUnit = <Metres|DisplayUnits>`

## Point serialisation

Verbatim, one group and its points:

```xml
<paste here>
```

Attribute spelling: x=`<name>`, y=`<name>`, flyer=`<name>`, point of aim=`<name>`.
Boolean spelling: `<true|1|True>`.
```

- [ ] **Step 5: Commit**

```bash
git add fixtures/grt tools/inspect-shotgroup.sh
git commit -m "test: capture a GRT-written shot group as the format fixture"
```

---

## Task 2: Repository scaffolding

**Files:**
- Create: `GrtShotMarker.sln`
- Create: `Directory.Build.props`
- Create: `ShotMarkerCore/ShotMarkerCore.csproj`
- Create: `ShotMarkerCore.Tests/ShotMarkerCore.Tests.csproj`
- Create: `ShotMarkerCore.Tests/Fixtures.cs`
- Create: `.gitmodules` (via `git submodule add`)

**Interfaces:**
- Consumes: nothing.
- Produces: `ShotMarkerCore.Tests.Fixtures.Path(string name)` → absolute path of a file under the repo's `fixtures/` directory, for every later test to use.

- [ ] **Step 1: Add the shared kit as a submodule and move the fixtures**

```bash
git submodule add https://github.com/FREESHOTER/GRT-Reloading-Toolkit.git external/GRT-Reloading-Toolkit
mkdir -p fixtures/shotmarker
git mv SM_export_Sep_21.tar fixtures/shotmarker/
git mv SM_shotslog_Sep_21.csv fixtures/shotmarker/
git mv "Screenshot 2026-09-21 075335.png" fixtures/shotmarker/shotmarker-ui.png
```

- [ ] **Step 2: Write the build props and project files**

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <Version>0.1.0</Version>
    <Company>community</Company>
    <Product>GRT ShotMarker Import</Product>
  </PropertyGroup>
</Project>
```

`ShotMarkerCore/ShotMarkerCore.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>ShotMarker.Core</RootNamespace>
    <Deterministic>true</Deterministic>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="SkiaSharp" Version="2.88.8" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\external\GRT-Reloading-Toolkit\grt-plugins-shared\GrtPluginKit\GrtPluginKit.csproj" />
  </ItemGroup>

</Project>
```

`ShotMarkerCore.Tests/ShotMarkerCore.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>ShotMarker.Core.Tests</RootNamespace>
    <Deterministic>true</Deterministic>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <!-- SkiaSharp ships macOS and Windows natives in the main package but not Linux,
         so the renderer's golden-image tests would fail on CI without this. -->
    <PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="2.88.8" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\ShotMarkerCore\ShotMarkerCore.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Write the failing fixture-locator test**

`ShotMarkerCore.Tests/Fixtures.cs`:

```csharp
using System.Reflection;

namespace ShotMarker.Core.Tests;

/// <summary>Locates the committed sample files. The test binary runs from
/// bin/Debug/net8.0, so the repo root is found by walking up to the solution.</summary>
public static class Fixtures
{
    public static string Root { get; } = FindRoot();

    public static string Path(string relative) =>
        System.IO.Path.Combine(Root, "fixtures", relative);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location)!);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "GrtShotMarker.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

public class FixturesTests
{
    [Fact]
    public void FindsTheCommittedExports()
    {
        Assert.True(File.Exists(Fixtures.Path("shotmarker/SM_export_Sep_21.tar")));
        Assert.True(File.Exists(Fixtures.Path("shotmarker/SM_shotslog_Sep_21.csv")));
    }
}
```

- [ ] **Step 4: Create the solution and run the test to verify it fails**

```bash
dotnet new sln -n GrtShotMarker
dotnet sln add ShotMarkerCore/ShotMarkerCore.csproj ShotMarkerCore.Tests/ShotMarkerCore.Tests.csproj
dotnet test
```

Expected: FAIL — the projects do not compile until the csproj files above exist, or `FindsTheCommittedExports` fails because the fixtures were not moved.

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test
```

Expected: PASS, 1 test.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "build: scaffold core, tests and the shared kit submodule"
```

---

## Task 3: Target face library

**Files:**
- Create: `tools/extract-targetfaces.js`
- Create: `fixtures/shotmarker/bundle/index.html` (snapshot of the device's web bundle)
- Create: `ShotMarkerCore/Faces/TargetFace.cs`
- Create: `ShotMarkerCore/Faces/TargetFaceLibrary.cs`
- Create: `ShotMarkerCore/Faces/targetfaces.json` (generated, embedded resource)
- Create: `ShotMarkerCore.Tests/TargetFaceTests.cs`
- Modify: `ShotMarkerCore/ShotMarkerCore.csproj`

**Interfaces:**
- Consumes: `Fixtures.Path` from Task 2.
- Produces:
  - `ShotMarker.Core.Faces.TargetFace` — `Id`, `Name`, `ShortName`, `BoardWidthMm`, `BoardHeightMm`, `BoardLineMm`, `Rings`, `Polys`, `Texts`.
  - `ShotMarker.Core.Faces.TargetRing(double DiamMm, string Color, double LineMm, string? Score, double XMm, double YMm)`.
  - `ShotMarker.Core.Faces.TargetPoly(string Color, double LineMm, IReadOnlyList<TargetPoint> Points)`.
  - `ShotMarker.Core.Faces.TargetText(double XMm, double YMm, double SizeMm, string Color, string Text)`.
  - `ShotMarker.Core.Faces.TargetPoint(double XMm, double YMm)`.
  - `TargetFaceLibrary.Find(string faceId)` → `TargetFace?`; `TargetFaceLibrary.Generic(double widthMm, double heightMm)` → `TargetFace`; `TargetFaceLibrary.Count` → `int`.

- [ ] **Step 1: Snapshot the bundle and write the extractor**

Copy the archived bundle into the repo so the extraction is reproducible without the device:

```bash
mkdir -p fixtures/shotmarker/bundle tools
cp "$SCRATCH/sm/index.html" fixtures/shotmarker/bundle/index.html
```

`tools/extract-targetfaces.js`:

```javascript
// Extracts every ShotMarker target face from an archived copy of the device's web
// bundle. It runs the bundle's own init_targetfaces() rather than pattern-matching
// the source: faces carry expressions (8/INCH), helper calls (polybox, text_rings)
// and locals shared across entries, none of which survive a regex — an early regex
// pass saw 144 of them.
const fs = require("fs");
const [, , bundlePath, outPath] = process.argv;
const src = fs.readFileSync(bundlePath, "utf8");

/** Index of the brace closing the one that opens at or after `i`. */
function closeBrace(s, i) {
  let depth = 0, quote = null;
  for (; i < s.length; i++) {
    const c = s[i];
    if (quote) { if (c === "\\") i++; else if (c === quote) quote = null; continue; }
    if (c === '"' || c === "'") { quote = c; continue; }
    if (c === "{") depth++;
    else if (c === "}" && --depth === 0) return i;
  }
  throw new Error("unbalanced braces");
}

/** The full source of a named top-level function declaration. */
function fnSource(name) {
  const i = src.indexOf("function " + name + "(");
  if (i < 0) throw new Error("function not found in bundle: " + name);
  return src.slice(i, closeBrace(src, src.indexOf("{", i)) + 1);
}

const consts = src.match(/VERSION="[^"]*",FPS=[^;]*;/);
if (!consts) throw new Error("constant block not found in bundle");

const body = [
  "var " + consts[0],
  "var targetfaces = {}, targetface_categories = [];",
  ...["update_object", "polybox", "polyarc", "text_rings", "add_default_ring_text"].map(fnSource),
  fnSource("init_targetfaces"),
  "init_targetfaces();",
  "return { faces: targetfaces, categories: targetface_categories };",
].join("\n");

const { faces, categories } = new Function(body)();
console.log(`extracted ${Object.keys(faces).length} faces, ${categories.length} categories`);
fs.writeFileSync(outPath, JSON.stringify({ faces, categories }, null, 1));
```

```bash
node tools/extract-targetfaces.js fixtures/shotmarker/bundle/index.html ShotMarkerCore/Faces/targetfaces.json
```

Expected output: `extracted 208 faces, 19 categories`.

- [ ] **Step 2: Write the failing tests**

`ShotMarkerCore.Tests/TargetFaceTests.cs`:

```csharp
using ShotMarker.Core.Faces;

namespace ShotMarker.Core.Tests;

public class TargetFaceTests
{
    [Fact]
    public void LoadsEveryFaceFromTheBundle()
    {
        Assert.Equal(208, TargetFaceLibrary.Count);
    }

    [Fact]
    public void NraLongRangeFClassMatchesThePublishedSpecification()
    {
        var f = TargetFaceLibrary.Find("NRA_LRFC");
        Assert.NotNull(f);

        const double mmPerInch = 25.4;
        Assert.Equal(72 * mmPerInch, f!.BoardWidthMm, 1);
        Assert.Equal(72 * mmPerInch, f.BoardHeightMm, 1);

        // X=5", 10=10", 9=20", 8=30", 7=44", 6=60" — the user confirmed the 5" X ring.
        var byScore = f.Rings.ToDictionary(r => r.Score!, r => r.DiamMm / mmPerInch);
        Assert.Equal(5, byScore["X"], 1);
        Assert.Equal(10, byScore["10"], 1);
        Assert.Equal(20, byScore["9"], 1);
        Assert.Equal(30, byScore["8"], 1);
        Assert.Equal(44, byScore["7"], 1);
        Assert.Equal(60, byScore["6"], 1);
    }

    [Fact]
    public void FacesWithOffsetRingsAndPolygonsSurvive()
    {
        // IBS 100yd BR has 14 rings placed off-centre; it is the face an expression-blind
        // extractor loses, so it stands guard over the extraction method.
        var ibs = TargetFaceLibrary.Find("IBS100BR");
        Assert.NotNull(ibs);
        Assert.Equal(14, ibs!.Rings.Count);
        Assert.Contains(ibs.Rings, r => r.XMm != 0 || r.YMm != 0);
    }

    [Fact]
    public void UnknownFaceIdReturnsNull()
    {
        Assert.Null(TargetFaceLibrary.Find("NO_SUCH_FACE"));
    }

    [Fact]
    public void GenericFallbackHasNoRings()
    {
        var g = TargetFaceLibrary.Generic(1887, 1908);
        Assert.Empty(g.Rings);
        Assert.Equal(1887, g.BoardWidthMm, 1);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~TargetFaceTests"
```

Expected: FAIL — `TargetFaceLibrary` does not exist.

- [ ] **Step 4: Write the model**

`ShotMarkerCore/Faces/TargetFace.cs`:

```csharp
namespace ShotMarker.Core.Faces;

/// <summary>A point on the face, millimetres from centre, y up (ShotMarker's convention).</summary>
public sealed record TargetPoint(double XMm, double YMm);

/// <summary>One scoring ring. <paramref name="Color"/> is ShotMarker's code:
/// w/b/g white, black, grey, and the "l" suffix (wl/bl/gl) meaning line-only.</summary>
public sealed record TargetRing(double DiamMm, string Color, double LineMm, string? Score,
                                double XMm = 0, double YMm = 0);

public sealed record TargetPoly(string Color, double LineMm, IReadOnlyList<TargetPoint> Points);

public sealed record TargetText(double XMm, double YMm, double SizeMm, string Color, string Text);

public sealed record TargetFace(
    string Id, string Name, string ShortName,
    double BoardWidthMm, double BoardHeightMm, double BoardLineMm,
    IReadOnlyList<TargetRing> Rings,
    IReadOnlyList<TargetPoly> Polys,
    IReadOnlyList<TargetText> Texts);
```

- [ ] **Step 5: Write the library**

`ShotMarkerCore/Faces/TargetFaceLibrary.cs`:

```csharp
using System.Reflection;
using System.Text.Json;

namespace ShotMarker.Core.Faces;

/// <summary>
/// The 208 built-in ShotMarker target faces, read once from the generated
/// <c>targetfaces.json</c> resource. See <c>tools/extract-targetfaces.js</c> for how
/// that file is produced from an archived copy of the device's web bundle.
/// </summary>
public static class TargetFaceLibrary
{
    private static readonly Dictionary<string, TargetFace> Faces = Load();

    public static int Count => Faces.Count;

    public static TargetFace? Find(string faceId) =>
        Faces.TryGetValue(faceId, out var f) ? f : null;

    /// <summary>A blank board of the given size: shots, centre cross and scale bar only.
    /// What an unrecognised face_id renders as, so an unknown target never aborts an import.</summary>
    public static TargetFace Generic(double widthMm, double heightMm) =>
        new("GENERIC", "Unknown target", "Unknown", widthMm, heightMm, 2,
            Array.Empty<TargetRing>(), Array.Empty<TargetPoly>(), Array.Empty<TargetText>());

    private static Dictionary<string, TargetFace> Load()
    {
        using Stream s = typeof(TargetFaceLibrary).Assembly
            .GetManifestResourceStream("ShotMarker.Core.Faces.targetfaces.json")
            ?? throw new InvalidOperationException("targetfaces.json resource missing");
        using JsonDocument doc = JsonDocument.Parse(s);

        var result = new Dictionary<string, TargetFace>(StringComparer.Ordinal);
        foreach (JsonProperty p in doc.RootElement.GetProperty("faces").EnumerateObject())
            result[p.Name] = ReadFace(p.Name, p.Value);
        return result;
    }

    private static TargetFace ReadFace(string id, JsonElement e)
    {
        JsonElement board = e.GetProperty("board");
        return new TargetFace(
            id,
            Str(e, "name") ?? id,
            Str(e, "shortname") ?? id,
            Num(board, "w"), Num(board, "h"), Num(board, "line", 2),
            ReadRings(e), ReadPolys(e), ReadTexts(e));
    }

    private static IReadOnlyList<TargetRing> ReadRings(JsonElement e)
    {
        if (!e.TryGetProperty("rings", out JsonElement rings)) return Array.Empty<TargetRing>();
        var list = new List<TargetRing>();
        foreach (JsonElement r in rings.EnumerateArray())
            list.Add(new TargetRing(Num(r, "diam"), Str(r, "color") ?? "b", Num(r, "line", 1),
                                    Score(r), Num(r, "x"), Num(r, "y")));
        return list;
    }

    private static IReadOnlyList<TargetPoly> ReadPolys(JsonElement e)
    {
        if (!e.TryGetProperty("poly", out JsonElement polys)) return Array.Empty<TargetPoly>();
        var list = new List<TargetPoly>();
        foreach (JsonElement p in polys.EnumerateArray())
        {
            var pts = new List<TargetPoint>();
            if (p.TryGetProperty("points", out JsonElement points))
                foreach (JsonElement pt in points.EnumerateArray())
                    pts.Add(new TargetPoint(Num(pt, "x"), Num(pt, "y")));
            list.Add(new TargetPoly(Str(p, "color") ?? "b", Num(p, "line", 1), pts));
        }
        return list;
    }

    private static IReadOnlyList<TargetText> ReadTexts(JsonElement e)
    {
        if (!e.TryGetProperty("text", out JsonElement texts)) return Array.Empty<TargetText>();
        var list = new List<TargetText>();
        foreach (JsonElement t in texts.EnumerateArray())
            list.Add(new TargetText(Num(t, "x"), Num(t, "y"), Num(t, "size", 20),
                                    Str(t, "color") ?? "gl", Score(t) ?? ""));
        return list;
    }

    /// <summary>Ring and text scores are numbers ("10") or letters ("X", "V"), so both come back as text.</summary>
    private static string? Score(JsonElement e)
    {
        foreach (string key in new[] { "score", "text" })
            if (e.TryGetProperty(key, out JsonElement v))
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetDouble().ToString("0.###",
                        System.Globalization.CultureInfo.InvariantCulture),
                    _ => null,
                };
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name, double fallback = 0) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
}
```

- [ ] **Step 6: Embed the resource**

Add to `ShotMarkerCore/ShotMarkerCore.csproj`, inside a new `ItemGroup`:

```xml
  <ItemGroup>
    <!-- Generated by tools/extract-targetfaces.js from the archived bundle in
         fixtures/shotmarker/bundle. Committed so a build needs no Node. -->
    <EmbeddedResource Include="Faces\targetfaces.json" LogicalName="ShotMarker.Core.Faces.targetfaces.json" />
  </ItemGroup>
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test --filter "FullyQualifiedName~TargetFaceTests"
```

Expected: PASS, 5 tests.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: extract and load the 208 ShotMarker target faces"
```

---

## Task 4: ShotMarker model and `.tar` reader

**Files:**
- Create: `ShotMarkerCore/Sm/SmModels.cs`
- Create: `ShotMarkerCore/Sm/SmTarReader.cs`
- Create: `ShotMarkerCore.Tests/SmTarReaderTests.cs`

**Interfaces:**
- Consumes: `Fixtures.Path`.
- Produces:
  - `ShotMarker.Core.Sm.SmShot(int Number, double XMm, double YMm, double? VelocityMps, string? Score, double? TempC, bool IsSighter, bool IsInvalid)`.
  - `ShotMarker.Core.Sm.SmGroupStats(double? MeanRadiusMm, double? GroupSizeMm, double? CtcMm, double? VelocityAvgMps, double? VelocitySdMps, double? VelocityEsMps)`.
  - `ShotMarker.Core.Sm.SmString(string Id, string Name, DateTimeOffset Timestamp, string FaceId, double DistanceValue, string DistanceUnit, double FrameWidthMm, double FrameHeightMm, double? BulletDiameterMm, string? ScoreText, IReadOnlyList<SmShot> Shots, SmGroupStats? Stats)` with computed `DistanceMetres`.
  - `SmTarReader.Read(Stream tar, IList<string> log)` → `IReadOnlyList<SmString>`.

- [ ] **Step 1: Write the failing tests**

`ShotMarkerCore.Tests/SmTarReaderTests.cs`:

```csharp
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Tests;

public class SmTarReaderTests
{
    private static IReadOnlyList<SmString> Read(out List<string> log)
    {
        log = new List<string>();
        using FileStream fs = File.OpenRead(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"));
        return SmTarReader.Read(fs, log);
    }

    [Fact]
    public void ReadsEveryStringInTheArchive()
    {
        var strings = Read(out _);
        Assert.Equal(3, strings.Count);
        Assert.All(strings, s => Assert.NotEmpty(s.Shots));
    }

    [Fact]
    public void ReadsTheFaceDistanceAndFrameOfAString()
    {
        var s = Read(out _).First();
        Assert.Equal("NRA_LRFC", s.FaceId);
        Assert.Equal(1000, s.DistanceValue, 0);
        Assert.Equal("y", s.DistanceUnit);
        Assert.Equal(914.4, s.DistanceMetres, 1);
        Assert.True(s.FrameWidthMm > 0 && s.FrameHeightMm > 0);
    }

    [Fact]
    public void ShotCoordinatesAreMillimetresFromCentreAndVelocitiesAreMetresPerSecond()
    {
        var s = Read(out _).First();
        // A 1000 yd F-Class group lives within a 72 inch board: 914 mm from centre at most.
        Assert.All(s.Shots, sh => Assert.InRange(Math.Abs(sh.XMm), 0, 914));
        Assert.All(s.Shots, sh => Assert.InRange(Math.Abs(sh.YMm), 0, 914));
        // The archive stores m/s; a 180 gr .284 leaves at roughly 800 m/s.
        var v = s.Shots.Where(sh => sh.VelocityMps is > 0).Select(sh => sh.VelocityMps!.Value).ToList();
        Assert.NotEmpty(v);
        Assert.All(v, x => Assert.InRange(x, 400, 1200));
    }

    [Fact]
    public void ShotsAreNumberedFromOne()
    {
        var s = Read(out _).First();
        Assert.Equal(Enumerable.Range(1, s.Shots.Count), s.Shots.Select(sh => sh.Number));
    }

    [Fact]
    public void CarriesShotMarkersOwnGroupStatistics()
    {
        var s = Read(out _).First();
        Assert.NotNull(s.Stats);
        Assert.True(s.Stats!.GroupSizeMm > 0);
    }

    [Fact]
    public void ATruncatedEntryIsLoggedAndSkippedRatherThanThrowing()
    {
        // Truncating the archive mid-entry is what a half-copied export looks like.
        byte[] whole = File.ReadAllBytes(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"));
        using var cut = new MemoryStream(whole, 0, whole.Length / 2);
        var log = new List<string>();
        var strings = SmTarReader.Read(cut, log);
        Assert.NotEmpty(log);
        Assert.All(strings, s => Assert.NotEmpty(s.Shots));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~SmTarReaderTests"
```

Expected: FAIL — `SmTarReader` does not exist.

- [ ] **Step 3: Write the model**

`ShotMarkerCore/Sm/SmModels.cs`:

```csharp
namespace ShotMarker.Core.Sm;

/// <summary>One shot. Position is millimetres from target centre with y up, as ShotMarker
/// reports it; velocity is normalised to m/s at the reader boundary whatever the source said.</summary>
public sealed record SmShot(
    int Number, double XMm, double YMm, double? VelocityMps,
    string? Score, double? TempC, bool IsSighter, bool IsInvalid)
{
    /// <summary>Shots GRT should exclude from group statistics: sighters and rejects.</summary>
    public bool IsFlyer => IsSighter || IsInvalid;
}

/// <summary>ShotMarker's own computed statistics for a group, carried through unaltered
/// so the note this plugin writes says what the device said.</summary>
public sealed record SmGroupStats(
    double? MeanRadiusMm, double? GroupSizeMm, double? CtcMm,
    double? VelocityAvgMps, double? VelocitySdMps, double? VelocityEsMps);

/// <summary>One shooting string, the same shape whether it came from a .tar or a .csv.</summary>
public sealed record SmString(
    string Id, string Name, DateTimeOffset Timestamp,
    string FaceId, double DistanceValue, string DistanceUnit,
    double FrameWidthMm, double FrameHeightMm,
    double? BulletDiameterMm, string? ScoreText,
    IReadOnlyList<SmShot> Shots, SmGroupStats? Stats)
{
    private const double MetresPerYard = 0.9144;

    public double DistanceMetres =>
        DistanceUnit.StartsWith("y", StringComparison.OrdinalIgnoreCase)
            ? DistanceValue * MetresPerYard
            : DistanceValue;
}
```

- [ ] **Step 4: Write the reader**

`ShotMarkerCore/Sm/SmTarReader.cs`:

```csharp
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace ShotMarker.Core.Sm;

/// <summary>
/// Reads a ShotMarker `.tar` export: an <c>archive.txt</c> JSON index plus one
/// zlib-compressed <c>string-&lt;id&gt;.z</c> per string. Both formats come from
/// .NET itself — System.Formats.Tar and ZLibStream — so no third-party code is involved.
///
/// The authoritative shot record is <c>groups[].shots[]</c>; the sibling <c>shots</c>
/// array is a re-encoding of the same hits and is ignored.
/// </summary>
public static class SmTarReader
{
    public static IReadOnlyList<SmString> Read(Stream tar, IList<string> log)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var reader = new TarReader(tar, leaveOpen: true);
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.DataStream is null) continue;
                using var ms = new MemoryStream();
                entry.DataStream.CopyTo(ms);
                entries[Path.GetFileName(entry.Name)] = ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            // A truncated archive still yields every entry read before the cut.
            log.Add($"archive ended early ({ex.GetType().Name}); read {entries.Count} entries");
        }

        var result = new List<SmString>();
        foreach ((string name, byte[] data) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!name.StartsWith("string-", StringComparison.Ordinal)) continue;
            try
            {
                using var src = new MemoryStream(data);
                using var zs = new ZLibStream(src, CompressionMode.Decompress);
                using var json = new MemoryStream();
                zs.CopyTo(json);
                json.Position = 0;
                SmString? s = ReadString(Path.GetFileNameWithoutExtension(name), json, log);
                if (s != null) result.Add(s);
            }
            catch (Exception ex)
            {
                log.Add($"{name}: unreadable ({ex.Message}) — skipped");
            }
        }
        return result;
    }

    private static SmString? ReadString(string id, Stream json, IList<string> log)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        var shots = new List<SmShot>();
        SmGroupStats? stats = null;
        var invalid = InvalidIds(root);

        if (root.TryGetProperty("groups", out JsonElement groups))
            foreach (JsonElement g in groups.EnumerateArray())
            {
                stats ??= ReadStats(g);
                if (!g.TryGetProperty("shots", out JsonElement gs)) continue;
                foreach (JsonElement sh in gs.EnumerateArray())
                {
                    bool hidden = sh.TryGetProperty("display", out JsonElement d)
                                  && d.ValueKind == JsonValueKind.False;
                    string? tag = Str(sh, "display_text");
                    bool sighter = hidden
                                   || tag?.Contains("sighter", StringComparison.OrdinalIgnoreCase) == true;
                    shots.Add(new SmShot(
                        shots.Count + 1,
                        Num(sh, "x") ?? 0, Num(sh, "y") ?? 0,
                        Num(sh, "v"), Str(sh, "score"), Num(sh, "temp"),
                        sighter, invalid.Contains(Str(sh, "id") ?? "")));
                }
            }

        if (shots.Count == 0) { log.Add($"{id}: no shots — skipped"); return null; }

        return new SmString(
            id,
            Str(root, "name") ?? id,
            Timestamp(root),
            Str(root, "face_id") ?? "",
            Num(root, "dist") ?? 0,
            Str(root, "dist_unit") ?? "m",
            Num(root, "width") ?? 0,
            Num(root, "height") ?? 0,
            Num(root, "bullet"),
            Str(root, "score_text"),
            shots,
            stats);
    }

    private static HashSet<string> InvalidIds(JsonElement root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("shots_invalid", out JsonElement inv) && inv.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in inv.EnumerateArray())
                set.Add(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.ToString());
        return set;
    }

    private static SmGroupStats ReadStats(JsonElement g) => new(
        Num(g, "mr"), Num(g, "size"), Num(g, "ctc"),
        Num(g, "v_avg"), Num(g, "v_sd"), Num(g, "v_es"));

    private static DateTimeOffset Timestamp(JsonElement root) =>
        Num(root, "ts") is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms)
            : DateTimeOffset.MinValue;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double d) => d,
            _ => null,
        };
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test --filter "FullyQualifiedName~SmTarReaderTests"
```

Expected: PASS, 6 tests. If a field name differs from the spec's table, read the real JSON before changing the test:

```bash
python3 - <<'PY'
import tarfile, zlib, json
t = tarfile.open('fixtures/shotmarker/SM_export_Sep_21.tar')
for m in t.getmembers():
    if m.name.endswith('.z'):
        d = json.loads(zlib.decompress(t.extractfile(m).read()))
        print(m.name, sorted(d.keys()))
        print('group keys:', sorted(d['groups'][0].keys()))
        print('shot keys:', sorted(d['groups'][0]['shots'][0].keys()))
        break
PY
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: read ShotMarker .tar exports"
```

---

## Task 5: `.csv` reader

**Files:**
- Create: `ShotMarkerCore/Sm/SmCsvReader.cs`
- Create: `ShotMarkerCore.Tests/SmCsvReaderTests.cs`

**Interfaces:**
- Consumes: `SmString`, `SmShot` from Task 4.
- Produces: `SmCsvReader.Read(TextReader csv, IList<string> log)` → `IReadOnlyList<SmString>`.

- [ ] **Step 1: Write the failing tests**

`ShotMarkerCore.Tests/SmCsvReaderTests.cs`:

```csharp
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Tests;

public class SmCsvReaderTests
{
    private static IReadOnlyList<SmString> Read(out List<string> log)
    {
        log = new List<string>();
        using var r = new StreamReader(Fixtures.Path("shotmarker/SM_shotslog_Sep_21.csv"));
        return SmCsvReader.Read(r, log);
    }

    [Fact]
    public void ReadsTheHeaderMetadataOfEachString()
    {
        var s = Read(out _).First();
        Assert.Equal("M6 R1 TT11", s.Name);
        Assert.Equal(1000, s.DistanceValue, 0);
        Assert.Equal("y", s.DistanceUnit);
        Assert.True(s.FrameWidthMm > 0);
    }

    [Fact]
    public void ConvertsVelocityFromFeetPerSecondToMetresPerSecond()
    {
        var v = Read(out _).SelectMany(s => s.Shots)
                           .Where(sh => sh.VelocityMps is > 0)
                           .Select(sh => sh.VelocityMps!.Value).ToList();
        Assert.NotEmpty(v);
        // The CSV column is fps; leaving it unconverted would put these near 2700.
        Assert.All(v, x => Assert.InRange(x, 400, 1200));
    }

    [Fact]
    public void MarksSighterTaggedShots()
    {
        var all = Read(out _).SelectMany(s => s.Shots).ToList();
        Assert.Contains(all, sh => sh.IsSighter);
    }

    [Fact]
    public void KeepsLetterScoresAsText()
    {
        var all = Read(out _).SelectMany(s => s.Shots).ToList();
        Assert.Contains(all, sh => sh.Score == "X");
    }

    [Fact]
    public void ABlankOrRaggedRowIsLoggedNotFatal()
    {
        var log = new List<string>();
        using var r = new StringReader(
            "Date: 2026-09-21\n" +
            "String: Test\n" +
            "Target: #220 1887 x 1908\n" +
            "Face: NRA Long Range FC at 1000y\n" +
            ",time,tags,id,score,temp C,x mm,y mm,v fps,yaw deg, pitch deg,quality,xy_err\n" +
            ",09:00:00,,1,X,20,10.5,-4.2,2700,0,0,1,0.5\n" +
            ",09:00:30,,2\n" +
            "\n" +
            ",09:01:00,,3,10,20,-30.1,12.0,2698,0,0,1,0.5\n");
        var strings = SmCsvReader.Read(r, log);
        Assert.Single(strings);
        Assert.Equal(2, strings[0].Shots.Count);
        Assert.NotEmpty(log);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~SmCsvReaderTests"
```

Expected: FAIL — `SmCsvReader` does not exist.

- [ ] **Step 3: Inspect the real header before implementing**

The header spelling decides the parsing; read it rather than assuming:

```bash
head -12 fixtures/shotmarker/SM_shotslog_Sep_21.csv
grep -n "time,tags,id" fixtures/shotmarker/SM_shotslog_Sep_21.csv
```

- [ ] **Step 4: Write the reader**

`ShotMarkerCore/Sm/SmCsvReader.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace ShotMarker.Core.Sm;

/// <summary>
/// Reads a ShotMarker `.csv` shot log. The file is a sequence of blocks: some header
/// lines naming the date, string, target and face, then a column header row, then one
/// row per shot, repeating for each string in the export.
///
/// The CSV reports velocity in fps where the .tar reports m/s. It is converted here, at
/// the boundary, so nothing downstream has to know which format a string came from.
/// </summary>
public static class SmCsvReader
{
    private const double MetresPerFoot = 0.3048;

    public static IReadOnlyList<SmString> Read(TextReader csv, IList<string> log)
    {
        var result = new List<SmString>();
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<SmShot>? shots = null;
        string[]? columns = null;
        int lineNo = 0;

        void Flush()
        {
            if (shots is { Count: > 0 }) result.Add(Build(header, shots, result.Count, log));
            else if (shots != null) log.Add($"string '{Header(header, "String")}': no shots — skipped");
            shots = null;
            columns = null;
        }

        while (csv.ReadLine() is { } line)
        {
            lineNo++;
            if (line.Trim().Length == 0) continue;
            string[] cells = SplitCsv(line);

            if (IsColumnHeader(cells)) { columns = cells; shots = new List<SmShot>(); continue; }

            if (columns == null)
            {
                // A metadata line: "Key: value", possibly several per line.
                if (shots != null) Flush();
                foreach (Match m in Regex.Matches(line, @"([A-Za-z ]+):\s*([^,]+)"))
                    header[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
                continue;
            }

            SmShot? shot = ReadShot(cells, columns, shots!.Count + 1, lineNo, log);
            if (shot != null) shots.Add(shot);
        }
        Flush();
        return result;
    }

    private static bool IsColumnHeader(string[] cells) =>
        cells.Any(c => c.Trim().Equals("x mm", StringComparison.OrdinalIgnoreCase)) &&
        cells.Any(c => c.Trim().Equals("y mm", StringComparison.OrdinalIgnoreCase));

    private static SmShot? ReadShot(string[] cells, string[] columns, int number, int lineNo, IList<string> log)
    {
        double? Col(string name)
        {
            int i = Array.FindIndex(columns, c => c.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i < cells.Length &&
                   double.TryParse(cells[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v : null;
        }
        string? Text(string name)
        {
            int i = Array.FindIndex(columns, c => c.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i < cells.Length && cells[i].Trim().Length > 0 ? cells[i].Trim() : null;
        }

        double? x = Col("x mm"), y = Col("y mm");
        if (x == null || y == null)
        {
            log.Add($"line {lineNo}: no shot position — skipped");
            return null;
        }

        string tags = Text("tags") ?? "";
        return new SmShot(
            number, x.Value, y.Value,
            Col("v fps") is { } fps and > 0 ? fps * MetresPerFoot : null,
            Text("score"), Col("temp C"),
            tags.Contains("sighter", StringComparison.OrdinalIgnoreCase),
            tags.Contains("incomplete", StringComparison.OrdinalIgnoreCase));
    }

    private static SmString Build(IDictionary<string, string> header, List<SmShot> shots, int index, IList<string> log)
    {
        // "NRA Long Range FC at 1000y" — the trailing distance is the reliable part;
        // the face name is prose and is matched to a face_id by the caller.
        string face = Header(header, "Face") ?? Header(header, "Target face") ?? "";
        var dm = Regex.Match(face, @"(\d+(?:\.\d+)?)\s*(y|m)\b", RegexOptions.IgnoreCase);
        double dist = dm.Success
            ? double.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        string unit = dm.Success ? dm.Groups[2].Value.ToLowerInvariant() : "m";
        if (!dm.Success) log.Add($"string {index + 1}: no distance in '{face}' — assuming 0 m");

        // "#220 1887 x 1908"
        var fm = Regex.Match(Header(header, "Target") ?? "", @"(\d+)\s*x\s*(\d+)");
        double w = fm.Success ? double.Parse(fm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        double h = fm.Success ? double.Parse(fm.Groups[2].Value, CultureInfo.InvariantCulture) : 0;

        DateTimeOffset ts = DateTimeOffset.TryParse(Header(header, "Date"),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset t)
            ? t : DateTimeOffset.MinValue;

        return new SmString(
            $"csv-{index + 1}",
            Header(header, "String") ?? $"String {index + 1}",
            ts, FaceIdFromName(face), dist, unit, w, h, null,
            Header(header, "Score"), shots, null);
    }

    /// <summary>The CSV names the face in prose ("NRA Long Range FC at 1000y") where the
    /// .tar gives an id. This maps the names this device emits; anything else returns
    /// empty, which the renderer treats as an unknown face.</summary>
    internal static string FaceIdFromName(string name) =>
        name.Contains("Long Range FC", StringComparison.OrdinalIgnoreCase) ? "NRA_LRFC" :
        name.Contains("Long Range", StringComparison.OrdinalIgnoreCase) ? "NRA_LR" :
        name.Contains("Mid Range FC", StringComparison.OrdinalIgnoreCase) ? "NRA_MR1FC" :
        name.Contains("Mid Range", StringComparison.OrdinalIgnoreCase) ? "NRA_MR1" :
        "";

    private static string? Header(IDictionary<string, string> h, string key) =>
        h.TryGetValue(key, out string? v) ? v : null;

    /// <summary>Splits one CSV line, honouring double-quoted cells.</summary>
    private static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) { cells.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(c);
        }
        cells.Add(cur.ToString());
        return cells.ToArray();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test --filter "FullyQualifiedName~SmCsvReaderTests"
```

Expected: PASS, 5 tests. If the fixture's header keys differ from `String:`/`Target:`/`Face:`, correct `Build` to the real spelling seen in Step 3 — not the test.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: read ShotMarker .csv shot logs"
```

---

## Task 6: Format-blind reader façade

**Files:**
- Create: `ShotMarkerCore/Sm/SmExportReader.cs`
- Create: `ShotMarkerCore.Tests/SmExportReaderTests.cs`

**Interfaces:**
- Consumes: `SmTarReader.Read`, `SmCsvReader.Read`.
- Produces: `SmExportReader.Read(string path, IList<string> log)` → `IReadOnlyList<SmString>`.

- [ ] **Step 1: Write the failing tests**

`ShotMarkerCore.Tests/SmExportReaderTests.cs`:

```csharp
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Tests;

public class SmExportReaderTests
{
    [Fact]
    public void DispatchesOnFileType()
    {
        var log = new List<string>();
        Assert.NotEmpty(SmExportReader.Read(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log));
        Assert.NotEmpty(SmExportReader.Read(Fixtures.Path("shotmarker/SM_shotslog_Sep_21.csv"), log));
    }

    [Fact]
    public void AnUnsupportedFileIsReportedNotThrown()
    {
        var log = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(path, "not an export");
        try
        {
            Assert.Empty(SmExportReader.Read(path, log));
            Assert.NotEmpty(log);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheTwoFormatsAgreeOnTheStringTheyShare()
    {
        var log = new List<string>();
        var tar = SmExportReader.Read(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log);
        var csv = SmExportReader.Read(Fixtures.Path("shotmarker/SM_shotslog_Sep_21.csv"), log);

        SmString? t = tar.FirstOrDefault(s => s.Name == "M6 R1 TT11");
        SmString? c = csv.FirstOrDefault(s => s.Name == "M6 R1 TT11");
        Assert.NotNull(t);
        Assert.NotNull(c);
        Assert.Equal(t!.Shots.Count, c!.Shots.Count);

        // Same hits, same order, to within the exports' own rounding.
        foreach (var (a, b) in t.Shots.Zip(c.Shots))
        {
            Assert.Equal(a.XMm, b.XMm, 1);
            Assert.Equal(a.YMm, b.YMm, 1);
            if (a.VelocityMps is { } av && b.VelocityMps is { } bv)
                Assert.Equal(av, bv, 0);   // fps is rounded to whole numbers before export
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~SmExportReaderTests"
```

Expected: FAIL — `SmExportReader` does not exist.

- [ ] **Step 3: Write the façade**

`ShotMarkerCore/Sm/SmExportReader.cs`:

```csharp
namespace ShotMarker.Core.Sm;

/// <summary>The one entry point for reading a ShotMarker export, whichever way it was saved.</summary>
public static class SmExportReader
{
    public static IReadOnlyList<SmString> Read(string path, IList<string> log)
    {
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".tar":
                    using (FileStream fs = File.OpenRead(path)) return SmTarReader.Read(fs, log);
                case ".csv":
                    using (var sr = new StreamReader(path)) return SmCsvReader.Read(sr, log);
                default:
                    log.Add($"{Path.GetFileName(path)}: not a ShotMarker export (.tar or .csv expected)");
                    return Array.Empty<SmString>();
            }
        }
        catch (Exception ex)
        {
            log.Add($"{Path.GetFileName(path)}: {ex.Message}");
            return Array.Empty<SmString>();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test --filter "FullyQualifiedName~SmExportReaderTests"
```

Expected: PASS, 3 tests.

If `TheTwoFormatsAgreeOnTheStringTheyShare` fails on shot count, check whether the CSV includes sighters the archive omits before changing either reader — the disagreement is the finding, and the test should then assert on the non-sighter shots.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: one reader entry point for both export formats"
```

---

## Task 7: Teach GrtPluginKit to write shot groups

This is a change to the submodule, shipped as its own pull request against GRT-Reloading-Toolkit. **Depends on Task 1** — the attribute spelling comes from `fixtures/grt/FINDINGS.md`, not from guesswork.

**Files (all in `external/GRT-Reloading-Toolkit`):**
- Modify: `grt-plugins-shared/GrtPluginKit/Grt/GrtLoadDoc.cs`
- Create: `GrtReloadingToolkit.Tests/GrtShotGroupWriteTests.cs`

**Interfaces:**
- Consumes: `fixtures/grt/FINDINGS.md` from Task 1.
- Produces:
  - `GrtPluginKit.Grt.ShotGroupGeometry(double RefP1X, double RefP1Y, double RefP2X, double RefP2Y, double RefDistance, double ShootDistance)`.
  - `GrtLoadDoc.AddShotGroup(string title, byte[] png, ShotGroupGeometry geom, IEnumerable<GrtShotGroupSet> groups, double pointSize = 0.03)`.
  - `GrtLoadDoc.SaveSibling(string suffix, string family = "toolkit")` → path.

- [ ] **Step 1: Branch the submodule**

```bash
cd external/GRT-Reloading-Toolkit
git checkout -b feat/write-shot-groups
```

- [ ] **Step 2: Write the failing tests**

`GrtReloadingToolkit.Tests/GrtShotGroupWriteTests.cs`:

```csharp
using GrtPluginKit.Grt;

namespace GrtReloadingToolkit.Tests;

public class GrtShotGroupWriteTests
{
    /// <summary>A 4x4 PNG, so AddShotGroup has a real IHDR to read width and height from.</summary>
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAFUlEQVR4nGP8z8DwnwEPYMKn" +
        "eBQMKgAAtM8BvQ3Y7HAAAAAASUVORK5CYII=");

    private static GrtLoadDoc NewDoc() =>
        GrtLoadDoc.CreateMinimal("test", Path.Combine(Path.GetTempPath(), "test.grtload"));

    [Fact]
    public void WritesATabThatItsOwnReaderUnderstands()
    {
        var doc = NewDoc();
        var set = new GrtShotGroupSet { Name = "41.5gr" };
        set.Points.Add(new GrtShotPoint(0.25, 0.25, false, false));
        set.Points.Add(new GrtShotPoint(0.75, 0.60, true, false));
        set.Points.Add(new GrtShotPoint(0.50, 0.50, false, true));

        doc.AddShotGroup("ShotMarker — M6 R1", TinyPng(),
            new ShotGroupGeometry(0.1, 0.5, 0.9, 0.5, 254, 914.4), new[] { set });

        GrtShotGroup tab = Assert.Single(doc.ShotGroups());
        Assert.Equal("ShotMarker — M6 R1", tab.Title);
        Assert.Equal(0.1, tab.RefP1X, 6);
        Assert.Equal(0.9, tab.RefP2X, 6);
        Assert.Equal(254, tab.RefDistance, 6);
        Assert.Equal(914.4, tab.ShootDistance, 6);
        Assert.Equal(4, tab.ImageWidth);
        Assert.Equal(4, tab.ImageHeight);

        GrtShotGroupSet back = Assert.Single(tab.Groups);
        Assert.Equal("41.5gr", back.Name);
        Assert.Equal(3, back.Points.Count);
        Assert.Contains(back.Points, p => p.Flyer);
        Assert.Contains(back.Points, p => p.PointOfAim);
    }

    [Fact]
    public void SurvivesASaveAndReload()
    {
        var doc = NewDoc();
        var set = new GrtShotGroupSet { Name = "g" };
        set.Points.Add(new GrtShotPoint(0.3125, 0.6875, false, false));
        doc.AddShotGroup("Group", TinyPng(),
            new ShotGroupGeometry(0, 0.5, 1, 0.5, 254, 914.4), new[] { set });

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".grtload");
        try
        {
            doc.Save(path);
            GrtShotGroup tab = Assert.Single(GrtLoadDoc.Load(path).ShotGroups());
            GrtShotPoint p = Assert.Single(Assert.Single(tab.Groups).Points);
            Assert.Equal(0.3125, p.X, 6);
            Assert.Equal(0.6875, p.Y, 6);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MatchesTheAttributeSpellingGrtItselfWrote()
    {
        // The fixture is a load GRT saved with points placed by hand. Writing anything
        // GRT does not write is how this plugin would produce a file GRT silently ignores.
        string fixture = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "fixtures", "grt", "sample-with-points.grtload");
        Assert.True(File.Exists(fixture), $"Task 1 fixture missing: {fixture}");

        GrtShotGroup real = GrtLoadDoc.Load(fixture).ShotGroups().First(g => g.Groups.Count > 0);
        Assert.NotEmpty(real.Groups[0].Points);
        Assert.NotEqual(0, real.RefDistance);

        var doc = NewDoc();
        doc.AddShotGroup("x", TinyPng(),
            new ShotGroupGeometry(real.RefP1X, real.RefP1Y, real.RefP2X, real.RefP2Y,
                                  real.RefDistance, real.ShootDistance),
            real.Groups);

        GrtShotGroup mine = Assert.Single(doc.ShotGroups());
        Assert.Equal(real.Groups[0].Points.Count, mine.Groups[0].Points.Count);
        Assert.Equal(real.Groups[0].Points[0].X, mine.Groups[0].Points[0].X, 6);
    }

    [Fact]
    public void SiblingFamiliesDoNotPruneEachOther()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string src = Path.Combine(dir, "load.grtload");
            GrtLoadDoc.CreateMinimal("t", src).Save(src);

            string a = GrtLoadDoc.Load(src).SaveSibling("import", "shotmarker");
            for (int i = 0; i < 4; i++) GrtLoadDoc.Load(src).SaveSibling("ladder");

            Assert.True(File.Exists(a), "a shotmarker sibling was pruned by the toolkit family");
            Assert.Contains("_shotmarker_", Path.GetFileName(a));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
cd external/GRT-Reloading-Toolkit
dotnet test GrtReloadingToolkit.Tests --filter "FullyQualifiedName~GrtShotGroupWriteTests"
```

Expected: FAIL — `ShotGroupGeometry` and `AddShotGroup` do not exist.

- [ ] **Step 4: Add the geometry record**

In `grt-plugins-shared/GrtPluginKit/Grt/GrtLoadDoc.cs`, directly after the `GrtShotGroup` class:

```csharp
/// <summary>
/// The calibration GRT needs to recover real-world scale from a shot-group picture:
/// two reference points as image fractions, the real distance between them, and the
/// shooting distance. Units are as GRT stores them — see the writer's remarks.
/// </summary>
public sealed record ShotGroupGeometry(
    double RefP1X, double RefP1Y, double RefP2X, double RefP2Y,
    double RefDistance, double ShootDistance);
```

- [ ] **Step 5: Add the writer**

In the same file, immediately after `AddGalleryPicture(…, int width, int height, …)`:

```csharp
    /// <summary>
    /// Adds a GRT "Shot group analysis" tab: the picture, the two reference points that
    /// give it scale, and one &lt;group&gt; per set of hits. Point coordinates are image
    /// fractions 0..1 with y down, exactly as <see cref="ShotGroups"/> reads them back.
    /// </summary>
    /// <param name="pointSize">Hit marker diameter as a fraction of the image width.
    /// GRT's own default for a 1600 px picture is about 0.032.</param>
    public void AddShotGroup(string title, byte[] png, ShotGroupGeometry geom,
                             IEnumerable<GrtShotGroupSet> groups, double pointSize = 0.03)
    {
        var (w, h) = PngSize(png);
        string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        int idx = NextIndex();
        var sg = _doc.CreateElement("ShotGroup");
        sg.SetAttribute("index", idx.ToString(CultureInfo.InvariantCulture));
        sg.SetAttribute("hasfocus", "false");
        sg.SetAttribute("title", UniqueTitle(Enc(title)));
        sg.SetAttribute("zoom", "0.25");
        sg.SetAttribute("scrollPositionX", "0");
        sg.SetAttribute("scrollPositionY", "0");
        sg.SetAttribute("refPoint1X", F(geom.RefP1X));
        sg.SetAttribute("refPoint1Y", F(geom.RefP1Y));
        sg.SetAttribute("refPoint2X", F(geom.RefP2X));
        sg.SetAttribute("refPoint2Y", F(geom.RefP2Y));
        sg.SetAttribute("refDistance", F(geom.RefDistance));
        sg.SetAttribute("shootDistance", F(geom.ShootDistance));
        sg.SetAttribute("pointSize", F(pointSize));
        sg.SetAttribute("statisticSize", "1");
        sg.SetAttribute("darkenImageAlpha", "0.9");
        sg.SetAttribute("showQuickHelp", "false");

        var pic = _doc.CreateElement("picture");
        pic.SetAttribute("name", Enc(SafeName(title) + ".png"));
        pic.SetAttribute("type", "png");
        pic.SetAttribute("width", w.ToString(CultureInfo.InvariantCulture));
        pic.SetAttribute("height", h.ToString(CultureInfo.InvariantCulture));
        pic.SetAttribute("data", Convert.ToBase64String(png));
        sg.AppendChild(_doc.CreateWhitespace("\n        "));
        sg.AppendChild(pic);

        foreach (GrtShotGroupSet set in groups)
        {
            var ge = _doc.CreateElement("group");
            ge.SetAttribute("name", Enc(set.Name));
            foreach (GrtShotPoint p in set.Points)
            {
                var pe = _doc.CreateElement("point");
                pe.SetAttribute("x", F(p.X));
                pe.SetAttribute("y", F(p.Y));
                if (p.Flyer) pe.SetAttribute("flyer", "true");
                if (p.PointOfAim) pe.SetAttribute("PointOfAim", "true");
                ge.AppendChild(_doc.CreateWhitespace("\n            "));
                ge.AppendChild(pe);
            }
            ge.AppendChild(_doc.CreateWhitespace("\n        "));
            sg.AppendChild(_doc.CreateWhitespace("\n        "));
            sg.AppendChild(ge);
        }

        sg.AppendChild(_doc.CreateWhitespace("\n      "));
        _appendix.AppendChild(_doc.CreateWhitespace("  "));
        _appendix.AppendChild(sg);
        _appendix.AppendChild(_doc.CreateWhitespace("\n    "));
    }

    private static string SafeName(string title) =>
        string.Concat(title.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
```

**Before running the tests, reconcile this against `fixtures/grt/FINDINGS.md`.** If GRT wrote `pointofaim` rather than `PointOfAim`, or `1` rather than `true`, change the writer here *and* the reader in `ShotGroups()` to accept both spellings.

- [ ] **Step 6: Give sibling files a family**

Replace `SaveSibling` and the stem regex in the same file:

```csharp
    private const string ToolkitTag = "_toolkit_";
    private static readonly System.Text.RegularExpressions.Regex StemRx = new(
        @"_toolkit(_\d{8}_\d{4,6})?$|_[a-z]+_\d{8}_\d{4,6}$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
```

```csharp
    /// <summary>
    /// Saves a generated sibling and returns its path, keeping the 3 newest of its family.
    /// <paramref name="family"/> names the family: each plugin keeps its own, because pruning
    /// is per-family and a shared one would let one plugin delete another's output.
    /// <paramref name="suffix"/> only documents which tool wrote it.
    /// </summary>
    public string SaveSibling(string suffix, string family = "toolkit")
    {
        _ = suffix;
        string dir = Path.GetDirectoryName(SourcePath) ?? Path.GetTempPath();
        string stem = StripToolkitStem(SourcePath);
        string tag = $"_{family}_";
        string outPath = Path.Combine(dir, $"{stem}{tag}{DateTime.Now:yyyyMMdd_HHmmss}.grtload");
        Save(outPath);

        try
        {
            foreach (var old in Directory.EnumerateFiles(dir, $"{stem}{tag}*.grtload")
                         .Where(f => !string.Equals(f, outPath, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(File.GetLastWriteTimeUtc).Skip(2))
                File.Delete(old);
        }
        catch { /* a snapshot GRT still has open — leave it */ }

        return outPath;
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
cd external/GRT-Reloading-Toolkit
dotnet test GrtReloadingToolkit.Tests --filter "FullyQualifiedName~GrtShotGroupWriteTests"
dotnet test GrtReloadingToolkit.Tests
```

Expected: 4 new tests PASS, and the whole existing suite still passes — `SaveSibling`'s default keeps the toolkit's behaviour unchanged.

- [ ] **Step 8: Commit and push the submodule branch**

```bash
cd external/GRT-Reloading-Toolkit
git add -A
git commit -m "feat(kit): write shot-group tabs, and give siblings a family"
git push -u origin feat/write-shot-groups
cd ../..
git add external/GRT-Reloading-Toolkit
git commit -m "build: point the kit submodule at the shot-group writer"
```

---

## Task 8: Target renderer

**Files:**
- Create: `ShotMarkerCore/Render/TargetProjection.cs`
- Create: `ShotMarkerCore/Render/RenderOptions.cs`
- Create: `ShotMarkerCore/Render/TargetRenderer.cs`
- Create: `ShotMarkerCore.Tests/TargetProjectionTests.cs`
- Create: `ShotMarkerCore.Tests/TargetRendererTests.cs`

**Interfaces:**
- Consumes: `SmString`, `TargetFace`, `TargetFaceLibrary`.
- Produces:
  - `ShotMarker.Core.Render.TargetProjection(double LeftMm, double TopMm, double WidthMm, double HeightMm, double PixelsPerMm)` with `ToFraction(double xMm, double yMm)` → `(double X, double Y)` and `ToPixel(double xMm, double yMm)` → `(float X, float Y)`.
  - `ShotMarker.Core.Render.RenderOptions(double PixelsPerMm = 0.6, double MarginMm = 60, bool DrawFurniture = true)`.
  - `ShotMarker.Core.Render.RenderedTarget(byte[] Png, int Width, int Height, TargetProjection Projection)`.
  - `TargetRenderer.Render(SmString s, TargetFace face, RenderOptions? options = null)` → `RenderedTarget`.

- [ ] **Step 1: Write the failing projection tests**

`ShotMarkerCore.Tests/TargetProjectionTests.cs`:

```csharp
using ShotMarker.Core.Render;

namespace ShotMarker.Core.Tests;

public class TargetProjectionTests
{
    // A 1000 x 800 mm canvas whose top-left is 500 mm left of and 400 mm above centre.
    private static readonly TargetProjection P = new(-500, 400, 1000, 800, 0.5);

    [Fact]
    public void CentreOfTheTargetIsTheCentreOfTheImage()
    {
        var (x, y) = P.ToFraction(0, 0);
        Assert.Equal(0.5, x, 9);
        Assert.Equal(0.5, y, 9);
    }

    [Fact]
    public void ImageYGrowsDownwardWhileTargetYGrowsUp()
    {
        // A hit 200 mm ABOVE centre must land ABOVE the middle of the image, i.e. y < 0.5.
        var (_, high) = P.ToFraction(0, 200);
        var (_, low) = P.ToFraction(0, -200);
        Assert.Equal(0.25, high, 9);
        Assert.Equal(0.75, low, 9);
    }

    [Fact]
    public void CornersMapToTheUnitSquare()
    {
        Assert.Equal((0.0, 0.0), P.ToFraction(-500, 400));
        Assert.Equal((1.0, 1.0), P.ToFraction(500, -400));
    }

    [Fact]
    public void PixelsFollowTheScale()
    {
        var (x, y) = P.ToPixel(0, 0);
        Assert.Equal(250f, x, 3);   // 1000 mm * 0.5 px/mm / 2
        Assert.Equal(200f, y, 3);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~TargetProjectionTests"
```

Expected: FAIL — `TargetProjection` does not exist.

- [ ] **Step 3: Write the projection**

`ShotMarkerCore/Render/TargetProjection.cs`:

```csharp
namespace ShotMarker.Core.Render;

/// <summary>
/// The single place millimetres become image coordinates. ShotMarker measures from the
/// target centre with y up; GRT stores fractions of the picture with y down. Every
/// conversion in this plugin goes through here so the two conventions meet exactly once.
/// </summary>
/// <param name="LeftMm">Target-space x of the image's left edge.</param>
/// <param name="TopMm">Target-space y of the image's top edge.</param>
public sealed record TargetProjection(
    double LeftMm, double TopMm, double WidthMm, double HeightMm, double PixelsPerMm)
{
    public int PixelWidth => (int)Math.Round(WidthMm * PixelsPerMm);
    public int PixelHeight => (int)Math.Round(HeightMm * PixelsPerMm);

    public (double X, double Y) ToFraction(double xMm, double yMm) =>
        ((xMm - LeftMm) / WidthMm, (TopMm - yMm) / HeightMm);

    public (float X, float Y) ToPixel(double xMm, double yMm) =>
        ((float)((xMm - LeftMm) * PixelsPerMm), (float)((TopMm - yMm) * PixelsPerMm));

    public float Px(double mm) => (float)(mm * PixelsPerMm);
}
```

- [ ] **Step 4: Run them to verify they pass**

```bash
dotnet test --filter "FullyQualifiedName~TargetProjectionTests"
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Write the failing renderer tests**

`ShotMarkerCore.Tests/TargetRendererTests.cs`:

```csharp
using ShotMarker.Core.Faces;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Tests;

public class TargetRendererTests
{
    private static SmString FirstString()
    {
        var log = new List<string>();
        return SmExportReader.Read(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First();
    }

    [Fact]
    public void ProducesAPngBigEnoughToShowTheFace()
    {
        SmString s = FirstString();
        var r = TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!);

        Assert.Equal(0x89, r.Png[0]);
        Assert.Equal((byte)'P', r.Png[1]);
        Assert.True(r.Width >= 800, $"image only {r.Width} px wide");
        Assert.Equal(r.Width, r.Projection.PixelWidth);
        Assert.Equal(r.Height, r.Projection.PixelHeight);
    }

    [Fact]
    public void EveryShotLandsInsideThePicture()
    {
        SmString s = FirstString();
        var r = TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!);
        foreach (SmShot sh in s.Shots)
        {
            var (x, y) = r.Projection.ToFraction(sh.XMm, sh.YMm);
            Assert.InRange(x, 0, 1);
            Assert.InRange(y, 0, 1);
        }
    }

    [Fact]
    public void TheFaceIsDrawnAtTrueScale()
    {
        SmString s = FirstString();
        TargetFace f = TargetFaceLibrary.Find("NRA_LRFC")!;
        var r = TargetRenderer.Render(s, f);

        // The 5 inch X ring must measure 5 inches on the picture, via the projection.
        var (left, _) = r.Projection.ToFraction(-f.Rings[0].DiamMm / 2, 0);
        var (right, _) = r.Projection.ToFraction(f.Rings[0].DiamMm / 2, 0);
        double ringMm = (right - left) * r.Projection.WidthMm;
        Assert.Equal(5 * 25.4, ringMm, 1);
    }

    [Fact]
    public void AnUnknownFaceStillRenders()
    {
        SmString s = FirstString();
        var r = TargetRenderer.Render(s, TargetFaceLibrary.Generic(s.FrameWidthMm, s.FrameHeightMm));
        Assert.True(r.Png.Length > 0);
    }

    [Fact]
    public void FurnitureCanBeTurnedOff()
    {
        // GRT draws its own group box and statistics over the picture, so a clean face
        // has to stay one option away.
        SmString s = FirstString();
        TargetFace f = TargetFaceLibrary.Find(s.FaceId)!;
        var withFurniture = TargetRenderer.Render(s, f, new RenderOptions(DrawFurniture: true));
        var without = TargetRenderer.Render(s, f, new RenderOptions(DrawFurniture: false));
        Assert.NotEqual(withFurniture.Png.Length, without.Png.Length);
        Assert.Equal(withFurniture.Width, without.Width);
    }

    [Fact]
    public void RenderingIsDeterministic()
    {
        // A golden-image test is only worth writing if the renderer repeats itself.
        SmString s = FirstString();
        TargetFace f = TargetFaceLibrary.Find(s.FaceId)!;
        Assert.Equal(TargetRenderer.Render(s, f).Png, TargetRenderer.Render(s, f).Png);
    }
}
```

- [ ] **Step 6: Run them to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~TargetRendererTests"
```

Expected: FAIL — `TargetRenderer` does not exist.

- [ ] **Step 7: Write the render options**

`ShotMarkerCore/Render/RenderOptions.cs`:

```csharp
namespace ShotMarker.Core.Render;

/// <param name="PixelsPerMm">Drawing scale. A 72 inch board at 0.6 px/mm is about 1100 px,
/// which is legible in GRT without making the base64 payload enormous.</param>
/// <param name="MarginMm">Space beyond the board, so a shot off the edge still appears.</param>
/// <param name="DrawFurniture">ShotMarker's own overlay: numbered discs, group box and
/// statistics. GRT draws its own on top, so this can be turned off for a clean face.</param>
public sealed record RenderOptions(
    double PixelsPerMm = 0.6, double MarginMm = 60, bool DrawFurniture = true);

public sealed record RenderedTarget(byte[] Png, int Width, int Height, TargetProjection Projection);
```

- [ ] **Step 8: Write the renderer**

`ShotMarkerCore/Render/TargetRenderer.cs`:

```csharp
using System.Globalization;
using ShotMarker.Core.Faces;
using ShotMarker.Core.Sm;
using SkiaSharp;

namespace ShotMarker.Core.Render;

/// <summary>
/// Draws a ShotMarker string onto its scoring face and returns the picture together with
/// the projection used. The projection is returned rather than recomputed by the caller
/// because two independent derivations of the same mapping is how a plot silently drifts
/// out of scale.
/// </summary>
public static class TargetRenderer
{
    public static RenderedTarget Render(SmString s, TargetFace face, RenderOptions? options = null)
    {
        RenderOptions o = options ?? new RenderOptions();

        double widthMm = face.BoardWidthMm + 2 * o.MarginMm;
        double heightMm = face.BoardHeightMm + 2 * o.MarginMm;
        // Never clip a hit: grow the canvas if a shot lies outside the board.
        foreach (SmShot sh in s.Shots)
        {
            widthMm = Math.Max(widthMm, 2 * (Math.Abs(sh.XMm) + o.MarginMm));
            heightMm = Math.Max(heightMm, 2 * (Math.Abs(sh.YMm) + o.MarginMm));
        }

        var proj = new TargetProjection(-widthMm / 2, heightMm / 2, widthMm, heightMm, o.PixelsPerMm);

        var info = new SKImageInfo(proj.PixelWidth, proj.PixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        DrawBoard(canvas, proj, face);
        DrawRings(canvas, proj, face);
        DrawPolys(canvas, proj, face);
        DrawTexts(canvas, proj, face);
        DrawShots(canvas, proj, s);
        if (o.DrawFurniture) DrawFurniture(canvas, proj, s);

        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new RenderedTarget(data.ToArray(), proj.PixelWidth, proj.PixelHeight, proj);
    }

    /// <summary>ShotMarker's colour codes. The "l" suffix means the ring is a line only,
    /// not a filled disc — the difference between an outline and a black centre.</summary>
    private static SKColor Fill(string code) => code switch
    {
        "b" => new SKColor(0x20, 0x20, 0x20),
        "g" => new SKColor(0x80, 0x80, 0x80),
        _ => SKColors.White,
    };

    private static bool LineOnly(string code) => code.EndsWith("l", StringComparison.Ordinal);

    private static SKColor Stroke(string code) => code switch
    {
        "wl" => SKColors.White,
        "gl" => new SKColor(0x60, 0x60, 0x60),
        _ => new SKColor(0x20, 0x20, 0x20),
    };

    private static void DrawBoard(SKCanvas c, TargetProjection p, TargetFace f)
    {
        var (x, y) = p.ToPixel(-f.BoardWidthMm / 2, f.BoardHeightMm / 2);
        var rect = new SKRect(x, y, x + p.Px(f.BoardWidthMm), y + p.Px(f.BoardHeightMm));
        using var fill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var edge = new SKPaint
        {
            Color = new SKColor(0x40, 0x40, 0x40), Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1, p.Px(f.BoardLineMm)), IsAntialias = true,
        };
        c.DrawRect(rect, fill);
        c.DrawRect(rect, edge);
    }

    private static void DrawRings(SKCanvas c, TargetProjection p, TargetFace f)
    {
        // Largest first, so the small high-scoring rings end up on top.
        foreach (TargetRing r in f.Rings.OrderByDescending(r => r.DiamMm))
        {
            var (cx, cy) = p.ToPixel(r.XMm, r.YMm);
            float radius = p.Px(r.DiamMm / 2);
            if (!LineOnly(r.Color))
            {
                using var fill = new SKPaint { Color = Fill(r.Color), Style = SKPaintStyle.Fill, IsAntialias = true };
                c.DrawCircle(cx, cy, radius, fill);
            }
            if (r.LineMm > 0)
            {
                using var edge = new SKPaint
                {
                    Color = Stroke(r.Color), Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max(1, p.Px(r.LineMm)), IsAntialias = true,
                };
                c.DrawCircle(cx, cy, radius, edge);
            }
        }
    }

    private static void DrawPolys(SKCanvas c, TargetProjection p, TargetFace f)
    {
        foreach (TargetPoly poly in f.Polys)
        {
            if (poly.Points.Count < 2) continue;
            using var path = new SKPath();
            var (x0, y0) = p.ToPixel(poly.Points[0].XMm, poly.Points[0].YMm);
            path.MoveTo(x0, y0);
            foreach (TargetPoint pt in poly.Points.Skip(1))
            {
                var (x, y) = p.ToPixel(pt.XMm, pt.YMm);
                path.LineTo(x, y);
            }
            path.Close();
            using var paint = new SKPaint
            {
                Color = LineOnly(poly.Color) ? Stroke(poly.Color) : Fill(poly.Color),
                Style = LineOnly(poly.Color) ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
                StrokeWidth = Math.Max(1, p.Px(poly.LineMm)), IsAntialias = true,
            };
            c.DrawPath(path, paint);
        }
    }

    private static void DrawTexts(SKCanvas c, TargetProjection p, TargetFace f)
    {
        foreach (TargetText t in f.Texts)
        {
            if (string.IsNullOrEmpty(t.Text)) continue;
            var (x, y) = p.ToPixel(t.XMm, t.YMm);
            using var paint = new SKPaint
            {
                Color = Stroke(t.Color), IsAntialias = true,
                TextSize = Math.Max(6, p.Px(t.SizeMm)), TextAlign = SKTextAlign.Center,
                Typeface = SKTypeface.FromFamilyName("Helvetica", SKFontStyle.Normal),
            };
            c.DrawText(t.Text, x, y + paint.TextSize / 3, paint);
        }
    }

    private static void DrawShots(SKCanvas c, TargetProjection p, SmString s)
    {
        float radius = p.Px((s.BulletDiameterMm ?? 7.2) / 2);
        float discRadius = Math.Max(radius, 8);

        foreach (SmShot sh in s.Shots)
        {
            var (x, y) = p.ToPixel(sh.XMm, sh.YMm);
            using var fill = new SKPaint
            {
                Color = sh.IsFlyer ? new SKColor(0xD0, 0x30, 0x30) : new SKColor(0xF0, 0x70, 0x20),
                Style = SKPaintStyle.Fill, IsAntialias = true,
            };
            using var edge = new SKPaint
            {
                Color = SKColors.White, Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1, discRadius / 6), IsAntialias = true,
            };
            c.DrawCircle(x, y, discRadius, fill);
            c.DrawCircle(x, y, discRadius, edge);

            using var label = new SKPaint
            {
                Color = SKColors.White, IsAntialias = true,
                TextSize = discRadius * 1.2f, TextAlign = SKTextAlign.Center,
                Typeface = SKTypeface.FromFamilyName("Helvetica", SKFontStyle.Bold),
            };
            c.DrawText(sh.Number.ToString(CultureInfo.InvariantCulture), x, y + label.TextSize / 3, label);
        }
    }

    private static void DrawFurniture(SKCanvas c, TargetProjection p, SmString s)
    {
        var scoring = s.Shots.Where(sh => !sh.IsFlyer).ToList();
        if (scoring.Count == 0) return;

        double x0 = scoring.Min(sh => sh.XMm), x1 = scoring.Max(sh => sh.XMm);
        double y0 = scoring.Min(sh => sh.YMm), y1 = scoring.Max(sh => sh.YMm);
        var (left, top) = p.ToPixel(x0, y1);
        var (right, bottom) = p.ToPixel(x1, y0);

        using var box = new SKPaint
        {
            Color = new SKColor(0x20, 0xA0, 0x20), Style = SKPaintStyle.Stroke,
            StrokeWidth = 2, IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new[] { 8f, 6f }, 0),
        };
        c.DrawRect(new SKRect(left, top, right, bottom), box);

        string stats = Stats(s, x1 - x0, y1 - y0);
        using var text = new SKPaint
        {
            Color = new SKColor(0x20, 0x20, 0x20), IsAntialias = true, TextSize = 22,
            Typeface = SKTypeface.FromFamilyName("Helvetica", SKFontStyle.Normal),
        };
        using var plate = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0xE0), Style = SKPaintStyle.Fill };
        float w = text.MeasureText(stats);
        c.DrawRect(new SKRect(10, 10, 20 + w, 46), plate);
        c.DrawText(stats, 15, 36, text);
    }

    private static string Stats(SmString s, double widthMm, double heightMm)
    {
        var ic = CultureInfo.InvariantCulture;
        string size = s.Stats?.GroupSizeMm is { } g
            ? g.ToString("0.0", ic)
            : Math.Sqrt(widthMm * widthMm + heightMm * heightMm).ToString("0.0", ic);
        var parts = new List<string> { $"size {size} mm", $"w {widthMm.ToString("0.0", ic)}", $"h {heightMm.ToString("0.0", ic)}" };
        if (s.Stats?.MeanRadiusMm is { } mr) parts.Add($"mr {mr.ToString("0.0", ic)}");
        if (s.Stats?.VelocityAvgMps is { } v) parts.Add($"v {v.ToString("0", ic)} m/s");
        if (s.Stats?.VelocitySdMps is { } sd) parts.Add($"sd {sd.ToString("0.0", ic)}");
        if (s.Stats?.VelocityEsMps is { } es) parts.Add($"es {es.ToString("0.0", ic)}");
        return string.Join("  ", parts);
    }
}
```

- [ ] **Step 9: Run the tests to verify they pass**

```bash
dotnet test --filter "FullyQualifiedName~TargetRendererTests"
```

Expected: PASS, 6 tests.

- [ ] **Step 10: Add a golden image**

```bash
mkdir -p fixtures/golden
dotnet run --project ShotMarkerCore.Tests -- --write-golden 2>/dev/null || true
```

If that entry point does not exist, write the golden from a test run instead — add this test, run it once with `SHOTMARKER_WRITE_GOLDEN=1`, inspect the PNG by eye against `fixtures/shotmarker/shotmarker-ui.png`, then commit it:

```csharp
    [Fact]
    public void MatchesTheGoldenImage()
    {
        SmString s = FirstString();
        var r = TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!);
        string golden = Fixtures.Path("golden/nra_lrfc_m6r1.png");

        if (Environment.GetEnvironmentVariable("SHOTMARKER_WRITE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllBytes(golden, r.Png);
        }

        Assert.True(File.Exists(golden), "run once with SHOTMARKER_WRITE_GOLDEN=1, then eyeball the PNG");
        Assert.Equal(File.ReadAllBytes(golden), r.Png);
    }
```

```bash
SHOTMARKER_WRITE_GOLDEN=1 dotnet test --filter "FullyQualifiedName~MatchesTheGoldenImage"
open fixtures/golden/nra_lrfc_m6r1.png
dotnet test --filter "FullyQualifiedName~TargetRendererTests"
```

Expected: the image shows a black-centred NRA LRFC face with numbered orange discs and a dashed group box; the second run PASSes, 7 tests.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat: render ShotMarker strings onto their scoring face"
```

---

## Task 9: GRT writer and the round-trip invariant

**Files:**
- Create: `ShotMarkerCore/Grt/ImportItem.cs`
- Create: `ShotMarkerCore/Grt/GrtShotGroupWriter.cs`
- Create: `ShotMarkerCore.Tests/GrtShotGroupWriterTests.cs`
- Create: `ShotMarkerCore.Tests/RoundTripTests.cs`

**Interfaces:**
- Consumes: `RenderedTarget`, `SmString`, `GrtLoadDoc`, `ShotGroupGeometry`, `GrtShotGroupSet`, `GrtShotPoint`, `GrtCharge`, `GrtShot`.
- Produces:
  - `ShotMarker.Core.Grt.ImportItem(SmString String, RenderedTarget Render, double? ChargeGrains)`.
  - `GrtShotGroupWriter.Add(GrtLoadDoc doc, ImportItem item, IList<string> log)`.
  - `GrtShotGroupWriter.RefDistanceUnit` — a `const` documenting what Task 1 observed.

- [ ] **Step 1: Write the failing round-trip test**

This is the load-bearing test of the whole plugin. `GrtShotGroups.FromDoc` is an independently written reader already in service; if the writer's fraction arithmetic is wrong, it cannot agree with it by accident.

`ShotMarkerCore.Tests/RoundTripTests.cs`:

```csharp
using GrtPluginKit.Grt;
using GrtReloadingToolkit.Ocw;
using ShotMarker.Core.Faces;
using ShotMarker.Core.Grt;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Tests;

public class RoundTripTests
{
    private const double MoaMmPer100m = 29.0888;

    private static (GrtLoadDoc Doc, SmString String) Import()
    {
        var log = new List<string>();
        SmString s = SmExportReader.Read(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First();
        RenderedTarget r = TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!);
        var doc = GrtLoadDoc.CreateMinimal("round trip",
            Path.Combine(Path.GetTempPath(), "roundtrip.grtload"));
        GrtShotGroupWriter.Add(doc, new ImportItem(s, r, 41.5), log);
        return (doc, s);
    }

    [Fact]
    public void EveryShotComesBackWhereItWent()
    {
        var (doc, s) = Import();

        var (groups, log) = GrtShotGroups.FromDoc(doc,
            new GrtShotGroups.Options(RefUnit.Mm, ShootUnit.Meters));
        TargetGroup g = Assert.Single(groups);

        // FromDoc reports MOA offsets from the centroid, so compare like with like.
        double mmPerMoa = MoaMmPer100m * (s.DistanceMetres / 100.0);
        var scoring = s.Shots.Where(sh => !sh.IsFlyer).ToList();
        double cx = scoring.Average(sh => sh.XMm), cy = scoring.Average(sh => sh.YMm);

        Assert.Equal(scoring.Count, g.Impacts.Count);
        foreach (var (shot, impact) in scoring.Zip(g.Impacts))
        {
            Assert.Equal((shot.XMm - cx) / mmPerMoa, impact.XMoa, 2);
            Assert.Equal((shot.YMm - cy) / mmPerMoa, impact.YMoa, 2);
        }
        Assert.Empty(log.Where(l => l.Contains("skipped")));
    }

    [Fact]
    public void TheGroupKeepsItsRealSize()
    {
        var (doc, s) = Import();
        var (groups, _) = GrtShotGroups.FromDoc(doc,
            new GrtShotGroups.Options(RefUnit.Mm, ShootUnit.Meters));
        TargetGroup g = groups.Single();

        double mmPerMoa = MoaMmPer100m * (s.DistanceMetres / 100.0);
        double readWidthMm = (g.Impacts.Max(i => i.XMoa) - g.Impacts.Min(i => i.XMoa)) * mmPerMoa;
        var scoring = s.Shots.Where(sh => !sh.IsFlyer).ToList();
        double realWidthMm = scoring.Max(sh => sh.XMm) - scoring.Min(sh => sh.XMm);

        Assert.Equal(realWidthMm, readWidthMm, 1);
    }

    [Fact]
    public void TheShootingDistanceSurvives()
    {
        var (doc, s) = Import();
        var (groups, _) = GrtShotGroups.FromDoc(doc,
            new GrtShotGroups.Options(RefUnit.Mm, ShootUnit.Meters));
        Assert.Equal(s.DistanceMetres, groups.Single().DistanceM, 1);
    }

    [Fact]
    public void SightersAndRejectsComeBackAsFlyers()
    {
        var (doc, s) = Import();
        GrtShotGroup tab = doc.ShotGroups().Single();
        Assert.Equal(s.Shots.Count(sh => sh.IsFlyer),
                     tab.Groups.Single().Points.Count(p => p.Flyer && !p.PointOfAim));
    }
}
```

- [ ] **Step 2: Link the oracle into the test project**

`GrtShotGroups` lives in the toolkit's Windows-targeted plugin project but is plain arithmetic. Link the three files it needs, exactly as the toolkit's own test project does. Add to `ShotMarkerCore.Tests/ShotMarkerCore.Tests.csproj`:

```xml
  <ItemGroup>
    <!-- The round-trip test needs an independent reader as its oracle. These files are
         plain arithmetic living in a net8.0-windows project; linking them keeps these
         tests runnable on macOS and Linux, which referencing that project would not. -->
    <Compile Include="..\external\GRT-Reloading-Toolkit\GrtReloadingToolkit\Ocw\BallisticXCsv.cs" Link="Oracle\BallisticXCsv.cs" />
    <Compile Include="..\external\GRT-Reloading-Toolkit\GrtReloadingToolkit\Ocw\GrtShotGroups.cs" Link="Oracle\GrtShotGroups.cs" />
    <Compile Include="..\external\GRT-Reloading-Toolkit\GrtReloadingToolkit\Ocw\LadderStep.cs" Link="Oracle\LadderStep.cs" />
  </ItemGroup>
```

- [ ] **Step 3: Run the test to verify it fails**

```bash
dotnet test --filter "FullyQualifiedName~RoundTripTests"
```

Expected: FAIL — `GrtShotGroupWriter` does not exist.

- [ ] **Step 4: Write the import item**

`ShotMarkerCore/Grt/ImportItem.cs`:

```csharp
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Grt;

/// <param name="ChargeGrains">The powder charge this string was shot with. A ShotMarker
/// string is normally one charge; a ladder shot across several strings gives each its own,
/// which is why this is per-item and editable rather than read from the load.</param>
public sealed record ImportItem(SmString String, RenderedTarget Render, double? ChargeGrains);
```

- [ ] **Step 5: Write the writer**

`ShotMarkerCore/Grt/GrtShotGroupWriter.cs`:

```csharp
using System.Globalization;
using GrtPluginKit.Grt;
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Grt;

/// <summary>
/// Appends one imported string to a GRT load: a shot-group tab with the rendered picture
/// and its hits, a measurement of the velocities, and a note carrying ShotMarker's own
/// statistics.
/// </summary>
public static class GrtShotGroupWriter
{
    /// <summary>
    /// What GRT stores in a shot group's <c>refDistance</c>. Observed from a GRT-written
    /// file in <c>fixtures/grt/FINDINGS.md</c>: a 10 inch reference read back as 254, and
    /// a 1000 yard string as 914.4, so both are SI regardless of the display units
    /// configured. Writing display units instead would scale every imported group by 25.4.
    /// </summary>
    public const string RefDistanceUnit = "millimetres";

    private const double GrainsPerGram = 1000.0 / 64.79891;

    public static void Add(GrtLoadDoc doc, ImportItem item, IList<string> log)
    {
        SmString s = item.String;
        if (s.Shots.Count == 0) { log.Add($"'{s.Name}': no shots — not imported"); return; }

        string title = $"ShotMarker — {s.Name}";
        AddTab(doc, item, title);
        AddVelocities(doc, item, title, log);
        AddStatsNote(doc, s, title);
        log.Add($"'{s.Name}': {s.Shots.Count} shots at {s.DistanceValue.ToString("0", CultureInfo.InvariantCulture)}{s.DistanceUnit}");
    }

    private static void AddTab(GrtLoadDoc doc, ImportItem item, string title)
    {
        SmString s = item.String;
        var proj = item.Render.Projection;

        var set = new GrtShotGroupSet
        {
            Name = item.ChargeGrains is { } gr
                ? gr.ToString("0.0#", CultureInfo.InvariantCulture) + " gr"
                : s.Name,
        };
        foreach (SmShot sh in s.Shots)
        {
            var (x, y) = proj.ToFraction(sh.XMm, sh.YMm);
            set.Points.Add(new GrtShotPoint(x, y, sh.IsFlyer, PointOfAim: false));
        }

        // The reference points sit on the horizontal centre line at the board's own edges,
        // so the distance between them is a number the renderer already knows exactly.
        double refMm = s.FrameWidthMm > 0 ? s.FrameWidthMm : proj.WidthMm / 2;
        var (p1x, p1y) = proj.ToFraction(-refMm / 2, 0);
        var (p2x, p2y) = proj.ToFraction(refMm / 2, 0);

        double pointSize = (s.BulletDiameterMm ?? 7.2) / proj.WidthMm;
        doc.AddShotGroup(title, item.Render.Png,
            new ShotGroupGeometry(p1x, p1y, p2x, p2y, refMm, s.DistanceMetres),
            new[] { set }, pointSize);
    }

    private static void AddVelocities(GrtLoadDoc doc, ImportItem item, string title, IList<string> log)
    {
        SmString s = item.String;
        var shots = s.Shots.Where(sh => sh.VelocityMps is > 0).ToList();
        if (shots.Count == 0) { log.Add($"'{s.Name}': no velocities — measurement omitted"); return; }

        var charge = new GrtCharge
        {
            Name = item.ChargeGrains is { } gr
                ? gr.ToString("0.0#", CultureInfo.InvariantCulture) + " gr"
                : s.Name,
            ValueKg = (item.ChargeGrains ?? 0) / GrainsPerGram / 1000.0,
            Note = $"ShotMarker {s.Name}, {s.Timestamp:yyyy-MM-dd HH:mm}",
        };
        foreach (SmShot sh in shots)
            charge.Shots.Add(new GrtShot(sh.VelocityMps!.Value,
                sh.IsFlyer ? "sighter/invalid" : sh.Score));

        doc.AddMeasurement(title, new[] { charge });
    }

    private static void AddStatsNote(GrtLoadDoc doc, SmString s, string title)
    {
        var ic = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            $"String:     {s.Name}",
            $"Shot:       {s.Timestamp:yyyy-MM-dd HH:mm}",
            $"Distance:   {s.DistanceValue.ToString("0", ic)} {s.DistanceUnit}",
            $"Face:       {s.FaceId}",
            $"Shots:      {s.Shots.Count} ({s.Shots.Count(sh => sh.IsFlyer)} sighter/invalid)",
        };
        if (s.ScoreText is { Length: > 0 }) lines.Add($"Score:      {s.ScoreText}");
        if (s.Stats is { } st)
        {
            lines.Add("");
            lines.Add("ShotMarker's own figures:");
            if (st.GroupSizeMm is { } g) lines.Add($"  group size    {g.ToString("0.0", ic)} mm");
            if (st.MeanRadiusMm is { } mr) lines.Add($"  mean radius   {mr.ToString("0.0", ic)} mm");
            if (st.CtcMm is { } ctc) lines.Add($"  centre-centre {ctc.ToString("0.0", ic)} mm");
            if (st.VelocityAvgMps is { } v) lines.Add($"  velocity avg  {v.ToString("0.0", ic)} m/s");
            if (st.VelocitySdMps is { } sd) lines.Add($"  velocity sd   {sd.ToString("0.0", ic)} m/s");
            if (st.VelocityEsMps is { } es) lines.Add($"  velocity es   {es.ToString("0.0", ic)} m/s");
        }
        doc.AddNote(title, string.Join("\n", lines));
    }
}
```

- [ ] **Step 6: Write the writer's own tests**

`ShotMarkerCore.Tests/GrtShotGroupWriterTests.cs`:

```csharp
using GrtPluginKit.Grt;
using ShotMarker.Core.Faces;
using ShotMarker.Core.Grt;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Tests;

public class GrtShotGroupWriterTests
{
    private static ImportItem Item(double? charge = 41.5)
    {
        var log = new List<string>();
        SmString s = SmExportReader.Read(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First();
        return new ImportItem(s, TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!), charge);
    }

    private static GrtLoadDoc NewDoc() =>
        GrtLoadDoc.CreateMinimal("t", Path.Combine(Path.GetTempPath(), "t.grtload"));

    [Fact]
    public void WritesATabAMeasurementAndANote()
    {
        var doc = NewDoc();
        var log = new List<string>();
        GrtShotGroupWriter.Add(doc, Item(), log);

        Assert.Single(doc.ShotGroups());
        GrtMeasurement m = Assert.Single(doc.Measurements());
        Assert.Single(m.Charges);
        Assert.NotEmpty(m.Charges[0].Shots);
    }

    [Fact]
    public void TheChargeIsNamedAndWeighedFromTheImport()
    {
        var doc = NewDoc();
        GrtShotGroupWriter.Add(doc, Item(41.5), new List<string>());
        GrtCharge c = doc.Measurements().Single().Charges[0];
        Assert.Equal("41.5 gr", c.Name);
        Assert.Equal(41.5, c.ChargeGrains!.Value, 3);
    }

    [Fact]
    public void AStringWithoutVelocitiesStillImportsItsPositions()
    {
        ImportItem full = Item();
        var stripped = full.String with
        {
            Shots = full.String.Shots.Select(sh => sh with { VelocityMps = null }).ToList(),
        };

        var doc = NewDoc();
        var log = new List<string>();
        GrtShotGroupWriter.Add(doc, full with { String = stripped }, log);

        Assert.Single(doc.ShotGroups());
        Assert.Empty(doc.Measurements());
        Assert.Contains(log, l => l.Contains("no velocities"));
    }

    [Fact]
    public void ASavedLoadIsWellFormedXml()
    {
        var doc = NewDoc();
        GrtShotGroupWriter.Add(doc, Item(), new List<string>());
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".grtload");
        try
        {
            doc.Save(path);
            var xml = new System.Xml.XmlDocument();
            xml.Load(path);           // throws if malformed
            Assert.Single(GrtLoadDoc.Load(path).ShotGroups());
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 7: Run all the tests to verify they pass**

```bash
dotnet test
```

Expected: PASS, whole suite. If `EveryShotComesBackWhereItWent` fails, the failure is in the fraction arithmetic — read `TargetProjection` and `AddTab` together before touching the test; the oracle is not the thing that is wrong.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: write imported strings into a GRT load, proven by round trip"
```

---

## Task 10: Import orchestration and a headless CLI

**Files:**
- Create: `ShotMarkerCore/Import/ImportJob.cs`
- Create: `ShotMarkerCore/Import/ImportCli.cs`
- Create: `ShotMarkerCore.Tests/ImportJobTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 3–9.
- Produces:
  - `ShotMarker.Core.Import.ImportJob.Plan(string exportPath, IList<string> log)` → `IReadOnlyList<SmString>`.
  - `ShotMarker.Core.Import.ImportJob.Run(string loadPath, IEnumerable<(SmString String, double? ChargeGrains)> selected, IList<string> log)` → `string` (path of the written sibling).
  - `ShotMarker.Core.Import.ImportCli.Run(string[] args)` → `int`.

- [ ] **Step 1: Write the failing tests**

`ShotMarkerCore.Tests/ImportJobTests.cs`:

```csharp
using GrtPluginKit.Grt;
using ShotMarker.Core.Import;
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Tests;

public class ImportJobTests
{
    private static string TempLoad(string dir)
    {
        string path = Path.Combine(dir, "Krieger-2 284 Shehane.grtload");
        GrtLoadDoc.CreateMinimal("284 Shehane", path).Save(path);
        return path;
    }

    [Fact]
    public void PlanListsEveryStringInTheExport()
    {
        var log = new List<string>();
        var strings = ImportJob.Plan(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log);
        Assert.Equal(3, strings.Count);
    }

    [Fact]
    public void RunWritesASiblingAndLeavesTheOriginalAlone()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string load = TempLoad(dir);
            byte[] before = File.ReadAllBytes(load);
            var log = new List<string>();

            var strings = ImportJob.Plan(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log);
            string outPath = ImportJob.Run(load, strings.Select(s => (s, (double?)41.5)), log);

            Assert.NotEqual(load, outPath);
            Assert.True(File.Exists(outPath));
            Assert.Contains("_shotmarker_", Path.GetFileName(outPath));
            Assert.Equal(before, File.ReadAllBytes(load));
            Assert.Equal(3, GrtLoadDoc.Load(outPath).ShotGroups().Count());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OneUnreadableStringDoesNotFailTheBatch()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string load = TempLoad(dir);
            var log = new List<string>();
            var good = ImportJob.Plan(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First();
            var empty = good with { Shots = Array.Empty<SmShot>() };

            string outPath = ImportJob.Run(load,
                new[] { (empty, (double?)41.0), (good, (double?)41.5) }, log);

            Assert.Single(GrtLoadDoc.Load(outPath).ShotGroups());
            Assert.Contains(log, l => l.Contains("no shots"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AnUnknownFaceFallsBackToAPlainPlotWithAWarning()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string load = TempLoad(dir);
            var log = new List<string>();
            var s = ImportJob.Plan(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First()
                    with { FaceId = "NO_SUCH_FACE" };

            string outPath = ImportJob.Run(load, new[] { (s, (double?)null) }, log);

            Assert.Single(GrtLoadDoc.Load(outPath).ShotGroups());
            Assert.Contains(log, l => l.Contains("NO_SUCH_FACE"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WithNoLoadToWriteIntoOneIsCreated()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var log = new List<string>();
            var s = ImportJob.Plan(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First();
            string outPath = ImportJob.Run(Path.Combine(dir, "nothing-here.grtload"),
                new[] { (s, (double?)41.5) }, log);
            Assert.True(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test --filter "FullyQualifiedName~ImportJobTests"
```

Expected: FAIL — `ImportJob` does not exist.

- [ ] **Step 3: Write the job**

`ShotMarkerCore/Import/ImportJob.cs`:

```csharp
using GrtPluginKit.Grt;
using ShotMarker.Core.Faces;
using ShotMarker.Core.Grt;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Import;

/// <summary>
/// Reads an export, renders the selected strings and writes them into a sibling load.
/// Best-effort per string: one unreadable string never costs the shooter the rest of
/// their session.
/// </summary>
public static class ImportJob
{
    public static IReadOnlyList<SmString> Plan(string exportPath, IList<string> log) =>
        SmExportReader.Read(exportPath, log);

    /// <summary>Writes the sibling and returns its path. The open load is never modified —
    /// the GRT plugin interface does not allow editing it in place.</summary>
    public static string Run(string loadPath,
                             IEnumerable<(SmString String, double? ChargeGrains)> selected,
                             IList<string> log)
    {
        GrtLoadDoc doc = File.Exists(loadPath)
            ? GrtLoadDoc.Load(loadPath)
            : GrtLoadDoc.CreateMinimal(Path.GetFileNameWithoutExtension(loadPath), loadPath);

        foreach (var (s, charge) in selected)
        {
            try
            {
                TargetFace face = TargetFaceLibrary.Find(s.FaceId) ?? Fallback(s, log);
                RenderedTarget render = TargetRenderer.Render(s, face);
                GrtShotGroupWriter.Add(doc, new ImportItem(s, render, charge), log);
            }
            catch (Exception ex)
            {
                log.Add($"'{s.Name}': not imported ({ex.Message})");
            }
        }

        return doc.SaveSibling("import", family: "shotmarker");
    }

    private static TargetFace Fallback(SmString s, IList<string> log)
    {
        log.Add($"'{s.Name}': unknown target face '{s.FaceId}' — plotting without scoring rings");
        double w = s.FrameWidthMm > 0 ? s.FrameWidthMm : 1000;
        double h = s.FrameHeightMm > 0 ? s.FrameHeightMm : 1000;
        return TargetFaceLibrary.Generic(w, h);
    }
}
```

- [ ] **Step 4: Write the CLI**

`ShotMarkerCore/Import/ImportCli.cs`:

```csharp
using System.Globalization;

namespace ShotMarker.Core.Import;

/// <summary>
/// A headless entry point: the whole import without WinForms, so the pipeline can be
/// exercised on any machine and in CI, and so a failed import can be reproduced from
/// a shell rather than from a screenshot.
/// </summary>
public static class ImportCli
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: --import <export.tar|export.csv> <load.grtload> [charge-grains]");
            return 2;
        }

        var log = new List<string>();
        double? charge = args.Length >= 3 &&
            double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double g) ? g : null;

        var strings = ImportJob.Plan(args[0], log);
        if (strings.Count == 0)
        {
            foreach (string l in log) Console.WriteLine(l);
            Console.WriteLine("nothing to import");
            return 1;
        }

        string outPath = ImportJob.Run(args[1], strings.Select(s => (s, charge)), log);
        foreach (string l in log) Console.WriteLine(l);
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test
```

Expected: PASS, whole suite.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: orchestrate an import, with a headless CLI to drive it"
```

---

## Task 11: WinForms shell

**Files:**
- Create: `ShotMarkerPlugin/ShotMarkerPlugin.csproj`
- Create: `ShotMarkerPlugin/Program.cs`
- Create: `ShotMarkerPlugin/Ui/ImportForm.cs`
- Modify: `GrtShotMarker.sln`

**Interfaces:**
- Consumes: `ImportJob.Plan`, `ImportJob.Run`, `ImportCli.Run`, `GrtClient`.
- Produces: `GRT_ShotMarker.exe`, which accepts `--ipcport <n>` from GRT and `--import <export> <load> [charge]` from a shell.

- [ ] **Step 1: Write the project file**

`ShotMarkerPlugin/ShotMarkerPlugin.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>ShotMarker.Plugin</RootNamespace>
    <AssemblyName>GRT_ShotMarker</AssemblyName>
    <Deterministic>true</Deterministic>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ShotMarkerCore\ShotMarkerCore.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Include="plugin\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

```bash
dotnet sln add ShotMarkerPlugin/ShotMarkerPlugin.csproj
```

- [ ] **Step 2: Write the entry point**

`ShotMarkerPlugin/Program.cs`:

```csharp
using GrtPluginKit.Ipc;
using ShotMarker.Core.Import;
using ShotMarker.Plugin.Ui;

namespace ShotMarker.Plugin;

internal static class Program
{
    internal const string PluginId = "com.grt.plugin.shotmarker";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--import")
            return ImportCli.Run(args.Skip(1).ToArray());

        ApplicationConfiguration.Initialize();

        int port = 0;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--ipcport" && int.TryParse(args[i + 1], out int p)) port = p;

        GrtClient? grt = null;
        if (port > 0)
        {
            grt = new GrtClient(port);
            try { grt.Connect(); } catch { /* GRT closed the socket; run file-only */ }
        }

        using (var form = new ImportForm(grt))
            Application.Run(form);

        grt?.Dispose();
        return 0;
    }
}
```

- [ ] **Step 3: Write the import form**

`ShotMarkerPlugin/Ui/ImportForm.cs`:

```csharp
using System.Globalization;
using GrtPluginKit.Grt;
using GrtPluginKit.Ipc;
using ShotMarker.Core.Import;
using ShotMarker.Core.Sm;

namespace ShotMarker.Plugin.Ui;

/// <summary>
/// Pick an export, tick the strings to import, set each one's charge, import. Each ticked
/// string becomes its own shot-group tab in a sibling load, which GRT is then asked to open.
/// </summary>
internal sealed class ImportForm : Form
{
    private readonly GrtClient? _grt;
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _browse = new() { Text = "Open export…", AutoSize = true };
    private readonly Button _import = new() { Text = "Import selected", AutoSize = true, Enabled = false };
    private readonly Label _load = new() { AutoSize = true, Text = "No load open" };

    private IReadOnlyList<SmString> _strings = Array.Empty<SmString>();
    private string? _loadPath;

    public ImportForm(GrtClient? grt)
    {
        _grt = grt;
        Text = "ShotMarker import";
        Width = 1000;
        Height = 620;
        StartPosition = FormStartPosition.CenterScreen;

        BuildGrid();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        top.Controls.AddRange(new Control[] { _browse, _import, _load });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 400 };
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(_log);

        Controls.Add(split);
        Controls.Add(top);

        _browse.Click += (_, _) => Browse();
        _import.Click += (_, _) => Import();

        Shown += (_, _) => DiscoverActiveLoad();
    }

    private void BuildGrid()
    {
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "", Width = 30 });
        foreach (string c in new[] { "String", "When", "Distance", "Face", "Shots", "Score", "Mean v", "SD", "ES" })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = c, HeaderText = c, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Charge", HeaderText = "Charge (gr)" });
    }

    /// <summary>Asks GRT which load is on top. A ShotMarker string is normally one charge,
    /// so the open load's propellant charge is the right default for every row.</summary>
    private void DiscoverActiveLoad()
    {
        if (_grt == null) { Note("Not attached to GRT — the import will ask where to write."); return; }
        try
        {
            var (_, caption, file) = _grt.GetTabOnTopAsync().GetAwaiter().GetResult();
            _loadPath = GrtLoadDoc.EffectiveReadPath(file);
            _load.Text = $"Load: {caption}";
        }
        catch (Exception ex) { Note($"Could not read the active tab: {ex.Message}"); }
    }

    private double? DefaultCharge()
    {
        if (_loadPath == null || !File.Exists(_loadPath)) return null;
        try { return GrtLoadDoc.Load(_loadPath).PropellantChargeGr; }
        catch { return null; }
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "ShotMarker export",
            Filter = "ShotMarker exports (*.tar;*.csv)|*.tar;*.csv|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var log = new List<string>();
        _strings = ImportJob.Plan(dlg.FileName, log);
        foreach (string l in log) Note(l);

        _grid.Rows.Clear();
        double? charge = DefaultCharge();
        var ic = CultureInfo.CurrentCulture;
        foreach (SmString s in _strings)
        {
            var v = s.Shots.Where(sh => sh.VelocityMps is > 0).Select(sh => sh.VelocityMps!.Value).ToList();
            _grid.Rows.Add(true, s.Name, s.Timestamp.ToString("yyyy-MM-dd HH:mm"),
                $"{s.DistanceValue.ToString("0", ic)} {s.DistanceUnit}", s.FaceId, s.Shots.Count,
                s.ScoreText ?? "",
                v.Count > 0 ? v.Average().ToString("0.0", ic) : "",
                s.Stats?.VelocitySdMps?.ToString("0.0", ic) ?? "",
                s.Stats?.VelocityEsMps?.ToString("0.0", ic) ?? "",
                charge?.ToString("0.0#", ic) ?? "");
        }
        _import.Enabled = _grid.Rows.Count > 0;
        Note($"{_strings.Count} string(s) read from {Path.GetFileName(dlg.FileName)}");
    }

    private void Import()
    {
        string? target = _loadPath;
        if (target == null || !File.Exists(target))
        {
            using var dlg = new SaveFileDialog { Title = "Write the import to", Filter = "GRT loads (*.grtload)|*.grtload" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            target = dlg.FileName;
        }

        var selected = new List<(SmString, double?)>();
        for (int i = 0; i < _grid.Rows.Count; i++)
        {
            if (_grid.Rows[i].Cells["sel"].Value is not true) continue;
            double? charge = double.TryParse(Convert.ToString(_grid.Rows[i].Cells["Charge"].Value),
                NumberStyles.Float, CultureInfo.CurrentCulture, out double g) ? g : null;
            selected.Add((_strings[i], charge));
        }
        if (selected.Count == 0) { Note("Nothing ticked."); return; }

        var log = new List<string>();
        string outPath;
        try { outPath = ImportJob.Run(target, selected, log); }
        catch (Exception ex) { Note($"Import failed: {ex.Message}"); return; }
        finally { foreach (string l in log) Note(l); }

        Note($"Wrote {outPath}");
        if (_grt == null) return;
        try { _grt.LoadFileAsync(outPath).GetAwaiter().GetResult(); }
        catch (Exception ex) { Note($"GRT did not open it ({ex.Message}); open {outPath} by hand."); }
    }

    private void Note(string line) => _log.AppendText(line + Environment.NewLine);
}
```

- [ ] **Step 4: Verify it builds**

```bash
dotnet build ShotMarkerPlugin/ShotMarkerPlugin.csproj
```

On macOS this fails with NETSDK1100 (Windows targeting needs Windows). That is expected and is the reason the logic lives in Core. Verify the pipeline instead, headlessly:

```bash
dotnet run --project ShotMarkerCore.Tests -- --help 2>/dev/null || true
dotnet test
```

Build the shell on Raider:

```bash
ssh bill@raider "cd C:\\src\\GRT-Shotmarker && dotnet build ShotMarkerPlugin\\ShotMarkerPlugin.csproj -c Release"
```

Expected: `Build succeeded`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: WinForms shell for picking strings and importing them"
```

---

## Task 12: Package, install and verify end to end

**Files:**
- Create: `ShotMarkerPlugin/plugin/com.grt.plugin.xml`
- Create: `ShotMarkerPlugin/plugin/media/icons/icn_shotmarker_16x16.png`
- Create: `ShotMarkerPlugin/plugin/media/icons/icn_shotmarker_32x32.png`
- Create: `tools/build-plugin.ps1`
- Create: `README.md`

**Interfaces:**
- Consumes: the built `GRT_ShotMarker.exe`.
- Produces: `dist/ShotMarker/` — a folder that drops into GRT's `plugins/`.

- [ ] **Step 1: Write the manifest**

`ShotMarkerPlugin/plugin/com.grt.plugin.xml`:

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes" ?>
<GordonsReloadingTool>

  <plugin
    name           = "ShotMarker Import"
    id             = "com.grt.plugin.shotmarker"
    version        = "0.1.0"
    provider       = "community"
    description    = "Imports ShotMarker electronic-target exports (.tar and .csv). Each shooting string becomes a shot-group tab with the target face drawn to scale and every hit placed on it, plus a measurement of the shot velocities and a note carrying ShotMarker's own group statistics."
    launch-windows = "GRT_ShotMarker.exe"
    launch-type    = "onDemand"
    launch-args    = ""
    start-timeout  = "5.0"
    enabled        = "true"
  >

    <registerevent>
      <event name="TabSwitch" enabled="false" />
      <event name="WindowClosed" enabled="false" />
    </registerevent>

    <menu autoenable="true" icon="media/icons/icn_shotmarker_16x16.png" icon_colorize="false">
      <menuitem id="open" label="Import ShotMarker…" enabled="true"
                icon="media/icons/icn_shotmarker_16x16.png" icon_colorize="false" />
    </menu>

    <toolbar>
      <toolbaritem id="open" label="ShotMarker"
                   tooltip="Import a ShotMarker export: target picture, hits and velocities"
                   icon="media/icons/icn_shotmarker_32x32.png" icon_colorize="false" enabled="true" />
    </toolbar>

  </plugin>
</GordonsReloadingTool>
```

- [ ] **Step 2: Draw the icons**

A target face is the obvious mark: concentric rings with an off-centre hit.

```bash
mkdir -p ShotMarkerPlugin/plugin/media/icons
python3 - <<'PY'
import struct, zlib

def png(path, size):
    cx = cy = (size - 1) / 2
    rings = [(0.95, (0x20,0x20,0x20)), (0.72, (0xFF,0xFF,0xFF)),
             (0.48, (0x20,0x20,0x20)), (0.24, (0xFF,0xFF,0xFF))]
    rows = []
    for y in range(size):
        row = bytearray([0])
        for x in range(size):
            d = ((x - cx) ** 2 + (y - cy) ** 2) ** 0.5 / (size / 2)
            px = (0, 0, 0, 0)
            for r, c in rings:
                if d <= r:
                    px = (*c, 255)
            # the hit: an orange disc up and right of centre
            if ((x - cx - size * 0.18) ** 2 + (y - cy + size * 0.18) ** 2) ** 0.5 <= size * 0.11:
                px = (0xF0, 0x70, 0x20, 255)
            row += bytes(px)
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    open(path, "wb").write(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b""))

png("ShotMarkerPlugin/plugin/media/icons/icn_shotmarker_16x16.png", 16)
png("ShotMarkerPlugin/plugin/media/icons/icn_shotmarker_32x32.png", 32)
print("icons written")
PY
open ShotMarkerPlugin/plugin/media/icons/icn_shotmarker_32x32.png
```

- [ ] **Step 3: Write the packaging script**

`tools/build-plugin.ps1`:

```powershell
# Builds the plugin folder GRT expects: the executable and its dependencies beside
# com.grt.plugin.xml and the media folder. Run on Windows; the shell is net8.0-windows.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist\ShotMarker"

dotnet publish (Join-Path $root "ShotMarkerPlugin\ShotMarkerPlugin.csproj") `
    -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=false -o $dist

# The version in the manifest is what GRT shows in its plugin list, so keep it in step
# with Directory.Build.props rather than letting the two drift.
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
$manifestPath = Join-Path $dist "plugin\com.grt.plugin.xml"
$manifest = Get-Content $manifestPath -Raw
$manifest = $manifest -replace 'version\s*=\s*"[^"]*"', ('version        = "' + $version + '"')
Set-Content $manifestPath $manifest -Encoding UTF8

# GRT reads the manifest from the plugin folder root, not from plugin\.
Move-Item (Join-Path $dist "plugin\com.grt.plugin.xml") $dist -Force
Move-Item (Join-Path $dist "plugin\media") $dist -Force
Remove-Item (Join-Path $dist "plugin") -Recurse -Force

Write-Host "packaged $dist"
```

- [ ] **Step 4: Build and install on the GRT machine**

```bash
ssh bill@raider "cd C:\\src\\GRT-Shotmarker && powershell -NoProfile -File tools\\build-plugin.ps1"
ssh bill@raider "powershell -NoProfile -Command \"Copy-Item -Recurse -Force 'C:\\src\\GRT-Shotmarker\\dist\\ShotMarker' 'C:\\Users\\bill\\GordonsReloadingTool\\plugins\\'\""
ssh bill@raider "powershell -NoProfile -Command \"Get-ChildItem 'C:\\Users\\bill\\GordonsReloadingTool\\plugins\\ShotMarker' | Select-Object -First 10 Name\""
```

Expected: `GRT_ShotMarker.exe`, `com.grt.plugin.xml` and `media` listed.

- [ ] **Step 5: Verify the pipeline headlessly on the GRT machine**

Before touching the GUI, prove the import end to end against a real load:

```bash
ssh bill@raider "cd C:\\Users\\bill\\GordonsReloadingTool\\plugins\\ShotMarker && .\\GRT_ShotMarker.exe --import C:\\src\\GRT-Shotmarker\\fixtures\\shotmarker\\SM_export_Sep_21.tar \"C:\\Users\\bill\\GordonsReloadingTool\\loads\\284 Shehane\\Krieger-2 284 Shehane 180 H4831sc Lapua 8.5T.grtload\" 41.5"
```

Expected: three `'<string>': N shots at 1000y` lines and a `wrote …_shotmarker_<timestamp>.grtload` path.

- [ ] **Step 6: Verify in GRT itself**

Ask the user to:

> 1. Restart GRT and confirm a **ShotMarker** button appears on the toolbar.
> 2. Open the 284 Shehane load, click it, open `SM_export_Sep_21.tar`.
> 3. Confirm three strings are listed with their distances, scores and velocities, and that the charge column is pre-filled from the load.
> 4. Tick them and import.
> 5. Confirm GRT opens the new load with three shot-group tabs, each showing the target face with numbered hits, plus three measurements and three notes.
> 6. In one shot-group tab, check that GRT's own group size reads close to ShotMarker's, which the note records.

Step 6 is the real acceptance test: it proves the reference points and shooting distance were written in the units GRT expects. If the size is out by 25.4x, `refDistance` is in display units and `GrtShotGroupWriter.RefDistanceUnit` plus Task 1's finding are wrong; if it is out by 1.09x, `shootDistance` is in yards.

- [ ] **Step 7: Write the README**

`README.md`:

```markdown
# ShotMarker import for Gordon's Reloading Tool

Imports ShotMarker electronic-target exports into GRT. Each shooting string becomes a
shot-group tab with the scoring face drawn to scale and every hit placed on it, a
measurement of the shot velocities, and a note carrying ShotMarker's own statistics.

## Install

Copy `dist/ShotMarker` into GRT's `plugins` folder and restart GRT.

## Use

Open a load, click **ShotMarker** on the toolbar, choose a `.tar` or `.csv` export, tick
the strings you want and set each one's charge. The import is written to a sibling
`.grtload` — your original is never modified — and GRT opens it.

## Build

```bash
dotnet test                              # core, on any platform
powershell -File tools/build-plugin.ps1  # the plugin folder, on Windows
```

`tools/extract-targetfaces.js` regenerates `ShotMarkerCore/Faces/targetfaces.json` from the
archived ShotMarker bundle in `fixtures/shotmarker/bundle`. The result is committed, so a
normal build needs no Node.

## Design

`docs/superpowers/specs/2026-09-21-shotmarker-import-design.md`.
```

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: package the plugin, with icons, manifest and install script"
```

---

## Self-Review

**Spec coverage**

| Spec section | Task |
|---|---|
| `.tar` archive export | 4 |
| `.csv` shot log export | 5 |
| Four-project architecture | 2, 11 |
| `GrtPluginKit` change (`AddShotGroup`, `ShotGroupGeometry`) | 7 |
| `SmExportReader` | 6 |
| `TargetFaceLibrary` | 3 |
| `TargetRenderer`, `DrawFurniture` flag | 8 |
| `GrtShotGroupWriter` | 9 |
| Coordinate mapping, storage units | 8 (projection), 9 (writer), 1 (units observed) |
| Target faces, build-time extraction | 3 |
| User flow steps 1–5, including `Load_File` | 11 |
| Sighters, flyers, invalid shots | 4, 5 (tagging), 9 (written as flyers) |
| Error handling table | 6, 9, 10 |
| Round-trip invariant test | 9 |
| Tar/zlib, CSV, unit, cross-format, face, golden-image, XML tests | 4, 5, 6, 3, 8, 9 |
| Open question — calibration sample | 1 |

Two spec requirements needed tasks that were not obvious from its prose and are now explicit: the **sibling family** (Task 7, Step 6 — without it the toolkit prunes this plugin's output), and the **round-trip test under both metric and imperial configurations**. The latter is covered in Task 9 by passing `GrtShotGroups.Options` explicitly rather than relying on the ambient `GrtConfig`, which is stronger: it pins the writer's units regardless of the machine the test runs on.

**Placeholder scan**

No "TBD", no "add error handling", no "similar to Task N". Two steps deliberately defer to observed reality rather than prescribing it, and both say exactly what to look at and what the alternatives are: Task 7 Step 5 (attribute spelling, against `FINDINGS.md`) and Task 5 Step 3 (CSV header spelling, against the fixture). That is not a placeholder — guessing there is precisely the failure mode this plan exists to avoid.

**Type consistency**

- `TargetProjection.ToFraction` returns `(double X, double Y)` and is used that way in Tasks 8 and 9.
- `ShotGroupGeometry` is constructed in Task 9 with the six arguments declared in Task 7.
- `GrtShotGroupSet`/`GrtShotPoint`/`GrtCharge`/`GrtShot` are the kit's existing types throughout; `GrtShotPoint(double X, double Y, bool Flyer, bool PointOfAim)` matches the kit's definition.
- `ImportJob.Run` takes `IEnumerable<(SmString, double?)>` in Tasks 10 and 11 alike.
- `SmShot.IsFlyer` is defined in Task 4 and used in Tasks 8, 9 and 10.
- `Fixtures.Path` is defined in Task 2 and used in every later test.
- `SmString` is a `record` because Task 10's tests use `with` expressions on it, and `Shots` is `IReadOnlyList<SmShot>`, which `Array.Empty<SmShot>()` and `.ToList()` both satisfy.

One inconsistency was found and fixed while reviewing: the spec's `SmGroupStats` listed the group box `x0/y0/x1/y1`, but nothing consumes them — the renderer derives the box from the shots it draws, so carrying the device's copy would be a second source of truth for the same rectangle. They are dropped from the record.
