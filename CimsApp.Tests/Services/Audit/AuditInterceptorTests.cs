using System.Text.Json;
using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Xunit;

namespace CimsApp.Tests.Services.Audit;

/// <summary>
/// Unit tests for AuditInterceptor helpers and its behaviour against a
/// DbContext change-tracker without hitting a database. Runtime-behavioural
/// tests (full SaveChanges integration producing actual AuditLog rows)
/// land with T-S0-06b once ADR-0009 approves an in-memory EF provider.
/// </summary>
public class AuditInterceptorTests
{
    private static CimsDbContext BuildContext() =>
        new(new DbContextOptionsBuilder<CimsDbContext>()
            .UseSqlServer("Server=model-only;Database=model-only;")
            .Options);

    [Theory]
    [InlineData(EntityState.Added,    "Insert")]
    [InlineData(EntityState.Modified, "Update")]
    [InlineData(EntityState.Deleted,  "Delete")]
    public void ActionFor_maps_mutating_states(EntityState state, string expected) =>
        Assert.Equal(expected, AuditInterceptor.ActionFor(state));

    [Theory]
    [InlineData(EntityState.Unchanged)]
    [InlineData(EntityState.Detached)]
    public void ActionFor_returns_null_for_non_mutating_states(EntityState state) =>
        Assert.Null(AuditInterceptor.ActionFor(state));

    [Fact]
    public void Skip_list_contains_the_noise_entity_types()
    {
        Assert.Contains(typeof(AuditLog),     AuditInterceptor.SkippedEntityTypes);
        Assert.Contains(typeof(RefreshToken), AuditInterceptor.SkippedEntityTypes);
        Assert.Contains(typeof(Notification), AuditInterceptor.SkippedEntityTypes);
    }

    [Fact]
    public void Skip_list_does_not_contain_tenant_scoped_business_entities()
    {
        Assert.DoesNotContain(typeof(Project),  AuditInterceptor.SkippedEntityTypes);
        Assert.DoesNotContain(typeof(Document), AuditInterceptor.SkippedEntityTypes);
        Assert.DoesNotContain(typeof(Rfi),      AuditInterceptor.SkippedEntityTypes);
        Assert.DoesNotContain(typeof(User),     AuditInterceptor.SkippedEntityTypes);
    }

    [Fact]
    public void ShouldAudit_returns_false_for_skipped_types()
    {
        using var ctx = BuildContext();
        var audit = new AuditLog { UserId = Guid.NewGuid(), Action = "x", Entity = "x", EntityId = "x" };
        var entry = ctx.Entry(audit);
        entry.State = EntityState.Added;

        Assert.False(AuditInterceptor.ShouldAudit(entry));
    }

    [Fact]
    public void ShouldAudit_returns_true_for_added_business_entity()
    {
        using var ctx = BuildContext();
        var project = new Project { Name = "Test", Code = "TST", AppointingPartyId = Guid.NewGuid() };
        var entry = ctx.Entry(project);
        entry.State = EntityState.Added;

        Assert.True(AuditInterceptor.ShouldAudit(entry));
    }

    [Fact]
    public void BuildAuditLog_populates_insert_metadata_correctly()
    {
        using var ctx = BuildContext();
        var orgId = Guid.NewGuid();
        var project = new Project { Name = "Alpha", Code = "ALP", AppointingPartyId = orgId };
        var entry = ctx.Entry(project);
        entry.State = EntityState.Added;

        var userId = Guid.NewGuid();
        var log = AuditInterceptor.BuildAuditLog(entry, userId, ip: "10.0.0.1", ua: "test-agent");

        Assert.NotNull(log);
        Assert.Equal(userId,         log!.UserId);
        Assert.Equal("Insert",       log.Action);
        Assert.Equal("Project",      log.Entity);
        Assert.Equal(project.Id.ToString(), log.EntityId);
        Assert.Equal("10.0.0.1",     log.IpAddress);
        Assert.Equal("test-agent",   log.UserAgent);
        Assert.Null(log.BeforeValue);
        Assert.NotNull(log.AfterValue);
        Assert.Contains("Alpha", log.AfterValue);
        Assert.Equal(project.Id, log.ProjectId);   // Project-type self-reference
    }

    [Fact]
    public void BuildAuditLog_captures_document_and_project_ids_from_document()
    {
        using var ctx = BuildContext();
        var projectId = Guid.NewGuid();
        var doc = new Document
        {
            ProjectId = projectId,
            ProjectCode = "P1", Originator = "X", DocType = "DR",
            Number = "0001", DocumentNumber = "P1-X-DR-0001",
            Title = "Doc", CreatorId = Guid.NewGuid(),
        };
        var entry = ctx.Entry(doc);
        entry.State = EntityState.Added;

        var log = AuditInterceptor.BuildAuditLog(entry, Guid.NewGuid(), ip: null, ua: null);

        Assert.NotNull(log);
        Assert.Equal(projectId, log!.ProjectId);
        Assert.Equal(doc.Id,    log.DocumentId);
    }

    [Fact]
    public void BuildAuditLog_serialises_scalar_properties_as_json()
    {
        using var ctx = BuildContext();
        var project = new Project { Name = "Alpha", Code = "ALP", AppointingPartyId = Guid.NewGuid() };
        var entry = ctx.Entry(project);
        entry.State = EntityState.Added;

        var log = AuditInterceptor.BuildAuditLog(entry, Guid.NewGuid(), null, null);

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(log!.AfterValue!);
        Assert.NotNull(parsed);
        Assert.True(parsed!.ContainsKey("Name"));
        Assert.True(parsed.ContainsKey("Code"));
        Assert.True(parsed.ContainsKey("AppointingPartyId"));
        Assert.Equal("Alpha", parsed["Name"].GetString());
    }

    [Fact]
    public void ExtractRelatedIds_finds_project_id_via_convention_property()
    {
        using var ctx = BuildContext();
        var projectId = Guid.NewGuid();
        var rfi = new Rfi
        {
            ProjectId = projectId,
            RfiNumber = "RFI-001", Subject = "S", Description = "d",
            RaisedById = Guid.NewGuid(),
        };
        var entry = ctx.Entry(rfi);
        entry.State = EntityState.Added;

        var (pid, did) = AuditInterceptor.ExtractRelatedIds(entry);

        Assert.Equal(projectId, pid);
        Assert.Null(did);
    }
}
