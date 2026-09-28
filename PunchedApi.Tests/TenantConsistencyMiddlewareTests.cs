using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Moq;
using PunchedApi.API.Middleware;
using PunchedApi.Application.Services;

namespace PunchedApi.Tests;

/// <summary>
/// Tenant boundary matrix: the host selects the ACTIVE tenant; token and
/// membership decide whether the caller may operate inside it. Covers both
/// directions — authorized user + authorized tenant (pass, context populated)
/// and authorized user + unauthorized tenant (403 / 409) — plus the
/// compatibility guarantees: platform root never gated, anonymous never gated,
/// auth lifecycle always reachable, Platform Admin bypass intact.
/// </summary>
public class TenantConsistencyMiddlewareTests
{
    private const string TestBusiness = "11111111-1111-1111-1111-111111111111";
    private const string OtherBusiness = "22222222-2222-2222-2222-222222222222";

    private sealed class Outcome
    {
        public bool NextCalled { get; set; }
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? Body { get; set; }
        public TenantContext Tenant { get; init; } = new();
    }

    private sealed class RunOptions
    {
        public string? TenantHeader { get; init; }
        public string Path { get; init; } = "/v1/customers/me/stamp-cards";

        /// <summary>HTTP verb — the join-flow exemptions are verb-aware.</summary>
        public string Method { get; init; } = HttpMethods.Get;

        public string? QueryString { get; init; }
        public bool Authenticated { get; init; } = true;
        public string? Role { get; init; } = "Customer";
        public Guid? TokenBusinessId { get; init; }
        public Guid? RouteBusinessId { get; init; }
        public bool AnonymousEndpoint { get; init; }
        public BusinessMembership? Membership { get; init; }
        public bool IdentityClaimPresent { get; init; } = true;
    }

    private static Mock<ITenantHostResolver> CreateResolver()
    {
        var mock = new Mock<ITenantHostResolver>();
        mock.Setup(r => r.ParseSlug(It.IsAny<string?>()))
            .Returns((string? host) =>
                host != null && host.StartsWith("java-house.") ? "java-house" : null);
        mock.Setup(r => r.ResolveBusinessIdAsync("java-house"))
            .ReturnsAsync(Guid.Parse(TestBusiness));
        return mock;
    }

    private static Mock<IBusinessMembershipResolver> CreateMemberships(BusinessMembership? membership)
    {
        var mock = new Mock<IBusinessMembershipResolver>();
        mock.Setup(m => m.ResolveAsync(It.IsAny<Guid>(), Guid.Parse(TestBusiness)))
            .ReturnsAsync(membership ??
                new BusinessMembership(BusinessMembershipKind.None, "None", null,
                    Guid.Parse(TestBusiness)));
        return mock;
    }

    private static async Task<Outcome> RunAsync(RunOptions options)
    {
        var outcome = new Outcome { Tenant = new TenantContext() };
        var context = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
            User = new ClaimsPrincipal(new ClaimsIdentity())
        };
        context.Request.Path = options.Path;
        context.Request.Method = options.Method;
        if (options.QueryString != null)
            context.Request.QueryString = new QueryString(options.QueryString);

        if (options.TenantHeader != null)
            context.Request.Headers[TenantConsistencyMiddleware.TenantHeader] = options.TenantHeader;

        if (options.Authenticated)
        {
            var claims = new List<Claim>();
            if (options.IdentityClaimPresent)
                claims.Add(new Claim("userId", Guid.NewGuid().ToString()));
            if (options.Role != null)
                claims.Add(new Claim(ClaimTypes.Role, options.Role));
            if (options.TokenBusinessId.HasValue)
                claims.Add(new Claim(TenantClaimNames.BusinessId, options.TokenBusinessId.Value.ToString()));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        var metadata = options.AnonymousEndpoint
            ? new EndpointMetadataCollection(new AllowAnonymousAttribute())
            : new EndpointMetadataCollection();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test"));

        // Route values are read by context.GetRouteData().Values in the
        // middleware; DefaultHttpContext exposes Request.RouteValues directly.
        if (options.RouteBusinessId.HasValue)
            context.Request.RouteValues["businessId"] = options.RouteBusinessId.Value.ToString();

        var middleware = new TenantConsistencyMiddleware(
            _ =>
            {
                outcome.NextCalled = true;
                return Task.CompletedTask;
            },
            TestHelpers.CreateLogger<TenantConsistencyMiddleware>());

        await middleware.InvokeAsync(
            context,
            CreateResolver().Object,
            outcome.Tenant,
            CreateMemberships(options.Membership).Object);

        outcome.StatusCode = context.Response.StatusCode;
        if (!outcome.NextCalled)
        {
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            outcome.Body = await reader.ReadToEndAsync();
        }
        return outcome;
    }

    private static string? ErrorCode(string? body)
    {
        if (body == null) return null;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static BusinessMembership Membership(BusinessMembershipKind kind, string status = "Active") =>
        new(kind, status, kind.ToString(), Guid.Parse(TestBusiness));

    // ── Compatibility: root host, anonymous, auth lifecycle, admin ──────

    [Fact]
    public async Task AnonymousCallers_PassThrough_OnTenantHost()
    {
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Authenticated = false,
            Role = null
        });

        Assert.True(outcome.NextCalled);
        Assert.Equal(StatusCodes.Status200OK, outcome.StatusCode);
    }

    [Fact]
    public async Task RootHost_NoTenantHeader_NeverGated_AndContextInactive()
    {
        // Backward compatibility: without a tenant header the API behaves
        // exactly as before — no gate, inactive context, identity scoping only.
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = null,
            Role = "Customer",
            TokenBusinessId = Guid.Parse(OtherBusiness),
            Membership = Membership(BusinessMembershipKind.None, "None")
        });

        Assert.True(outcome.NextCalled);
        Assert.False(outcome.Tenant.IsActive);
        Assert.Null(outcome.Tenant.BusinessId);
    }

    [Fact]
    public async Task Member_AuthorizedTenant_Passes_AndTenantContextPopulated()
    {
        // authorized user + authorized tenant: the invariant chain runs and
        // downstream services can read the resolved tenant from ITenantContext.
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Role = "Customer",
            TokenBusinessId = Guid.Parse(TestBusiness),
            Membership = Membership(BusinessMembershipKind.Customer)
        });

        Assert.True(outcome.NextCalled);
        Assert.True(outcome.Tenant.IsActive);
        Assert.Equal("java-house", outcome.Tenant.Slug);
        Assert.Equal(Guid.Parse(TestBusiness), outcome.Tenant.BusinessId);
    }

    [Fact]
    public async Task AuthLifecycleEndpoints_AreAlwaysExempt_EvenOnMismatch()
    {
        foreach (var path in new[] { "/v1/auth/login", "/v1/auth/refresh-token", "/v1/auth/logout" })
        {
            var outcome = await RunAsync(new RunOptions
            {
                TenantHeader = "java-house.punched.app",
                Path = path,
                Role = "Customer",
                TokenBusinessId = Guid.Parse(OtherBusiness),
                Membership = Membership(BusinessMembershipKind.None, "None")
            });
            Assert.True(outcome.NextCalled, $"{path} must stay reachable for a mismatched user.");
        }
    }

    [Fact]
    public async Task UnknownHostSlug_IsIgnored_NotAnError()
    {
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "ghost.punched.app",
            Role = "Customer",
            TokenBusinessId = Guid.Parse(OtherBusiness),
            Membership = Membership(BusinessMembershipKind.None, "None")
        });

        Assert.True(outcome.NextCalled);
        Assert.False(outcome.Tenant.IsActive);
    }

    [Fact]
    public async Task AllowAnonymousStorefrontRead_Passes_EvenForNonMember()
    {
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Path = "/v1/businesses/public/anything",
            AnonymousEndpoint = true,
            Membership = Membership(BusinessMembershipKind.None, "None")
        });

        Assert.True(outcome.NextCalled);
        Assert.True(outcome.Tenant.IsActive); // context still populated for scoping
    }

    [Fact]
    public async Task IdentityProfileEndpoint_Passes_EvenForNonMember()
    {
        // Storefront session hydration (GET /v1/users/profile) must work for a
        // customer who is not a member of the host — identity is not business data.
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Path = "/v1/users/profile",
            Membership = Membership(BusinessMembershipKind.None, "None")
        });

        Assert.True(outcome.NextCalled);
    }

    [Fact]
    public async Task PlatformAdmin_BypassesMembershipGate()
    {
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Path = "/v1/admin/users",
            Role = "Admin",
            Membership = Membership(BusinessMembershipKind.None, "None")
        });

        Assert.True(outcome.NextCalled);
    }

    // ── The boundary: authorized user + UNAUTHORIZED tenant ─────────────

    [Fact]
    public async Task NonMember_WithoutForeignClaim_Returns403_TenantAccessDenied()
    {
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Role = "Customer",
            Membership = Membership(BusinessMembershipKind.None, "Left")
        });

        Assert.False(outcome.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, outcome.StatusCode);
        Assert.Equal("TENANT_ACCESS_DENIED", ErrorCode(outcome.Body));
    }

    [Fact]
    public async Task TokenBoundToOtherBusiness_Returns409_TenantMismatch()
    {
        // Preserves the existing frontend contract: the switch-workspace UX
        // keys off 409 TENANT_MISMATCH.
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Role = "Customer",
            TokenBusinessId = Guid.Parse(OtherBusiness),
            Membership = Membership(BusinessMembershipKind.None, "None")
        });

        Assert.False(outcome.NextCalled);
        Assert.Equal(StatusCodes.Status409Conflict, outcome.StatusCode);
        Assert.Equal("TENANT_MISMATCH", ErrorCode(outcome.Body));
    }

    // ── Role → required membership kind ─────────────────────────────────

    [Fact]
    public async Task BusinessRole_RequiresOwnerMembership()
    {
        var denied = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Role = "Business",
            Membership = Membership(BusinessMembershipKind.Customer)
        });
        Assert.False(denied.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);

        var allowed = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Role = "Business",
            Membership = Membership(BusinessMembershipKind.Owner)
        });
        Assert.True(allowed.NextCalled);
    }

    [Fact]
    public async Task StaffRole_RequiresStaffMembership()
    {
        var denied = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Role = "Staff",
            Membership = Membership(BusinessMembershipKind.Customer)
        });
        Assert.False(denied.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);

        var allowed = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Role = "Staff",
            Membership = Membership(BusinessMembershipKind.Staff)
        });
        Assert.True(allowed.NextCalled);
    }

    [Fact]
    public async Task CustomerRole_RequiresCustomerMembership()
    {
        var denied = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Role = "Customer",
            Membership = Membership(BusinessMembershipKind.Owner)
        });
        Assert.False(denied.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);

        var allowed = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Role = "Customer",
            Membership = Membership(BusinessMembershipKind.Customer)
        });
        Assert.True(allowed.NextCalled);
    }

    [Fact]
    public async Task MultiBusinessCustomer_ForeignClaim_MemberOfHost_Passes()
    {
        // A customer of Aurelia AND Java House may operate on java-house host
        // even while the token still carries the Aurelia `biz` claim: the
        // HOST (membership-verified) is the active tenant, not the claim.
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Role = "Customer",
            TokenBusinessId = Guid.Parse(OtherBusiness),
            Membership = Membership(BusinessMembershipKind.Customer)
        });

        Assert.True(outcome.NextCalled);
        Assert.Equal(Guid.Parse(TestBusiness), outcome.Tenant.BusinessId);
    }

    [Fact]
    public async Task LegacyToken_NoBizClaim_MemberOfHost_Passes_WithoutMismatch()
    {
        // Phase 5 acceptance: a legacy token (no `biz` claim at all — e.g.
        // minted before Phase 3) must behave exactly as today for a caller who
        // really belongs to the host. Membership decides; the absent claim is
        // never read as a mismatch.
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Role = "Customer",
            TokenBusinessId = null,
            Membership = Membership(BusinessMembershipKind.Customer)
        });

        Assert.True(outcome.NextCalled);
        Assert.Equal(Guid.Parse(TestBusiness), outcome.Tenant.BusinessId);
    }

    // ── Client-supplied businessId is never the boundary ────────────────

    [Fact]
    public async Task RouteBusinessId_DifferentFromTenant_Returns403_EvenForMember()
    {
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Method = HttpMethods.Post,
            Path = $"/v1/customers/me/businesses/{OtherBusiness}/enroll",
            RouteBusinessId = Guid.Parse(OtherBusiness),
            Role = "Customer",
            Membership = Membership(BusinessMembershipKind.Customer)
        });

        Assert.False(outcome.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, outcome.StatusCode);
        Assert.Equal("TENANT_ACCESS_DENIED", ErrorCode(outcome.Body));
    }

    [Fact]
    public async Task QueryBusinessId_DifferentFromTenant_Returns403()
    {
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Path = "/v1/customers/me/stamp-cards",
            QueryString = $"?businessId={OtherBusiness}",
            Membership = Membership(BusinessMembershipKind.Customer)
        });

        Assert.False(outcome.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, outcome.StatusCode);
    }

    [Fact]
    public async Task QueryBusinessId_EqualToTenant_Allows()
    {
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Path = "/v1/customers/me/stamp-cards",
            QueryString = $"?businessId={TestBusiness}",
            Membership = Membership(BusinessMembershipKind.Customer)
        });

        Assert.True(outcome.NextCalled);
    }

    // ── Join flows: membership-creating endpoints predate membership ────

    [Fact]
    public async Task JoinFlow_EnrollAllowed_ForNonMember()
    {
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Method = HttpMethods.Post,
            Path = $"/v1/customers/me/businesses/{TestBusiness}/enroll",
            RouteBusinessId = Guid.Parse(TestBusiness),
            Membership = Membership(BusinessMembershipKind.None, "None")
        });

        Assert.True(outcome.NextCalled);
    }

    [Fact]
    public async Task JoinFlow_MembershipReadAllowed_ForNonMember()
    {
        // GET /v1/customers/me/businesses must answer [] for a non-member —
        // the probe the frontend guard uses to bounce to the storefront.
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Path = "/v1/customers/me/businesses",
            Membership = Membership(BusinessMembershipKind.None, "None")
        });

        Assert.True(outcome.NextCalled);
    }

    [Fact]
    public async Task JoinFlow_LeaveAllowed_ForNonMember()
    {
        // DELETE /v1/customers/me/businesses/{businessId} is the inverse of
        // enrolling and is scoped to the caller's own enrollment row.
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Method = HttpMethods.Delete,
            Path = $"/v1/customers/me/businesses/{TestBusiness}",
            Membership = Membership(BusinessMembershipKind.None, "None")
        });

        Assert.True(outcome.NextCalled);
    }

    [Fact]
    public async Task JoinFlow_LoyaltyProgramEnrollAllowed_ForNonMember()
    {
        // POST /v1/cards/enroll — joining a loyalty program of the host
        // business; the body businessId is guarded in the service.
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Method = HttpMethods.Post,
            Path = "/v1/cards/enroll",
            Membership = Membership(BusinessMembershipKind.None, "None")
        });

        Assert.True(outcome.NextCalled);
    }

    [Theory]
    [InlineData("GET", "/v1/customers/me/stamp-cards")]
    [InlineData("GET", "/v1/customers/me/reviews")]
    [InlineData("POST", "/v1/customers/me/stamp-cards/33333333-3333-3333-3333-333333333333/join")]
    public async Task CustomerDataSurface_StaysGated_DespiteSharingTheJoinFlowNamespace(string method, string path)
    {
        // Regression guard: /v1/customers/me is a NAMESPACE, not a flow. Only
        // the membership lifecycle endpoints are exempt — stamp cards and
        // reviews must still require a membership of the host business.
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Method = method,
            Path = path,
            Membership = Membership(BusinessMembershipKind.None, "None")
        });

        Assert.False(outcome.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, outcome.StatusCode);
        Assert.Equal("TENANT_ACCESS_DENIED", ErrorCode(outcome.Body));
    }

    [Fact]
    public async Task JoinFlowExemption_IsShapeAndVerbExact()
    {
        // The bare-list probe is GET-only: the same path under a writing verb
        // neither creates nor reads a membership, so it stays gated.
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            Method = HttpMethods.Post,
            Path = "/v1/customers/me/businesses",
            Membership = Membership(BusinessMembershipKind.None, "None")
        });

        Assert.False(outcome.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, outcome.StatusCode);
    }

    [Fact]
    public async Task MissingIdentityClaim_Returns403_NotPassThrough()
    {
        var outcome = await RunAsync(new RunOptions
        {
            TenantHeader = "java-house.punched.app",
            IdentityClaimPresent = false,
            Membership = Membership(BusinessMembershipKind.Customer)
        });

        Assert.False(outcome.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, outcome.StatusCode);
        Assert.Equal("TENANT_ACCESS_DENIED", ErrorCode(outcome.Body));
    }
}
