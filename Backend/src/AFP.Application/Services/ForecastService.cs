using System.Text.Json;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Contracts;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Domain.Rules;

namespace AFP.Application.Services;

public interface IForecastService
{
    /// <summary>The future sensor forecast for an aircraft and horizon, or an explicit NotAvailable with the reason.</summary>
    Task<SensorForecastDto> GetSensorForecastAsync(Guid aircraftId, SensorForecastHorizon? horizon, CancellationToken ct);
}

/// <summary>
/// Serves stored forecasts of the ACTIVE <see cref="ModelType.SensorForecast"/> model only (Active means approved -
/// activation requires approval). A horizon is supported only if that model's backtest validated it
/// (<c>metrics.supported_horizons</c>); a metric outside the validated list is never returned. A shorter horizon the
/// model did not validate on its own is served as the opening hours of the shortest validated longer forecast (user
/// decision 2026-10-28), flagged with <c>SlicedFromHorizon</c>. Anything else - no active forecaster, no validated
/// horizon covering it, nothing generated yet - is NotAvailable with the reason. Recorded telemetry is never
/// extrapolated or relabelled as a forecast.
/// </summary>
public sealed class ForecastService(
    IAircraftRepository aircraft, IModelVersionRepository models, ISensorForecastRepository forecasts) : IForecastService
{
    public const string NoForecastModelReason =
        "No approved sensor-forecasting model is active. A forecaster must pass its backtest validation and be approved "
        + "and activated by a Data Engineer before forecasts are shown.";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<SensorForecastDto> GetSensorForecastAsync(Guid aircraftId, SensorForecastHorizon? horizon, CancellationToken ct)
    {
        if (horizon is not { } h || !Enum.IsDefined(h))
            throw new ValidationException("horizon", $"Choose a horizon: {string.Join(", ", Enum.GetNames<SensorForecastHorizon>())}.");
        var a = await aircraft.GetByIdAsync(aircraftId, ct) ?? throw new NotFoundException("Aircraft", aircraftId);

        var model = await models.GetActiveAsync(ModelType.SensorForecast, ct);
        if (model is null)
            return NotAvailable(a, h, NoForecastModelReason, [], null);

        var validated = SupportedHorizons(model.Metrics);
        var servable = Enum.GetValues<SensorForecastHorizon>().Where(x => SourceHorizon(validated, x) is not null).ToList();
        if (SourceHorizon(validated, h) is not { } source)
            return NotAvailable(a, h, $"The active forecaster ({model.ModelName} {model.Version}) did not pass validation for this horizon.",
                servable, model);
        var metrics = validated[source];

        var stored = await forecasts.GetLatestAsync(a.Id, model.Id, source, ct);
        if (stored is null)
            return NotAvailable(a, h, "No forecast has been generated yet for this aircraft and horizon.", servable, model);

        var end = stored.DataAsOfUtc.AddHours(AnomalyRules.HorizonHours(h));
        var points = (JsonSerializer.Deserialize<List<SensorForecastPointDto>>(stored.Points, Web) ?? [])
            .Where(p => p.TimestampUtc > stored.DataAsOfUtc && p.TimestampUtc <= end)   // future of the cutoff, within the horizon
            .Select(p => KeepOnly(p, metrics))
            .OrderBy(p => p.TimestampUtc)
            .ToList();
        return new SensorForecastDto(a.Id, a.RegistrationNumber, h,
            points.Count > 0 ? SensorForecastStatus.Available : SensorForecastStatus.NotAvailable,
            points.Count > 0 ? null : "The stored forecast contains no future points.",
            servable, model.ModelName, model.Version, stored.GeneratedAtUtc, stored.DataAsOfUtc, points,
            metrics.ToList(), source == h ? null : source);
    }

    /// <summary>The horizon itself when validated, else the shortest validated longer horizon; null when none covers it.</summary>
    private static SensorForecastHorizon? SourceHorizon(Dictionary<SensorForecastHorizon, HashSet<string>> validated, SensorForecastHorizon h) =>
        validated.Keys.Where(v => AnomalyRules.HorizonHours(v) >= AnomalyRules.HorizonHours(h))
            .OrderBy(AnomalyRules.HorizonHours).Cast<SensorForecastHorizon?>().FirstOrDefault();

    /// <summary><c>metrics.supported_horizons</c>: { "OneDay": ["temperatureC", ...], ... } - unknown names are ignored.</summary>
    private static Dictionary<SensorForecastHorizon, HashSet<string>> SupportedHorizons(string metricsJson)
    {
        var result = new Dictionary<SensorForecastHorizon, HashSet<string>>();
        if (Json.Find(Json.Parse(metricsJson), "supported_horizons") is not { ValueKind: JsonValueKind.Object } map) return result;
        foreach (var entry in map.EnumerateObject())
            if (Enum.TryParse<SensorForecastHorizon>(entry.Name, out var h) && entry.Value.ValueKind == JsonValueKind.Array)
            {
                var names = entry.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToHashSet();
                if (names.Count > 0) result[h] = names;
            }
        return result;
    }

    /// <summary>Defence in depth: a value for a metric the model is not validated for is dropped, whatever was stored.</summary>
    private static SensorForecastPointDto KeepOnly(SensorForecastPointDto p, HashSet<string> m) => new(
        p.TimestampUtc,
        m.Contains("temperatureC") ? p.TemperatureC : null, m.Contains("pressurePsi") ? p.PressurePsi : null,
        m.Contains("vibrationIndex") ? p.VibrationIndex : null,
        m.Contains("temperatureC") ? p.TemperatureCLow : null, m.Contains("temperatureC") ? p.TemperatureCHigh : null,
        m.Contains("pressurePsi") ? p.PressurePsiLow : null, m.Contains("pressurePsi") ? p.PressurePsiHigh : null,
        m.Contains("vibrationIndex") ? p.VibrationIndexLow : null, m.Contains("vibrationIndex") ? p.VibrationIndexHigh : null);

    private static SensorForecastDto NotAvailable(Aircraft a, SensorForecastHorizon h, string reason,
        IReadOnlyList<SensorForecastHorizon> supported, ModelVersion? model) =>
        new(a.Id, a.RegistrationNumber, h, SensorForecastStatus.NotAvailable, reason, supported,
            model?.ModelName, model?.Version, null, null, []);
}
