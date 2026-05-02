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

namespace CimsApp.Tests.Services.Documents;

/// <summary>
/// Audit-twin coverage for `DocumentsService.TransitionAsync`. The
/// audit-twin atomicity refactor (PR #33) changed every audit.WriteAsync
/// site, and the transaction-wrap fix (PR #34) reordered the saves
/// inside TransitionAsync — without an explicit assertion on the
/// `document.state_transition` action and its before/after detail, a
/// regression at this site would only be visible to a forensic
/// reviewer searching the audit log.
/// </summary>
public class DocumentTransitionAuditTests
{
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
        // TransitionAsync wraps its writes in BeginTransactionAsync
        // (PR #34) which the in-memory provider treats as a no-op
        // and reports as a TransactionIgnoredWarning that escalates
        // to an exception by default. Suppress so the test exercises
        // the production code shape without false positives.
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptor)
            .Options;

        using var seed = new CimsDbContext(options, tenant);
        seed.Organisations.Add(new Organisation { Id = orgId, Name = "Org", Code = "OG" });
        seed.Users.Add(new User
        {
            Id = userId, Email = $"u-{Guid.NewGuid():N}@e.com",
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

    private static CreateDocumentRequest NewDocRequest() => new(
        ProjectCode: "PR1", Originator: "ABC",
        Volume: null, Level: null, DocType: "RP",
        Role: null, Number: 1,
        Title: "Spec", Description: null,
        Type: DocumentType.Report, ContainerId: null, Tags: null);

    [Fact]
    public async Task Transition_emits_document_state_transition_audit_with_before_after()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid docId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new DocumentsService(db, new AuditService(db), new CimsApp.Services.Iso19650.Iso19650FilenameValidator());
            var doc = await svc.CreateAsync(projectId, NewDocRequest(), userId, null, null);
            docId = doc.Id;
            // WIP → Shared is the first valid transition. ReviewedBy
            // permitted because the test user is OrgAdmin which
            // satisfies any project-role floor at the service layer.
            await svc.TransitionAsync(docId, projectId, CdeState.Shared,
                suitability: null, userId, UserRole.InformationManager,
                ip: "203.0.113.4", ua: "ua-trans");
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "document.state_transition"));
        Assert.Equal("Document", audit.Entity);
        Assert.Equal(docId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Equal(docId, audit.DocumentId);
        Assert.Equal(userId, audit.UserId);
        Assert.Equal("203.0.113.4", audit.IpAddress);
        Assert.Equal("ua-trans", audit.UserAgent);
        // Detail carries the before/after pair so a reviewer can
        // reconstruct the transition without joining against the
        // entity's per-row audit row.
        Assert.Contains("\"from\":\"WorkInProgress\"", audit.Detail);
        Assert.Contains("\"to\":\"Shared\"", audit.Detail);
    }
}
