namespace AFP.Application.Common;

/// <summary>Base for exceptions translated into ProblemDetails by the API layer.</summary>
public abstract class AppException(int statusCode, string code, string title, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string Title { get; } = title;
}

public sealed class NotFoundException(string entity, object key)
    : AppException(404, "not_found", "Resource not found", $"{entity} '{key}' was not found.");

public sealed class ConflictException(string message, string code = "conflict")
    : AppException(409, code, "Conflict", message);

public sealed class ForbiddenException(string message, string code = "forbidden")
    : AppException(403, code, "Forbidden", message);

public sealed class AuthenticationFailedException(int statusCode, string code, string message)
    : AppException(statusCode, code, "Authentication failed", message);

/// <summary>A required external dependency (e.g. MinIO object storage) refused or could not be reached.</summary>
public sealed class DependencyUnavailableException(string message, string code = "dependency_unavailable")
    : AppException(503, code, "Service dependency unavailable", message);

public sealed class BusinessRuleException(string message, string code = "business_rule_violation")
    : AppException(400, code, "Business rule violation", message);

public sealed class ValidationException(IReadOnlyDictionary<string, string[]> errors)
    : AppException(400, "validation_failed", "Validation failed", "One or more validation errors occurred.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public ValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] }) { }
}
