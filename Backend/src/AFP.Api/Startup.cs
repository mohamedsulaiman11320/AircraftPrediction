using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AFP.Api.Controllers;
using AFP.Api.Infrastructure;
using AFP.Api.Security;
using AFP.Application;
using AFP.Application.Abstractions;
using AFP.Application.Services;
using AFP.Infrastructure;
using AFP.Infrastructure.Configuration;
using AFP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using AFP.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

namespace AFP.Api;

public sealed class RateLimitOptions
{
    public const string Section = "RateLimiting";
    public int AuthPermitLimit { get; set; } = 10;
    public int AuthWindowSeconds { get; set; } = 60;
    public int GlobalPermitLimit { get; set; } = 600;
    public int GlobalWindowSeconds { get; set; } = 60;
}

public static class Startup
{
    public static void AddAfpServices(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var config = builder.Configuration;

        // Console only: the default Windows EventLog provider throws when the process lacks event-source rights,
        // which would surface as a crash inside background services. Structured JSON outside Development.
        builder.Logging.ClearProviders();
        if (builder.Environment.IsDevelopment()) builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");
        else builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

        services.AddApplication();
        services.AddInfrastructure(config);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddSingleton<TelemetryImportQueue>();
        services.AddSingleton<ITelemetryImportQueue>(sp => sp.GetRequiredService<TelemetryImportQueue>());
        services.AddHostedService<TelemetryImportWorker>();

        services.AddControllers()
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
                // 64-bit user ids may arrive as numeric strings from HTML form values.
                o.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
            })
            .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ProblemDetailsSetup.InvalidModelState);

        services.AddProblemDetails(o => o.CustomizeProblemDetails = ProblemDetailsSetup.Customize);
        services.AddExceptionHandler<GlobalExceptionHandler>();

        AddAuthentication(services);
        services.AddAuthorization(Policies.Configure);
        AddRateLimiting(services, config);

        services.AddHealthChecks().AddCheck<DatabaseIdentityHealthCheck>("database", HealthStatus.Unhealthy, ["ready"]);

        var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"];
        services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()
            .WithExposedHeaders("Content-Disposition", "Location")));

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Aircraft Failure Prediction API", Version = "v1",
                Description = "Fleet, telemetry, anomaly, prediction and maintenance APIs. Authenticate with POST /api/v1/auth/login and use the Bearer token.",
            });
            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                Description = "JWT from /api/v1/auth/login (60-minute expiry).",
            });
            c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
            });
        });
    }

    private static void AddAuthentication(IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((options, jwt) =>
            {
                var o = jwt.Value;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = o.Issuer,
                    ValidateAudience = true,
                    ValidAudience = o.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = o.GetKey(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = AfpClaims.Name,
                    RoleClaimType = AfpClaims.Role,
                };
                options.Events = new JwtBearerEvents
                {
                    // Server-side revocation: logout, disable, role change and password reset invalidate tokens immediately.
                    OnTokenValidated = async ctx =>
                    {
                        var principal = ctx.Principal!;
                        var validator = ctx.HttpContext.RequestServices.GetRequiredService<ISessionValidator>();
                        var valid = long.TryParse(principal.FindFirst(AfpClaims.Subject)?.Value, out var userId)
                                    && await validator.IsValidAsync(userId, principal.FindFirst(AfpClaims.TokenId)?.Value,
                                        principal.FindFirst(AfpClaims.SecurityStamp)?.Value, ctx.HttpContext.RequestAborted);
                        if (!valid) ctx.Fail("The session has been revoked.");
                    },
                };
            });
    }

    private static void AddRateLimiting(IServiceCollection services, IConfiguration config)
    {
        var limits = config.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthController.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.AuthPermitLimit, Window = TimeSpan.FromSeconds(limits.AuthWindowSeconds), QueueLimit = 0,
                }));
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.GlobalPermitLimit, Window = TimeSpan.FromSeconds(limits.GlobalWindowSeconds), QueueLimit = 0,
                }));
            o.OnRejected = async (ctx, ct) =>
            {
                var problems = ctx.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)retry.TotalSeconds).ToString();
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = ctx.HttpContext,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = 429, Title = "Too many requests", Detail = "Rate limit exceeded. Wait a moment and try again.",
                    },
                });
            };
        });
    }

    public static void UseAfpPipeline(this WebApplication app)
    {
        // Fail fast on a missing or weak signing key rather than at the first login.
        _ = app.Services.GetRequiredService<IOptions<JwtOptions>>().Value.GetKey();
        ApplyMigrationsIfEnabled(app);

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "AFP API v1"));
        }
        else
        {
            app.UseHsts();
        }

        app.UseCors();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthAsync,
        }).AllowAnonymous();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteHealthAsync }).AllowAnonymous();
    }

    /// <summary>
    /// Opt-in (DB_AUTO_MIGRATE=true) migration at startup using the schema-owner connection. Off by default:
    /// normal runs and builds never change the database. Guarded against protected database names.
    /// </summary>
    private static void ApplyMigrationsIfEnabled(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!db.Database.IsRelational()) return;
        var logger = app.Logger;
        var apiTarget = DatabaseTarget.Describe(db.Database.GetConnectionString()!);
        logger.LogInformation("API database target: {Target}", apiTarget);
        if (!bool.TryParse(app.Configuration["DB_AUTO_MIGRATE"], out var enabled) || !enabled) return;

        var owner = AFP.Infrastructure.DependencyInjection.ResolveOwnerConnectionString(app.Configuration)
                    ?? throw new InvalidOperationException("DB_AUTO_MIGRATE=true requires DB_USER/DB_PASSWORD (schema owner).");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(owner, n => n.MigrationsHistoryTable("__ef_migrations_history")).UseSnakeCaseNamingConvention().Options;
        using var ownerDb = new AppDbContext(options);
        logger.LogWarning("DB_AUTO_MIGRATE=true: applying migrations as {Target}", DatabaseTarget.Describe(owner));
        ownerDb.Database.Migrate();
    }

    private static Task WriteHealthAsync(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description }),
        }));
    }
}
