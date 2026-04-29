using CimsApp.Core;
using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Projects;

/// <summary>
/// SR-S0-05: `ProjectsService.AddMemberAsync` must verify that the
/// new member's `User.OrganisationId` matches the project's
/// `AppointingPartyId`. Without the check, a PM in Org A could add
/// a User from Org B as a project member — the row would persist
/// but the user's tenant filter (Project filtered by
/// AppointingPartyId == tenant.OrganisationId) would hide the
/// project from them, leaving an orphan ProjectMember row.
/// </summary>
public class AddMemberOrgMatchTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgA, Guid orgB, Guid userInA, Guid userInB, Guid projectInA)
        BuildFixture()
    {
        var orgA       = Guid.NewGuid();
        var orgB       = Guid.NewGuid();
        var userInA    = Guid.NewGuid();
        var userInB    = Guid.NewGuid();
        var projectInA = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgA, UserId = userInA,
            GlobalRole     = UserRole.OrgAdmin,
        };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "A", Code = "TA" },
                new Organisation { Id = orgB, Name = "B", Code = "TB" });
            seed.Users.AddRange(
                new User
                {
                    Id = userInA, Email = $"a-{Guid.NewGuid():N}@e.com",
                    PasswordHash = "x", FirstName = "A", LastName = "U",
                    OrganisationId = orgA,
                },
                new User
                {
                    Id = userInB, Email = $"b-{Guid.NewGuid():N}@e.com",
                    PasswordHash = "x", FirstName = "B", LastName = "U",
                    OrganisationId = orgB,
                });
            seed.Projects.Add(new Project
            {
                Id = projectInA, Name = "Project A", Code = "PA",
                AppointingPartyId = orgA, Currency = "GBP",
            });
            seed.SaveChanges();
        }
        return (options, tenant, orgA, orgB, userInA, userInB, projectInA);
    }

    private static ProjectsService NewService(DbContextOptions<CimsDbContext> options,
        StubTenantContext tenant, out CimsDbContext db)
    {
        db = new CimsDbContext(options, tenant);
        return new ProjectsService(db, new AuditService(db), tenant);
    }

    [Fact]
    public async Task AddMember_user_in_same_org_succeeds()
    {
        var (options, tenant, _, _, userInA, _, projectInA) = BuildFixture();
        var svc = NewService(options, tenant, out var db);
        await using (db)
        {
            await svc.AddMemberAsync(projectInA, userInA, UserRole.TaskTeamMember, actorId: userInA);
        }

        using var verify = new CimsDbContext(options, tenant);
        var member = Assert.Single(verify.ProjectMembers.IgnoreQueryFilters()
            .Where(m => m.ProjectId == projectInA && m.UserId == userInA));
        Assert.True(member.IsActive);
        Assert.Equal(UserRole.TaskTeamMember, member.Role);
    }

    [Fact]
    public async Task AddMember_user_in_different_org_is_rejected_with_no_row_written()
    {
        // Cross-org attempt: PM in Org A tries to add a user from
        // Org B. The check throws ValidationException and no
        // ProjectMember row is written.
        var (options, tenant, _, _, _, userInB, projectInA) = BuildFixture();
        var svc = NewService(options, tenant, out var db);
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.AddMemberAsync(projectInA, userInB, UserRole.TaskTeamMember, actorId: Guid.NewGuid()));
        Assert.Contains("must belong to the project's appointing organisation",
            ex.Errors[0]);
        await db.DisposeAsync();

        using var verify = new CimsDbContext(options, tenant);
        Assert.False(verify.ProjectMembers.IgnoreQueryFilters()
            .Any(m => m.ProjectId == projectInA && m.UserId == userInB));
    }

    [Fact]
    public async Task AddMember_unknown_user_throws_NotFound()
    {
        var (options, tenant, _, _, _, _, projectInA) = BuildFixture();
        var svc = NewService(options, tenant, out var db);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.AddMemberAsync(projectInA, Guid.NewGuid(), UserRole.TaskTeamMember, Guid.NewGuid()));
        await db.DisposeAsync();
    }

    [Fact]
    public async Task AddMember_unknown_project_throws_NotFound()
    {
        var (options, tenant, _, _, userInA, _, _) = BuildFixture();
        var svc = NewService(options, tenant, out var db);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.AddMemberAsync(Guid.NewGuid(), userInA, UserRole.TaskTeamMember, Guid.NewGuid()));
        await db.DisposeAsync();
    }

    [Fact]
    public async Task AddMember_emits_project_member_added_audit_with_role_and_reactivated_flag()
    {
        var (options, tenant, _, _, userInA, _, projectInA) = BuildFixture();
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = projectInA, UserId = userInA,
                Role = UserRole.TaskTeamMember, IsActive = false,
            });
            seed.SaveChanges();
        }

        var actor = Guid.NewGuid();
        var svc = NewService(options, tenant, out var db);
        await using (db)
        {
            await svc.AddMemberAsync(projectInA, userInA, UserRole.ProjectManager,
                actorId: actor);
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "project.member_added"));
        Assert.Equal("ProjectMember", audit.Entity);
        Assert.Equal($"{projectInA}:{userInA}", audit.EntityId);
        Assert.Equal(projectInA, audit.ProjectId);
        Assert.Equal(actor, audit.UserId);
        Assert.Contains($"\"targetUserId\":\"{userInA}\"", audit.Detail);
        Assert.Contains("\"grantedRole\":\"ProjectManager\"", audit.Detail);
        // Re-add over an existing inactive membership → reactivated:true.
        Assert.Contains("\"reactivated\":true", audit.Detail);
    }

    [Fact]
    public async Task AddMember_first_time_emits_audit_with_reactivated_false()
    {
        var (options, tenant, _, _, userInA, _, projectInA) = BuildFixture();
        var actor = Guid.NewGuid();
        var svc = NewService(options, tenant, out var db);
        await using (db)
        {
            await svc.AddMemberAsync(projectInA, userInA, UserRole.TaskTeamMember,
                actorId: actor);
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "project.member_added"));
        Assert.Contains("\"reactivated\":false", audit.Detail);
    }

    [Fact]
    public async Task AddMember_cross_org_attempt_does_not_emit_audit()
    {
        // Cross-org rejection should leave NO audit row — the
        // ValidationException fires before SaveChanges, so the
        // explicit audit.WriteAsync is never reached.
        var (options, tenant, _, _, _, userInB, projectInA) = BuildFixture();
        var svc = NewService(options, tenant, out var db);
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.AddMemberAsync(projectInA, userInB, UserRole.TaskTeamMember,
                actorId: Guid.NewGuid()));
        await db.DisposeAsync();

        using var verify = new CimsDbContext(options, tenant);
        Assert.Empty(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "project.member_added"));
    }

    [Fact]
    public async Task AddMember_idempotent_re_adds_reactivate_existing_membership()
    {
        // Existing member, re-added with a new role: the row updates
        // (existing.Role = role, existing.IsActive = true) rather
        // than creating a duplicate.
        var (options, tenant, _, _, userInA, _, projectInA) = BuildFixture();

        // Pre-seed an inactive ProjectMember row at TaskTeamMember.
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = projectInA, UserId = userInA,
                Role = UserRole.TaskTeamMember, IsActive = false,
            });
            seed.SaveChanges();
        }

        var svc = NewService(options, tenant, out var db);
        await using (db)
        {
            await svc.AddMemberAsync(projectInA, userInA, UserRole.ProjectManager,
                actorId: userInA);
        }

        using var verify = new CimsDbContext(options, tenant);
        var rows = verify.ProjectMembers.IgnoreQueryFilters()
            .Where(m => m.ProjectId == projectInA && m.UserId == userInA)
            .ToList();
        var member = Assert.Single(rows);
        Assert.True(member.IsActive);
        Assert.Equal(UserRole.ProjectManager, member.Role);
    }
}
