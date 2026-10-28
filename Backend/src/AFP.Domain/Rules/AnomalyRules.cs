using AFP.Domain.Enums;

namespace AFP.Domain.Rules;

/// <summary>
/// Which anomalies raise automatic work orders. Since 2026-10-28 (user decision) work is raised from the 30-day sensor
/// forecast only (<see cref="AnomalySource.ForecastModel"/>). Anomalies from recorded telemetry and failure-model scores
/// never raise a work order.
/// </summary>
public static class AnomalyRules
{
    public static readonly AnomalySource[] WorkOrderSources = [AnomalySource.ForecastModel];

    /// <summary>A forecast breach of one approved limit.</summary>
    public const string ConditionCritical = "Critical";
    /// <summary>Forecast breaches of both the engine-temperature and the oil-pressure limits.</summary>
    public const string ConditionHighCritical = "HighCritical";

    /// <summary>Condition of an open forecast incident, from the measurements of the limits it breaches.</summary>
    public static string ForecastCondition(IReadOnlyCollection<string?> breachedMeasurements) =>
        breachedMeasurements.Contains(FleetHealthRules.MeasurementEngineTemperature) && breachedMeasurements.Contains(FleetHealthRules.MeasurementOilPressure)
            ? ConditionHighCritical : ConditionCritical;

    /// <summary>Forecast length of each horizon, in hours.</summary>
    public static int HorizonHours(SensorForecastHorizon h) => h switch
    {
        SensorForecastHorizon.OneDay => 24,
        SensorForecastHorizon.SevenDays => 168,
        _ => 720,
    };

    /// <summary>Status reason written when forecast work places an aircraft in maintenance; release looks for it.</summary>
    public const string ForecastMaintenanceReason = "Forecast breach: maintenance work order open";
}
