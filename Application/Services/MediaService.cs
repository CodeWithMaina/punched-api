using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

public sealed partial class MediaService : IMediaService
{
    private readonly ApplicationDbContext _db;
    private readonly IObjectStore _store;
    private readonly IMediaKeyFactory _keys;
    private readonly IMediaUrlFactory _urls;
    private readonly IIdempotencyService _idempotency;
    private readonly IBusinessContext _businessContext;
    private readonly IPermissionService _permissions;
    private readonly MediaStorageOptions _options;

    public MediaService(ApplicationDbContext db, IObjectStore store, IMediaKeyFactory keys, IMediaUrlFactory urls,
        IIdempotencyService idempotency, IBusinessContext businessContext, IPermissionService permissions,
        IOptions<MediaStorageOptions> options)
    {
        _db = db; _store = store; _keys = keys; _urls = urls; _idempotency = idempotency;
        _businessContext = businessContext; _permissions = permissions; _options = options.Value;
    }

    public async Task<ApiResponse<MediaUploadGrantResponse>> CreateUploadAsync(Guid userId, CreateMediaUploadRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            return Fail<MediaUploadGrantResponse>("VALIDATION_ERROR", "A valid Idempotency-Key is required.");
        var purpose = request.Purpose?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!MediaPurposes.All.Contains(purpose)) return Fail<MediaUploadGrantResponse>("INVALID_MEDIA_PURPOSE", "Unsupported media purpose.");
        if (request.SizeBytes < 1) return Fail<MediaUploadGrantResponse>("MEDIA_TOO_LARGE", "The declared file size must be positive.");
        if (!_options.Limits.Purposes.TryGetValue(purpose, out var purposeLimit))
            return Fail<MediaUploadGrantResponse>("INVALID_MEDIA_PURPOSE", "Unsupported media purpose.");
        if (request.SizeBytes > purposeLimit.MaxBytes)
            return Fail<MediaUploadGrantResponse>("MEDIA_TOO_LARGE", "The declared file size exceeds the configured limit for this media purpose.");
        if (!_options.Limits.SupportedInputMimeTypes.Contains(request.DeclaredMimeType ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            return Fail<MediaUploadGrantResponse>("UNSUPPORTED_MEDIA_TYPE", "The declared image type is not supported.");
        var scope = await ResolveUploadScopeAsync(userId, purpose, request, cancellationToken);
        if (!scope.Success) return Fail<MediaUploadGrantResponse>(scope.ErrorCode!, scope.Message!);
        var hash = Hash("POST", "/v1/media/uploads", userId, request);
        var replay = await _idempotency.TryGetAsync(idempotencyKey, userId, hash);
        if (replay.Conflict) return Fail<MediaUploadGrantResponse>("IDEMPOTENCY_CONFLICT", "The idempotency key was used with a different request.");
        if (replay.Found && ParseMediaId(replay.ResponseJson) is Guid replayId)
        {
            var existing = await FindScopedAsync(userId, replayId, cancellationToken);
            if (existing != null) return await GrantAsync(existing, cancellationToken);
        }
        var media = new Domain.Entities.Media
        {
            Id = Guid.NewGuid(), BusinessId = scope.BusinessId, OwnerUserId = scope.OwnerUserId,
            UploadedByUserId = userId, Purpose = purpose, SourceKey = "pending/generated-after-validation",
            Status = MediaStatus.Pending, DeclaredMimeType = request.DeclaredMimeType!.Trim().ToLowerInvariant(),
            ExpectedSizeBytes = request.SizeBytes, OriginalFileName = SanitizeFileName(request.FileName),
            UploadGrantExpiresAt = DateTime.UtcNow.AddMinutes(_options.PresignMinutes)
        };
        media.SourceKey = _keys.CreatePendingKey(media);
        _db.Media.Add(media);
        await _db.SaveChangesAsync(cancellationToken);
        await _idempotency.StoreAsync(idempotencyKey, userId, hash, JsonSerializer.Serialize(new { mediaId = media.Id }));
        return await GrantAsync(media, cancellationToken);
    }


    private async Task<(bool Success, string? ErrorCode, string Message, Guid? BusinessId, Guid? OwnerUserId)> ResolveUploadScopeAsync(Guid userId, string purpose, CreateMediaUploadRequest request, CancellationToken cancellationToken)
    {
        if (purpose == MediaPurposes.UserAvatar)
            return request.TargetId is null || request.TargetId == userId ? (true, null, "", null, userId) : (false, "TARGET_NOT_FOUND", "The avatar target is not available.", null, null);
        var businessId = await _businessContext.GetBusinessIdAsync();
        if (!businessId.HasValue || _businessContext.GetRole() is not ("Business" or "Staff"))
            return (false, "FORBIDDEN", "Business media management is not authorized.", null, null);
        if (_businessContext.GetRole() == "Staff" || !_permissions.HasPermission("Business", RequiredPermission(purpose)))
            return (false, "FORBIDDEN", "The current role cannot manage this media purpose.", null, null);
        if (purpose == MediaPurposes.ServiceImage && (!request.TargetId.HasValue || !await _db.ServiceCatalogItems.AnyAsync(x => x.Id == request.TargetId && x.BusinessId == businessId, cancellationToken)))
            return (false, "TARGET_NOT_FOUND", "The service target is not available.", null, null);
        if (purpose == MediaPurposes.LoyaltyProgramImage && (!request.TargetId.HasValue || !await _db.LoyaltyPrograms.AnyAsync(x => x.Id == request.TargetId && x.BusinessId == businessId, cancellationToken)))
            return (false, "TARGET_NOT_FOUND", "The program target is not available.", null, null);
        if (purpose == MediaPurposes.ReviewImage && (!request.TargetId.HasValue || !await _db.Reviews.AnyAsync(x => x.Id == request.TargetId && x.BusinessId == businessId, cancellationToken)))
            return (false, "TARGET_NOT_FOUND", "The review target is not available.", null, null);
        return (true, null, "", businessId, null);
    }

    private static string RequiredPermission(string purpose) => purpose switch
    {
        MediaPurposes.ServiceImage => "serviceCatalog.manage",
        MediaPurposes.LoyaltyProgramImage => "programs.manage",
        _ => "settings.manage"
    };

    private static string Hash(string method, string route, Guid userId, object request)
    {
        var canonical = $"{method}\n{route}\n{userId}\n{JsonSerializer.Serialize(request)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static Guid? ParseMediaId(string? json)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(json!).GetProperty("mediaId").GetGuid(); }
        catch (JsonException) { return null; }
    }

    private static string? SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = Path.GetFileName(value).Trim();
        return name.Length > 255 ? name[..255] : name;
    }
}

