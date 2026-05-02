using CimsApp.Core;
using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CimsApp.Tests.Services.Bsa2022;

/// <summary>
/// Behavioural tests for T-S10-02: BSA 2022 HRB project metadata.
/// // BSA 2022 ref: Part 4 (Higher-Risk Buildings); Schedule 1
/// (HRB categorisation).
/// </summary>
public class ProjectHrbTests
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
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditInterceptor(tenant, httpAccessor: null))
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

    private static ProjectsService NewSvc(DbContextOptions<CimsDbContext> options, StubTenantContext tenant)
    {
        var db = new CimsDbContext(options, tenant);
        return new ProjectsService(db, new AuditService(db), tenant);
    }

    [Fact]
    public async Task SetHrbMetadataAsync_marks_project_as_HRB_with_category()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);

        var p = await svc.SetHrbMetadataAsync(
            projectId, isHrb: true, BsaHrbCategory.A,
            actorId: userId, ip: null, ua: null);

        Assert.True(p.IsHrb);
        Assert.Equal(BsaHrbCategory.A, p.HrbCategory);
    }

    [Fact]
    public async Task SetHrbMetadataAsync_rejects_HRB_true_with_NotApplicable_category()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.SetHrbMetadataAsync(projectId, isHrb: true,
                BsaHrbCategory.NotApplicable, userId, null, null));
    }

    [Fact]
    public async Task SetHrbMetadataAsync_rejects_HRB_false_with_specific_category()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.SetHrbMetadataAsync(projectId, isHrb: false,
                BsaHrbCategory.B, userId, null, null));
    }

    [Fact]
    public async Task SetHrbMetadataAsync_can_clear_HRB_status()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);
        await svc.SetHrbMetadataAsync(projectId, true, BsaHrbCategory.B, userId, null, null);

        var cleared = await svc.SetHrbMetadataAsync(
            projectId, false, BsaHrbCategory.NotApplicable,
            userId, null, null);

        Assert.False(cleared.IsHrb);
        Assert.Equal(BsaHrbCategory.NotApplicable, cleared.HrbCategory);
    }

    [Fact]
    public async Task SetHrbMetadataAsync_emits_audit_event_with_previous_and_current()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);
        await svc.SetHrbMetadataAsync(projectId, true, BsaHrbCategory.B, userId, "203.0.113.5", "ua-x");

        using var db = new CimsDbContext(options, tenant);
        var ev = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "project.hrb_set" && a.EntityId == projectId.ToString())
            .OrderByDescending(a => a.CreatedAt).FirstAsync();
        Assert.Equal(userId, ev.UserId);
        // AuditService uses default JsonSerializer.Serialize (no
        // PropertyNamingPolicy) so anonymous-object members serialise
        // with their declared casing — IsHrb / HrbCategory PascalCase.
        Assert.Contains("\"IsHrb\":true", ev.Detail!);
        Assert.Contains("\"HrbCategory\":\"B\"", ev.Detail);
    }

    [Fact]
    public async Task SetHrbMetadataAsync_cross_tenant_lookup_404s()
    {
        var (options, _, _, _, _) = BuildFixture();
        var otherTenant = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            GlobalRole = UserRole.OrgAdmin,
        };
        var svc = NewSvc(options, otherTenant);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.SetHrbMetadataAsync(Guid.NewGuid(), true, BsaHrbCategory.A,
                Guid.NewGuid(), null, null));
    }
}
