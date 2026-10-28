using System.Text.Json;

namespace AFP.Application.Telemetry;

public sealed record RawJsonCheck(int RecordCount, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Structural validation of the agreed raw telemetry format (a JSON array of objects such as
/// <c>{telemetry_id, tail_number, timestamp_epoch, metrics{...}, status_flag}</c>). Only structure and required
/// keys are checked here; value-level validation and quarantine happen in the Spark ETL.
/// </summary>
public static class RawTelemetryJson
{
    private const int MaxReportedErrors = 20;

    public static RawJsonCheck Check(Stream content, IReadOnlyCollection<string> requiredKeys)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            return new RawJsonCheck(0, [$"The file is not valid JSON: {ex.Message}"]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return new RawJsonCheck(0, ["The file must contain a JSON array of telemetry records."]);

            var errors = new List<string>();
            var index = 0;
            foreach (var record in root.EnumerateArray())
            {
                index++;
                if (errors.Count >= MaxReportedErrors) break;
                if (record.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"Record {index}: must be a JSON object.");
                    continue;
                }
                var missing = requiredKeys.Where(k => !record.TryGetProperty(k, out _)).ToList();
                if (missing.Count > 0) errors.Add($"Record {index}: missing required field(s) {string.Join(", ", missing)}.");
                else if (record.GetProperty("metrics").ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                    errors.Add($"Record {index}: 'metrics' must be an object.");
            }
            var count = root.GetArrayLength();
            if (count == 0) errors.Add("The array contains no telemetry records.");
            return new RawJsonCheck(count, errors);
        }
    }
}
