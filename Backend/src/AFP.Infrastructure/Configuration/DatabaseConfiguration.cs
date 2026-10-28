using Npgsql;

namespace AFP.Infrastructure.Configuration;

/// <summary>
/// Minimal .env reader (KEY=VALUE, '#' comments, optional quotes). Values never override variables that are
/// already set, so real environment variables and user-secrets keep precedence. Same format as ml/afp_ml/config.py.
/// </summary>
public static class DotEnv
{
    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var hash and >= 0) value = value[..hash].TrimEnd();
            result[key] = value;
        }
        return result;
    }

    /// <summary>Loads the nearest <c>.env</c> found from <paramref name="startDirectory"/> upwards. Returns the file used, or null.</summary>
    public static string? LoadFromAncestors(string startDirectory, string fileName = ".env")
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, fileName);
            if (!File.Exists(path)) continue;
            foreach (var (key, value) in Parse(File.ReadAllLines(path)))
                if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, value);
            return path;
        }
        return null;
    }
}

/// <summary>
/// Database identity and credentials from configuration (DB_* keys in .env). Explicit
/// <c>ConnectionStrings:AppDb</c> / <c>ConnectionStrings:Migrations</c> values still take precedence.
/// </summary>
public static class DatabaseTarget
{
    public const string DefaultDatabaseName = "aircraft_predict_db";

    /// <summary>Databases this implementation must never touch. Extendable with DB_FORBIDDEN_NAMES (comma-separated).</summary>
    public static readonly string[] AlwaysForbidden = ["aircraft_app"];

    public static string ExpectedName(Func<string, string?> get) =>
        string.IsNullOrWhiteSpace(get("DB_NAME")) ? DefaultDatabaseName : get("DB_NAME")!.Trim();

    public static IReadOnlySet<string> ForbiddenNames(Func<string, string?> get) =>
        AlwaysForbidden.Concat((get("DB_FORBIDDEN_NAMES") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Connection string for the API role, composed from DB_HOST/DB_PORT/DB_NAME/DB_APP_USER/API_USER_PASSWORD.</summary>
    public static string? ApiConnectionString(Func<string, string?> get) =>
        Compose(get, get("DB_APP_USER"), get("API_USER_PASSWORD"));

    /// <summary>Connection string for the schema owner (migrations), from DB_USER/DB_PASSWORD.</summary>
    public static string? OwnerConnectionString(Func<string, string?> get) =>
        Compose(get, get("DB_USER"), get("DB_PASSWORD"));

    private static string? Compose(Func<string, string?> get, string? user, string? password)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password)) return null;
        return new NpgsqlConnectionStringBuilder
        {
            Host = string.IsNullOrWhiteSpace(get("DB_HOST")) ? "localhost" : get("DB_HOST"),
            Port = int.TryParse(get("DB_PORT"), out var port) ? port : 5432,
            Database = ExpectedName(get),
            Username = user,
            Password = password,
        }.ConnectionString;
    }

    /// <summary>Throws unless the connection string targets the expected database and not a forbidden one.</summary>
    public static string Validate(string connectionString, Func<string, string?> get, string purpose)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        var expected = ExpectedName(get);
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException($"{purpose}: the connection string does not name a database.");
        if (ForbiddenNames(get).Contains(database) || ForbiddenNames(get).Contains(expected))
            throw new InvalidOperationException($"{purpose}: database '{database}' is protected and must not be used by this application.");
        if (!string.Equals(database, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{purpose}: connection targets '{database}' but DB_NAME is '{expected}'.");
        return database;
    }

    /// <summary>
    /// Connection to the isolated development (synthetic) database named by <c>DEV_DB_NAME</c>, with the API role's
    /// credentials. Null when <c>DEV_DB_NAME</c> is not set (the development view is then simply unavailable).
    /// Refuses the operational database, the default operational name and every forbidden database.
    /// </summary>
    public static string? DevelopmentConnectionString(Func<string, string?> get)
    {
        var name = get("DEV_DB_NAME")?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (string.Equals(name, ExpectedName(get), StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, DefaultDatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"DEV_DB_NAME '{name}' is the operational database; development must be isolated.");
        if (ForbiddenNames(get).Contains(name))
            throw new InvalidOperationException($"DEV_DB_NAME '{name}' is protected and must not be used by this application.");
        var user = get("DB_APP_USER");
        var password = get("API_USER_PASSWORD");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password)) return null;
        return new NpgsqlConnectionStringBuilder
        {
            Host = string.IsNullOrWhiteSpace(get("DB_HOST")) ? "localhost" : get("DB_HOST"),
            Port = int.TryParse(get("DB_PORT"), out var port) ? port : 5432,
            Database = name,
            Username = user,
            Password = password,
        }.ConnectionString;
    }

    /// <summary>Host/port/database/user only — never the password.</summary>
    public static string Describe(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        return $"{b.Username}@{b.Host}:{b.Port}/{b.Database}";
    }
}
