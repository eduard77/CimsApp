using System.Text;
using CimsApp.Core;
using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Schedule;

/// <summary>
/// Behavioural tests for <see cref="ScheduleService.ImportFromMsProjectAsync"/>
/// (T-S4-09). Covers the empty-project happy path, the
/// non-empty-rejected guard, dependency mapping with mixed types,
/// warnings on unresolved UIDs, and cross-tenant 404 via the query
/// filter.
/// </summary>
public class ScheduleServiceImportTests
{
    private const string Ns = "http://schemas.microsoft.com/project";

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
            StartDate = new DateTime(2026, 6, 1),
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    private static MemoryStream Xml(string content) =>
        new(Encoding.UTF8.GetBytes(content));

    // ── Happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task ImportFromMsProjectAsync_into_empty_project_inserts_activities_and_deps()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Name>Pilot Programme</Name>
  <StartDate>2026-06-01T08:00:00</StartDate>
  <Tasks>
    <Task>
      <UID>1</UID><Name>Excavation</Name><Duration>PT40H0M0S</Duration>
    </Task>
    <Task>
      <UID>2</UID><Name>Foundations</Name><Duration>PT24H0M0S</Duration>
      <PredecessorLink>
        <PredecessorUID>1</PredecessorUID><Type>1</Type><LinkLag>0</LinkLag>
      </PredecessorLink>
    </Task>
    <Task>
      <UID>3</UID><Name>Frame</Name><Duration>PT80H0M0S</Duration>
      <PredecessorLink>
        <PredecessorUID>2</PredecessorUID><Type>1</Type><LinkLag>0</LinkLag>
      </PredecessorLink>
    </Task>
  </Tasks>
</Project>";

        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            var result = await svc.ImportFromMsProjectAsync(projectId, Xml(xml), userId);
            Assert.Equal("Pilot Programme",  result.ProjectName);
            Assert.Equal(3, result.ActivitiesImported);
            Assert.Equal(2, result.DependenciesImported);
            Assert.Empty(result.Warnings);
        }

        using var verify = new CimsDbContext(options, tenant);
        var acts = await verify.Activities.Where(a => a.ProjectId == projectId).ToListAsync();
        Assert.Equal(3, acts.Count);
        // Codes prefixed MSP-{UID}
        Assert.Contains(acts, a => a.Code == "MSP-1" && a.Duration == 5m);
        Assert.Contains(acts, a => a.Code == "MSP-2" && a.Duration == 3m);
        Assert.Contains(acts, a => a.Code == "MSP-3" && a.Duration == 10m);

        var deps = await verify.Dependencies.Where(d => d.ProjectId == projectId).ToListAsync();
        Assert.Equal(2, deps.Count);
        Assert.All(deps, d => Assert.Equal(DependencyType.FS, d.Type));
    }

    [Fact]
    public async Task ImportFromMsProjectAsync_emits_schedule_imported_audit()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Name>Audit Test</Name>
  <Tasks>
    <Task><UID>1</UID><Name>Single</Name><Duration>PT8H0M0S</Duration></Task>
  </Tasks>
</Project>";
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.ImportFromMsProjectAsync(projectId, Xml(xml), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "schedule.imported");
        Assert.Contains("\"source\":\"MsProjectXml\"",      row.Detail!);
        Assert.Contains("\"activitiesImported\":1",         row.Detail);
        Assert.Contains("\"dependenciesImported\":0",       row.Detail);
        Assert.Contains("\"projectName\":\"Audit Test\"",   row.Detail);
    }

    // ── Guards ──────────────────────────────────────────────────────

    [Fact]
    public async Task ImportFromMsProjectAsync_rejects_when_project_already_has_activities()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Tasks><Task><UID>1</UID><Name>X</Name><Duration>PT8H0M0S</Duration></Task></Tasks>
</Project>";
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Activities.Add(new Activity
            {
                ProjectId = projectId, Code = "PRE", Name = "Existing", Duration = 1m,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.ImportFromMsProjectAsync(projectId, Xml(xml), userId));
    }

    [Fact]
    public async Task ImportFromMsProjectAsync_rejects_malformed_XML_with_validation_error()
    {
        var xml = "this is not xml";
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ImportFromMsProjectAsync(projectId, Xml(xml), userId));
    }

    [Fact]
    public async Task ImportFromMsProjectAsync_warns_on_unresolved_predecessor_UID()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Tasks>
    <Task>
      <UID>1</UID><Name>Solo</Name><Duration>PT8H0M0S</Duration>
      <PredecessorLink>
        <PredecessorUID>999</PredecessorUID><Type>1</Type><LinkLag>0</LinkLag>
      </PredecessorLink>
    </Task>
  </Tasks>
</Project>";
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        var result = await svc.ImportFromMsProjectAsync(projectId, Xml(xml), userId);
        Assert.Equal(1, result.ActivitiesImported);
        Assert.Equal(0, result.DependenciesImported);
        Assert.Single(result.Warnings);
        Assert.Contains("999", result.Warnings[0]);
    }

    [Fact]
    public async Task ImportFromMsProjectAsync_handles_empty_Tasks_block()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Name>Empty</Name>
  <Tasks/>
</Project>";
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        var result = await svc.ImportFromMsProjectAsync(projectId, Xml(xml), userId);
        Assert.Equal(0, result.ActivitiesImported);
        Assert.Equal(0, result.DependenciesImported);
    }

    [Fact]
    public async Task ImportFromMsProjectAsync_recompute_runs_against_imported_data()
    {
        // End-to-end: import the chain, then RecomputeAsync, and
        // verify the CPM solver computed Early/Late dates against
        // imported Activities + Dependencies.
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Name>End to end</Name>
  <Tasks>
    <Task><UID>1</UID><Name>A</Name><Duration>PT24H0M0S</Duration></Task>
    <Task>
      <UID>2</UID><Name>B</Name><Duration>PT16H0M0S</Duration>
      <PredecessorLink>
        <PredecessorUID>1</PredecessorUID><Type>1</Type><LinkLag>0</LinkLag>
      </PredecessorLink>
    </Task>
  </Tasks>
</Project>";
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.ImportFromMsProjectAsync(projectId, Xml(xml), userId);
            var rec = await svc.RecomputeAsync(projectId, dataDate: null, userId);
            Assert.Equal(2, rec.ActivitiesCount);
            Assert.Equal(2, rec.CriticalActivitiesCount);
            Assert.Equal(new DateTime(2026, 6, 6), rec.ProjectFinish);   // 3 + 2 = 5 days from 2026-06-01
        }
    }

    [Fact]
    public async Task ImportFromMsProjectAsync_cross_tenant_404s()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}""><Tasks/></Project>";
        var (options, tenant, _, userId, projectId) = BuildFixture();

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(),
            UserId         = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        using var db = new CimsDbContext(options, attacker);
        var svc = new ScheduleService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.ImportFromMsProjectAsync(projectId, Xml(xml), attacker.UserId!.Value));
    }
}
