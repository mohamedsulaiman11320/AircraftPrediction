using AFP.Domain.Enums;
namespace AFP.Application.Common;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    public int MaxFailedLoginAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int PasswordResetTokenMinutes { get; set; } = 30;

    /// <summary>Lifetime of OAuth 2.0 refresh tokens (rotated on every use).</summary>
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>Frontend page that receives the reset token, e.g. http://localhost:5173/reset-password.</summary>
    public string PasswordResetUrl { get; set; } = "http://localhost:5173/reset-password";
}

public sealed class PredictionOptions
{
    public const string Section = "Prediction";

    /// <summary>Business prediction horizon in days (configurable; initial target 30).</summary>
    public int HorizonDays { get; set; } = 30;
}

/// <summary>MinIO bronze storage used by the Spark ETL (keys MINIO_* in the root .env).</summary>
public sealed class RawTelemetryStorageOptions
{
    public string Endpoint { get; set; } = "http://localhost:9000";
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string Bucket { get; set; } = "bronze";
    public string Prefix { get; set; } = "telemetry/";
    public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>Required keys of every raw JSON telemetry record (values may be null; the ETL quarantines them).</summary>
    public string[] RequiredRecordKeys { get; set; } = ["telemetry_id", "tail_number", "timestamp_epoch", "metrics"];

    /// <summary>
    /// Buckets an import deletion is allowed to remove objects from. Anything outside this list is refused,
    /// so a corrupted or hostile location can never reach another application's storage.
    /// </summary>
    public string[] DeletableBuckets { get; set; } = ["bronze", "silver", "gold"];

    /// <summary>Lake layout used to resolve a batch partition owned exclusively by one import.</summary>
    public string SilverBucket { get; set; } = "silver";
    public string SilverPrefix { get; set; } = "telemetry_clean";
    public string GoldBucket { get; set; } = "gold";
    public string GoldPrefix { get; set; } = "aircraft_features";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);
}

/// <summary>
/// Automatic work-order creation from anomalies. Deliberately conservative: the BRD defines dispatcher-raised
/// inspection flags but no automatic ticket rule, so the threshold and the per-run cap are configuration
/// rather than hard-coded policy, and the feature can be switched off outright.
/// </summary>
public sealed class AnomalyWorkOrderOptions
{
    public const string Section = "AnomalyWorkOrders";

    public bool Enabled { get; set; } = true;

    /// <summary>Only anomalies at or above this severity raise a ticket.</summary>
    public AnomalySeverity MinimumSeverity { get; set; } = AnomalySeverity.High;

    /// <summary>Cap per run so a historical backlog cannot flood the dispatcher's queue at once.</summary>
    public int MaxPerRun { get; set; } = 25;

    /// <summary>Creator recorded when the generator runs without a signed-in user (scheduler/worker).</summary>
    public long SystemUserId { get; set; } = 1;

    /// <summary>
    /// Only anomalies observed within this many days raise a ticket, so months of historical scoring are not turned
    /// into new work in one go (they stay visible on the Anomalies screen). 0 = no age limit.
    /// </summary>
    public int MaxAgeDays { get; set; } = 7;
}

public sealed class TelemetryImportOptions
{
    public const string Section = "TelemetryImport";

    public long MaxFileBytes { get; set; } = 200L * 1024 * 1024;
    public int BatchSize { get; set; } = 5000;
    public int MaxStoredRejectedRows { get; set; } = 10000;

    /// <summary>Directory where original CSV files are preserved. Defaults to %LOCALAPPDATA%\AircraftFailurePrediction\imports.</summary>
    public string? StorageDirectory { get; set; }

    /// <summary>Tolerance for timestamps slightly ahead of the server clock.</summary>
    public int MaxFutureSkewMinutes { get; set; } = 5;
}
