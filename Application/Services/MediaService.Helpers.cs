using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

public sealed partial class MediaService
{
    private async Task<Domain.Entities.Media?> FindScopedAsync(Guid userId, Guid mediaId, System.Threading.CancellationToken cancellationToken)
    {
        var businessId = await _businessContext.GetBusinessIdAsync();
        return await _db.Media.FirstOrDefaultAsync(x => x.Id == mediaId &&
            (x.OwnerUserId == userId || (businessId.HasValue && x.BusinessId == businessId)), cancellationToken);
    }

    private MediaResponse Map(Domain.Entities.Media media)
    {
        var variants = ParseVariants(media.VariantsJson);
        if (media.Status != MediaStatus.Ready) variants = [];
        return new MediaResponse
        {
            Id = media.Id, Purpose = media.Purpose, Status = media.Status.ToString(), Visibility = media.Visibility.ToString(),
            DeclaredMimeType = media.DeclaredMimeType, DetectedMimeType = media.DetectedMimeType,
            Width = media.Width, Height = media.Height, SourceSizeBytes = media.SourceSizeBytes,
            Variants = variants, CreatedAt = media.CreatedAt, UpdatedAt = media.UpdatedAt
        };
    }

    private static IReadOnlyList<MediaVariantResponse> ParseVariants(string json)
    {
        try { return JsonSerializer.Deserialize<List<MediaVariantResponse>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task<ApiResponse<Domain.Entities.Media>> RequireOwnedBusinessMediaAsync(Guid userId, Guid mediaId, string purpose, System.Threading.CancellationToken cancellationToken)
    {
        var businessId = await _businessContext.GetBusinessIdAsync();
        var role = _businessContext.GetRole();
        if (!businessId.HasValue || role is not ("Business" or "Staff") || (role == "Staff" && !_permissions.HasPermission("Staff", RequiredPermission(purpose))))
            return Fail<Domain.Entities.Media>("FORBIDDEN", "You are not authorized to manage this media.");
        var media = await _db.Media.FirstOrDefaultAsync(x => x.Id == mediaId && x.BusinessId == businessId && x.Purpose == purpose, cancellationToken);
        return media == null ? Fail<Domain.Entities.Media>("MEDIA_NOT_FOUND", "The media could not be found.") : ApiResponse<Domain.Entities.Media>.Ok(media);
    }

    private bool CanMutate(Domain.Entities.Media media)
    {
        if (!media.BusinessId.HasValue) return media.OwnerUserId.HasValue;
        var role = _businessContext.GetRole();
        return role == "Business" || (role == "Staff" && _permissions.HasPermission("Staff", RequiredPermission(media.Purpose)));
    }

    private Task<ApiResponse<bool>> AuthorizeMutationAsync(Domain.Entities.Media? media)
    {
        if (media == null) return Task.FromResult(ApiResponse<bool>.Fail("MEDIA_NOT_FOUND", "The media could not be found."));
        return Task.FromResult(CanMutate(media) ? ApiResponse<bool>.Ok(true) : ApiResponse<bool>.Fail("FORBIDDEN", "You are not authorized to mutate this media."));
    }

    private static ApiResponse<T> Fail<T>(string code, string message) => ApiResponse<T>.Fail(code, message);
}
