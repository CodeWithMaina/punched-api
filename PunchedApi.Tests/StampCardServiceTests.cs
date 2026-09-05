using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// Integration-style tests for stamp card + card design management using an
/// in-memory SQLite database (same provider family as production).
/// </summary>
public class StampCardServiceTests
{
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _businessId = Guid.NewGuid();
    private readonly ApplicationDbContext _context;
    private readonly UnitOfWork _unitOfWork;
    private readonly StampCardService _service;

    public StampCardServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _context = new ApplicationDbContext(options);
                _context.Database.OpenConnection();
        _context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        _context.Database.EnsureCreated();

        _unitOfWork = new UnitOfWork(_context);
        _service = new StampCardService(_unitOfWork, NullLogger<StampCardService>.Instance);

        SeedBusinessAndProgram();
    }

    private void SeedBusinessAndProgram()
    {
                                        var business = new Business
        {
            Id = _businessId,
                                    OwnerId = _ownerId,
            Name = "Java House",
            Category = "cafe",
            Location = "Nairobi, Nairobi County",
            MpesaNumber = "123456"
        };

        var owner = new User
        {
            Id = _ownerId,
            Email = "owner@example.com",
            FullName = "Peter Maina",
            Role = UserRole.Business,
            IsDeleted = false
        };

        var program = new LoyaltyProgram
        {
            Id = Guid.NewGuid(),
            BusinessId = _businessId,
            Name = "Coffee Rewards",
            IsActive = true,
            StampsRequired = 10,
            RewardValue = 50,
            RewardDescription = "Free Coffee",
            Status = ProgramStatus.Active,
            CreatedAt = DateTime.UtcNow
        };

                _context.Businesses.Add(business);
        _context.Users.Add(owner);
        _context.LoyaltyPrograms.Add(program);
        _context.SaveChanges();
    }

    private async Task<Guid> FirstProgramIdAsync() =>
        (await _context.LoyaltyPrograms.FirstAsync(p => p.BusinessId == _businessId)).Id;

        [Fact]
    public async Task MultipleStampCardsBelongToSingleCampaign()
    {
        var programId = await FirstProgramIdAsync();

        await _service.CreateStampCardAsync(_ownerId, programId, new CreateStampCardRequest
        { Name = "Coffee Card", StampsRequired = 10, RewardDescription = "Free Coffee", RewardValue = 50 });
        await _service.CreateStampCardAsync(_ownerId, programId, new CreateStampCardRequest
        { Name = "VIP Coffee Card", StampsRequired = 15, RewardDescription = "Free Large", RewardValue = 80 });
        await _service.CreateStampCardAsync(_ownerId, programId, new CreateStampCardRequest
        { Name = "Christmas Coffee Card", StampsRequired = 8, RewardDescription = "Free Mocha", RewardValue = 60 });

        var result = await _service.GetProgramStampCardsAsync(_ownerId, programId);

        Assert.True(result.Success);
        Assert.Equal(3, result.Data!.Count);
        Assert.Contains(result.Data, c => c.Name == "Coffee Card" && c.StampsRequired == 10);
        Assert.Contains(result.Data, c => c.Name == "VIP Coffee Card" && c.StampsRequired == 15);
        Assert.Contains(result.Data, c => c.Name == "Christmas Coffee Card" && c.StampsRequired == 8);
    }

        [Fact]
    public async Task CreateAndAssignReusableCardDesign()
    {
        var programId = await FirstProgramIdAsync();
        var design = await _service.CreateCardDesignAsync(_ownerId, new CreateCardDesignRequest
        {
            Name = "Christmas",
            HtmlTemplate = "<div class=\"card\">{{business.name}}</div><script>evil()</script>"
        });
        Assert.True(design.Success);
        Assert.DoesNotContain("script", design.Data!.HtmlTemplate, StringComparison.OrdinalIgnoreCase);

        var card = await _service.CreateStampCardAsync(_ownerId, programId, new CreateStampCardRequest
        {
            Name = "Christmas Card",
            StampsRequired = 12,
            RewardDescription = "Free Gift",
            RewardValue = 100,
            CardDesignId = design.Data.Id
        });
        Assert.True(card.Success);

        var fetched = await _service.GetStampCardAsync(_ownerId, card.Data!.Id);
        Assert.Equal("Christmas", fetched.Data!.CardDesignName);
    }

        [Fact]
    public async Task ActivateThenArchiveThenDelete_IsAllowed()
    {
        var programId = await FirstProgramIdAsync();
        var created = await _service.CreateStampCardAsync(_ownerId, programId, new CreateStampCardRequest
        { Name = "Card", StampsRequired = 5, RewardDescription = "R", RewardValue = 10 });
        var cardId = created.Data!.Id;

        await _service.UpdateStampCardStatusAsync(_ownerId, cardId, new UpdateStampCardStatusRequest { Status = "active" });
        var archive = await _service.UpdateStampCardStatusAsync(_ownerId, cardId, new UpdateStampCardStatusRequest { Status = "archived" });
        Assert.True(archive.Success);
        var delete = await _service.DeleteStampCardAsync(_ownerId, cardId);
        Assert.True(delete.Success);
    }

    [Fact]
    public async Task DeleteActiveCard_IsBlocked()
    {
        var programId = await FirstProgramIdAsync();
        var created = await _service.CreateStampCardAsync(_ownerId, programId, new CreateStampCardRequest
        { Name = "Card", StampsRequired = 5, RewardDescription = "R", RewardValue = 10 });
        // Default status is "draft" → delete must be blocked.
        var delete = await _service.DeleteStampCardAsync(_ownerId, created.Data!.Id);
        Assert.False(delete.Success);
        Assert.Equal("NOT_SAFE", delete.Error!.Code);
    }

    [Fact]
    public async Task Preview_RendersSanitizedTemplateAndVariables()
    {
        var design = await _service.CreateCardDesignAsync(_ownerId, new CreateCardDesignRequest
        { Name = "V", HtmlTemplate = "<div>{{business.name}}:{{customer.name}}:{{stamps}}</div>" });
        var preview = await _service.PreviewCardDesignAsync(_ownerId, new PreviewCardDesignRequest
        { CardDesignId = design.Data!.Id });

        Assert.True(preview.Success);
        Assert.Contains("Java House", preview.Data!.RenderedHtml);
        Assert.Contains("Peter Maina", preview.Data.RenderedHtml);
        Assert.Contains("stamp filled", preview.Data.RenderedHtml);
        Assert.NotEmpty(preview.Data.Variables);
    }

        [Fact]
    public async Task DeleteDesign_AssignedToCard_IsBlocked()
    {
        var programId = await FirstProgramIdAsync();
        var design = await _service.CreateCardDesignAsync(_ownerId, new CreateCardDesignRequest
        { Name = "D", HtmlTemplate = "<div>x</div>" });
        await _service.CreateStampCardAsync(_ownerId, programId, new CreateStampCardRequest
        { Name = "C", StampsRequired = 5, RewardDescription = "R", RewardValue = 10, CardDesignId = design.Data!.Id });

        var delete = await _service.DeleteCardDesignAsync(_ownerId, design.Data!.Id);
        Assert.False(delete.Success);
        Assert.Equal("IN_USE", delete.Error!.Code);
    }

    [Fact]
    public async Task OwnerCannotManageOtherBusinessCards()
    {
        var programId = await FirstProgramIdAsync();
        var otherOwner = Guid.NewGuid();
        var result = await _service.GetProgramStampCardsAsync(otherOwner, programId);
        Assert.False(result.Success);
    }

    public void Dispose()
    {
        _context.Database.CloseConnection();
        _context.Dispose();
        _unitOfWork.Dispose();
    }
}

