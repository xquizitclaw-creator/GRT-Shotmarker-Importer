using System.Globalization;
using ShotMarker.Core.Sm;

namespace ShotMarker.Plugin.Ui;

/// <summary>One string in the list. Reference D2: identity travels on the row, never by index —
/// the list sorts on a header click, and a positional lookup would pair the wrong charge with the
/// wrong string. The row is also where curation lives: striking a shot out replaces
/// <see cref="String"/> with the edited string, so the row always carries exactly what will be
/// imported.</summary>
public sealed class StringRow : Observable
{
    private bool _selected;
    private SmString _string;
    private string _shots;
    private string _charge;

    internal StringRow(SmString s, double? charge, IFormatProvider ic)
    {
        // Added ticked; which of them stay ticked is ImportViewModel.ApplyFiringPoint's single
        // decision, so there is one rule rather than two that have to agree.
        _selected = true;
        _string = s;
        Name = s.Name;
        When = s.Timestamp.ToString("yyyy-MM-dd HH:mm");
        Distance = $"{s.DistanceValue.ToString("0", ic)} {s.DistanceUnit}";
        Face = s.FaceId;
        _shots = RowText.CountedText(s);
        Score = s.ScoreText ?? "";
        MeanV = RowText.MeanVelocityText(s, ic);
        Sd = s.Stats?.VelocitySdMps?.ToString("0.0", ic) ?? "";
        Es = s.Stats?.VelocityEsMps?.ToString("0.0", ic) ?? "";
        _charge = charge?.ToString("0.0#", ic) ?? "";
    }

    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    public string Name { get; }
    public string When { get; }
    public string Distance { get; }
    public string Face { get; }
    public string Shots { get => _shots; private set => Set(ref _shots, value); }
    public string Score { get; }
    public string MeanV { get; }
    public string Sd { get; }
    public string Es { get; }

    /// <summary>The charge in grains as the shooter typed it, in their own number format.</summary>
    public string Charge { get => _charge; set => Set(ref _charge, value ?? ""); }

    /// <summary>The string as it will be imported, strike-outs included.</summary>
    internal SmString String
    {
        get => _string;
        set { _string = value; Shots = RowText.CountedText(value); }
    }

    /// <summary>The typed charge, or null when it is empty or not a number — which imports the
    /// string without one, as before.</summary>
    internal double? ChargeGrains =>
        double.TryParse(_charge, NumberStyles.Float, CultureInfo.CurrentCulture, out double g) ? g : null;
}
