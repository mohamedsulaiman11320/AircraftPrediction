using System.Globalization;
using System.Text;
using System.Text.Json;
using AFP.Application.Telemetry;

namespace AFP.SeedSql;

public sealed record SeedColumn(string Name, string Type, bool Required = false, int? MaxLength = null, long? Min = null);

public sealed record SeedDataset(string File, string Table, string[] Key, SeedColumn[] Columns, string[][]? Unique = null);

public sealed record SeedManifest(string TargetDatabase, string Output, SeedDataset[] Datasets);

public sealed record SeedError(string File, int Line, string? Column, string Message)
{
    public override string ToString() => Column is null ? $"{File}:{Line}: {Message}" : $"{File}:{Line} [{Column}]: {Message}";
}

/// <summary>A validated value: SQL literal text, plus the raw value for key/uniqueness checks.</summary>
internal sealed record SeedCell(string Raw, string Sql);

/// <summary>
/// Compiles hand-maintained reference CSVs (described by datasets.json) into one deterministic, idempotent,
/// transactional PostgreSQL script. Rows removed from a CSV are never deleted from the database.
/// </summary>
public static class SeedCompiler
{
    /// <summary>Databases the seed must never target (mirrors AFP.Infrastructure DatabaseTarget).</summary>
    public static readonly string[] ForbiddenDatabases = ["aircraft_app"];

    private static readonly string[] SupportedTypes = ["int", "numeric", "text", "bool"];

    public static SeedManifest LoadManifest(string seedRoot)
    {
        var path = Path.Combine(seedRoot, "datasets.json");
        var manifest = JsonSerializer.Deserialize<SeedManifest>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                       ?? throw new InvalidDataException($"{path} is empty.");
        if (string.IsNullOrWhiteSpace(manifest.TargetDatabase) || ForbiddenDatabases.Contains(manifest.TargetDatabase, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"datasets.json targetDatabase '{manifest.TargetDatabase}' is not allowed.");
        return manifest;
    }

    /// <summary>Validates every dataset; returns the SQL (or null) and all errors found.</summary>
    public static (string? Sql, IReadOnlyList<SeedError> Errors) Compile(string seedRoot, SeedManifest manifest)
    {
        var errors = new List<SeedError>();
        var sections = new List<string>();
        foreach (var dataset in manifest.Datasets)
        {
            var rows = ReadDataset(seedRoot, dataset, errors);
            if (rows is not null) sections.Add(RenderDataset(dataset, rows));
        }
        return errors.Count > 0 ? (null, errors) : (RenderScript(manifest, sections), errors);
    }

    /// <summary>Validated rows of one table as column→raw value maps (used to load reference data into test hosts).</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadRecords(string seedRoot, string table)
    {
        var manifest = LoadManifest(seedRoot);
        var ds = manifest.Datasets.Single(d => d.Table == table);
        var errors = new List<SeedError>();
        var rows = ReadDataset(seedRoot, ds, errors) ?? throw new InvalidDataException(string.Join("; ", errors));
        return rows.Select(r => (IReadOnlyDictionary<string, string>)ds.Columns.Select((col, i) => (col.Name, r[i].Raw))
            .ToDictionary(x => x.Name, x => x.Raw)).ToList();
    }

    internal static List<SeedCell[]>? ReadDataset(string seedRoot, SeedDataset ds, List<SeedError> errors)
    {
        var path = Path.Combine(seedRoot, ds.File);
        var before = errors.Count;
        foreach (var c in ds.Columns.Where(c => !SupportedTypes.Contains(c.Type)))
            errors.Add(new SeedError(ds.File, 0, c.Name, $"unsupported type '{c.Type}' in datasets.json"));
        if (!File.Exists(path))
        {
            errors.Add(new SeedError(ds.File, 0, null, "file not found"));
            return null;
        }

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0)
        {
            errors.Add(new SeedError(ds.File, 1, null, "missing header row"));
            return null;
        }
        var header = CsvLine.Split(lines[0].TrimStart('﻿')).Select(h => h.Trim()).ToArray();
        var expected = ds.Columns.Select(c => c.Name).ToArray();
        if (!header.SequenceEqual(expected))
        {
            errors.Add(new SeedError(ds.File, 1, null, $"header must be exactly: {string.Join(",", expected)}"));
            return null;
        }

        var rows = new List<SeedCell[]>();
        var keyIndexes = ds.Key.Select(k => Array.IndexOf(expected, k)).ToArray();
        var seen = new Dictionary<string, int>();
        var uniqueSets = (ds.Unique ?? []).Select(u => (Cols: u.Select(n => Array.IndexOf(expected, n)).ToArray(), Seen: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), Name: string.Join("+", u))).ToList();

        for (var i = 1; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var fields = CsvLine.Split(lines[i]);
            if (fields.Count != expected.Length)
            {
                errors.Add(new SeedError(ds.File, lineNo, null, $"expected {expected.Length} fields, found {fields.Count}"));
                continue;
            }
            var cells = new SeedCell[expected.Length];
            var rowOk = true;
            for (var c = 0; c < expected.Length; c++)
            {
                var cell = ParseCell(ds.Columns[c], fields[c], out var problem);
                if (problem is not null)
                {
                    errors.Add(new SeedError(ds.File, lineNo, ds.Columns[c].Name, problem));
                    rowOk = false;
                }
                cells[c] = cell;
            }
            if (!rowOk) continue;

            var key = string.Join("\u001f", keyIndexes.Select(k => cells[k].Raw));
            if (seen.TryGetValue(key, out var firstLine))
            {
                errors.Add(new SeedError(ds.File, lineNo, string.Join("+", ds.Key), $"duplicate key (first seen on line {firstLine})"));
                continue;
            }
            seen[key] = lineNo;
            foreach (var (cols, uniqueSeen, name) in uniqueSets)
            {
                var value = string.Join("\u001f", cols.Select(k => cells[k].Raw));
                if (uniqueSeen.TryGetValue(value, out var other))
                    errors.Add(new SeedError(ds.File, lineNo, name, $"duplicate unique value (first seen on line {other})"));
                else uniqueSeen[value] = lineNo;
            }
            rows.Add(cells);
        }
        if (errors.Count > before) return null;
        if (rows.Count == 0) errors.Add(new SeedError(ds.File, 2, null, "no data rows"));

        // Stable order: by key columns (numeric keys compared numerically).
        return rows
            .OrderBy(r => r, Comparer<SeedCell[]>.Create((a, b) => CompareKeys(a, b, keyIndexes, ds)))
            .ToList();
    }

    private static int CompareKeys(SeedCell[] a, SeedCell[] b, int[] keyIndexes, SeedDataset ds)
    {
        foreach (var k in keyIndexes)
        {
            var cmp = ds.Columns[k].Type == "int"
                ? long.Parse(a[k].Raw, CultureInfo.InvariantCulture).CompareTo(long.Parse(b[k].Raw, CultureInfo.InvariantCulture))
                : string.CompareOrdinal(a[k].Raw, b[k].Raw);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    internal static SeedCell ParseCell(SeedColumn column, string rawValue, out string? problem)
    {
        problem = null;
        var value = rawValue.Trim();
        if (value.Length == 0)
        {
            if (column.Required) problem = "required value is missing";
            return new SeedCell(string.Empty, "NULL");
        }
        switch (column.Type)
        {
            case "int":
                if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                    problem = $"'{value}' is not an integer";
                else if (column.Min is { } min && number < min)
                    problem = $"{number} is below the minimum {min}";
                return new SeedCell(value, value);
            case "numeric":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var quantity))
                    problem = $"'{value}' is not a number";
                else if (column.Min is { } lowest && quantity < lowest)
                    problem = $"{value} is below the minimum {lowest}";
                return new SeedCell(value, value);
            case "bool":
                if (!bool.TryParse(value, out var flag)) problem = $"'{value}' is not true/false";
                return new SeedCell(value, flag ? "TRUE" : "FALSE");
            default:
                if (column.MaxLength is { } max && value.Length > max) problem = $"longer than {max} characters";
                if (value.Any(ch => char.IsControl(ch))) problem = "contains control characters";
                return new SeedCell(value, "'" + value.Replace("'", "''") + "'");
        }
    }

    private static string RenderDataset(SeedDataset ds, List<SeedCell[]> rows)
    {
        var columns = string.Join(", ", ds.Columns.Select(c => c.Name));
        var nonKey = ds.Columns.Select(c => c.Name).Except(ds.Key).ToList();
        var sb = new StringBuilder();
        sb.Append($"-- {ds.Table}: {rows.Count} row(s) from {ds.File}\n");
        sb.Append($"INSERT INTO {ds.Table} ({columns}) VALUES\n");
        sb.Append(string.Join(",\n", rows.Select(r => "    (" + string.Join(", ", r.Select(c => c.Sql)) + ")")));
        sb.Append($"\nON CONFLICT ({string.Join(", ", ds.Key)}) DO ");
        sb.Append(nonKey.Count == 0 ? "NOTHING" : "UPDATE SET " + string.Join(", ", nonKey.Select(n => $"{n} = EXCLUDED.{n}")));
        sb.Append(";\n");
        return sb.ToString();
    }

    private static string RenderScript(SeedManifest manifest, List<string> sections)
    {
        var db = manifest.TargetDatabase;
        var testPattern = (db + "_test_").Replace("_", "\\_") + "%";
        var sb = new StringBuilder();
        sb.Append("-- GENERATED FILE - DO NOT EDIT. Source: tools/db/seed/csv/*.csv described by tools/db/seed/datasets.json\n");
        sb.Append("-- Regenerate: dotnet run --project Backend/tools/AFP.SeedSql -- generate\n");
        sb.Append("-- Apply explicitly: tools/db/apply-seed.ps1. Idempotent upserts; rows removed from a CSV are NOT deleted.\n");
        sb.Append("BEGIN;\n\n");
        sb.Append("DO $guard$\nBEGIN\n");
        sb.Append($"    IF current_database() <> '{db}' AND current_database() NOT LIKE '{testPattern}' THEN\n");
        sb.Append($"        RAISE EXCEPTION 'Reference seed must target {db} (connected to %)', current_database();\n");
        sb.Append("    END IF;\nEND\n$guard$;\n\n");
        sb.Append(string.Join("\n", sections));
        sb.Append("\nCOMMIT;\n");
        return sb.ToString();
    }
}
