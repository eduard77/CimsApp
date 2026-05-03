using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CimsApp.Tests.Services.Alerts;

/// <summary>
/// T-S14-04 AlertRule CRUD behavioural tests. The threshold
/// evaluator (background hosted service) is tested separately
/// via the pure-logic ThresholdRule + AlertMetricComputer
/// helpers.
/// </summary>
public class AlertRuleServiceTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId, Guid recipientId) BuildFixture()
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
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
        seed.Organisations.Add(new Organisation { Id = orgId, Name = "O", Code = "O" });
        seed.Users.Add(new User
        {
            Id = userId, Email = $"u-{Guid.NewGuid():N}@e.com",
            PasswordHash = "x", FirstName = "T", LastName = "U",
            OrganisationId = orgId,
        });
        seed.Users.Add(new User
        {
            Id = recipientId, Email = $"r-{Guid.NewGuid():N}@e.com",
            PasswordHash = "x", FirstName = "Re", LastName = "Cv",
            OrganisationId = orgId,
        });
        seed.Projects.Add(new Project
        {
            Id = projectId, Name = "P", Code = "P-1",
            AppointingPartyId = orgId, Currency = "GBP",
            Status = ProjectStatus.Execution,
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId, recipientId);
    }

    [Fact]
    public async Task CreateAsync_persists_rule_with_defaults()
    {
        var (opts, tenant, _, userId, projectId, recipientId) = BuildFixture();
        var db = new CimsDbContext(opts, tenant);
        var svc = new AlertRuleService(db, new AuditService(db));

        var dto = await svc.CreateAsync(projectId,
            new CreateAlertRuleRequest("Cost > 110%",
                AlertMetric.CostUtilizationPercent, AlertComparison.GreaterThan,
                Threshold: 110m, RecipientUserId: recipientId, CooldownMinutes: null),
            userId, null, null);

        Assert.Equal("Cost > 110%", dto.Title);
        Assert.Equal(AlertMetric.CostUtilizationPercent, dto.Metric);
        Assert.Equal(60, dto.CooldownMinutes); // default
        Assert.True(dto.IsActive);
        Assert.Null(dto.LastFiredAt);
    }

    [Fact]
    public async Task CreateAsync_rejects_unknown_recipient()
    {
        var (opts, tenant, _, userId, projectId, _) = BuildFixture();
        var db = new CimsDbContext(opts, tenant);
        var svc = new AlertRuleService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() => svc.CreateAsync(projectId,
            new CreateAlertRuleRequest("X", AlertMetric.OpenRisks, AlertComparison.GreaterThan,
                Threshold: 5m, RecipientUserId: Guid.NewGuid(), CooldownMinutes: null),
            userId, null, null));
    }

    [Fact]
    public async Task CreateAsync_rejects_negative_threshold()
    {
        var (opts, tenant, _, userId, projectId, recipientId) = BuildFixture();
        var db = new CimsDbContext(opts, tenant);
        var svc = new AlertRuleService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() => svc.CreateAsync(projectId,
            new CreateAlertRuleRequest("X", AlertMetric.OpenRisks, AlertComparison.GreaterThan,
                Threshold: -1m, RecipientUserId: recipientId, CooldownMinutes: null),
            userId, null, null));
    }

    [Fact]
    public async Task UpdateAsync_changes_threshold_and_active_flag()
    {
        var (opts, tenant, _, userId, projectId, recipientId) = BuildFixture();
        var db = new CimsDbContext(opts, tenant);
        var svc = new AlertRuleService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateAlertRuleRequest("X", AlertMetric.OpenRisks, AlertComparison.GreaterThan,
                Threshold: 5m, RecipientUserId: recipientId, CooldownMinutes: 30),
            userId, null, null);

        var updated = await svc.UpdateAsync(projectId, dto.Id,
            new UpdateAlertRuleRequest(Title: null, Threshold: 10m,
                Comparison: null, RecipientUserId: null,
                CooldownMinutes: null, IsActive: false),
            userId, null, null);

        Assert.Equal(10m, updated.Threshold);
        Assert.False(updated.IsActive);
    }

    [Fact]
    public async Task DeleteAsync_soft_deletes()
    {
        var (opts, tenant, _, userId, projectId, recipientId) = BuildFixture();
        var db = new CimsDbContext(opts, tenant);
        var svc = new AlertRuleService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateAlertRuleRequest("X", AlertMetric.OpenRisks, AlertComparison.GreaterThan,
                Threshold: 5m, RecipientUserId: recipientId, CooldownMinutes: null),
            userId, null, null);

        await svc.DeleteAsync(projectId, dto.Id, userId, null, null);
        var list = await svc.ListAsync(projectId);
        Assert.Empty(list);
        // Row still exists in DB (soft delete via IsActive=false).
        Assert.Equal(1, await db.AlertRules.IgnoreQueryFilters().CountAsync());
    }
}
