using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;

namespace CimsApp.Controllers;

[ApiController]
[Authorize]
public abstract class CimsControllerBase : ControllerBase
{
    protected Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    protected string? ClientIp    => HttpContext.Connection.RemoteIpAddress?.ToString();
    protected string? ClientAgent => Request.Headers.UserAgent.ToString();

    protected async Task<UserRole> GetProjectRoleAsync(CimsDbContext db, Guid projectId)
    {
        var m = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == CurrentUserId && m.IsActive);
        return m?.Role ?? throw new ForbiddenException("Not a member of this project");
    }
}

// ── Auth ──────────────────────────────────────────────────────────────────────
[AllowAnonymous, Route("api/v1/auth")]
public class AuthController(AuthService svc) : ControllerBase
{
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest req) =>
        Created("", new { success = true, data = await svc.RegisterAsync(req) });

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest req) =>
        Ok(new { success = true, data = await svc.LoginAsync(req, Request.Headers.UserAgent.ToString(), HttpContext.Connection.RemoteIpAddress?.ToString()) });

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshRequest req)
    { var (a, r) = await svc.RefreshAsync(req.RefreshToken); return Ok(new { success = true, data = new { accessToken = a, refreshToken = r } }); }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshRequest req)
    { await svc.LogoutAsync(req.RefreshToken); return Ok(new { success = true }); }

    [Authorize, HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user   = await svc.GetUserAsync(userId);
        return Ok(new { success = true, data = new UserSummaryDto(user.Id, user.Email, user.FirstName, user.LastName, user.JobTitle, new OrgSummaryDto(user.Organisation.Id, user.Organisation.Name, user.Organisation.Code)) });
    }
}

// ── Organisations ─────────────────────────────────────────────────────────────
[Route("api/v1/organisations")]
public class OrganisationsController(
    CimsDbContext db,
    InvitationService invitations,
    CimsApp.Services.Tenancy.ITenantContext tenant) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search = null)
    {
        var q = db.Organisations.Where(o => o.IsActive);
        if (!string.IsNullOrEmpty(search)) q = q.Where(o => o.Name.Contains(search));
        return Ok(new { success = true, data = await q.OrderBy(o => o.Name).ToListAsync() });
    }

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Create(CreateOrgRequest req)
    {
        if (await db.Organisations.AnyAsync(o => o.Code == req.Code.ToUpperInvariant()))
            throw new ConflictException($"Code '{req.Code}' already exists");
        var org = new Organisation { Name = req.Name, Code = req.Code.ToUpperInvariant(), Country = req.Country };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        // Mint a 24-hour bootstrap invitation so the sign-up flow can
        // continue with POST /api/v1/auth/register. The first registrant
        // becomes the org's first OrgAdmin (set in AuthService.RegisterAsync).
        // The plaintext token is shown ONCE in this response; only the
        // hash is persisted, so a lost token is irrecoverable. See ADR-0011.
        var bootstrap = await invitations.CreateAsync(
            organisationId: org.Id,
            createdById:    null,    // anonymous flow
            emailBind:      null,    // registrant chooses their own email
            expiresInDays:  1,
            isBootstrap:    true);
        return Created("", new
        {
            success = true,
            data = new
            {
                organisation = org,
                bootstrap    = new InvitationDto(bootstrap.Id, bootstrap.Token, bootstrap.ExpiresAt, true, null),
            },
        });
    }

    [HttpPost("{orgId:guid}/invitations")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> CreateInvitation(Guid orgId, CreateInvitationRequest req)
    {
        // OrgAdmin can only mint for their own organisation; SuperAdmin
        // may mint for any. Mirrors ADR-0012's caller's-org-default
        // platform-reading rule for project creation.
        if (!tenant.IsSuperAdmin && tenant.OrganisationId != orgId)
            throw new ForbiddenException("Cannot mint invitations for another organisation");

        var inv = await invitations.CreateAsync(
            organisationId: orgId,
            createdById:    CurrentUserId,
            emailBind:      req.Email,
            expiresInDays:  req.ExpiresInDays ?? 7,
            isBootstrap:    false);

        return Created("", new
        {
            success = true,
            data = new InvitationDto(inv.Id, inv.Token, inv.ExpiresAt, false, req.Email),
        });
    }
}

// ── Projects ──────────────────────────────────────────────────────────────────
[Route("api/v1/projects")]
public class ProjectsController(ProjectsService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search = null) =>
        Ok(new { success = true, data = await svc.ListAsync(CurrentUserId, search) });

    [HttpPost]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Create(CreateProjectRequest req) =>
        Created("", new { success = true, data = await svc.CreateAsync(req, CurrentUserId, ClientIp, ClientAgent) });

    [HttpGet("{projectId:guid}")]
    public async Task<IActionResult> Get(Guid projectId) =>
        Ok(new { success = true, data = await svc.GetByIdAsync(projectId, CurrentUserId) });

    [HttpPost("{projectId:guid}/members")]
    public async Task<IActionResult> AddMember(Guid projectId, AddMemberRequest req)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager)) throw new ForbiddenException();
        await svc.AddMemberAsync(projectId, req.UserId, req.Role, CurrentUserId);
        return Created("", new { success = true });
    }
}

// ── CDE ───────────────────────────────────────────────────────────────────────
[Route("api/v1/projects/{projectId:guid}/cde")]
public class CdeController(CdeService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet("containers")]
    public async Task<IActionResult> List(Guid projectId)
    { await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.ListContainersAsync(projectId) }); }

    [HttpPost("containers")]
    public async Task<IActionResult> Create(Guid projectId, CreateContainerRequest req)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager)) throw new ForbiddenException();
        return Created("", new { success = true, data = await svc.CreateContainerAsync(projectId, req, CurrentUserId, ClientIp, ClientAgent) });
    }
}

// ── Cost & Commercial ─────────────────────────────────────────────────────────
[Route("api/v1/projects/{projectId:guid}/cbs")]
public class CostController(CostService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpPost("import")]
    public async Task<IActionResult> Import(Guid projectId, IFormFile file, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        if (file is null || file.Length == 0)
            throw new ValidationException(["file is required"]);
        await using var stream = file.OpenReadStream();
        var result = await svc.ImportCbsAsync(projectId, stream,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = result });
    }

    [HttpPut("{itemId:guid}/budget")]
    public async Task<IActionResult> SetLineBudget(
        Guid projectId, Guid itemId, SetLineBudgetRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        await svc.SetLineBudgetAsync(projectId, itemId, req.Budget,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true });
    }
}

// ── Documents ─────────────────────────────────────────────────────────────────
[Route("api/v1/projects/{projectId:guid}/documents")]
public class DocumentsController(DocumentsService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, [FromQuery] string? state = null, [FromQuery] string? search = null)
    {
        await GetProjectRoleAsync(db, projectId);
        CdeState? s = Enum.TryParse<CdeState>(state, true, out var parsed) ? parsed : null;
        return Ok(new { success = true, data = await svc.ListAsync(projectId, s, search) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid projectId, CreateDocumentRequest req)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember)) throw new ForbiddenException();
        return Created("", new { success = true, data = await svc.CreateAsync(projectId, req, CurrentUserId, ClientIp, ClientAgent) });
    }

    [HttpGet("{documentId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid documentId)
    { await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.GetByIdAsync(documentId, projectId) }); }

    [HttpPost("{documentId:guid}/transition")]
    public async Task<IActionResult> Transition(Guid projectId, Guid documentId, TransitionRequest req)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.TransitionAsync(documentId, projectId, req.ToState, req.Suitability, CurrentUserId, role, ClientIp, ClientAgent) });
    }
}

// ── RFIs ──────────────────────────────────────────────────────────────────────
[Route("api/v1/projects/{projectId:guid}/rfis")]
public class RfisController(RfiService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, [FromQuery] string? status = null, [FromQuery] string? search = null)
    {
        await GetProjectRoleAsync(db, projectId);
        RfiStatus? s = Enum.TryParse<RfiStatus>(status, true, out var p) ? p : null;
        return Ok(new { success = true, data = await svc.ListAsync(projectId, s, search) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid projectId, CreateRfiRequest req)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember)) throw new ForbiddenException();
        return Created("", new { success = true, data = await svc.CreateAsync(projectId, req, CurrentUserId, ClientIp, ClientAgent) });
    }

    [HttpPost("{rfiId:guid}/respond")]
    public async Task<IActionResult> Respond(Guid projectId, Guid rfiId, RespondRfiRequest req)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember)) throw new ForbiddenException();
        return Ok(new { success = true, data = await svc.RespondAsync(rfiId, projectId, req, CurrentUserId, ClientIp, ClientAgent) });
    }
}

// ── Actions ───────────────────────────────────────────────────────────────────
[Route("api/v1/projects/{projectId:guid}/actions")]
public class ActionsController(ActionsService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, [FromQuery] string? status = null, [FromQuery] bool overdue = false)
    {
        await GetProjectRoleAsync(db, projectId);
        ActionStatus? s = Enum.TryParse<ActionStatus>(status, true, out var p) ? p : null;
        return Ok(new { success = true, data = await svc.ListAsync(projectId, s, overdue) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid projectId, CreateActionRequest req)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember)) throw new ForbiddenException();
        return Created("", new { success = true, data = await svc.CreateAsync(projectId, req, CurrentUserId, ClientIp, ClientAgent) });
    }

    [HttpPatch("{actionId:guid}")]
    public async Task<IActionResult> Update(Guid projectId, Guid actionId, UpdateActionRequest req)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember)) throw new ForbiddenException();
        return Ok(new { success = true, data = await svc.UpdateAsync(actionId, projectId, req, CurrentUserId, ClientIp, ClientAgent) });
    }
}

// ── Audit ─────────────────────────────────────────────────────────────────────
[Route("api/v1/projects/{projectId:guid}/audit")]
public class AuditController(CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, [FromQuery] int limit = 50)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager)) throw new ForbiddenException();
        var logs = await db.AuditLogs.Include(a => a.User).Where(a => a.ProjectId == projectId)
            .OrderByDescending(a => a.CreatedAt).Take(limit).ToListAsync();
        return Ok(new { success = true, data = logs });
    }
}
