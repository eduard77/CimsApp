using System.Text;
using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Cost;

/// <summary>
/// Behavioural tests for <see cref="CostService.ImportCbsAsync"/>
/// (T-S1-03). The service-layer half landed in <c>b0ab2fb</c>; this
/// suite covers the happy path, every parser/validation branch
/// listed in the T-S1-03 handoff, the conflict guard for
/// re-import, and the tenant query-filter 404 for cross-tenant
/// project lookup.
/// </summary>
public class CostServiceTests
{
    private static readonly string Header =
        "Code,Name,ParentCode,Description,SortOrder";

    private static MemoryStream Csv(string body) =>
        new(Encoding.UTF8.GetBytes(body));

    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId) BuildFixture()
    {
        var orgId     = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId,
            UserId         = userId,
            GlobalRole     = UserRole.OrgAdmin,
        };
        var interceptor = new AuditInterceptor(tenant, httpAccessor: null);
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;

        using var seed = new CimsDbContext(options, tenant);
        seed.Organisations.Add(new Organisation { Id = orgId, Name = "Org", Code = "OG" });
        seed.Users.Add(new User
        {
            Id = userId, Email = $"u-{Guid.NewGuid():N}@example.com",
            PasswordHash = "x", FirstName = "T", LastName = "U",
            OrganisationId = orgId,
        });
        seed.Projects.Add(new Project
        {
            Id = projectId, Name = "Project", Code = "PR1",
            AppointingPartyId = orgId, Currency = "GBP",
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    [Fact]
    public async Task Happy_path_imports_tree_resolves_parents_and_writes_audit_row()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var csv = Csv($"{Header}\n1,Root,,Top-level,1\n1.1,Child A,1,,2\n1.2,Child B,1,,3\n");

        int rowsImported;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var r = await svc.ImportCbsAsync(projectId, csv, userId);
            rowsImported = r.RowsImported;
        }

        Assert.Equal(3, rowsImported);

        using var verify = new CimsDbContext(options, tenant);
        var items = verify.CostBreakdownItems.OrderBy(c => c.Code).ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("1",   items[0].Code);
        Assert.Equal("1.1", items[1].Code);
        Assert.Equal("1.2", items[2].Code);
        Assert.Null(items[0].ParentId);
        Assert.Equal(items[0].Id, items[1].ParentId);
        Assert.Equal(items[0].Id, items[2].ParentId);

        var imported = verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "cbs.imported").ToList();
        var audit = Assert.Single(imported);
        Assert.Equal("CostBreakdownItem", audit.Entity);
        Assert.Equal(projectId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Equal(userId, audit.UserId);
        Assert.NotNull(audit.Detail);
        Assert.Contains("\"rowCount\":3", audit.Detail);
    }

    [Fact]
    public async Task Empty_file_throws_ValidationException()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var csv = Csv("");

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ImportCbsAsync(projectId, csv, userId));
        Assert.Contains("CSV is empty", ex.Errors[0]);
    }

    [Fact]
    public async Task Header_mismatch_throws_ValidationException()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var csv = Csv("Wrong,Headers,Here,Description,SortOrder\n1,Root,,,1\n");

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ImportCbsAsync(projectId, csv, userId));
        Assert.Contains("CSV header must be: Code,Name,ParentCode,Description,SortOrder",
            ex.Errors[0]);
    }

    [Fact]
    public async Task Forward_reference_to_unseen_ParentCode_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        // Child appears before its parent — parser order is depth order,
        // so the lookup must fail.
        var csv = Csv($"{Header}\n1.1,Child,1,,1\n1,Root,,,2\n");

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ImportCbsAsync(projectId, csv, userId));
        Assert.Contains("Line 2: ParentCode '1' not found earlier in file", ex.Errors);
    }

    [Fact]
    public async Task Duplicate_Code_within_file_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var csv = Csv($"{Header}\n1,Root,,,1\n1,Dup,,,2\n");

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ImportCbsAsync(projectId, csv, userId));
        Assert.Contains("Line 3: duplicate Code '1'", ex.Errors);
    }

    [Fact]
    public async Task Non_integer_SortOrder_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var csv = Csv($"{Header}\n1,Root,,,foo\n");

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ImportCbsAsync(projectId, csv, userId));
        Assert.Contains("Line 2: SortOrder 'foo' is not a valid integer", ex.Errors[0]);
    }

    [Fact]
    public async Task Project_with_existing_CBS_rows_throws_ConflictException()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.CostBreakdownItems.Add(new CostBreakdownItem
            {
                ProjectId = projectId, Code = "X", Name = "Pre-existing",
            });
            seed.SaveChanges();
        }

        var csv = Csv($"{Header}\n1,Root,,,1\n");
        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.ImportCbsAsync(projectId, csv, userId));
    }

    [Fact]
    public async Task Cross_tenant_project_lookup_is_NotFound()
    {
        // Two tenants share an in-memory store. Project belongs to B; the
        // call comes from A. The Project query filter (AppointingPartyId
        // == _tenant.OrganisationId) hides B's project from A entirely,
        // and CostService.ImportCbsAsync surfaces that as NotFound rather
        // than leaking existence.
        var dbName    = Guid.NewGuid().ToString();
        var orgA      = Guid.NewGuid();
        var orgB      = Guid.NewGuid();
        var userA     = Guid.NewGuid();
        var userB     = Guid.NewGuid();
        var projectB  = Guid.NewGuid();

        var tenantA = new StubTenantContext { OrganisationId = orgA, UserId = userA };
        var tenantB = new StubTenantContext { OrganisationId = orgB, UserId = userB };

        var optionsB = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using (var seed = new CimsDbContext(optionsB, tenantB))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "A", Code = "TA" },
                new Organisation { Id = orgB, Name = "B", Code = "TB" });
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B Project", Code = "PB",
                AppointingPartyId = orgB, Currency = "GBP",
            });
            seed.SaveChanges();
        }

        var optionsA = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var csv = Csv($"{Header}\n1,Root,,,1\n");
        using var dbA = new CimsDbContext(optionsA, tenantA);
        var svc = new CostService(dbA, new AuditService(dbA));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.ImportCbsAsync(projectB, csv, userA));
    }

    // ── T-S1-04 SetLineBudgetAsync ───────────────────────────────────────────

    private static Guid SeedSingleLine(DbContextOptions<CimsDbContext> options,
        StubTenantContext tenant, Guid projectId)
    {
        var lineId = Guid.NewGuid();
        using var seed = new CimsDbContext(options, tenant);
        seed.CostBreakdownItems.Add(new CostBreakdownItem
        {
            Id = lineId, ProjectId = projectId, Code = "1", Name = "Root",
        });
        seed.SaveChanges();
        return lineId;
    }

    [Fact]
    public async Task SetLineBudget_writes_value_and_emits_audit_with_before_after()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var lineId = SeedSingleLine(options, tenant, projectId);

        // First write: previous == null, current == 12_345.67.
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            await svc.SetLineBudgetAsync(projectId, lineId, 12_345.67m, userId);
        }
        // Second write: previous == 12_345.67, current == 99_999.00 — verifies
        // the audit detail captures the actual previous value, not null.
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            await svc.SetLineBudgetAsync(projectId, lineId, 99_999.00m, userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var line = verify.CostBreakdownItems.Single(c => c.Id == lineId);
        Assert.Equal(99_999.00m, line.Budget);

        var audits = verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "cbs.line_budget_set")
            .OrderBy(a => a.CreatedAt).ToList();
        Assert.Equal(2, audits.Count);
        Assert.Equal("CostBreakdownItem", audits[0].Entity);
        Assert.Equal(lineId.ToString(), audits[0].EntityId);
        Assert.Equal(projectId, audits[0].ProjectId);
        Assert.Contains("\"previous\":null", audits[0].Detail);
        Assert.Contains("\"current\":12345.67", audits[0].Detail);
        Assert.Contains("\"previous\":12345.67", audits[1].Detail);
        Assert.Contains("\"current\":99999.00", audits[1].Detail);
    }

    [Fact]
    public async Task SetLineBudget_clearing_to_null_is_allowed_and_audited()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var lineId = SeedSingleLine(options, tenant, projectId);

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            await svc.SetLineBudgetAsync(projectId, lineId, 500m, userId);
            await svc.SetLineBudgetAsync(projectId, lineId, null, userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        Assert.Null(verify.CostBreakdownItems.Single(c => c.Id == lineId).Budget);
    }

    [Fact]
    public async Task SetLineBudget_negative_value_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var lineId = SeedSingleLine(options, tenant, projectId);

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.SetLineBudgetAsync(projectId, lineId, -1m, userId));
        Assert.Contains("Budget must be zero or greater", ex.Errors[0]);
    }

    [Fact]
    public async Task SetLineBudget_item_belonging_to_a_different_project_is_NotFound()
    {
        var (options, tenant, orgId, userId, projectA) = BuildFixture();
        // Second project under the SAME tenant — no cross-tenant filtering
        // here, just the (Id, ProjectId) tuple constraint inside the
        // service. A line on projectB must not be settable through projectA.
        var projectB = Guid.NewGuid();
        Guid lineOnB;
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "Project B", Code = "PR2",
                AppointingPartyId = orgId, Currency = "GBP",
            });
            var line = new CostBreakdownItem
            {
                ProjectId = projectB, Code = "1", Name = "B Root",
            };
            seed.CostBreakdownItems.Add(line);
            seed.SaveChanges();
            lineOnB = line.Id;
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.SetLineBudgetAsync(projectA, lineOnB, 100m, userId));
    }

    [Fact]
    public async Task SetLineBudget_cross_tenant_line_lookup_is_NotFound()
    {
        var dbName    = Guid.NewGuid().ToString();
        var orgA      = Guid.NewGuid();
        var orgB      = Guid.NewGuid();
        var userA     = Guid.NewGuid();
        var userB     = Guid.NewGuid();
        var projectB  = Guid.NewGuid();

        var tenantA = new StubTenantContext { OrganisationId = orgA, UserId = userA };
        var tenantB = new StubTenantContext { OrganisationId = orgB, UserId = userB };

        var optionsB = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        Guid lineOnB;
        using (var seed = new CimsDbContext(optionsB, tenantB))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "A", Code = "TA" },
                new Organisation { Id = orgB, Name = "B", Code = "TB" });
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B Project", Code = "PB",
                AppointingPartyId = orgB, Currency = "GBP",
            });
            var line = new CostBreakdownItem
            {
                ProjectId = projectB, Code = "1", Name = "B Root",
            };
            seed.CostBreakdownItems.Add(line);
            seed.SaveChanges();
            lineOnB = line.Id;
        }

        var optionsA = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using var dbA = new CimsDbContext(optionsA, tenantA);
        var svc = new CostService(dbA, new AuditService(dbA));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.SetLineBudgetAsync(projectB, lineOnB, 100m, userA));
    }

    // ── T-S1-05 CreateCommitmentAsync + GetCbsRollupAsync ────────────────────

    private static CreateCommitmentRequest NewCommitment(
        Guid lineId, decimal amount, CommitmentType type = CommitmentType.PO,
        string? reference = null, string? counterparty = null) =>
        new(lineId, type,
            reference    ?? $"PO-{Guid.NewGuid():N}".Substring(0, 12),
            counterparty ?? "Acme Co",
            amount, null);

    [Fact]
    public async Task CreateCommitment_writes_row_and_emits_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var lineId = SeedSingleLine(options, tenant, projectId);

        Guid commitmentId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var c = await svc.CreateCommitmentAsync(projectId,
                new CreateCommitmentRequest(lineId, CommitmentType.Subcontract,
                    "SC-001", "BuildCo Ltd", 250_000m, "Phase 1 groundworks"),
                userId);
            commitmentId = c.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var stored = verify.Commitments.Single(c => c.Id == commitmentId);
        Assert.Equal(projectId, stored.ProjectId);
        Assert.Equal(lineId, stored.CostBreakdownItemId);
        Assert.Equal(CommitmentType.Subcontract, stored.Type);
        Assert.Equal("SC-001", stored.Reference);
        Assert.Equal("BuildCo Ltd", stored.Counterparty);
        Assert.Equal(250_000m, stored.Amount);

        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "commitment.created"));
        Assert.Equal("Commitment", audit.Entity);
        Assert.Equal(commitmentId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.NotNull(audit.Detail);
        Assert.Contains("\"type\":\"Subcontract\"", audit.Detail);
        Assert.Contains("\"amount\":250000", audit.Detail);
        Assert.Contains("\"reference\":\"SC-001\"", audit.Detail);
    }

    [Fact]
    public async Task CreateCommitment_non_positive_amount_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var lineId = SeedSingleLine(options, tenant, projectId);

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var ex0 = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateCommitmentAsync(projectId, NewCommitment(lineId, 0m), userId));
        Assert.Contains("Amount must be greater than zero", ex0.Errors[0]);
        var exNeg = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateCommitmentAsync(projectId, NewCommitment(lineId, -100m), userId));
        Assert.Contains("Amount must be greater than zero", exNeg.Errors[0]);
    }

    [Fact]
    public async Task CreateCommitment_required_fields_are_validated()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var lineId = SeedSingleLine(options, tenant, projectId);

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var exRef = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateCommitmentAsync(projectId,
                new CreateCommitmentRequest(lineId, CommitmentType.PO, "", "Acme", 10m, null), userId));
        Assert.Contains("Reference is required", exRef.Errors);
        var exParty = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateCommitmentAsync(projectId,
                new CreateCommitmentRequest(lineId, CommitmentType.PO, "PO-1", "  ", 10m, null), userId));
        Assert.Contains("Counterparty is required", exParty.Errors);
    }

    [Fact]
    public async Task CreateCommitment_line_belonging_to_a_different_project_is_NotFound()
    {
        var (options, tenant, orgId, userId, projectA) = BuildFixture();
        var projectB = Guid.NewGuid();
        Guid lineOnB;
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "Project B", Code = "PR2",
                AppointingPartyId = orgId, Currency = "GBP",
            });
            var line = new CostBreakdownItem
            {
                ProjectId = projectB, Code = "1", Name = "B Root",
            };
            seed.CostBreakdownItems.Add(line);
            seed.SaveChanges();
            lineOnB = line.Id;
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.CreateCommitmentAsync(projectA, NewCommitment(lineOnB, 100m), userId));
    }

    [Fact]
    public async Task CreateCommitment_cross_tenant_line_lookup_is_NotFound()
    {
        var dbName    = Guid.NewGuid().ToString();
        var orgA      = Guid.NewGuid();
        var orgB      = Guid.NewGuid();
        var userA     = Guid.NewGuid();
        var userB     = Guid.NewGuid();
        var projectB  = Guid.NewGuid();

        var tenantA = new StubTenantContext { OrganisationId = orgA, UserId = userA };
        var tenantB = new StubTenantContext { OrganisationId = orgB, UserId = userB };

        var optionsB = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        Guid lineOnB;
        using (var seed = new CimsDbContext(optionsB, tenantB))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "A", Code = "TA" },
                new Organisation { Id = orgB, Name = "B", Code = "TB" });
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B Project", Code = "PB",
                AppointingPartyId = orgB, Currency = "GBP",
            });
            var line = new CostBreakdownItem
            {
                ProjectId = projectB, Code = "1", Name = "B Root",
            };
            seed.CostBreakdownItems.Add(line);
            seed.SaveChanges();
            lineOnB = line.Id;
        }

        var optionsA = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using var dbA = new CimsDbContext(optionsA, tenantA);
        var svc = new CostService(dbA, new AuditService(dbA));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.CreateCommitmentAsync(projectB, NewCommitment(lineOnB, 100m), userA));
    }

    [Fact]
    public async Task GetCbsRollup_returns_committed_sum_and_variance_per_line()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        // Two lines: line1 with Budget=1000 + two POs (300 + 400);
        // line2 unbudgeted with one Subcontract (150).
        Guid line1Id, line2Id;
        using (var seed = new CimsDbContext(options, tenant))
        {
            var l1 = new CostBreakdownItem
            {
                ProjectId = projectId, Code = "1", Name = "Line 1", Budget = 1000m,
            };
            var l2 = new CostBreakdownItem
            {
                ProjectId = projectId, Code = "2", Name = "Line 2",
            };
            seed.CostBreakdownItems.AddRange(l1, l2);
            seed.SaveChanges();
            line1Id = l1.Id; line2Id = l2.Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            await svc.CreateCommitmentAsync(projectId,
                new CreateCommitmentRequest(line1Id, CommitmentType.PO, "PO-A", "X", 300m, null), userId);
            await svc.CreateCommitmentAsync(projectId,
                new CreateCommitmentRequest(line1Id, CommitmentType.PO, "PO-B", "Y", 400m, null), userId);
            await svc.CreateCommitmentAsync(projectId,
                new CreateCommitmentRequest(line2Id, CommitmentType.Subcontract, "SC-A", "Z", 150m, null), userId);
        }

        List<CbsLineRollupDto> rollup;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            rollup = await svc.GetCbsRollupAsync(projectId);
        }

        Assert.Equal(2, rollup.Count);
        var r1 = rollup.Single(r => r.ItemId == line1Id);
        Assert.Equal(1000m, r1.Budget);
        Assert.Equal(700m, r1.Committed);
        Assert.Equal(300m, r1.Variance);

        var r2 = rollup.Single(r => r.ItemId == line2Id);
        Assert.Null(r2.Budget);
        Assert.Equal(150m, r2.Committed);
        // Variance is null when no budget is set — under-/over-spend
        // against an unbudgeted line is not meaningful.
        Assert.Null(r2.Variance);
    }

    [Fact]
    public async Task GetCbsRollup_returns_zero_committed_for_lines_with_no_commitments()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        var lineId = SeedSingleLine(options, tenant, projectId);
        using (var seed = new CimsDbContext(options, tenant))
        {
            // Set a budget but no commitments yet.
            var line = seed.CostBreakdownItems.Single(c => c.Id == lineId);
            line.Budget = 5_000m;
            seed.SaveChanges();
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var rollup = await svc.GetCbsRollupAsync(projectId);

        var row = Assert.Single(rollup);
        Assert.Equal(0m, row.Committed);
        Assert.Equal(5_000m, row.Variance);
    }

    [Fact]
    public async Task GetCbsRollup_cross_tenant_project_is_NotFound()
    {
        var dbName    = Guid.NewGuid().ToString();
        var orgA      = Guid.NewGuid();
        var orgB      = Guid.NewGuid();
        var userA     = Guid.NewGuid();
        var userB     = Guid.NewGuid();
        var projectB  = Guid.NewGuid();

        var tenantA = new StubTenantContext { OrganisationId = orgA, UserId = userA };
        var tenantB = new StubTenantContext { OrganisationId = orgB, UserId = userB };

        var optionsB = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using (var seed = new CimsDbContext(optionsB, tenantB))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "A", Code = "TA" },
                new Organisation { Id = orgB, Name = "B", Code = "TB" });
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B Project", Code = "PB",
                AppointingPartyId = orgB, Currency = "GBP",
            });
            seed.SaveChanges();
        }

        var optionsA = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using var dbA = new CimsDbContext(optionsA, tenantA);
        var svc = new CostService(dbA, new AuditService(dbA));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.GetCbsRollupAsync(projectB));
    }

    // ── T-S1-06 CostPeriod + ActualCost ──────────────────────────────────────

    private static CreatePeriodRequest NewPeriod(
        string label = "2026-04",
        DateTime? start = null, DateTime? end = null) =>
        new(label,
            start ?? new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            end   ?? new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task CreatePeriod_writes_row_and_emits_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid periodId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var p = await svc.CreatePeriodAsync(projectId,
                NewPeriod(label: "April 2026"), userId);
            periodId = p.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var stored = verify.CostPeriods.Single(p => p.Id == periodId);
        Assert.Equal(projectId, stored.ProjectId);
        Assert.Equal("April 2026", stored.Label);
        Assert.False(stored.IsClosed);
        Assert.Null(stored.ClosedAt);

        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "cost_period.opened"));
        Assert.Equal("CostPeriod", audit.Entity);
        Assert.Equal(periodId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Contains("\"label\":\"April 2026\"", audit.Detail);
    }

    [Fact]
    public async Task CreatePeriod_StartDate_must_be_before_EndDate()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var sameDay = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("X", sameDay, sameDay), userId));
        Assert.Contains("StartDate must be before EndDate", ex.Errors[0]);
    }

    [Fact]
    public async Task ClosePeriod_marks_closed_with_audit_and_double_close_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid periodId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var p = await svc.CreatePeriodAsync(projectId, NewPeriod(), userId);
            periodId = p.Id;
            await svc.ClosePeriodAsync(projectId, periodId, userId);
        }

        using (var verify = new CimsDbContext(options, tenant))
        {
            var period = verify.CostPeriods.Single(p => p.Id == periodId);
            Assert.True(period.IsClosed);
            Assert.NotNull(period.ClosedAt);
            Assert.Equal(userId, period.ClosedById);
            Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
                .Where(a => a.Action == "cost_period.closed"));
        }

        // Second close on the same period must fail — the close is a
        // one-way integrity boundary in v1.0.
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            await Assert.ThrowsAsync<ConflictException>(() =>
                svc.ClosePeriodAsync(projectId, periodId, userId));
        }
    }

    [Fact]
    public async Task RecordActual_writes_row_and_emits_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var lineId = SeedSingleLine(options, tenant, projectId);

        Guid periodId, actualId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var p = await svc.CreatePeriodAsync(projectId, NewPeriod(), userId);
            periodId = p.Id;
            var a = await svc.RecordActualAsync(projectId,
                new RecordActualRequest(lineId, periodId, 4_321.00m, "INV-99", "April invoice"),
                userId);
            actualId = a.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var stored = verify.ActualCosts.Single(a => a.Id == actualId);
        Assert.Equal(projectId, stored.ProjectId);
        Assert.Equal(lineId, stored.CostBreakdownItemId);
        Assert.Equal(periodId, stored.PeriodId);
        Assert.Equal(4_321.00m, stored.Amount);
        Assert.Equal("INV-99", stored.Reference);

        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "actual_cost.recorded"));
        Assert.Equal("ActualCost", audit.Entity);
        Assert.Equal(actualId.ToString(), audit.EntityId);
        Assert.Contains("\"amount\":4321.00", audit.Detail);
        Assert.Contains("\"reference\":\"INV-99\"", audit.Detail);
    }

    [Fact]
    public async Task RecordActual_non_positive_amount_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var lineId = SeedSingleLine(options, tenant, projectId);

        Guid periodId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var p = await svc.CreatePeriodAsync(projectId, NewPeriod(), userId);
            periodId = p.Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CostService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.RecordActualAsync(projectId,
                new RecordActualRequest(lineId, periodId, 0m, null, null), userId));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.RecordActualAsync(projectId,
                new RecordActualRequest(lineId, periodId, -10m, null, null), userId));
    }

    [Fact]
    public async Task RecordActual_against_closed_period_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var lineId = SeedSingleLine(options, tenant, projectId);

        Guid periodId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var p = await svc.CreatePeriodAsync(projectId, NewPeriod(), userId);
            periodId = p.Id;
            await svc.ClosePeriodAsync(projectId, periodId, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CostService(db2, new AuditService(db2));
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.RecordActualAsync(projectId,
                new RecordActualRequest(lineId, periodId, 100m, null, null), userId));
        Assert.Contains("Period is closed", ex.Message);
    }

    [Fact]
    public async Task RecordActual_line_in_wrong_project_is_NotFound()
    {
        var (options, tenant, orgId, userId, projectA) = BuildFixture();
        var projectB = Guid.NewGuid();
        Guid lineOnB, periodOnA;
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B", Code = "PR2",
                AppointingPartyId = orgId, Currency = "GBP",
            });
            var line = new CostBreakdownItem
            {
                ProjectId = projectB, Code = "1", Name = "B Root",
            };
            seed.CostBreakdownItems.Add(line);
            seed.SaveChanges();
            lineOnB = line.Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var p = await svc.CreatePeriodAsync(projectA, NewPeriod(), userId);
            periodOnA = p.Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CostService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.RecordActualAsync(projectA,
                new RecordActualRequest(lineOnB, periodOnA, 100m, null, null), userId));
    }

    [Fact]
    public async Task RecordActual_period_in_wrong_project_is_NotFound()
    {
        var (options, tenant, orgId, userId, projectA) = BuildFixture();
        var lineOnA = SeedSingleLine(options, tenant, projectA);
        var projectB = Guid.NewGuid();
        Guid periodOnB;
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B", Code = "PR2",
                AppointingPartyId = orgId, Currency = "GBP",
            });
            seed.SaveChanges();
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var p = await svc.CreatePeriodAsync(projectB, NewPeriod(), userId);
            periodOnB = p.Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CostService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.RecordActualAsync(projectA,
                new RecordActualRequest(lineOnA, periodOnB, 100m, null, null), userId));
    }

    [Fact]
    public async Task GetCbsRollup_includes_actual_sum_per_line()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid line1Id, line2Id;
        using (var seed = new CimsDbContext(options, tenant))
        {
            var l1 = new CostBreakdownItem
            {
                ProjectId = projectId, Code = "1", Name = "Line 1", Budget = 1000m,
            };
            var l2 = new CostBreakdownItem
            {
                ProjectId = projectId, Code = "2", Name = "Line 2",
            };
            seed.CostBreakdownItems.AddRange(l1, l2);
            seed.SaveChanges();
            line1Id = l1.Id; line2Id = l2.Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            // Two periods. Actuals on both close-then-stay-summed.
            var p1 = await svc.CreatePeriodAsync(projectId, NewPeriod("Apr"), userId);
            var p2 = await svc.CreatePeriodAsync(projectId,
                NewPeriod("May",
                    new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc)),
                userId);
            await svc.RecordActualAsync(projectId,
                new RecordActualRequest(line1Id, p1.Id, 200m, "INV-A", null), userId);
            await svc.RecordActualAsync(projectId,
                new RecordActualRequest(line1Id, p2.Id, 100m, "INV-B", null), userId);
            await svc.ClosePeriodAsync(projectId, p1.Id, userId);
            // p1 closed but its actuals still count in the rollup.
            await svc.RecordActualAsync(projectId,
                new RecordActualRequest(line2Id, p2.Id, 50m, "INV-C", null), userId);
        }

        List<CbsLineRollupDto> rollup;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            rollup = await svc.GetCbsRollupAsync(projectId);
        }

        var r1 = rollup.Single(r => r.ItemId == line1Id);
        Assert.Equal(300m, r1.Actual);
        var r2 = rollup.Single(r => r.ItemId == line2Id);
        Assert.Equal(50m, r2.Actual);
    }
}
