using System.Text.Json;

namespace AFP.Application.Common;

/// <summary>Helpers for jsonb columns, which are carried as raw JSON strings on entities.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public static JsonElement Parse(string? json, JsonElement? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback ?? EmptyObject;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return fallback ?? EmptyObject;
        }
    }

    public static JsonElement? ParseOptional(string? json) => string.IsNullOrWhiteSpace(json) ? null : Parse(json);

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Navigates a dotted property path (e.g. "failure_prediction_readiness.status").</summary>
    public static JsonElement? Find(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        }
        return current;
    }
}
