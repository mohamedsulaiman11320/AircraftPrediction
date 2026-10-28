using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Infrastructure.Configuration;
using AFP.Infrastructure.Integration;
using AFP.Infrastructure.Persistence;
using AFP.Infrastructure.Persistence.Repositories;
using AFP.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AFP.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "AppDb";

    /// <param name="configureDb">Optional override of the database provider (used by integration tests).</param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration, Action<DbContextOptionsBuilder>? configureDb = null)
    {
        services.AddDbContext<AppDbContext>(options =>
        {
            if (configureDb is not null)
            {
                configureDb(options);
                return;
            }
            var connectionString = ResolveApiConnectionString(configuration);
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
                .UseSnakeCaseNamingConvention();
        });

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.Section));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.Configure<PredictionOptions>(configuration.GetSection(PredictionOptions.Section));
        // One horizon for API and ML: AFP_PREDICTION_HORIZON_DAYS (root .env) unless Prediction:HorizonDays is set explicitly.
        services.PostConfigure<PredictionOptions>(o =>
        {
            if (configuration[$"{PredictionOptions.Section}:HorizonDays"] is null
                && int.TryParse(configuration["AFP_PREDICTION_HORIZON_DAYS"], out var days) && days > 0)
                o.HorizonDays = days;
        });
        services.Configure<TelemetryImportOptions>(configuration.GetSection(TelemetryImportOptions.Section));
        services.Configure<AnomalyWorkOrderOptions>(configuration.GetSection(AnomalyWorkOrderOptions.Section));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IAircraftRepository, AircraftRepository>();
        services.AddScoped<IMaintenanceRepository, MaintenanceRepository>();
        services.AddScoped<ITelemetryRepository, TelemetryRepository>();
        services.AddScoped<IAnomalyRepository, AnomalyRepository>();
        services.AddScoped<IPredictionRepository, PredictionRepository>();
        services.AddScoped<ISensorForecastRepository, SensorForecastRepository>();
        services.AddScoped<IModelVersionRepository, ModelVersionRepository>();
        services.AddScoped<IPipelineRunRepository, PipelineRunRepository>();
        // The isolated development (synthetic) database: its own connection, available only when DEV_DB_NAME is set.
        // Tests that replace the provider get an unconfigured source unless they register their own.
        services.AddScoped<IDevelopmentDataSource>(_ => configureDb is not null
            ? new DevelopmentDataSource(null)
            : new DevelopmentDataSource(DatabaseTarget.DevelopmentConnectionString(key => configuration[key])));
        services.AddScoped<DemoFleetSeeder>();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IImportFileStore, LocalImportFileStore>();
        // MinIO bronze storage for raw JSON (MINIO_* keys in the root .env; MinIO itself runs in Docker).
        services.Configure<RawTelemetryStorageOptions>(o =>
        {
            o.Endpoint = configuration["MINIO_ENDPOINT"] ?? o.Endpoint;
            o.AccessKey = configuration["MINIO_ACCESS_KEY"];
            o.SecretKey = configuration["MINIO_SECRET_KEY"];
            o.Bucket = configuration["MINIO_BRONZE_BUCKET"] ?? o.Bucket;
            o.Prefix = configuration["MINIO_BRONZE_PREFIX"] ?? o.Prefix;
            if (long.TryParse(configuration["RAW_UPLOAD_MAX_BYTES"], out var max) && max > 0) o.MaxFileBytes = max;
        });
        services.AddSingleton<IRawTelemetryStore, MinioRawTelemetryStore>();

        var emailMode = configuration.GetSection(EmailOptions.Section)["Mode"] ?? "Pickup";
        if (string.Equals(emailMode, "Smtp", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        else
            services.AddSingleton<IEmailSender, PickupDirectoryEmailSender>();

        return services;
    }

    /// <summary>ConnectionStrings:AppDb if set, otherwise composed from DB_* keys; always checked by <see cref="DatabaseTarget"/>.</summary>
    public static string ResolveApiConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName) is { Length: > 0 } explicitValue
            ? explicitValue
            : DatabaseTarget.ApiConnectionString(key => configuration[key]);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Database connection is not configured. Set DB_HOST/DB_PORT/DB_NAME/DB_APP_USER/API_USER_PASSWORD in the root .env " +
                $"(see .env.example) or ConnectionStrings:{ConnectionStringName}.");
        DatabaseTarget.Validate(connectionString, key => configuration[key], "API database");
        return connectionString;
    }

    /// <summary>Schema-owner connection for migrations: ConnectionStrings:Migrations, else DB_USER/DB_PASSWORD.</summary>
    public static string? ResolveOwnerConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Migrations") is { Length: > 0 } explicitValue
            ? explicitValue
            : DatabaseTarget.OwnerConnectionString(key => configuration[key]);
        if (connectionString is not null) DatabaseTarget.Validate(connectionString, key => configuration[key], "Migrations");
        return connectionString;
    }
}
