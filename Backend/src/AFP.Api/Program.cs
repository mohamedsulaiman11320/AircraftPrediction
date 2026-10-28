using AFP.Api;
using AFP.Api.Infrastructure;

// Root .env (git-ignored) supplies DB_* and secrets; real environment variables keep precedence.
// Integration tests set AFP_DISABLE_DOTENV=1 so a developer .env can never leak into a test host.
if (Environment.GetEnvironmentVariable("AFP_DISABLE_DOTENV") != "1")
    AFP.Infrastructure.Configuration.DotEnv.LoadFromAncestors(Directory.GetCurrentDirectory());

var builder = WebApplication.CreateBuilder(args);
builder.AddAfpServices();

var app = builder.Build();

// One-off administrative commands (run instead of the web server), e.g.
//   dotnet run --project src/AFP.Api -- bootstrap-admin --email admin@example.com --name "Ops Admin"
if (args.Length > 0 && CliCommands.IsCommand(args[0]))
{
    return await CliCommands.RunAsync(app, args);
}

app.UseAfpPipeline();
await app.RunAsync();
return 0;

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
