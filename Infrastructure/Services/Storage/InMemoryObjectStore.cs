namespace PunchedApi.Infrastructure.Services.Storage;

public sealed class InMemoryObjectStore : Domain.Interfaces.IObjectStore
{
    private sealed record Item(byte[] Bytes, string ContentType, string CacheControl);
    private readonly Dictionary<(Domain.Interfaces.ObjectStoreBucket Bucket, string Key), Item> _items = new();

    public Task<Domain.Interfaces.PresignedObjectWrite> PresignPutAsync(Domain.Interfaces.ObjectStoreBucket bucket, string key, string contentType, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new Domain.Interfaces.PresignedObjectWrite(new Uri($"https://upload.invalid/{Uri.EscapeDataString(key)}?code=fake"), new Dictionary<string, string> { ["Content-Type"] = contentType }, expiresAt));
    }

    public Task<Domain.Interfaces.ObjectMetadata?> HeadAsync(Domain.Interfaces.ObjectStoreBucket bucket, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_items.TryGetValue((bucket, key), out var item) ? new Domain.Interfaces.ObjectMetadata(item.Bytes.Length, item.ContentType) : null);
    }

    public Task<Domain.Interfaces.ObjectReadResult?> GetBoundedAsync(Domain.Interfaces.ObjectStoreBucket bucket, string key, long maxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_items.TryGetValue((bucket, key), out var item)) return Task.FromResult<Domain.Interfaces.ObjectReadResult?>(null);
        if (item.Bytes.LongLength > maxBytes) throw new InvalidDataException("OBJECT_TOO_LARGE");
        return Task.FromResult<Domain.Interfaces.ObjectReadResult?>(new(new MemoryStream(item.Bytes, writable: false), new(item.Bytes.Length, item.ContentType)));
    }

    public async Task PutAsync(Domain.Interfaces.ObjectStoreBucket bucket, string key, Stream content, string contentType, string cacheControl, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        _items[(bucket, key)] = new(buffer.ToArray(), contentType, cacheControl);
    }

    public Task DeleteAsync(Domain.Interfaces.ObjectStoreBucket bucket, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _items.Remove((bucket, key));
        return Task.CompletedTask;
    }
}
