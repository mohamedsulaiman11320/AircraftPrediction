using AFP.Application.Common;
using AFP.Application.Telemetry;

namespace AFP.UnitTests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("short1!A", "at least 12")]
    [InlineData("alllowercase-1234", "upper-case")]
    [InlineData("ALLUPPERCASE-1234", "lower-case")]
    [InlineData("NoDigitsHere-abc", "digit")]
    [InlineData("NoSymbols12345abc", "symbol")]
    public void Weak_passwords_are_rejected(string password, string expectedFragment) =>
        Assert.Contains(PasswordPolicy.Check(password), p => p.Contains(expectedFragment));

    [Fact]
    public void Strong_password_passes() => Assert.Empty(PasswordPolicy.Check("Correct-Horse-9!"));

    [Fact]
    public void Validator_collects_errors_by_camel_case_field()
    {
        var ex = Assert.Throws<ValidationException>(() => new Validator()
            .Email("Email", "not-an-email")
            .Password("Password", "Correct-Horse-9!", "different", "ConfirmPassword")
            .ThrowIfInvalid());
        Assert.Contains("email", ex.Errors.Keys);
        Assert.Contains("confirmPassword", ex.Errors.Keys);
        Assert.DoesNotContain("password", ex.Errors.Keys);
    }
}

public class TelemetryCsvTests
{
    private const string Header = "aircraft_registration,sensor_id,timestamp_utc,temperature,temperature_unit,pressure,pressure_unit,data_source";
    private static readonly DateTime Latest = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TelemetryRowValidator Validator() => TelemetryRowValidator.FromHeader(Header, Latest).Validator!;

    [Fact]
    public void Header_missing_required_columns_is_reported()
    {
        var (validator, missing) = TelemetryRowValidator.FromHeader("aircraft_registration,timestamp_utc,temperature", Latest);
        Assert.Null(validator);
        Assert.Equal(["temperature_unit", "pressure", "pressure_unit"], missing);
    }

    [Fact]
    public void Header_is_case_insensitive_and_tolerates_bom() =>
        Assert.NotNull(TelemetryRowValidator.FromHeader("﻿" + Header.ToUpperInvariant(), Latest).Validator);

    [Fact]
    public void Valid_row_is_normalised_to_celsius_and_kpa()
    {
        var result = Validator().Validate(2, "demo-a01,S1,2026-08-01T10:00:00Z,212,F,14.5,psi,ACARS");
        Assert.Empty(result.Errors);
        var row = result.Row!;
        Assert.Equal("DEMO-A01", row.Registration);
        Assert.Equal(100, row.TemperatureC!.Value, 6);
        Assert.Equal(99.974, row.PressureKpa!.Value, 2);
        Assert.Equal("F", row.TemperatureUnit);
        Assert.Equal("psi", row.PressureUnit);
        Assert.Empty(row.QualityFlags);
    }

    [Fact]
    public void Offset_timestamp_is_converted_to_utc()
    {
        var row = Validator().Validate(2, "DEMO-A01,S1,2026-08-01T12:00:00+02:00,20,C,101,kPa,X").Row!;
        Assert.Equal(new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc), row.RecordedAtUtc);
        Assert.Equal(DateTimeKind.Utc, row.RecordedAtUtc.Kind);
    }

    [Theory]
    [InlineData("DEMO-A01,S1,2026-08-01 10:00:00,20,C,101,kPa,X", "UTC offset")]
    [InlineData("DEMO-A01,S1,2027-01-01T00:00:00Z,20,C,101,kPa,X", "future")]
    [InlineData("DEMO-A01,S1,2026-08-01T10:00:00Z,-300,C,101,kPa,X", "absolute zero")]
    [InlineData("DEMO-A01,S1,2026-08-01T10:00:00Z,20,C,-5,kPa,X", "negative")]
    [InlineData("DEMO-A01,S1,2026-08-01T10:00:00Z,20,,101,kPa,X", "temperature_unit is required")]
    [InlineData("DEMO-A01,S1,2026-08-01T10:00:00Z,20,R,101,kPa,X", "not supported")]
    [InlineData("DEMO-A01,S1,2026-08-01T10:00:00Z,abc,C,101,kPa,X", "not a number")]
    [InlineData("DEMO-A01,S1,2026-08-01T10:00:00Z,NaN,C,101,kPa,X", "not a number")]
    [InlineData("DEMO-A01,S1,2026-08-01T10:00:00Z,,C,,kPa,X", "both temperature and pressure are missing")]
    [InlineData(",S1,2026-08-01T10:00:00Z,20,C,101,kPa,X", "aircraft_registration is required")]
    public void Invalid_rows_are_rejected_with_reason(string line, string expected)
    {
        var result = Validator().Validate(2, line);
        Assert.Null(result.Row);
        Assert.Contains(result.Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Single_missing_measurement_is_accepted_as_suspect()
    {
        var row = Validator().Validate(2, "DEMO-A01,S1,2026-08-01T10:00:00Z,20,C,,,X").Row!;
        Assert.Contains(QualityFlags.MissingPressure, row.QualityFlags);
        Assert.Null(row.PressureKpa);
    }

    [Fact]
    public void Kelvin_and_other_pressure_units_convert()
    {
        Assert.Equal(0, TelemetryRowValidator.ToCelsius(273.15, "K")!.Value, 6);
        Assert.Equal(101.325, TelemetryRowValidator.ToKpa(1013.25, "hPa")!.Value, 6);
        Assert.Equal(100, TelemetryRowValidator.ToKpa(1, "bar")!.Value, 6);
    }

    [Fact]
    public void Csv_splitter_handles_quotes()
    {
        Assert.Equal(["a", "b,c", "d\"e", ""], CsvLine.Split("a,\"b,c\",\"d\"\"e\","));
    }
}
