using System.Security.Claims;
using System.Text.Json;
using AFP.Application.Abstractions;
using AFP.Application.Common;
using AFP.Application.Services;
using AFP.Domain.Enums;
using AFP.Infrastructure.Persistence;
using AFP.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace AFP.Api.Infrastructure;

/// <summary>
/// Administrative commands executed through the API host so they share configuration and DI:
/// <list type="bullet">
/// <item><c>bootstrap-admin --email &lt;email&gt; --name &lt;display name&gt; [--role &lt;Role&gt;]</c> — password read from AFP_BOOTSTRAP_ADMIN_PASSWORD.</item>
/// <item><c>seed-demo-fleet</c> — Development environment only; creates fictional DEMO-A01..A12 aircraft.</item>
/// <item><c>ingest-raw-telemetry --file &lt;path&gt; --run-as &lt;username&gt; [--wait &lt;seconds&gt;] [--poll &lt;seconds&gt;]</c> —
/// queues a raw JSON file for the ETL through exactly the same service as <c>POST /telemetry/raw-uploads</c>, as an
/// existing Active DataEngineer (used by the daily telemetry simulator). A file already imported (same SHA-256) is not
/// uploaded again; its import is reused. With <c>--wait</c> it then waits for the ETL and prints the final import as
/// JSON. Exit 0 = Completed (or queued without --wait), 4 = the ETL failed it, 5 = still pending when the wait ended.</item>
/// </list>
/// </summary>
public static class CliCommands
{
    public const string BootstrapAdmin = "bootstrap-admin";
    public const string SeedDemoFleet = "seed-demo-fleet";
    public const string IngestRawTelemetry = "ingest-raw-telemetry";
    public const string ProcessAnomalies = "process-anomalies";
    public const int ExitEtlFailed = 4;
    public const int ExitStillPending = 5;
    public const string PasswordVariable = "AFP_BOOTSTRAP_ADMIN_PASSWORD";

    public static bool IsCommand(string arg) => arg is BootstrapAdmin or SeedDemoFleet or IngestRawTelemetry or ProcessAnomalies;

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        try
        {
            switch (args[0])
            {
                case BootstrapAdmin:
                {
                    var username = Option(args, "--username");
                    var email = Option(args, "--email");
                    var roleText = Option(args, "--role") ?? nameof(UserRole.FlightDispatcher);
                    var name = Option(args, "--name") ?? roleText;
                    var password = Environment.GetEnvironmentVariable(PasswordVariable);
                    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)
                        || !Enum.TryParse<UserRole>(roleText, ignoreCase: false, out var role))
                    {
                        Console.Error.WriteLine($"Usage: {BootstrapAdmin} --username <username> --email <email> [--name <display name>] [--role FlightDispatcher|DataEngineer]");
                        Console.Error.WriteLine("Creates the first active account of that role (default FlightDispatcher). The DataEngineer approves all other accounts.");
                        Console.Error.WriteLine($"The password is read from the {PasswordVariable} environment variable (never pass it on the command line).");
                        return 2;
                    }
                    var id = await services.GetRequiredService<IAdminBootstrapService>()
                        .CreateFirstAccountAsync(role, username, email, name, password, CancellationToken.None);
                    Console.WriteLine($"{role} created: {username.Trim().ToLowerInvariant()} (id {id}). Clear {PasswordVariable} from your session now.");
                    return 0;
                }
                case SeedDemoFleet:
                {
                    if (!app.Environment.IsDevelopment())
                    {
                        Console.Error.WriteLine("seed-demo-fleet is only available in the Development environment.");
                        return 2;
                    }
                    var created = await services.GetRequiredService<DemoFleetSeeder>().SeedAsync(CancellationToken.None);
                    Console.WriteLine($"Demo fleet seeded: {created} aircraft created (DEMO-A01..DEMO-A12, development data only).");
                    return 0;
                }
                case IngestRawTelemetry:
                    return await IngestRawTelemetryAsync(app.Services, services, args);
                case ProcessAnomalies:
                {
                    // Detect anomalies (approved BR-01 rules / validated model assessments) and raise their work orders,
                    // as a verified DataEngineer so the run, the tickets and the audit are attributed to a real account.
                    if (await SignInAsDataEngineerAsync(services, Option(args, "--run-as")) is { } failure) return failure;
                    var result = await services.GetRequiredService<IAnomalyProcessingService>().RunAsync(CancellationToken.None);
                    Console.WriteLine(JsonSerializer.Serialize(result));
                    return result.Status == "Succeeded" ? 0 : 1;
                }
            }
        }
        catch (ValidationException ex)
        {
            foreach (var (field, messages) in ex.Errors)
                foreach (var message in messages) Console.Error.WriteLine($"{field}: {message}");
            return 1;
        }
        catch (AppException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        return 2;
    }

    /// <summary>
    /// Runs the normal raw-upload service as a real, verified account: the command's scope gets an HTTP context
    /// carrying that user's id and roles, so validation, duplicate detection, Bronze storage, the import row,
    /// <c>requested_by</c> and the audit entry are exactly what the endpoint produces. No synthetic identity is used.
    /// </summary>
    /// <summary>Signs the command's scope in as an existing Active DataEngineer; returns an exit code on refusal.</summary>
    private static async Task<int?> SignInAsDataEngineerAsync(IServiceProvider services, string? runAs)
    {
        if (string.IsNullOrWhiteSpace(runAs))
        {
            Console.Error.WriteLine("--run-as <DataEngineer username> is required.");
            return 2;
        }
        var user = await services.GetRequiredService<IUserRepository>().GetByUsernameAsync(runAs.Trim().ToLowerInvariant(), CancellationToken.None);
        if (user is null || user.Status != UserStatus.Active || !user.HasRole(UserRole.DataEngineer))
        {
            Console.Error.WriteLine($"'{runAs}' is not an active DataEngineer account; refusing to run on its behalf.");
            return 2;
        }
        var claims = new List<Claim> { new(AfpClaims.Subject, user.Id.ToString()), new(AfpClaims.Username, user.Username) };
        claims.AddRange(user.ActiveRoles.Select(r => new Claim(AfpClaims.Role, r.ToString())));
        services.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "cli")) };
        return null;
    }

    private static async Task<int> IngestRawTelemetryAsync(IServiceProvider root, IServiceProvider services, string[] args)
    {
        var path = Option(args, "--file");
        var runAs = Option(args, "--run-as");
        var waitSeconds = int.TryParse(Option(args, "--wait"), out var w) ? Math.Max(0, w) : 0;
        var pollSeconds = int.TryParse(Option(args, "--poll"), out var p) ? Math.Max(1, p) : 10;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(runAs) || !File.Exists(path))
        {
            Console.Error.WriteLine($"Usage: {IngestRawTelemetry} --file <existing .json file> --run-as <DataEngineer username> [--wait <seconds>] [--poll <seconds>]");
            return 2;
        }
        var user = await services.GetRequiredService<IUserRepository>().GetByUsernameAsync(runAs.Trim().ToLowerInvariant(), CancellationToken.None);
        if (user is null || user.Status != UserStatus.Active || !user.HasRole(UserRole.DataEngineer))
        {
            Console.Error.WriteLine($"'{runAs}' is not an active DataEngineer account; refusing to ingest on its behalf.");
            return 2;
        }
        var claims = new List<Claim> { new(AfpClaims.Subject, user.Id.ToString()), new(AfpClaims.Username, user.Username) };
        claims.AddRange(user.ActiveRoles.Select(r => new Claim(AfpClaims.Role, r.ToString())));
        services.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "cli")) };

        // A file already imported (same bytes) is never uploaded twice: resume by confirming the existing import.
        var bytes = await File.ReadAllBytesAsync(path);
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var existing = await services.GetRequiredService<ITelemetryRepository>().GetImportByHashAsync(sha256, CancellationToken.None);
        Guid importId;
        if (existing is not null)
            importId = existing.Id;
        else
        {
            await using var stream = new MemoryStream(bytes, writable: false);
            importId = (await services.GetRequiredService<ITelemetryService>()
                .StartRawJsonImportAsync(new UploadedFile(Path.GetFileName(path), bytes.Length, stream), CancellationToken.None)).Id;
        }

        // Each poll uses a fresh scope so the import row is re-read from the database, never from a tracked cache.
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (true)
        {
            await using var scope = root.CreateAsyncScope();
            var import = await scope.ServiceProvider.GetRequiredService<ITelemetryRepository>().GetImportAsync(importId, CancellationToken.None);
            var status = import?.Status;
            var done = status is TelemetryImportStatus.Completed or TelemetryImportStatus.Failed;
            if (done || waitSeconds == 0 || DateTime.UtcNow >= deadline)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    importId, status = status?.ToString(), reused = existing is not null,
                    totalRows = import?.TotalRows, acceptedRows = import?.AcceptedRows, rejectedRows = import?.RejectedRows,
                    duplicateRows = import?.DuplicateRows, errorMessage = import?.ErrorMessage,
                }));
                return status switch
                {
                    TelemetryImportStatus.Completed => 0,
                    TelemetryImportStatus.Failed => ExitEtlFailed,
                    _ => waitSeconds == 0 ? 0 : ExitStillPending,
                };
            }
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds));
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
