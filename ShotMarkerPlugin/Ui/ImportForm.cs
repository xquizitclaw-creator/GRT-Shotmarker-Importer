using System.Globalization;
using GrtPluginKit.Grt;
using GrtPluginKit.Ipc;
using ShotMarker.Core.Faces;
using ShotMarker.Core.Import;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;

namespace ShotMarker.Plugin.Ui;

/// <summary>
/// Pick an export, tick the strings to import, look at each one and strike out any shot that
/// should not count, set each one's charge, import. Each ticked string becomes its own
/// shot-group tab in a sibling load, which GRT is then asked to open.
/// </summary>
internal sealed class ImportForm : Form
{
    private readonly GrtClient? _grt;
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _browse = new() { Text = "Open export…", AutoSize = true };
    private readonly Button _import = new() { Text = "Import selected", AutoSize = true, Enabled = false };
    private readonly Label _load = new() { AutoSize = true, Text = "No load open" };
    private readonly Label _pointLabel = new() { AutoSize = true, Text = "I shot from:", Visible = false,
                                                Padding = new Padding(12, 6, 2, 0) };
    private readonly ComboBox _point = new() { Visible = false, Width = 90,
                                               DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };

    // The preview: the string as it will be imported, and every shot in it.
    private readonly SplitContainer _middle = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
    private readonly SplitContainer _preview = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly PictureBox _face = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly DataGridView _shots = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false };
    private readonly Label _caption = new() { Dock = DockStyle.Top, AutoSize = false, Height = 34, Padding = new Padding(4, 2, 4, 2) };

    private string? _loadPath;

    /// <summary>Set while the shot grid is being rebuilt, so the tick-box handler does not
    /// treat the rows it is populating as the shooter striking shots out.</summary>
    private bool _populating;

    public ImportForm(GrtClient? grt)
    {
        _grt = grt;
        Text = "ShotMarker import";
        Width = 1240;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;

        BuildGrid();
        BuildShotGrid();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        top.Controls.AddRange(new Control[] { _browse, _import, _pointLabel, _point, _load });

        _preview.Panel1.Controls.Add(_face);
        _preview.Panel2.Controls.Add(_shots);

        _middle.Panel1.Controls.Add(_grid);
        _middle.Panel2.Controls.Add(_preview);
        _middle.Panel2.Controls.Add(_caption);   // added after the splitter so Dock.Top wins

        _split.Panel1.Controls.Add(_middle);
        _split.Panel2.Controls.Add(_log);

        Controls.Add(_split);
        Controls.Add(top);

        _browse.Click += (_, _) => Browse();
        _import.Click += async (_, _) => await ImportAsync();
        _grid.SelectionChanged += (_, _) => ShowPreview();
        _point.SelectedIndexChanged += (_, _) => ApplyFiringPoint();

        // The window never blocks on IPC (reference D1): both the tab discovery and the
        // eventual Load_File call are awaited here, not run synchronously, so GRT taking its
        // full timeout to answer never freezes the message loop.
        Shown += async (_, _) =>
        {
            // D4: SplitterDistance means what it says only once the control has its real,
            // laid-out size. Setting it in the initializer gets silently clamped to the
            // unparented default size and then rescaled proportionally — cosmetic, but this
            // is the cheap way to make the number in the source the number on screen.
            _split.SplitterDistance = 540;
            _middle.SplitterDistance = 560;
            _preview.SplitterDistance = 330;
            await DiscoverActiveLoadAsync();
        };
    }

    private void BuildGrid()
    {
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "", Width = 30 });
        foreach (string c in new[] { "String", "When", "Distance", "Face", "Shots", "Score", "Mean v", "SD", "ES" })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = c, HeaderText = c, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Charge", HeaderText = "Charge (gr)" });
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
    }

    private void BuildShotGrid()
    {
        _shots.Columns.Add(new DataGridViewCheckBoxColumn { Name = "keep", HeaderText = "", Width = 30 });
        _shots.Columns.Add(new DataGridViewTextBoxColumn { Name = "No", HeaderText = "#", ReadOnly = true, Width = 40 });
        _shots.Columns.Add(new DataGridViewTextBoxColumn { Name = "Score", HeaderText = "Score", ReadOnly = true, Width = 55 });
        _shots.Columns.Add(new DataGridViewTextBoxColumn { Name = "V", HeaderText = "m/s", ReadOnly = true, Width = 70 });
        _shots.Columns.Add(new DataGridViewTextBoxColumn { Name = "Pos", HeaderText = "x, y (mm)", ReadOnly = true, Width = 110 });
        _shots.Columns.Add(new DataGridViewTextBoxColumn { Name = "Why", HeaderText = "Excluded", ReadOnly = true, Width = 130 });

        // A checkbox cell does not raise CellValueChanged until the cell loses focus, which
        // would leave the picture disagreeing with the tick the shooter just made until they
        // clicked elsewhere. Committing on the dirty-state change makes the toggle take
        // effect on the click itself.
        _shots.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_shots.IsCurrentCellDirty) _shots.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _shots.CellValueChanged += (_, e) =>
        {
            if (_populating || e.RowIndex < 0 || _shots.Columns[e.ColumnIndex].Name != "keep") return;
            ApplyStrikeOuts();
        };
    }

    /// <summary>Asks GRT which load is on top. A ShotMarker string is normally one charge,
    /// so the open load's propellant charge is the right default for every row.</summary>
    private async Task DiscoverActiveLoadAsync()
    {
        // Captured into a local: nullable flow analysis narrows a local across an `await`
        // reliably, where it cannot always be trusted to keep narrowing a field.
        GrtClient? grt = _grt;
        if (grt == null) { Note("Not attached to GRT — the import will ask where to write."); return; }
        try
        {
            var (_, caption, file) = await grt.GetTabOnTopAsync();
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
        IReadOnlyList<SmString> strings = ImportJob.Plan(dlg.FileName, log);
        foreach (string l in log) Note(l);

        _grid.Rows.Clear();
        ClearPreview();
        double? charge = DefaultCharge();
        var ic = CultureInfo.CurrentCulture;
        foreach (SmString s in strings)
        {
            // Added ticked; which of them stay ticked is ApplyFiringPoint's single decision,
            // made once the firing-point list exists, so there is one rule rather than two
            // that have to agree.
            int i = _grid.Rows.Add(true, s.Name, s.Timestamp.ToString("yyyy-MM-dd HH:mm"),
                $"{s.DistanceValue.ToString("0", ic)} {s.DistanceUnit}", s.FaceId, CountedText(s),
                s.ScoreText ?? "",
                MeanVelocityText(s, ic),
                s.Stats?.VelocitySdMps?.ToString("0.0", ic) ?? "",
                s.Stats?.VelocityEsMps?.ToString("0.0", ic) ?? "",
                charge?.ToString("0.0#", ic) ?? "");

            // Reference D2: identity travels on the row, never by index. The grid is unbound
            // and its text columns sort automatically, so a header click reorders the rows
            // while any positional lookup into `strings` would silently keep pointing at the
            // pre-sort position — pairing the wrong charge with the wrong string. The row's
            // Tag is also where curation lives: striking a shot out replaces it with the
            // edited string, so the row always carries exactly what will be imported.
            _grid.Rows[i].Tag = s;
        }
        _import.Enabled = _grid.Rows.Count > 0;
        Note($"{strings.Count} string(s) read from {Path.GetFileName(dlg.FileName)}");
        OfferFiringPoints(strings);
        ShowPreview();
    }

    /// <summary>Fills the "I shot from" list with the firing points this file actually holds,
    /// and opens it on the shooter's most likely one. Hidden entirely for a file off a single
    /// target, which is most of them — a frame shared by one shooter has no side to pick.
    /// </summary>
    private void OfferFiringPoints(IReadOnlyList<SmString> strings)
    {
        _point.Items.Clear();

        // Device slot order (Right, Middle, Left), so the list reads the way the tablet's own
        // score columns do rather than in whatever order the shooters happened to fire.
        var points = SmFiringPoint.InSlotOrder(
            strings.Select(s => s.FiringPoint).Where(p => p is not null).Select(p => p!));

        bool shared = points.Count > 1;
        _pointLabel.Visible = _point.Visible = shared;
        if (!shared) { ApplyFiringPoint(); return; }

        foreach (string code in points) _point.Items.Add(new PointItem(code, SmFiringPoint.Word(code)));

        // The tablet's selected shooter is the export's best guess at who made it. Weak
        // evidence, so it is stated rather than quietly acted on: a shooter who reads this and
        // says "no, I was on the right" has the list right there.
        string? assumed = strings.FirstOrDefault(s => s.WasSelectedOnDevice && s.FiringPoint is not null)
                                 ?.FiringPoint;
        _point.SelectedIndex = assumed is null ? 0 : points.IndexOf(assumed);

        Note($"This frame was shared by {points.Count} shooters ("
           + string.Join(", ", points.Select(SmFiringPoint.Word)) + ").");
        Note(assumed is null
            ? $"The export does not say which point was yours — assuming {SmFiringPoint.Word(points[0])}. "
            + "Set 'I shot from' if that is wrong."
            : $"Your tablet had the {SmFiringPoint.Word(assumed)} point selected when this was "
            + "exported, so that is assumed to be yours. Set 'I shot from' if you were elsewhere.");
    }

    /// <summary>Ticks the strings shot from the selected point and unticks the rest. Strings
    /// with no firing point — a lone target, or a frame where the point could not be worked
    /// out — stay ticked whatever is chosen: they are nobody else's.
    ///
    /// <para>The one place the tick is decided, and it overwrites by hand ticks on purpose:
    /// changing which point was yours is a statement about the whole file. Individual rows
    /// stay editable afterwards for the relay where you moved.</para></summary>
    private void ApplyFiringPoint()
    {
        string? mine = (_point.SelectedItem as PointItem)?.Code;
        foreach (DataGridViewRow row in _grid.Rows)
            if (row.Tag is SmString s)
                row.Cells["sel"].Value = s.FiringPoint is null || s.FiringPoint == mine;
    }

    /// <summary>A firing point in the list: the device's code to match on, the device's word to
    /// show. ComboBox displays whatever ToString gives it.</summary>
    private sealed record PointItem(string Code, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>"20" normally; "18 of 20" once some are struck out, so the count in the list
    /// is the count that will be measured.</summary>
    private static string CountedText(SmString s)
    {
        int struck = s.Shots.Count(sh => sh.IsExcludedByUser);
        return struck == 0 ? s.Shots.Count.ToString(CultureInfo.CurrentCulture)
                           : $"{s.Shots.Count - struck} of {s.Shots.Count}";
    }

    /// <summary>Reference D3: uses the same shot set as the writer's velocity measurement
    /// (which is also where the SD/ES cells beside this one come from) — not every shot with
    /// a velocity reading, which disagrees with both. Prefers ShotMarker's own group average
    /// when it is present, for the same reason SD and ES do.</summary>
    private static string MeanVelocityText(SmString s, IFormatProvider ic)
    {
        if (s.Stats?.VelocityAvgMps is { } avg) return avg.ToString("0.0", ic);
        var v = s.Shots
            .Where(sh => !sh.IsFlyer && sh.VelocityMps is > 0 && double.IsFinite(sh.VelocityMps.Value))
            .Select(sh => sh.VelocityMps!.Value)
            .ToList();
        return v.Count > 0 ? v.Average().ToString("0.0", ic) : "";
    }

    // ---- the preview -------------------------------------------------------------------

    private DataGridViewRow? SelectedRow()
    {
        if (_grid.SelectedRows.Count > 0) return _grid.SelectedRows[0];
        return _grid.CurrentRow;
    }

    private void ClearPreview()
    {
        Image? old = _face.Image;
        _face.Image = null;
        old?.Dispose();
        _populating = true;
        _shots.Rows.Clear();
        _populating = false;
        _caption.Text = "";
    }

    private void ShowPreview()
    {
        if (SelectedRow()?.Tag is not SmString s) { ClearPreview(); return; }
        DrawFace(s);
        FillShotList(s);
        _caption.Text = CaptionFor(s);
    }

    /// <summary>The device's own figures are shown here as they are in the note, and carry the
    /// same qualifier once a shot has been struck out: they measured the string ShotMarker
    /// saw, which is no longer the group being imported.</summary>
    private static string CaptionFor(SmString s)
    {
        var ic = CultureInfo.CurrentCulture;
        string stats = s.Stats is { } st && st.GroupSizeMm is { } g
            ? $"   ·   ShotMarker: group {g.ToString("0.0", ic)} mm"
              + (st.VelocitySdMps is { } sd ? $", sd {sd.ToString("0.0", ic)} m/s" : "")
              + (s.Shots.Any(sh => sh.IsExcludedByUser) ? " (full string, before strike-outs)" : "")
            : "";
        return $"{s.Name}   ·   {CountedText(s)} shots   ·   {s.FaceId}{stats}";
    }

    /// <summary>Draws the string on the face the import will use. ImportJob.ResolveFace is
    /// called rather than the library directly so an unknown face falls back here exactly as
    /// it will there — a preview of a target the shooter is not going to get would be worse
    /// than no preview. Its log lines are swallowed: Browse already reported them once, and
    /// this runs again on every click and every tick.</summary>
    private void DrawFace(SmString s)
    {
        Image? old = _face.Image;
        try
        {
            RenderedTarget r = TargetRenderer.Render(
                s, ImportJob.ResolveFace(s, new List<string>()), RenderOptions.ForPreview);
            using var ms = new MemoryStream(r.Png);
            // Copied into a Bitmap of its own rather than handed out directly: Image.FromStream
            // keeps the stream it was given and reads from it lazily, so the picture would be
            // drawn from a MemoryStream this method has already disposed.
            using var decoded = Image.FromStream(ms);
            _face.Image = new Bitmap(decoded);
        }
        catch (Exception ex)
        {
            _face.Image = null;
            Note($"'{s.Name}': could not be drawn ({ex.Message})");
        }
        old?.Dispose();
    }

    private void FillShotList(SmString s)
    {
        _populating = true;
        try
        {
            _shots.Rows.Clear();
            var ic = CultureInfo.CurrentCulture;
            foreach (SmShot sh in s.Shots)
            {
                string why = DeviceExclusion(sh);
                int i = _shots.Rows.Add(
                    !sh.IsFlyer,
                    sh.Number.ToString(ic),
                    sh.Score ?? "",
                    sh.VelocityMps is { } v && double.IsFinite(v) ? v.ToString("0.0", ic) : "",
                    double.IsFinite(sh.XMm) && double.IsFinite(sh.YMm)
                        ? $"{sh.XMm.ToString("0", ic)}, {sh.YMm.ToString("0", ic)}" : "",
                    why);
                DataGridViewRow row = _shots.Rows[i];
                row.Tag = sh.Number;

                // A shot the DEVICE excluded is shown unticked and read-only. The tick-box
                // is the shooter's own decision and nothing else: ticking one of these on
                // could not include it anyway (IsFlyer ORs the device's flags), so letting it
                // be ticked would promise something the import would not honour.
                if (why.Length > 0)
                {
                    row.Cells["keep"].ReadOnly = true;
                    row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
                }
            }
        }
        finally { _populating = false; }
    }

    /// <summary>Why the device, or ShotMarker's own group, left this shot out — empty when it
    /// did not, which is also what makes the tick-box the shooter's to set.</summary>
    private static string DeviceExclusion(SmShot sh) =>
        sh.IsSighter ? "sighter"
        : sh.IsInvalid ? "no position"
        : sh.IsExcludedOnDevice ? "excluded on device"
        : sh.InSelectedGroup == false ? "not in group"
        : "";

    /// <summary>Folds the tick-boxes back into the selected row's string. The row's Tag is
    /// replaced, so what the row carries is always what the import will write.</summary>
    private void ApplyStrikeOuts()
    {
        if (SelectedRow() is not { } row || row.Tag is not SmString s) return;

        var struck = new HashSet<int>();
        foreach (DataGridViewRow r in _shots.Rows)
            if (r.Cells["keep"].Value is not true && r.Tag is int n) struck.Add(n);

        var shots = s.Shots
            // Only ever sets the shooter's own flag. A device-excluded shot is already out and
            // its tick-box is read-only, so marking it again would say the shooter struck out
            // a shot they were never offered.
            .Select(sh => sh with { IsExcludedByUser = struck.Contains(sh.Number) && DeviceExclusion(sh).Length == 0 })
            .ToList();

        SmString curated = s with { Shots = shots };
        row.Tag = curated;
        row.Cells["Shots"].Value = CountedText(curated);
        // The shot list is deliberately not rebuilt: the shooter is mid-edit in it, and
        // repopulating would move the cursor out from under them. Only the two things that
        // actually changed are redrawn.
        DrawFace(curated);
        _caption.Text = CaptionFor(curated);
    }

    // ---- the import --------------------------------------------------------------------

    private async Task ImportAsync()
    {
        string? target = _loadPath;
        if (target == null || !File.Exists(target))
        {
            using var dlg = new SaveFileDialog { Title = "Write the import to", Filter = "GRT loads (*.grtload)|*.grtload" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            target = dlg.FileName;
        }

        var selected = new List<(SmString, double?)>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Cells["sel"].Value is not true) continue;
            if (row.Tag is not SmString s) continue;
            double? charge = double.TryParse(Convert.ToString(row.Cells["Charge"].Value),
                NumberStyles.Float, CultureInfo.CurrentCulture, out double g) ? g : null;
            selected.Add((s, charge));
        }
        if (selected.Count == 0) { Note("Nothing ticked."); return; }

        // A second click while this one is still in flight would start a concurrent write
        // against the same sibling load; RequestAsync serialises GRT's own IPC traffic but the
        // file write on disk does not.
        // Browse is disabled alongside it: it clears the grid, and a grid cleared out from
        // under an import in flight leaves the window describing a state that is not the one
        // being written.
        _import.Enabled = false;
        _browse.Enabled = false;
        try
        {
            var log = new List<string>();
            string outPath;
            try { outPath = ImportJob.Run(target, selected, log); }
            catch (Exception ex) { Note($"Import failed: {ex.Message}"); return; }
            finally { foreach (string l in log) Note(l); }

            Note($"Wrote {outPath}");
            GrtClient? grt = _grt;
            if (grt == null) return;
            try { await grt.LoadFileAsync(outPath); }
            catch (Exception ex) { Note($"GRT did not open it ({ex.Message}); open {outPath} by hand."); }
        }
        finally { _import.Enabled = true; _browse.Enabled = true; }
    }

    private void Note(string line) => _log.AppendText(line + Environment.NewLine);
}
