using System.Text;
using AFP.SeedSql;

// Usage: dotnet run --project Backend/tools/AFP.SeedSql -- <validate|generate|check> [--root <tools/db/seed>]
// Never connects to a database; applying the generated SQL is a separate explicit step (tools/db/apply-seed.ps1).
var command = args.FirstOrDefault() ?? "check";
var rootIndex = Array.IndexOf(args, "--root");
var root = rootIndex >= 0 && rootIndex + 1 < args.Length ? args[rootIndex + 1] : SeedPaths.FindSeedRoot(Directory.GetCurrentDirectory());
if (root is null || !File.Exists(Path.Combine(root, "datasets.json")))
{
    Console.Error.WriteLine("Could not find tools/db/seed/datasets.json. Pass --root <path>.");
    return 2;
}

var manifest = SeedCompiler.LoadManifest(root);
var (sql, errors) = SeedCompiler.Compile(root, manifest);
if (errors.Count > 0)
{
    foreach (var error in errors) Console.Error.WriteLine(error);
    Console.Error.WriteLine($"{errors.Count} validation error(s).");
    return 1;
}

var outputPath = Path.Combine(root, manifest.Output);
switch (command)
{
    case "validate":
        Console.WriteLine($"OK: {manifest.Datasets.Length} dataset(s) valid.");
        return 0;
    case "generate":
        File.WriteAllText(outputPath, sql, new UTF8Encoding(false));
        Console.WriteLine($"Wrote {outputPath}");
        return 0;
    case "check":
        var current = File.Exists(outputPath) ? File.ReadAllText(outputPath) : null;
        if (current != sql)
        {
            Console.Error.WriteLine($"{manifest.Output} is stale or missing. Run: dotnet run --project Backend/tools/AFP.SeedSql -- generate");
            return 1;
        }
        Console.WriteLine($"OK: {manifest.Output} matches the CSV sources.");
        return 0;
    default:
        Console.Error.WriteLine("Commands: validate | generate | check");
        return 2;
}

namespace AFP.SeedSql
{
    public static class SeedPaths
    {
        public static string? FindSeedRoot(string start)
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tools", "db", "seed");
                if (File.Exists(Path.Combine(candidate, "datasets.json"))) return candidate;
            }
            return null;
        }
    }
}
