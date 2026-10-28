using AFP.Domain.Enums;

namespace AFP.Application.Contracts;

/// <summary>
/// One predicted reading. A null value means the forecaster is not validated for that sensor and horizon - never a
/// guess. Low/High bound the out-of-sample 80 % interval when the model supplies one.
/// </summary>
public sealed record SensorForecastPointDto(
    DateTime TimestampUtc, double? TemperatureC, double? PressurePsi, double? VibrationIndex,
    double? TemperatureCLow = null, double? TemperatureCHigh = null, double? PressurePsiLow = null, double? PressurePsiHigh = null,
    double? VibrationIndexLow = null, double? VibrationIndexHigh = null);

/// <summary>
/// Future sensor readings for one aircraft over one horizon, from the Active (approved) SensorForecast model. Points are
/// strictly after <see cref="DataAsOfUtc"/>; <see cref="Status"/> NotAvailable carries <see cref="Reason"/> and no
/// points. Recorded telemetry is served separately (GET /telemetry/series) and is never returned here.
/// <see cref="SupportedMetrics"/> lists the metrics validated for this horizon (the others are always null).
/// </summary>
public sealed record SensorForecastDto(
    Guid AircraftId, string RegistrationNumber, SensorForecastHorizon Horizon, SensorForecastStatus Status, string? Reason,
    IReadOnlyList<SensorForecastHorizon> SupportedHorizons, string? ModelName, string? ModelVersion,
    DateTime? GeneratedAtUtc, DateTime? DataAsOfUtc, IReadOnlyList<SensorForecastPointDto> Points,
    IReadOnlyList<string>? SupportedMetrics = null,
    // Added 2026-10-28 (additive): set when this horizon is the opening part of a longer validated forecast.
    SensorForecastHorizon? SlicedFromHorizon = null);
