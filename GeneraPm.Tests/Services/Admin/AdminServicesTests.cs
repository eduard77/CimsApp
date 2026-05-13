using GeneraPm.Core;
using GeneraPm.Data;
using GeneraPm.Models;
using GeneraPm.Services.Admin;
using GeneraPm.Services.Audit;
using GeneraPm.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace GeneraPm.Tests.Services.Admin;

/// <summary>
/// T-S16-05 admin console behavioural tests. Covers per-service paging
/// happy path, tenant isolation (OrgAdmin) vs cross-tenant visibility
/// (SuperAdmin), privilege-escalation guards on the role-set
/// endpoint, and the SuperAdmin-only org deactivation gate.
/// </summary>
public class AdminServicesTests
{
    // ── UserAdminService ─────────────────────────────────────────

    [Fact]
    public async Task UserList_OrgAdmin_sees_only_own_tenant()
    {
        var f = BuildTwoTenantFixture();
        var svc = new UserAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        var page = await svc.ListAsync();
        // Two seeded in Org A (admin + regular), zero in Org B from
        // Org A's view.
        Assert.Equal(2, page.Total);
        Assert.All(page.Items, u => Assert.Equal(f.OrgAId, u.OrganisationId));
    }

    [Fact]
    public async Task UserList_SuperAdmin_sees_every_tenant()
    {
        var f = BuildTwoTenantFixture();
        var svc = new UserAdminService(f.SuperDb, f.SuperTenant, new AuditService(f.SuperDb));
        var page = await svc.ListAsync();
        Assert.Equal(4, page.Total); // 2 in A + 2 in B
    }

    [Fact]
    public async Task UserList_search_filters_by_email_or_name()
    {
        var f = BuildTwoTenantFixture();
        var svc = new UserAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        var byEmail = await svc.ListAsync(search: "admin-a");
        Assert.Equal(1, byEmail.Total);
        var byName = await svc.ListAsync(search: "Regular");
        Assert.Equal(1, byName.Total);
    }

    [Fact]
    public async Task SetGlobalRole_rejects_self_targeting()
    {
        var f = BuildTwoTenantFixture();
        var svc = new UserAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.SetGlobalRoleAsync(f.OrgAAdminId, UserRole.OrgAdmin));
    }

    [Fact]
    public async Task SetGlobalRole_OrgAdmin_cannot_grant_SuperAdmin()
    {
        var f = BuildTwoTenantFixture();
        var svc = new UserAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.SetGlobalRoleAsync(f.OrgARegularId, UserRole.SuperAdmin));
    }

    [Fact]
    public async Task SetGlobalRole_SuperAdmin_can_grant_SuperAdmin()
    {
        var f = BuildTwoTenantFixture();
        var svc = new UserAdminService(f.SuperDb, f.SuperTenant, new AuditService(f.SuperDb));
        await svc.SetGlobalRoleAsync(f.OrgARegularId, UserRole.SuperAdmin);
        var promoted = await f.SuperDb.Users.IgnoreQueryFilters()
            .FirstAsync(u => u.Id == f.OrgARegularId);
        Assert.Equal(UserRole.SuperAdmin, promoted.GlobalRole);
        Assert.NotNull(promoted.TokenInvalidationCutoff);
    }

    [Fact]
    public async Task SetGlobalRole_OrgAdmin_cannot_target_other_tenant_user()
    {
        var f = BuildTwoTenantFixture();
        var svc = new UserAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.SetGlobalRoleAsync(f.OrgBRegularId, UserRole.OrgAdmin));
    }

    [Fact]
    public async Task SetGlobalRole_writes_structured_audit_event()
    {
        var f = BuildTwoTenantFixture();
        var svc = new UserAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        await svc.SetGlobalRoleAsync(f.OrgARegularId, UserRole.OrgAdmin);
        var audit = await f.OrgADb.AuditLogs
            .Where(a => a.Action == "auth.user_global_role_set")
            .ToListAsync();
        Assert.Single(audit);
        Assert.Equal(f.OrgARegularId.ToString(), audit[0].EntityId);
    }

    // ── OrganisationAdminService ─────────────────────────────────

    [Fact]
    public async Task OrgList_OrgAdmin_sees_only_own_org()
    {
        var f = BuildTwoTenantFixture();
        var svc = new OrganisationAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        var page = await svc.ListAsync();
        Assert.Equal(1, page.Total);
        Assert.Equal(f.OrgAId, page.Items[0].Id);
    }

    [Fact]
    public async Task OrgList_SuperAdmin_sees_every_org()
    {
        var f = BuildTwoTenantFixture();
        var svc = new OrganisationAdminService(f.SuperDb, f.SuperTenant, new AuditService(f.SuperDb));
        var page = await svc.ListAsync();
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task OrgUpdate_rejects_empty_name()
    {
        var f = BuildTwoTenantFixture();
        var svc = new OrganisationAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.UpdateAsync(f.OrgAId, new UpdateOrganisationRequest("   ", "UK")));
    }

    [Fact]
    public async Task OrgUpdate_OrgAdmin_cannot_update_other_tenant()
    {
        var f = BuildTwoTenantFixture();
        var svc = new OrganisationAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        // OrgAdmin in A targets Org B — query filter on Organisations
        // hides Org B from A's view, so the lookup 404s.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.UpdateAsync(f.OrgBId, new UpdateOrganisationRequest("Renamed", "UK")));
    }

    [Fact]
    public async Task OrgUpdate_SuperAdmin_can_update_any()
    {
        var f = BuildTwoTenantFixture();
        var svc = new OrganisationAdminService(f.SuperDb, f.SuperTenant, new AuditService(f.SuperDb));
        await svc.UpdateAsync(f.OrgBId, new UpdateOrganisationRequest("Org B Renamed", "UK"));
        var org = await f.SuperDb.Organisations.IgnoreQueryFilters()
            .FirstAsync(o => o.Id == f.OrgBId);
        Assert.Equal("Org B Renamed", org.Name);
        Assert.Equal("UK", org.Country);
    }

    [Fact]
    public async Task OrgDeactivate_OrgAdmin_rejected()
    {
        var f = BuildTwoTenantFixture();
        var svc = new OrganisationAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.DeactivateAsync(f.OrgAId));
    }

    [Fact]
    public async Task OrgDeactivate_SuperAdmin_flips_IsActive_and_audits()
    {
        var f = BuildTwoTenantFixture();
        var svc = new OrganisationAdminService(f.SuperDb, f.SuperTenant, new AuditService(f.SuperDb));
        await svc.DeactivateAsync(f.OrgBId);
        var org = await f.SuperDb.Organisations.IgnoreQueryFilters()
            .FirstAsync(o => o.Id == f.OrgBId);
        Assert.False(org.IsActive);
        var audit = await f.SuperDb.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "organisation.deactivated").ToListAsync();
        Assert.Single(audit);
    }

    // ── InvitationAdminService ───────────────────────────────────

    [Fact]
    public async Task InvitationList_OrgAdmin_sees_only_own_tenant_active()
    {
        var f = BuildTwoTenantFixture();
        // Seed two active invitations in A, one in B, and one expired in A.
        SeedInvite(f.OrgADb, f.OrgAId, expiresIn: TimeSpan.FromDays(7));
        SeedInvite(f.OrgADb, f.OrgAId, expiresIn: TimeSpan.FromDays(3));
        SeedInvite(f.OrgADb, f.OrgAId, expiresIn: TimeSpan.FromDays(-1)); // expired
        SeedInvite(f.OrgADb, f.OrgBId, expiresIn: TimeSpan.FromDays(7));
        await f.OrgADb.SaveChangesAsync();

        // Re-create OrgADb so EF re-reads (avoid the seeded entities
        // being tracked across test phases).
        var svc = new InvitationAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        var page = await svc.ListActiveAsync();
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task InvitationList_SuperAdmin_sees_every_tenant_active()
    {
        var f = BuildTwoTenantFixture();
        SeedInvite(f.SuperDb, f.OrgAId, expiresIn: TimeSpan.FromDays(7));
        SeedInvite(f.SuperDb, f.OrgBId, expiresIn: TimeSpan.FromDays(7));
        await f.SuperDb.SaveChangesAsync();

        var svc = new InvitationAdminService(f.SuperDb, f.SuperTenant, new AuditService(f.SuperDb));
        var page = await svc.ListActiveAsync();
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task InvitationRevoke_already_consumed_throws()
    {
        var f = BuildTwoTenantFixture();
        var inv = SeedInvite(f.OrgADb, f.OrgAId, expiresIn: TimeSpan.FromDays(7));
        inv.ConsumedAt = DateTime.UtcNow;
        inv.ConsumedByUserId = f.OrgAAdminId;
        await f.OrgADb.SaveChangesAsync();

        var svc = new InvitationAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        await Assert.ThrowsAsync<ValidationException>(() => svc.RevokeAsync(inv.Id));
    }

    [Fact]
    public async Task InvitationRevoke_expires_now_and_audits()
    {
        var f = BuildTwoTenantFixture();
        var inv = SeedInvite(f.OrgADb, f.OrgAId, expiresIn: TimeSpan.FromDays(7));
        await f.OrgADb.SaveChangesAsync();

        var svc = new InvitationAdminService(f.OrgADb, f.OrgATenant, new AuditService(f.OrgADb));
        await svc.RevokeAsync(inv.Id);

        var refetched = await f.OrgADb.Invitations.FirstAsync(i => i.Id == inv.Id);
        Assert.True(refetched.ExpiresAt <= DateTime.UtcNow.AddSeconds(1));
        var audit = await f.OrgADb.AuditLogs
            .Where(a => a.Action == "invitation.revoked").ToListAsync();
        Assert.Single(audit);
    }

    // ── AuditAdminService ────────────────────────────────────────

    [Fact]
    public async Task AuditList_OrgAdmin_sees_only_own_tenant_rows()
    {
        var f = BuildTwoTenantFixture();
        SeedAuditRow(f.OrgADb, f.OrgAAdminId, "x.created", "Foo");
        SeedAuditRow(f.OrgADb, f.OrgBAdminId, "x.created", "Foo");
        SeedAuditRow(f.OrgADb, null, "anon.created", "Boot"); // anonymous-actor row, hidden from tenant queries
        await f.OrgADb.SaveChangesAsync();

        var svc = new AuditAdminService(f.OrgADb, f.OrgATenant);
        var page = await svc.ListAsync();
        // Only the Org A user's row visible. Org B row hidden by
        // the tenant filter; anonymous-actor row hidden by the
        // User != null clause (PR #41 semantic).
        Assert.Equal(1, page.Total);
        Assert.Equal(f.OrgAAdminId, page.Items[0].UserId);
    }

    [Fact]
    public async Task AuditList_SuperAdmin_sees_every_row_including_anonymous()
    {
        var f = BuildTwoTenantFixture();
        SeedAuditRow(f.SuperDb, f.OrgAAdminId, "x.created", "Foo");
        SeedAuditRow(f.SuperDb, f.OrgBAdminId, "x.created", "Foo");
        SeedAuditRow(f.SuperDb, null, "anon.created", "Boot");
        await f.SuperDb.SaveChangesAsync();

        var svc = new AuditAdminService(f.SuperDb, f.SuperTenant);
        var page = await svc.ListAsync();
        Assert.Equal(3, page.Total);
    }

    [Fact]
    public async Task AuditList_filters_by_date_action_entity_and_user()
    {
        var f = BuildTwoTenantFixture();
        SeedAuditRow(f.OrgADb, f.OrgAAdminId, "doc.created", "Document",
            createdAt: DateTime.UtcNow.AddDays(-10));
        SeedAuditRow(f.OrgADb, f.OrgAAdminId, "doc.updated", "Document",
            createdAt: DateTime.UtcNow.AddDays(-1));
        SeedAuditRow(f.OrgADb, f.OrgARegularId, "rfi.created", "Rfi",
            createdAt: DateTime.UtcNow.AddDays(-1));
        await f.OrgADb.SaveChangesAsync();

        var svc = new AuditAdminService(f.OrgADb, f.OrgATenant);

        var byDate = await svc.ListAsync(from: DateTime.UtcNow.AddDays(-3));
        Assert.Equal(2, byDate.Total);

        var byActionContains = await svc.ListAsync(actionContains: "updated");
        Assert.Equal(1, byActionContains.Total);

        var byEntity = await svc.ListAsync(entity: "Rfi");
        Assert.Equal(1, byEntity.Total);

        var byUser = await svc.ListAsync(userId: f.OrgARegularId);
        Assert.Equal(1, byUser.Total);
    }

    // ── Fixture ──────────────────────────────────────────────────

    private sealed record TwoTenantFixture(
        Guid OrgAId, Guid OrgBId,
        Guid OrgAAdminId, Guid OrgARegularId,
        Guid OrgBAdminId, Guid OrgBRegularId,
        StubTenantContext OrgATenant, GeneraPmDbContext OrgADb,
        StubTenantContext SuperTenant, GeneraPmDbContext SuperDb);

    private static TwoTenantFixture BuildTwoTenantFixture()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var aAdmin = Guid.NewGuid();
        var aReg = Guid.NewGuid();
        var bAdmin = Guid.NewGuid();
        var bReg = Guid.NewGuid();

        // Seed under SuperAdmin so we can write users for both orgs.
        var seedTenant = new StubTenantContext { GlobalRole = UserRole.SuperAdmin };
        var options = new DbContextOptionsBuilder<GeneraPmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditInterceptor(seedTenant, httpAccessor: null))
            .Options;

        using (var seed = new GeneraPmDbContext(options, seedTenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgA, Name = "Org A", Code = "A" });
            seed.Organisations.Add(new Organisation { Id = orgB, Name = "Org B", Code = "B" });
            seed.Users.Add(new User { Id = aAdmin, Email = "admin-a@e.com", PasswordHash = "x", FirstName = "Admin", LastName = "Alpha", OrganisationId = orgA, GlobalRole = UserRole.OrgAdmin });
            seed.Users.Add(new User { Id = aReg,   Email = "reg-a@e.com",   PasswordHash = "x", FirstName = "Regular", LastName = "Alpha", OrganisationId = orgA });
            seed.Users.Add(new User { Id = bAdmin, Email = "admin-b@e.com", PasswordHash = "x", FirstName = "Admin", LastName = "Beta", OrganisationId = orgB, GlobalRole = UserRole.OrgAdmin });
            seed.Users.Add(new User { Id = bReg,   Email = "reg-b@e.com",   PasswordHash = "x", FirstName = "Regular", LastName = "Beta", OrganisationId = orgB });
            seed.SaveChanges();
        }

        var orgATenant = new StubTenantContext { OrganisationId = orgA, UserId = aAdmin, GlobalRole = UserRole.OrgAdmin };
        var orgADb     = new GeneraPmDbContext(options, orgATenant);

        var superTenant = new StubTenantContext { OrganisationId = orgA, UserId = aAdmin, GlobalRole = UserRole.SuperAdmin };
        var superDb     = new GeneraPmDbContext(options, superTenant);

        return new TwoTenantFixture(
            orgA, orgB, aAdmin, aReg, bAdmin, bReg,
            orgATenant, orgADb, superTenant, superDb);
    }

    private static Invitation SeedInvite(GeneraPmDbContext db, Guid orgId, TimeSpan expiresIn)
    {
        var inv = new Invitation
        {
            OrganisationId = orgId,
            TokenHash      = Guid.NewGuid().ToString("N"),
            ExpiresAt      = DateTime.UtcNow.Add(expiresIn),
            IsBootstrap    = false,
        };
        db.Invitations.Add(inv);
        return inv;
    }

    private static void SeedAuditRow(GeneraPmDbContext db, Guid? userId, string action, string entity, DateTime? createdAt = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId    = userId,
            Action    = action,
            Entity    = entity,
            EntityId  = Guid.NewGuid().ToString(),
            CreatedAt = createdAt ?? DateTime.UtcNow,
        });
    }
}
