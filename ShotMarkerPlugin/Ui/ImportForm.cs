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
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };

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

        _split.Panel1.Controls.Add(_grid);
        _split.Panel2.Controls.Add(_log);

        Controls.Add(_split);
        Controls.Add(top);

        _browse.Click += (_, _) => Browse();
        _import.Click += async (_, _) => await ImportAsync();

        // The window never blocks on IPC (reference D1): both the tab discovery and the
        // eventual Load_File call are awaited here, not run synchronously, so GRT taking its
        // full timeout to answer never freezes the message loop.
        Shown += async (_, _) =>
        {
            // D4: SplitterDistance means what it says only once the control has its real,
            // laid-out size. Setting it in the initializer gets silently clamped to the
            // unparented default size and then rescaled proportionally — cosmetic, but this
            // is the cheap way to make the number in the source the number on screen.
            _split.SplitterDistance = 400;
            await DiscoverActiveLoadAsync();
        };
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
        double? charge = DefaultCharge();
        var ic = CultureInfo.CurrentCulture;
        foreach (SmString s in strings)
        {
            int i = _grid.Rows.Add(true, s.Name, s.Timestamp.ToString("yyyy-MM-dd HH:mm"),
                $"{s.DistanceValue.ToString("0", ic)} {s.DistanceUnit}", s.FaceId, s.Shots.Count,
                s.ScoreText ?? "",
                MeanVelocityText(s, ic),
                s.Stats?.VelocitySdMps?.ToString("0.0", ic) ?? "",
                s.Stats?.VelocityEsMps?.ToString("0.0", ic) ?? "",
                charge?.ToString("0.0#", ic) ?? "");

            // Reference D2: identity travels on the row, never by index. The grid is unbound
            // and its text columns sort automatically, so a header click reorders the rows
            // while any positional lookup into `strings` would silently keep pointing at the
            // pre-sort position — pairing the wrong charge with the wrong string.
            _grid.Rows[i].Tag = s;
        }
        _import.Enabled = _grid.Rows.Count > 0;
        Note($"{strings.Count} string(s) read from {Path.GetFileName(dlg.FileName)}");
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
