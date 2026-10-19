using System.Collections.ObjectModel;
using System.Globalization;
using GrtPluginKit.Grt;
using ShotMarker.Core.Import;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;

namespace ShotMarker.Plugin.Ui;

/// <summary>
/// Pick an export, tick the strings to import, look at each one and strike out any shot that
/// should not count, set each one's charge, import. Each ticked string becomes its own
/// shot-group tab in a sibling load, which GRT is then asked to open.
///
/// <para>Everything the window decides lives here; <see cref="ImportWindow"/> only binds to it
/// and shows the file dialogs. That is what lets the decisions be tested without a screen.</para>
/// </summary>
public sealed class ImportViewModel : Observable
{
    private readonly IGrtHost? _grt;

    private StringRow? _selectedRow;
    private PointItem? _selectedPoint;
    private bool _pointsVisible;
    private string _loadText = "No load open";
    private string _caption = "";
    private string _log = "";
    private byte[]? _facePng;
    private bool _importEnabled;
    private bool _browseEnabled = true;

    /// <summary>Set while the shot list is being rebuilt, so the tick-box handler does not
    /// treat the rows it is populating as the shooter striking shots out.</summary>
    private bool _populating;

    internal ImportViewModel(IGrtHost? grt) => _grt = grt;

    public ObservableCollection<StringRow> Rows { get; } = new();
    public ObservableCollection<ShotRow> Shots { get; } = new();
    public ObservableCollection<PointItem> Points { get; } = new();

    public StringRow? SelectedRow
    {
        get => _selectedRow;
        set { if (Set(ref _selectedRow, value)) ShowPreview(); }
    }

    /// <summary>The "I shot from" choice. Changing it re-ticks the whole list.</summary>
    public PointItem? SelectedPoint
    {
        get => _selectedPoint;
        set { if (Set(ref _selectedPoint, value)) ApplyFiringPoint(); }
    }

    /// <summary>The "I shot from" label and its list show and hide as one. Hidden for a file off a
    /// single target, which is most of them — a frame shared by one shooter has no side to pick.</summary>
    public bool PointsVisible { get => _pointsVisible; private set => Set(ref _pointsVisible, value); }

    public string LoadText { get => _loadText; private set => Set(ref _loadText, value); }
    public string Caption { get => _caption; private set => Set(ref _caption, value); }
    public string Log { get => _log; private set => Set(ref _log, value); }

    /// <summary>The preview picture as PNG bytes, or null for none. The view decodes it.</summary>
    public byte[]? FacePng { get => _facePng; private set => Set(ref _facePng, value); }

    public bool ImportEnabled { get => _importEnabled; private set => Set(ref _importEnabled, value); }
    public bool BrowseEnabled { get => _browseEnabled; private set => Set(ref _browseEnabled, value); }

    /// <summary>The open load's file, once GRT has said which it is.</summary>
    internal string? LoadPath { get; private set; }

    internal bool HasLoad => LoadPath != null && File.Exists(LoadPath);

    /// <summary>Asks GRT which load is on top. A ShotMarker string is normally one charge,
    /// so the open load's propellant charge is the right default for every row.
    ///
    /// <para>The window never blocks on IPC (reference D1): this and the eventual Load_File call
    /// are awaited, not run synchronously, so GRT taking its full timeout to answer never freezes
    /// the window.</para></summary>
    public async Task DiscoverActiveLoadAsync()
    {
        IGrtHost? grt = _grt;
        if (grt == null) { Note("Not attached to GRT — the import will ask where to write."); return; }
        try
        {
            var (_, caption, file) = await grt.GetTabOnTopAsync();
            LoadPath = GrtLoadDoc.EffectiveReadPath(file);
            LoadText = $"Load: {caption}";
        }
        catch (Exception ex) { Note($"Could not read the active tab: {ex.Message}"); }
    }

    internal double? DefaultCharge()
    {
        if (LoadPath == null || !File.Exists(LoadPath)) return null;
        try { return GrtLoadDoc.Load(LoadPath).PropellantChargeGr; }
        catch { return null; }
    }

    /// <summary>Reads the chosen export and lists its strings, every one ticked, each charge
    /// pre-filled from the load, then opens the preview on the first.</summary>
    public void OpenExport(string path)
    {
        var log = new List<string>();
        IReadOnlyList<SmString> strings = ImportJob.Plan(path, log);
        foreach (string l in log) Note(l);

        SelectedRow = null;
        Rows.Clear();
        ClearPreview();
        double? charge = DefaultCharge();
        var ic = CultureInfo.CurrentCulture;
        foreach (SmString s in strings) Rows.Add(new StringRow(s, charge, ic));

        ImportEnabled = Rows.Count > 0;
        Note($"{strings.Count} string(s) read from {Path.GetFileName(path)}");
        OfferFiringPoints(strings);
        SelectedRow = Rows.FirstOrDefault();
    }

    /// <summary>Fills the "I shot from" list with the firing points this file actually holds,
    /// and opens it on the shooter's most likely one.</summary>
    private void OfferFiringPoints(IReadOnlyList<SmString> strings)
    {
        _selectedPoint = null;
        Points.Clear();
        Raise(nameof(SelectedPoint));

        // Device slot order (Right, Middle, Left), so the list reads the way the tablet's own
        // score columns do rather than in whatever order the shooters happened to fire.
        var points = SmFiringPoint.InSlotOrder(
            strings.Select(s => s.FiringPoint).Where(p => p is not null).Select(p => p!));

        bool shared = points.Count > 1;
        PointsVisible = shared;
        if (!shared) { ApplyFiringPoint(); return; }

        foreach (string code in points) Points.Add(new PointItem(code, SmFiringPoint.Word(code)));

        // The tablet's selected shooter is the export's best guess at who made it. Weak
        // evidence, so it is stated rather than quietly acted on: a shooter who reads this and
        // says "no, I was on the right" has the list right there.
        string? assumed = strings.FirstOrDefault(s => s.WasSelectedOnDevice && s.FiringPoint is not null)
                                 ?.FiringPoint;
        SelectedPoint = Points[assumed is null ? 0 : points.IndexOf(assumed)];

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
    /// <para>The one place the tick is decided, and it overwrites hand ticks on purpose:
    /// changing which point was yours is a statement about the whole file. Individual rows
    /// stay editable afterwards for the relay where you moved.</para></summary>
    internal void ApplyFiringPoint()
    {
        string? mine = SelectedPoint?.Code;
        foreach (StringRow row in Rows)
            row.Selected = row.String.FiringPoint is null || row.String.FiringPoint == mine;
    }

    // ---- the preview -------------------------------------------------------------------

    private void ClearPreview()
    {
        FacePng = null;
        _populating = true;
        Shots.Clear();
        _populating = false;
        Caption = "";
    }

    private void ShowPreview()
    {
        if (SelectedRow is not { } row) { ClearPreview(); return; }
        SmString s = row.String;
        DrawFace(s);
        FillShotList(s);
        Caption = RowText.CaptionFor(s);
    }

    /// <summary>Draws the string on the face the import will use. ImportJob.ResolveFace is
    /// called rather than the library directly so an unknown face falls back here exactly as
    /// it will there. Its log lines are swallowed: OpenExport already reported them once, and
    /// this runs again on every click and every tick.</summary>
    private void DrawFace(SmString s)
    {
        try
        {
            RenderedTarget r = TargetRenderer.Render(
                s, ImportJob.ResolveFace(s, new List<string>()), RenderOptions.ForPreview);
            FacePng = r.Png;
        }
        catch (Exception ex)
        {
            FacePng = null;
            Note($"'{s.Name}': could not be drawn ({ex.Message})");
        }
    }

    private void FillShotList(SmString s)
    {
        _populating = true;
        try
        {
            Shots.Clear();
            var ic = CultureInfo.CurrentCulture;
            foreach (SmShot sh in s.Shots) Shots.Add(new ShotRow(sh, ic, OnKeepChanged));
        }
        finally { _populating = false; }
    }

    private void OnKeepChanged(ShotRow _)
    {
        if (!_populating) ApplyStrikeOuts();
    }

    /// <summary>Folds the tick-boxes back into the selected row's string. The row's string is
    /// replaced, so what the row carries is always what the import will write.</summary>
    private void ApplyStrikeOuts()
    {
        if (SelectedRow is not { } row) return;
        SmString s = row.String;

        var struck = Shots.Where(r => !r.Keep).Select(r => r.Number).ToHashSet();
        var shots = s.Shots
            // Only ever sets the shooter's own flag. A device-excluded shot is already out and
            // its tick-box is read-only, so marking it again would say the shooter struck out
            // a shot they were never offered.
            .Select(sh => sh with { IsExcludedByUser = struck.Contains(sh.Number) && RowText.DeviceExclusion(sh).Length == 0 })
            .ToList();

        SmString curated = s with { Shots = shots };
        row.String = curated;
        // The shot list is deliberately not rebuilt: the shooter is mid-edit in it, and
        // repopulating would move the cursor out from under them. Only the two things that
        // actually changed are redrawn.
        DrawFace(curated);
        Caption = RowText.CaptionFor(curated);
    }

    // ---- the import --------------------------------------------------------------------

    /// <summary>The ticked strings with their charges, in list order.</summary>
    internal List<(SmString String, double? ChargeGrains)> Selection() =>
        Rows.Where(r => r.Selected).Select(r => (r.String, r.ChargeGrains)).ToList();

    /// <summary>Writes the ticked strings into a sibling of the open load and asks GRT to open
    /// it. With no load open (or no GRT), <paramref name="askTarget"/> asks the shooter where to
    /// write; null from it cancels.</summary>
    public async Task ImportAsync(Func<Task<string?>> askTarget)
    {
        string? target = LoadPath;
        if (target == null || !File.Exists(target))
        {
            target = await askTarget();
            if (target == null) return;
        }

        var selected = Selection();
        if (selected.Count == 0) { Note("Nothing ticked."); return; }

        // A second click while this one is still in flight would start a concurrent write
        // against the same sibling load. Browse is disabled alongside it: it clears the list,
        // and a list cleared out from under an import in flight leaves the window describing a
        // state that is not the one being written.
        ImportEnabled = false;
        BrowseEnabled = false;
        try
        {
            var log = new List<string>();
            string outPath;
            try { outPath = ImportJob.Run(target, selected, log); }
            catch (Exception ex) { Note($"Import failed: {ex.Message}"); return; }
            finally { foreach (string l in log) Note(l); }

            Note($"Wrote {outPath}");
            IGrtHost? grt = _grt;
            if (grt == null) return;
            try { await grt.LoadFileAsync(outPath); }
            catch (Exception ex) { Note($"GRT did not open it ({ex.Message}); open {outPath} by hand."); }
        }
        finally { ImportEnabled = true; BrowseEnabled = true; }
    }

    internal void Note(string line) => Log += line + Environment.NewLine;
}
