using AFP.Domain.Enums;

namespace AFP.Domain.Rules;

/// <summary>
/// Whether a model's output may be used operationally. Only an Approved or Active model qualifies; a Trained or
/// Registered candidate (for example one scored in the development environment) never does, whatever its metrics.
/// </summary>
public static class ModelGovernance
{
    public static readonly ModelStatus[] ApprovedForUse = [ModelStatus.Approved, ModelStatus.Active];

    public static bool IsApprovedForUse(ModelStatus? status) => status is { } s && ApprovedForUse.Contains(s);
}
