using AFP.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AFP.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>. Migrations run as the schema owner: ConnectionStrings__Migrations, else
/// DB_USER/DB_PASSWORD from the root .env. The target database is checked by <see cref="DatabaseTarget"/>, so
/// <c>database update</c> can never run against a protected database. Generating a migration does not connect.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        DotEnv.LoadFromAncestors(Directory.GetCurrentDirectory());
        static string? Get(string key) => Environment.GetEnvironmentVariable(key);

        var connectionString = Get("ConnectionStrings__Migrations") is { Length: > 0 } explicitValue
            ? explicitValue
            : DatabaseTarget.OwnerConnectionString(Get)
              ?? $"Host=localhost;Port=5432;Database={DatabaseTarget.ExpectedName(Get)};Username=migration_placeholder";
        DatabaseTarget.Validate(connectionString, Get, "Migrations");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options);
    }
}
