<!-- The release page's prose. The workflow fills in each zip's name, size and hash where the
     doubled-brace tokens are, and publishes the rest of this file verbatim. The What's new
     section is updated by hand in the same commit that bumps the version. -->

## What's new

### 0.1.1

Sighters are left out of the load instead of being written as flyers. A GRT shot point is either a
scoring shot or a flyer — GRT's own word for a reject, which it prints beside the hit — and a
sighter is neither. In a Match 1 string, where sighters are unlimited, writing them as flyers
labelled most of the tab as rejects. Now the tab holds your record shots alone, numbered from #1,
and the note names the sighters it dropped and where GRT's numbering starts. The import window
still draws them in red, so you can see where they went before you import.

### 0.1.0

First release.

Reads ShotMarker's `.tar` session archives and its `.csv` shot logs, and knows the 208 target
faces ShotMarker itself ships, so the face your string was shot on is the face GRT draws.

Pair- and triple-fire frames are handled: the import window asks which firing point was yours,
and the other shooters on the frame stay out of your load. Checked end to end against a real
two-up export — six strings at 1000 yards, both shooters on one sensor frame.

## Install

1. Download **{{ZIP}}** below ({{MB}} MB).
2. Unzip it. You get a folder called `ShotMarker`.
3. Put that folder inside GRT's `plugins` folder.
4. Restart GRT. A **ShotMarker** button appears on the toolbar.

Windows, 64-bit. Nothing else to install — that zip carries its own copy of .NET, which is nearly
all of its size. If you are not sure what you have on the GRT machine, this is the one to take.

### The smaller download

**{{SLIM_ZIP}}** ({{SLIM_MB}} MB) is the same plugin with the bundled copy of .NET left out: a
dozen files instead of 250. It installs exactly the same way, and it needs the
[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) already installed —
without it, GRT lists the plugin but cannot start it.

## What it does

Point it at the `.tar` or `.csv` your ShotMarker exported. Every string in the export is listed
with its distance, score and velocities; tick the ones you want, set each one's charge weight, and
import. GRT opens a new load beside the one you had open, with one shot-group tab per string — the
scoring face drawn to scale, every hit placed on it, the velocities measured, and a note carrying
ShotMarker's own group statistics.

Your original load file is never modified. Only the three most recent imports from a given load are
kept, so trial runs don't pile up; rename one to keep it for good.

---

- `{{ZIP}}` — SHA-256 `{{SHA}}` ({{MB}} MB)
- `{{SLIM_ZIP}}` — SHA-256 `{{SLIM_SHA}}` ({{SLIM_MB}} MB)
