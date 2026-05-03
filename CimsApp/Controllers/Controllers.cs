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

    // T-S10-02. BSA 2022 HRB metadata. PM-only because the
    // categorisation has statutory weight; per-tenant inference
    // rules → v1.1 / B-072.
    [HttpPut("{projectId:guid}/hrb")]
    public async Task<IActionResult> SetHrb(Guid projectId, SetProjectHrbRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var p = await svc.SetHrbMetadataAsync(projectId, req.IsHrb, req.HrbCategory, CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = new { p.Id, p.IsHrb, HrbCategory = p.HrbCategory.ToString() } });
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

    [HttpPost("{riskId:guid}/quantify")]
    public async Task<IActionResult> Quantify(
        Guid projectId, Guid riskId,
        RecordQuantitativeAssessmentRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var risk = await svc.RecordQuantitativeAssessmentAsync(projectId, riskId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = risk });
    }

    [HttpGet("monte-carlo")]
    public async Task<IActionResult> MonteCarlo(
        Guid projectId,
        [FromQuery] int iterations = 10_000,
        [FromQuery] int? seed = null,
        CancellationToken ct = default)
    {
        // Membership-only: simulation is a read-side aggregation.
        await GetProjectRoleAsync(db, projectId);
        var result = await svc.RunMonteCarloAsync(projectId, iterations, seed, ct);
        return Ok(new { success = true, data = result });
    }

    [HttpPost("{riskId:guid}/drawdowns")]
    public async Task<IActionResult> RecordDrawdown(
        Guid projectId, Guid riskId,
        RecordRiskDrawdownRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var d = await svc.RecordDrawdownAsync(projectId, riskId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = d });
    }

    [HttpGet("{riskId:guid}/drawdowns")]
    public async Task<IActionResult> ListDrawdowns(
        Guid projectId, Guid riskId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var rows = await svc.ListDrawdownsAsync(projectId, riskId, ct);
        return Ok(new { success = true, data = rows });
    }
}

// ── Stakeholder & Communications ──────────────────────────────────────────────
// PAFM-SD F.4 (S3 module). T-S3-03 controller surface.
[Route("api/v1/projects/{projectId:guid}/stakeholders")]
public class StakeholdersController(StakeholdersService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var rows = await svc.ListAsync(projectId, ct);
        return Ok(new { success = true, data = rows });
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
        Guid projectId, CreateStakeholderRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var s = await svc.CreateAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = s });
    }

    [HttpPut("{stakeholderId:guid}")]
    public async Task<IActionResult> Update(
        Guid projectId, Guid stakeholderId,
        UpdateStakeholderRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var s = await svc.UpdateAsync(projectId, stakeholderId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = s });
    }

    [HttpPost("{stakeholderId:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(
        Guid projectId, Guid stakeholderId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var s = await svc.DeactivateAsync(projectId, stakeholderId,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = s });
    }

    // T-S3-06 engagement log: record + list per stakeholder.
    [HttpPost("{stakeholderId:guid}/engagements")]
    public async Task<IActionResult> RecordEngagement(
        Guid projectId, Guid stakeholderId,
        RecordEngagementRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var entry = await svc.RecordEngagementAsync(projectId, stakeholderId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = entry });
    }

    [HttpGet("{stakeholderId:guid}/engagements")]
    public async Task<IActionResult> ListEngagements(
        Guid projectId, Guid stakeholderId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var rows = await svc.ListEngagementsAsync(projectId, stakeholderId, ct);
        return Ok(new { success = true, data = rows });
    }
}

// PAFM-SD F.4 fourth bullet (T-S3-07). Project-level communications
// matrix — what / who / when / how.
[Route("api/v1/projects/{projectId:guid}/communications")]
public class CommunicationsController(CommunicationsService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var rows = await svc.ListAsync(projectId, ct);
        return Ok(new { success = true, data = rows });
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId, CreateCommunicationItemRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var item = await svc.CreateAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = item });
    }

    [HttpPut("{itemId:guid}")]
    public async Task<IActionResult> Update(
        Guid projectId, Guid itemId,
        UpdateCommunicationItemRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var item = await svc.UpdateAsync(projectId, itemId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = item });
    }

    [HttpPost("{itemId:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(
        Guid projectId, Guid itemId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var item = await svc.DeactivateAsync(projectId, itemId,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = item });
    }
}

// ── Schedule & Programme (T-S4-05) ────────────────────────────────────────────
// PAFM-SD F.5. Activity CRUD, dependency CRUD, and CPM recompute.
[Route("api/v1/projects/{projectId:guid}/schedule")]
public class ScheduleController(ScheduleService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet("activities")]
    public async Task<IActionResult> ListActivities(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var rows = await svc.ListActivitiesAsync(projectId, ct);
        return Ok(new { success = true, data = rows });
    }

    [HttpGet("activities/{activityId:guid}")]
    public async Task<IActionResult> GetActivity(
        Guid projectId, Guid activityId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var row = await svc.GetActivityAsync(projectId, activityId, ct);
        return Ok(new { success = true, data = row });
    }

    [HttpPost("activities")]
    public async Task<IActionResult> CreateActivity(
        Guid projectId, CreateActivityRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var act = await svc.CreateActivityAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = act });
    }

    [HttpPut("activities/{activityId:guid}")]
    public async Task<IActionResult> UpdateActivity(
        Guid projectId, Guid activityId,
        UpdateActivityRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var act = await svc.UpdateActivityAsync(projectId, activityId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = act });
    }

    [HttpPost("activities/{activityId:guid}/deactivate")]
    public async Task<IActionResult> DeactivateActivity(
        Guid projectId, Guid activityId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var act = await svc.DeactivateActivityAsync(projectId, activityId,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = act });
    }

    [HttpGet("dependencies")]
    public async Task<IActionResult> ListDependencies(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var rows = await svc.ListDependenciesAsync(projectId, ct);
        return Ok(new { success = true, data = rows });
    }

    [HttpPost("dependencies")]
    public async Task<IActionResult> AddDependency(
        Guid projectId, AddDependencyRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var dep = await svc.AddDependencyAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = dep });
    }

    [HttpDelete("dependencies/{dependencyId:guid}")]
    public async Task<IActionResult> RemoveDependency(
        Guid projectId, Guid dependencyId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        await svc.RemoveDependencyAsync(projectId, dependencyId,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true });
    }

    [HttpPost("recompute")]
    public async Task<IActionResult> Recompute(
        Guid projectId, RecomputeScheduleRequest? req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var result = await svc.RecomputeAsync(projectId, req?.DataDate,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = result });
    }

    // T-S4-06 baselines.
    [HttpGet("baselines")]
    public async Task<IActionResult> ListBaselines(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var rows = await svc.ListBaselinesAsync(projectId, ct);
        return Ok(new { success = true, data = rows });
    }

    [HttpPost("baselines")]
    public async Task<IActionResult> CreateBaseline(
        Guid projectId, CreateBaselineRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var b = await svc.CreateBaselineAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = b });
    }

    [HttpGet("baselines/{baselineId:guid}/comparison")]
    public async Task<IActionResult> BaselineComparison(
        Guid projectId, Guid baselineId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetBaselineComparisonAsync(projectId, baselineId, ct);
        return Ok(new { success = true, data = dto });
    }

    // T-S4-11 Gantt data endpoint. Read-only, membership.
    [HttpGet("gantt")]
    public async Task<IActionResult> Gantt(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetGanttAsync(projectId, ct);
        return Ok(new { success = true, data = dto });
    }

    // T-S4-09 MS Project XML import. Multipart `file`. Same shape
    // as T-S1-03 CBS import. Into-empty only.
    [HttpPost("import")]
    public async Task<IActionResult> ImportMsProject(
        Guid projectId, IFormFile file, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        if (file is null || file.Length == 0)
            throw new ValidationException(["file is required"]);
        await using var stream = file.OpenReadStream();
        var result = await svc.ImportFromMsProjectAsync(projectId, stream,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = result });
    }
}

// ── Last Planner System (T-S4-07) ─────────────────────────────────────────────
// PAFM-SD F.5 third bullet — Lookahead, Weekly Work Plan, PPC.
[Route("api/v1/projects/{projectId:guid}/schedule/lps")]
public class LpsController(LpsService svc, CimsDbContext db) : CimsControllerBase
{
    // ── Lookahead ──
    [HttpGet("lookahead")]
    public async Task<IActionResult> ListLookahead(
        Guid projectId, [FromQuery] DateTime? weekStarting, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var rows = await svc.ListLookaheadAsync(projectId, weekStarting, ct);
        return Ok(new { success = true, data = rows });
    }

    [HttpPost("lookahead")]
    public async Task<IActionResult> AddLookahead(
        Guid projectId, CreateLookaheadEntryRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var entry = await svc.AddLookaheadAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = entry });
    }

    [HttpPut("lookahead/{lookaheadId:guid}")]
    public async Task<IActionResult> UpdateLookahead(
        Guid projectId, Guid lookaheadId,
        UpdateLookaheadEntryRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var entry = await svc.UpdateLookaheadAsync(projectId, lookaheadId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = entry });
    }

    [HttpDelete("lookahead/{lookaheadId:guid}")]
    public async Task<IActionResult> RemoveLookahead(
        Guid projectId, Guid lookaheadId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        await svc.RemoveLookaheadAsync(projectId, lookaheadId,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true });
    }

    // ── Weekly Work Plans ──
    [HttpGet("weekly-plans")]
    public async Task<IActionResult> ListWeeklyPlans(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var rows = await svc.ListWeeklyWorkPlansAsync(projectId, ct);
        return Ok(new { success = true, data = rows });
    }

    [HttpPost("weekly-plans")]
    public async Task<IActionResult> CreateWeeklyPlan(
        Guid projectId, CreateWeeklyWorkPlanRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var wwp = await svc.CreateWeeklyWorkPlanAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = wwp });
    }

    [HttpGet("weekly-plans/{wwpId:guid}")]
    public async Task<IActionResult> GetWeeklyPlan(
        Guid projectId, Guid wwpId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetWeeklyWorkPlanAsync(projectId, wwpId, ct);
        return Ok(new { success = true, data = dto });
    }

    // ── Commitments ──
    [HttpPost("weekly-plans/{wwpId:guid}/commitments")]
    public async Task<IActionResult> AddCommitment(
        Guid projectId, Guid wwpId,
        AddCommitmentRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var c = await svc.AddCommitmentAsync(projectId, wwpId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = c });
    }

    [HttpPut("weekly-plans/{wwpId:guid}/commitments/{commitmentId:guid}")]
    public async Task<IActionResult> UpdateCommitment(
        Guid projectId, Guid wwpId, Guid commitmentId,
        UpdateCommitmentRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var c = await svc.UpdateCommitmentAsync(projectId, wwpId, commitmentId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = c });
    }

    [HttpDelete("weekly-plans/{wwpId:guid}/commitments/{commitmentId:guid}")]
    public async Task<IActionResult> RemoveCommitment(
        Guid projectId, Guid wwpId, Guid commitmentId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        await svc.RemoveCommitmentAsync(projectId, wwpId, commitmentId,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true });
    }
}

// ── Change Control (T-S5-05) ──────────────────────────────────────────────────
// PAFM-SD F.6. Formal change request workflow with 5 transitions.
[Route("api/v1/projects/{projectId:guid}/change-requests")]
public class ChangeRequestsController(ChangeRequestService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] string? state = null,
        [FromQuery] string? category = null,
        CancellationToken ct = default)
    {
        await GetProjectRoleAsync(db, projectId);
        ChangeRequestState? s = Enum.TryParse<ChangeRequestState>(state, true, out var ps) ? ps : null;
        ChangeRequestCategory? c = Enum.TryParse<ChangeRequestCategory>(category, true, out var pc) ? pc : null;
        var rows = await svc.ListAsync(projectId, s, c, ct);
        return Ok(new { success = true, data = rows });
    }

    [HttpGet("{changeRequestId:guid}")]
    public async Task<IActionResult> Get(
        Guid projectId, Guid changeRequestId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var row = await svc.GetAsync(projectId, changeRequestId, ct);
        return Ok(new { success = true, data = row });
    }

    [HttpPost]
    public async Task<IActionResult> Raise(
        Guid projectId, RaiseChangeRequestRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var c = await svc.RaiseAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = c });
    }

    [HttpPost("{changeRequestId:guid}/assess")]
    public async Task<IActionResult> Assess(
        Guid projectId, Guid changeRequestId,
        AssessChangeRequestRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var c = await svc.AssessAsync(projectId, changeRequestId, req,
            CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = c });
    }

    [HttpPost("{changeRequestId:guid}/approve")]
    public async Task<IActionResult> Approve(
        Guid projectId, Guid changeRequestId,
        ApproveChangeRequestRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var c = await svc.ApproveAsync(projectId, changeRequestId, req,
            CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = c });
    }

    [HttpPost("{changeRequestId:guid}/reject")]
    public async Task<IActionResult> Reject(
        Guid projectId, Guid changeRequestId,
        RejectChangeRequestRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var c = await svc.RejectAsync(projectId, changeRequestId, req,
            CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = c });
    }

    [HttpPost("{changeRequestId:guid}/implement")]
    public async Task<IActionResult> Implement(
        Guid projectId, Guid changeRequestId,
        ImplementChangeRequestRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var c = await svc.ImplementAsync(projectId, changeRequestId, req,
            CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = c });
    }

    [HttpPost("{changeRequestId:guid}/close")]
    public async Task<IActionResult> Close(
        Guid projectId, Guid changeRequestId,
        CloseChangeRequestRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var c = await svc.CloseAsync(projectId, changeRequestId, req,
            CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = c });
    }
}

// ── Procurement (T-S6-02) ─────────────────────────────────────────────────────
// PAFM-SD F.7. Project-level procurement strategy (single row per project).
[Route("api/v1/projects/{projectId:guid}/procurement/strategy")]
public class ProcurementStrategyController(ProcurementStrategyService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var s = await svc.GetAsync(projectId, ct);
        return Ok(new { success = true, data = s });
    }

    [HttpPut]
    public async Task<IActionResult> Upsert(
        Guid projectId, UpsertProcurementStrategyRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var s = await svc.CreateOrUpdateAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = s });
    }

    [HttpPost("approve")]
    public async Task<IActionResult> Approve(Guid projectId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var s = await svc.ApproveAsync(projectId,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = s });
    }
}

// PAFM-SD F.7 second bullet — Tender packages.
[Route("api/v1/projects/{projectId:guid}/procurement/tender-packages")]
public class TenderPackagesController(TenderPackagesService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId, [FromQuery] string? state = null, CancellationToken ct = default)
    {
        await GetProjectRoleAsync(db, projectId);
        TenderPackageState? s = Enum.TryParse<TenderPackageState>(state, true, out var p) ? p : null;
        var rows = await svc.ListAsync(projectId, s, ct);
        return Ok(new { success = true, data = rows });
    }

    [HttpGet("{tenderPackageId:guid}")]
    public async Task<IActionResult> Get(
        Guid projectId, Guid tenderPackageId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var t = await svc.GetAsync(projectId, tenderPackageId, ct);
        return Ok(new { success = true, data = t });
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId, CreateTenderPackageRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var t = await svc.CreateAsync(projectId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = t });
    }

    [HttpPut("{tenderPackageId:guid}")]
    public async Task<IActionResult> Update(
        Guid projectId, Guid tenderPackageId,
        UpdateTenderPackageRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var t = await svc.UpdateAsync(projectId, tenderPackageId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = t });
    }

    [HttpPost("{tenderPackageId:guid}/issue")]
    public async Task<IActionResult> Issue(
        Guid projectId, Guid tenderPackageId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var t = await svc.IssueAsync(projectId, tenderPackageId,
            CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = t });
    }

    [HttpPost("{tenderPackageId:guid}/close")]
    public async Task<IActionResult> Close(
        Guid projectId, Guid tenderPackageId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var t = await svc.CloseAsync(projectId, tenderPackageId,
            CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = t });
    }

    [HttpPost("{tenderPackageId:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(
        Guid projectId, Guid tenderPackageId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var t = await svc.DeactivateAsync(projectId, tenderPackageId,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = t });
    }

    [HttpPost("{tenderPackageId:guid}/award")]
    public async Task<IActionResult> Award(
        Guid projectId, Guid tenderPackageId,
        AwardTenderPackageRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var contract = await svc.AwardAsync(projectId, tenderPackageId, req,
            CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = contract });
    }
}

// PAFM-SD F.7 — Tenders (bids submitted against an Issued package).
[Route("api/v1/projects/{projectId:guid}/procurement/tender-packages/{tenderPackageId:guid}/tenders")]
public class TendersController(TendersService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId, Guid tenderPackageId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var rows = await svc.ListAsync(projectId, tenderPackageId, ct);
        return Ok(new { success = true, data = rows });
    }

    [HttpGet("{tenderId:guid}")]
    public async Task<IActionResult> Get(
        Guid projectId, Guid tenderPackageId, Guid tenderId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var t = await svc.GetAsync(projectId, tenderId, ct);
        return Ok(new { success = true, data = t });
    }

    [HttpPost]
    public async Task<IActionResult> Submit(
        Guid projectId, Guid tenderPackageId,
        SubmitTenderRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var t = await svc.SubmitAsync(projectId, tenderPackageId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = t });
    }

    [HttpPost("{tenderId:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(
        Guid projectId, Guid tenderPackageId, Guid tenderId,
        WithdrawTenderRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var t = await svc.WithdrawAsync(projectId, tenderId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = t });
    }
}

// PAFM-SD F.7 third bullet — evaluation matrix (criteria + scores).
[Route("api/v1/projects/{projectId:guid}/procurement/tender-packages/{tenderPackageId:guid}")]
public class EvaluationController(EvaluationService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet("evaluation-criteria")]
    public async Task<IActionResult> ListCriteria(
        Guid projectId, Guid tenderPackageId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var rows = await svc.ListCriteriaAsync(projectId, tenderPackageId, ct);
        return Ok(new { success = true, data = rows });
    }

    [HttpPost("evaluation-criteria")]
    public async Task<IActionResult> AddCriterion(
        Guid projectId, Guid tenderPackageId,
        AddEvaluationCriterionRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var c = await svc.AddCriterionAsync(projectId, tenderPackageId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = c });
    }

    [HttpPut("evaluation-criteria/{criterionId:guid}")]
    public async Task<IActionResult> UpdateCriterion(
        Guid projectId, Guid tenderPackageId, Guid criterionId,
        UpdateEvaluationCriterionRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var c = await svc.UpdateCriterionAsync(projectId, tenderPackageId, criterionId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = c });
    }

    [HttpDelete("evaluation-criteria/{criterionId:guid}")]
    public async Task<IActionResult> RemoveCriterion(
        Guid projectId, Guid tenderPackageId, Guid criterionId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        await svc.RemoveCriterionAsync(projectId, tenderPackageId, criterionId,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true });
    }

    [HttpPut("tenders/{tenderId:guid}/scores/{criterionId:guid}")]
    public async Task<IActionResult> SetScore(
        Guid projectId, Guid tenderPackageId, Guid tenderId, Guid criterionId,
        SetEvaluationScoreRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager))
            throw new ForbiddenException();
        var s = await svc.SetScoreAsync(projectId, tenderId, criterionId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = s });
    }

    [HttpGet("evaluation-matrix")]
    public async Task<IActionResult> GetMatrix(
        Guid projectId, Guid tenderPackageId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetMatrixAsync(projectId, tenderPackageId, ct);
        return Ok(new { success = true, data = dto });
    }
}

// PAFM-SD F.7 fifth bullet — early warnings (NEC4 clause 15).
[Route("api/v1/projects/{projectId:guid}/procurement/contracts/{contractId:guid}/early-warnings")]
public class EarlyWarningsController(EarlyWarningsService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId, Guid contractId,
        [FromQuery] string? state = null, CancellationToken ct = default)
    {
        await GetProjectRoleAsync(db, projectId);
        EarlyWarningState? s = Enum.TryParse<EarlyWarningState>(state, true, out var p) ? p : null;
        var rows = await svc.ListAsync(projectId, contractId, s, ct);
        return Ok(new { success = true, data = rows });
    }

    [HttpGet("{earlyWarningId:guid}")]
    public async Task<IActionResult> Get(
        Guid projectId, Guid contractId, Guid earlyWarningId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var w = await svc.GetAsync(projectId, earlyWarningId, ct);
        return Ok(new { success = true, data = w });
    }

    [HttpPost]
    public async Task<IActionResult> Raise(
        Guid projectId, Guid contractId,
        RaiseEarlyWarningRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var w = await svc.RaiseAsync(projectId, contractId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = w });
    }

    [HttpPost("{earlyWarningId:guid}/review")]
    public async Task<IActionResult> Review(
        Guid projectId, Guid contractId, Guid earlyWarningId,
        ReviewEarlyWarningRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager))
            throw new ForbiddenException();
        var w = await svc.ReviewAsync(projectId, earlyWarningId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = w });
    }

    [HttpPost("{earlyWarningId:guid}/close")]
    public async Task<IActionResult> Close(
        Guid projectId, Guid contractId, Guid earlyWarningId,
        CloseEarlyWarningRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var w = await svc.CloseAsync(projectId, earlyWarningId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = w });
    }
}

// PAFM-SD F.7 fifth bullet — compensation events (NEC4 clause 60.1).
[Route("api/v1/projects/{projectId:guid}/procurement/contracts/{contractId:guid}/compensation-events")]
public class CompensationEventsController(CompensationEventsService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId, Guid contractId,
        [FromQuery] string? state = null, CancellationToken ct = default)
    {
        await GetProjectRoleAsync(db, projectId);
        CompensationEventState? s = Enum.TryParse<CompensationEventState>(state, true, out var p) ? p : null;
        var rows = await svc.ListAsync(projectId, contractId, s, ct);
        return Ok(new { success = true, data = rows });
    }

    [HttpGet("{compensationEventId:guid}")]
    public async Task<IActionResult> Get(
        Guid projectId, Guid contractId, Guid compensationEventId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var ce = await svc.GetAsync(projectId, compensationEventId, ct);
        return Ok(new { success = true, data = ce });
    }

    [HttpPost]
    public async Task<IActionResult> Notify(
        Guid projectId, Guid contractId,
        NotifyCompensationEventRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var ce = await svc.NotifyAsync(projectId, contractId, req,
            CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = ce });
    }

    [HttpPost("{compensationEventId:guid}/quote")]
    public async Task<IActionResult> Quote(
        Guid projectId, Guid contractId, Guid compensationEventId,
        QuoteCompensationEventRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var ce = await svc.QuoteAsync(projectId, compensationEventId, req,
            CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = ce });
    }

    [HttpPost("{compensationEventId:guid}/accept")]
    public async Task<IActionResult> Accept(
        Guid projectId, Guid contractId, Guid compensationEventId,
        DecideCompensationEventRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var ce = await svc.AcceptAsync(projectId, compensationEventId, req,
            CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = ce });
    }

    [HttpPost("{compensationEventId:guid}/reject")]
    public async Task<IActionResult> Reject(
        Guid projectId, Guid contractId, Guid compensationEventId,
        DecideCompensationEventRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var ce = await svc.RejectAsync(projectId, compensationEventId, req,
            CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = ce });
    }

    [HttpPost("{compensationEventId:guid}/implement")]
    public async Task<IActionResult> Implement(
        Guid projectId, Guid contractId, Guid compensationEventId,
        ImplementCompensationEventRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var ce = await svc.ImplementAsync(projectId, compensationEventId, req,
            CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = ce });
    }
}

// ── Dashboards (T-S7-02) ──────────────────────────────────────────────────────
// PAFM-SD F.8 first bullet — six per-role dashboards.
[Route("api/v1/projects/{projectId:guid}/dashboards")]
public class DashboardsController(DashboardsService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet("pm")]
    public async Task<IActionResult> Pm(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetPmDashboardAsync(projectId, ct);
        return Ok(new { success = true, data = dto });
    }

    [HttpGet("cm")]
    public async Task<IActionResult> Cm(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetCmDashboardAsync(projectId, ct);
        return Ok(new { success = true, data = dto });
    }

    [HttpGet("sm")]
    public async Task<IActionResult> Sm(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetSmDashboardAsync(projectId, ct);
        return Ok(new { success = true, data = dto });
    }

    [HttpGet("im")]
    public async Task<IActionResult> Im(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetImDashboardAsync(projectId, ct);
        return Ok(new { success = true, data = dto });
    }

    [HttpGet("hse")]
    public async Task<IActionResult> Hse(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetHseDashboardAsync(projectId, ct);
        return Ok(new { success = true, data = dto });
    }

    [HttpGet("client")]
    public async Task<IActionResult> Client(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetClientDashboardAsync(projectId, ct);
        return Ok(new { success = true, data = dto });
    }
}

// ── Reporting / MPR (T-S7-03) ─────────────────────────────────────────────────
// PAFM-SD F.8 second bullet — Monthly Project Report. v1.0 returns
// JSON only; PDF rendering deferred to v1.1 / B-055.
[Route("api/v1/projects/{projectId:guid}/reports")]
public class ReportingController(ReportingService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet("mpr")]
    public async Task<IActionResult> Mpr(
        Guid projectId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GenerateMonthlyProjectReportAsync(projectId, from, to, ct);
        return Ok(new { success = true, data = dto });
    }

    [HttpGet("kpi")]
    public async Task<IActionResult> Kpi(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        var dto = await svc.GetProjectKpiCardsAsync(projectId, ct);
        return Ok(new { success = true, data = dto });
    }
}

// ── UK GDPR ROPA (T-S11-02) ──────────────────────────────────────────────────
// PAFM-SD F.11 first bullet — UK GDPR Art. 30. Org-scoped.
[Route("api/v1/gdpr/ropa")]
public class RopaController(RopaService svc) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(new { success = true, data = await svc.ListAsync(ct) });

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.GetAsync(id, ct) });

    [HttpPost]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Create(CreateRopaEntryRequest req, CancellationToken ct) =>
        Created("", new { success = true, data = await svc.CreateAsync(req, CurrentUserId, ClientIp, ClientAgent, ct) });

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Update(Guid id, UpdateRopaEntryRequest req, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.UpdateAsync(id, req, CurrentUserId, ClientIp, ClientAgent, ct) });

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    { await svc.DeleteAsync(id, CurrentUserId, ClientIp, ClientAgent, ct); return NoContent(); }
}

// ── UK GDPR DPIA (T-S11-03) ──────────────────────────────────────────────────
// PAFM-SD F.11 second bullet — UK GDPR Art. 35. Project-scoped.
[Route("api/v1/projects/{projectId:guid}/gdpr/dpias")]
public class DpiaController(DpiaService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    { await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.ListAsync(projectId, ct) }); }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid id, CancellationToken ct)
    { await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.GetAsync(projectId, id, ct) }); }

    [HttpPost]
    public async Task<IActionResult> Create(Guid projectId, CreateDpiaRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember)) throw new ForbiddenException();
        return Created("", new { success = true, data = await svc.CreateAsync(projectId, req, CurrentUserId, ClientIp, ClientAgent, ct) });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid projectId, Guid id, UpdateDpiaRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember)) throw new ForbiddenException();
        return Ok(new { success = true, data = await svc.UpdateAsync(projectId, id, req, CurrentUserId, ClientIp, ClientAgent, ct) });
    }

    [HttpPost("{id:guid}/submit")]
    public async Task<IActionResult> Submit(Guid projectId, Guid id, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.TransitionAsync(projectId, id, DpiaState.UnderReview, decisionNote: null, CurrentUserId, role, ClientIp, ClientAgent, ct) });
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid projectId, Guid id, DpiaDecisionRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.TransitionAsync(projectId, id, DpiaState.Approved, req.DecisionNote, CurrentUserId, role, ClientIp, ClientAgent, ct) });
    }

    [HttpPost("{id:guid}/require-changes")]
    public async Task<IActionResult> RequireChanges(Guid projectId, Guid id, DpiaDecisionRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.TransitionAsync(projectId, id, DpiaState.RequiresChanges, req.DecisionNote, CurrentUserId, role, ClientIp, ClientAgent, ct) });
    }

    [HttpPost("{id:guid}/return-to-drafting")]
    public async Task<IActionResult> ReturnToDrafting(Guid projectId, Guid id, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.TransitionAsync(projectId, id, DpiaState.Drafting, decisionNote: null, CurrentUserId, role, ClientIp, ClientAgent, ct) });
    }
}

// ── UK GDPR SAR (T-S11-04) ───────────────────────────────────────────────────
// PAFM-SD F.11 third bullet — UK GDPR Art. 12, 15. Org-scoped.
[Route("api/v1/gdpr/sars")]
public class SarController(SarService svc) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(new { success = true, data = await svc.ListAsync(ct) });

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.GetAsync(id, ct) });

    [HttpPost]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Create(CreateSarRequest req, CancellationToken ct) =>
        Created("", new { success = true, data = await svc.CreateAsync(req, CurrentUserId, ClientIp, ClientAgent, ct) });

    [HttpPost("{id:guid}/start")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Start(Guid id, StartSarFulfilmentRequest req, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.StartFulfilmentAsync(id, req, CurrentUserId, ClientIp, ClientAgent, ct) });

    [HttpPost("{id:guid}/fulfil")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Fulfil(Guid id, FulfilSarRequest req, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.FulfilAsync(id, req, CurrentUserId, ClientIp, ClientAgent, ct) });

    [HttpPost("{id:guid}/refuse")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Refuse(Guid id, RefuseSarRequest req, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.RefuseAsync(id, req, CurrentUserId, ClientIp, ClientAgent, ct) });
}

// ── UK GDPR Data Breach Log (T-S11-05) ───────────────────────────────────────
// PAFM-SD F.11 fourth bullet — UK GDPR Art. 33-34. Org-scoped.
[Route("api/v1/gdpr/data-breaches")]
public class DataBreachController(DataBreachService svc) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(new { success = true, data = await svc.ListAsync(ct) });

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.GetAsync(id, ct) });

    [HttpPost]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Create(CreateBreachRequest req, CancellationToken ct) =>
        Created("", new { success = true, data = await svc.CreateAsync(req, CurrentUserId, ClientIp, ClientAgent, ct) });

    [HttpPost("{id:guid}/mark-reported-to-ico")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> MarkReportedToIco(Guid id, MarkBreachReportedToIcoRequest req, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.MarkReportedToIcoAsync(id, req, CurrentUserId, ClientIp, ClientAgent, ct) });

    [HttpPost("{id:guid}/mark-subjects-notified")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> MarkSubjectsNotified(Guid id, MarkBreachNotifiedDataSubjectsRequest req, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.MarkNotifiedDataSubjectsAsync(id, CurrentUserId, ClientIp, ClientAgent, ct) });
}

// ── UK GDPR Retention Schedules (T-S11-06) ───────────────────────────────────
// PAFM-SD F.11 fifth bullet — UK GDPR Art. 5(1)(e). Org-scoped.
[Route("api/v1/gdpr/retention-schedules")]
public class RetentionSchedulesController(RetentionScheduleService svc) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(new { success = true, data = await svc.ListAsync(ct) });

    [HttpPost]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Create(CreateRetentionScheduleRequest req, CancellationToken ct) =>
        Created("", new { success = true, data = await svc.CreateAsync(req, CurrentUserId, ClientIp, ClientAgent, ct) });

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Update(Guid id, UpdateRetentionScheduleRequest req, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.UpdateAsync(id, req, CurrentUserId, ClientIp, ClientAgent, ct) });

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    { await svc.DeleteAsync(id, CurrentUserId, ClientIp, ClientAgent, ct); return NoContent(); }
}

// ── Improvement Register (T-S12-02) ──────────────────────────────────────────
// PAFM-SD F.12 first bullet — PDCA continuous improvement.
[Route("api/v1/projects/{projectId:guid}/improvements")]
public class ImprovementRegisterController(ImprovementRegisterService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    { await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.ListAsync(projectId, ct) }); }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid id, CancellationToken ct)
    { await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.GetAsync(projectId, id, ct) }); }

    [HttpPost]
    public async Task<IActionResult> Create(Guid projectId, CreateImprovementRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember)) throw new ForbiddenException();
        return Created("", new { success = true, data = await svc.CreateAsync(projectId, req, CurrentUserId, ClientIp, ClientAgent, ct) });
    }

    [HttpPost("{id:guid}/transition/do")]
    public async Task<IActionResult> ToDo(Guid projectId, Guid id, TransitionImprovementRequest req, CancellationToken ct)
    { var role = await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.TransitionAsync(projectId, id, PdcaState.Do, req.StageNotes, CurrentUserId, role, ClientIp, ClientAgent, ct) }); }

    [HttpPost("{id:guid}/transition/check")]
    public async Task<IActionResult> ToCheck(Guid projectId, Guid id, TransitionImprovementRequest req, CancellationToken ct)
    { var role = await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.TransitionAsync(projectId, id, PdcaState.Check, req.StageNotes, CurrentUserId, role, ClientIp, ClientAgent, ct) }); }

    [HttpPost("{id:guid}/transition/act")]
    public async Task<IActionResult> ToAct(Guid projectId, Guid id, TransitionImprovementRequest req, CancellationToken ct)
    { var role = await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.TransitionAsync(projectId, id, PdcaState.Act, req.StageNotes, CurrentUserId, role, ClientIp, ClientAgent, ct) }); }

    [HttpPost("{id:guid}/transition/cycle-back-to-plan")]
    public async Task<IActionResult> CycleBackToPlan(Guid projectId, Guid id, TransitionImprovementRequest req, CancellationToken ct)
    { var role = await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.TransitionAsync(projectId, id, PdcaState.Plan, req.StageNotes, CurrentUserId, role, ClientIp, ClientAgent, ct) }); }

    [HttpPost("{id:guid}/transition/close")]
    public async Task<IActionResult> Close(Guid projectId, Guid id, TransitionImprovementRequest req, CancellationToken ct)
    { var role = await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.TransitionAsync(projectId, id, PdcaState.Closed, req.StageNotes, CurrentUserId, role, ClientIp, ClientAgent, ct) }); }
}

// ── Lessons Learned Library (T-S12-03) ───────────────────────────────────────
// PAFM-SD F.12 second bullet — org-scoped cross-project library.
[Route("api/v1/lessons-learned")]
public class LessonsLearnedController(LessonsLearnedService svc) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? category, [FromQuery] string? tag, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.ListAsync(category, tag, ct) });

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.GetAsync(id, ct) });

    [HttpPost]
    public async Task<IActionResult> Create(CreateLessonLearnedRequest req, CancellationToken ct) =>
        Created("", new { success = true, data = await svc.CreateAsync(req, CurrentUserId, ClientIp, ClientAgent, ct) });

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateLessonLearnedRequest req, CancellationToken ct) =>
        Ok(new { success = true, data = await svc.UpdateAsync(id, req, CurrentUserId, ClientIp, ClientAgent, ct) });

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "OrgAdmin,SuperAdmin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    { await svc.DeleteAsync(id, CurrentUserId, ClientIp, ClientAgent, ct); return NoContent(); }
}

// ── Opportunity to Improve (T-S12-04) ────────────────────────────────────────
// PAFM-SD F.12 third bullet — linked from any module.
[Route("api/v1/projects/{projectId:guid}/opportunities-to-improve")]
public class OpportunityToImproveController(OpportunityToImproveService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, [FromQuery] bool? actioned, CancellationToken ct)
    { await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.ListAsync(projectId, actioned, ct) }); }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid id, CancellationToken ct)
    { await GetProjectRoleAsync(db, projectId); return Ok(new { success = true, data = await svc.GetAsync(projectId, id, ct) }); }

    [HttpPost]
    public async Task<IActionResult> Create(Guid projectId, CreateOpportunityToImproveRequest req, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Created("", new { success = true, data = await svc.CreateAsync(projectId, req, CurrentUserId, ClientIp, ClientAgent, ct) });
    }

    [HttpPost("{id:guid}/action")]
    public async Task<IActionResult> Action(Guid projectId, Guid id, ActionOpportunityToImproveRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember)) throw new ForbiddenException();
        return Ok(new { success = true, data = await svc.ActionAsync(projectId, id, req, CurrentUserId, ClientIp, ClientAgent, ct) });
    }
}

// ── BSA 2022 Gateway packages (T-S10-03) ─────────────────────────────────────
// PAFM-SD F.10 second bullet — Gateway 1/2/3 statutory submissions.
[Route("api/v1/projects/{projectId:guid}/gateway-packages")]
public class GatewayPackagesController(GatewayPackageService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.ListAsync(projectId, ct) });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid id, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.GetAsync(projectId, id, ct) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid projectId, CreateGatewayPackageRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager))
            throw new ForbiddenException();
        var dto = await svc.CreateAsync(projectId, req, CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = dto });
    }

    [HttpPost("{id:guid}/submit")]
    public async Task<IActionResult> Submit(Guid projectId, Guid id, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var dto = await svc.SubmitAsync(projectId, id, CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = dto });
    }

    [HttpPost("{id:guid}/decide")]
    public async Task<IActionResult> Decide(Guid projectId, Guid id, DecideGatewayPackageRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        var dto = await svc.DecideAsync(projectId, id, req, CurrentUserId, role, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = dto });
    }
}

// ── BSA 2022 Mandatory Occurrence Reports (T-S10-04) ─────────────────────────
// PAFM-SD F.10 third bullet — BSA 2022 s.87.
[Route("api/v1/projects/{projectId:guid}/mandatory-occurrence-reports")]
public class MorController(MorService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.ListAsync(projectId, ct) });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid id, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.GetAsync(projectId, id, ct) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid projectId, CreateMorRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var dto = await svc.CreateAsync(projectId, req, CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = dto });
    }

    [HttpPost("{id:guid}/mark-reported")]
    public async Task<IActionResult> MarkReported(Guid projectId, Guid id, MarkMorReportedToBsrRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var dto = await svc.MarkReportedToBsrAsync(projectId, id, req, CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = dto });
    }
}

// ── BSA 2022 Safety Case Summary (T-S10-05) ──────────────────────────────────
// PAFM-SD F.10 fourth bullet — JSON aggregator.
[Route("api/v1/projects/{projectId:guid}/safety-case")]
public class SafetyCaseController(SafetyCaseService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.GenerateAsync(projectId, ct) });
    }
}

// ── BSA 2022 Golden Thread (T-S10-06) ────────────────────────────────────────
// PAFM-SD F.10 fifth bullet — soft immutability + listing endpoint.
[Route("api/v1/projects/{projectId:guid}/golden-thread")]
public class GoldenThreadController(DocumentsService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.ListGoldenThreadAsync(projectId, ct) });
    }

    [HttpPost("documents/{documentId:guid}")]
    public async Task<IActionResult> Add(Guid projectId, Guid documentId, AddDocumentToGoldenThreadRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        var doc = await svc.AddToGoldenThreadAsync(documentId, projectId, CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = new { doc.Id, doc.DocumentNumber, doc.IsInGoldenThread, doc.AddedToGoldenThreadAt } });
    }
}

// ── MIDP — Master Information Delivery Plan (T-S9-05) ────────────────────────
// PAFM-SD F.9 second bullet — ISO 19650-2 §5.4. Per-project list of
// planned information deliveries.
[Route("api/v1/projects/{projectId:guid}/midp/entries")]
public class MidpController(MidpService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.ListAsync(projectId, ct) });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid id, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.GetAsync(projectId, id, ct) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid projectId, CreateMidpEntryRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager))
            throw new ForbiddenException();
        var dto = await svc.CreateAsync(projectId, req, CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = dto });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid projectId, Guid id, UpdateMidpEntryRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager))
            throw new ForbiddenException();
        return Ok(new { success = true, data = await svc.UpdateAsync(projectId, id, req, CurrentUserId, ClientIp, ClientAgent, ct) });
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid projectId, Guid id, CompleteMidpEntryRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager))
            throw new ForbiddenException();
        return Ok(new { success = true, data = await svc.CompleteAsync(projectId, id, req, CurrentUserId, ClientIp, ClientAgent, ct) });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid id, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        await svc.DeleteAsync(projectId, id, CurrentUserId, ClientIp, ClientAgent, ct);
        return NoContent();
    }
}

// ── TIDP — Task Information Delivery Plan (T-S9-06) ──────────────────────────
// PAFM-SD F.9 third bullet — per-team slice of an MIDP entry.
[Route("api/v1/projects/{projectId:guid}/tidp/entries")]
public class TidpController(TidpService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        Guid projectId, [FromQuery] Guid? midpEntryId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.ListAsync(projectId, midpEntryId, ct) });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid id, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.GetAsync(projectId, id, ct) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid projectId, CreateTidpEntryRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager))
            throw new ForbiddenException();
        var dto = await svc.CreateAsync(projectId, req, CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = dto });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid projectId, Guid id, UpdateTidpEntryRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager))
            throw new ForbiddenException();
        return Ok(new { success = true, data = await svc.UpdateAsync(projectId, id, req, CurrentUserId, ClientIp, ClientAgent, ct) });
    }

    [HttpPost("{id:guid}/sign-off")]
    public async Task<IActionResult> SignOff(Guid projectId, Guid id, SignOffTidpEntryRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        return Ok(new { success = true, data = await svc.SignOffAsync(projectId, id, req, CurrentUserId, ClientIp, ClientAgent, ct) });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid id, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException();
        await svc.DeleteAsync(projectId, id, CurrentUserId, ClientIp, ClientAgent, ct);
        return NoContent();
    }
}

// ── Custom Report Definitions (T-S7-05) ───────────────────────────────────────
// PAFM-SD F.8 fourth bullet — basic custom report builder. Saved
// queries persisted per project. CRUD gated `TaskTeamMember+`;
// list / get / run gated to project membership.
[Route("api/v1/projects/{projectId:guid}/custom-reports")]
public class CustomReportDefinitionsController(
    CustomReportDefinitionsService svc, CimsDbContext db) : CimsControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.ListAsync(projectId, ct) });
    }

    [HttpGet("{definitionId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid definitionId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.GetAsync(projectId, definitionId, ct) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId, CreateCustomReportDefinitionRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var dto = await svc.CreateAsync(projectId, req, CurrentUserId, ClientIp, ClientAgent, ct);
        return Created("", new { success = true, data = dto });
    }

    [HttpPut("{definitionId:guid}")]
    public async Task<IActionResult> Update(
        Guid projectId, Guid definitionId,
        UpdateCustomReportDefinitionRequest req, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        var dto = await svc.UpdateAsync(projectId, definitionId, req, CurrentUserId, ClientIp, ClientAgent, ct);
        return Ok(new { success = true, data = dto });
    }

    [HttpDelete("{definitionId:guid}")]
    public async Task<IActionResult> Delete(
        Guid projectId, Guid definitionId, CancellationToken ct)
    {
        var role = await GetProjectRoleAsync(db, projectId);
        if (!CdeStateMachine.HasMinimumRole(role, UserRole.TaskTeamMember))
            throw new ForbiddenException();
        await svc.DeleteAsync(projectId, definitionId, CurrentUserId, ClientIp, ClientAgent, ct);
        return NoContent();
    }

    [HttpPost("{definitionId:guid}/run")]
    public async Task<IActionResult> Run(
        Guid projectId, Guid definitionId, CancellationToken ct)
    {
        await GetProjectRoleAsync(db, projectId);
        return Ok(new { success = true, data = await svc.RunAsync(projectId, definitionId, ct) });
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
