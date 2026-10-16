using System.Text;
using Microsoft.AspNetCore.DataProtection;
using PunchedApi.Application.Notifications;

namespace PunchedApi.Tests.Notifications;

/// <summary>
/// Round-trips unsubscribe tokens through a reversible in-memory protector so the
/// payload grammar, expiry enforcement, and tamper handling are exercised without
/// touching the file system. The production service swaps in the real
/// data-protection layer with a different protector; only crypto strength differs.
/// </summary>
public class UnsubscribeTokenServiceTests
{
    private static IUnsubscribeTokenService CreateService() =>
        new UnsubscribeTokenService(new ReversingProtectionProvider());

    [Fact]
    public void Round_trip_preserves_user_and_business()
    {
        var service = CreateService();
        var userId = Guid.NewGuid();
        var businessId = Guid.NewGuid();

        var grant = service.ReadGrant(service.CreateToken(userId, businessId));

        Assert.NotNull(grant);
        Assert.Equal(userId, grant!.UserId);
        Assert.Equal(businessId, grant.BusinessId);
    }

    [Fact]
    public void Round_trip_preserves_a_global_scope_as_null_business()
    {
        var service = CreateService();
        var userId = Guid.NewGuid();

        var grant = service.ReadGrant(service.CreateToken(userId, null));

        Assert.NotNull(grant);
        Assert.Equal(userId, grant!.UserId);
        Assert.Null(grant.BusinessId);
    }

    [Fact]
    public void Tampered_or_empty_tokens_are_rejected()
    {
        var service = CreateService();
        var token = service.CreateToken(Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(service.ReadGrant(string.Empty));
        Assert.Null(service.ReadGrant("   "));
        Assert.Null(service.ReadGrant("not-a-token"));
        Assert.Null(service.ReadGrant(token[..^2] + "xx"));
    }

    [Fact]
    public void Expired_payload_is_rejected()
    {
        var service = CreateService();
        var expiredPayload = $"{Guid.NewGuid():D}|{Guid.NewGuid():D}|0";
        var token = Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(expiredPayload)));

        Assert.Null(service.ReadGrant(token));
    }

    private sealed class ReversingProtectionProvider : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new ReversingProtector();
    }

    private sealed class ReversingProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }
}
