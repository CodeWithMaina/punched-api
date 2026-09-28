using PunchedApi.Application.Media;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Tests;

public sealed class MediaPolicyTests
{
    private static readonly string[] Purposes =
    [
        MediaPurposes.BusinessLogo, MediaPurposes.BusinessCover, MediaPurposes.BusinessGallery,
        MediaPurposes.UserAvatar, MediaPurposes.ServiceImage, MediaPurposes.LoyaltyProgramImage,
        MediaPurposes.ReviewImage
    ];

    private static readonly string[] Relationships =
    [
        MediaRelationshipKinds.BusinessLogo, MediaRelationshipKinds.BusinessCover,
        MediaRelationshipKinds.BusinessGallery, MediaRelationshipKinds.UserAvatar,
        MediaRelationshipKinds.Service, MediaRelationshipKinds.LoyaltyProgram,
        MediaRelationshipKinds.Review
    ];

    public static IEnumerable<object[]> PurposeAndRelationshipMatrix()
    {
        foreach (var purpose in Purposes)
        foreach (var relationship in Relationships)
            yield return [purpose, relationship];
    }

    [Theory]
    [MemberData(nameof(PurposeAndRelationshipMatrix))]
    public void ExactlyOneRelationshipIsAllowedForEachPurpose(string purpose, string relationship)
    {
        Assert.Equal(1, Relationships.Count(x => MediaPolicy.AllowsRelationship(purpose, x)));
        if (MediaPolicy.AllowsRelationship(purpose, relationship)) return;
        Assert.Throws<InvalidOperationException>(() => MediaPolicy.RequireRelationship(purpose, relationship));
    }

    [Fact]
    public void OwnerMatrixIsExhaustive()
    {
        var businessId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        foreach (var purpose in Purposes)
        {
            var personal = purpose == MediaPurposes.UserAvatar;
            Assert.Equal(personal, MediaPolicy.RequiredOwner(purpose) == MediaPolicy.OwnerScope.Personal);
            Assert.Equal(personal, MediaPolicy.HasValidOwner(purpose, null, userId));
            Assert.Equal(!personal, MediaPolicy.HasValidOwner(purpose, businessId, null));
            Assert.False(MediaPolicy.HasValidOwner(purpose, businessId, userId));
            Assert.False(MediaPolicy.HasValidOwner(purpose, null, null));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => MediaPolicy.RequiredOwner("card-artwork"));
    }
}
