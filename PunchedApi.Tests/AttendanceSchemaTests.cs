using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Phase 1 attendance schema tests (SQLite in-memory + EnsureCreated, the
/// EntitySchemaTests convention). Asserts the five tables, their columns,
/// the unique / partial-unique indexes and both check constraints — the
/// duplicate-clock-in and one-active-QR-per-location guarantees are enforced
/// at the DATABASE level, not in service code. PRAGMA foreign_keys = OFF so
/// parent rows (businesses/users) are not required; the session ↔ event FK
/// cycle is deliberately not exercised here.
/// </summary>
public class AttendanceSchemaTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _ctx;

    public AttendanceSchemaTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _ctx = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _ctx.Database.EnsureCreated();
        _ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _connection.Dispose();
    }

    // ── Tables + columns ──────────────────────────────────────

    [Fact]
    public void AttendanceTables_AreMapped_WithSnakeCaseNames()
    {
        Assert.Equal("attendance_policies", _ctx.Model.FindEntityType(typeof(AttendancePolicy))!.GetTableName());
        Assert.Equal("attendance_locations", _ctx.Model.FindEntityType(typeof(AttendanceLocation))!.GetTableName());
        Assert.Equal("attendance_qr_credentials", _ctx.Model.FindEntityType(typeof(AttendanceQrCredential))!.GetTableName());
        Assert.Equal("attendance_events", _ctx.Model.FindEntityType(typeof(AttendanceEvent))!.GetTableName());
        Assert.Equal("attendance_sessions", _ctx.Model.FindEntityType(typeof(AttendanceSession))!.GetTableName());

        var tables = _ctx.Database
            .SqlQuery<string>($"SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToList();

        foreach (var table in new[] { "attendance_policies", "attendance_locations", "attendance_qr_credentials", "attendance_events", "attendance_sessions" })
            Assert.Contains(table, tables);
    }

    [Fact]
    public void AttendanceColumns_ExistInDatabase()
    {
        AssertColumns("attendance_policies", "id", "business_id", "mode", "required_verifications_json", "is_active", "updated_at", "created_at");
        AssertColumns("attendance_locations", "id", "business_id", "name", "description", "is_active", "created_by_user_id", "created_at");
        AssertColumns("attendance_qr_credentials", "id", "business_id", "attendance_location_id", "token_hash", "status",
            "created_by_user_id", "revoked_at", "revoked_by_user_id", "last_used_at", "created_at");
        AssertColumns("attendance_events", "id", "business_id", "staff_user_id", "event_type", "occurred_at",
            "attendance_location_id", "attendance_qr_credential_id", "attendance_session_id", "source",
            "verification_summary_json", "client_idempotency_key", "created_by_user_id", "recorded_at", "created_at");
        AssertColumns("attendance_sessions", "id", "business_id", "staff_user_id", "opening_event_id", "closing_event_id",
            "opened_at", "closed_at", "opening_location_id", "closing_location_id", "worked_minutes", "status", "created_at");
    }

    private void AssertColumns(string table, params string[] expected)
    {
        var columns = _ctx.Database
            .SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({table})")
            .ToList();
        foreach (var column in expected)
            Assert.True(columns.Contains(column), $"Column '{column}' missing on table '{table}'.");
    }

    // ── Unique indexes ────────────────────────────────────────

    [Fact]
    public void AttendancePolicy_OnePerBusiness()
    {
        var businessId = Guid.NewGuid();
        _ctx.AttendancePolicies.Add(new AttendancePolicy { BusinessId = businessId });
        _ctx.SaveChanges();

        _ctx.AttendancePolicies.Add(new AttendancePolicy { BusinessId = businessId });
        Assert.ThrowsAny<DbUpdateException>(() => _ctx.SaveChanges());
        _ctx.ChangeTracker.Clear();
    }

    [Fact]
    public void QrCredential_TokenHash_IsUnique()
    {
        var hash = new string('a', 64);
        _ctx.AttendanceQrCredentials.Add(new AttendanceQrCredential
        {
            BusinessId = Guid.NewGuid(),
            AttendanceLocationId = Guid.NewGuid(),
            TokenHash = hash,
        });
        _ctx.SaveChanges();

        _ctx.AttendanceQrCredentials.Add(new AttendanceQrCredential
        {
            BusinessId = Guid.NewGuid(),
            AttendanceLocationId = Guid.NewGuid(),
            TokenHash = hash,
        });
        Assert.ThrowsAny<DbUpdateException>(() => _ctx.SaveChanges());
        _ctx.ChangeTracker.Clear();
    }

    // ── Partial unique: one ACTIVE credential per location ─────

    [Fact]
    public void QrCredential_OnlyOneActivePerLocation()
    {
        var businessId = Guid.NewGuid();
        var locationId = Guid.NewGuid();

        _ctx.AttendanceQrCredentials.Add(new AttendanceQrCredential
        {
            BusinessId = businessId,
            AttendanceLocationId = locationId,
            TokenHash = new string('1', 64),
            Status = AttendanceCredentialStatus.Active,
        });
        _ctx.SaveChanges();

        // Second ACTIVE credential at the same location — must be refused.
        _ctx.AttendanceQrCredentials.Add(new AttendanceQrCredential
        {
            BusinessId = businessId,
            AttendanceLocationId = locationId,
            TokenHash = new string('2', 64),
            Status = AttendanceCredentialStatus.Active,
        });
        Assert.ThrowsAny<DbUpdateException>(() => _ctx.SaveChanges());
        _ctx.ChangeTracker.Clear();

        // Revoking the first credential frees the slot (rotation works).
        var first = _ctx.AttendanceQrCredentials.Single(c => c.TokenHash == new string('1', 64));
        first.Status = AttendanceCredentialStatus.Revoked;
        first.RevokedAt = DateTime.UtcNow;
        _ctx.SaveChanges();

        // A new ACTIVE credential after revoke, plus multiple REVOKED
        // credentials at the same location, are all allowed.
        _ctx.AttendanceQrCredentials.Add(new AttendanceQrCredential
        {
            BusinessId = businessId,
            AttendanceLocationId = locationId,
            TokenHash = new string('2', 64),
            Status = AttendanceCredentialStatus.Active,
        });
        _ctx.SaveChanges();
        _ctx.ChangeTracker.Clear();

        _ctx.AttendanceQrCredentials.Add(new AttendanceQrCredential
        {
            BusinessId = businessId,
            AttendanceLocationId = locationId,
            TokenHash = new string('3', 64),
            Status = AttendanceCredentialStatus.Revoked,
        });
        _ctx.SaveChanges();
        _ctx.ChangeTracker.Clear();
    }

    // ── Partial unique: one OPEN session per staff member ──────

    [Fact]
    public void Session_OnlyOneOpenPerStaff()
    {
        var staffUserId = Guid.NewGuid();
        var openEventId = Guid.NewGuid();

        _ctx.AttendanceEvents.Add(new AttendanceEvent
        {
            BusinessId = Guid.NewGuid(),
            StaffUserId = staffUserId,
            EventType = AttendanceEventType.ClockIn,
            OccurredAt = DateTime.UtcNow.AddHours(-1),
        });
        _ctx.SaveChanges();

        _ctx.AttendanceSessions.Add(new AttendanceSession
        {
            BusinessId = Guid.NewGuid(),
            StaffUserId = staffUserId,
            OpeningEventId = openEventId,
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            Status = AttendanceSessionStatus.Open,
        });
        _ctx.SaveChanges();

        // Second OPEN session for the same staff — the duplicate clock-in
        // guarantee. Must be refused at the database level.
        _ctx.AttendanceSessions.Add(new AttendanceSession
        {
            BusinessId = Guid.NewGuid(),
            StaffUserId = staffUserId,
            OpeningEventId = Guid.NewGuid(),
            OpenedAt = DateTime.UtcNow,
            Status = AttendanceSessionStatus.Open,
        });
        Assert.ThrowsAny<DbUpdateException>(() => _ctx.SaveChanges());
        _ctx.ChangeTracker.Clear();

        // After closing the first session, a new open session is allowed.
        var first = _ctx.AttendanceSessions.Single(s => s.OpeningEventId == openEventId);
        first.ClosedAt = DateTime.UtcNow;
        first.ClosingEventId = Guid.NewGuid();
        first.Status = AttendanceSessionStatus.Closed;
        _ctx.SaveChanges();

        _ctx.AttendanceSessions.Add(new AttendanceSession
        {
            BusinessId = Guid.NewGuid(),
            StaffUserId = staffUserId,
            OpeningEventId = Guid.NewGuid(),
            OpenedAt = DateTime.UtcNow,
            Status = AttendanceSessionStatus.Open,
        });
        _ctx.SaveChanges();
        _ctx.ChangeTracker.Clear();
    }

    // ── Check constraints ─────────────────────────────────────

    [Fact]
    public void Event_ClockOut_RequiresSession()
    {
        // CLOCK_IN without a session is fine.
        _ctx.AttendanceEvents.Add(new AttendanceEvent
        {
            BusinessId = Guid.NewGuid(),
            StaffUserId = Guid.NewGuid(),
            EventType = AttendanceEventType.ClockIn,
            OccurredAt = DateTime.UtcNow,
            AttendanceSessionId = null,
        });
        _ctx.SaveChanges();
        _ctx.ChangeTracker.Clear();

        // CLOCK_OUT without a session — refused by the check constraint.
        _ctx.AttendanceEvents.Add(new AttendanceEvent
        {
            BusinessId = Guid.NewGuid(),
            StaffUserId = Guid.NewGuid(),
            EventType = AttendanceEventType.ClockOut,
            OccurredAt = DateTime.UtcNow,
            AttendanceSessionId = null,
        });
        Assert.ThrowsAny<DbUpdateException>(() => _ctx.SaveChanges());
        _ctx.ChangeTracker.Clear();

        // With a session attached, CLOCK_OUT is accepted.
        _ctx.AttendanceEvents.Add(new AttendanceEvent
        {
            BusinessId = Guid.NewGuid(),
            StaffUserId = Guid.NewGuid(),
            EventType = AttendanceEventType.ClockOut,
            OccurredAt = DateTime.UtcNow,
            AttendanceSessionId = Guid.NewGuid(),
        });
        _ctx.SaveChanges();
        _ctx.ChangeTracker.Clear();
    }

    [Fact]
    public void Session_CloseConsistency_IsEnforced()
    {
        // Closed without a closing event — refused.
        _ctx.AttendanceSessions.Add(new AttendanceSession
        {
            BusinessId = Guid.NewGuid(),
            StaffUserId = Guid.NewGuid(),
            OpeningEventId = Guid.NewGuid(),
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            ClosedAt = DateTime.UtcNow,
            ClosingEventId = null,
        });
        Assert.ThrowsAny<DbUpdateException>(() => _ctx.SaveChanges());
        _ctx.ChangeTracker.Clear();

        // Closed BEFORE it opened — refused.
        _ctx.AttendanceSessions.Add(new AttendanceSession
        {
            BusinessId = Guid.NewGuid(),
            StaffUserId = Guid.NewGuid(),
            OpeningEventId = Guid.NewGuid(),
            OpenedAt = DateTime.UtcNow,
            ClosedAt = DateTime.UtcNow.AddHours(-2),
            ClosingEventId = Guid.NewGuid(),
        });
        Assert.ThrowsAny<DbUpdateException>(() => _ctx.SaveChanges());
        _ctx.ChangeTracker.Clear();

        // Consistent close — accepted.
        _ctx.AttendanceSessions.Add(new AttendanceSession
        {
            BusinessId = Guid.NewGuid(),
            StaffUserId = Guid.NewGuid(),
            OpeningEventId = Guid.NewGuid(),
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            ClosedAt = DateTime.UtcNow,
            ClosingEventId = Guid.NewGuid(),
        });
        _ctx.SaveChanges();
        _ctx.ChangeTracker.Clear();
    }

    // ── Enum storage values ───────────────────────────────────

    [Fact]
    public void Enums_PersistAsString_MatchingCheckConstraintValues()
    {
        var staffUserId = Guid.NewGuid();
        _ctx.AttendanceEvents.Add(new AttendanceEvent
        {
            BusinessId = Guid.NewGuid(),
            StaffUserId = staffUserId,
            EventType = AttendanceEventType.ClockIn,
            OccurredAt = DateTime.UtcNow,
        });
        _ctx.SaveChanges();

        var stored = _ctx.Database
            .SqlQuery<string>($"SELECT event_type AS Value FROM attendance_events WHERE staff_user_id = {staffUserId}")
            .Single();

        // HasConversion<string>() persists the C# member name; the
        // chk_attendance_events_clockout_requires_session constraint
        // references exactly this stored value.
        Assert.Equal(nameof(AttendanceEventType.ClockIn), stored);
        _ctx.ChangeTracker.Clear();
    }
}

/// <summary>
/// Phase 3 schema extension: the full event ↔ session FK cycle with real
/// foreign keys ON, and the close-consistency + worked_minutes semantics the
/// service relies on (plan §18.7).
/// </summary>
public class AttendanceSessionLinkageTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _ctx;

    public AttendanceSessionLinkageTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _ctx = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void EventSessionLinkage_Persists_WithRealForeignKeys()
    {
        var now = DateTime.UtcNow;

        // Real parent rows (foreign keys are ON in this fixture) — committed
        // one by one so any FK problem points at a single row.
        var owner = BookingTestBase.CreateOwner("linkage-owner@schema.test");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Linkage Biz");
        var staff = BookingTestBase.CreateStaff(business.Id, "linkage-staff@schema.test");
        _ctx.Add(owner);
        _ctx.SaveChanges();
        _ctx.Add(business);
        _ctx.SaveChanges();
        _ctx.Add(staff);
        _ctx.SaveChanges();
        _ctx.ChangeTracker.Clear();

        // CLOCK_IN event → OPEN session with back-filled attendance_session_id.
        var clockIn = new AttendanceEvent
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            StaffUserId = staff.Id,
            EventType = AttendanceEventType.ClockIn,
            OccurredAt = now.AddHours(-8),
            CreatedByUserId = staff.Id, // FK → users
        };
        _ctx.AttendanceEvents.Add(clockIn);
        _ctx.SaveChanges();

        var session = new AttendanceSession
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            StaffUserId = staff.Id,
            OpeningEventId = clockIn.Id,
            OpenedAt = clockIn.OccurredAt,
            OpeningLocationId = null,
            Status = AttendanceSessionStatus.Open,
        };
        _ctx.AttendanceSessions.Add(session);
        clockIn.AttendanceSessionId = session.Id;
        _ctx.SaveChanges();
        _ctx.ChangeTracker.Clear();

        // CLOCK_OUT event closes the session and computes worked minutes (§9.5).
        var clockOut = new AttendanceEvent
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            StaffUserId = staff.Id,
            EventType = AttendanceEventType.ClockOut,
            OccurredAt = now,
            AttendanceSessionId = session.Id,
        };
        _ctx.AttendanceEvents.Add(clockOut);

        var tracked = _ctx.AttendanceSessions.Single(s => s.Id == session.Id);
        tracked.ClosingEventId = clockOut.Id;
        tracked.ClosedAt = clockOut.OccurredAt;
        tracked.WorkedMinutes = (int)Math.Floor((tracked.ClosedAt!.Value - tracked.OpenedAt).TotalMinutes);
        tracked.Status = AttendanceSessionStatus.Closed;
        _ctx.SaveChanges();
        _ctx.ChangeTracker.Clear();

        var closed = _ctx.AttendanceSessions.Single();
        Assert.Equal(AttendanceSessionStatus.Closed, closed.Status);
        Assert.Equal(clockOut.Id, closed.ClosingEventId);
        Assert.InRange(closed.WorkedMinutes!.Value, 479, 481); // 8 h in whole minutes

        // Both ledger rows reference the same session.
        Assert.Equal(2, _ctx.AttendanceEvents.Count(e => e.AttendanceSessionId == closed.Id));
    }

    [Fact]
    public void StaleAutoCloseShape_SatisfiesCloseConsistencyConstraint()
    {
        // The §9.2 auto-close (no fabricated CLOCK_OUT event): closed_at is set
        // with closing_event_id anchored to the OPENING event — accepted.
        var owner = BookingTestBase.CreateOwner("stale-owner@schema.test");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Stale Biz");
        var staff = BookingTestBase.CreateStaff(business.Id, "stale-staff@schema.test");
        _ctx.Add(owner);
        _ctx.SaveChanges();
        _ctx.Add(business);
        _ctx.SaveChanges();
        _ctx.Add(staff);
        _ctx.SaveChanges();
        _ctx.ChangeTracker.Clear();

        var openedAt = DateTime.UtcNow.AddHours(-17);
        var clockIn = new AttendanceEvent
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            StaffUserId = staff.Id,
            EventType = AttendanceEventType.ClockIn,
            OccurredAt = openedAt,
            CreatedByUserId = staff.Id, // FK → users
        };
        _ctx.AttendanceEvents.Add(clockIn);
        _ctx.SaveChanges();

        _ctx.AttendanceSessions.Add(new AttendanceSession
        {
            Id = Guid.NewGuid(),
            BusinessId = clockIn.BusinessId,
            StaffUserId = clockIn.StaffUserId,
            OpeningEventId = clockIn.Id,
            OpenedAt = openedAt,
            ClosedAt = openedAt.AddHours(16),
            ClosingEventId = clockIn.Id, // anchored — no synthetic event
            WorkedMinutes = 16 * 60,     // capped duration
            Status = AttendanceSessionStatus.Closed,
        });
        _ctx.SaveChanges();
        _ctx.ChangeTracker.Clear();

        var autoClosed = _ctx.AttendanceSessions.Single();
        Assert.Equal(openedAt.AddHours(16), autoClosed.ClosedAt);
        Assert.Equal(clockIn.Id, autoClosed.ClosingEventId);
        Assert.Equal(960, autoClosed.WorkedMinutes);
    }
}
