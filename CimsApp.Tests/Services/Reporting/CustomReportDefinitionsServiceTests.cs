using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;
using RiskEntity = CimsApp.Models.Risk;

namespace CimsApp.Tests.Services.Reporting;

/// <summary>
/// Behavioural tests for <see cref="CustomReportDefinitionsService"/>
/// (T-S7-05). Covers CRUD + Run paths + per-entity field allow-list
/// validation + name uniqueness + cross-tenant 404. v1.0 ships
/// pure-equality filtering; richer operators are B-060.
/// </summary>
public class CustomReportDefinitionsServiceTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId) BuildFixture()
    {
        var orgId     = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = userId, GlobalRole = UserRole.OrgAdmin,
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
            Id = projectId, Name = "P", Code = "TP-1",
            AppointingPartyId = orgId, Currency = "GBP",
            Status = ProjectStatus.Execution,
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    private static (CustomReportDefinitionsService svc, CimsDbContext db) NewSvc(
        DbContextOptions<CimsDbContext> options, StubTenantContext tenant)
    {
        var db = new CimsDbContext(options, tenant);
        var audit = new AuditService(db);
        return (new CustomReportDefinitionsService(db, audit), db);
    }

    [Fact]
    public async Task CreateAsync_persists_definition_and_returns_dto()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, db) = NewSvc(options, tenant);

        var dto = await svc.CreateAsync(projectId,
            new CreateCustomReportDefinitionRequest(
                Name: "Open RFIs",
                EntityType: CustomReportEntityType.Rfi,
                FilterJson: """{"Status":"Open"}""",
                ColumnsJson: """["RfiNumber","Subject","Status"]"""),
            actorId: userId, ip: null, ua: null);

        Assert.Equal("Open RFIs",                       dto.Name);
        Assert.Equal(CustomReportEntityType.Rfi,        dto.EntityType);
        Assert.Equal("""{"Status":"Open"}""",          dto.FilterJson);

        var stored = await db.CustomReportDefinitions.SingleAsync();
        Assert.True(stored.IsActive);
        Assert.Equal(userId, stored.CreatedById);
    }

    [Fact]
    public async Task CreateAsync_rejects_duplicate_name_within_project()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, _) = NewSvc(options, tenant);
        await svc.CreateAsync(projectId,
            new CreateCustomReportDefinitionRequest("Open RFIs",
                CustomReportEntityType.Rfi, "{}", "[]"),
            userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.CreateAsync(projectId,
                new CreateCustomReportDefinitionRequest("Open RFIs",
                    CustomReportEntityType.Risk, "{}", "[]"),
                userId, null, null));
    }

    [Fact]
    public async Task CreateAsync_rejects_unknown_filter_field()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, _) = NewSvc(options, tenant);

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId,
                new CreateCustomReportDefinitionRequest(
                    "Bad",
                    CustomReportEntityType.Rfi,
                    """{"NotARealField":"x"}""",
                    "[]"),
                userId, null, null));
    }

    [Fact]
    public async Task CreateAsync_rejects_unknown_column()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, _) = NewSvc(options, tenant);

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId,
                new CreateCustomReportDefinitionRequest(
                    "Bad",
                    CustomReportEntityType.Risk,
                    "{}",
                    """["NotARealColumn"]"""),
                userId, null, null));
    }

    [Fact]
    public async Task UpdateAsync_changes_only_supplied_fields_and_emits_changedFields()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, _) = NewSvc(options, tenant);
        var created = await svc.CreateAsync(projectId,
            new CreateCustomReportDefinitionRequest(
                "v1", CustomReportEntityType.Rfi,
                """{"Status":"Open"}""",
                """["RfiNumber"]"""),
            userId, null, null);

        var updated = await svc.UpdateAsync(projectId, created.Id,
            new UpdateCustomReportDefinitionRequest(
                Name: null,
                FilterJson: """{"Status":"Closed"}""",
                ColumnsJson: null),
            userId, null, null);

        Assert.Equal("v1",                              updated.Name);
        Assert.Equal("""{"Status":"Closed"}""",        updated.FilterJson);
        Assert.Equal("""["RfiNumber"]""",              updated.ColumnsJson);
    }

    [Fact]
    public async Task UpdateAsync_rejects_no_op()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, _) = NewSvc(options, tenant);
        var created = await svc.CreateAsync(projectId,
            new CreateCustomReportDefinitionRequest(
                "v1", CustomReportEntityType.Rfi, "{}", "[]"),
            userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.UpdateAsync(projectId, created.Id,
                new UpdateCustomReportDefinitionRequest(null, null, null),
                userId, null, null));
    }

    [Fact]
    public async Task DeleteAsync_soft_deletes_and_frees_name_for_reuse()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, db) = NewSvc(options, tenant);
        var created = await svc.CreateAsync(projectId,
            new CreateCustomReportDefinitionRequest(
                "Reused", CustomReportEntityType.Risk, "{}", "[]"),
            userId, null, null);

        await svc.DeleteAsync(projectId, created.Id, userId, null, null);

        var stored = await db.CustomReportDefinitions.SingleAsync();
        Assert.False(stored.IsActive);

        // Name should be reusable now.
        var second = await svc.CreateAsync(projectId,
            new CreateCustomReportDefinitionRequest(
                "Reused", CustomReportEntityType.Variation, "{}", "[]"),
            userId, null, null);
        Assert.Equal("Reused", second.Name);
    }

    [Fact]
    public async Task ListAsync_returns_only_active_for_project_ordered_by_name()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, _) = NewSvc(options, tenant);
        await svc.CreateAsync(projectId,
            new CreateCustomReportDefinitionRequest("Bravo",
                CustomReportEntityType.Risk, "{}", "[]"),
            userId, null, null);
        await svc.CreateAsync(projectId,
            new CreateCustomReportDefinitionRequest("Alpha",
                CustomReportEntityType.Rfi, "{}", "[]"),
            userId, null, null);
        var charlie = await svc.CreateAsync(projectId,
            new CreateCustomReportDefinitionRequest("Charlie",
                CustomReportEntityType.Variation, "{}", "[]"),
            userId, null, null);
        await svc.DeleteAsync(projectId, charlie.Id, userId, null, null);

        var rows = await svc.ListAsync(projectId);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Alpha", rows[0].Name);
        Assert.Equal("Bravo", rows[1].Name);
    }

    [Fact]
    public async Task RunAsync_filters_and_projects_for_Risk_entity_type()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Risks.AddRange(
                new RiskEntity { ProjectId = projectId, Title = "A", Probability = 4, Impact = 5, Score = 20, Status = RiskStatus.Active },
                new RiskEntity { ProjectId = projectId, Title = "B", Probability = 4, Impact = 5, Score = 20, Status = RiskStatus.Active },
                new RiskEntity { ProjectId = projectId, Title = "C", Probability = 1, Impact = 1, Score = 1,  Status = RiskStatus.Closed });
            db.SaveChanges();
        }

        var (svc, _) = NewSvc(options, tenant);
        var def = await svc.CreateAsync(projectId,
            new CreateCustomReportDefinitionRequest(
                "Active high-score risks",
                CustomReportEntityType.Risk,
                """{"Status":"Active"}""",
                """["Title","Score","Status"]"""),
            userId, null, null);

        var result = await svc.RunAsync(projectId, def.Id);

        Assert.Equal(2, result.RowCount);
        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.Equal("Active", r["Status"]));
        Assert.Contains("Title", result.Columns);
        Assert.Contains("Score", result.Columns);
    }

    [Fact]
    public async Task RunAsync_with_empty_columns_projects_full_allow_list()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Rfis.Add(new Rfi
            {
                ProjectId = projectId, RfiNumber = "RFI-001", Subject = "x",
                Description = "y", Status = RfiStatus.Open,
                Priority = Priority.Medium, RaisedById = userId,
            });
            db.SaveChanges();
        }
        var (svc, _) = NewSvc(options, tenant);
        var def = await svc.CreateAsync(projectId,
            new CreateCustomReportDefinitionRequest(
                "All RFIs", CustomReportEntityType.Rfi, "{}", "[]"),
            userId, null, null);

        var result = await svc.RunAsync(projectId, def.Id);

        Assert.Equal(1, result.RowCount);
        Assert.Equal(
            CustomReportRunner.AllowedFields[CustomReportEntityType.Rfi].Count,
            result.Columns.Count);
    }

    [Fact]
    public async Task GetAsync_cross_tenant_lookup_404s()
    {
        var (options, _, _, _, _) = BuildFixture();
        var otherTenant = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            GlobalRole = UserRole.OrgAdmin,
        };
        var (svc, _) = NewSvc(options, otherTenant);

        await Assert.ThrowsAsync<NotFoundException>(
            () => svc.GetAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
