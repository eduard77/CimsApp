using CimsApp.Core;
using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Auth;

/// <summary>
/// Audit-twin coverage for `InvitationService` — the service
/// previously emitted only the `AuditInterceptor` per-row audit
/// on Invitation Insert/Update; now also emits structured
/// `invitation.created` and `invitation.consumed` events.
/// </summary>
public class InvitationAuditTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid actorId) BuildFixture()
    {
        var orgId   = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = actorId,
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
            Id = actorId, Email = $"u-{Guid.NewGuid():N}@e.com",
            PasswordHash = "x", FirstName = "T", LastName = "U",
            OrganisationId = orgId,
        });
        seed.SaveChanges();
        return (options, tenant, orgId, actorId);
    }

    [Fact]
    public async Task CreateAsync_emits_invitation_created_audit_with_org_and_bootstrap_flag()
    {
        var (options, tenant, orgId, actorId) = BuildFixture();

        Guid invitationId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new InvitationService(db, new AuditService(db));
            var r = await svc.CreateAsync(
                organisationId: orgId,
                createdById: actorId,
                emailBind: "alice@example.com",
                expiresInDays: 7,
                isBootstrap: false);
            invitationId = r.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "invitation.created"));
        Assert.Equal("Invitation", audit.Entity);
        Assert.Equal(invitationId.ToString(), audit.EntityId);
        Assert.Equal(actorId, audit.UserId);
        Assert.Contains($"\"organisationId\":\"{orgId}\"", audit.Detail);
        Assert.Contains("\"isBootstrap\":false", audit.Detail);
        Assert.Contains("\"hasEmailBind\":true", audit.Detail);
    }

    [Fact]
    public async Task CreateAsync_bootstrap_invitation_attributes_to_Guid_Empty()
    {
        // Bootstrap invitations are minted by the anonymous
        // org-creation flow — there is no caller userId at that
        // moment, so the audit row's UserId is Guid.Empty by
        // design. The audit row still exists so a search for
        // "every bootstrap minted" lands on it.
        var (options, tenant, orgId, _) = BuildFixture();

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new InvitationService(db, new AuditService(db));
            await svc.CreateAsync(
                organisationId: orgId,
                createdById: null,         // anonymous flow
                emailBind: null,
                expiresInDays: 1,
                isBootstrap: true);
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "invitation.created"));
        Assert.Equal(Guid.Empty, audit.UserId);
        Assert.Contains("\"isBootstrap\":true", audit.Detail);
        Assert.Contains("\"hasEmailBind\":false", audit.Detail);
    }

    // `MarkConsumedAsync_emits_invitation_consumed_audit` is omitted
    // here: `MarkConsumedAsync` uses `ExecuteUpdateAsync` for the
    // atomic check-and-set against double-consume races, and the
    // EF in-memory provider doesn't translate that operation.
    // Adding the equivalent test requires either SQL Server fixtures
    // or refactoring the service method. The `invitation.consumed`
    // emission itself is correct by inspection (fires only when
    // `rows == 1`, attributed to the consumer userId, single
    // structured event with the consumerUserId in detail).
}
