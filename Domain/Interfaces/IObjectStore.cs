namespace PunchedApi.Domain.Interfaces;

public enum ObjectStoreBucket { PrivateSource, PublicDelivery }

public sealed record PresignedObjectWrite(Uri Url, IReadOnlyDictionary<string, string> RequiredHeaders, DateTimeOffset ExpiresAt);
public sealed record ObjectMetadata(long SizeBytes, string? ContentType);
public sealed record ObjectReadResult(Stream Content, ObjectMetadata Metadata) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IObjectStore
{
    Task<PresignedObjectWrite> PresignPutAsync(ObjectStoreBucket bucket, string key, string contentType, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task<ObjectMetadata?> HeadAsync(ObjectStoreBucket bucket, string key, CancellationToken cancellationToken);
    Task<ObjectReadResult?> GetBoundedAsync(ObjectStoreBucket bucket, string key, long maxBytes, CancellationToken cancellationToken);
    Task PutAsync(ObjectStoreBucket bucket, string key, Stream content, string contentType, string cacheControl, CancellationToken cancellationToken);
    Task DeleteAsync(ObjectStoreBucket bucket, string key, CancellationToken cancellationToken);
}
