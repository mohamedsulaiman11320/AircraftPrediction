using AFP.Domain.Enums;

namespace AFP.Domain.Rules;

/// <summary>An approved inclusive limit range for one measurement (from <c>engineering_threshold</c>). Null bound = unbounded.</summary>
public sealed record ApprovedLimit(double? Min, double? Max)
{
    /// <summary>A breach is strictly outside the range; a value equal to a limit is within limits.</summary>
    public bool IsBreachedBy(double value) => (Min is { } min && value < min) || (Max is { } max && value > max);
}

/// <summary>
/// Normal = both measurements within approved limits; Critical = exactly one breaches; HighCritical = both breach.
/// Missing/non-finite values or a missing approved limit yield Unclassified — never Normal.
/// Separate from lifecycle status, ML prediction state and anomaly investigation state.
/// </summary>
public static class OperationalCriticalityRule
{
    public static OperationalCriticality Classify(
        double? temperatureC, ApprovedLimit? temperatureLimit, double? pressureKpa, ApprovedLimit? pressureLimit)
    {
        if (temperatureLimit is null || pressureLimit is null) return OperationalCriticality.Unclassified;
        if (temperatureC is not { } t || !double.IsFinite(t)) return OperationalCriticality.Unclassified;
        if (pressureKpa is not { } p || !double.IsFinite(p)) return OperationalCriticality.Unclassified;

        var breaches = (temperatureLimit.IsBreachedBy(t) ? 1 : 0) + (pressureLimit.IsBreachedBy(p) ? 1 : 0);
        return breaches switch
        {
            0 => OperationalCriticality.Normal,
            1 => OperationalCriticality.Critical,
            _ => OperationalCriticality.HighCritical,
        };
    }
}
