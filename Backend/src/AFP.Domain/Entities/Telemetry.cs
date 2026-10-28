using AFP.Domain.Enums;

namespace AFP.Domain.Entities;

public class TelemetryImport : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string FileSha256 { get; set; } = string.Empty;
    /// <summary>
    /// Where the original payload was stored: the MinIO Bronze object key for raw JSON uploads,
    /// or the local staging path for CSV imports. Shown to the Data Engineer for lineage.
    /// </summary>
    public string StoredPath { get; set; } = string.Empty;
    /// <summary>Size of the uploaded file as received, in bytes.</summary>
    public long FileSizeBytes { get; set; }
    /// <summary>
    /// Spark micro-batch that produced this import's Silver/Gold Parquet, written by the ETL.
    /// Lake output is partitioned by <c>batch_id</c>, not by import, and one batch can carry several
    /// files — so a partition may only be deleted when no other import shares this batch id.
    /// Null for imports processed before this was recorded: ownership is then unknown, and the
    /// partition must be preserved rather than guessed at.
    /// </summary>
    public long? LakeBatchId { get; set; }
    public TelemetryImportStatus Status { get; set; } = TelemetryImportStatus.Queued;
    public TelemetryImportSource Source { get; set; } = TelemetryImportSource.Csv;
    public int TotalRows { get; set; }
    public int AcceptedRows { get; set; }
    public int RejectedRows { get; set; }
    public int DuplicateRows { get; set; }
    public int WarningCount { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string? ErrorMessage { get; set; }
    /// <summary>Null when the ETL discovered an object placed directly in MinIO.</summary>
    public long? RequestedByUserId { get; set; }
    public AppUser? RequestedBy { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public class TelemetryRejectedRow : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public long Id { get; set; }
    public Guid ImportId { get; set; }
    public int RowNumber { get; set; }
    public string RawLine { get; set; } = string.Empty;
    public string Reasons { get; set; } = string.Empty;
}

public class TelemetryRecord : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public long Id { get; set; }
    public Guid AircraftId { get; set; }
    public Aircraft? Aircraft { get; set; }
    public string? SensorId { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public double? TemperatureValue { get; set; }
    public string? TemperatureUnit { get; set; }
    public double? TemperatureC { get; set; }
    public double? PressureValue { get; set; }
    public string? PressureUnit { get; set; }
    public double? PressureKpa { get; set; }
    public string DataSource { get; set; } = string.Empty;
    public QualityStatus QualityStatus { get; set; }
    public string? QualityFlags { get; set; }
    public DateTime IngestedAtUtc { get; set; }
    public Guid? ImportId { get; set; }
    public int? SourceRowNumber { get; set; }

    /// <summary>Identifier assigned by the source system (raw JSON telemetry_id); unique per data source.</summary>
    public string? SourceRecordId { get; set; }
    public double? VibrationIndex { get; set; }

    /// <summary>Source operational flag (e.g. NORMAL/CRITICAL). Threshold-derived: never a confirmed failure label.</summary>
    public string? SourceStatusFlag { get; set; }
}

/// <summary>
/// An engineering limit. Rows may only exist when backed by a documented component specification
/// and an approver; the platform ships with none.
/// </summary>
public class EngineeringThreshold : ILifecycleEntity
{
    public LifecycleStatus StatusId { get; set; } = LifecycleStatus.Active;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ComponentType { get; set; } = string.Empty;
    public MeasurementKind Measurement { get; set; }
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string SpecificationReference { get; set; } = string.Empty;
    public long ApprovedByUserId { get; set; }
    public DateTime ApprovedAtUtc { get; set; }
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
