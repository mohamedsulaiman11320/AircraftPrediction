using AFP.Application.Abstractions;
using AFP.Application.Common;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace AFP.Infrastructure.Integration;

/// <summary>
/// Writes raw telemetry into the MinIO bucket/prefix watched by the Spark ETL (default bronze/telemetry/).
/// Uses a dedicated upload-only MinIO identity (MINIO_ACCESS_KEY / MINIO_SECRET_KEY), never the root account.
/// </summary>
public sealed class MinioRawTelemetryStore : IRawTelemetryStore
{
    private readonly RawTelemetryStorageOptions _options;
    private readonly Lazy<IMinioClient> _client;

    public MinioRawTelemetryStore(IOptions<RawTelemetryStorageOptions> options)
    {
        _options = options.Value;
        _client = new Lazy<IMinioClient>(() =>
        {
            var endpoint = new Uri(_options.Endpoint);
            return new MinioClient()
                .WithEndpoint(endpoint.Host, endpoint.Port)
                .WithCredentials(_options.AccessKey, _options.SecretKey)
                .WithSSL(endpoint.Scheme == Uri.UriSchemeHttps)
                .Build();
        });
    }

    public Task<string> PutAsync(string key, Stream content, long length, string contentType, CancellationToken ct) =>
        Guard(() => PutCoreAsync(key, content, length, contentType, ct));

    public Task<string> CopyAsync(string sourceLocation, string destinationKey, CancellationToken ct) =>
        Guard(() => CopyCoreAsync(sourceLocation, destinationKey, ct));

    public async Task DeleteAsync(string location, CancellationToken ct) =>
        await Guard(async () =>
        {
            var (bucket, objectName) = Parse(location);
            EnsureDeletable(bucket, objectName);
            await _client.Value.RemoveObjectAsync(
                new RemoveObjectArgs().WithBucket(bucket).WithObject(objectName), ct);
            return location;
        });

    public async Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken ct)
    {
        EnsureDeletable(bucket, prefix);
        var keys = new List<string>();
        var listing = _client.Value.ListObjectsEnumAsync(
            new ListObjectsArgs().WithBucket(bucket).WithPrefix(prefix).WithRecursive(true), ct);
        await foreach (var item in listing.WithCancellation(ct))
            if (!string.IsNullOrEmpty(item.Key)) keys.Add(item.Key);

        foreach (var key in keys)
            await _client.Value.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(key), ct);
        return keys.Count;
    }

    /// <summary>
    /// Only keys inside the buckets this application owns may be removed, and never a traversal or an
    /// empty/rooted key that would widen the blast radius. The caller always supplies a location taken
    /// from persisted lineage, but this is the last line of defence if that ever stops being true.
    /// </summary>
    private void EnsureDeletable(string bucket, string objectName)
    {
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(objectName))
            throw new ArgumentException("Refusing to delete without an explicit bucket and key.");
        if (!_options.DeletableBuckets.Contains(bucket, StringComparer.Ordinal))
            throw new ArgumentException($"Refusing to delete from bucket '{bucket}': not an application bucket.");
        if (objectName.Contains("..", StringComparison.Ordinal) || objectName.StartsWith('/'))
            throw new ArgumentException("Unsafe object key.");
    }

    /// <summary>Storage failures surface as 503 with a clear message instead of an internal error.</summary>
    private static async Task<string> Guard(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (MinioException ex) when (ex is not ObjectNotFoundException)
        {
            throw new DependencyUnavailableException($"Raw telemetry storage refused the request ({ex.GetType().Name}).", "storage_unavailable");
        }
        catch (HttpRequestException)
        {
            throw new DependencyUnavailableException("Raw telemetry storage (MinIO) is not reachable.", "storage_unavailable");
        }
    }

    private async Task<string> PutCoreAsync(string key, Stream content, long length, string contentType, CancellationToken ct)
    {
        var objectName = ObjectName(key);
        if (await ExistsAsync(objectName, ct))
            throw new ConflictException($"Raw object '{objectName}' already exists; raw telemetry is never overwritten.", "object_exists");
        await _client.Value.PutObjectAsync(new PutObjectArgs()
            .WithBucket(_options.Bucket).WithObject(objectName)
            .WithStreamData(content).WithObjectSize(length).WithContentType(contentType), ct);
        return Location(objectName);
    }

    private async Task<string> CopyCoreAsync(string sourceLocation, string destinationKey, CancellationToken ct)
    {
        var (bucket, sourceObject) = Parse(sourceLocation);
        var destination = ObjectName(destinationKey);
        if (await ExistsAsync(destination, ct))
            throw new ConflictException($"Raw object '{destination}' already exists.", "object_exists");
        await _client.Value.CopyObjectAsync(new CopyObjectArgs()
            .WithBucket(_options.Bucket).WithObject(destination)
            .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(bucket).WithObject(sourceObject)), ct);
        return Location(destination);
    }

    private async Task<bool> ExistsAsync(string objectName, CancellationToken ct)
    {
        try
        {
            await _client.Value.StatObjectAsync(new StatObjectArgs().WithBucket(_options.Bucket).WithObject(objectName), ct);
            return true;
        }
        catch (ObjectNotFoundException)
        {
            return false;
        }
    }

    private string ObjectName(string key)
    {
        if (key.Contains("..", StringComparison.Ordinal) || key.StartsWith('/'))
            throw new ArgumentException("Unsafe object key.", nameof(key));
        return _options.Prefix.TrimEnd('/') + "/" + key;
    }

    private string Location(string objectName) => $"s3://{_options.Bucket}/{objectName}";

    internal static (string Bucket, string Object) Parse(string location)
    {
        if (!location.StartsWith("s3://", StringComparison.Ordinal)) throw new ArgumentException("Not an object-storage location.", nameof(location));
        var path = location["s3://".Length..];
        var slash = path.IndexOf('/');
        if (slash <= 0) throw new ArgumentException("Invalid object-storage location.", nameof(location));
        return (path[..slash], path[(slash + 1)..]);
    }
}
