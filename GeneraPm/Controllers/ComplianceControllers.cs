using GeneraPm.Core;
using GeneraPm.Data;
using GeneraPm.Models;
using GeneraPm.Services.Compliance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GeneraPm.Controllers;

// T-S18 Audit & Compliance Support controllers. Three controllers
// matching the three F.17 bullets: Evidence library, Auditor admin,
// Audit export. Read endpoints are member-or-auditor; mutation
// endpoints are gated to InformationManager-and-up via the existing
// HasMinimumRole helper. ExternalAuditor users carry
// UserRole.ExternalAuditor which is OUTSIDE the role hierarchy —
// every HasMinimumRole(ExternalAuditor, _) call returns false, so
// mutation gates reject without per-call special-casing.

// ── /api/v1/projects/{id}/evidence ────────────────────────────────────────────
[Route("api/v1/projects/{projectId:guid}/evidence")]
public class EvidenceController(
    EvidenceLibraryService svc, GeneraPmDbContext db) : GeneraPmControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] ComplianceArea? area = null,
        [FromQuery] int skip = 0,
        [FromQuery] int? take = null,
        CancellationToken ct = default)
    {
        // Read access: any project member (incl. ExternalAuditor with
        // active assignment via the GetProjectRoleAsync override).
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.ListAsync(projectId, area, skip, take, ct) });
    }

    [HttpPost]
    public async Task<IActionResult> Add(
        Guid projectId, AddEvidenceRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager))
            throw new ForbiddenException();
        var row = await svc.AddAsync(projectId, req, CurrentUserId, ct);
        return Created("", new { success = true, data = new { row.Id } });
    }

    [HttpDelete("{evidenceId:guid}")]
    public async Task<IActionResult> Remove(
        Guid projectId, Guid evidenceId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager))
            throw new ForbiddenException();
        await svc.RemoveAsync(evidenceId, CurrentUserId, ct);
        return Ok(new { success = true });
    }
}

// ── /api/v1/admin/auditor-assignments ─────────────────────────────────────────
// Cross-project surface — managed by tenant admin (OrgAdmin /
// SuperAdmin). Lives under /admin/* alongside the other S16 admin
// surfaces.
[Route("api/v1/admin/auditor-assignments")]
[Authorize(Roles = "OrgAdmin,SuperAdmin")]
public class AdminAuditorAssignmentsController(AuditorAdminService svc) : GeneraPmControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? projectId = null,
        [FromQuery] bool includeRevoked = false,
        [FromQuery] int skip = 0,
        [FromQuery] int? take = null,
        CancellationToken ct = default) =>
        Ok(new { success = true, data = await svc.ListAsync(projectId, includeRevoked, skip, take, ct) });

    [HttpPost]
    public async Task<IActionResult> Create(
        CreateAuditorAssignmentRequest req, CancellationToken ct)
    {
        var row = await svc.CreateAsync(req, CurrentUserId, ct);
        return Created("", new
        {
            success = true,
            data = new { row.Id, row.UserId, row.ProjectId, row.ExpiresAt, row.ScopeAreas }
        });
    }

    [HttpPost("{assignmentId:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid assignmentId, CancellationToken ct)
    {
        await svc.RevokeAsync(assignmentId, CurrentUserId, ct);
        return Ok(new { success = true });
    }
}

// ── /api/v1/projects/{id}/audit-export ────────────────────────────────────────
[Route("api/v1/projects/{projectId:guid}/audit-export")]
public class AuditExportController(
    AuditExportService svc, GeneraPmDbContext db) : GeneraPmControllerBase
{
    /// <summary>
    /// Build a ZIP bundle for the project + window. ExternalAuditor
    /// users can call this on their assigned project (read-only,
    /// passes the membership check). Mutations don't apply (export
    /// is read-only). Default window: last 90 days.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Export(
        Guid projectId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] ComplianceAreaScope areas = ComplianceAreaScope.All,
        CancellationToken ct = default)
    {
        await GetProjectRoleAsync(db, projectId);

        var fromUtc = from ?? DateTime.UtcNow.AddDays(-90);
        var toUtc   = to   ?? DateTime.UtcNow;

        var bytes = await svc.BuildBundleAsync(projectId, areas, fromUtc, toUtc, ct);
        var filename = $"audit-bundle-{projectId:N}-{fromUtc:yyyyMMdd}-{toUtc:yyyyMMdd}.zip";
        return File(bytes, "application/zip", filename);
    }
}
