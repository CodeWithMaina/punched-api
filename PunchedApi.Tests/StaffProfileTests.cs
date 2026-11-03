using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Tests;

/// <summary>
/// The professional part of a staff profile: specialities and stated years of
/// experience, alongside the biography that already existed.
///
/// <para>Two properties matter most here. First, the fields are business-scoped —
/// a business can never edit another business's member. Second, an unstated value
/// stays unstated: the storefront must be able to tell "not provided" from "zero
/// years", because it renders nothing rather than inventing a number.</para>
/// </summary>
public partial class StaffManagementTests
{
    [Fact]
    public async Task Update_stores_specialities_and_experience_and_reads_them_back()
    {
        var result = await UpdateAsync(new UpdateStaffRequest
        {
            Specialities = "Balayage, Curly cuts, Bridal hair",
            YearsOfExperience = 9
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("Balayage, Curly cuts, Bridal hair", result.Data!.Specialities);
        Assert.Equal(9, result.Data.YearsOfExperience);

        // And they survive a fresh read.
        var reread = await DetailAsync(_member.Id);
        Assert.Equal("Balayage, Curly cuts, Bridal hair", reread.Data!.Specialities);
        Assert.Equal(9, reread.Data.YearsOfExperience);
    }

    [Fact]
    public async Task Update_clears_specialities_with_an_empty_string()
    {
        await UpdateAsync(new UpdateStaffRequest { Specialities = "Nails" });
        Assert.Equal("Nails", (await DetailAsync(_member.Id)).Data!.Specialities);

        var cleared = await UpdateAsync(new UpdateStaffRequest { Specialities = "   " });

        Assert.True(cleared.Success, cleared.Error?.Message);
        Assert.Null(cleared.Data!.Specialities);
    }

    [Fact]
    public async Task Update_leaves_experience_untouched_unless_it_is_explicitly_cleared()
    {
        await UpdateAsync(new UpdateStaffRequest { YearsOfExperience = 4 });

        // An update that says nothing about experience must not erase it.
        var untouched = await UpdateAsync(new UpdateStaffRequest { Bio = "Ten years on the floor." });
        Assert.Equal(4, untouched.Data!.YearsOfExperience);

        // Clearing is a deliberate instruction.
        var cleared = await UpdateAsync(new UpdateStaffRequest { ClearYearsOfExperience = true });
        Assert.Null(cleared.Data!.YearsOfExperience);
    }

    [Fact]
    public async Task Update_accepts_zero_experience_but_rejects_nonsense()
    {
        // Zero is a real answer (someone new to the trade), distinct from unset.
        var zero = await UpdateAsync(new UpdateStaffRequest { YearsOfExperience = 0 });
        Assert.True(zero.Success, zero.Error?.Message);
        Assert.Equal(0, zero.Data!.YearsOfExperience);

        var tooMany = await UpdateAsync(new UpdateStaffRequest { YearsOfExperience = 81 });
        Assert.Equal("VALIDATION_ERROR", tooMany.Error?.Code);

        var negative = await UpdateAsync(new UpdateStaffRequest { YearsOfExperience = -1 });
        Assert.Equal("VALIDATION_ERROR", negative.Error?.Code);

        // The rejected values were not written.
        Assert.Equal(0, (await DetailAsync(_member.Id)).Data!.YearsOfExperience);
    }

    [Fact]
    public async Task Update_cannot_touch_another_businesses_staff()
    {
        var result = await _staff.UpdateStaffAsync(
            _owner.Id, "Business", _otherMember.Id,
            new UpdateStaffRequest { Specialities = "Hijacked", YearsOfExperience = 30 });

        Assert.Equal("NOT_FOUND", result.Error?.Code);

        var other = await _context.BusinessMemberships.AsNoTracking()
            .FirstAsync(m => m.UserId == _otherMember.Id && m.BusinessId == _otherBusiness.Id);
        Assert.Null(other.Specialities);
        Assert.Null(other.YearsOfExperience);
    }

    [Fact]
    public async Task Public_roster_publishes_the_biography_specialities_and_experience()
    {
        await UpdateAsync(new UpdateStaffRequest
        {
            Bio = "Detail-obsessed nail artist.",
            Specialities = "Gel, Nail art",
            YearsOfExperience = 7,
            IsPublic = true
        });

        var catalog = BookingTestBase.CreateCatalogService(_context);
        var roster = await catalog.GetEligibleStaffAsync(_business.Id, Array.Empty<Guid>());

        Assert.True(roster.Success, roster.Error?.Message);
        var published = Assert.Single(roster.Data!);
        Assert.Equal(_member.Id, published.UserId);
        Assert.Equal("Detail-obsessed nail artist.", published.Bio);
        Assert.Equal("Gel, Nail art", published.Specialities);
        Assert.Equal(7, published.YearsOfExperience);
    }

    [Fact]
    public async Task Public_roster_never_publishes_a_private_members_profile()
    {
        await UpdateAsync(new UpdateStaffRequest
        {
            Bio = "Should never be public.",
            Specialities = "Secret skills",
            YearsOfExperience = 20,
            IsPublic = false
        });

        var catalog = BookingTestBase.CreateCatalogService(_context);
        var roster = await catalog.GetEligibleStaffAsync(_business.Id, Array.Empty<Guid>());

        Assert.True(roster.Success, roster.Error?.Message);
        Assert.Empty(roster.Data!);
    }
}
