using GeneraPm.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GeneraPm.Controllers;

// T-S16-03 — Admin Console controllers. Four separate controllers
// under /api/v1/admin/* matching the four admin services. Class-level
// [Authorize(Roles = "OrgAdmin,SuperAdmin")] gates every endpoint;
// per-action authorisation refinements (e.g. SuperAdmin-only
// org deactivation) live in the service layer so they're enforced
// regardless of caller (controller, future Razor server-side handler,
// integration test).

// ── /admin/users ──────────────────────────────────────────────────────────────
[Route("api/v1/admin/users")]
[Authorize(Roles = "OrgAdmin,SuperAdmin")]
public class AdminUsersController(UserAdminService svc) : GeneraPmControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search = null,
        [FromQuery] int skip = 0,
        [FromQuery] int? take = null,
        CancellationToken ct = default) =>
        Ok(new { success = true, data = await svc.ListAsync(search, skip, take, ct) });

    [HttpPut("{userId:guid}/global-role")]
    public async Task<IActionResult> SetGlobalRole(
        Guid userId, SetGlobalRoleRequest req, CancellationToken ct)
    {
        await svc.SetGlobalRoleAsync(userId, req.GlobalRole, ct);
        return Ok(new { success = true });
    }
}

// ── /admin/organisations ──────────────────────────────────────────────────────
[Route("api/v1/admin/organisations")]
[Authorize(Roles = "OrgAdmin,SuperAdmin")]
public class AdminOrganisationsController(OrganisationAdminService svc) : GeneraPmControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search = null,
        [FromQuery] int skip = 0,
        [FromQuery] int? take = null,
        [FromQuery] bool includeInactive = false,
        CancellationToken ct = default) =>
        Ok(new { success = true, data = await svc.ListAsync(search, skip, take, includeInactive, ct) });

    [HttpPut("{orgId:guid}")]
    public async Task<IActionResult> Update(
        Guid orgId, UpdateOrganisationRequest req, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.UpdateAsync(orgId, req, ct) });

    [HttpPost("{orgId:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid orgId, CancellationToken ct)
    {
        await svc.DeactivateAsync(orgId, ct);
        return Ok(new { success = true });
    }
}

// ── /admin/invitations ────────────────────────────────────────────────────────
[Route("api/v1/admin/invitations")]
[Authorize(Roles = "OrgAdmin,SuperAdmin")]
public class AdminInvitationsController(InvitationAdminService svc) : GeneraPmControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? organisationId = null,
        [FromQuery] int skip = 0,
        [FromQuery] int? take = null,
        CancellationToken ct = default) =>
        Ok(new { success = true, data = await svc.ListActiveAsync(organisationId, skip, take, ct) });

    [HttpDelete("{invitationId:guid}")]
    public async Task<IActionResult> Revoke(Guid invitationId, CancellationToken ct)
    {
        await svc.RevokeAsync(invitationId, ct);
        return Ok(new { success = true });
    }
}

// ── /admin/audit ──────────────────────────────────────────────────────────────
[Route("api/v1/admin/audit")]
[Authorize(Roles = "OrgAdmin,SuperAdmin")]
public class AdminAuditController(AuditAdminService svc) : GeneraPmControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? action = null,
        [FromQuery] string? entity = null,
        [FromQuery] Guid? userId = null,
        [FromQuery] int skip = 0,
        [FromQuery] int? take = null,
        CancellationToken ct = default) =>
        Ok(new { success = true, data = await svc.ListAsync(from, to, action, entity, userId, skip, take, ct) });
}
