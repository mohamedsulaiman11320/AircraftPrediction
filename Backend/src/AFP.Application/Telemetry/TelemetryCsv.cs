using System.Globalization;
using System.Text;

namespace AFP.Application.Telemetry;

public static class TelemetryColumns
{
    public const string Registration = "aircraft_registration";
    public const string Timestamp = "timestamp_utc";
    public const string Temperature = "temperature";
    public const string TemperatureUnit = "temperature_unit";
    public const string Pressure = "pressure";
    public const string PressureUnit = "pressure_unit";
    public const string SensorId = "sensor_id";
    public const string DataSource = "data_source";

    public static readonly string[] Required = [Registration, Timestamp, Temperature, TemperatureUnit, Pressure, PressureUnit];
}

public static class QualityFlags
{
    public const string MissingTemperature = "MISSING_TEMPERATURE";
    public const string MissingPressure = "MISSING_PRESSURE";
}

/// <summary>A row that passed validation, with values normalised to °C and kPa.</summary>
public sealed record ParsedTelemetryRow(
    int RowNumber, string Registration, string? SensorId, DateTime RecordedAtUtc,
    double? TemperatureValue, string? TemperatureUnit, double? TemperatureC,
    double? PressureValue, string? PressureUnit, double? PressureKpa,
    string DataSource, IReadOnlyList<string> QualityFlags);

public sealed record RowValidationResult(ParsedTelemetryRow? Row, IReadOnlyList<string> Errors);

/// <summary>
/// Validates telemetry CSV rows. Only physical-plausibility checks are applied to measurements;
/// engineering limits require approved component specifications and are not invented here.
/// </summary>
public sealed class TelemetryRowValidator
{
    public const double AbsoluteZeroC = -273.15;

    private readonly Dictionary<string, int> _columns;
    private readonly DateTime _latestAllowedUtc;
    private readonly string _defaultDataSource;

    private TelemetryRowValidator(Dictionary<string, int> columns, DateTime latestAllowedUtc, string defaultDataSource)
    {
        _columns = columns;
        _latestAllowedUtc = latestAllowedUtc;
        _defaultDataSource = defaultDataSource;
    }

    /// <summary>Parses the header; returns the missing required columns when the header is unusable.</summary>
    public static (TelemetryRowValidator? Validator, IReadOnlyList<string> MissingColumns) FromHeader(
        string headerLine, DateTime latestAllowedUtc, string defaultDataSource = "CSV_IMPORT")
    {
        var names = CsvLine.Split(headerLine.TrimStart('﻿'));
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i].Trim();
            if (name.Length > 0) columns.TryAdd(name, i);
        }
        var missing = TelemetryColumns.Required.Where(c => !columns.ContainsKey(c)).ToList();
        return missing.Count > 0
            ? (null, missing)
            : (new TelemetryRowValidator(columns, latestAllowedUtc, defaultDataSource), missing);
    }

    public RowValidationResult Validate(int rowNumber, string line)
    {
        var errors = new List<string>();
        var fields = CsvLine.Split(line);
        string? Get(string column) =>
            _columns.TryGetValue(column, out var idx) && idx < fields.Count && !string.IsNullOrWhiteSpace(fields[idx])
                ? fields[idx].Trim()
                : null;

        var registration = Get(TelemetryColumns.Registration)?.ToUpperInvariant();
        if (registration is null) errors.Add("aircraft_registration is required");
        else if (registration.Length > 16) errors.Add("aircraft_registration is longer than 16 characters");

        var sensorId = Get(TelemetryColumns.SensorId);
        if (sensorId is { Length: > 64 }) errors.Add("sensor_id is longer than 64 characters");

        var timestamp = ParseTimestamp(Get(TelemetryColumns.Timestamp), errors);

        var flags = new List<string>();
        var (tempValue, tempUnit, tempC) = ParseMeasurement(
            Get(TelemetryColumns.Temperature), Get(TelemetryColumns.TemperatureUnit), "temperature", ToCelsius, errors);
        var (pressValue, pressUnit, pressKpa) = ParseMeasurement(
            Get(TelemetryColumns.Pressure), Get(TelemetryColumns.PressureUnit), "pressure", ToKpa, errors);

        if (tempC is < AbsoluteZeroC) errors.Add("temperature is below absolute zero");
        if (pressKpa is < 0) errors.Add("pressure is negative (absolute pressure expected)");

        if (tempValue is null && pressValue is null && !errors.Any(e => e.Contains("not a number")))
            errors.Add("both temperature and pressure are missing");
        if (tempValue is null) flags.Add(QualityFlags.MissingTemperature);
        if (pressValue is null) flags.Add(QualityFlags.MissingPressure);

        var dataSource = Get(TelemetryColumns.DataSource) ?? _defaultDataSource;
        if (dataSource.Length > 64) errors.Add("data_source is longer than 64 characters");

        if (errors.Count > 0) return new RowValidationResult(null, errors);
        return new RowValidationResult(new ParsedTelemetryRow(
            rowNumber, registration!, sensorId, timestamp!.Value, tempValue, tempUnit, tempC, pressValue, pressUnit, pressKpa,
            dataSource, flags), errors);
    }

    private DateTime? ParseTimestamp(string? raw, List<string> errors)
    {
        if (raw is null)
        {
            errors.Add("timestamp_utc is required");
            return null;
        }
        // Require an explicit offset or 'Z': naive timestamps are ambiguous and are rejected.
        var hasOffset = raw.EndsWith('Z') || raw.EndsWith('z') ||
                        (raw.Length > 6 && (raw[^6] == '+' || raw[^6] == '-') && raw[^3] == ':');
        if (!hasOffset || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            errors.Add($"timestamp_utc '{raw}' is not ISO-8601 with a UTC offset");
            return null;
        }
        var utc = parsed.UtcDateTime;
        if (utc > _latestAllowedUtc) errors.Add("timestamp_utc is in the future");
        if (utc.Year < 1990) errors.Add("timestamp_utc is implausibly old");
        return utc;
    }

    private static (double? Value, string? Unit, double? Normalised) ParseMeasurement(
        string? rawValue, string? rawUnit, string name, Func<double, string, double?> convert, List<string> errors)
    {
        if (rawValue is null) return (null, rawUnit, null);
        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            errors.Add($"{name} '{rawValue}' is not a number");
            return (null, null, null);
        }
        if (rawUnit is null)
        {
            errors.Add($"{name}_unit is required when {name} is present");
            return (value, null, null);
        }
        var normalised = convert(value, rawUnit);
        if (normalised is null)
        {
            errors.Add($"{name}_unit '{rawUnit}' is not supported");
            return (value, rawUnit, null);
        }
        return (value, CanonicalUnit(rawUnit), normalised);
    }

    public static double? ToCelsius(double value, string unit) => unit.Trim().ToUpperInvariant() switch
    {
        "C" or "°C" or "DEGC" => value,
        "F" or "°F" or "DEGF" => (value - 32d) * 5d / 9d,
        "K" => value + AbsoluteZeroC,
        _ => null,
    };

    private const double KpaPerPsi = 6.894757293168;

    public static double? ToKpa(double value, string unit) => unit.Trim().ToUpperInvariant() switch
    {
        "KPA" => value,
        "HPA" => value / 10d,
        "PSI" => value * KpaPerPsi,
        "BAR" => value * 100d,
        _ => null,
    };

    /// <summary>
    /// Converts canonical kPa back to PSI. Readings are stored in kPa, but the recorded engine-health business
    /// rules (BR-01) are written in PSI, so comparisons and the values shown to users are converted here.
    /// </summary>
    public static double? KpaToPsi(double? kpa) => kpa is null ? null : kpa.Value / KpaPerPsi;

    private static string CanonicalUnit(string unit) => unit.Trim().ToUpperInvariant() switch
    {
        "C" or "°C" or "DEGC" => "C",
        "F" or "°F" or "DEGF" => "F",
        "K" => "K",
        "KPA" => "kPa",
        "HPA" => "hPa",
        "PSI" => "psi",
        "BAR" => "bar",
        var other => other,
    };
}

/// <summary>Minimal RFC 4180 field splitter for a single physical line (quoted fields and escaped quotes).</summary>
public static class CsvLine
{
    public static IReadOnlyList<string> Split(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
