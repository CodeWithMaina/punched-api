using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Infrastructure.Services.Storage;

public sealed partial class R2ObjectStore : IObjectStore, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly MediaStorageOptions _options;

    public R2ObjectStore(IOptions<MediaStorageOptions> options)
    {
        _options = options.Value;
        _options.Validate(requireR2: true);
        var config = new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl,
            AuthenticationRegion = _options.Region,
            ForcePathStyle = false,
            UseHttp = false
        };
        _client = new AmazonS3Client(_options.AccessKeyId, _options.SecretAccessKey, config);
    }

    internal R2ObjectStore(IAmazonS3 client, MediaStorageOptions options)
    {
        _client = client;
        _options = options;
    }

    public Task<PresignedObjectWrite> PresignPutAsync(ObjectStoreBucket bucket, string key, string contentType, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket(bucket), Key = key, Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime, Protocol = Protocol.HTTPS,
            ContentType = contentType
        };
        return Task.FromResult(new PresignedObjectWrite(new Uri(_client.GetPreSignedURL(request)),
            new Dictionary<string, string> { ["Content-Type"] = contentType }, expiresAt));
    }

    public async Task<ObjectMetadata?> HeadAsync(ObjectStoreBucket bucket, string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        try
        {
            var response = await _client.GetObjectMetadataAsync(Bucket(bucket), key, cancellationToken);
            return new ObjectMetadata(response.ContentLength, response.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<ObjectReadResult?> GetBoundedAsync(ObjectStoreBucket bucket, string key, long maxBytes, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        try
        {
            var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = Bucket(bucket), Key = key }, cancellationToken);
            if (response.ContentLength > maxBytes) { response.Dispose(); throw new InvalidDataException("OBJECT_TOO_LARGE"); }
            return new ObjectReadResult(new BoundedReadStream(response.ResponseStream, maxBytes),
                new ObjectMetadata(response.ContentLength, response.Headers.ContentType));
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public Task PutAsync(ObjectStoreBucket bucket, string key, Stream content, string contentType, string cacheControl, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        return _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket(bucket), Key = key, InputStream = content,
            ContentType = contentType, Headers = { CacheControl = cacheControl }
        }, cancellationToken);
    }

    public async Task DeleteAsync(ObjectStoreBucket bucket, string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        try { await _client.DeleteObjectAsync(Bucket(bucket), key, cancellationToken); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
    }

    private string Bucket(ObjectStoreBucket bucket) => bucket switch
    {
        ObjectStoreBucket.PrivateSource => _options.PrivateBucket,
        ObjectStoreBucket.PublicDelivery => _options.PublicBucket,
        _ => throw new ArgumentOutOfRangeException(nameof(bucket))
    };

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith('/') || key.Contains("..", StringComparison.Ordinal) || key.Contains('\\') || key.Contains('\0'))
            throw new InvalidOperationException("Invalid object key.");
    }

    public void Dispose() { if (_client is IDisposable disposable) disposable.Dispose(); }
}
