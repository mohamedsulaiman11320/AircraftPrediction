using AFP.Domain.Enums;
using System.Text.Json;
using AFP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AFP.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Status> Statuses => Set<Status>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRoleAssignment> UserRoles => Set<UserRoleAssignment>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();
    public DbSet<AuthAudit> AuthAudits => Set<AuthAudit>();
    public DbSet<ActionAudit> ActionAudits => Set<ActionAudit>();
    public DbSet<Aircraft> Aircraft => Set<Aircraft>();
    public DbSet<InspectionFlag> InspectionFlags => Set<InspectionFlag>();
    public DbSet<MaintenanceLog> MaintenanceLogs => Set<MaintenanceLog>();
    public DbSet<MaintenanceWorkOrder> WorkOrders => Set<MaintenanceWorkOrder>();
    public DbSet<WorkOrderReport> WorkOrderReports => Set<WorkOrderReport>();
    public DbSet<MaintenanceActionCode> MaintenanceActionCodes => Set<MaintenanceActionCode>();
    public DbSet<TelemetryImport> TelemetryImports => Set<TelemetryImport>();
    public DbSet<TelemetryRejectedRow> TelemetryRejectedRows => Set<TelemetryRejectedRow>();
    public DbSet<TelemetryRecord> TelemetryRecords => Set<TelemetryRecord>();
    public DbSet<EngineeringThreshold> EngineeringThresholds => Set<EngineeringThreshold>();
    public DbSet<RiskStatus> RiskStatuses => Set<RiskStatus>();
    public DbSet<HealthThreshold> HealthThresholds => Set<HealthThreshold>();
    public DbSet<ModelVersion> ModelVersions => Set<ModelVersion>();
    public DbSet<AircraftAnomalyResult> AnomalyResults => Set<AircraftAnomalyResult>();
    public DbSet<AircraftRiskPrediction> RiskPredictions => Set<AircraftRiskPrediction>();
    public DbSet<SensorForecast> SensorForecasts => Set<SensorForecast>();
    public DbSet<PipelineRun> PipelineRuns => Set<PipelineRun>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Enums are stored as their PascalCase names so that SQL consumers (the ML package) read meaningful values.
        foreach (var enumType in typeof(Domain.Enums.UserRole).Assembly.GetTypes()
                     .Where(t => t.IsEnum && t.Namespace == "AFP.Domain.Enums" && t != typeof(LifecycleStatus) && t != typeof(UserRole)))
        {
            builder.Properties(enumType).HaveConversion<string>().HaveMaxLength(32);
        }
        // Lifecycle status is an integer FK to the seeded status table, not a string.
        builder.Properties<LifecycleStatus>().HaveConversion<int>();
        // Roles are an integer FK to the seeded role table.
        builder.Properties<UserRole>().HaveConversion<int>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        ConfigureLifecycle(b);
        ConfigureSecurity(b);
        ConfigureFleet(b);
        ConfigureTelemetry(b);
        ConfigureHealthThresholds(b);
        ConfigureMachineLearning(b);
        ConfigureLifecycleForeignKeys(b);
    }

    private static void ConfigureLifecycle(ModelBuilder b)
    {
        // Rows are supplied only by the reference-data seed (tools/db/seed); IDs are stable, never generated.
        b.Entity<Status>(e =>
        {
            e.ToTable("status");
            e.HasKey(x => x.StatusId);
            e.Property(x => x.StatusId).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Description).HasMaxLength(256);
        });
    }

    /// <summary>
    /// Every lifecycle entity gets status_id (int, NOT NULL, default Active) with a RESTRICT FK to status.
    /// The sentinel makes EF always send the value, so an explicit Inactive (0) is never replaced by the default.
    /// </summary>
    private static void ConfigureLifecycleForeignKeys(ModelBuilder b)
    {
        foreach (var entityType in b.Model.GetEntityTypes().Where(t => typeof(ILifecycleEntity).IsAssignableFrom(t.ClrType)).ToList())
        {
            var entity = b.Entity(entityType.ClrType);
            entity.Property(nameof(ILifecycleEntity.StatusId))
                .IsRequired()
                .HasDefaultValue(LifecycleStatus.Active)
                .HasSentinel((LifecycleStatus)(-1));
            entity.HasOne(typeof(Status)).WithMany().HasForeignKey(nameof(ILifecycleEntity.StatusId)).OnDelete(DeleteBehavior.Restrict);
        }
    }

    private static void ConfigureSecurity(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.ToTable("app_user");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("user_id").UseIdentityByDefaultColumn();
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
            e.Property(x => x.SecurityStamp).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisabledReason).HasMaxLength(500);
            e.Ignore(x => x.ActiveRoles);
            e.HasIndex(x => x.Status);
            e.HasMany(x => x.UserRoles).WithOne(r => r.User).HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        // Rows come only from tools/db/seed/csv/role.csv; IDs mirror the UserRole enum and are never generated.
        b.Entity<Role>(e =>
        {
            e.ToTable("role");
            e.HasKey(x => x.RoleId);
            e.Property(x => x.RoleId).ValueGeneratedNever();
            e.Property(x => x.RoleName).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.RoleName).IsUnique();
        });

        b.Entity<UserRoleAssignment>(e =>
        {
            e.ToTable("user_role");
            e.HasKey(x => x.UserRoleId);
            e.Property(x => x.UserRoleId).UseIdentityByDefaultColumn();
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.RevokedByUserId).OnDelete(DeleteBehavior.Restrict);
            // At most one ACTIVE assignment per user and role; revoked (Inactive) rows are kept as history.
            e.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique().HasFilter("status_id = 1").HasDatabaseName("ux_user_role_active");
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_token");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => new { x.UserId, x.ExpiresAtUtc });
            e.Property(x => x.RevokedReason).HasMaxLength(64);
            e.Property(x => x.CreatedByIp).HasMaxLength(64);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PasswordResetToken>(e =>
        {
            e.ToTable("password_reset_token");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.RequestedIp).HasMaxLength(64);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RevokedToken>(e =>
        {
            e.ToTable("revoked_token");
            e.HasKey(x => x.Jti);
            e.Property(x => x.Jti).HasMaxLength(64);
            e.HasIndex(x => x.ExpiresAtUtc);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuthAudit>(e =>
        {
            e.ToTable("auth_audit");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.HasIndex(x => x.OccurredAtUtc);
            e.HasIndex(x => x.Email);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ActionAudit>(e =>
        {
            e.ToTable("action_audit");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.OccurredAtUtc);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureFleet(ModelBuilder b)
    {
        b.Entity<Aircraft>(e =>
        {
            e.ToTable("aircraft");
            e.HasKey(x => x.Id);
            e.Property(x => x.RegistrationNumber).HasMaxLength(16).IsRequired();
            e.HasIndex(x => x.RegistrationNumber).IsUnique();
            e.Property(x => x.Model).HasMaxLength(64).IsRequired();
            e.Property(x => x.Manufacturer).HasMaxLength(64).IsRequired();
            e.Property(x => x.FleetGroup).HasMaxLength(64).IsRequired();
            e.Property(x => x.SerialNumber).HasMaxLength(64);
            e.Property(x => x.StatusReason).HasMaxLength(500);
            e.HasIndex(x => x.OperationalStatus);
            e.HasIndex(x => x.FleetGroup);
        });

        b.Entity<InspectionFlag>(e =>
        {
            e.ToTable("inspection_flag");
            e.HasKey(x => x.Id);
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ClearanceNotes).HasMaxLength(2000);
            e.HasIndex(x => new { x.AircraftId, x.IsActive });
            e.HasOne(x => x.Aircraft).WithMany().HasForeignKey(x => x.AircraftId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.RaisedBy).WithMany().HasForeignKey(x => x.RaisedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ClearedBy).WithMany().HasForeignKey(x => x.ClearedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AircraftAnomalyResult>().WithMany().HasForeignKey(x => x.AnomalyId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<AircraftRiskPrediction>().WithMany().HasForeignKey(x => x.PredictionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<MaintenanceWorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<MaintenanceLog>(e =>
        {
            e.ToTable("maintenance_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.ComponentId).HasMaxLength(64);
            e.Property(x => x.Description).HasMaxLength(4000).IsRequired();
            e.HasIndex(x => new { x.AircraftId, x.OccurredAtUtc });
            e.HasIndex(x => x.EventType);
            e.HasOne(x => x.Aircraft).WithMany().HasForeignKey(x => x.AircraftId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MaintenanceWorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.RecordedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<MaintenanceWorkOrder>(e =>
        {
            e.ToTable("maintenance_work_order");
            e.HasKey(x => x.Id);
            e.Property(x => x.WorkOrderNumber).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.WorkOrderNumber).IsUnique();
            e.Property(x => x.ComponentId).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000).IsRequired();
            e.Property(x => x.InspectionFindings).HasMaxLength(4000);
            e.Property(x => x.CorrectiveAction).HasMaxLength(4000);
            e.Property(x => x.ResolutionNotes).HasMaxLength(8000);
            e.Property(x => x.DispatcherReviewNotes).HasMaxLength(4000);
            e.Property(x => x.Source).HasMaxLength(16).HasDefaultValue(WorkOrderSource.Manual).HasSentinel((WorkOrderSource)(-1));
            e.Property(x => x.DiagnosisStatus).HasMaxLength(16).HasDefaultValue(DiagnosisStatus.NotGenerated).HasSentinel((DiagnosisStatus)(-1));
            e.Property(x => x.DiagnosisText).HasMaxLength(8000);
            JsonbOptional(e.Property(x => x.DiagnosisEvidence));
            e.Property(x => x.DiagnosisMethod).HasMaxLength(64);
            e.Property(x => x.DiagnosisRuleVersion).HasMaxLength(200);
            e.Property(x => x.DiagnosisError).HasMaxLength(1000);
            // Database-backed idempotency: at most ONE work order per anomaly, whatever runs concurrently.
            e.HasIndex(x => x.AnomalyId).IsUnique().HasFilter("anomaly_id IS NOT NULL");
            e.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.AircraftId, x.Status });
            e.HasIndex(x => x.AssignedTechnicianId);
            e.HasOne(x => x.Aircraft).WithMany().HasForeignKey(x => x.AircraftId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Anomaly).WithMany().HasForeignKey(x => x.AnomalyId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Prediction).WithMany().HasForeignKey(x => x.PredictionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.AssignedTechnician).WithMany().HasForeignKey(x => x.AssignedTechnicianId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Reports).WithOne(r => r.WorkOrder).HasForeignKey(r => r.WorkOrderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MaintenanceActionCode>(e =>
        {
            e.ToTable("maintenance_action_code");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.Description).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.DisplayOrder).IsUnique();
        });

        b.Entity<WorkOrderReport>(e =>
        {
            e.ToTable("work_order_report");
            e.HasKey(x => x.Id);
            e.Property(x => x.InspectionFindings).HasMaxLength(4000);
            e.Property(x => x.CorrectiveAction).HasMaxLength(4000).IsRequired();
            e.Property(x => x.ResolutionNotes).HasMaxLength(8000).IsRequired();
            // One report per (ticket, submission): a re-report after reassignment is the next sequence number.
            e.HasIndex(x => new { x.WorkOrderId, x.Sequence }).IsUnique();
            e.Property(x => x.ActionCode).HasMaxLength(32);
            e.HasOne<MaintenanceActionCode>().WithMany().HasForeignKey(x => x.ActionCode).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.TechnicianUserId);
            e.HasOne(x => x.Technician).WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureTelemetry(ModelBuilder b)
    {
        b.Entity<TelemetryImport>(e =>
        {
            e.ToTable("telemetry_import");
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.FileSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            e.HasIndex(x => x.FileSha256).IsUnique();
            e.Property(x => x.StoredPath).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ErrorMessage).HasMaxLength(1000);
            e.Property(x => x.Warnings).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>(),
                new ValueComparer<List<string>>((l, r) => l!.SequenceEqual(r!), l => l.Aggregate(0, (h, s) => HashCode.Combine(h, s)), l => l.ToList()));
            e.HasIndex(x => x.RequestedAtUtc);
            e.HasOne(x => x.RequestedBy).WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TelemetryRejectedRow>(e =>
        {
            e.ToTable("telemetry_rejected_row");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
            e.Property(x => x.RawLine).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Reasons).HasMaxLength(1000).IsRequired();
            e.HasIndex(x => new { x.ImportId, x.RowNumber });
            e.HasOne<TelemetryImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TelemetryRecord>(e =>
        {
            e.ToTable("telemetry_record");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
            e.Property(x => x.SensorId).HasMaxLength(64);
            e.Property(x => x.TemperatureUnit).HasMaxLength(8);
            e.Property(x => x.PressureUnit).HasMaxLength(8);
            e.Property(x => x.DataSource).HasMaxLength(64).IsRequired();
            e.Property(x => x.QualityFlags).HasMaxLength(256);
            e.Property(x => x.SourceRecordId).HasMaxLength(64);
            e.Property(x => x.SourceStatusFlag).HasMaxLength(32);
            e.HasIndex(x => new { x.AircraftId, x.SensorId, x.RecordedAtUtc }).IsUnique().AreNullsDistinct(false);
            // ETL idempotency: a source record (raw JSON telemetry_id) is stored at most once per data source.
            e.HasIndex(x => new { x.DataSource, x.SourceRecordId }).IsUnique()
                .HasFilter("source_record_id IS NOT NULL").HasDatabaseName("ux_telemetry_record_source_record");
            e.HasIndex(x => new { x.AircraftId, x.RecordedAtUtc });
            e.HasIndex(x => x.RecordedAtUtc);
            e.HasIndex(x => x.ImportId);
            e.HasOne(x => x.Aircraft).WithMany().HasForeignKey(x => x.AircraftId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TelemetryImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<EngineeringThreshold>(e =>
        {
            e.ToTable("engineering_threshold");
            e.HasKey(x => x.Id);
            e.Property(x => x.ComponentType).HasMaxLength(64).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(8).IsRequired();
            e.Property(x => x.SpecificationReference).HasMaxLength(256).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_engineering_threshold_specification", "length(trim(specification_reference)) > 0"));
        });
    }

    private static void ConfigureHealthThresholds(ModelBuilder b)
    {
        b.Entity<HealthThreshold>(e =>
        {
            e.ToTable("health_threshold");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.RuleReference).HasMaxLength(32).IsRequired();
            e.Property(x => x.Measurement).HasMaxLength(32).IsRequired();
            e.Property(x => x.Comparison).HasMaxLength(16).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(16);
            e.Property(x => x.Tier).HasMaxLength(32);
            e.Property(x => x.RecommendedInspection).HasMaxLength(500);
            e.Property(x => x.SourceReference).HasMaxLength(256).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500).IsRequired();
            e.HasIndex(x => x.DisplayOrder).IsUnique();
            // Rows are maintained in tools/db/seed/csv/health_threshold.csv (single source of reference data).
        });
    }

    private static void ConfigureMachineLearning(ModelBuilder b)
    {
        b.Entity<RiskStatus>(e =>
        {
            e.ToTable("risk_status");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.Description).HasMaxLength(500).IsRequired();
            // Rows are maintained in tools/db/seed/csv/risk_status.csv (single source of reference data).
        });

        b.Entity<ModelVersion>(e =>
        {
            e.ToTable("model_version");
            e.HasKey(x => x.Id);
            e.Property(x => x.ModelName).HasMaxLength(64).IsRequired();
            e.Property(x => x.Version).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.ModelName, x.Version }).IsUnique();
            e.Property(x => x.Algorithm).HasMaxLength(64).IsRequired();
            e.Property(x => x.ArtifactPath).HasMaxLength(500).IsRequired();
            e.Property(x => x.ArtifactSha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.ArtifactUri).HasMaxLength(500);
            Jsonb(e.Property(x => x.FeatureNames));
            Jsonb(e.Property(x => x.Hyperparameters));
            Jsonb(e.Property(x => x.Metrics));
            e.HasIndex(x => x.ModelType).IsUnique().HasFilter("status = 'Active'").HasDatabaseName("ux_model_version_one_active_per_type");
            e.HasOne(x => x.ApprovedBy).WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PipelineRun>().WithMany().HasForeignKey(x => x.TrainingRunId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AircraftAnomalyResult>(e =>
        {
            e.ToTable("aircraft_anomaly_result");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
            e.Property(x => x.SensorId).HasMaxLength(64);
            Jsonb(e.Property(x => x.FeatureValues));
            e.Property(x => x.InvestigationNotes).HasMaxLength(4000);
            e.HasIndex(x => new { x.TelemetryRecordId, x.ModelVersionId }).IsUnique();
            e.Property(x => x.Source).HasMaxLength(32).HasDefaultValue(AnomalySource.IsolationForest).HasSentinel((AnomalySource)(-1));
            e.Property(x => x.DedupKey).HasMaxLength(200);
            e.HasIndex(x => x.DedupKey).IsUnique().HasFilter("dedup_key IS NOT NULL");
            e.Property(x => x.RuleCodes).HasMaxLength(200);
            JsonbOptional(e.Property(x => x.Evidence));
            e.HasIndex(x => new { x.Source, x.AircraftId, x.InvestigationStatus });
            e.HasIndex(x => new { x.AircraftId, x.ObservedAtUtc });
            e.HasIndex(x => x.InvestigationStatus);
            e.HasIndex(x => x.ObservedAtUtc);
            e.HasOne(x => x.Aircraft).WithMany().HasForeignKey(x => x.AircraftId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.TelemetryRecord).WithMany().HasForeignKey(x => x.TelemetryRecordId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ModelVersion).WithMany().HasForeignKey(x => x.ModelVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PipelineRun>().WithMany().HasForeignKey(x => x.PipelineRunId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.AcknowledgedBy).WithMany().HasForeignKey(x => x.AcknowledgedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MaintenanceWorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.WorkOrderId);
        });

        b.Entity<AircraftRiskPrediction>(e =>
        {
            e.ToTable("aircraft_risk_prediction", t =>
                t.HasCheckConstraint("ck_aircraft_risk_prediction_probability_validated", "probability IS NULL OR probability_validated"));
            e.HasKey(x => x.Id);
            e.Property(x => x.ComponentId).HasMaxLength(64);
            e.Property(x => x.RiskCategory).HasMaxLength(32).IsRequired();
            Jsonb(e.Property(x => x.FeatureSnapshot));
            e.Property(x => x.ReviewNotes).HasMaxLength(2000);
            e.HasIndex(x => new { x.AircraftId, x.PredictionTimestampUtc });
            e.HasIndex(x => x.ApprovalStatus);
            // Scoring idempotency: one row per aircraft/basis/model/feature cutoff/horizon (ML inserts ON CONFLICT DO NOTHING).
            e.HasIndex(x => new { x.AircraftId, x.Basis, x.ModelVersionId, x.FeatureCutoffUtc, x.HorizonDays })
                .IsUnique().AreNullsDistinct(false).HasDatabaseName("ux_aircraft_risk_prediction_idempotency");
            e.HasOne(x => x.Aircraft).WithMany().HasForeignKey(x => x.AircraftId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<RiskStatus>().WithMany().HasForeignKey(x => x.RiskCategory).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ModelVersion).WithMany().HasForeignKey(x => x.ModelVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PipelineRun>().WithMany().HasForeignKey(x => x.PipelineRunId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ReviewedBy).WithMany().HasForeignKey(x => x.ReviewedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MaintenanceWorkOrder>().WithMany().HasForeignKey(x => x.OutcomeWorkOrderId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<SensorForecast>(e =>
        {
            e.ToTable("sensor_forecast", t => t.HasCheckConstraint("ck_sensor_forecast_points_array", "jsonb_typeof(points) = 'array'"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Horizon).HasMaxLength(32);
            Jsonb(e.Property(x => x.Points));
            // Idempotency: one forecast per aircraft/model/horizon/data cutoff (the ML job inserts ON CONFLICT DO NOTHING).
            e.HasIndex(x => new { x.AircraftId, x.ModelVersionId, x.Horizon, x.DataAsOfUtc }).IsUnique()
                .HasDatabaseName("ux_sensor_forecast_idempotency");
            e.HasIndex(x => new { x.AircraftId, x.Horizon, x.GeneratedAtUtc });
            e.HasOne(x => x.Aircraft).WithMany().HasForeignKey(x => x.AircraftId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ModelVersion).WithMany().HasForeignKey(x => x.ModelVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PipelineRun>().WithMany().HasForeignKey(x => x.PipelineRunId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<PipelineRun>(e =>
        {
            e.ToTable("pipeline_run");
            e.HasKey(x => x.Id);
            Jsonb(e.Property(x => x.Parameters));
            e.Property(x => x.ResultSummary).HasColumnType("jsonb");
            e.Property(x => x.ErrorMessage).HasMaxLength(4000);
            e.Property(x => x.WorkerHost).HasMaxLength(128);
            e.HasIndex(x => new { x.Status, x.RequestedAtUtc });
            e.HasIndex(x => x.JobType);
            // model_version_id is informational (no FK) to avoid a cycle with model_version.training_run_id.
            e.HasOne(x => x.RequestedBy).WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void Jsonb(PropertyBuilder<string> property) => property.HasColumnType("jsonb").IsRequired();

    /// <summary>A jsonb column that may legitimately be absent (e.g. evidence that does not apply to a row's source).</summary>
    private static void JsonbOptional(PropertyBuilder<string?> property) => property.HasColumnType("jsonb");
}
