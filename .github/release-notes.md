<!-- The stable part of every release's notes. The release workflow fills in {{ZIP}}, {{SHA}} and
     {{MB}} from the file it built, and {{WHATS_NEW}} from the tag's own message — so what changed
     in a release is written once, in the tag, and the rest of this is edited here rather than
     inside a YAML string. -->

## Install

1. Download **{{ZIP}}** below.
2. Unzip it. You get a folder called `ShotMarker`.
3. Put that folder inside GRT's `plugins` folder.
4. Restart GRT. A **ShotMarker** button appears on the toolbar.

Windows, 64-bit. Nothing else to install — this build carries its own copy of .NET, so there is no
runtime to chase down first.

## What it does

Point it at the `.tar` or `.csv` your ShotMarker exported. Every string in the export is listed
with its distance, score and velocities; tick the ones you want, set each one's charge weight, and
import. GRT opens a new load beside the one you had open, with one shot-group tab per string — the
scoring face drawn to scale, every hit placed on it, the velocities measured, and a note carrying
ShotMarker's own group statistics.

Your original load file is never modified. Pair- and triple-fire frames are handled: pick which
firing point was yours, and the other shooters' strings stay out of your load.

{{WHATS_NEW}}

---

`{{ZIP}}` SHA-256: `{{SHA}}` ({{MB}} MB)
