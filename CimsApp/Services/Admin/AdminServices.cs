using CimsApp.Core;
using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CimsApp.Services.Admin;

// ── DTOs (sprint-local) ───────────────────────────────────────────────────────
// Inlined here rather than in DTOs/Dtos.cs because they are
// admin-surface-only and live and die with the four admin services
// below. If a non-admin endpoint ever needs the same shape, promote
// to the shared DTO file.

public sealed record AdminUserListItem(
    Guid Id, string Email, string FirstName, string LastName,
    string? JobTitle, UserRole? GlobalRole, bool IsActive,
    Guid OrganisationId, string OrganisationName, string OrganisationCode,
    DateTime CreatedAt, DateTime? LastLoginAt);

public sealed record AdminOrgListItem(
    Guid Id, string Name, string Code, string? Country, bool IsActive,
    int UserCount, int ProjectCount, DateTime CreatedAt);

public sealed record AdminInvitationListItem(
    Guid Id, Guid OrganisationId, string OrganisationName,
    string? Email, bool IsBootstrap, DateTime ExpiresAt,
    DateTime CreatedAt, Guid? CreatedById, string? CreatedByName);

public sealed record AdminAuditListItem(
    Guid Id, DateTime CreatedAt, string Action, string Entity,
    string EntityId, Guid? UserId, string? UserName,
    Guid? ProjectId, string? IpAddress);

public sealed record AdminPagedResult<T>(
    IReadOnlyList<T> Items, int Total, int Skip, int Take);

public sealed record SetGlobalRoleRequest(UserRole? GlobalRole);
public sealed record UpdateOrganisationRequest(string Name, string? Country);

// ── User admin ────────────────────────────────────────────────────────────────
// List + role-management for users in the caller's tenant. Existing
// AuthService owns RevokeUserTokensAsync / DeactivateUserAsync —
// they are reused unchanged from the existing /users/{id}/... endpoints.

public class UserAdminService(CimsDbContext db, ITenantContext tenant, AuditService audit)
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    /// <summary>
    /// Tenant-scoped paged user list. SuperAdmin sees every user
    /// across every tenant via IgnoreQueryFilters (ADR-0007);
    /// OrgAdmin sees their own tenant only via the standard global
    /// filter on <see cref="User.OrganisationId"/>.
    /// </summary>
    public async Task<AdminPagedResult<AdminUserListItem>> ListAsync(
        string? search = null, int skip = 0, int? take = null,
        CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        var t = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        var q = tenant.IsSuperAdmin
            ? db.Users.IgnoreQueryFilters().AsQueryable()
            : db.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(u => u.Email.Contains(s)
                          || u.FirstName.Contains(s)
                          || u.LastName.Contains(s));
        }

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(u => u.CreatedAt)
            .Skip(skip).Take(t)
            .Select(u => new AdminUserListItem(
                u.Id, u.Email, u.FirstName, u.LastName, u.JobTitle,
                u.GlobalRole, u.IsActive,
                u.OrganisationId, u.Organisation.Name, u.Organisation.Code,
                u.CreatedAt, u.LastLoginAt))
            .ToListAsync(ct);

        return new AdminPagedResult<AdminUserListItem>(rows, total, skip, t);
    }

    /// <summary>
    /// Set a user's GlobalRole. Three privilege guards:
    /// (1) caller cannot change their own role — would let an
    ///     OrgAdmin lock themselves out OR self-promote.
    /// (2) only SuperAdmin may set <c>SuperAdmin</c> — without this
    ///     guard an OrgAdmin could promote themselves or any user
    ///     in their tenant to SuperAdmin (privilege escalation).
    /// (3) OrgAdmin may only target users in their own tenant
    ///     (enforced by the standard query filter — cross-tenant
    ///     attempts hit the User lookup and 404 with no leak).
    /// SuperAdmin uses IgnoreQueryFilters to reach any user.
    /// Bumps TokenInvalidationCutoff so the new role is in effect
    /// on the next access-token issue (existing access tokens with
    /// the old role are rejected per ADR-0014).
    /// </summary>
    public async Task SetGlobalRoleAsync(Guid userId, UserRole? newRole, CancellationToken ct = default)
    {
        var actorId = tenant.UserId
            ?? throw new ForbiddenException("No tenant context");
        if (userId == actorId)
            throw new ForbiddenException("Cannot change your own role");
        if (newRole == UserRole.SuperAdmin && !tenant.IsSuperAdmin)
            throw new ForbiddenException("Only SuperAdmin may grant SuperAdmin");

        var query = tenant.IsSuperAdmin
            ? db.Users.IgnoreQueryFilters()
            : db.Users.AsQueryable();
        var user = await query.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User");

        var previous = user.GlobalRole;
        if (previous == newRole) return;

        user.GlobalRole = newRole;
        // The role baked into existing JWTs is now stale. Bump cutoff
        // so subsequent requests with the old token are rejected and
        // the user must refresh (ADR-0014).
        user.TokenInvalidationCutoff = DateTime.UtcNow;

        await audit.WriteAsync(actorId,
            "auth.user_global_role_set", "User", userId.ToString(),
            detail: new
            {
                targetUserId = userId,
                previousRole = previous?.ToString(),
                newRole      = newRole?.ToString(),
            });
        await db.SaveChangesAsync(ct);
    }
}

// ── Organisation admin ────────────────────────────────────────────────────────
// List + update + deactivate for organisations. SuperAdmin reaches
// every org; OrgAdmin can only see/edit their own (single row) per
// the role-matrix audit on /organisations.

public class OrganisationAdminService(CimsDbContext db, ITenantContext tenant, AuditService audit)
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    public async Task<AdminPagedResult<AdminOrgListItem>> ListAsync(
        string? search = null, int skip = 0, int? take = null,
        bool includeInactive = false, CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        var t = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        IQueryable<Organisation> q = db.Organisations;
        if (!includeInactive) q = q.Where(o => o.IsActive);

        if (!tenant.IsSuperAdmin)
        {
            var callerOrgId = tenant.OrganisationId
                ?? throw new ForbiddenException("No tenant context");
            q = q.Where(o => o.Id == callerOrgId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(o => o.Name.Contains(s) || o.Code.Contains(s));
        }

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderBy(o => o.Name)
            .Skip(skip).Take(t)
            .Select(o => new AdminOrgListItem(
                o.Id, o.Name, o.Code, o.Country, o.IsActive,
                db.Users.IgnoreQueryFilters().Count(u => u.OrganisationId == o.Id),
                db.Projects.IgnoreQueryFilters().Count(p => p.AppointingPartyId == o.Id),
                o.CreatedAt))
            .ToListAsync(ct);

        return new AdminPagedResult<AdminOrgListItem>(rows, total, skip, t);
    }

    /// <summary>
    /// Update Name and Country on an org. <c>Code</c> is intentionally
    /// immutable here — it's the canonical lookup key (used in
    /// project-template storage paths, audit log entries, and the
    /// Organisations.Code unique index). Renaming Code would orphan
    /// the on-disk template folder at <c>storage/projects/{code}</c>
    /// and require a migration. If the pilot ever needs to rename a
    /// Code, that's a v1.1 migration task with explicit data movement.
    /// OrgAdmin can only update their own org; SuperAdmin can target
    /// any org via IgnoreQueryFilters.
    /// </summary>
    public async Task<Organisation> UpdateAsync(Guid orgId, UpdateOrganisationRequest req, CancellationToken ct = default)
    {
        var actorId = tenant.UserId
            ?? throw new ForbiddenException("No tenant context");
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ValidationException(["Name is required"]);

        var query = tenant.IsSuperAdmin
            ? db.Organisations.IgnoreQueryFilters()
            : db.Organisations.AsQueryable();
        var org = await query.FirstOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new NotFoundException("Organisation");

        if (!tenant.IsSuperAdmin && org.Id != tenant.OrganisationId)
            throw new ForbiddenException("Cannot update another organisation");

        org.Name = req.Name.Trim();
        org.Country = string.IsNullOrWhiteSpace(req.Country) ? null : req.Country.Trim();
        org.UpdatedAt = DateTime.UtcNow;

        await audit.WriteAsync(actorId,
            "organisation.updated", "Organisation", orgId.ToString(),
            detail: new { orgId, newName = org.Name, newCountry = org.Country });
        await db.SaveChangesAsync(ct);
        return org;
    }

    /// <summary>
    /// Deactivate an org. SuperAdmin only — deactivating an org
    /// affects every user, project, and downstream record that
    /// belongs to it. OrgAdmin self-deactivation would lock
    /// themselves out atomically (their next request would 401),
    /// which is a footgun without a recovery path; deferred to v1.1.
    /// Sets <c>IsActive = false</c>; downstream records keep their
    /// own IsActive flags untouched (per-entity deactivation is a
    /// separate v1.1 concern — B-099).
    /// </summary>
    public async Task DeactivateAsync(Guid orgId, CancellationToken ct = default)
    {
        if (!tenant.IsSuperAdmin)
            throw new ForbiddenException("Only SuperAdmin may deactivate an organisation");
        var actorId = tenant.UserId
            ?? throw new ForbiddenException("No tenant context");

        var org = await db.Organisations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new NotFoundException("Organisation");
        if (!org.IsActive) return;

        org.IsActive = false;
        org.UpdatedAt = DateTime.UtcNow;

        await audit.WriteAsync(actorId,
            "organisation.deactivated", "Organisation", orgId.ToString(),
            detail: new { orgId });
        await db.SaveChangesAsync(ct);
    }
}

// ── Invitation admin ──────────────────────────────────────────────────────────
// List active (unconsumed, unexpired) invitations + revoke pending.
// Mint already exists at InvitationService.CreateAsync (consumed by
// the existing POST /organisations/{orgId}/invitations endpoint).

public class InvitationAdminService(CimsDbContext db, ITenantContext tenant, AuditService audit)
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    /// <summary>
    /// List active (unconsumed AND unexpired) invitations.
    /// OrgAdmin sees their own tenant only via the standard
    /// Invitations query filter; SuperAdmin sees all via
    /// IgnoreQueryFilters. Filter to a specific org with
    /// <paramref name="orgId"/>.
    /// </summary>
    public async Task<AdminPagedResult<AdminInvitationListItem>> ListActiveAsync(
        Guid? orgId = null, int skip = 0, int? take = null,
        CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        var t = Math.Clamp(take ?? DefaultTake, 1, MaxTake);
        var now = DateTime.UtcNow;

        var q = tenant.IsSuperAdmin
            ? db.Invitations.IgnoreQueryFilters()
            : db.Invitations.AsQueryable();
        q = q.Where(i => i.ConsumedAt == null && i.ExpiresAt > now);
        if (orgId is { } id) q = q.Where(i => i.OrganisationId == id);

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(i => i.CreatedAt)
            .Skip(skip).Take(t)
            .Select(i => new AdminInvitationListItem(
                i.Id, i.OrganisationId, i.Organisation.Name,
                i.Email, i.IsBootstrap, i.ExpiresAt, i.CreatedAt,
                i.CreatedById,
                i.CreatedById == null ? null
                    : i.CreatedBy!.FirstName + " " + i.CreatedBy.LastName))
            .ToListAsync(ct);

        return new AdminPagedResult<AdminInvitationListItem>(rows, total, skip, t);
    }

    /// <summary>
    /// Revoke a pending invitation by setting <c>ExpiresAt = UtcNow</c>.
    /// Hard-deletion would orphan any audit references and lose the
    /// "this invitation existed" record; expiring it preserves the
    /// row while making it unusable. Already-consumed invitations
    /// cannot be revoked — they minted a User and the right action
    /// is to deactivate the User, not the invitation.
    /// </summary>
    public async Task RevokeAsync(Guid invitationId, CancellationToken ct = default)
    {
        var actorId = tenant.UserId
            ?? throw new ForbiddenException("No tenant context");

        var query = tenant.IsSuperAdmin
            ? db.Invitations.IgnoreQueryFilters()
            : db.Invitations.AsQueryable();
        var inv = await query.FirstOrDefaultAsync(i => i.Id == invitationId, ct)
            ?? throw new NotFoundException("Invitation");

        if (inv.ConsumedAt != null)
            throw new ValidationException(["Invitation already consumed; deactivate the user instead"]);
        if (inv.ExpiresAt <= DateTime.UtcNow) return;

        inv.ExpiresAt = DateTime.UtcNow;

        await audit.WriteAsync(actorId,
            "invitation.revoked", "Invitation", invitationId.ToString(),
            detail: new { invitationId, organisationId = inv.OrganisationId });
        await db.SaveChangesAsync(ct);
    }
}

// ── Audit admin ───────────────────────────────────────────────────────────────
// Tenant-scoped paged audit log with date / action / entity / user
// filters. Existing AuditController is project-scoped; this is the
// org-wide complement. Reuses the existing AuditLog query filter
// (User.OrganisationId == _tenant.OrganisationId), so OrgAdmin sees
// only their tenant's rows. SuperAdmin via IgnoreQueryFilters.
//
// Anonymous-actor rows (User == null) are hidden from tenant queries
// by the existing filter (PR #41 semantic) — SuperAdmin includes them.

public class AuditAdminService(CimsDbContext db, ITenantContext tenant)
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    public async Task<AdminPagedResult<AdminAuditListItem>> ListAsync(
        DateTime? from = null, DateTime? to = null,
        string? actionContains = null, string? entity = null,
        Guid? userId = null,
        int skip = 0, int? take = null, CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        var t = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        var q = tenant.IsSuperAdmin
            ? db.AuditLogs.IgnoreQueryFilters()
            : db.AuditLogs.AsQueryable();

        if (from is { } f) q = q.Where(a => a.CreatedAt >= f);
        if (to is { } u) q = q.Where(a => a.CreatedAt <= u);
        if (!string.IsNullOrWhiteSpace(actionContains))
        {
            var s = actionContains.Trim();
            q = q.Where(a => a.Action.Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(entity))
        {
            var s = entity.Trim();
            q = q.Where(a => a.Entity == s);
        }
        if (userId is { } uid) q = q.Where(a => a.UserId == uid);

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(a => a.CreatedAt)
            .Skip(skip).Take(t)
            .Select(a => new AdminAuditListItem(
                a.Id, a.CreatedAt, a.Action, a.Entity, a.EntityId,
                a.UserId,
                a.User == null ? null : a.User.FirstName + " " + a.User.LastName,
                a.ProjectId, a.IpAddress))
            .ToListAsync(ct);

        return new AdminPagedResult<AdminAuditListItem>(rows, total, skip, t);
    }
}
