using AFP.Domain.Entities;
using AFP.Domain.Enums;

namespace AFP.Application.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>The authenticated caller, resolved from the validated JWT.</summary>
public interface ICurrentUser
{
    long? UserId { get; }
    IReadOnlyList<UserRole> Roles { get; }
    string? TokenId { get; }
    DateTime? TokenExpiresAtUtc { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
    string? CorrelationId { get; }

    /// <summary>The Flight Dispatcher is the administrator role with access to every screen.</summary>
    bool IsDispatcher => Roles.Contains(UserRole.FlightDispatcher);

    long RequireUserId() => UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
}

public interface IPasswordHasher
{
    string Hash(AppUser user, string password);
    bool Verify(AppUser user, string password, out bool needsRehash);
}

public sealed record IssuedToken(string AccessToken, string TokenId, DateTime ExpiresAtUtc);

public interface ITokenService
{
    IssuedToken Issue(AppUser user);
}

public sealed record EmailMessage(string To, string Subject, string TextBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>Preserves original uploaded files outside the database.</summary>
public interface IImportFileStore
{
    Task<string> SaveAsync(Guid importId, string originalFileName, Stream content, CancellationToken ct);
    Stream OpenRead(string storedPath);
    void Delete(string storedPath);
}

/// <summary>
/// Raw telemetry object storage (MinIO bronze). Keys are relative to the configured bucket/prefix;
/// returned locations are <c>s3://bucket/prefix/key</c> URIs as recorded in telemetry_import.stored_path.
/// </summary>
public interface IRawTelemetryStore
{
    /// <summary>Uploads a new object; fails if the key already exists (never overwrites raw data).</summary>
    Task<string> PutAsync(string key, Stream content, long length, string contentType, CancellationToken ct);

    /// <summary>Server-side copy of an existing object (used to re-submit a failed import to the ETL).</summary>
    Task<string> CopyAsync(string sourceLocation, string destinationKey, CancellationToken ct);

    /// <summary>
    /// Removes one object identified by a stored <c>s3://bucket/key</c> location.
    /// The location must come from persisted lineage, never from client input: implementations reject
    /// anything outside the configured raw-telemetry bucket and prefix. Idempotent — deleting an object
    /// that is already gone succeeds, so a retried cleanup does not fail.
    /// </summary>
    Task DeleteAsync(string location, CancellationToken ct);

    /// <summary>Deletes every object under a prefix, e.g. one Silver/Gold batch partition. Idempotent.</summary>
    Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken ct);
}

/// <summary>Hands telemetry import ids to the background processor.</summary>
public interface ITelemetryImportQueue
{
    ValueTask EnqueueAsync(Guid importId, CancellationToken ct);
}

public interface IActionAuditor
{
    /// <summary>Stages an audit entry; it is persisted with the caller's unit of work.</summary>
    Task RecordAsync(string action, string entityType, string entityId, object? details, CancellationToken ct);
}
