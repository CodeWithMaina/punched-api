using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Media;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Upload-grant scope rules for business media.
///
/// Regression cover for the defect that made every service photo fail with
/// "The service target is not available.": the service editor uploads the photos
/// BEFORE the service row exists, so a mandatory target made creating a service
/// with photos impossible. The target is now optional on upload — and still
/// validated when supplied, because the ownership check that actually matters
/// happens on attach.
/// </summary>
public sealed class MediaUploadScopeTests
{
    private const string Key = "scope-test-key";

    [Fact]
    public async Task ServiceImageGrant_SucceedsWithoutATarget_WhenTheServiceIsCreatedAfterThePhoto()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var result = await CreateMediaService(context, business.Id).CreateUploadAsync(
            owner.Id,
            new CreateMediaUploadRequest
            {
                Purpose = MediaPurposes.ServiceImage,
                FileName = "photo.jpg",
                DeclaredMimeType = "image/jpeg",
                SizeBytes = 2048
                // No TargetId — this is the create-a-service flow.
            },
            Key,
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotEqual(Guid.Empty, result.Data!.MediaId);

        // The row must still be tenant-scoped: business-owned, not user-owned.
        var row = context.Media.Single();
        Assert.Equal(business.Id, row.BusinessId);
        Assert.Null(row.OwnerUserId);
        Assert.Equal(MediaStatus.Pending, row.Status);
    }

    [Fact]
    public async Task ServiceImageGrant_RejectsATargetBelongingToAnotherBusiness()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var otherOwner = BookingTestBase.CreateOwner("other-owner@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Business");
        var foreignService = BookingTestBase.CreateService(otherBusiness.Id, "Their service", 30, 100m);
        await BookingTestBase.SeedAsync(context, owner, business, otherOwner, otherBusiness, foreignService);

        var result = await CreateMediaService(context, business.Id).CreateUploadAsync(
            owner.Id,
            new CreateMediaUploadRequest
            {
                Purpose = MediaPurposes.ServiceImage,
                TargetId = foreignService.Id,
                FileName = "photo.jpg",
                DeclaredMimeType = "image/jpeg",
                SizeBytes = 2048
            },
            Key,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TARGET_NOT_FOUND", result.Error?.Code);
        Assert.Empty(context.Media);
    }

    [Fact]
    public async Task ServiceImageGrant_AcceptsAnOwnServiceTarget()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var service = BookingTestBase.CreateService(business.Id, "My service", 30, 100m);
        await BookingTestBase.SeedAsync(context, owner, business, service);

        var result = await CreateMediaService(context, business.Id).CreateUploadAsync(
            owner.Id,
            new CreateMediaUploadRequest
            {
                Purpose = MediaPurposes.ServiceImage,
                TargetId = service.Id,
                FileName = "photo.jpg",
                DeclaredMimeType = "image/jpeg",
                SizeBytes = 2048
            },
            Key,
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(business.Id, context.Media.Single().BusinessId);
    }

    [Fact]
    public async Task AvatarGrant_StillRejectsSomebodyElsesTarget()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var result = await CreateMediaService(context, business.Id).CreateUploadAsync(
            owner.Id,
            new CreateMediaUploadRequest
            {
                Purpose = MediaPurposes.UserAvatar,
                TargetId = Guid.NewGuid(),
                FileName = "avatar.jpg",
                DeclaredMimeType = "image/jpeg",
                SizeBytes = 2048
            },
            Key,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TARGET_NOT_FOUND", result.Error?.Code);
    }

    [Fact]
    public async Task StaffPersona_CannotStartABusinessUpload()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var result = await CreateMediaService(context, business.Id, role: "Staff").CreateUploadAsync(
            owner.Id,
            new CreateMediaUploadRequest
            {
                Purpose = MediaPurposes.ServiceImage,
                FileName = "photo.jpg",
                DeclaredMimeType = "image/jpeg",
                SizeBytes = 2048
            },
            Key,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error?.Code);
    }

    /// <summary>
    /// A <see cref="MediaService"/> over the test database with the object store,
    /// key factory and idempotency store stubbed — only the scope rules are under
    /// test, and the real <see cref="PermissionService"/> keeps the persona matrix
    /// authoritative rather than mocked to always-allow.
    /// </summary>
    private static MediaService CreateMediaService(
        ApplicationDbContext context,
        Guid businessId,
        string role = "Business")
    {
        var businessContext = new Mock<IBusinessContext>();
        businessContext.Setup(x => x.GetBusinessIdAsync()).ReturnsAsync(businessId);
        businessContext.Setup(x => x.GetRole()).Returns(role);

        var store = new Mock<IObjectStore>();
        store
            .Setup(x => x.PresignPutAsync(
                It.IsAny<ObjectStoreBucket>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                ObjectStoreBucket _,
                string key,
                string contentType,
                DateTimeOffset expiresAt,
                CancellationToken _) => new PresignedObjectWrite(
                    new Uri($"https://store.test/{key}"),
                    new Dictionary<string, string> { ["Content-Type"] = contentType },
                    expiresAt));

        var keys = new Mock<IMediaKeyFactory>();
        keys
            .Setup(x => x.CreatePendingKey(It.IsAny<PunchedApi.Domain.Entities.Media>()))
            .Returns((PunchedApi.Domain.Entities.Media media) => $"pending/{media.Id:N}.bin");

        var idempotency = new Mock<IIdempotencyService>();
        idempotency
            .Setup(x => x.TryGetAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(new IdempotencyLookupResult { Found = false });

        return new MediaService(
            context,
            store.Object,
            keys.Object,
            Mock.Of<IMediaUrlFactory>(),
            idempotency.Object,
            businessContext.Object,
            new PermissionService(),
            Options.Create(new MediaStorageOptions()));
    }
}
