using System.Globalization;
using ShotMarker.Core.Sm;

namespace ShotMarker.Plugin.Ui;

/// <summary>One shot of the string being previewed. The tick-box is the shooter's own decision
/// and nothing else: a shot the DEVICE excluded is shown unticked, greyed and read-only, because
/// ticking it could not include it anyway (IsFlyer ORs the device's flags) — letting it be ticked
/// would promise something the import would not honour.</summary>
public sealed class ShotRow : Observable
{
    private bool _keep;
    private readonly Action<ShotRow>? _changed;

    internal ShotRow(SmShot sh, IFormatProvider ic, Action<ShotRow>? changed)
    {
        Number = sh.Number;
        _keep = !sh.IsFlyer;
        Why = RowText.DeviceExclusion(sh);
        No = sh.Number.ToString(ic);
        Score = sh.Score ?? "";
        V = sh.VelocityMps is { } v && double.IsFinite(v) ? v.ToString("0.0", ic) : "";
        Pos = double.IsFinite(sh.XMm) && double.IsFinite(sh.YMm)
            ? $"{sh.XMm.ToString("0", ic)}, {sh.YMm.ToString("0", ic)}" : "";
        _changed = changed;
    }

    internal int Number { get; }

    public bool Keep
    {
        get => _keep;
        set { if (Set(ref _keep, value)) _changed?.Invoke(this); }
    }

    /// <summary>False for a shot the device excluded: its tick-box cannot be changed.</summary>
    public bool CanChange => Why.Length == 0;

    public string No { get; }
    public string Score { get; }
    public string V { get; }
    public string Pos { get; }
    public string Why { get; }
}

/// <summary>A firing point in the "I shot from" list: the device's code to match on, the device's
/// word to show.</summary>
public sealed record PointItem(string Code, string Text)
{
    public override string ToString() => Text;
}
