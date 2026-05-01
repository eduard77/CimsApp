using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
// [ApiController] is REQUIRED here. Without it, the model binder
// does NOT infer [FromBody] for complex action parameters, so
// RegisterRequest / LoginRequest / RefreshRequest arrive as default
// records (all string fields == ""), the null-guards fire on every
// request, and every auth endpoint silently returns the same
// "Invalid credentials" / "required fields" error regardless of the
// actual JSON body. This was latent because:
//   - Unit tests construct DTOs directly, never exercising binding.
//   - Both an empty body and a real body produce the same response
//     code (401 / 400) thanks to the defensive guards, so a casual
//     curl smoke would not notice.
// AuthController extends ControllerBase directly (NOT
// CimsControllerBase) to bypass the [Authorize] baseline; that's
// why we have to repeat [ApiController] explicitly here. Found by
// running the bootstrap → register flow against real SQL Server
// (2026-04-29).
[ApiController]
[Route("api/v1/auth")]
public class AuthController(AuthService svc) : ControllerBase
{
    // Class-level [AllowAnonymous] used to live here. Per ASP.NET
    // Core docs, [AllowAnonymous] at the controller level overrides
    // EVERY [Authorize] attribute on every action — meaning the
    // [Authorize, HttpGet("me")] and [Authorize, HttpPost("logout-everywhere")]
    // declarations below were silently ignored, and those endpoints
    // were reachable without a token. Calling /me without auth threw
    // ArgumentNullException at Guid.Parse(User.FindFirstValue(NameIdentifier)!)
    // → HTTP 500 instead of the expected 401. Found 2026-04-30 by
    // smoke-testing /logout-everywhere then re-calling /me with the
    // (now post-cutoff) access token.
    //
    // Fix: scope [AllowAnonymous] to the genuinely-anonymous actions
    // (register / login / refresh / logout). Me + LogoutEverywhere
    // get the default [Authorize] behaviour (auth middleware
    // short-circuits unauthenticated callers with 401 before the
    // action runs).

    [AllowAnonymous, HttpPost("register"), EnableRateLimiting("anon-default")]
    public async Task<IActionResult> Register(RegisterRequest req) =>
        Created("", new { success = true, data = await svc.RegisterAsync(req) });

    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("anon-login")]
    public async Task<IActionResult> Login(LoginRequest req) =>
        Ok(new { success = true, data = await svc.LoginAsync(req, Request.Headers.UserAgent.ToString(), HttpContext.Connection.RemoteIpAddress?.ToString()) });

    [AllowAnonymous, HttpPost("refresh"), EnableRateLimiting("anon-default")]
    public async Task<IActionResult> Refresh(RefreshRequest req)
    { var (a, r) = await svc.RefreshAsync(req.RefreshToken); return Ok(new { success = true, data = new { accessToken = a, refreshToken = r } }); }

    [AllowAnonymous, HttpPost("logout"), EnableRateLimiting("anon-default")]
    public async Task<IActionResult> Logout(RefreshRequest req)
    { await svc.LogoutAsync(req.RefreshToken); return Ok(new { success = true }); }

    [Authorize, HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user   = await svc.GetUserAsync(userId);
        return Ok(new { success = true, data = new UserSummaryDto(user.Id, user.Email, user.FirstName, user.LastName, user.JobTitle, new OrgSummaryDto(user.Organisation.Id, user.Organisation.Name, user.Organisation.Code)) });
    }

    /// <summary>
    /// B-001 / ADR-0014: self-service "log out everywhere". Bumps the
    /// caller's own <see cref="User.TokenInvalidationCutoff"/>; all
    /// access tokens minted before this moment — including the one
    /// in the request that called this endpoint — are rejected at
    /// the next authenticated request.
    /// </summary>
    [Authorize, HttpPost("logout-everywhere")]
    public async Task<IActionResult> LogoutEverywhere()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await svc.RevokeOwnTokensAsync(userId);
        return Ok(new { success = true });
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
        // B-007: scope non-SuperAdmin callers to their own organisation
        // only. ADR-0003 leaves Organisation intentionally unfiltered at
        // the DbContext level (it's the tenant anchor itself, used during
        // pre-auth flows like sign-up and login), so the scoping has to
        // happen at the controller. Without this, any authenticated
        // OrgAdmin in Org A could enumerate every other organisation's
        // Name and Code via this endpoint. SuperAdmin retains the wider
        // view per ADR-0007.
        var q = db.Organisations.Where(o => o.IsActive);
        if (!tenant.IsSuperAdmin)
        {
            var callerOrgId = tenant.OrganisationId
                ?? throw new ForbiddenException("No tenant context");
            q = q.Where(o => o.Id == callerOrgId);
        }
        if (!string.IsNullOrEmpty(search)) q = q.Where(o => o.Name.Contains(search));
        return Ok(new { success = true, data = await q.OrderBy(o => o.Name).ToListAsync() });
    }

    [HttpPost]
    [AllowAnonymous, EnableRateLimiting("anon-default")]
    public async Task<IActionResult> Create(CreateOrgRequest req)
    {
        if (await db.Organisations.AnyAsync(o => o.Code == req.Code.ToUpperInvariant()))
            throw new ConflictException($"Code '{req.Code}' already exists");
        var org = new Organisation { Name = req.Name, Code = req.Code.ToUpperInvariant(), Country = req.Country };
        // Wrap the Organisation save and the bootstrap-invitation
        // save in one transaction. Without this wrap, a process crash
        // between the two saves would leave an Organisation created
        // (Code reserved by the unique index) but no bootstrap
        // invitation minted — the operator could not register against
        // the new org and could not retry the create call (409 on the
        // Code). The org row would be orphaned forever. Same shape
        // as the RegisterAsync transaction wrap (PR #29). EF in-memory
        // provider treats this as a no-op.
        await using var tx = await db.Database.BeginTransactionAsync();
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
        await tx.CommitAsync();
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

// ── Users (admin) ─────────────────────────────────────────────────────────────
// B-001 / ADR-0014: admin user-management endpoints that consume the
// revocation primitive. OrgAdmin can target users in their own
// organisation only (tenant query filter on the User lookup);
// SuperAdmin can target any user (IgnoreQueryFilters via the
// `IsSuperAdmin` branch in the service).
[Route("api/v1/users")]
public class UsersController(
    AuthService svc,
    CimsApp.Services.Tenancy.ITenantContext tenant) : CimsControllerBase
{
    [HttpPost("{userId:guid}/revoke-tokens")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> RevokeTokens(Guid userId)
    {
        await svc.RevokeUserTokensAsync(userId, tenant);
        return Ok(new { success = true });
    }

    [HttpPost("{userId:guid}/deactivate")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Deactivate(Guid userId)
    {
        await svc.DeactivateUserAsync(userId, tenant);
        return Ok(new { success = true });
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

    [HttpGet("rollup")]
    public async Task<IActionResult> Rollup(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.GetCbsRollupAsync(projectId, ct) });
    }

    [HttpPut("{itemId:guid}/schedule")]
    public async Task<IActionResult> SetLineSchedule(
        Guid projectId, Guid itemId, SetLineScheduleRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        await svc.SetLineScheduleAsync(projectId, itemId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true });
    }

    [HttpPut("{itemId:guid}/progress")]
    public async Task<IActionResult> SetLineProgress(
        Guid projectId, Guid itemId, SetLineProgressRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        await svc.SetLineProgressAsync(projectId, itemId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true });
    }
}

[Route("api/v1/projects/{projectId:guid}/commitments")]
public class CommitmentsController(CostService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId, CreateCommitmentRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var c = await svc.CreateCommitmentAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = c });
    }
}

[Route("api/v1/projects/{projectId:guid}/cost-periods")]
public class CostPeriodsController(CostService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId, CreatePeriodRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var p = await svc.CreatePeriodAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = p });
    }

    [HttpPost("{periodId:guid}/close")]
    public async Task<IActionResult> Close(
        Guid projectId, Guid periodId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        await svc.ClosePeriodAsync(projectId, periodId,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true });
    }

    [HttpPut("{periodId:guid}/baseline")]
    public async Task<IActionResult> SetBaseline(
        Guid projectId, Guid periodId, SetPeriodBaselineRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        await svc.SetPeriodBaselineAsync(projectId, periodId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true });
    }
}

[Route("api/v1/projects/{projectId:guid}/cashflow")]
public class CashflowController(CostService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetCashflowAsync(projectId, ct);
        return Ok(new { success = true, data = dto });
    }

    [HttpGet("by-line")]
    public async Task<IActionResult> GetByLine(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetCashflowByLineAsync(projectId, ct);
        return Ok(new { success = true, data = dto });
    }
}

[Route("api/v1/projects/{projectId:guid}/evm")]
public class EvmController(CostService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        Guid projectId,
        [FromQuery] DateTime? dataDate,
        CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var snapshot = await svc.GetEvmSnapshotAsync(
            projectId,
            dataDate ?? DateTime.UtcNow,
            ct);
        return Ok(new { success = true, data = snapshot });
    }
}

[Route("api/v1/projects/{projectId:guid}/actuals")]
public class ActualsController(CostService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Record(
        Guid projectId, RecordActualRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var a = await svc.RecordActualAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = a });
    }
}

[Route("api/v1/projects/{projectId:guid}/payment-certificates")]
public class PaymentCertificatesController(PaymentCertificatesService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpPost]
    public async Task<IActionResult> CreateDraft(
        Guid projectId, CreatePaymentCertificateDraftRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var dto = await svc.CreateDraftAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = dto });
    }

    [HttpPut("{certificateId:guid}")]
    public async Task<IActionResult> UpdateDraft(
        Guid projectId, Guid certificateId,
        UpdatePaymentCertificateDraftRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var dto = await svc.UpdateDraftAsync(projectId, certificateId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = dto });
    }

    [HttpPost("{certificateId:guid}/issue")]
    public async Task<IActionResult> Issue(
        Guid projectId, Guid certificateId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var dto = await svc.IssueAsync(projectId, certificateId,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = dto });
    }

    [HttpGet("{certificateId:guid}")]
    public async Task<IActionResult> Get(
        Guid projectId, Guid certificateId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetAsync(projectId, certificateId, ct);
        return Ok(new { success = true, data = dto });
    }
}

[Route("api/v1/projects/{projectId:guid}/variations")]
public class VariationsController(VariationsService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Raise(
        Guid projectId, RaiseVariationRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var v = await svc.RaiseAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = v });
    }

    [HttpPost("{variationId:guid}/approve")]
    public async Task<IActionResult> Approve(
        Guid projectId, Guid variationId, VariationDecisionRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        await svc.ApproveAsync(projectId, variationId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true });
    }

    [HttpPost("{variationId:guid}/reject")]
    public async Task<IActionResult> Reject(
        Guid projectId, Guid variationId, VariationDecisionRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        await svc.RejectAsync(projectId, variationId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true });
    }
}

// ── Risk & Opportunities ──────────────────────────────────────────────────────
// PAFM-SD F.3 (S2 module). T-S2-04 controller surface — Create, Update,
// Close. Per S2 kickoff: TaskTeamMember+ for create/update,
// ProjectManager+ for close.
[Route("api/v1/projects/{projectId:guid}/risks")]
public class RisksController(RisksService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        // Membership-only: any project member can read the register.
        await GetProjectRoleAsync(db, projectId);
        var risks = await svc.ListAsync(projectId, ct);
        return Ok(new { success = true, data = risks });
    }

    [HttpGet("matrix")]
    public async Task<IActionResult> Matrix(Guid projectId, CancellationToken ct)
    {
        // Membership-only: heat-map is a read view.
        await GetProjectRoleAsync(db, projectId);
        var cells = await svc.GetMatrixAsync(projectId, ct);
        return Ok(new { success = true, data = cells });
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId, CreateRiskRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var risk = await svc.CreateAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = risk });
    }

    [HttpPut("{riskId:guid}")]
    public async Task<IActionResult> Update(
        Guid projectId, Guid riskId, UpdateRiskRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var risk = await svc.UpdateAsync(projectId, riskId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = risk });
    }

    [HttpPost("{riskId:guid}/close")]
    public async Task<IActionResult> Close(
        Guid projectId, Guid riskId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var risk = await svc.CloseAsync(projectId, riskId,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = risk });
    }

    [HttpPost("{riskId:guid}/assess")]
    public async Task<IActionResult> Assess(
        Guid projectId, Guid riskId,
        RecordQualitativeAssessmentRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var risk = await svc.RecordQualitativeAssessmentAsync(projectId, riskId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = risk });
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
        return Ok(new { success = true, data = await svc.RespondAsync(rfiId, projectId, req, CurrentUserId, role, ClientIp, ClientAgent) });
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
        return Ok(new { success = true, data = await svc.UpdateAsync(actionId, projectId, req, CurrentUserId, role, ClientIp, ClientAgent) });
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
