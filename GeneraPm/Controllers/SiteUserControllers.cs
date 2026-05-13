using GeneraPm.Core;
using GeneraPm.Data;
using GeneraPm.Models;
using GeneraPm.Services.SiteUserWorkflows;
using Microsoft.AspNetCore.Mvc;

namespace GeneraPm.Controllers;

// T-S20-04 Site-user mobile workflow controllers. Two project-scoped
// surfaces:
//   /api/v1/projects/{id}/diary  — daily diary entries
//   /api/v1/projects/{id}/ncrs   — NCR raise + assign + transition

[Route("api/v1/projects/{projectId:guid}/diary")]
public class DailyDiaryController(
    DailyDiaryService svc, GeneraPmDbContext db) : GeneraPmControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] int skip = 0,
        [FromQuery] int? take = null,
        CancellationToken ct = default)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.ListAsync(projectId, from, to, skip, take, ct) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId, CreateDailyDiaryRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var row = await svc.CreateAsync(projectId, req, CurrentUserId, ct);
        return Created("", new { success = true, data = new { row.Id, row.DiaryDate } });
    }

    [HttpPut("{entryId:guid}")]
    public async Task<IActionResult> Update(
        Guid projectId, Guid entryId, UpdateDailyDiaryRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        // Authoring write — TaskTeamMember+ may edit. Author-only
        // restriction (only the original author can edit their own
        // diary) is a tighter rule that v1.1 may want; v1.0 lets
        // any TaskTeamMember+ in the project edit, audit-twin
        // captures who.
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var row = await svc.UpdateAsync(entryId, req, CurrentUserId, ct);
        return Ok(new { success = true, data = new { row.Id } });
    }
}

[Route("api/v1/projects/{projectId:guid}/ncrs")]
public class NcrController(
    NcrService svc, GeneraPmDbContext db) : GeneraPmControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] NcrState? status = null,
        [FromQuery] int skip = 0,
        [FromQuery] int? take = null,
        CancellationToken ct = default)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.ListAsync(projectId, status, skip, take, ct) });
    }

    [HttpGet("{ncrId:guid}")]
    public async Task<IActionResult> Get(
        Guid projectId, Guid ncrId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.GetAsync(ncrId, ct) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId, CreateNcrRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var row = await svc.CreateAsync(projectId, req, CurrentUserId, ct);
        return Created("", new { success = true, data = new { row.Id, row.Number } });
    }

    [HttpPost("{ncrId:guid}/assign")]
    public async Task<IActionResult> Assign(
        Guid projectId, Guid ncrId, AssignNcrRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var row = await svc.AssignAsync(ncrId, req, role, CurrentUserId, ct);
        return Ok(new { success = true, data = new { row.Id, row.Status, row.AssignedToId } });
    }

    [HttpPost("{ncrId:guid}/transition")]
    public async Task<IActionResult> Transition(
        Guid projectId, Guid ncrId, TransitionNcrRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var row = await svc.TransitionAsync(ncrId, req, role, CurrentUserId, ct);
        return Ok(new { success = true, data = new { row.Id, row.Status } });
    }
}
