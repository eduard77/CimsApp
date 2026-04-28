using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;

namespace CimsApp.Services;

// ── Auth ──────────────────────────────────────────────────────────────────────
public class AuthService(
    CimsDbContext db,
    IConfiguration cfg,
    InvitationService invitations,
    CimsApp.Services.Auth.ILoginAttemptTracker loginTracker)
{

    private string AccessSecret  => cfg["Jwt:AccessSecret"]!;
    private string RefreshSecret => cfg["Jwt:RefreshSecret"]!;
    private string Issuer        => cfg["Jwt:Issuer"]!;
    private string Audience      => cfg["Jwt:Audience"]!;
    private int    AccessMinutes => int.Parse(cfg["Jwt:AccessExpiresMinutes"] ?? "60");
    private int    RefreshDays   => int.Parse(cfg["Jwt:RefreshExpiresDays"]   ?? "7");

    public async Task<UserSummaryDto> RegisterAsync(RegisterRequest req)
    {
        // Defensive null/empty guard on the request shape. Same fix-class
        // as LoginAsync — without it, downstream code (`.Trim()` in
        // InvitationService.ValidateAsync, `.ToLowerInvariant()` in the
        // duplicate-email check) throws NRE and the operator sees a 500
        // instead of the 400 / 409 the API contract promises.
        if (string.IsNullOrEmpty(req.Email) || string.IsNullOrEmpty(req.Password)
            || string.IsNullOrEmpty(req.FirstName) || string.IsNullOrEmpty(req.LastName))
            throw new ValidationException(["Email, Password, FirstName, LastName are required"]);

        // Closes SR-S0-01: tenant ownership of the new User is no longer
        // attacker-supplied. The invitation token determines OrganisationId
        // and (for bootstrap tokens minted at org-creation time) the
        // initial GlobalRole.
        var invitation = await invitations.ValidateAsync(req.InvitationToken, req.Email);

        // Pre-auth: ignore tenant filter while checking email uniqueness.
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == req.Email))
            throw new ConflictException("Email already registered");

        var org = await db.Organisations.FindAsync(invitation.OrganisationId)
            ?? throw new NotFoundException("Organisation");

        var user = new User
        {
            Email          = req.Email.ToLowerInvariant(),
            PasswordHash   = BCrypt.Net.BCrypt.HashPassword(req.Password),
            FirstName      = req.FirstName,
            LastName       = req.LastName,
            JobTitle       = req.JobTitle,
            OrganisationId = invitation.OrganisationId,
            // Bootstrap tokens are issued by anonymous org-creation and the
            // first registrant becomes the org's first OrgAdmin so they can
            // mint further invitations. Non-bootstrap tokens never elevate.
            GlobalRole     = invitation.IsBootstrap ? UserRole.OrgAdmin : null,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Mark consumed only after the User row actually persisted, so a
        // failed save (FK violation, duplicate index) leaves the invitation
        // available for retry rather than burning it.
        await invitations.MarkConsumedAsync(invitation.Id, user.Id);

        return Map(user, org);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest req, string? ua, string? ip)
    {
        // Defensive null/empty guard on the request shape. Without it,
        // the LINQ expression below evaluates `req.Email.ToLowerInvariant()`
        // as a query parameter inline and a null Email throws a
        // NullReferenceException deep inside EF's expression interpreter
        // — the operator sees a 500 instead of the 401 the API contract
        // promises for any malformed credential payload.
        if (string.IsNullOrEmpty(req.Email) || string.IsNullOrEmpty(req.Password))
            throw new AppException("Invalid credentials", 401, "INVALID_CREDENTIALS");

        // B-002 progressive back-off: short-circuit BEFORE any DB work
        // when the caller's IP is locked out. Tighter than the
        // anon-login rate limiter (5/min hard cap) — locks out for the
        // remaining 15-minute sliding window after 5 consecutive
        // failures from this IP. A successful login resets the
        // counter at the bottom of this method.
        if (!string.IsNullOrEmpty(ip) && loginTracker.IsLockedOut(ip))
            throw new AppException(
                "Too many failed login attempts. Try again later.",
                429, "LOGIN_LOCKOUT");

        try
        {
            // Pre-auth: tenant is not yet known. Bypass the User query filter.
            var user = await db.Users.IgnoreQueryFilters().Include(u => u.Organisation)
                .FirstOrDefaultAsync(u => u.Email == req.Email.ToLowerInvariant() && u.IsActive)
                ?? throw new AppException("Invalid credentials", 401, "INVALID_CREDENTIALS");
            if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                throw new AppException("Invalid credentials", 401, "INVALID_CREDENTIALS");
            user.LastLoginAt = DateTime.UtcNow;
            var access  = GenerateAccess(user);
            var refresh = await CreateRefreshAsync(user.Id, ua, ip);
            await db.SaveChangesAsync();
            if (!string.IsNullOrEmpty(ip)) loginTracker.RecordSuccess(ip);
            return new AuthResponse(access, refresh.Token, Map(user, user.Organisation));
        }
        catch (AppException ex) when (ex.StatusCode == 401)
        {
            // Only credential-class failures count toward lockout.
            // Validation/server errors don't tighten the limiter
            // (operator typos shouldn't help an attacker accelerate
            // a discovery they wouldn't otherwise get).
            if (!string.IsNullOrEmpty(ip)) loginTracker.RecordFailure(ip);
            throw;
        }
    }

    public async Task<(string Access, string Refresh)> RefreshAsync(string token)
    {
        var principal = Validate(token, RefreshSecret) ?? throw new AppException("Invalid refresh token", 401, "INVALID_REFRESH");
        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        // Pre-auth: refresh endpoint has no access token, tenant filter bypassed.
        var stored = await db.RefreshTokens.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Token == token);
        if (stored == null || !stored.IsActive) throw new AppException("Token revoked", 401, "TOKEN_REVOKED");
        stored.RevokedAt = DateTime.UtcNow;
        // Pre-auth (refresh endpoint has no access token): bypass User filter.
        var user   = await db.Users.IgnoreQueryFilters().Include(u => u.Organisation).FirstAsync(u => u.Id == userId);
        var access = GenerateAccess(user);
        var newRef = await CreateRefreshAsync(userId, null, null);
        await db.SaveChangesAsync();
        return (access, newRef.Token);
    }

    public async Task LogoutAsync(string token)
    {
        // Logout accepts an opaque token without tenant context guarantees
        // (token may outlive its access JWT). Bypass the filter here too.
        var stored = await db.RefreshTokens.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Token == token);
        if (stored != null) { stored.RevokedAt = DateTime.UtcNow; await db.SaveChangesAsync(); }
    }

    public async Task<User> GetUserAsync(Guid id) =>
        await db.Users.Include(u => u.Organisation).FirstOrDefaultAsync(u => u.Id == id && u.IsActive)
        ?? throw new NotFoundException("User");

    /// <summary>
    /// B-001: self-service revoke. Bumps the caller's own
    /// <see cref="User.TokenInvalidationCutoff"/> to UtcNow so all
    /// other access tokens minted before this moment are rejected by
    /// the JwtBearer `OnTokenValidated` hook. The caller's CURRENT
    /// access token is also invalidated — they will need to log in
    /// again. By design: "log out everywhere" includes "this device".
    /// </summary>
    public async Task RevokeOwnTokensAsync(Guid actorId)
    {
        // IgnoreQueryFilters because the caller IS the target — there
        // is no tenant-scope concern when revoking your own sessions.
        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == actorId)
            ?? throw new NotFoundException("User");
        user.TokenInvalidationCutoff = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// B-001 / ADR-0014: admin revoke. Bumps the target user's
    /// <see cref="User.TokenInvalidationCutoff"/>. The lookup respects
    /// the tenant query filter — OrgAdmin can only target users in
    /// their own organisation; SuperAdmin bypasses the filter via
    /// IgnoreQueryFilters per ADR-0007. Cross-tenant attempts 404.
    /// </summary>
    public async Task RevokeUserTokensAsync(Guid userId, CimsApp.Services.Tenancy.ITenantContext tenant)
    {
        var query = tenant.IsSuperAdmin
            ? db.Users.IgnoreQueryFilters()
            : db.Users.AsQueryable();
        var user = await query.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new NotFoundException("User");
        user.TokenInvalidationCutoff = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// B-001 / ADR-0014: deactivate a user AND revoke their tokens
    /// atomically. Sets `IsActive = false` (which the
    /// `TokenRevocation.IsRevoked` helper checks first — independent
    /// of cutoff, so an inactive user's JWT is rejected regardless
    /// of timing) and bumps the cutoff (belt-and-braces; also covers
    /// any future code path that re-activates a user but should still
    /// reject tokens issued during the inactive window).
    /// </summary>
    public async Task DeactivateUserAsync(Guid userId, CimsApp.Services.Tenancy.ITenantContext tenant)
    {
        var query = tenant.IsSuperAdmin
            ? db.Users.IgnoreQueryFilters()
            : db.Users.AsQueryable();
        var user = await query.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new NotFoundException("User");
        user.IsActive = false;
        user.TokenInvalidationCutoff = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private string GenerateAccess(User user)
    {
        var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AccessSecret));
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(CimsApp.Services.Tenancy.HttpTenantContext.OrganisationClaimType, user.OrganisationId.ToString()),
        };
        if (user.GlobalRole is { } role)
            claims.Add(new Claim(CimsApp.Services.Tenancy.HttpTenantContext.GlobalRoleClaimType, role.ToString()));
        var token = new JwtSecurityToken(Issuer, Audience, claims,
            expires: DateTime.UtcNow.AddMinutes(AccessMinutes),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<RefreshToken> CreateRefreshAsync(Guid userId, string? ua, string? ip)
    {
        var t = new RefreshToken { Token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), UserId = userId, ExpiresAt = DateTime.UtcNow.AddDays(RefreshDays), UserAgent = ua, IpAddress = ip };
        db.RefreshTokens.Add(t);
        return t;
    }

    private ClaimsPrincipal? Validate(string token, string secret)
    {
        try { return new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters { ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), ValidateIssuer = true, ValidIssuer = Issuer, ValidateAudience = true, ValidAudience = Audience, ValidateLifetime = true, ClockSkew = TimeSpan.Zero }, out _); }
        catch { return null; }
    }

    private static UserSummaryDto Map(User u, Organisation o) =>
        new(u.Id, u.Email, u.FirstName, u.LastName, u.JobTitle, new OrgSummaryDto(o.Id, o.Name, o.Code));
}

// ── Invitations ───────────────────────────────────────────────────────────────
// Single-use tokens binding a registering user to a specific organisation.
// See ADR-0011 (planned) and CR-002. Closes SR-S0-01 by removing the
// attacker-supplied OrganisationId from the anonymous register flow.
public class InvitationService(CimsDbContext db)
{
    public sealed record CreateResult(Guid Id, string Token, DateTime ExpiresAt);

    /// <summary>
    /// Mint an invitation. Returns the plaintext token once — only the
    /// SHA-256 hash is persisted, so a lost token cannot be recovered.
    /// </summary>
    public async Task<CreateResult> CreateAsync(
        Guid organisationId,
        Guid? createdById,
        string? emailBind,
        int expiresInDays,
        bool isBootstrap = false)
    {
        if (expiresInDays < 1) expiresInDays = 1;
        if (expiresInDays > 30) expiresInDays = 30;
        // Bootstrap tokens shorten to 24h regardless — they are issued
        // anonymously at organisation-creation time and a longer window
        // would widen the unauthenticated attack surface unnecessarily.
        if (isBootstrap) expiresInDays = 1;

        // 32 bytes of cryptographic randomness, base64url-encoded.
        var bytes = RandomNumberGenerator.GetBytes(32);
        var plaintext = Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var invitation = new Invitation
        {
            OrganisationId = organisationId,
            TokenHash      = HashToken(plaintext),
            Email          = string.IsNullOrWhiteSpace(emailBind) ? null : emailBind.Trim().ToLowerInvariant(),
            IsBootstrap    = isBootstrap,
            ExpiresAt      = DateTime.UtcNow.AddDays(expiresInDays),
            CreatedById    = createdById,
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync();
        return new CreateResult(invitation.Id, plaintext, invitation.ExpiresAt);
    }

    /// <summary>
    /// Validate a plaintext token and return the matching Invitation. Does
    /// NOT mark consumed — caller marks consumption via MarkConsumedAsync
    /// after the dependent User row is persisted, so we never end up with
    /// a consumed invitation pointing at a User that failed to save.
    /// </summary>
    public async Task<Invitation> ValidateAsync(string plaintextToken, string registeringEmail)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
            throw new ValidationException(["InvitationToken is required"]);

        var hash = HashToken(plaintextToken);
        // Pre-auth: tenant context is null at register-time. Same pattern
        // as AuthService.LoginAsync uses for User/RefreshToken lookups.
        var inv = await db.Invitations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TokenHash == hash)
            ?? throw new NotFoundException("Invitation");

        if (inv.ConsumedAt != null)
            throw new ValidationException(["Invitation has already been used"]);
        if (inv.ExpiresAt < DateTime.UtcNow)
            throw new ValidationException(["Invitation has expired"]);
        if (!string.IsNullOrEmpty(inv.Email)
            && !string.Equals(inv.Email, registeringEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ValidationException(["Invitation is bound to a different email address"]);

        return inv;
    }

    /// <summary>
    /// Atomically mark an invitation consumed. Uses an Update-where on the
    /// non-null ConsumedAt column so a race between two concurrent
    /// register calls cannot double-consume the same token.
    /// </summary>
    public async Task<bool> MarkConsumedAsync(Guid invitationId, Guid consumerUserId)
    {
        var rows = await db.Invitations.IgnoreQueryFilters()
            .Where(i => i.Id == invitationId && i.ConsumedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(i => i.ConsumedAt, DateTime.UtcNow)
                .SetProperty(i => i.ConsumedByUserId, consumerUserId));
        return rows == 1;
    }

    private static string HashToken(string plaintext)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexString(bytes);
    }
}

// ── Projects ──────────────────────────────────────────────────────────────────
public class ProjectsService(CimsDbContext db, AuditService audit, CimsApp.Services.Tenancy.ITenantContext tenant)
{
    public async Task<List<Project>> ListAsync(Guid userId, string? search = null)
    {
        var q = db.Projects.Include(p => p.AppointingParty)
            .Where(p => p.IsActive && p.Members.Any(m => m.UserId == userId && m.IsActive));
        if (!string.IsNullOrEmpty(search)) q = q.Where(p => p.Name.Contains(search) || p.Code.Contains(search));
        return await q.OrderByDescending(p => p.UpdatedAt).ToListAsync();
    }

    public async Task<Project> GetByIdAsync(Guid id, Guid userId) =>
        await db.Projects.Include(p => p.AppointingParty).Include(p => p.Members).ThenInclude(m => m.User)
            .Include(p => p.Appointments).ThenInclude(a => a.Organisation)
            .FirstOrDefaultAsync(p => p.Id == id && p.IsActive && p.Members.Any(m => m.UserId == userId && m.IsActive))
        ?? throw new NotFoundException("Project");

    public async Task<Project> CreateAsync(CreateProjectRequest req, Guid userId, string? ip, string? ua)
    {
        // Tenant-ownership of the new Project is determined by AppointingPartyId
        // (see CimsDbContext query filter). Non-SuperAdmin callers are locked to
        // their own organisation; SuperAdmin may create projects under any
        // appointing party (platform-level bypass, audited). See ADR-0012.
        var appointingPartyId = tenant.IsSuperAdmin
            ? req.AppointingPartyId
            : tenant.OrganisationId ?? throw new ForbiddenException("No tenant context");
        if (!tenant.IsSuperAdmin && req.AppointingPartyId != appointingPartyId)
            throw new ForbiddenException("AppointingPartyId must match the caller's organisation");
        var p = new Project { Name = req.Name, Code = req.Code.ToUpperInvariant(), Description = req.Description, AppointingPartyId = appointingPartyId, StartDate = req.StartDate, EndDate = req.EndDate, Location = req.Location, Country = req.Country, Currency = req.Currency ?? "GBP", BudgetValue = req.BudgetValue, Sector = req.Sector, Sponsor = req.Sponsor, EirRef = req.EirRef, Members = [new ProjectMember { UserId = userId, Role = UserRole.ProjectManager }] };
        db.Projects.Add(p);
        await db.SaveChangesAsync();
        await audit.WriteAsync(userId, tenant.IsSuperAdmin ? "project.created.superadmin_bypass" : "project.created", "Project", p.Id.ToString(), p.Id, ip: ip, ua: ua);
        return p;
    }

    public async Task AddMemberAsync(Guid projectId, Guid userId, UserRole role, Guid actorId)
    {
        var existing = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId);
        if (existing != null) { existing.Role = role; existing.IsActive = true; }
        else db.ProjectMembers.Add(new ProjectMember { ProjectId = projectId, UserId = userId, Role = role });
        await db.SaveChangesAsync();
    }
}

// ── Cost & Commercial ─────────────────────────────────────────────────────────
// PAFM-SD Appendix F.2 (S1 module). T-S1-03 ships CSV import on top
// of the CostBreakdownItem entity (T-S1-02). Budget at line, EVM,
// commitments, variations etc. follow in T-S1-04..09.
public class CostService(CimsDbContext db, AuditService audit)
{
    public sealed record ImportResult(int RowsImported);

    /// <summary>
    /// Import a CBS (Cost Breakdown Structure) from CSV into a project
    /// that does not yet have CBS rows. Header columns:
    /// Code, Name, ParentCode, Description, SortOrder.
    /// Code and Name are required; ParentCode is empty for top-level
    /// rows and otherwise must match a Code that appeared earlier in
    /// the file (forward references are rejected to keep import order
    /// = tree-depth order).
    /// </summary>
    public async Task<ImportResult> ImportCbsAsync(
        Guid projectId, Stream csvStream, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        // Project must exist in the caller's tenant scope. The query
        // filter handles cross-tenant attempts — they 404 here.
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        // V1.0 import-into-empty only. Re-import / replace semantics
        // deferred (would need a separate clear endpoint or merge
        // logic — out of T-S1-03 scope).
        if (await db.CostBreakdownItems.AnyAsync(c => c.ProjectId == projectId, ct))
            throw new ConflictException("Project already has CBS rows. Clear them before re-importing.");

        var rows = ParseCsv(csvStream);
        if (rows.Count == 0) throw new ValidationException(["CSV contains no data rows"]);

        // Build code -> id map as we insert so ParentCode lookups
        // resolve in O(1). Forward-reference rejection keeps this
        // safe — a row's ParentCode must be in the map already.
        var codeToId = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var errors = new List<string>();
        var entities = new List<CostBreakdownItem>(rows.Count);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var lineNo = i + 2; // header is line 1, first data row is line 2
            if (codeToId.ContainsKey(row.Code))
            {
                errors.Add($"Line {lineNo}: duplicate Code '{row.Code}'");
                continue;
            }
            Guid? parentId = null;
            if (!string.IsNullOrEmpty(row.ParentCode))
            {
                if (!codeToId.TryGetValue(row.ParentCode, out var pid))
                {
                    errors.Add($"Line {lineNo}: ParentCode '{row.ParentCode}' not found earlier in file");
                    continue;
                }
                parentId = pid;
            }
            var entity = new CostBreakdownItem
            {
                ProjectId   = projectId,
                ParentId    = parentId,
                Code        = row.Code,
                Name        = row.Name,
                Description = row.Description,
                SortOrder   = row.SortOrder,
            };
            codeToId[row.Code] = entity.Id;
            entities.Add(entity);
        }

        if (errors.Count > 0) throw new ValidationException(errors);

        db.CostBreakdownItems.AddRange(entities);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actorId, "cbs.imported", "CostBreakdownItem", projectId.ToString(), projectId,
            detail: new { rowCount = entities.Count }, ip: ip, ua: ua);

        return new ImportResult(entities.Count);
    }

    /// <summary>
    /// Set (or clear, with budget == null) the planned budget on a single
    /// CBS line. T-S1-04, F.2 "Budget set at CBS line level". Currency is
    /// implied by Project.Currency — the line carries the amount only.
    /// </summary>
    public async Task SetLineBudgetAsync(
        Guid projectId, Guid itemId, decimal? budget, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (budget.HasValue && budget.Value < 0m)
            throw new ValidationException(["Budget must be zero or greater"]);

        // Single query enforces both tenant scope (via the CBS query
        // filter through Project.AppointingPartyId) and project membership
        // of the line. Cross-tenant or wrong-project itemIds 404.
        var item = await db.CostBreakdownItems
            .FirstOrDefaultAsync(c => c.Id == itemId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("CBS line");

        var previous = item.Budget;
        item.Budget = budget;
        await db.SaveChangesAsync(ct);

        // Per-row Update audit is captured automatically by AuditInterceptor;
        // the explicit cbs.line_budget_set entry below carries the
        // before/after pair as a structured detail for cost-domain
        // reporting (separate from the field-level audit).
        await audit.WriteAsync(actorId, "cbs.line_budget_set", "CostBreakdownItem",
            itemId.ToString(), projectId,
            detail: new { previous, current = budget }, ip: ip, ua: ua);
    }

    /// <summary>
    /// Set or clear the scheduled date range for a CBS line (B-017).
    /// Both ends nullable; if both are set, ScheduledStart must be
    /// strictly before ScheduledEnd. Setting one without the other
    /// is allowed — assessor may know the start before the end-date
    /// estimate firms up. Clearing (null, null) is also allowed.
    /// </summary>
    public async Task SetLineScheduleAsync(
        Guid projectId, Guid itemId, SetLineScheduleRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (req.ScheduledStart.HasValue && req.ScheduledEnd.HasValue
            && req.ScheduledStart.Value >= req.ScheduledEnd.Value)
            throw new ValidationException(["ScheduledStart must be before ScheduledEnd"]);

        var item = await db.CostBreakdownItems
            .FirstOrDefaultAsync(c => c.Id == itemId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("CBS line");

        var previousStart = item.ScheduledStart;
        var previousEnd   = item.ScheduledEnd;
        item.ScheduledStart = req.ScheduledStart;
        item.ScheduledEnd   = req.ScheduledEnd;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actorId, "cbs.line_schedule_set", "CostBreakdownItem",
            itemId.ToString(), projectId,
            detail: new
            {
                previousStart, previousEnd,
                currentStart = req.ScheduledStart,
                currentEnd   = req.ScheduledEnd,
            }, ip: ip, ua: ua);
    }

    /// <summary>
    /// Set or clear the percent-complete on a CBS line (B-017). Stored
    /// as a fraction in [0, 1]. Null = not yet reported. The unblocker
    /// for EVM EV (T-S1-07) and payment-cert valuation auto-derivation
    /// (T-S1-09) — those wire-ups follow when there's appetite.
    /// </summary>
    public async Task SetLineProgressAsync(
        Guid projectId, Guid itemId, SetLineProgressRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (req.PercentComplete is { } pc && (pc < 0m || pc > 1m))
            throw new ValidationException(["PercentComplete must be between 0 and 1"]);

        var item = await db.CostBreakdownItems
            .FirstOrDefaultAsync(c => c.Id == itemId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("CBS line");

        var previous = item.PercentComplete;
        item.PercentComplete = req.PercentComplete;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actorId, "cbs.line_progress_set", "CostBreakdownItem",
            itemId.ToString(), projectId,
            detail: new { previous, current = req.PercentComplete }, ip: ip, ua: ua);
    }

    /// <summary>
    /// Record a monetary commitment (PO or subcontract) against a CBS
    /// line. T-S1-05, F.2 "Commitments (POs, subcontracts) tracked".
    /// Currency is implied by Project.Currency; the line carries the
    /// amount only.
    /// </summary>
    public async Task<Commitment> CreateCommitmentAsync(
        Guid projectId, CreateCommitmentRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (req.Amount <= 0m)
            throw new ValidationException(["Amount must be greater than zero"]);
        if (string.IsNullOrWhiteSpace(req.Reference))
            throw new ValidationException(["Reference is required"]);
        if (string.IsNullOrWhiteSpace(req.Counterparty))
            throw new ValidationException(["Counterparty is required"]);

        // (Id, ProjectId) tuple lookup enforces both the CBS query filter
        // (tenant scope) AND that the line actually belongs to the
        // project the caller named in the URL — same pattern as
        // SetLineBudgetAsync.
        var line = await db.CostBreakdownItems
            .FirstOrDefaultAsync(c => c.Id == req.CostBreakdownItemId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("CBS line");

        var commitment = new Commitment
        {
            ProjectId           = projectId,
            CostBreakdownItemId = line.Id,
            Type                = req.Type,
            Reference           = req.Reference.Trim(),
            Counterparty        = req.Counterparty.Trim(),
            Amount              = req.Amount,
            Description         = req.Description,
        };
        db.Commitments.Add(commitment);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actorId, "commitment.created", "Commitment",
            commitment.Id.ToString(), projectId,
            detail: new
            {
                cbsLineId = line.Id,
                type      = req.Type.ToString(),
                amount    = req.Amount,
                reference = commitment.Reference,
            }, ip: ip, ua: ua);
        return commitment;
    }

    /// <summary>
    /// Per-line committed-vs-budget rollup for a project's CBS. T-S1-05,
    /// F.2 "committed-vs-budget rollup". Returns one row per CBS line
    /// (flat, ordered by Code) carrying the line's Budget, the SUM of
    /// commitment Amounts on that line, and the variance
    /// (Budget - Committed) when Budget is set.
    ///
    /// Tree aggregation (parent rolls up children) is intentionally NOT
    /// done here — that semantic belongs in the EVM PV calculation
    /// (T-S1-07) where it has to make explicit decisions about whether
    /// budgets are placed at leaves or at parents. v1.0 callers
    /// (UI, EVM) build the tree on top of this flat result.
    /// </summary>
    public async Task<List<CbsLineRollupDto>> GetCbsRollupAsync(
        Guid projectId, CancellationToken ct = default)
    {
        // Tenant scope: the project lookup uses the Project query filter,
        // so a cross-tenant projectId 404s before any CBS / Commitment
        // query runs. Lines and commitments share the same filter.
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var lines = await db.CostBreakdownItems
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.Code)
            .ToListAsync(ct);

        var commitmentSums = await db.Commitments
            .Where(c => c.ProjectId == projectId)
            .GroupBy(c => c.CostBreakdownItemId)
            .Select(g => new { ItemId = g.Key, Total = g.Sum(c => c.Amount) })
            .ToListAsync(ct);
        var commByItem = commitmentSums.ToDictionary(x => x.ItemId, x => x.Total);

        // Actuals are summed across all periods (open and closed) — the
        // rollup reflects everything that has actually been spent against
        // the line. Per-period breakdown belongs to the cashflow report
        // (T-S1-11), not the cost rollup.
        var actualSums = await db.ActualCosts
            .Where(a => a.ProjectId == projectId)
            .GroupBy(a => a.CostBreakdownItemId)
            .Select(g => new { ItemId = g.Key, Total = g.Sum(a => a.Amount) })
            .ToListAsync(ct);
        var actByItem = actualSums.ToDictionary(x => x.ItemId, x => x.Total);

        return lines.Select(l =>
        {
            var committed = commByItem.TryGetValue(l.Id, out var c) ? c : 0m;
            var actual    = actByItem.TryGetValue(l.Id, out var a) ? a : 0m;
            var variance  = l.Budget.HasValue ? (decimal?)(l.Budget.Value - committed) : null;
            return new CbsLineRollupDto(l.Id, l.Code, l.Name, l.ParentId,
                l.Budget, committed, actual, variance);
        }).ToList();
    }

    /// <summary>
    /// Open a new CostPeriod on the project. v1.0 requires
    /// `StartDate &lt; EndDate` and a non-empty Label; period overlap
    /// with existing periods is intentionally NOT enforced — the
    /// project may legitimately want overlapping windows (months
    /// alongside quarters, or contractual periods alongside
    /// reporting months). T-S1-06.
    /// </summary>
    public async Task<CostPeriod> CreatePeriodAsync(
        Guid projectId, CreatePeriodRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Label))
            throw new ValidationException(["Label is required"]);
        if (req.StartDate >= req.EndDate)
            throw new ValidationException(["StartDate must be before EndDate"]);
        if (req.PlannedCashflow is < 0m)
            throw new ValidationException(["PlannedCashflow must be zero or greater"]);

        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var period = new CostPeriod
        {
            ProjectId       = projectId,
            Label           = req.Label.Trim(),
            StartDate       = req.StartDate,
            EndDate         = req.EndDate,
            PlannedCashflow = req.PlannedCashflow,
        };
        db.CostPeriods.Add(period);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actorId, "cost_period.opened", "CostPeriod",
            period.Id.ToString(), projectId,
            detail: new { label = period.Label, period.StartDate, period.EndDate, period.PlannedCashflow },
            ip: ip, ua: ua);
        return period;
    }

    /// <summary>
    /// Set or clear the planned-cashflow baseline on an existing
    /// CostPeriod. T-S1-11. Allowed on both Open and Closed periods —
    /// baselines often get refined after the period closes (a re-baseline
    /// is a forecast adjustment, not an actual mutation).
    /// </summary>
    public async Task SetPeriodBaselineAsync(
        Guid projectId, Guid periodId, SetPeriodBaselineRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (req.PlannedCashflow is < 0m)
            throw new ValidationException(["PlannedCashflow must be zero or greater"]);

        var period = await db.CostPeriods
            .FirstOrDefaultAsync(p => p.Id == periodId && p.ProjectId == projectId, ct)
            ?? throw new NotFoundException("CostPeriod");

        var previous = period.PlannedCashflow;
        period.PlannedCashflow = req.PlannedCashflow;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actorId, "cost_period.baseline_set", "CostPeriod",
            period.Id.ToString(), projectId,
            detail: new { period.Label, previous, current = req.PlannedCashflow },
            ip: ip, ua: ua);
    }

    /// <summary>
    /// Close a CostPeriod. Once closed, RecordActualAsync rejects any
    /// further actuals targeting it; the close is a one-way integrity
    /// boundary in v1.0 (re-open deferred — out-of-period correction
    /// goes to the next open period). Closing an already-closed period
    /// is rejected with ConflictException so the operator notices.
    /// </summary>
    public async Task ClosePeriodAsync(
        Guid projectId, Guid periodId, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var period = await db.CostPeriods
            .FirstOrDefaultAsync(p => p.Id == periodId && p.ProjectId == projectId, ct)
            ?? throw new NotFoundException("CostPeriod");
        if (period.IsClosed)
            throw new ConflictException("Period is already closed");

        period.IsClosed   = true;
        period.ClosedAt   = DateTime.UtcNow;
        period.ClosedById = actorId;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actorId, "cost_period.closed", "CostPeriod",
            period.Id.ToString(), projectId,
            detail: new { label = period.Label }, ip: ip, ua: ua);
    }

    /// <summary>
    /// Record an actual cost against a CBS line in a specific
    /// CostPeriod. Validates `Amount &gt; 0`, the line belongs to the
    /// project (tenant + cross-project guard pattern shared with
    /// SetLineBudget / CreateCommitment), and the target period is open
    /// AND belongs to the same project. Closed periods reject writes.
    /// T-S1-06.
    /// </summary>
    public async Task<ActualCost> RecordActualAsync(
        Guid projectId, RecordActualRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (req.Amount <= 0m)
            throw new ValidationException(["Amount must be greater than zero"]);

        var line = await db.CostBreakdownItems
            .FirstOrDefaultAsync(c => c.Id == req.CostBreakdownItemId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("CBS line");

        var period = await db.CostPeriods
            .FirstOrDefaultAsync(p => p.Id == req.PeriodId && p.ProjectId == projectId, ct)
            ?? throw new NotFoundException("CostPeriod");
        if (period.IsClosed)
            throw new ConflictException("Period is closed; record actuals against an open period instead");

        var actual = new ActualCost
        {
            ProjectId           = projectId,
            CostBreakdownItemId = line.Id,
            PeriodId            = period.Id,
            Amount              = req.Amount,
            Reference           = req.Reference,
            Description         = req.Description,
        };
        db.ActualCosts.Add(actual);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actorId, "actual_cost.recorded", "ActualCost",
            actual.Id.ToString(), projectId,
            detail: new
            {
                cbsLineId = line.Id,
                periodId  = period.Id,
                amount    = req.Amount,
                reference = req.Reference,
            }, ip: ip, ua: ua);
        return actual;
    }

    /// <summary>
    /// Project-level EVM snapshot at a given data date — wires the
    /// B-017 schedule + progress primitive into the pure math in
    /// <see cref="Evm.Calculate"/>. Closes the EVM service-integration
    /// half of T-S1-07.
    ///
    /// Per CBS line, given Budget, PercentComplete, ScheduledStart,
    /// ScheduledEnd, the snapshot contributions are:
    ///
    ///   BAC contribution = Budget ?? 0
    ///   EV  contribution = (Budget ?? 0) × (PercentComplete ?? 0)
    ///   PV  contribution = (Budget ?? 0) × scheduleElapsed(dataDate,
    ///                                                       Start,
    ///                                                       End)
    ///
    /// where `scheduleElapsed` returns 0 when either schedule end is
    /// null (no plan = no PV contribution), 0 before Start, 1 at or
    /// after End, and a linear fraction in between. AC is summed
    /// across every <see cref="ActualCost"/> on the project, ignoring
    /// the data date — actuals don't have a "should-have-happened-by"
    /// gate; what's been spent is what's been spent.
    /// </summary>
    public async Task<Evm.EvmSnapshot> GetEvmSnapshotAsync(
        Guid projectId, DateTime dataDate, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var lines = await db.CostBreakdownItems
            .Where(c => c.ProjectId == projectId)
            .Select(c => new
            {
                c.Budget,
                c.PercentComplete,
                c.ScheduledStart,
                c.ScheduledEnd,
            })
            .ToListAsync(ct);

        decimal bac = 0m, ev = 0m, pv = 0m;
        foreach (var l in lines)
        {
            var budget = l.Budget ?? 0m;
            bac += budget;
            ev  += budget * (l.PercentComplete ?? 0m);
            pv  += budget * ScheduleFraction(dataDate, l.ScheduledStart, l.ScheduledEnd);
        }

        var ac = await db.ActualCosts
            .Where(a => a.ProjectId == projectId)
            .SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;

        return Evm.Calculate(pv: pv, ev: ev, ac: ac, bac: bac);
    }

    /// <summary>Linear schedule-elapsed fraction in [0, 1]. Returns 0
    /// when either bound is unset — a line with no schedule contributes
    /// no PV (it isn't "supposed" to be earning value at any specific
    /// date).</summary>
    private static decimal ScheduleFraction(DateTime dataDate, DateTime? start, DateTime? end)
    {
        if (!start.HasValue || !end.HasValue) return 0m;
        if (dataDate <= start.Value) return 0m;
        if (dataDate >= end.Value) return 1m;
        var total   = (decimal)(end.Value - start.Value).TotalSeconds;
        var elapsed = (decimal)(dataDate - start.Value).TotalSeconds;
        return elapsed / total;
    }

    /// <summary>Linear overlap of [periodStart, periodEnd] with the
    /// schedule [scheduleStart, scheduleEnd], expressed as a fraction
    /// of the schedule's total duration. Returns 0 when the line has
    /// no schedule. Used to distribute a CBS line's Budget across
    /// CostPeriods for the per-line cashflow breakdown (T-S1-11
    /// wire-up).</summary>
    private static decimal ScheduleOverlapFraction(
        DateTime periodStart, DateTime periodEnd,
        DateTime? scheduleStart, DateTime? scheduleEnd)
    {
        if (!scheduleStart.HasValue || !scheduleEnd.HasValue) return 0m;
        if (scheduleEnd.Value <= scheduleStart.Value) return 0m;
        var overlapStart = periodStart > scheduleStart.Value ? periodStart : scheduleStart.Value;
        var overlapEnd   = periodEnd   < scheduleEnd.Value   ? periodEnd   : scheduleEnd.Value;
        if (overlapEnd <= overlapStart) return 0m;
        var total   = (decimal)(scheduleEnd.Value - scheduleStart.Value).TotalSeconds;
        var overlap = (decimal)(overlapEnd - overlapStart).TotalSeconds;
        return overlap / total;
    }

    /// <summary>
    /// Per-CBS-line cashflow breakdown (T-S1-11 wire-up via B-017).
    /// One series per CBS line, each carrying one point per CostPeriod.
    ///
    ///   BaselinePlanned(line, period) = Budget × overlapFraction(
    ///     period.[Start, End], line.[ScheduledStart, ScheduledEnd])
    ///   Actual(line, period)          = Σ ActualCost.Amount where
    ///     CostBreakdownItemId == line.Id AND PeriodId == period.Id
    ///
    /// Lines with no schedule contribute zero baseline across all
    /// periods (line not "supposed" to be spending at any specific
    /// date). Lines with no Budget contribute zero too.
    /// </summary>
    public async Task<CashflowByLineDto> GetCashflowByLineAsync(
        Guid projectId, CancellationToken ct = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var periods = await db.CostPeriods
            .Where(p => p.ProjectId == projectId)
            .OrderBy(p => p.StartDate).ThenBy(p => p.Id)
            .ToListAsync(ct);

        var lines = await db.CostBreakdownItems
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.Code)
            .ToListAsync(ct);

        // Group actuals by (line, period) so we can join in-memory.
        var actualSums = await db.ActualCosts
            .Where(a => a.ProjectId == projectId)
            .GroupBy(a => new { a.CostBreakdownItemId, a.PeriodId })
            .Select(g => new { g.Key.CostBreakdownItemId, g.Key.PeriodId, Total = g.Sum(a => a.Amount) })
            .ToListAsync(ct);
        var actualByLinePeriod = actualSums.ToDictionary(
            x => (x.CostBreakdownItemId, x.PeriodId), x => x.Total);

        var series = new List<CashflowLineSeries>(lines.Count);
        foreach (var line in lines)
        {
            var budget = line.Budget ?? 0m;
            var points = new List<CashflowLinePeriodPoint>(periods.Count);
            foreach (var p in periods)
            {
                var fraction = ScheduleOverlapFraction(
                    p.StartDate, p.EndDate, line.ScheduledStart, line.ScheduledEnd);
                var planned  = budget * fraction;
                var actual   = actualByLinePeriod.TryGetValue((line.Id, p.Id), out var a) ? a : 0m;
                points.Add(new CashflowLinePeriodPoint(
                    PeriodId: p.Id, Label: p.Label,
                    StartDate: p.StartDate, EndDate: p.EndDate,
                    BaselinePlanned: planned, Actual: actual));
            }
            series.Add(new CashflowLineSeries(
                ItemId: line.Id, Code: line.Code, Name: line.Name,
                Budget: line.Budget,
                ScheduledStart: line.ScheduledStart,
                ScheduledEnd:   line.ScheduledEnd,
                Points: points));
        }

        return new CashflowByLineDto(project.Currency, series);
    }

    /// <summary>
    /// Project-level cashflow S-curve (T-S1-11). One point per
    /// CostPeriod, ordered by StartDate. For each point:
    ///   BaselinePlanned     = the period's manual PlannedCashflow (nullable).
    ///   CumulativeBaseline  = running total of PlannedCashflow values
    ///                         (null treated as 0 — periods without a baseline
    ///                         simply don't add to the total).
    ///   Actual              = sum of ActualCost.Amount for the period.
    ///   CumulativeActual    = running total of Actual.
    ///   Forecast            = CumulativeActual for periods up to and
    ///                         including the latest period that has any
    ///                         actuals; for later periods,
    ///                         CumulativeActual_latest +
    ///                           (CumulativeBaseline_thisPeriod −
    ///                            CumulativeBaseline_atLatestActualPeriod).
    ///                         Null when no baseline AND no actuals exist.
    /// Per-CBS-line cashflow breakdown is deferred (v1.0 is project-level
    /// only; the consumer composes per-line views from the rollup if needed).
    /// </summary>
    public async Task<CashflowDto> GetCashflowAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var periods = await db.CostPeriods
            .Where(p => p.ProjectId == projectId)
            .OrderBy(p => p.StartDate).ThenBy(p => p.Id)
            .ToListAsync(ct);

        // Group ActualCost by PeriodId so we can join in-memory; the
        // (ProjectId, PeriodId) index on ActualCost (T-S1-06) makes this
        // a single index seek per project.
        var actualSums = await db.ActualCosts
            .Where(a => a.ProjectId == projectId)
            .GroupBy(a => a.PeriodId)
            .Select(g => new { PeriodId = g.Key, Total = g.Sum(a => a.Amount) })
            .ToListAsync(ct);
        var actualByPeriod = actualSums.ToDictionary(x => x.PeriodId, x => x.Total);

        var points = new List<CashflowPeriodPoint>(periods.Count);
        decimal cumBaseline = 0m;
        decimal cumActual   = 0m;
        var snapshots = new (decimal cumBaseline, decimal cumActual, bool hasActuals)[periods.Count];

        // Pass 1 — accumulate cumulative totals; record per-period
        // snapshots so the forecast pass can reference the latest
        // actual period.
        for (var i = 0; i < periods.Count; i++)
        {
            var p          = periods[i];
            var actual     = actualByPeriod.TryGetValue(p.Id, out var a) ? a : 0m;
            cumBaseline   += p.PlannedCashflow ?? 0m;
            cumActual     += actual;
            snapshots[i]   = (cumBaseline, cumActual, hasActuals: actual != 0m);
        }

        // Latest period index that has any actuals; -1 if none.
        var latestActualIdx = -1;
        for (var i = periods.Count - 1; i >= 0; i--)
            if (snapshots[i].hasActuals) { latestActualIdx = i; break; }

        var latestActualValue   = latestActualIdx >= 0 ? snapshots[latestActualIdx].cumActual   : 0m;
        var latestActualBaseline = latestActualIdx >= 0 ? snapshots[latestActualIdx].cumBaseline : 0m;

        for (var i = 0; i < periods.Count; i++)
        {
            var p   = periods[i];
            var s   = snapshots[i];
            var act = actualByPeriod.TryGetValue(p.Id, out var v) ? v : 0m;

            decimal? forecast;
            if (i <= latestActualIdx)
            {
                // Past or current: forecast = what actually happened cumulatively.
                forecast = s.cumActual;
            }
            else if (p.PlannedCashflow.HasValue || s.cumBaseline > 0m)
            {
                // Future: project from the latest actual at the
                // baseline rate.
                forecast = latestActualValue + (s.cumBaseline - latestActualBaseline);
            }
            else if (latestActualIdx >= 0)
            {
                // Future with no baseline at all: hold the latest
                // actual flat (no signal otherwise).
                forecast = latestActualValue;
            }
            else
            {
                // No baseline data and no actuals — no projection.
                forecast = null;
            }

            points.Add(new CashflowPeriodPoint(
                PeriodId:           p.Id,
                Label:              p.Label,
                StartDate:          p.StartDate,
                EndDate:            p.EndDate,
                IsClosed:           p.IsClosed,
                BaselinePlanned:    p.PlannedCashflow,
                CumulativeBaseline: s.cumBaseline,
                Actual:             act,
                CumulativeActual:   s.cumActual,
                Forecast:           forecast));
        }

        return new CashflowDto(project.Currency, points);
    }

    private sealed record CsvRow(string Code, string Name, string? ParentCode, string? Description, int SortOrder);

    private static List<CsvRow> ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
        }
        if (lines.Count == 0) throw new ValidationException(["CSV is empty"]);

        // Header validation: at minimum Code,Name,ParentCode,Description,SortOrder.
        var header = lines[0].Split(',');
        var expected = new[] { "Code", "Name", "ParentCode", "Description", "SortOrder" };
        if (header.Length < expected.Length || !header.Take(expected.Length).Select(h => h.Trim())
                .SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
        {
            throw new ValidationException([$"CSV header must be: {string.Join(",", expected)}"]);
        }

        var rows = new List<CsvRow>();
        for (int i = 1; i < lines.Count; i++)
        {
            var lineNo = i + 1;
            var fields = lines[i].Split(',');
            if (fields.Length < expected.Length)
                throw new ValidationException([$"Line {lineNo}: expected {expected.Length} columns, found {fields.Length}"]);

            var code = fields[0].Trim();
            var name = fields[1].Trim();
            var parentCode = string.IsNullOrWhiteSpace(fields[2]) ? null : fields[2].Trim();
            var description = string.IsNullOrWhiteSpace(fields[3]) ? null : fields[3].Trim();
            var sortOrderRaw = fields[4].Trim();

            if (string.IsNullOrEmpty(code))
                throw new ValidationException([$"Line {lineNo}: Code is required"]);
            if (string.IsNullOrEmpty(name))
                throw new ValidationException([$"Line {lineNo}: Name is required"]);
            if (code.Length > 50)
                throw new ValidationException([$"Line {lineNo}: Code exceeds 50 characters"]);
            if (name.Length > 200)
                throw new ValidationException([$"Line {lineNo}: Name exceeds 200 characters"]);

            var sortOrder = 0;
            if (!string.IsNullOrEmpty(sortOrderRaw) && !int.TryParse(sortOrderRaw, out sortOrder))
                throw new ValidationException([$"Line {lineNo}: SortOrder '{sortOrderRaw}' is not a valid integer"]);

            rows.Add(new CsvRow(code, name, parentCode, description, sortOrder));
        }
        return rows;
    }
}

// ── Payment Certificates ──────────────────────────────────────────────────────
// PAFM-SD Appendix F.2 seventh bullet (S1). T-S1-09. NEC4 cumulative
// semantics per ADR-0013 — JCT and Construction Act notice flows
// (B-014) deferred to v1.1.
//
// Calculation (NEC4 / ADR-0013):
//   CumulativeGross    = Valuation + IncludedVariations + Materials
//   RetentionBase      = Valuation + IncludedVariations  (materials excluded)
//   RetentionAmount    = RetentionBase × (RetentionPercent / 100)
//   CumulativeNet      = CumulativeGross − RetentionAmount
//   PreviouslyCertified = Σ AmountDue from prior Issued certs
//   AmountDue          = CumulativeNet − PreviouslyCertified
//
// `IncludedVariationsAmount` is null while a certificate is Draft —
// the service computes a live preview on read. At issue time, it
// snapshots the sum of EstimatedCostImpact on Approved Variations
// (so a variation approved AFTER issue lands in the next certificate).
public class PaymentCertificatesService(CimsDbContext db, AuditService audit)
{
    public async Task<PaymentCertificateDto> CreateDraftAsync(
        Guid projectId, CreatePaymentCertificateDraftRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        ValidateInputs(req.CumulativeValuation, req.CumulativeMaterialsOnSite, req.RetentionPercent);

        // Period must exist in this project and tenant scope.
        _ = await db.CostPeriods
            .FirstOrDefaultAsync(p => p.Id == req.PeriodId && p.ProjectId == projectId, ct)
            ?? throw new NotFoundException("CostPeriod");

        // One certificate per period — Draft or Issued. Two parallel
        // drafts on the same period would race the cumulative chain.
        if (await db.PaymentCertificates.AnyAsync(
                c => c.ProjectId == projectId && c.PeriodId == req.PeriodId, ct))
            throw new ConflictException("A payment certificate already exists for this period");

        var count = await db.PaymentCertificates
            .CountAsync(c => c.ProjectId == projectId, ct);

        var cert = new PaymentCertificate
        {
            ProjectId                 = projectId,
            PeriodId                  = req.PeriodId,
            CertificateNumber         = $"PC-{(count + 1):D4}",
            State                     = PaymentCertificateState.Draft,
            CumulativeValuation       = req.CumulativeValuation,
            CumulativeMaterialsOnSite = req.CumulativeMaterialsOnSite,
            RetentionPercent          = req.RetentionPercent,
            IncludedVariationsAmount  = null,
        };
        db.PaymentCertificates.Add(cert);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actorId, "payment_certificate.draft_created",
            "PaymentCertificate", cert.Id.ToString(), projectId,
            detail: new
            {
                number             = cert.CertificateNumber,
                periodId           = cert.PeriodId,
                valuation          = cert.CumulativeValuation,
                materialsOnSite    = cert.CumulativeMaterialsOnSite,
                retentionPercent   = cert.RetentionPercent,
            }, ip: ip, ua: ua);

        return await BuildDtoAsync(cert, ct);
    }

    public async Task<PaymentCertificateDto> UpdateDraftAsync(
        Guid projectId, Guid certificateId, UpdatePaymentCertificateDraftRequest req,
        Guid actorId, string? ip = null, string? ua = null,
        CancellationToken ct = default)
    {
        ValidateInputs(req.CumulativeValuation, req.CumulativeMaterialsOnSite, req.RetentionPercent);

        var cert = await db.PaymentCertificates
            .FirstOrDefaultAsync(c => c.Id == certificateId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("PaymentCertificate");
        if (cert.State != PaymentCertificateState.Draft)
            throw new ConflictException(
                $"Certificate is in state {cert.State}; only Draft certificates can be updated");

        cert.CumulativeValuation       = req.CumulativeValuation;
        cert.CumulativeMaterialsOnSite = req.CumulativeMaterialsOnSite;
        cert.RetentionPercent          = req.RetentionPercent;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actorId, "payment_certificate.draft_updated",
            "PaymentCertificate", cert.Id.ToString(), projectId,
            detail: new
            {
                number             = cert.CertificateNumber,
                valuation          = cert.CumulativeValuation,
                materialsOnSite    = cert.CumulativeMaterialsOnSite,
                retentionPercent   = cert.RetentionPercent,
            }, ip: ip, ua: ua);

        return await BuildDtoAsync(cert, ct);
    }

    /// <summary>
    /// Transition Draft → Issued. Snapshots the approved-variations
    /// sum at this moment into IncludedVariationsAmount. Once issued,
    /// the certificate is immutable in v1.0; corrections go into the
    /// next period's certificate (cumulative chain self-corrects).
    /// </summary>
    public async Task<PaymentCertificateDto> IssueAsync(
        Guid projectId, Guid certificateId, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var cert = await db.PaymentCertificates
            .FirstOrDefaultAsync(c => c.Id == certificateId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("PaymentCertificate");
        if (cert.State != PaymentCertificateState.Draft)
            throw new ConflictException(
                $"Certificate is in state {cert.State}; only Draft certificates can be issued");

        var variationsAtIssue = await SumApprovedVariationsAsync(projectId, ct);
        cert.IncludedVariationsAmount = variationsAtIssue;
        cert.State                    = PaymentCertificateState.Issued;
        cert.IssuedById               = actorId;
        cert.IssuedAt                 = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actorId, "payment_certificate.issued",
            "PaymentCertificate", cert.Id.ToString(), projectId,
            detail: new
            {
                number             = cert.CertificateNumber,
                valuation          = cert.CumulativeValuation,
                materialsOnSite    = cert.CumulativeMaterialsOnSite,
                retentionPercent   = cert.RetentionPercent,
                variationsIncluded = variationsAtIssue,
            }, ip: ip, ua: ua);

        return await BuildDtoAsync(cert, ct);
    }

    public async Task<PaymentCertificateDto> GetAsync(
        Guid projectId, Guid certificateId, CancellationToken ct = default)
    {
        var cert = await db.PaymentCertificates
            .FirstOrDefaultAsync(c => c.Id == certificateId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("PaymentCertificate");
        return await BuildDtoAsync(cert, ct);
    }

    private static void ValidateInputs(decimal valuation, decimal materials, decimal retentionPct)
    {
        var errors = new List<string>();
        if (valuation < 0m)         errors.Add("CumulativeValuation must be zero or greater");
        if (materials < 0m)         errors.Add("CumulativeMaterialsOnSite must be zero or greater");
        if (retentionPct < 0m || retentionPct > 100m)
            errors.Add("RetentionPercent must be between 0 and 100");
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private async Task<decimal> SumApprovedVariationsAsync(Guid projectId, CancellationToken ct)
    {
        // Variations with a null EstimatedCostImpact contribute zero
        // (raised without a costed estimate). Unsigned sum is the
        // contractual norm — a negative impact (omission saving) is
        // an approved reduction and reduces the certificate value.
        var sum = await db.Variations
            .Where(v => v.ProjectId == projectId
                     && v.State == VariationState.Approved
                     && v.EstimatedCostImpact != null)
            .SumAsync(v => v.EstimatedCostImpact!.Value, ct);
        return sum;
    }

    private async Task<PaymentCertificateDto> BuildDtoAsync(
        PaymentCertificate cert, CancellationToken ct)
    {
        // Variations: snapshot when issued; live preview when draft.
        var variationsAmount = cert.IncludedVariationsAmount
            ?? await SumApprovedVariationsAsync(cert.ProjectId, ct);

        // T-S1-09 / B-017 valuation auto-derive — Σ (Budget × PercentComplete)
        // across the project's CBS lines. NEC4 PWDD interpretation per
        // ADR-0013. Stored CumulativeValuation remains source of truth;
        // this is the progress-derived guide.
        var derivedValuation = await db.CostBreakdownItems
            .Where(c => c.ProjectId == cert.ProjectId
                     && c.Budget != null
                     && c.PercentComplete != null)
            .SumAsync(c => (decimal?)(c.Budget!.Value * c.PercentComplete!.Value), ct) ?? 0m;

        // Cumulative chain: PreviouslyCertified is the **latest prior
        // Issued cert's CumulativeNet**, not the sum of prior
        // AmountDues. With cumulative PWDD valuations, each cert's
        // CumulativeNet IS the running total certified to date — so
        // the AmountDue this period is just (this cert's net) minus
        // (last cert's net). Σ AmountDue across all issued certs
        // therefore equals the latest cert's net (conservation
        // check), but that's not how PreviouslyCertified is computed.
        var priorIssued = await db.PaymentCertificates
            .Where(c => c.ProjectId == cert.ProjectId
                     && c.Id != cert.Id
                     && c.State == PaymentCertificateState.Issued
                     && c.IssuedAt != null
                     && (cert.IssuedAt == null || c.IssuedAt < cert.IssuedAt))
            .OrderByDescending(c => c.IssuedAt)
            .FirstOrDefaultAsync(ct);

        var previouslyCertified = priorIssued == null ? 0m : NetOf(priorIssued);

        var gross           = cert.CumulativeValuation + variationsAmount + cert.CumulativeMaterialsOnSite;
        var retentionBase   = cert.CumulativeValuation + variationsAmount;
        var retentionAmount = retentionBase * (cert.RetentionPercent / 100m);
        var net             = gross - retentionAmount;
        var amountDue       = net - previouslyCertified;

        return new PaymentCertificateDto(
            Id:                        cert.Id,
            ProjectId:                 cert.ProjectId,
            PeriodId:                  cert.PeriodId,
            CertificateNumber:         cert.CertificateNumber,
            State:                     cert.State,
            CumulativeValuation:       cert.CumulativeValuation,
            CumulativeMaterialsOnSite: cert.CumulativeMaterialsOnSite,
            RetentionPercent:          cert.RetentionPercent,
            IncludedVariationsAmount:  variationsAmount,
            CumulativeGross:           gross,
            RetentionAmount:           retentionAmount,
            CumulativeNet:                  net,
            PreviouslyCertified:            previouslyCertified,
            AmountDue:                      amountDue,
            IssuedAt:                       cert.IssuedAt,
            DerivedValuationFromProgress:   derivedValuation);
    }

    /// <summary>NEC4 net for an Issued cert, used to compute the
    /// next cert's PreviouslyCertified. Identical math to BuildDtoAsync
    /// except it relies on the snapshotted IncludedVariationsAmount
    /// (always non-null on Issued certs).</summary>
    private static decimal NetOf(PaymentCertificate cert)
    {
        var v               = cert.IncludedVariationsAmount ?? 0m;
        var gross           = cert.CumulativeValuation + v + cert.CumulativeMaterialsOnSite;
        var retentionAmount = (cert.CumulativeValuation + v) * (cert.RetentionPercent / 100m);
        return gross - retentionAmount;
    }
}

// ── Variations ────────────────────────────────────────────────────────────────
// PAFM-SD Appendix F.2 sixth bullet (S1). T-S1-08 ships the **core
// 3-state machine** only — Raised → Approved or Raised → Rejected
// per CR-003. The full PMBOK / NEC4 6-state workflow (assess /
// instruct / value / agree) is a v1.1 candidate (B-016).
//
// Approval / rejection is a *decision record* — it does not
// automatically integrate cost / schedule impact into the project
// baseline. Manual data entry on the affected CBS lines /
// commitments is expected. Auto-integration is intentionally out of
// T-S1-08 scope; revisit when there is concrete demand.
public class VariationsService(CimsDbContext db, AuditService audit)
{
    /// <summary>
    /// Raise a new Variation in state <see cref="VariationState.Raised"/>.
    /// VariationNumber is auto-generated as `VAR-NNNN` per project,
    /// matching the existing RFI numbering pattern. If a CBS line is
    /// referenced, it must belong to the same project (cross-tenant
    /// or wrong-project lineIds 404 here, same guard as
    /// CostService.SetLineBudget / CreateCommitment).
    /// </summary>
    public async Task<Variation> RaiseAsync(
        Guid projectId, RaiseVariationRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ValidationException(["Title is required"]);

        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        if (req.CostBreakdownItemId.HasValue)
        {
            var lineExists = await db.CostBreakdownItems.AnyAsync(
                c => c.Id == req.CostBreakdownItemId.Value && c.ProjectId == projectId, ct);
            if (!lineExists) throw new NotFoundException("CBS line");
        }

        // Concurrency note: count + 1 racing two requests can produce
        // duplicate VariationNumbers. The unique index on
        // (ProjectId, VariationNumber) catches it as a SaveChanges
        // exception. Same trade-off the existing RfiService accepts;
        // a strictly serial counter is a v1.1 candidate if real
        // workflows surface contention.
        var count = await db.Variations.CountAsync(v => v.ProjectId == projectId, ct);

        var v = new Variation
        {
            ProjectId               = projectId,
            VariationNumber         = $"VAR-{(count + 1):D4}",
            Title                   = req.Title.Trim(),
            Description             = req.Description,
            Reason                  = req.Reason,
            EstimatedCostImpact     = req.EstimatedCostImpact,
            EstimatedTimeImpactDays = req.EstimatedTimeImpactDays,
            CostBreakdownItemId     = req.CostBreakdownItemId,
            RaisedById              = actorId,
            State                   = VariationState.Raised,
        };
        db.Variations.Add(v);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actorId, "variation.raised", "Variation",
            v.Id.ToString(), projectId,
            detail: new
            {
                number          = v.VariationNumber,
                costImpact      = req.EstimatedCostImpact,
                timeImpactDays  = req.EstimatedTimeImpactDays,
                cbsLineId       = req.CostBreakdownItemId,
            }, ip: ip, ua: ua);
        return v;
    }

    public Task ApproveAsync(
        Guid projectId, Guid variationId, VariationDecisionRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default) =>
        DecideAsync(projectId, variationId, VariationState.Approved, req, actorId, ip, ua, ct);

    public Task RejectAsync(
        Guid projectId, Guid variationId, VariationDecisionRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default) =>
        DecideAsync(projectId, variationId, VariationState.Rejected, req, actorId, ip, ua, ct);

    /// <summary>
    /// Shared transition path. Only Raised → Approved and Raised →
    /// Rejected are valid in v1.0; both terminal states reject any
    /// further transition with ConflictException. Re-decide after
    /// terminal is a v1.1 candidate (B-016).
    /// </summary>
    private async Task DecideAsync(
        Guid projectId, Guid variationId, VariationState target,
        VariationDecisionRequest req, Guid actorId,
        string? ip, string? ua, CancellationToken ct)
    {
        var v = await db.Variations
            .FirstOrDefaultAsync(x => x.Id == variationId && x.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Variation");

        if (v.State != VariationState.Raised)
            throw new ConflictException(
                $"Variation is in state {v.State}; only Raised variations can be {target.ToString().ToLowerInvariant()}");

        v.State        = target;
        v.DecidedById  = actorId;
        v.DecidedAt    = DateTime.UtcNow;
        v.DecisionNote = req.DecisionNote;
        await db.SaveChangesAsync(ct);

        var action = target == VariationState.Approved
            ? "variation.approved"
            : "variation.rejected";
        await audit.WriteAsync(actorId, action, "Variation",
            v.Id.ToString(), projectId,
            detail: new
            {
                number         = v.VariationNumber,
                decisionNote   = req.DecisionNote,
                costImpact     = v.EstimatedCostImpact,
                timeImpactDays = v.EstimatedTimeImpactDays,
            }, ip: ip, ua: ua);
    }
}

// ── CDE ───────────────────────────────────────────────────────────────────────
public class CdeService(CimsDbContext db, AuditService audit)
{
    public async Task<List<CdeContainer>> ListContainersAsync(Guid projectId) =>
        await db.CdeContainers.Where(c => c.ProjectId == projectId && c.IsActive).ToListAsync();

    public async Task<CdeContainer> CreateContainerAsync(Guid projectId, CreateContainerRequest req, Guid userId, string? ip, string? ua)
    {
        var c = new CdeContainer { ProjectId = projectId, Name = req.Name, Originator = req.Originator.ToUpperInvariant(), Volume = req.Volume?.ToUpperInvariant(), Level = req.Level?.ToUpperInvariant(), Type = req.Type.ToUpperInvariant(), Discipline = req.Discipline?.ToUpperInvariant(), Description = req.Description };
        db.CdeContainers.Add(c);
        await db.SaveChangesAsync();
        await audit.WriteAsync(userId, "cde.container_created", "CdeContainer", c.Id.ToString(), projectId, ip: ip, ua: ua);
        return c;
    }
}

// ── Documents ─────────────────────────────────────────────────────────────────
public class DocumentsService(CimsDbContext db, AuditService audit)
{
    public async Task<List<Document>> ListAsync(Guid projectId, CdeState? state = null, string? search = null)
    {
        var q = db.Documents.Include(d => d.Creator).Include(d => d.Container)
            .Include(d => d.Revisions.Where(r => r.IsLatest))
            .Where(d => d.ProjectId == projectId && d.IsActive);
        if (state.HasValue) q = q.Where(d => d.CurrentState == state);
        if (!string.IsNullOrEmpty(search)) q = q.Where(d => d.Title.Contains(search) || d.DocumentNumber.Contains(search));
        return await q.OrderByDescending(d => d.UpdatedAt).ToListAsync();
    }

    public async Task<Document> GetByIdAsync(Guid id, Guid projectId) =>
        await db.Documents.Include(d => d.Creator).Include(d => d.Container)
            .Include(d => d.Revisions).ThenInclude(r => r.UploadedBy)
            .FirstOrDefaultAsync(d => d.Id == id && d.ProjectId == projectId && d.IsActive)
        ?? throw new NotFoundException("Document");

    public async Task<Document> CreateAsync(Guid projectId, CreateDocumentRequest req, Guid userId, string? ip, string? ua)
    {
        var errors = DocumentNaming.Validate(req.ProjectCode, req.Originator, req.DocType, req.Number);
        if (errors.Count > 0) throw new ValidationException(errors);
        var docNum = DocumentNaming.Build(req.ProjectCode, req.Originator, req.Volume, req.Level, req.DocType, req.Role, req.Number);
        if (await db.Documents.AnyAsync(d => d.DocumentNumber == docNum)) throw new ConflictException($"Document {docNum} already exists");
        var doc = new Document { ProjectId = projectId, ContainerId = req.ContainerId, ProjectCode = req.ProjectCode.ToUpperInvariant(), Originator = req.Originator.ToUpperInvariant(), Volume = req.Volume?.ToUpperInvariant(), Level = req.Level?.ToUpperInvariant(), DocType = req.DocType.ToUpperInvariant(), Role = req.Role?.ToUpperInvariant(), Number = req.Number.ToString("D4"), DocumentNumber = docNum, Title = req.Title, Description = req.Description, Type = req.Type ?? DocumentType.Other, Tags = req.Tags ?? [], CreatorId = userId, CurrentState = CdeState.WorkInProgress };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        await audit.WriteAsync(userId, "document.created", "Document", doc.Id.ToString(), projectId, doc.Id, ip: ip, ua: ua);
        return doc;
    }

    public async Task<Document> TransitionAsync(Guid docId, Guid projectId, CdeState toState, SuitabilityCode? suitability, Guid userId, UserRole userRole, string? ip, string? ua)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == docId && d.ProjectId == projectId && d.IsActive) ?? throw new NotFoundException("Document");
        if (!CdeStateMachine.IsValidTransition(doc.CurrentState, toState)) throw new CdeTransitionException(doc.CurrentState, toState);
        if (!CdeStateMachine.CanTransition(doc.CurrentState, toState, userRole)) throw new ForbiddenException($"Role {userRole} cannot perform this transition");
        if (toState == CdeState.Published)
            await db.DocumentRevisions.Where(r => r.DocumentId == docId && r.IsLatest)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.PublishedAt, DateTime.UtcNow).SetProperty(r => r.ApprovedById, userId).SetProperty(r => r.ApprovedAt, DateTime.UtcNow).SetProperty(r => r.Suitability, suitability ?? SuitabilityCode.S2));
        var from = doc.CurrentState;
        doc.CurrentState = toState;
        await db.SaveChangesAsync();
        await audit.WriteAsync(userId, "document.state_transition", "Document", docId.ToString(), projectId, docId, new { from = from.ToString(), to = toState.ToString() }, ip, ua);
        return doc;
    }
}

// ── RFIs ──────────────────────────────────────────────────────────────────────
public class RfiService(CimsDbContext db, AuditService audit)
{
    public async Task<List<Rfi>> ListAsync(Guid projectId, RfiStatus? status = null, string? search = null)
    {
        var q = db.Rfis.Where(r => r.ProjectId == projectId);
        if (status.HasValue) q = q.Where(r => r.Status == status);
        if (!string.IsNullOrEmpty(search)) q = q.Where(r => r.Subject.Contains(search) || r.RfiNumber.Contains(search));
        return await q.OrderByDescending(r => r.CreatedAt).ToListAsync();
    }

    public async Task<Rfi> CreateAsync(Guid projectId, CreateRfiRequest req, Guid userId, string? ip, string? ua)
    {
        var count = await db.Rfis.CountAsync(r => r.ProjectId == projectId);
        var rfi = new Rfi { ProjectId = projectId, RfiNumber = $"RFI-{(count + 1):D4}", Subject = req.Subject, Description = req.Description, Discipline = req.Discipline, Priority = req.Priority, AssignedToId = req.AssignedToId, DueDate = req.DueDate, RaisedById = userId, Status = RfiStatus.Open };
        db.Rfis.Add(rfi);
        await db.SaveChangesAsync();
        await audit.WriteAsync(userId, "rfi.created", "Rfi", rfi.Id.ToString(), projectId, ip: ip, ua: ua);
        return rfi;
    }

    public async Task<Rfi> RespondAsync(Guid rfiId, Guid projectId, RespondRfiRequest req, Guid userId, UserRole role, string? ip, string? ua)
    {
        var rfi = await db.Rfis.FirstOrDefaultAsync(r => r.Id == rfiId && r.ProjectId == projectId) ?? throw new NotFoundException("RFI");

        // B-006: responder verification. If the RFI has an assigned
        // responder, only that user OR an InformationManager+ on the
        // project may respond — IMs are the natural escalation path
        // when an assigned responder is unavailable. RFIs without an
        // AssignedToId remain open for any TaskTeamMember+ (the floor
        // enforced at the controller) to respond.
        if (rfi.AssignedToId.HasValue
            && rfi.AssignedToId.Value != userId
            && !CdeStateMachine.HasMinimumRole(role, UserRole.InformationManager))
            throw new ForbiddenException("Only the assigned responder or an InformationManager+ may respond to this RFI");

        rfi.Response = req.Response; rfi.Status = req.Status;
        if (req.Status == RfiStatus.Closed) rfi.ClosedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.WriteAsync(userId, "rfi.responded", "Rfi", rfiId.ToString(), projectId, ip: ip, ua: ua);
        return rfi;
    }
}

// ── Actions ───────────────────────────────────────────────────────────────────
public class ActionsService(CimsDbContext db, AuditService audit)
{
    public async Task<List<ActionItem>> ListAsync(Guid projectId, ActionStatus? status = null, bool overdue = false)
    {
        var q = db.ActionItems.Include(a => a.Assignee).Include(a => a.CreatedBy).Where(a => a.ProjectId == projectId);
        if (status.HasValue) q = q.Where(a => a.Status == status);
        if (overdue) q = q.Where(a => a.DueDate < DateTime.UtcNow && a.Status != ActionStatus.Closed && a.Status != ActionStatus.Cancelled);
        return await q.OrderBy(a => a.Status).ThenBy(a => a.DueDate).ToListAsync();
    }

    public async Task<ActionItem> CreateAsync(Guid projectId, CreateActionRequest req, Guid userId, string? ip, string? ua)
    {
        var a = new ActionItem { ProjectId = projectId, Title = req.Title, Description = req.Description, Source = req.Source, Priority = req.Priority, AssigneeId = req.AssigneeId, DueDate = req.DueDate, CreatedById = userId };
        db.ActionItems.Add(a);
        await db.SaveChangesAsync();
        await audit.WriteAsync(userId, "action.created", "ActionItem", a.Id.ToString(), projectId, ip: ip, ua: ua);
        return a;
    }

    public async Task<ActionItem> UpdateAsync(Guid actionId, Guid projectId, UpdateActionRequest req, Guid userId, UserRole role, string? ip, string? ua)
    {
        var a = await db.ActionItems.FirstOrDefaultAsync(x => x.Id == actionId && x.ProjectId == projectId) ?? throw new NotFoundException("Action item");

        // B-005: assignee ownership check. Caller must be the action's
        // assignee OR ProjectManager+ on the project (PM-level override:
        // PMs can correct or close any action regardless of assignment,
        // which is needed for re-assignments and stale-action cleanup).
        // Unassigned actions can be updated by any TaskTeamMember+
        // (the floor enforced at the controller) so the gap-of-ownership
        // case doesn't grind.
        if (a.AssigneeId.HasValue
            && a.AssigneeId.Value != userId
            && !CdeStateMachine.HasMinimumRole(role, UserRole.ProjectManager))
            throw new ForbiddenException("Only the action's assignee or a ProjectManager+ may update it");

        if (req.Title != null)       a.Title       = req.Title;
        if (req.Description != null) a.Description = req.Description;
        if (req.Priority != null)    a.Priority    = req.Priority.Value;
        if (req.Status != null)      { a.Status = req.Status.Value; if (req.Status == ActionStatus.Closed) a.ClosedAt = DateTime.UtcNow; }
        if (req.AssigneeId != null)  a.AssigneeId  = req.AssigneeId;
        if (req.DueDate != null)     a.DueDate     = req.DueDate;
        await db.SaveChangesAsync();
        await audit.WriteAsync(userId, "action.updated", "ActionItem", actionId.ToString(), projectId, ip: ip, ua: ua);
        return a;
    }
}
