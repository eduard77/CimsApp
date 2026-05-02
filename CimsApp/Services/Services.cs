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
    CimsApp.Services.Auth.ILoginAttemptTracker loginTracker,
    AuditService audit)
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
        // Wrap the User save and the Invitation consume in one
        // transaction. Without this wrap, a process crash between
        // the two SaveChanges calls would leave a User created
        // (visible to login) but the Invitation still consumable —
        // a second registration with a different email could mint a
        // second User from the same token, violating "one
        // invitation, one user". On SQL Server the transaction
        // serialises the two writes into a single atomic unit; on
        // the EF in-memory provider it's a no-op (no real isolation
        // semantics there) but the code shape stays correct for
        // production. Order preserved: User save first so an FK /
        // duplicate-index failure leaves the Invitation available
        // for a retry rather than burning it.
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await invitations.MarkConsumedAsync(invitation.Id, user.Id);
        await tx.CommitAsync();

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
        // Refresh tokens are opaque random hex (CreateRefreshAsync below
        // mints `Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")`,
        // 64 chars), NOT JWTs. The pre-fix code called Validate(token,
        // RefreshSecret) which JWT-validated the input and always returned
        // null for opaque tokens — so /auth/refresh returned 401
        // INVALID_REFRESH on EVERY call since the initial commit. Fixed
        // 2026-04-29 by smoke-testing the bootstrap → register → login →
        // refresh flow against real SQL Server.
        //
        // The DB lookup IS the authentication: only the server holds these
        // strings, and they're rotated on every refresh (current row's
        // RevokedAt is set, a new row is minted). Defense-in-depth checks
        // remain: the row must exist, must be IsActive (not revoked, not
        // expired), and the User must be findable. The JWT-validation
        // step was redundant given those checks.
        if (string.IsNullOrEmpty(token))
            throw new AppException("Invalid refresh token", 401, "INVALID_REFRESH");

        // Pre-auth: refresh endpoint has no access token, tenant filter bypassed.
        var stored = await db.RefreshTokens.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Token == token)
            ?? throw new AppException("Invalid refresh token", 401, "INVALID_REFRESH");
        if (!stored.IsActive) throw new AppException("Token revoked", 401, "TOKEN_REVOKED");
        stored.RevokedAt = DateTime.UtcNow;
        // Pre-auth (refresh endpoint has no access token): bypass User filter.
        var user   = await db.Users.IgnoreQueryFilters().Include(u => u.Organisation)
            .FirstOrDefaultAsync(u => u.Id == stored.UserId)
            ?? throw new AppException("Invalid refresh token", 401, "INVALID_REFRESH");
        var access = GenerateAccess(user);
        var newRef = await CreateRefreshAsync(stored.UserId, null, null);
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
    /// B-001 / ADR-0014: self-service revoke. Bumps the caller's
    /// <see cref="User.TokenInvalidationCutoff"/> AND revokes every
    /// active refresh token (B-019). The caller's current access
    /// token is invalidated by the cutoff; their refresh tokens by
    /// `RevokedAt`, so a multi-device user can't refresh on another
    /// device after calling this. "Log out everywhere" actually means
    /// everywhere.
    /// </summary>
    public async Task RevokeOwnTokensAsync(Guid actorId)
    {
        // IgnoreQueryFilters because the caller IS the target — there
        // is no tenant-scope concern when revoking your own sessions.
        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == actorId)
            ?? throw new NotFoundException("User");
        user.TokenInvalidationCutoff = DateTime.UtcNow;
        var swept = await SweepActiveRefreshTokensAsync(actorId);
        // B-021: structured audit-twin event. Per-row audit is
        // captured by AuditInterceptor; this adds the semantic
        // action name + refresh-token sweep count for log-discoverable
        // forensics. Added to the change tracker BEFORE SaveChanges so
        // both the User update and the structured event commit in one
        // transaction.
        await audit.WriteAsync(actorId, "auth.user_self_revoke",
            "User", actorId.ToString(),
            detail: new { refreshTokensSwept = swept });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// B-001 / ADR-0014: admin revoke. Bumps the target user's
    /// <see cref="User.TokenInvalidationCutoff"/> AND revokes every
    /// active refresh token (B-019). The User lookup respects the
    /// tenant query filter — OrgAdmin can only target users in their
    /// own organisation; SuperAdmin bypasses the filter via
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
        var swept = await SweepActiveRefreshTokensAsync(userId);
        // B-021. actorId is the admin's UserId from the tenant
        // context; targetUserId is the user being revoked. Audit-twin
        // atomic with the User + RefreshToken updates via the single
        // SaveChanges below.
        await audit.WriteAsync(tenant.UserId,
            "auth.user_admin_revoke", "User", userId.ToString(),
            detail: new { targetUserId = userId, refreshTokensSwept = swept });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// B-001 / ADR-0014: deactivate a user AND revoke their tokens
    /// (B-019 sweeps refresh tokens too). The IsActive=false flip is
    /// the primary kill-switch (rejected at every authenticated
    /// request via `TokenRevocation.IsRevoked`); cutoff bump and
    /// refresh-token sweep are belt-and-braces, also surviving any
    /// future reactivate path.
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
        var swept = await SweepActiveRefreshTokensAsync(userId);
        // B-021. The IsActive flip + cutoff bump + refresh sweep are
        // all captured by AuditInterceptor as per-row Updates; this
        // structured event ties them together with a single
        // discoverable action name. Atomic with the entity writes via
        // the single SaveChanges below.
        await audit.WriteAsync(tenant.UserId,
            "auth.user_deactivated", "User", userId.ToString(),
            detail: new { targetUserId = userId, refreshTokensSwept = swept });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// B-019: bulk-revoke every active refresh token for a user.
    /// Sets `RevokedAt = UtcNow` on each row, which flips the
    /// computed `RefreshToken.IsActive` to false and causes
    /// `RefreshAsync` to throw TOKEN_REVOKED on subsequent attempts.
    /// IgnoreQueryFilters because refresh-token rows route through
    /// the User filter and this helper is called from contexts
    /// (self-service revoke; SuperAdmin admin revoke) where bypassing
    /// is correct. The User-level revoke methods above are
    /// responsible for tenant scoping at the User row; once the user
    /// is found, sweeping their refresh tokens is unconditional.
    /// SaveChanges is batched into the calling method's
    /// SaveChangesAsync.
    /// </summary>
    private async Task<int> SweepActiveRefreshTokensAsync(Guid userId)
    {
        var now = DateTime.UtcNow;
        var active = await db.RefreshTokens.IgnoreQueryFilters()
            .Where(r => r.UserId == userId && r.RevokedAt == null && r.ExpiresAt > now)
            .ToListAsync();
        foreach (var t in active)
            t.RevokedAt = now;
        return active.Count;
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
public class InvitationService(CimsDbContext db, AuditService audit)
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

        // Audit-twin. Bootstrap invitations are minted by anonymous
        // org-creation flow (no caller); attribute the audit row to
        // Guid.Empty in that case so the structured event still
        // exists and can be searched. Email-bind presence is
        // captured as a flag rather than the email itself — the
        // address is already on the row's BeforeValue/AfterValue
        // via the AuditInterceptor and including it here would
        // double up. Atomic with the Invitation insert via the
        // single SaveChanges below.
        await audit.WriteAsync(createdById,
            "invitation.created", "Invitation",
            invitation.Id.ToString(),
            detail: new
            {
                organisationId    = organisationId,
                isBootstrap       = isBootstrap,
                expiresAt         = invitation.ExpiresAt,
                hasEmailBind      = invitation.Email is not null,
            });
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
        // Audit-twin only when the consume actually landed —
        // a race where another caller already consumed the token
        // returns rows==0 and we don't emit (no work happened, no
        // audit). Actor = the consumer (the new User who just
        // registered). Explicit SaveChanges here because the
        // Invitation update was an ExecuteUpdateAsync (which doesn't
        // go through the change tracker), so adding the audit row
        // alone would leave it unsaved without this call.
        if (rows == 1)
        {
            await audit.WriteAsync(consumerUserId, "invitation.consumed",
                "Invitation", invitationId.ToString(),
                detail: new { consumerUserId });
            await db.SaveChangesAsync();
        }
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
        await audit.WriteAsync(userId, tenant.IsSuperAdmin ? "project.created.superadmin_bypass" : "project.created", "Project", p.Id.ToString(), p.Id, ip: ip, ua: ua);
        await db.SaveChangesAsync();
        return p;
    }

    public async Task AddMemberAsync(Guid projectId, Guid userId, UserRole role, Guid actorId)
    {
        // SR-S0-05: verify the new member belongs to the project's
        // appointing organisation. Without this check, a PM in Org A
        // could add a User from Org B as a member — the row would
        // persist but the user's tenant filter (Project filtered by
        // AppointingPartyId == tenant.OrganisationId) would hide the
        // project from them, leaving an orphan ProjectMember row.
        // v1.0 model (ADR-0012): a project is owned by one org and
        // its members are users in that org. B2B contractor
        // membership via `ProjectAppointment` is a future post-v1.0
        // expansion (the check would widen to "member's org is the
        // AppointingParty OR is an appointed contractor on this
        // project"); for v1.0 the strict same-org rule is correct.
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId)
            ?? throw new NotFoundException("Project");
        var newMember = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new NotFoundException("User");
        if (newMember.OrganisationId != project.AppointingPartyId)
            throw new ValidationException(
                ["The user must belong to the project's appointing organisation"]);

        var existing = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId);
        var wasReactivated = existing != null;
        if (existing != null) { existing.Role = role; existing.IsActive = true; }
        else db.ProjectMembers.Add(new ProjectMember { ProjectId = projectId, UserId = userId, Role = role });
        // Audit-twin: AuditInterceptor captures the per-row Insert /
        // Update on ProjectMember; this structured event names the
        // semantic action and carries the granted role + reactivation
        // flag so a "who joined / was promoted on this project"
        // audit-log search lands on a single discoverable row. Atomic
        // with the entity write via the single SaveChanges below.
        await audit.WriteAsync(actorId, "project.member_added",
            "ProjectMember", $"{projectId}:{userId}", projectId,
            detail: new
            {
                targetUserId = userId,
                grantedRole  = role.ToString(),
                reactivated  = wasReactivated,
            });
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
        await audit.WriteAsync(actorId, "cbs.imported", "CostBreakdownItem", projectId.ToString(), projectId,
            detail: new { rowCount = entities.Count }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);

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
        // Per-row Update audit is captured automatically by AuditInterceptor;
        // the explicit cbs.line_budget_set entry below carries the
        // before/after pair as a structured detail for cost-domain
        // reporting (separate from the field-level audit). Atomic with
        // the entity write via the single SaveChanges below.
        await audit.WriteAsync(actorId, "cbs.line_budget_set", "CostBreakdownItem",
            itemId.ToString(), projectId,
            detail: new { previous, current = budget }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
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
        await audit.WriteAsync(actorId, "cbs.line_schedule_set", "CostBreakdownItem",
            itemId.ToString(), projectId,
            detail: new
            {
                previousStart, previousEnd,
                currentStart = req.ScheduledStart,
                currentEnd   = req.ScheduledEnd,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
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
        await audit.WriteAsync(actorId, "cbs.line_progress_set", "CostBreakdownItem",
            itemId.ToString(), projectId,
            detail: new { previous, current = req.PercentComplete }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
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
        await audit.WriteAsync(actorId, "commitment.created", "Commitment",
            commitment.Id.ToString(), projectId,
            detail: new
            {
                cbsLineId = line.Id,
                type      = req.Type.ToString(),
                amount    = req.Amount,
                reference = commitment.Reference,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
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
        await audit.WriteAsync(actorId, "cost_period.opened", "CostPeriod",
            period.Id.ToString(), projectId,
            detail: new { label = period.Label, period.StartDate, period.EndDate, period.PlannedCashflow },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
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
        await audit.WriteAsync(actorId, "cost_period.baseline_set", "CostPeriod",
            period.Id.ToString(), projectId,
            detail: new { period.Label, previous, current = req.PlannedCashflow },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
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
        await audit.WriteAsync(actorId, "cost_period.closed", "CostPeriod",
            period.Id.ToString(), projectId,
            detail: new { label = period.Label }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
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
        await audit.WriteAsync(actorId, "actual_cost.recorded", "ActualCost",
            actual.Id.ToString(), projectId,
            detail: new
            {
                cbsLineId = line.Id,
                periodId  = period.Id,
                amount    = req.Amount,
                reference = req.Reference,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
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
        await db.SaveChangesAsync(ct);

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
        await audit.WriteAsync(actorId, "payment_certificate.draft_updated",
            "PaymentCertificate", cert.Id.ToString(), projectId,
            detail: new
            {
                number             = cert.CertificateNumber,
                valuation          = cert.CumulativeValuation,
                materialsOnSite    = cert.CumulativeMaterialsOnSite,
                retentionPercent   = cert.RetentionPercent,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);

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
        await db.SaveChangesAsync(ct);

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
        await audit.WriteAsync(actorId, "variation.raised", "Variation",
            v.Id.ToString(), projectId,
            detail: new
            {
                number          = v.VariationNumber,
                costImpact      = req.EstimatedCostImpact,
                timeImpactDays  = req.EstimatedTimeImpactDays,
                cbsLineId       = req.CostBreakdownItemId,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
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
        await db.SaveChangesAsync(ct);
    }
}

// ── Risk & Opportunities ──────────────────────────────────────────────────────
// PAFM-SD Appendix F.3 (S2 module). T-S2-04 ships the core lifecycle:
// Create, Update, Close, each with audit-twin events committed atomically
// with the entity write per the post-S1 PR #33 pattern.
public class RisksService(CimsDbContext db, AuditService audit)
{
    /// <summary>
    /// Register a new Risk in state <see cref="RiskStatus.Identified"/>.
    /// Probability and Impact are validated to 1..5 (the standard 5×5
    /// matrix); Score is the persisted product (denormalised for fast
    /// heat-map queries). CategoryId, if set, must belong to the same
    /// project (cross-tenant or wrong-project IDs 404 here, same
    /// guard pattern as VariationsService.RaiseAsync).
    /// </summary>
    public async Task<Risk> CreateAsync(
        Guid projectId, CreateRiskRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ValidationException(["Title is required"]);
        ValidateMatrixInputs(req.Probability, req.Impact);

        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        if (req.CategoryId.HasValue)
        {
            var catOk = await db.RiskCategories.AnyAsync(
                c => c.Id == req.CategoryId.Value && c.ProjectId == projectId, ct);
            if (!catOk) throw new NotFoundException("RiskCategory");
        }
        if (req.OwnerId.HasValue)
        {
            var ownerOk = await db.Users.AnyAsync(u => u.Id == req.OwnerId.Value, ct);
            if (!ownerOk) throw new NotFoundException("User");
        }

        var risk = new Risk
        {
            ProjectId         = projectId,
            CategoryId        = req.CategoryId,
            Title             = req.Title.Trim(),
            Description       = req.Description,
            Probability       = req.Probability,
            Impact            = req.Impact,
            Score             = req.Probability * req.Impact,
            Status            = RiskStatus.Identified,
            OwnerId           = req.OwnerId,
            ResponseStrategy  = req.ResponseStrategy,
            ResponsePlan      = req.ResponsePlan,
            ContingencyAmount = req.ContingencyAmount,
        };
        db.Risks.Add(risk);
        await audit.WriteAsync(actorId, "risk.created", "Risk",
            risk.Id.ToString(), projectId,
            detail: new
            {
                title      = risk.Title,
                score      = risk.Score,
                categoryId = risk.CategoryId,
                ownerId    = risk.OwnerId,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return risk;
    }

    /// <summary>
    /// Partial update on a non-Closed Risk. Any field set on the
    /// request is applied; null = "leave unchanged" (so v1.0 cannot
    /// explicitly clear nullable fields back to null — accepted
    /// limitation, revisit with a tri-state DTO if real workflows
    /// demand it). Setting Status to <see cref="RiskStatus.Closed"/>
    /// is rejected here — callers must use <see cref="CloseAsync"/>
    /// so the audit history carries a distinct `risk.closed` event
    /// rather than a generic `risk.updated`.
    /// </summary>
    public async Task<Risk> UpdateAsync(
        Guid projectId, Guid riskId, UpdateRiskRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var risk = await db.Risks
            .FirstOrDefaultAsync(r => r.Id == riskId && r.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Risk");

        if (risk.Status == RiskStatus.Closed)
            throw new ConflictException("Risk is Closed; cannot update");
        if (req.Status == RiskStatus.Closed)
            throw new ConflictException("Use CloseAsync to set Status to Closed");

        var oldScore = risk.Score;
        var changed = new List<string>();

        if (req.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                throw new ValidationException(["Title cannot be empty"]);
            risk.Title = req.Title.Trim();
            changed.Add("Title");
        }
        if (req.Description is not null) { risk.Description = req.Description; changed.Add("Description"); }

        if (req.CategoryId.HasValue)
        {
            var catOk = await db.RiskCategories.AnyAsync(
                c => c.Id == req.CategoryId.Value && c.ProjectId == projectId, ct);
            if (!catOk) throw new NotFoundException("RiskCategory");
            risk.CategoryId = req.CategoryId.Value;
            changed.Add("CategoryId");
        }

        if (req.Probability.HasValue || req.Impact.HasValue)
        {
            var newP = req.Probability ?? risk.Probability;
            var newI = req.Impact ?? risk.Impact;
            ValidateMatrixInputs(newP, newI);
            if (req.Probability.HasValue) { risk.Probability = newP; changed.Add("Probability"); }
            if (req.Impact.HasValue)      { risk.Impact      = newI; changed.Add("Impact"); }
            risk.Score = newP * newI;
            if (oldScore != risk.Score)   changed.Add("Score");
        }

        if (req.Status.HasValue)         { risk.Status = req.Status.Value; changed.Add("Status"); }

        if (req.OwnerId.HasValue)
        {
            var ownerOk = await db.Users.AnyAsync(u => u.Id == req.OwnerId.Value, ct);
            if (!ownerOk) throw new NotFoundException("User");
            risk.OwnerId = req.OwnerId.Value;
            changed.Add("OwnerId");
        }

        if (req.ResponseStrategy.HasValue) { risk.ResponseStrategy = req.ResponseStrategy.Value; changed.Add("ResponseStrategy"); }
        if (req.ResponsePlan is not null)  { risk.ResponsePlan = req.ResponsePlan; changed.Add("ResponsePlan"); }
        if (req.ContingencyAmount.HasValue){ risk.ContingencyAmount = req.ContingencyAmount.Value; changed.Add("ContingencyAmount"); }

        if (changed.Count == 0)
            throw new ValidationException(["No updatable fields provided"]);

        await audit.WriteAsync(actorId, "risk.updated", "Risk",
            risk.Id.ToString(), projectId,
            detail: new
            {
                changedFields = changed,
                scoreBefore   = oldScore,
                scoreAfter    = risk.Score,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return risk;
    }

    /// <summary>
    /// Move a Risk to <see cref="RiskStatus.Closed"/>. Idempotent on
    /// already-Closed risks (rejected as ConflictException so callers
    /// don't silently double-emit close events).
    /// </summary>
    public async Task<Risk> CloseAsync(
        Guid projectId, Guid riskId, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var risk = await db.Risks
            .FirstOrDefaultAsync(r => r.Id == riskId && r.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Risk");

        if (risk.Status == RiskStatus.Closed)
            throw new ConflictException("Risk is already Closed");

        var previousStatus = risk.Status;
        risk.Status = RiskStatus.Closed;

        await audit.WriteAsync(actorId, "risk.closed", "Risk",
            risk.Id.ToString(), projectId,
            detail: new { previousStatus = previousStatus.ToString() },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return risk;
    }

    private static void ValidateMatrixInputs(int probability, int impact)
    {
        var errors = new List<string>();
        if (probability < 1 || probability > 5) errors.Add("Probability must be 1..5");
        if (impact < 1 || impact > 5)            errors.Add("Impact must be 1..5");
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    /// <summary>
    /// List active risks on a project, ordered by Score descending then
    /// CreatedAt ascending — natural register / heat-map listing.
    /// Cross-tenant projectIds 404 via the query filter.
    /// </summary>
    public async Task<List<Risk>> ListAsync(Guid projectId, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        return await db.Risks
            .Where(r => r.ProjectId == projectId && r.IsActive)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Build the 25-cell 5×5 P×I matrix for the project — one cell per
    /// (Probability, Impact) coordinate, with RiskIds inside each cell.
    /// Closed risks are excluded (heat-maps show the live register only).
    /// </summary>
    public async Task<List<CimsApp.Core.RiskMatrixCell>> GetMatrixAsync(
        Guid projectId, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        var live = await db.Risks
            .Where(r => r.ProjectId == projectId && r.IsActive && r.Status != RiskStatus.Closed)
            .ToListAsync(ct);
        return CimsApp.Core.RiskMatrix.Build(live);
    }

    /// <summary>
    /// Record a qualitative assessment on a Risk (T-S2-06). Sets
    /// QualitativeNotes, stamps AssessedAt = UtcNow, AssessedById =
    /// caller. Re-assessment overwrites the previous notes (history
    /// captured passively via the AuditInterceptor's per-row
    /// before/after JSON; an explicit assessment-history entity is a
    /// v1.1 candidate). Bumps Status from Identified to Assessed if
    /// currently Identified — other statuses stay as they were.
    /// Already-Closed risks rejected.
    /// </summary>
    public async Task<Risk> RecordQualitativeAssessmentAsync(
        Guid projectId, Guid riskId, RecordQualitativeAssessmentRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Notes))
            throw new ValidationException(["Notes is required"]);

        var risk = await db.Risks
            .FirstOrDefaultAsync(r => r.Id == riskId && r.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Risk");

        if (risk.Status == RiskStatus.Closed)
            throw new ConflictException("Risk is Closed; cannot record assessment");

        var statusChanged = risk.Status == RiskStatus.Identified;
        risk.QualitativeNotes = req.Notes;
        risk.AssessedAt       = DateTime.UtcNow;
        risk.AssessedById     = actorId;
        if (statusChanged) risk.Status = RiskStatus.Assessed;

        await audit.WriteAsync(actorId, "risk.qualitative_assessed", "Risk",
            risk.Id.ToString(), projectId,
            detail: new
            {
                statusTransition = statusChanged ? "Identified -> Assessed" : null,
                notesLength      = req.Notes.Length,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return risk;
    }

    /// <summary>
    /// Record a quantitative assessment on a Risk (T-S2-07). Sets the
    /// 3-point estimate (BestCase, MostLikely, WorstCase) and the
    /// chosen Distribution shape. Validates BestCase ≤ MostLikely ≤
    /// WorstCase per the S2 kickoff Top-3-risks mitigation. Negative
    /// values rejected (Risk impacts are non-negative; opportunity
    /// "negative impact" semantics are a v1.1 backlog item B-029).
    /// Re-assessment overwrites; passive history via AuditInterceptor
    /// per-row JSON. Already-Closed risks rejected.
    /// </summary>
    public async Task<Risk> RecordQuantitativeAssessmentAsync(
        Guid projectId, Guid riskId, RecordQuantitativeAssessmentRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (req.BestCase   < 0) errors.Add("BestCase must be >= 0");
        if (req.MostLikely < 0) errors.Add("MostLikely must be >= 0");
        if (req.WorstCase  < 0) errors.Add("WorstCase must be >= 0");
        if (req.BestCase > req.MostLikely)   errors.Add("BestCase must be <= MostLikely");
        if (req.MostLikely > req.WorstCase)  errors.Add("MostLikely must be <= WorstCase");
        if (errors.Count > 0) throw new ValidationException(errors);

        var risk = await db.Risks
            .FirstOrDefaultAsync(r => r.Id == riskId && r.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Risk");

        if (risk.Status == RiskStatus.Closed)
            throw new ConflictException("Risk is Closed; cannot record assessment");

        risk.BestCase     = req.BestCase;
        risk.MostLikely   = req.MostLikely;
        risk.WorstCase    = req.WorstCase;
        risk.Distribution = req.Distribution;

        await audit.WriteAsync(actorId, "risk.quantitative_assessed", "Risk",
            risk.Id.ToString(), projectId,
            detail: new
            {
                bestCase     = req.BestCase,
                mostLikely   = req.MostLikely,
                worstCase    = req.WorstCase,
                distribution = req.Distribution.ToString(),
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return risk;
    }

    /// <summary>
    /// Run cost-side Monte Carlo simulation across the project's
    /// quantified risks (T-S2-08, PAFM-SD F.3 fourth bullet — cost
    /// half only per CR-004, schedule-side is v1.1 / B-028).
    /// Selects active non-Closed risks that have a complete 3-point
    /// estimate + Distribution; risks lacking any of the four are
    /// silently excluded (the analyst hasn't quantified them yet).
    /// Closed risks excluded — running MC against historical
    /// realised exposure is not what this view is for.
    /// </summary>
    public async Task<CimsApp.Core.MonteCarloResult> RunMonteCarloAsync(
        Guid projectId, int iterations, int? seed = null, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var risks = await db.Risks
            .Where(r => r.ProjectId == projectId
                     && r.IsActive
                     && r.Status != RiskStatus.Closed
                     && r.BestCase   != null
                     && r.MostLikely != null
                     && r.WorstCase  != null
                     && r.Distribution != null)
            .ToListAsync(ct);

        var inputs = risks.Select(r => new CimsApp.Core.MonteCarloInput
        {
            Probability  = r.Probability,
            BestCase     = (double)r.BestCase!.Value,
            MostLikely   = (double)r.MostLikely!.Value,
            WorstCase    = (double)r.WorstCase!.Value,
            Distribution = r.Distribution!.Value,
        }).ToList();

        return CimsApp.Core.MonteCarlo.Simulate(inputs, iterations, seed ?? Random.Shared.Next());
    }

    /// <summary>
    /// Record a contingency drawdown against a Risk (T-S2-09).
    /// Amount must be > 0; OccurredAt is the date the cost was
    /// incurred (analyst-supplied; distinct from row-write time).
    /// Cumulative drawdowns may exceed Risk.ContingencyAmount —
    /// over-runs are tracked honestly rather than blocked, matching
    /// real construction practice. Already-Closed risks rejected.
    /// Cross-module link to specific Commitment / ActualCost rows
    /// is deferred to v1.1 (B-030 per CR-004); v1.0 takes a free-text
    /// Reference for traceability.
    /// </summary>
    public async Task<RiskDrawdown> RecordDrawdownAsync(
        Guid projectId, Guid riskId, RecordRiskDrawdownRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (req.Amount <= 0)
            throw new ValidationException(["Amount must be > 0"]);

        var risk = await db.Risks
            .FirstOrDefaultAsync(r => r.Id == riskId && r.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Risk");

        if (risk.Status == RiskStatus.Closed)
            throw new ConflictException("Risk is Closed; cannot record drawdown");

        var drawdown = new RiskDrawdown
        {
            ProjectId    = projectId,
            RiskId       = riskId,
            Amount       = req.Amount,
            OccurredAt   = req.OccurredAt,
            Reference    = req.Reference,
            Note         = req.Note,
            RecordedById = actorId,
        };
        db.RiskDrawdowns.Add(drawdown);

        await audit.WriteAsync(actorId, "risk.drawdown_recorded", "RiskDrawdown",
            drawdown.Id.ToString(), projectId,
            detail: new
            {
                riskId    = riskId,
                amount    = req.Amount,
                occurredAt = req.OccurredAt,
                reference = req.Reference,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return drawdown;
    }

    /// <summary>List drawdowns recorded against a Risk, oldest first.
    /// Cross-tenant 404 via the query filter.</summary>
    public async Task<List<RiskDrawdown>> ListDrawdownsAsync(
        Guid projectId, Guid riskId, CancellationToken ct = default)
    {
        var riskExists = await db.Risks.AnyAsync(r => r.Id == riskId && r.ProjectId == projectId, ct);
        if (!riskExists) throw new NotFoundException("Risk");
        return await db.RiskDrawdowns
            .Where(d => d.RiskId == riskId)
            .OrderBy(d => d.OccurredAt)
            .ThenBy(d => d.CreatedAt)
            .ToListAsync(ct);
    }
}

// ── Stakeholder & Communications ──────────────────────────────────────────────
// PAFM-SD Appendix F.4 (S3 module). T-S3-03 ships the stakeholder
// register lifecycle: Create / Update / Deactivate, each with audit-twin
// events committed atomically with the entity write per the post-S1
// PR #33 pattern.
public class StakeholdersService(CimsDbContext db, AuditService audit)
{
    /// <summary>
    /// Register a new Stakeholder. Validates Name + Power/Interest in
    /// 1..5; computes Score = P×I and Mendelow quadrant
    /// EngagementApproach (3-as-midpoint) unless the caller supplies
    /// an explicit override.
    /// </summary>
    public async Task<Stakeholder> CreateAsync(
        Guid projectId, CreateStakeholderRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ValidationException(["Name is required"]);
        ValidateMatrixInputs(req.Power, req.Interest);

        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var stakeholder = new Stakeholder
        {
            ProjectId          = projectId,
            Name               = req.Name.Trim(),
            Organisation       = req.Organisation,
            Role               = req.Role,
            Email              = req.Email,
            Phone              = req.Phone,
            Power              = req.Power,
            Interest           = req.Interest,
            Score              = req.Power * req.Interest,
            EngagementApproach = req.EngagementApproach
                                  ?? ComputeApproach(req.Power, req.Interest),
            EngagementNotes    = req.EngagementNotes,
        };
        db.Stakeholders.Add(stakeholder);
        await audit.WriteAsync(actorId, "stakeholder.created", "Stakeholder",
            stakeholder.Id.ToString(), projectId,
            detail: new
            {
                name     = stakeholder.Name,
                score    = stakeholder.Score,
                approach = stakeholder.EngagementApproach.ToString(),
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return stakeholder;
    }

    /// <summary>
    /// Partial update on an active Stakeholder. Null = "leave
    /// unchanged" (same v1.0 limitation as RisksService.UpdateAsync).
    /// Power/Interest change recomputes Score and, unless the caller
    /// also sets EngagementApproach, recomputes the Mendelow quadrant.
    /// Already-deactivated rows rejected with ConflictException so
    /// callers explicitly reactivate via a future endpoint (deferred).
    /// </summary>
    public async Task<Stakeholder> UpdateAsync(
        Guid projectId, Guid stakeholderId, UpdateStakeholderRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var s = await db.Stakeholders
            .FirstOrDefaultAsync(x => x.Id == stakeholderId && x.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Stakeholder");

        if (!s.IsActive)
            throw new ConflictException("Stakeholder is deactivated; cannot update");

        var changed = new List<string>();
        var oldScore = s.Score;

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                throw new ValidationException(["Name cannot be empty"]);
            s.Name = req.Name.Trim();
            changed.Add("Name");
        }
        if (req.Organisation is not null)    { s.Organisation = req.Organisation;       changed.Add("Organisation"); }
        if (req.Role is not null)            { s.Role = req.Role;                       changed.Add("Role"); }
        if (req.Email is not null)           { s.Email = req.Email;                     changed.Add("Email"); }
        if (req.Phone is not null)           { s.Phone = req.Phone;                     changed.Add("Phone"); }
        if (req.EngagementNotes is not null) { s.EngagementNotes = req.EngagementNotes; changed.Add("EngagementNotes"); }

        var matrixChanged = req.Power.HasValue || req.Interest.HasValue;
        if (matrixChanged)
        {
            var newP = req.Power    ?? s.Power;
            var newI = req.Interest ?? s.Interest;
            ValidateMatrixInputs(newP, newI);
            if (req.Power.HasValue)    { s.Power = newP;    changed.Add("Power"); }
            if (req.Interest.HasValue) { s.Interest = newI; changed.Add("Interest"); }
            s.Score = newP * newI;
            if (oldScore != s.Score) changed.Add("Score");
        }

        if (req.EngagementApproach.HasValue)
        {
            s.EngagementApproach = req.EngagementApproach.Value;
            changed.Add("EngagementApproach");
        }
        else if (matrixChanged)
        {
            // Auto-recompute approach when matrix changed and the
            // caller didn't explicitly set one.
            var newApproach = ComputeApproach(s.Power, s.Interest);
            if (newApproach != s.EngagementApproach)
            {
                s.EngagementApproach = newApproach;
                changed.Add("EngagementApproach");
            }
        }

        if (changed.Count == 0)
            throw new ValidationException(["No updatable fields provided"]);

        await audit.WriteAsync(actorId, "stakeholder.updated", "Stakeholder",
            s.Id.ToString(), projectId,
            detail: new
            {
                changedFields = changed,
                scoreBefore   = oldScore,
                scoreAfter    = s.Score,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return s;
    }

    /// <summary>Soft-delete: set IsActive = false. Idempotent
    /// rejection on already-deactivated.</summary>
    public async Task<Stakeholder> DeactivateAsync(
        Guid projectId, Guid stakeholderId, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var s = await db.Stakeholders
            .FirstOrDefaultAsync(x => x.Id == stakeholderId && x.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Stakeholder");

        if (!s.IsActive)
            throw new ConflictException("Stakeholder is already deactivated");

        s.IsActive = false;
        await audit.WriteAsync(actorId, "stakeholder.deactivated", "Stakeholder",
            s.Id.ToString(), projectId,
            detail: new { name = s.Name }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return s;
    }

    /// <summary>List active stakeholders on a project, ordered by
    /// Score desc then Name asc — "highest priority first" listing.
    /// </summary>
    public async Task<List<Stakeholder>> ListAsync(Guid projectId, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        return await db.Stakeholders
            .Where(s => s.ProjectId == projectId && s.IsActive)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Build the 25-cell 5×5 Power/Interest matrix for the project —
    /// one cell per (Power, Interest) coordinate with StakeholderIds.
    /// Excludes deactivated rows (matrix is a live-register view).
    /// </summary>
    public async Task<List<CimsApp.Core.StakeholderMatrixCell>> GetMatrixAsync(
        Guid projectId, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        var live = await db.Stakeholders
            .Where(s => s.ProjectId == projectId && s.IsActive)
            .ToListAsync(ct);
        return CimsApp.Core.StakeholderMatrix.Build(live);
    }

    /// <summary>Mendelow quadrant from Power/Interest at fixed
    /// 3-as-midpoint. v1.1 candidate: per-tenant threshold override
    /// via S14 Admin Console.</summary>
    public static EngagementApproach ComputeApproach(int power, int interest) =>
        (power >= 3, interest >= 3) switch
        {
            (true,  true)  => EngagementApproach.ManageClosely,
            (true,  false) => EngagementApproach.KeepSatisfied,
            (false, true)  => EngagementApproach.KeepInformed,
            (false, false) => EngagementApproach.Monitor,
        };

    private static void ValidateMatrixInputs(int power, int interest)
    {
        var errors = new List<string>();
        if (power < 1 || power > 5)       errors.Add("Power must be 1..5");
        if (interest < 1 || interest > 5) errors.Add("Interest must be 1..5");
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    /// <summary>
    /// Record one interaction with a stakeholder (T-S3-06). Summary
    /// required; ActionsAgreed optional. Cross-tenant /
    /// wrong-project stakeholderIds 404 via the query filter.
    /// </summary>
    public async Task<EngagementLog> RecordEngagementAsync(
        Guid projectId, Guid stakeholderId, RecordEngagementRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Summary))
            throw new ValidationException(["Summary is required"]);

        var stakeholderExists = await db.Stakeholders.AnyAsync(
            s => s.Id == stakeholderId && s.ProjectId == projectId, ct);
        if (!stakeholderExists) throw new NotFoundException("Stakeholder");

        var entry = new EngagementLog
        {
            ProjectId     = projectId,
            StakeholderId = stakeholderId,
            Type          = req.Type,
            OccurredAt    = req.OccurredAt,
            Summary       = req.Summary.Trim(),
            ActionsAgreed = req.ActionsAgreed,
            RecordedById  = actorId,
        };
        db.EngagementLogs.Add(entry);

        await audit.WriteAsync(actorId, "engagement.recorded", "EngagementLog",
            entry.Id.ToString(), projectId,
            detail: new
            {
                stakeholderId = stakeholderId,
                type          = req.Type.ToString(),
                occurredAt    = req.OccurredAt,
                hasActions    = !string.IsNullOrWhiteSpace(req.ActionsAgreed),
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>List the most-recent engagements with a stakeholder,
    /// newest first, capped at 200. Per the S3 kickoff Top-3 risks
    /// throughput mitigation; full pagination is a v1.1 candidate.
    /// Cross-tenant 404 via the query filter.</summary>
    public async Task<List<EngagementLog>> ListEngagementsAsync(
        Guid projectId, Guid stakeholderId, CancellationToken ct = default)
    {
        var stakeholderExists = await db.Stakeholders.AnyAsync(
            s => s.Id == stakeholderId && s.ProjectId == projectId, ct);
        if (!stakeholderExists) throw new NotFoundException("Stakeholder");

        return await db.EngagementLogs
            .Where(g => g.StakeholderId == stakeholderId)
            .OrderByDescending(g => g.OccurredAt)
            .ThenByDescending(g => g.CreatedAt)
            .Take(200)
            .ToListAsync(ct);
    }
}

/// <summary>
/// CommunicationsService — the project-level communications matrix
/// (T-S3-07, PAFM-SD F.4 fourth bullet — "what / who / when / how").
/// Mirrors StakeholdersService shape: Create / Update / Deactivate
/// + List, audit-twin pattern, cross-tenant 404 via query filter.
/// </summary>
public class CommunicationsService(CimsDbContext db, AuditService audit)
{
    public async Task<CommunicationItem> CreateAsync(
        Guid projectId, CreateCommunicationItemRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        ValidateCreate(req);
        await EnsureOwnerIsProjectMemberAsync(projectId, req.OwnerId, ct);

        var item = new CommunicationItem
        {
            ProjectId = projectId,
            ItemType  = req.ItemType.Trim(),
            Audience  = req.Audience.Trim(),
            Frequency = req.Frequency,
            Channel   = req.Channel,
            OwnerId   = req.OwnerId,
            Notes     = req.Notes,
        };
        db.CommunicationItems.Add(item);

        await audit.WriteAsync(actorId, "communication.created", "CommunicationItem",
            item.Id.ToString(), projectId,
            detail: new
            {
                itemType  = item.ItemType,
                audience  = item.Audience,
                frequency = item.Frequency.ToString(),
                channel   = item.Channel.ToString(),
                ownerId   = item.OwnerId,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task<CommunicationItem> UpdateAsync(
        Guid projectId, Guid itemId, UpdateCommunicationItemRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var item = await db.CommunicationItems
            .FirstOrDefaultAsync(c => c.Id == itemId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("CommunicationItem");

        if (!item.IsActive)
            throw new ConflictException("Communication item is deactivated; cannot update");

        var changed = new List<string>();

        if (req.ItemType is not null)
        {
            if (string.IsNullOrWhiteSpace(req.ItemType))
                throw new ValidationException(["ItemType cannot be empty"]);
            item.ItemType = req.ItemType.Trim();
            changed.Add("ItemType");
        }
        if (req.Audience is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Audience))
                throw new ValidationException(["Audience cannot be empty"]);
            item.Audience = req.Audience.Trim();
            changed.Add("Audience");
        }
        if (req.Frequency.HasValue) { item.Frequency = req.Frequency.Value; changed.Add("Frequency"); }
        if (req.Channel.HasValue)   { item.Channel   = req.Channel.Value;   changed.Add("Channel"); }
        if (req.OwnerId.HasValue && req.OwnerId.Value != item.OwnerId)
        {
            await EnsureOwnerIsProjectMemberAsync(projectId, req.OwnerId.Value, ct);
            item.OwnerId = req.OwnerId.Value;
            changed.Add("OwnerId");
        }
        if (req.Notes is not null) { item.Notes = req.Notes; changed.Add("Notes"); }

        if (changed.Count == 0)
            throw new ValidationException(["No updatable fields provided"]);

        await audit.WriteAsync(actorId, "communication.updated", "CommunicationItem",
            item.Id.ToString(), projectId,
            detail: new { changedFields = changed }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task<CommunicationItem> DeactivateAsync(
        Guid projectId, Guid itemId, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var item = await db.CommunicationItems
            .FirstOrDefaultAsync(c => c.Id == itemId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("CommunicationItem");

        if (!item.IsActive)
            throw new ConflictException("Communication item is already deactivated");

        item.IsActive = false;
        await audit.WriteAsync(actorId, "communication.deactivated", "CommunicationItem",
            item.Id.ToString(), projectId,
            detail: new { itemType = item.ItemType }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>List active communications for the project, ordered
    /// by ItemType then Frequency. The matrix is a planning view —
    /// no soft-deleted rows.</summary>
    public async Task<List<CommunicationItem>> ListAsync(Guid projectId, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        return await db.CommunicationItems
            .Where(c => c.ProjectId == projectId && c.IsActive)
            .OrderBy(c => c.ItemType)
            .ThenBy(c => c.Frequency)
            .ToListAsync(ct);
    }

    private static void ValidateCreate(CreateCommunicationItemRequest req)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(req.ItemType)) errors.Add("ItemType is required");
        if (string.IsNullOrWhiteSpace(req.Audience)) errors.Add("Audience is required");
        if (req.OwnerId == Guid.Empty)               errors.Add("OwnerId is required");
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private async Task EnsureOwnerIsProjectMemberAsync(Guid projectId, Guid ownerId, CancellationToken ct)
    {
        var isMember = await db.ProjectMembers.AnyAsync(
            m => m.ProjectId == projectId && m.UserId == ownerId, ct);
        if (!isMember)
            throw new ValidationException(["Owner must be a member of the project"]);
    }
}

/// <summary>
/// ScheduleService — the schedule-domain CRUD + CPM solver entry
/// point (T-S4-03 onwards, PAFM-SD F.5). Dependency Add / Remove
/// arrives in T-S4-03; Activity CRUD + RecomputeAsync (CPM solve)
/// arrive in T-S4-05 / T-S4-04.
/// </summary>
public class ScheduleService(CimsDbContext db, AuditService audit)
{
    /// <summary>
    /// Add a directed dependency (Predecessor → Successor) to the
    /// project's schedule. Both endpoints must be active Activity
    /// rows belonging to the same project. Lag is bounded to
    /// ±365 days to catch typos; the empirical limit could be
    /// higher but real construction lags rarely exceed a year.
    /// Self-loops and cycle-creating edges are rejected with
    /// ConflictException — the caller fixes the dependency
    /// topology, the service does not silently drop edges.
    /// </summary>
    public async Task<Dependency> AddDependencyAsync(
        Guid projectId, AddDependencyRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (req.PredecessorId == req.SuccessorId)
            throw new ValidationException(["Predecessor and Successor cannot be the same activity"]);
        if (req.Lag < -365m || req.Lag > 365m)
            throw new ValidationException(["Lag must be in the range -365..365 days"]);

        var endpoints = await db.Activities
            .Where(a => a.ProjectId == projectId
                     && a.IsActive
                     && (a.Id == req.PredecessorId || a.Id == req.SuccessorId))
            .Select(a => a.Id)
            .ToListAsync(ct);
        if (!endpoints.Contains(req.PredecessorId) || !endpoints.Contains(req.SuccessorId))
            throw new NotFoundException("Activity");

        var duplicate = await db.Dependencies.AnyAsync(d =>
            d.ProjectId == projectId
         && d.PredecessorId == req.PredecessorId
         && d.SuccessorId == req.SuccessorId, ct);
        if (duplicate)
            throw new ConflictException("Dependency already exists for this predecessor / successor pair");

        // Cycle detection: pull every active activity ID + every
        // existing dependency edge in the project, append the proposed
        // edge, run DFS three-colour. Rejected on cycle (incl. self-loop
        // — already gated above for fast feedback).
        var allIds = await db.Activities
            .Where(a => a.ProjectId == projectId && a.IsActive)
            .Select(a => a.Id).ToListAsync(ct);
        var existingEdges = await db.Dependencies
            .Where(d => d.ProjectId == projectId)
            .Select(d => new ValueTuple<Guid, Guid>(d.PredecessorId, d.SuccessorId))
            .ToListAsync(ct);
        var edges = new List<(Guid, Guid)>(existingEdges.Count + 1);
        edges.AddRange(existingEdges);
        edges.Add((req.PredecessorId, req.SuccessorId));
        var cycle = CimsApp.Core.DependencyGraph.DetectCycle(allIds, edges);
        if (cycle.HasCycle)
            throw new ConflictException(
                $"Adding this dependency would create a cycle: {string.Join(" -> ", cycle.CycleNodes)}");

        var dep = new Dependency
        {
            ProjectId     = projectId,
            PredecessorId = req.PredecessorId,
            SuccessorId   = req.SuccessorId,
            Type          = req.Type,
            Lag           = req.Lag,
        };
        db.Dependencies.Add(dep);

        await audit.WriteAsync(actorId, "dependency.added", "Dependency",
            dep.Id.ToString(), projectId,
            detail: new
            {
                predecessorId = req.PredecessorId,
                successorId   = req.SuccessorId,
                type          = req.Type.ToString(),
                lag           = req.Lag,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return dep;
    }

    public async Task RemoveDependencyAsync(
        Guid projectId, Guid dependencyId, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var dep = await db.Dependencies
            .FirstOrDefaultAsync(d => d.Id == dependencyId && d.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Dependency");

        db.Dependencies.Remove(dep);
        await audit.WriteAsync(actorId, "dependency.removed", "Dependency",
            dep.Id.ToString(), projectId,
            detail: new
            {
                predecessorId = dep.PredecessorId,
                successorId   = dep.SuccessorId,
                type          = dep.Type.ToString(),
                lag           = dep.Lag,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<Dependency>> ListDependenciesAsync(
        Guid projectId, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        return await db.Dependencies
            .Where(d => d.ProjectId == projectId)
            .OrderBy(d => d.PredecessorId).ThenBy(d => d.SuccessorId)
            .ToListAsync(ct);
    }

    // ── Activity CRUD (T-S4-05) ─────────────────────────────────────

    public async Task<Activity> CreateActivityAsync(
        Guid projectId, CreateActivityRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        ValidateActivityCommon(req.Code, req.Name, req.Duration,
            req.PercentComplete, req.ConstraintType, req.ConstraintDate);
        if (req.AssigneeId.HasValue)
            await EnsureAssigneeIsProjectMemberAsync(projectId, req.AssigneeId.Value, ct);

        var codeTaken = await db.Activities.AnyAsync(
            a => a.ProjectId == projectId && a.Code == req.Code.Trim(), ct);
        if (codeTaken)
            throw new ConflictException($"Activity code '{req.Code.Trim()}' already exists in this project");

        var act = new Activity
        {
            ProjectId        = projectId,
            Code             = req.Code.Trim(),
            Name             = req.Name.Trim(),
            Description      = req.Description,
            Duration         = req.Duration,
            DurationUnit     = req.DurationUnit,
            ScheduledStart   = req.ScheduledStart,
            ScheduledFinish  = req.ScheduledFinish,
            ConstraintType   = req.ConstraintType,
            ConstraintDate   = req.ConstraintDate,
            PercentComplete  = req.PercentComplete,
            AssigneeId       = req.AssigneeId,
            Discipline       = req.Discipline,
        };
        db.Activities.Add(act);

        await audit.WriteAsync(actorId, "activity.created", "Activity",
            act.Id.ToString(), projectId,
            detail: new
            {
                code           = act.Code,
                duration       = act.Duration,
                durationUnit   = act.DurationUnit.ToString(),
                constraintType = act.ConstraintType.ToString(),
                hasAssignee    = act.AssigneeId.HasValue,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return act;
    }

    public async Task<Activity> UpdateActivityAsync(
        Guid projectId, Guid activityId, UpdateActivityRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var act = await db.Activities
            .FirstOrDefaultAsync(a => a.Id == activityId && a.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Activity");

        if (!act.IsActive)
            throw new ConflictException("Activity is deactivated; cannot update");

        var changed = new List<string>();

        if (req.Code is not null)
        {
            var newCode = req.Code.Trim();
            if (string.IsNullOrEmpty(newCode))
                throw new ValidationException(["Code cannot be empty"]);
            if (newCode != act.Code)
            {
                var taken = await db.Activities.AnyAsync(
                    a => a.ProjectId == projectId && a.Code == newCode && a.Id != activityId, ct);
                if (taken)
                    throw new ConflictException($"Activity code '{newCode}' already exists in this project");
                act.Code = newCode;
                changed.Add("Code");
            }
        }
        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                throw new ValidationException(["Name cannot be empty"]);
            act.Name = req.Name.Trim();
            changed.Add("Name");
        }
        if (req.Description is not null) { act.Description = req.Description; changed.Add("Description"); }
        if (req.Duration.HasValue)
        {
            if (req.Duration.Value < 0m)
                throw new ValidationException(["Duration cannot be negative"]);
            act.Duration = req.Duration.Value;
            changed.Add("Duration");
        }
        if (req.DurationUnit.HasValue)    { act.DurationUnit = req.DurationUnit.Value;    changed.Add("DurationUnit"); }
        if (req.ScheduledStart  is not null) { act.ScheduledStart  = req.ScheduledStart;  changed.Add("ScheduledStart"); }
        if (req.ScheduledFinish is not null) { act.ScheduledFinish = req.ScheduledFinish; changed.Add("ScheduledFinish"); }
        if (req.ConstraintType.HasValue)
        {
            act.ConstraintType = req.ConstraintType.Value;
            // If switching to a no-date constraint (ASAP / ALAP),
            // clear the legacy ConstraintDate so it doesn't dangle.
            if (req.ConstraintType.Value is ConstraintType.ASAP or ConstraintType.ALAP)
                act.ConstraintDate = null;
            changed.Add("ConstraintType");
        }
        if (req.ConstraintDate is not null)
        {
            act.ConstraintDate = req.ConstraintDate;
            changed.Add("ConstraintDate");
        }
        if (req.PercentComplete.HasValue)
        {
            if (req.PercentComplete.Value < 0m || req.PercentComplete.Value > 1m)
                throw new ValidationException(["PercentComplete must be in [0, 1]"]);
            act.PercentComplete = req.PercentComplete.Value;
            changed.Add("PercentComplete");
        }
        if (req.AssigneeId.HasValue)
        {
            if (req.AssigneeId.Value != act.AssigneeId)
            {
                await EnsureAssigneeIsProjectMemberAsync(projectId, req.AssigneeId.Value, ct);
                act.AssigneeId = req.AssigneeId;
                changed.Add("AssigneeId");
            }
        }
        if (req.Discipline is not null) { act.Discipline = req.Discipline; changed.Add("Discipline"); }

        if (changed.Count == 0)
            throw new ValidationException(["No updatable fields provided"]);

        await audit.WriteAsync(actorId, "activity.updated", "Activity",
            act.Id.ToString(), projectId,
            detail: new { changedFields = changed }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return act;
    }

    public async Task<Activity> DeactivateActivityAsync(
        Guid projectId, Guid activityId, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var act = await db.Activities
            .FirstOrDefaultAsync(a => a.Id == activityId && a.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Activity");

        if (!act.IsActive)
            throw new ConflictException("Activity is already deactivated");

        var hasDeps = await db.Dependencies.AnyAsync(
            d => d.ProjectId == projectId
              && (d.PredecessorId == activityId || d.SuccessorId == activityId), ct);
        if (hasDeps)
            throw new ConflictException(
                "Activity has active dependencies; remove the dependencies before deactivating");

        act.IsActive = false;
        await audit.WriteAsync(actorId, "activity.deactivated", "Activity",
            act.Id.ToString(), projectId,
            detail: new { code = act.Code }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return act;
    }

    public async Task<Activity> GetActivityAsync(
        Guid projectId, Guid activityId, CancellationToken ct = default)
        => await db.Activities
            .FirstOrDefaultAsync(a => a.Id == activityId && a.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Activity");

    public async Task<List<Activity>> ListActivitiesAsync(
        Guid projectId, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        return await db.Activities
            .Where(a => a.ProjectId == projectId && a.IsActive)
            .OrderBy(a => a.Code)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gantt data endpoint (T-S4-11, PAFM-SD F.5 sixth bullet —
    /// "Gantt and network views"; network view deferred to v1.1 /
    /// B-033 per CR-005). Returns the per-activity bars + per-link
    /// arrows in a UI-friendly shape. Start / Finish prefer the
    /// CPM-computed EarlyStart / EarlyFinish; fall back to
    /// ScheduledStart / ScheduledFinish if the solver hasn't run.
    /// ProjectStart / ProjectFinish are min / max across the
    /// activity-set Start / Finish.
    /// </summary>
    public async Task<GanttDto> GetGanttAsync(
        Guid projectId, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var activities = await db.Activities
            .Where(a => a.ProjectId == projectId && a.IsActive)
            .OrderBy(a => a.Code)
            .ToListAsync(ct);
        var dependencies = await db.Dependencies
            .Where(d => d.ProjectId == projectId)
            .OrderBy(d => d.PredecessorId).ThenBy(d => d.SuccessorId)
            .ToListAsync(ct);

        var actDtos = activities.Select(a => new GanttActivityDto(
            a.Id, a.Code, a.Name,
            a.EarlyStart  ?? a.ScheduledStart,
            a.EarlyFinish ?? a.ScheduledFinish,
            a.Duration, a.PercentComplete,
            a.IsCritical, a.AssigneeId, a.Discipline)).ToList();

        var depDtos = dependencies.Select(d => new GanttDependencyDto(
            d.Id, d.PredecessorId, d.SuccessorId, d.Type, d.Lag)).ToList();

        var starts   = actDtos.Where(x => x.Start.HasValue).Select(x => x.Start!.Value).ToList();
        var finishes = actDtos.Where(x => x.Finish.HasValue).Select(x => x.Finish!.Value).ToList();
        DateTime? projectStart  = starts.Count   == 0 ? null : starts.Min();
        DateTime? projectFinish = finishes.Count == 0 ? null : finishes.Max();

        return new GanttDto(projectStart, projectFinish, actDtos, depDtos);
    }

    // ── Recompute (T-S4-05) ─────────────────────────────────────────
    /// <summary>
    /// Run the CPM solver against the project's active activities +
    /// dependencies; persist ES / EF / LS / LF / TotalFloat /
    /// FreeFloat / IsCritical to each Activity. Data date defaults
    /// to Project.StartDate; explicit override accepted via the
    /// `dataDate` parameter. Transactional — the solve + persist
    /// pair is wrapped, so a partial schedule never lands.
    /// Audit: `schedule.recomputed` with the solver summary.
    /// </summary>
    public async Task<ScheduleRecomputeResultDto> RecomputeAsync(
        Guid projectId, DateTime? dataDate, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        var ps = dataDate ?? project.StartDate
            ?? throw new ValidationException(["dataDate or Project.StartDate must be set"]);

        var activities = await db.Activities
            .Where(a => a.ProjectId == projectId && a.IsActive)
            .ToListAsync(ct);
        var dependencies = await db.Dependencies
            .Where(d => d.ProjectId == projectId)
            .ToListAsync(ct);

        var cpmActivities = activities.Select(a =>
            new Cpm.CpmActivity(a.Id, a.Duration, a.ConstraintType, a.ConstraintDate)).ToList();
        var cpmDeps = dependencies.Select(d =>
            new Cpm.CpmDependency(d.PredecessorId, d.SuccessorId, d.Type, d.Lag)).ToList();

        var result = Cpm.Solve(ps, cpmActivities, cpmDeps);
        var byId = activities.ToDictionary(a => a.Id);
        foreach (var r in result.Activities)
        {
            var act = byId[r.Id];
            act.EarlyStart  = r.EarlyStart;
            act.EarlyFinish = r.EarlyFinish;
            act.LateStart   = r.LateStart;
            act.LateFinish  = r.LateFinish;
            act.TotalFloat  = r.TotalFloat;
            act.FreeFloat   = r.FreeFloat;
            act.IsCritical  = r.IsCritical;
        }

        var critCount = result.Activities.Count(r => r.IsCritical);
        await audit.WriteAsync(actorId, "schedule.recomputed", "Schedule",
            projectId.ToString(), projectId,
            detail: new
            {
                projectStart    = result.ProjectStart,
                projectFinish   = result.ProjectFinish,
                activities      = activities.Count,
                criticalCount   = critCount,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return new ScheduleRecomputeResultDto(
            result.ProjectStart, result.ProjectFinish, activities.Count, critCount);
    }

    // ── Validation helpers ──────────────────────────────────────────
    private static void ValidateActivityCommon(
        string code, string name, decimal duration, decimal percentComplete,
        ConstraintType constraintType, DateTime? constraintDate)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(code)) errors.Add("Code is required");
        if (string.IsNullOrWhiteSpace(name)) errors.Add("Name is required");
        if (duration < 0m) errors.Add("Duration cannot be negative");
        if (percentComplete < 0m || percentComplete > 1m)
            errors.Add("PercentComplete must be in [0, 1]");
        if (constraintType is ConstraintType.SNET or ConstraintType.SNLT
                            or ConstraintType.FNET or ConstraintType.FNLT
                            or ConstraintType.MSO  or ConstraintType.MFO
            && !constraintDate.HasValue)
            errors.Add($"ConstraintDate is required for {constraintType}");
        if (errors.Count > 0) throw new ValidationException(errors);
    }

    private async Task EnsureAssigneeIsProjectMemberAsync(Guid projectId, Guid assigneeId, CancellationToken ct)
    {
        var isMember = await db.ProjectMembers.AnyAsync(
            m => m.ProjectId == projectId && m.UserId == assigneeId, ct);
        if (!isMember)
            throw new ValidationException(["Assignee must be a member of the project"]);
    }

    // ── MS Project XML import (T-S4-09) ─────────────────────────────

    /// <summary>
    /// Import an MSP XML file into the project's schedule. Same shape
    /// as T-S1-03 CBS import: import-into-empty only — refuses if the
    /// project already has any active activities. The Core/MsProjectXml
    /// parser handles the XML; this method maps parsed UIDs → CIMS
    /// Guids, inserts the rows in a single transaction, and emits a
    /// `schedule.imported` audit event with the counts in the detail.
    /// CPM is NOT auto-recomputed — the caller decides when to call
    /// /schedule/recompute (often after manual review of the import).
    /// </summary>
    public async Task<MsProjectImportResultDto> ImportFromMsProjectAsync(
        Guid projectId, Stream xmlStream, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var hasExisting = await db.Activities
            .AnyAsync(a => a.ProjectId == projectId && a.IsActive, ct);
        if (hasExisting)
            throw new ConflictException(
                "Project already has activities; MS Project XML import is into-empty only");

        CimsApp.Core.MsProjectXml.ImportResult parsed;
        try { parsed = CimsApp.Core.MsProjectXml.Parse(xmlStream); }
        catch (FormatException ex)
            { throw new ValidationException([$"MS Project XML parse error: {ex.Message}"]); }

        var warnings = new List<string>();

        // First pass: insert Activities, build UID → Guid map.
        var uidToId = new Dictionary<string, Guid>(parsed.Activities.Count);
        var usedCodes = new HashSet<string>();
        foreach (var p in parsed.Activities)
        {
            // MSP UID is the most stable identifier — use as Code so
            // round-trip identity is preserved if export ships in v1.1.
            // De-dupe within the import in the unlikely case MSP ships
            // a duplicate UID by appending a discriminator.
            var code = $"MSP-{p.Uid}";
            while (!usedCodes.Add(code))
            {
                code = $"MSP-{p.Uid}-{Guid.NewGuid():N}".Substring(0, 50);
            }

            var activity = new Activity
            {
                ProjectId       = projectId,
                Code            = code.Length > 50 ? code[..50] : code,
                Name            = p.Name.Length > 300 ? p.Name[..300] : p.Name,
                Duration        = p.DurationDays,
                DurationUnit    = DurationUnit.Day,
                ScheduledStart  = p.Start,
                ScheduledFinish = p.Finish,
                ConstraintType  = ConstraintType.ASAP,
                PercentComplete = Math.Clamp(p.PercentComplete, 0m, 1m),
            };
            db.Activities.Add(activity);
            uidToId[p.Uid] = activity.Id;
        }

        // Second pass: insert Dependencies. Skip and warn on links
        // pointing at unknown UIDs (the XML may reference external
        // tasks that didn't ship in this Tasks block).
        var depCount = 0;
        var seenPairs = new HashSet<(Guid, Guid)>();
        foreach (var d in parsed.Dependencies)
        {
            if (!uidToId.TryGetValue(d.PredecessorUid, out var predId))
            {
                warnings.Add($"Skipped dependency: predecessor UID '{d.PredecessorUid}' not found in import");
                continue;
            }
            if (!uidToId.TryGetValue(d.SuccessorUid, out var succId))
            {
                warnings.Add($"Skipped dependency: successor UID '{d.SuccessorUid}' not found in import");
                continue;
            }
            if (predId == succId)
            {
                warnings.Add($"Skipped self-loop on UID '{d.PredecessorUid}'");
                continue;
            }
            if (!seenPairs.Add((predId, succId)))
            {
                warnings.Add($"Skipped duplicate dependency {d.PredecessorUid} -> {d.SuccessorUid}");
                continue;
            }
            db.Dependencies.Add(new Dependency
            {
                ProjectId     = projectId,
                PredecessorId = predId,
                SuccessorId   = succId,
                Type          = d.Type,
                Lag           = Math.Clamp(d.LagDays, -365m, 365m),
            });
            depCount++;
        }

        await audit.WriteAsync(actorId, "schedule.imported", "Schedule",
            projectId.ToString(), projectId,
            detail: new
            {
                source              = "MsProjectXml",
                projectName         = parsed.ProjectName,
                activitiesImported  = parsed.Activities.Count,
                dependenciesImported = depCount,
                warningsCount       = warnings.Count,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);

        return new MsProjectImportResultDto(
            parsed.ProjectName, parsed.ProjectStart,
            parsed.Activities.Count, depCount, warnings);
    }

    // ── Baselines (T-S4-06) ─────────────────────────────────────────

    /// <summary>
    /// Snapshot every active activity in the project into a frozen
    /// <see cref="ScheduleBaseline"/>. Captures the activity's
    /// post-CPM EarlyStart / EarlyFinish / Duration / IsCritical at
    /// baseline time. Multiple baselines per project are allowed —
    /// the typical PMBOK workflow is "Original baseline" + several
    /// "Approved revision N" snapshots through the project life.
    /// Empty-activity-set baselines are accepted (an honest "we have
    /// nothing planned yet" anchor); the comparison endpoint handles
    /// the empty case cleanly.
    /// </summary>
    public async Task<ScheduleBaseline> CreateBaselineAsync(
        Guid projectId, CreateBaselineRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Label))
            throw new ValidationException(["Label is required"]);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var activities = await db.Activities
            .Where(a => a.ProjectId == projectId && a.IsActive)
            .ToListAsync(ct);

        var efs = activities.Where(a => a.EarlyFinish.HasValue)
            .Select(a => a.EarlyFinish!.Value).ToList();
        var baseline = new ScheduleBaseline
        {
            ProjectId               = projectId,
            Label                   = req.Label.Trim(),
            CapturedById            = actorId,
            ActivitiesCount         = activities.Count,
            ProjectFinishAtBaseline = efs.Count == 0 ? null : efs.Max(),
        };
        db.ScheduleBaselines.Add(baseline);

        foreach (var a in activities)
        {
            db.ScheduleBaselineActivities.Add(new ScheduleBaselineActivity
            {
                ScheduleBaselineId = baseline.Id,
                ProjectId          = projectId,
                ActivityId         = a.Id,
                Code               = a.Code,
                Name               = a.Name,
                Duration           = a.Duration,
                EarlyStart         = a.EarlyStart,
                EarlyFinish        = a.EarlyFinish,
                IsCritical         = a.IsCritical,
            });
        }

        await audit.WriteAsync(actorId, "schedule_baseline.captured", "ScheduleBaseline",
            baseline.Id.ToString(), projectId,
            detail: new
            {
                label                   = baseline.Label,
                activitiesCount         = baseline.ActivitiesCount,
                projectFinishAtBaseline = baseline.ProjectFinishAtBaseline,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return baseline;
    }

    public async Task<List<BaselineSummaryDto>> ListBaselinesAsync(
        Guid projectId, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        return await db.ScheduleBaselines
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.CapturedAt)
            .Select(b => new BaselineSummaryDto(
                b.Id, b.Label, b.CapturedAt, b.CapturedById,
                b.ActivitiesCount, b.ProjectFinishAtBaseline))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Compare a captured baseline against the project's current
    /// schedule. Per-activity output covers three cases:
    /// (1) activity in both → variance fields populated;
    /// (2) activity new since baseline → IsNewSinceBaseline = true,
    ///     baseline* fields null;
    /// (3) activity removed since baseline → IsRemovedSinceBaseline =
    ///     true, current* fields null.
    /// Variance = current minus baseline (positive = slipped later
    /// or expanded).
    /// </summary>
    public async Task<BaselineComparisonDto> GetBaselineComparisonAsync(
        Guid projectId, Guid baselineId, CancellationToken ct = default)
    {
        var baseline = await db.ScheduleBaselines
            .FirstOrDefaultAsync(b => b.Id == baselineId && b.ProjectId == projectId, ct)
            ?? throw new NotFoundException("ScheduleBaseline");

        var baselineRows = await db.ScheduleBaselineActivities
            .Where(b => b.ScheduleBaselineId == baselineId)
            .ToListAsync(ct);
        var currentRows = await db.Activities
            .Where(a => a.ProjectId == projectId && a.IsActive)
            .ToListAsync(ct);

        var byBaseline = baselineRows.ToDictionary(b => b.ActivityId);
        var byCurrent  = currentRows.ToDictionary(a => a.Id);
        var allIds = byBaseline.Keys.Union(byCurrent.Keys).ToList();

        var rows = new List<BaselineActivityComparisonDto>(allIds.Count);
        foreach (var id in allIds)
        {
            byBaseline.TryGetValue(id, out var b);
            byCurrent.TryGetValue(id, out var c);

            var isNew     = b is null && c is not null;
            var isRemoved = b is not null && c is null;

            decimal? startVar  = (b?.EarlyStart  is { } bs && c?.EarlyStart  is { } cs) ? DiffDays(cs, bs) : null;
            decimal? finishVar = (b?.EarlyFinish is { } bf && c?.EarlyFinish is { } cf) ? DiffDays(cf, bf) : null;
            decimal? durVar    = (b is not null && c is not null) ? c!.Duration - b!.Duration : null;

            rows.Add(new BaselineActivityComparisonDto(
                ActivityId: id,
                Code: c?.Code ?? b!.Code,
                Name: c?.Name ?? b!.Name,
                BaselineEarlyStart:  b?.EarlyStart,
                BaselineEarlyFinish: b?.EarlyFinish,
                BaselineDuration:    b?.Duration,
                BaselineWasCritical: b?.IsCritical ?? false,
                CurrentEarlyStart:   c?.EarlyStart,
                CurrentEarlyFinish:  c?.EarlyFinish,
                CurrentDuration:     c?.Duration,
                CurrentIsCritical:   c?.IsCritical ?? false,
                StartVarianceDays:   startVar,
                FinishVarianceDays:  finishVar,
                DurationVarianceDays: durVar,
                IsNewSinceBaseline:     isNew,
                IsRemovedSinceBaseline: isRemoved));
        }

        var currentEfs = currentRows
            .Where(a => a.EarlyFinish.HasValue)
            .Select(a => a.EarlyFinish!.Value).ToList();
        DateTime? currentFinishOrNull = currentEfs.Count == 0 ? null : currentEfs.Max();
        decimal? finishVarProject = (baseline.ProjectFinishAtBaseline is { } pb && currentFinishOrNull is { } pc)
            ? DiffDays(pc, pb)
            : null;

        return new BaselineComparisonDto(
            BaselineId:                 baseline.Id,
            Label:                      baseline.Label,
            CapturedAt:                 baseline.CapturedAt,
            ProjectFinishAtBaseline:    baseline.ProjectFinishAtBaseline,
            CurrentProjectFinish:       currentFinishOrNull,
            ProjectFinishVarianceDays:  finishVarProject,
            AddedActivitiesCount:       rows.Count(r => r.IsNewSinceBaseline),
            RemovedActivitiesCount:     rows.Count(r => r.IsRemovedSinceBaseline),
            Activities:                 rows);
    }

    private static decimal DiffDays(DateTime current, DateTime baseline) =>
        (decimal)(current - baseline).Ticks / TimeSpan.TicksPerDay;
}

/// <summary>
/// LpsService — Last Planner System boards (T-S4-07, PAFM-SD F.5
/// third bullet). Three sub-domains:
/// (1) LookaheadEntry CRUD — the 6-week-out commit window.
/// (2) WeeklyWorkPlan CRUD — the per-week commitment header.
/// (3) WeeklyTaskCommitment CRUD — the per-Activity commit + flag.
///
/// PPC (Percent Plan Complete) is computed on read inside
/// <see cref="GetWeeklyWorkPlanAsync"/> — never persisted, so it
/// always reflects current commitment state.
/// </summary>
public class LpsService(CimsDbContext db, AuditService audit)
{
    // ── Lookahead ───────────────────────────────────────────────────

    public async Task<LookaheadEntry> AddLookaheadAsync(
        Guid projectId, CreateLookaheadEntryRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var activityExists = await db.Activities.AnyAsync(
            a => a.Id == req.ActivityId && a.ProjectId == projectId && a.IsActive, ct);
        if (!activityExists) throw new NotFoundException("Activity");

        var weekMonday = NormalizeToMonday(req.WeekStarting);

        var entry = new LookaheadEntry
        {
            ProjectId          = projectId,
            ActivityId         = req.ActivityId,
            WeekStarting       = weekMonday,
            ConstraintsRemoved = req.ConstraintsRemoved,
            Notes              = req.Notes,
            CreatedById        = actorId,
        };
        db.LookaheadEntries.Add(entry);

        await audit.WriteAsync(actorId, "lookahead.added", "LookaheadEntry",
            entry.Id.ToString(), projectId,
            detail: new
            {
                activityId         = req.ActivityId,
                weekStarting       = weekMonday,
                constraintsRemoved = req.ConstraintsRemoved,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<LookaheadEntry> UpdateLookaheadAsync(
        Guid projectId, Guid lookaheadId, UpdateLookaheadEntryRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var entry = await db.LookaheadEntries
            .FirstOrDefaultAsync(le => le.Id == lookaheadId && le.ProjectId == projectId && le.IsActive, ct)
            ?? throw new NotFoundException("LookaheadEntry");

        var changed = new List<string>();
        if (req.ConstraintsRemoved.HasValue && req.ConstraintsRemoved.Value != entry.ConstraintsRemoved)
        {
            entry.ConstraintsRemoved = req.ConstraintsRemoved.Value;
            changed.Add("ConstraintsRemoved");
        }
        if (req.Notes is not null) { entry.Notes = req.Notes; changed.Add("Notes"); }

        if (changed.Count == 0)
            throw new ValidationException(["No updatable fields provided"]);

        await audit.WriteAsync(actorId, "lookahead.updated", "LookaheadEntry",
            entry.Id.ToString(), projectId,
            detail: new { changedFields = changed }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task RemoveLookaheadAsync(
        Guid projectId, Guid lookaheadId, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var entry = await db.LookaheadEntries
            .FirstOrDefaultAsync(le => le.Id == lookaheadId && le.ProjectId == projectId && le.IsActive, ct)
            ?? throw new NotFoundException("LookaheadEntry");

        entry.IsActive = false;
        await audit.WriteAsync(actorId, "lookahead.removed", "LookaheadEntry",
            entry.Id.ToString(), projectId,
            detail: new { activityId = entry.ActivityId, weekStarting = entry.WeekStarting },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<LookaheadEntry>> ListLookaheadAsync(
        Guid projectId, DateTime? weekStarting, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        var q = db.LookaheadEntries
            .Where(le => le.ProjectId == projectId && le.IsActive);
        if (weekStarting.HasValue)
        {
            var monday = NormalizeToMonday(weekStarting.Value);
            q = q.Where(le => le.WeekStarting == monday);
        }
        return await q.OrderBy(le => le.WeekStarting).ThenBy(le => le.CreatedAt)
            .ToListAsync(ct);
    }

    // ── Weekly Work Plan ────────────────────────────────────────────

    public async Task<WeeklyWorkPlan> CreateWeeklyWorkPlanAsync(
        Guid projectId, CreateWeeklyWorkPlanRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var weekMonday = NormalizeToMonday(req.WeekStarting);

        var duplicate = await db.WeeklyWorkPlans.AnyAsync(
            w => w.ProjectId == projectId && w.WeekStarting == weekMonday, ct);
        if (duplicate)
            throw new ConflictException(
                $"A weekly work plan for week starting {weekMonday:yyyy-MM-dd} already exists in this project");

        var wwp = new WeeklyWorkPlan
        {
            ProjectId    = projectId,
            WeekStarting = weekMonday,
            Notes        = req.Notes,
            CreatedById  = actorId,
        };
        db.WeeklyWorkPlans.Add(wwp);

        await audit.WriteAsync(actorId, "weekly_plan.created", "WeeklyWorkPlan",
            wwp.Id.ToString(), projectId,
            detail: new { weekStarting = weekMonday }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return wwp;
    }

    public async Task<List<WeeklyWorkPlan>> ListWeeklyWorkPlansAsync(
        Guid projectId, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        return await db.WeeklyWorkPlans
            .Where(w => w.ProjectId == projectId)
            .OrderByDescending(w => w.WeekStarting)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Read a weekly work plan with its commitments and the computed
    /// PPC. PPC = 100 × completed_count / committed_count when
    /// committed_count > 0; null otherwise.
    /// </summary>
    public async Task<WeeklyWorkPlanDto> GetWeeklyWorkPlanAsync(
        Guid projectId, Guid wwpId, CancellationToken ct = default)
    {
        var wwp = await db.WeeklyWorkPlans
            .FirstOrDefaultAsync(w => w.Id == wwpId && w.ProjectId == projectId, ct)
            ?? throw new NotFoundException("WeeklyWorkPlan");

        var commitments = await db.WeeklyTaskCommitments
            .Where(c => c.WeeklyWorkPlanId == wwpId)
            .Join(db.Activities, c => c.ActivityId, a => a.Id,
                (c, a) => new { c, a })
            .OrderBy(x => x.a.Code)
            .ToListAsync(ct);

        var rows = commitments.Select(x => new WeeklyTaskCommitmentDto(
            x.c.Id, x.c.WeeklyWorkPlanId, x.c.ActivityId,
            x.a.Code, x.a.Name,
            x.c.Committed, x.c.Completed, x.c.Reason, x.c.Notes,
            x.c.UpdatedAt)).ToList();

        var committedCount = rows.Count(r => r.Committed);
        var completedCount = rows.Count(r => r.Completed);
        decimal? ppc = committedCount > 0
            ? Math.Round(100m * completedCount / committedCount, 2)
            : null;

        return new WeeklyWorkPlanDto(
            wwp.Id, wwp.ProjectId, wwp.WeekStarting, wwp.Notes,
            wwp.CreatedAt, wwp.CreatedById,
            committedCount, completedCount, ppc,
            rows);
    }

    // ── Weekly task commitments ─────────────────────────────────────

    public async Task<WeeklyTaskCommitment> AddCommitmentAsync(
        Guid projectId, Guid wwpId, AddCommitmentRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var wwp = await db.WeeklyWorkPlans
            .FirstOrDefaultAsync(w => w.Id == wwpId && w.ProjectId == projectId, ct)
            ?? throw new NotFoundException("WeeklyWorkPlan");

        var activityOk = await db.Activities.AnyAsync(
            a => a.Id == req.ActivityId && a.ProjectId == projectId && a.IsActive, ct);
        if (!activityOk) throw new NotFoundException("Activity");

        var duplicate = await db.WeeklyTaskCommitments.AnyAsync(
            c => c.WeeklyWorkPlanId == wwpId && c.ActivityId == req.ActivityId, ct);
        if (duplicate)
            throw new ConflictException("Activity is already committed in this weekly plan");

        var commit = new WeeklyTaskCommitment
        {
            WeeklyWorkPlanId = wwpId,
            ProjectId        = projectId,
            ActivityId       = req.ActivityId,
            Committed        = true,
            Completed        = false,
            Notes            = req.Notes,
        };
        db.WeeklyTaskCommitments.Add(commit);

        await audit.WriteAsync(actorId, "weekly_commitment.added", "WeeklyTaskCommitment",
            commit.Id.ToString(), projectId,
            detail: new
            {
                weeklyWorkPlanId = wwpId,
                activityId       = req.ActivityId,
                weekStarting     = wwp.WeekStarting,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return commit;
    }

    public async Task<WeeklyTaskCommitment> UpdateCommitmentAsync(
        Guid projectId, Guid wwpId, Guid commitmentId,
        UpdateCommitmentRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var commit = await db.WeeklyTaskCommitments
            .FirstOrDefaultAsync(c => c.Id == commitmentId
                                   && c.WeeklyWorkPlanId == wwpId
                                   && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("WeeklyTaskCommitment");

        // The whole point of LPS is to surface the reason when a
        // commitment fails. Reject Completed = false without a Reason.
        if (commit.Committed && !req.Completed && !req.Reason.HasValue)
            throw new ValidationException(
                ["Reason is required when Completed = false on a committed task"]);

        commit.Completed = req.Completed;
        commit.Reason    = req.Completed ? null : req.Reason;
        if (req.Notes is not null) commit.Notes = req.Notes;

        await audit.WriteAsync(actorId, "weekly_commitment.updated", "WeeklyTaskCommitment",
            commit.Id.ToString(), projectId,
            detail: new
            {
                completed = req.Completed,
                reason    = req.Reason?.ToString(),
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return commit;
    }

    public async Task RemoveCommitmentAsync(
        Guid projectId, Guid wwpId, Guid commitmentId, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var commit = await db.WeeklyTaskCommitments
            .FirstOrDefaultAsync(c => c.Id == commitmentId
                                   && c.WeeklyWorkPlanId == wwpId
                                   && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("WeeklyTaskCommitment");

        db.WeeklyTaskCommitments.Remove(commit);
        await audit.WriteAsync(actorId, "weekly_commitment.removed", "WeeklyTaskCommitment",
            commit.Id.ToString(), projectId,
            detail: new
            {
                weeklyWorkPlanId = wwpId,
                activityId       = commit.ActivityId,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Normalise an arbitrary date to the Monday of its ISO week.
    /// LPS boards are aligned to Monday-starting weeks; allowing any
    /// day-of-week input keeps the API permissive (the front-end can
    /// just send "today" without knowing it's Monday).
    /// </summary>
    private static DateTime NormalizeToMonday(DateTime d)
    {
        var date = d.Date;
        var diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-diff);
    }
}

/// <summary>
/// ChangeRequestService — formal construction-site change-control
/// workflow (T-S5-04, PAFM-SD F.6). The 5-state machine in
/// <see cref="CimsApp.Core.ChangeWorkflow"/> drives every transition;
/// this service wraps with persistence and audit-twin emission.
/// Approve carries an optional CreateVariation flag (T-S5-06) that
/// atomically spawns an S1 Variation as a side-effect.
/// </summary>
public class ChangeRequestService(CimsDbContext db, AuditService audit)
{
    /// <summary>
    /// Raise a new ChangeRequest in state
    /// <see cref="ChangeRequestState.Raised"/>. Number is
    /// auto-generated as `CR-NNNN` per project, mirroring
    /// <see cref="VariationsService"/>'s VAR-NNNN pattern. Title +
    /// Category required; Description / Bsa-categorisation /
    /// impact summaries optional at raise time (typically populated
    /// by the IM at Assess).
    /// </summary>
    public async Task<ChangeRequest> RaiseAsync(
        Guid projectId, RaiseChangeRequestRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ValidationException(["Title is required"]);

        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        // Concurrency note: same shape as VariationsService.RaiseAsync —
        // count + 1 racing produces duplicates that the unique
        // (ProjectId, Number) index catches at SaveChanges. Strict
        // serial counter is a v1.1 candidate.
        var count = await db.ChangeRequests.CountAsync(c => c.ProjectId == projectId, ct);

        var c = new ChangeRequest
        {
            ProjectId               = projectId,
            Number                  = $"CR-{(count + 1):D4}",
            Title                   = req.Title.Trim(),
            Description             = req.Description,
            Category                = req.Category,
            BsaCategory             = req.BsaCategory,
            ProgrammeImpactSummary  = req.ProgrammeImpactSummary,
            CostImpactSummary       = req.CostImpactSummary,
            EstimatedCostImpact     = req.EstimatedCostImpact,
            EstimatedTimeImpactDays = req.EstimatedTimeImpactDays,
            RaisedById              = actorId,
            RaisedAt                = DateTime.UtcNow,
            State                   = ChangeRequestState.Raised,
        };
        db.ChangeRequests.Add(c);
        await audit.WriteAsync(actorId, "change_request.raised", "ChangeRequest",
            c.Id.ToString(), projectId,
            detail: new
            {
                number      = c.Number,
                category    = c.Category.ToString(),
                bsaCategory = c.BsaCategory.ToString(),
                costImpact  = req.EstimatedCostImpact,
                timeImpactDays = req.EstimatedTimeImpactDays,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>
    /// Assess transition: Raised → Assessed. AssessmentNote required.
    /// IM may also refine programme / cost impact summaries +
    /// estimates, and update BsaCategory if the initial raise
    /// classification was wrong. State-machine + role-gate enforced
    /// via <see cref="ChangeWorkflow.CanTransition"/>.
    /// </summary>
    public async Task<ChangeRequest> AssessAsync(
        Guid projectId, Guid changeRequestId, AssessChangeRequestRequest req,
        Guid actorId, UserRole role,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.AssessmentNote))
            throw new ValidationException(["AssessmentNote is required"]);

        var c = await LoadAsync(projectId, changeRequestId, ct);
        EnforceTransition(c.State, ChangeRequestState.Assessed, role);

        c.State          = ChangeRequestState.Assessed;
        c.AssessedById   = actorId;
        c.AssessedAt     = DateTime.UtcNow;
        c.AssessmentNote = req.AssessmentNote.Trim();
        if (req.ProgrammeImpactSummary  is not null) c.ProgrammeImpactSummary  = req.ProgrammeImpactSummary;
        if (req.CostImpactSummary       is not null) c.CostImpactSummary       = req.CostImpactSummary;
        if (req.EstimatedCostImpact.HasValue)        c.EstimatedCostImpact     = req.EstimatedCostImpact;
        if (req.EstimatedTimeImpactDays.HasValue)    c.EstimatedTimeImpactDays = req.EstimatedTimeImpactDays;
        if (req.BsaCategory.HasValue)                c.BsaCategory             = req.BsaCategory.Value;

        await audit.WriteAsync(actorId, "change_request.assessed", "ChangeRequest",
            c.Id.ToString(), projectId,
            detail: new
            {
                number      = c.Number,
                bsaCategory = c.BsaCategory.ToString(),
                costImpact  = c.EstimatedCostImpact,
                timeImpactDays = c.EstimatedTimeImpactDays,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>
    /// Approve transition: Assessed → Approved. DecisionNote required.
    /// CreateVariation flag triggers an S1 Variation spawn (T-S5-06)
    /// — landed in this method to keep the side-effect inside the
    /// single SaveChanges (transactional atomicity per the audit-twin
    /// guarantee from PR #33).
    /// </summary>
    public async Task<ChangeRequest> ApproveAsync(
        Guid projectId, Guid changeRequestId, ApproveChangeRequestRequest req,
        Guid actorId, UserRole role,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.DecisionNote))
            throw new ValidationException(["DecisionNote is required"]);

        var c = await LoadAsync(projectId, changeRequestId, ct);
        EnforceTransition(c.State, ChangeRequestState.Approved, role);

        c.State        = ChangeRequestState.Approved;
        c.DecisionById = actorId;
        c.DecisionAt   = DateTime.UtcNow;
        c.DecisionNote = req.DecisionNote.Trim();

        Variation? variation = null;
        if (req.CreateVariation)
        {
            // Spawn an S1 Variation atomically. Reference back via
            // GeneratedVariationId so the change register can link to
            // the ledger entry. The Variation itself starts in Raised
            // state — the PM still needs to approve it through the
            // S1 workflow if v1.0 wants double-confirmation; in
            // practice an Approved CR + spawned Variation is then
            // typically auto-approved on the Variation side as a
            // recording-only step (manual via /variations/{id}/approve).
            variation = new Variation
            {
                ProjectId = projectId,
                VariationNumber = await NextVariationNumberAsync(projectId, ct),
                Title           = $"From {c.Number}: {c.Title}",
                Description     = c.Description,
                Reason          = c.AssessmentNote,
                EstimatedCostImpact     = c.EstimatedCostImpact,
                EstimatedTimeImpactDays = c.EstimatedTimeImpactDays,
                RaisedById = actorId,
                State      = VariationState.Raised,
            };
            db.Variations.Add(variation);
            c.GeneratedVariationId = variation.Id;

            await audit.WriteAsync(actorId, "change_request.variation_created", "ChangeRequest",
                c.Id.ToString(), projectId,
                detail: new
                {
                    number          = c.Number,
                    variationId     = variation.Id,
                    variationNumber = variation.VariationNumber,
                }, ip: ip, ua: ua);
        }

        await audit.WriteAsync(actorId, "change_request.approved", "ChangeRequest",
            c.Id.ToString(), projectId,
            detail: new
            {
                number              = c.Number,
                decisionNote        = req.DecisionNote,
                createdVariation    = req.CreateVariation,
                variationId         = variation?.Id,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>
    /// Reject transition: Raised → Rejected OR Assessed → Rejected.
    /// DecisionNote required so the audit trail carries a "why
    /// rejected" rationale. Once Approved, the only forward path is
    /// Implemented → Closed; rejection after Approved rejected with
    /// ConflictException via the state-machine guard.
    /// </summary>
    public async Task<ChangeRequest> RejectAsync(
        Guid projectId, Guid changeRequestId, RejectChangeRequestRequest req,
        Guid actorId, UserRole role,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.DecisionNote))
            throw new ValidationException(["DecisionNote is required"]);

        var c = await LoadAsync(projectId, changeRequestId, ct);
        EnforceTransition(c.State, ChangeRequestState.Rejected, role);

        c.State        = ChangeRequestState.Rejected;
        c.DecisionById = actorId;
        c.DecisionAt   = DateTime.UtcNow;
        c.DecisionNote = req.DecisionNote.Trim();

        await audit.WriteAsync(actorId, "change_request.rejected", "ChangeRequest",
            c.Id.ToString(), projectId,
            detail: new
            {
                number       = c.Number,
                decisionNote = req.DecisionNote,
                fromState    = c.State.ToString(),  // post-mutation; harmless for audit
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>
    /// Implement transition: Approved → Implemented. Marks the change
    /// as actioned in delivery. Note optional.
    /// </summary>
    public async Task<ChangeRequest> ImplementAsync(
        Guid projectId, Guid changeRequestId, ImplementChangeRequestRequest req,
        Guid actorId, UserRole role,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var c = await LoadAsync(projectId, changeRequestId, ct);
        EnforceTransition(c.State, ChangeRequestState.Implemented, role);

        c.State         = ChangeRequestState.Implemented;
        c.ImplementedAt = DateTime.UtcNow;

        await audit.WriteAsync(actorId, "change_request.implemented", "ChangeRequest",
            c.Id.ToString(), projectId,
            detail: new { number = c.Number, note = req.Note },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>
    /// Close transition: Implemented → Closed. Terminal state. Note
    /// optional.
    /// </summary>
    public async Task<ChangeRequest> CloseAsync(
        Guid projectId, Guid changeRequestId, CloseChangeRequestRequest req,
        Guid actorId, UserRole role,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var c = await LoadAsync(projectId, changeRequestId, ct);
        EnforceTransition(c.State, ChangeRequestState.Closed, role);

        c.State    = ChangeRequestState.Closed;
        c.ClosedAt = DateTime.UtcNow;

        await audit.WriteAsync(actorId, "change_request.closed", "ChangeRequest",
            c.Id.ToString(), projectId,
            detail: new { number = c.Number, note = req.Note },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return c;
    }

    // ── Reads ───────────────────────────────────────────────────────

    public Task<ChangeRequest> GetAsync(
        Guid projectId, Guid changeRequestId, CancellationToken ct = default)
        => LoadAsync(projectId, changeRequestId, ct);

    public async Task<List<ChangeRequest>> ListAsync(
        Guid projectId, ChangeRequestState? state, ChangeRequestCategory? category,
        CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        var q = db.ChangeRequests.Where(c => c.ProjectId == projectId);
        if (state.HasValue)    q = q.Where(c => c.State == state.Value);
        if (category.HasValue) q = q.Where(c => c.Category == category.Value);
        return await q.OrderByDescending(c => c.RaisedAt).ToListAsync(ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private async Task<ChangeRequest> LoadAsync(
        Guid projectId, Guid changeRequestId, CancellationToken ct)
        => await db.ChangeRequests
            .FirstOrDefaultAsync(c => c.Id == changeRequestId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("ChangeRequest");

    /// <summary>
    /// Throws on transition rejection, mapping the failure to the
    /// right exception type:
    /// - State-machine invalid transition (e.g. Approved → Rejected)
    ///   → ConflictException with the from-state in the message.
    /// - Role doesn't meet the gate → ForbiddenException.
    /// </summary>
    private static void EnforceTransition(
        ChangeRequestState from, ChangeRequestState to, UserRole role)
    {
        if (!ChangeWorkflow.IsValidTransition(from, to))
            throw new ConflictException(
                $"Cannot transition ChangeRequest from {from} to {to}");
        if (!ChangeWorkflow.CanTransition(from, to, role))
            throw new ForbiddenException(
                $"Role {role} cannot perform the {from} → {to} transition");
    }

    private async Task<string> NextVariationNumberAsync(Guid projectId, CancellationToken ct)
    {
        var n = await db.Variations.CountAsync(v => v.ProjectId == projectId, ct);
        return $"VAR-{(n + 1):D4}";
    }
}

/// <summary>
/// ProcurementStrategyService — single-row-per-project strategy
/// capture (T-S6-02, PAFM-SD F.7 first bullet). Upsert semantics:
/// `CreateOrUpdateAsync` creates the strategy on first call, updates
/// it on subsequent calls. Approve transition is a separate
/// `ApproveAsync` that records the approver + timestamp; v1.0 does
/// not enforce a state-machine on the strategy itself (re-approval
/// is allowed and timestamps refresh).
/// </summary>
public class ProcurementStrategyService(CimsDbContext db, AuditService audit)
{
    /// <summary>
    /// Upsert the project's procurement strategy. Returns the
    /// (created or updated) row. Audit emits `procurement_strategy.created`
    /// on first write, `procurement_strategy.updated` on subsequent
    /// writes.
    /// </summary>
    public async Task<ProcurementStrategy> CreateOrUpdateAsync(
        Guid projectId, UpsertProcurementStrategyRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        if (req.EstimatedTotalValue.HasValue && req.EstimatedTotalValue.Value < 0m)
            throw new ValidationException(["EstimatedTotalValue cannot be negative"]);

        var existing = await db.ProcurementStrategies
            .FirstOrDefaultAsync(s => s.ProjectId == projectId, ct);

        var isCreate = existing is null;
        var s = existing ?? new ProcurementStrategy { ProjectId = projectId };
        s.Approach              = req.Approach;
        s.ContractForm          = req.ContractForm;
        s.EstimatedTotalValue   = req.EstimatedTotalValue;
        s.KeyDates              = req.KeyDates;
        s.PackageBreakdownNotes = req.PackageBreakdownNotes;
        if (isCreate) db.ProcurementStrategies.Add(s);

        var action = isCreate ? "procurement_strategy.created" : "procurement_strategy.updated";
        await audit.WriteAsync(actorId, action, "ProcurementStrategy",
            s.Id.ToString(), projectId,
            detail: new
            {
                approach     = req.Approach.ToString(),
                contractForm = req.ContractForm.ToString(),
                estimatedValue = req.EstimatedTotalValue,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return s;
    }

    /// <summary>
    /// Approve the strategy. Records the approver + UtcNow. v1.0
    /// allows re-approval (timestamps refresh) — there's no
    /// "already approved" rejection because real workflows revisit
    /// strategies after risk reviews / cost feedback. Audit logs
    /// each approval distinctly so the trail captures the history.
    /// </summary>
    public async Task<ProcurementStrategy> ApproveAsync(
        Guid projectId, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var s = await db.ProcurementStrategies
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct)
            ?? throw new NotFoundException("ProcurementStrategy");

        s.ApprovedById = actorId;
        s.ApprovedAt   = DateTime.UtcNow;
        await audit.WriteAsync(actorId, "procurement_strategy.approved", "ProcurementStrategy",
            s.Id.ToString(), projectId,
            detail: new { approach = s.Approach.ToString(), contractForm = s.ContractForm.ToString() },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return s;
    }

    public async Task<ProcurementStrategy?> GetAsync(
        Guid projectId, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        return await db.ProcurementStrategies
            .FirstOrDefaultAsync(s => s.ProjectId == projectId, ct);
    }
}

/// <summary>
/// TenderPackagesService — CRUD + 3-state workflow for tender
/// packages (T-S6-03, PAFM-SD F.7 second bullet). State machine
/// in <see cref="CimsApp.Core.TenderPackageWorkflow"/>; this
/// service wraps with persistence + audit-twin emission. Award
/// (T-S6-06) extends this service with the atomic Award→Contract
/// spawn.
/// </summary>
public class TenderPackagesService(CimsDbContext db, AuditService audit)
{
    public async Task<TenderPackage> CreateAsync(
        Guid projectId, CreateTenderPackageRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ValidationException(["Name is required"]);
        if (req.EstimatedValue.HasValue && req.EstimatedValue.Value < 0m)
            throw new ValidationException(["EstimatedValue cannot be negative"]);
        if (req.IssueDate.HasValue && req.ReturnDate.HasValue
            && req.ReturnDate.Value <= req.IssueDate.Value)
            throw new ValidationException(["ReturnDate must be after IssueDate"]);

        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        // Concurrency note: same shape as VariationsService /
        // ChangeRequestService — count + 1 racing produces duplicates
        // that the unique (ProjectId, Number) index catches at
        // SaveChanges. Strict serial counter is a v1.1 candidate.
        var count = await db.TenderPackages.CountAsync(t => t.ProjectId == projectId, ct);

        var t = new TenderPackage
        {
            ProjectId      = projectId,
            Number         = $"TP-{(count + 1):D4}",
            Name           = req.Name.Trim(),
            Description    = req.Description,
            EstimatedValue = req.EstimatedValue,
            IssueDate      = req.IssueDate,
            ReturnDate     = req.ReturnDate,
            State          = TenderPackageState.Draft,
            CreatedById    = actorId,
        };
        db.TenderPackages.Add(t);
        await audit.WriteAsync(actorId, "tender_package.created", "TenderPackage",
            t.Id.ToString(), projectId,
            detail: new { number = t.Number, name = t.Name, estimatedValue = t.EstimatedValue },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return t;
    }

    /// <summary>
    /// Partial update of a Draft TenderPackage. Update of an
    /// Issued or Closed package is rejected with ConflictException
    /// — the package is frozen post-Issue. No-op rejected.
    /// </summary>
    public async Task<TenderPackage> UpdateAsync(
        Guid projectId, Guid tenderPackageId, UpdateTenderPackageRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var t = await LoadAsync(projectId, tenderPackageId, ct);
        if (t.State != TenderPackageState.Draft)
            throw new ConflictException(
                $"TenderPackage is in state {t.State}; only Draft packages can be updated");

        var changed = new List<string>();
        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                throw new ValidationException(["Name cannot be empty"]);
            t.Name = req.Name.Trim();
            changed.Add("Name");
        }
        if (req.Description is not null) { t.Description = req.Description; changed.Add("Description"); }
        if (req.EstimatedValue.HasValue)
        {
            if (req.EstimatedValue.Value < 0m)
                throw new ValidationException(["EstimatedValue cannot be negative"]);
            t.EstimatedValue = req.EstimatedValue;
            changed.Add("EstimatedValue");
        }
        if (req.IssueDate is not null)  { t.IssueDate  = req.IssueDate;  changed.Add("IssueDate"); }
        if (req.ReturnDate is not null) { t.ReturnDate = req.ReturnDate; changed.Add("ReturnDate"); }

        // Cross-field validation: if both dates set, ReturnDate > IssueDate.
        if (t.IssueDate.HasValue && t.ReturnDate.HasValue
            && t.ReturnDate.Value <= t.IssueDate.Value)
            throw new ValidationException(["ReturnDate must be after IssueDate"]);

        if (changed.Count == 0)
            throw new ValidationException(["No updatable fields provided"]);

        await audit.WriteAsync(actorId, "tender_package.updated", "TenderPackage",
            t.Id.ToString(), projectId,
            detail: new { changedFields = changed }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return t;
    }

    /// <summary>
    /// Issue transition: Draft → Issued. Freezes the package — bidders
    /// can now submit tenders against the issued details. State-machine
    /// + role gate via TenderPackageWorkflow.CanTransition (PM+).
    /// </summary>
    public async Task<TenderPackage> IssueAsync(
        Guid projectId, Guid tenderPackageId,
        Guid actorId, UserRole role,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var t = await LoadAsync(projectId, tenderPackageId, ct);
        EnforceTransition(t.State, TenderPackageState.Issued, role);

        t.State      = TenderPackageState.Issued;
        t.IssuedById = actorId;
        t.IssuedAt   = DateTime.UtcNow;

        await audit.WriteAsync(actorId, "tender_package.issued", "TenderPackage",
            t.Id.ToString(), projectId,
            detail: new { number = t.Number, issueDate = t.IssueDate, returnDate = t.ReturnDate },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return t;
    }

    /// <summary>
    /// Close transition: Issued → Closed. The "abandon-without-award"
    /// path. Award (T-S6-06) calls into the same transition with
    /// additional side-effects (winning Tender → Awarded; others →
    /// Rejected; Contract spawn).
    /// </summary>
    public async Task<TenderPackage> CloseAsync(
        Guid projectId, Guid tenderPackageId,
        Guid actorId, UserRole role,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var t = await LoadAsync(projectId, tenderPackageId, ct);
        EnforceTransition(t.State, TenderPackageState.Closed, role);

        t.State      = TenderPackageState.Closed;
        t.ClosedById = actorId;
        t.ClosedAt   = DateTime.UtcNow;

        await audit.WriteAsync(actorId, "tender_package.closed", "TenderPackage",
            t.Id.ToString(), projectId,
            detail: new { number = t.Number, awarded = t.AwardedTenderId.HasValue },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return t;
    }

    /// <summary>
    /// Soft-delete a Draft TenderPackage. Issued / Closed packages
    /// cannot be deactivated — they're part of the audit chain.
    /// Idempotent rejection on already-deactivated.
    /// </summary>
    public async Task<TenderPackage> DeactivateAsync(
        Guid projectId, Guid tenderPackageId, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var t = await LoadAsync(projectId, tenderPackageId, ct);
        if (!t.IsActive)
            throw new ConflictException("TenderPackage is already deactivated");
        if (t.State != TenderPackageState.Draft)
            throw new ConflictException(
                $"TenderPackage is in state {t.State}; only Draft packages can be deactivated");

        t.IsActive = false;
        await audit.WriteAsync(actorId, "tender_package.deactivated", "TenderPackage",
            t.Id.ToString(), projectId,
            detail: new { number = t.Number, name = t.Name }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return t;
    }

    public Task<TenderPackage> GetAsync(
        Guid projectId, Guid tenderPackageId, CancellationToken ct = default)
        => LoadAsync(projectId, tenderPackageId, ct);

    public async Task<List<TenderPackage>> ListAsync(
        Guid projectId, TenderPackageState? state, CancellationToken ct = default)
    {
        _ = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");
        var q = db.TenderPackages.Where(t => t.ProjectId == projectId && t.IsActive);
        if (state.HasValue) q = q.Where(t => t.State == state.Value);
        return await q.OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
    }

    private async Task<TenderPackage> LoadAsync(
        Guid projectId, Guid tenderPackageId, CancellationToken ct)
        => await db.TenderPackages
            .FirstOrDefaultAsync(t => t.Id == tenderPackageId && t.ProjectId == projectId, ct)
            ?? throw new NotFoundException("TenderPackage");

    private static void EnforceTransition(
        TenderPackageState from, TenderPackageState to, UserRole role)
    {
        if (!CimsApp.Core.TenderPackageWorkflow.IsValidTransition(from, to))
            throw new ConflictException(
                $"Cannot transition TenderPackage from {from} to {to}");
        if (!CimsApp.Core.TenderPackageWorkflow.CanTransition(from, to, role))
            throw new ForbiddenException(
                $"Role {role} cannot perform the {from} → {to} transition");
    }

    // ── Award (T-S6-06) ─────────────────────────────────────────────

    /// <summary>
    /// Award a TenderPackage to a winning Tender (PAFM-SD F.7
    /// fourth bullet). Mirrors the S5 ChangeRequest.ApproveAsync
    /// approve-and-spawn pattern: one transactional SaveChanges
    /// holds:
    /// 1) winning Tender → Awarded;
    /// 2) all other active Tenders in the package → Rejected
    ///    automatically with a "not awarded" note;
    /// 3) TenderPackage → Closed (sets AwardedTenderId);
    /// 4) Contract spawned with copied BidAmount + BidderName +
    ///    ContractorOrganisation;
    /// 5) winning Tender's GeneratedContractId set to the new
    ///    Contract's Id;
    /// 6) Audit-twin emits tender.awarded + per-loser
    ///    tender.rejected + tender_package.closed +
    ///    contract.created.
    ///
    /// Role gate: ProjectManager+ via the state-machine path
    /// (Issued → Closed transition).
    /// </summary>
    public async Task<Contract> AwardAsync(
        Guid projectId, Guid tenderPackageId, AwardTenderPackageRequest req,
        Guid actorId, UserRole role,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.AwardNote))
            throw new ValidationException(["AwardNote is required"]);

        var pkg = await LoadAsync(projectId, tenderPackageId, ct);
        EnforceTransition(pkg.State, TenderPackageState.Closed, role);

        var winner = await db.Tenders
            .FirstOrDefaultAsync(t => t.Id == req.AwardedTenderId
                                    && t.TenderPackageId == tenderPackageId
                                    && t.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Tender");

        if (winner.State is TenderState.Withdrawn or TenderState.Awarded or TenderState.Rejected)
            throw new ConflictException(
                $"Tender is in state {winner.State}; cannot be awarded");

        // Determine ContractForm: explicit override → strategy default → Other.
        var contractForm = req.ContractForm;
        if (!contractForm.HasValue)
        {
            var strategy = await db.ProcurementStrategies
                .FirstOrDefaultAsync(s => s.ProjectId == projectId, ct);
            contractForm = strategy?.ContractForm ?? ContractForm.Other;
        }

        // Spawn the Contract row.
        var contractCount = await db.Contracts.CountAsync(c => c.ProjectId == projectId, ct);
        var contract = new Contract
        {
            ProjectId               = projectId,
            Number                  = $"CON-{(contractCount + 1):D4}",
            TenderPackageId         = tenderPackageId,
            AwardedTenderId         = winner.Id,
            ContractorName          = winner.BidderName,
            ContractorOrganisation  = winner.BidderOrganisation,
            ContractValue           = winner.BidAmount,
            ContractForm            = contractForm.Value,
            StartDate               = req.ContractStartDate,
            EndDate                 = req.ContractEndDate,
            State                   = ContractState.Active,
            AwardNote               = req.AwardNote.Trim(),
            AwardedById             = actorId,
            AwardedAt               = DateTime.UtcNow,
        };
        db.Contracts.Add(contract);

        // Winner: → Awarded; carry the contract FK.
        winner.State               = TenderState.Awarded;
        winner.StateNote           = req.AwardNote.Trim();
        winner.GeneratedContractId = contract.Id;

        // Losers: any Tender in the same package not the winner and
        // currently in Submitted / Evaluated → Rejected automatically.
        var losers = await db.Tenders
            .Where(t => t.TenderPackageId == tenderPackageId
                     && t.Id != winner.Id
                     && (t.State == TenderState.Submitted || t.State == TenderState.Evaluated))
            .ToListAsync(ct);
        foreach (var l in losers)
        {
            l.State     = TenderState.Rejected;
            l.StateNote = $"Not awarded; package awarded to {winner.BidderName}";
        }

        // Package: → Closed.
        pkg.State           = TenderPackageState.Closed;
        pkg.ClosedById      = actorId;
        pkg.ClosedAt        = DateTime.UtcNow;
        pkg.AwardedTenderId = winner.Id;

        // Audit chain.
        await audit.WriteAsync(actorId, "tender.awarded", "Tender",
            winner.Id.ToString(), projectId,
            detail: new
            {
                tenderPackageId,
                bidderName = winner.BidderName,
                bidAmount  = winner.BidAmount,
                contractId = contract.Id,
                contractNumber = contract.Number,
            }, ip: ip, ua: ua);

        foreach (var l in losers)
        {
            await audit.WriteAsync(actorId, "tender.rejected", "Tender",
                l.Id.ToString(), projectId,
                detail: new
                {
                    tenderPackageId,
                    bidderName = l.BidderName,
                    reason     = "Not awarded",
                }, ip: ip, ua: ua);
        }

        await audit.WriteAsync(actorId, "tender_package.closed", "TenderPackage",
            pkg.Id.ToString(), projectId,
            detail: new
            {
                number   = pkg.Number,
                awarded  = true,
                awardedTenderId = winner.Id,
            }, ip: ip, ua: ua);

        await audit.WriteAsync(actorId, "contract.created", "Contract",
            contract.Id.ToString(), projectId,
            detail: new
            {
                number          = contract.Number,
                tenderPackageId,
                awardedTenderId = winner.Id,
                contractorName  = contract.ContractorName,
                contractValue   = contract.ContractValue,
                contractForm    = contract.ContractForm.ToString(),
            }, ip: ip, ua: ua);

        await db.SaveChangesAsync(ct);
        return contract;
    }
}

/// <summary>
/// TendersService — bid receipt + lifecycle (T-S6-04, PAFM-SD F.7
/// second bullet downstream). Submit (create) records a bid against
/// an Issued TenderPackage; Withdraw is the bidder-pulls-out branch
/// (Submitted state only). Evaluated transition arrives via T-S6-05
/// (after all required scores are recorded); Awarded / Rejected
/// transitions arrive via T-S6-06 Award workflow.
/// </summary>
public class TendersService(CimsDbContext db, AuditService audit)
{
    /// <summary>
    /// Record a bid against an Issued TenderPackage. Validates
    /// the package is currently in Issued state — submissions
    /// against Draft (not yet released) or Closed (already
    /// awarded / abandoned) packages are rejected with
    /// ConflictException. SubmittedAt = UtcNow at create.
    /// </summary>
    public async Task<Tender> SubmitAsync(
        Guid projectId, Guid tenderPackageId, SubmitTenderRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.BidderName))
            throw new ValidationException(["BidderName is required"]);
        if (req.BidAmount <= 0m)
            throw new ValidationException(["BidAmount must be greater than zero"]);

        var pkg = await db.TenderPackages
            .FirstOrDefaultAsync(p => p.Id == tenderPackageId && p.ProjectId == projectId, ct)
            ?? throw new NotFoundException("TenderPackage");

        if (pkg.State != TenderPackageState.Issued)
            throw new ConflictException(
                $"TenderPackage is in state {pkg.State}; tenders can only be submitted against Issued packages");

        var t = new Tender
        {
            ProjectId          = projectId,
            TenderPackageId    = tenderPackageId,
            BidderName         = req.BidderName.Trim(),
            BidderOrganisation = req.BidderOrganisation,
            ContactEmail       = req.ContactEmail,
            BidAmount          = req.BidAmount,
            SubmittedAt        = DateTime.UtcNow,
            State              = TenderState.Submitted,
            CreatedById        = actorId,
        };
        db.Tenders.Add(t);

        await audit.WriteAsync(actorId, "tender.submitted", "Tender",
            t.Id.ToString(), projectId,
            detail: new
            {
                tenderPackageId    = tenderPackageId,
                tenderPackageNumber = pkg.Number,
                bidderName         = t.BidderName,
                bidderOrganisation = t.BidderOrganisation,
                bidAmount          = t.BidAmount,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return t;
    }

    /// <summary>
    /// Bidder withdraws: Submitted → Withdrawn. Note required so
    /// the audit trail captures the rationale. Withdrawn is
    /// terminal — once withdrawn the bid is out of evaluation
    /// scope. v1.0 doesn't allow re-submission of the same bidder
    /// in the same package (the audit chain is preferable to the
    /// workflow churn); a fresh bid would be a new Tender row.
    /// </summary>
    public async Task<Tender> WithdrawAsync(
        Guid projectId, Guid tenderId, WithdrawTenderRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Note))
            throw new ValidationException(["Note is required"]);

        var t = await db.Tenders
            .FirstOrDefaultAsync(x => x.Id == tenderId && x.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Tender");

        if (t.State != TenderState.Submitted)
            throw new ConflictException(
                $"Tender is in state {t.State}; only Submitted tenders can be withdrawn");

        t.State     = TenderState.Withdrawn;
        t.StateNote = req.Note.Trim();

        await audit.WriteAsync(actorId, "tender.withdrawn", "Tender",
            t.Id.ToString(), projectId,
            detail: new { bidderName = t.BidderName, note = req.Note },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return t;
    }

    public async Task<List<Tender>> ListAsync(
        Guid projectId, Guid tenderPackageId, CancellationToken ct = default)
    {
        // Validate the package belongs to the project (cross-tenant
        // 404 via the query filter); empty list if no tenders yet.
        _ = await db.TenderPackages
            .FirstOrDefaultAsync(p => p.Id == tenderPackageId && p.ProjectId == projectId, ct)
            ?? throw new NotFoundException("TenderPackage");
        return await db.Tenders
            .Where(t => t.TenderPackageId == tenderPackageId)
            .OrderBy(t => t.BidAmount)
            .ToListAsync(ct);
    }

    public Task<Tender> GetAsync(
        Guid projectId, Guid tenderId, CancellationToken ct = default)
        => LoadAsync(projectId, tenderId, ct);

    private async Task<Tender> LoadAsync(
        Guid projectId, Guid tenderId, CancellationToken ct)
        => await db.Tenders
            .FirstOrDefaultAsync(t => t.Id == tenderId && t.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Tender");
}

/// <summary>
/// EvaluationService — criteria CRUD + per-tender score recording
/// + matrix calculation (T-S6-05, PAFM-SD F.7 third bullet).
/// Criteria can only be added / removed when the parent
/// TenderPackage is in Draft state — once Issued the criteria are
/// frozen so bidders see stable evaluation rules. Scores can be
/// recorded once the package is Issued and tenders are Submitted.
/// Weight-sum invariant (Σ ≈ 1.0) is checked at matrix-calc time
/// rather than at criterion-write time, allowing weight edits
/// during Draft set-up.
/// </summary>
public class EvaluationService(CimsDbContext db, AuditService audit)
{
    // ── Criteria ────────────────────────────────────────────────────

    public async Task<EvaluationCriterion> AddCriterionAsync(
        Guid projectId, Guid tenderPackageId, AddEvaluationCriterionRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ValidationException(["Name is required"]);
        if (req.Weight < 0m || req.Weight > 1m)
            throw new ValidationException(["Weight must be in [0, 1]"]);

        var pkg = await db.TenderPackages
            .FirstOrDefaultAsync(p => p.Id == tenderPackageId && p.ProjectId == projectId, ct)
            ?? throw new NotFoundException("TenderPackage");
        if (pkg.State != TenderPackageState.Draft)
            throw new ConflictException(
                $"TenderPackage is in state {pkg.State}; criteria can only be added to Draft packages");

        var c = new EvaluationCriterion
        {
            ProjectId       = projectId,
            TenderPackageId = tenderPackageId,
            Name            = req.Name.Trim(),
            Type            = req.Type,
            Weight          = req.Weight,
        };
        db.EvaluationCriteria.Add(c);
        await audit.WriteAsync(actorId, "evaluation_criterion.added", "EvaluationCriterion",
            c.Id.ToString(), projectId,
            detail: new { tenderPackageId, name = c.Name, type = c.Type.ToString(), weight = c.Weight },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return c;
    }

    public async Task<EvaluationCriterion> UpdateCriterionAsync(
        Guid projectId, Guid tenderPackageId, Guid criterionId,
        UpdateEvaluationCriterionRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var c = await db.EvaluationCriteria
            .FirstOrDefaultAsync(x => x.Id == criterionId
                                    && x.TenderPackageId == tenderPackageId
                                    && x.ProjectId == projectId, ct)
            ?? throw new NotFoundException("EvaluationCriterion");

        var pkg = await db.TenderPackages
            .FirstAsync(p => p.Id == tenderPackageId && p.ProjectId == projectId, ct);
        if (pkg.State != TenderPackageState.Draft)
            throw new ConflictException(
                $"TenderPackage is in state {pkg.State}; criteria can only be edited in Draft");

        var changed = new List<string>();
        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                throw new ValidationException(["Name cannot be empty"]);
            c.Name = req.Name.Trim();
            changed.Add("Name");
        }
        if (req.Type.HasValue) { c.Type = req.Type.Value; changed.Add("Type"); }
        if (req.Weight.HasValue)
        {
            if (req.Weight.Value < 0m || req.Weight.Value > 1m)
                throw new ValidationException(["Weight must be in [0, 1]"]);
            c.Weight = req.Weight.Value;
            changed.Add("Weight");
        }
        if (changed.Count == 0)
            throw new ValidationException(["No updatable fields provided"]);

        await audit.WriteAsync(actorId, "evaluation_criterion.updated", "EvaluationCriterion",
            c.Id.ToString(), projectId,
            detail: new { changedFields = changed }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return c;
    }

    public async Task RemoveCriterionAsync(
        Guid projectId, Guid tenderPackageId, Guid criterionId, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var c = await db.EvaluationCriteria
            .FirstOrDefaultAsync(x => x.Id == criterionId
                                    && x.TenderPackageId == tenderPackageId
                                    && x.ProjectId == projectId, ct)
            ?? throw new NotFoundException("EvaluationCriterion");

        var pkg = await db.TenderPackages
            .FirstAsync(p => p.Id == tenderPackageId && p.ProjectId == projectId, ct);
        if (pkg.State != TenderPackageState.Draft)
            throw new ConflictException(
                $"TenderPackage is in state {pkg.State}; criteria can only be removed in Draft");

        db.EvaluationCriteria.Remove(c);
        await audit.WriteAsync(actorId, "evaluation_criterion.removed", "EvaluationCriterion",
            c.Id.ToString(), projectId,
            detail: new { tenderPackageId, name = c.Name }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<EvaluationCriterion>> ListCriteriaAsync(
        Guid projectId, Guid tenderPackageId, CancellationToken ct = default)
    {
        _ = await db.TenderPackages
            .FirstOrDefaultAsync(p => p.Id == tenderPackageId && p.ProjectId == projectId, ct)
            ?? throw new NotFoundException("TenderPackage");
        return await db.EvaluationCriteria
            .Where(c => c.TenderPackageId == tenderPackageId)
            .OrderBy(c => c.Type).ThenBy(c => c.Name)
            .ToListAsync(ct);
    }

    // ── Scores ──────────────────────────────────────────────────────

    /// <summary>
    /// Set or update the score for a (Tender, Criterion) pair.
    /// Validates: score in [0, 100]; tender in Submitted or
    /// Evaluated state (not Awarded / Rejected / Withdrawn);
    /// criterion belongs to the tender's package; package is
    /// Issued (scoring happens during the evaluation window after
    /// issue and before award).
    /// Re-scoring updates in place; a separate audit row is
    /// emitted each time so the trail is complete.
    /// </summary>
    public async Task<EvaluationScore> SetScoreAsync(
        Guid projectId, Guid tenderId, Guid criterionId, SetEvaluationScoreRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (req.Score < 0m || req.Score > 100m)
            throw new ValidationException(["Score must be in [0, 100]"]);

        var tender = await db.Tenders
            .FirstOrDefaultAsync(t => t.Id == tenderId && t.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Tender");
        if (tender.State is TenderState.Withdrawn or TenderState.Awarded or TenderState.Rejected)
            throw new ConflictException(
                $"Tender is in state {tender.State}; scoring is closed");

        var criterion = await db.EvaluationCriteria
            .FirstOrDefaultAsync(c => c.Id == criterionId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("EvaluationCriterion");
        if (criterion.TenderPackageId != tender.TenderPackageId)
            throw new ValidationException(["Criterion belongs to a different TenderPackage than the Tender"]);

        var pkg = await db.TenderPackages
            .FirstAsync(p => p.Id == tender.TenderPackageId, ct);
        if (pkg.State != TenderPackageState.Issued)
            throw new ConflictException(
                $"TenderPackage is in state {pkg.State}; scoring is only allowed when Issued");

        var existing = await db.EvaluationScores
            .FirstOrDefaultAsync(s => s.TenderId == tenderId && s.CriterionId == criterionId, ct);
        var isCreate = existing is null;
        var s = existing ?? new EvaluationScore
        {
            ProjectId   = projectId,
            TenderId    = tenderId,
            CriterionId = criterionId,
        };
        s.Score      = req.Score;
        s.Notes      = req.Notes;
        s.ScoredById = actorId;
        s.ScoredAt   = DateTime.UtcNow;
        if (isCreate) db.EvaluationScores.Add(s);

        await audit.WriteAsync(actorId, "evaluation_score.set", "EvaluationScore",
            s.Id.ToString(), projectId,
            detail: new
            {
                tenderId, criterionId,
                score = req.Score,
                isUpdate = !isCreate,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return s;
    }

    // ── Matrix ──────────────────────────────────────────────────────

    /// <summary>
    /// Build the evaluation matrix for a TenderPackage. Returns
    /// per-tender weighted overall scores plus the per-criterion
    /// breakdown. TotalWeight + IsValid are reported alongside —
    /// the caller can render "weights sum to 0.85; please fix"
    /// when IsValid is false. Withdrawn tenders are excluded
    /// (they're out of scope for evaluation).
    /// </summary>
    public async Task<EvaluationMatrixDto> GetMatrixAsync(
        Guid projectId, Guid tenderPackageId, CancellationToken ct = default)
    {
        _ = await db.TenderPackages
            .FirstOrDefaultAsync(p => p.Id == tenderPackageId && p.ProjectId == projectId, ct)
            ?? throw new NotFoundException("TenderPackage");

        var criteria = await db.EvaluationCriteria
            .Where(c => c.TenderPackageId == tenderPackageId)
            .OrderBy(c => c.Type).ThenBy(c => c.Name)
            .ToListAsync(ct);
        var tenders = await db.Tenders
            .Where(t => t.TenderPackageId == tenderPackageId
                     && t.State != TenderState.Withdrawn)
            .OrderBy(t => t.BidAmount)
            .ToListAsync(ct);
        var scores = await db.EvaluationScores
            .Where(s => criteria.Select(c => c.Id).Contains(s.CriterionId))
            .ToListAsync(ct);

        // Pure-function aggregator.
        var coreInput = criteria.Select(c =>
            new CimsApp.Core.EvaluationMatrix.CriterionInput(c.Id, c.Weight)).ToList();
        var coreScores = scores.Select(s =>
            new CimsApp.Core.EvaluationMatrix.ScoreInput(s.TenderId, s.CriterionId, s.Score)).ToList();
        var matrixResult = CimsApp.Core.EvaluationMatrix.Compute(
            tenders.Select(t => t.Id).ToList(), coreInput, coreScores);

        // Compose the DTO. Score lookup for the per-cell breakdown.
        var scoresByPair = scores
            .ToDictionary(s => (s.TenderId, s.CriterionId), s => s);
        var resultsByTender = matrixResult.Tenders
            .ToDictionary(r => r.TenderId, r => r.OverallScore);

        var rows = tenders.Select(t => new EvaluationMatrixRowDto(
            t.Id, t.BidderName, t.BidAmount, t.State,
            resultsByTender.TryGetValue(t.Id, out var os) ? os : null,
            criteria.Select(c =>
            {
                scoresByPair.TryGetValue((t.Id, c.Id), out var s);
                return new EvaluationMatrixCellDto(
                    c.Id, c.Name, c.Type, c.Weight,
                    s?.Score, s?.Notes);
            }).ToList())).ToList();

        return new EvaluationMatrixDto(
            tenderPackageId,
            matrixResult.TotalWeight,
            matrixResult.IsValid,
            rows);
    }
}

/// <summary>
/// EarlyWarningsService — NEC4 clause-15 early-warning notices
/// per Contract (T-S6-07, PAFM-SD F.7 fifth bullet). Linear
/// 3-state workflow Raised → UnderReview → Closed; the inline
/// service-layer guard does the state-machine work since it's
/// only three states (no separate Core/<X>.cs file warranted).
/// </summary>
public class EarlyWarningsService(CimsDbContext db, AuditService audit)
{
    /// <summary>
    /// Raise a new early-warning notice against an Active Contract.
    /// Title required. Initial state Raised. Audit:
    /// early_warning.raised.
    /// </summary>
    public async Task<EarlyWarning> RaiseAsync(
        Guid projectId, Guid contractId, RaiseEarlyWarningRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ValidationException(["Title is required"]);

        var contract = await db.Contracts
            .FirstOrDefaultAsync(c => c.Id == contractId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Contract");
        if (contract.State != ContractState.Active)
            throw new ConflictException(
                $"Contract is in state {contract.State}; early warnings can only be raised against Active contracts");

        var w = new EarlyWarning
        {
            ProjectId   = projectId,
            ContractId  = contractId,
            Title       = req.Title.Trim(),
            Description = req.Description,
            State       = EarlyWarningState.Raised,
            RaisedById  = actorId,
            RaisedAt    = DateTime.UtcNow,
        };
        db.EarlyWarnings.Add(w);

        await audit.WriteAsync(actorId, "early_warning.raised", "EarlyWarning",
            w.Id.ToString(), projectId,
            detail: new
            {
                contractId,
                contractNumber = contract.Number,
                title = w.Title,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return w;
    }

    /// <summary>
    /// Review transition: Raised → UnderReview. ResponseNote
    /// required — the reviewer's analysis is the whole point of
    /// the review step.
    /// </summary>
    public async Task<EarlyWarning> ReviewAsync(
        Guid projectId, Guid earlyWarningId, ReviewEarlyWarningRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ResponseNote))
            throw new ValidationException(["ResponseNote is required"]);

        var w = await LoadAsync(projectId, earlyWarningId, ct);
        if (w.State != EarlyWarningState.Raised)
            throw new ConflictException(
                $"EarlyWarning is in state {w.State}; only Raised early warnings can be reviewed");

        w.State        = EarlyWarningState.UnderReview;
        w.ReviewedById = actorId;
        w.ReviewedAt   = DateTime.UtcNow;
        w.ResponseNote = req.ResponseNote.Trim();

        await audit.WriteAsync(actorId, "early_warning.reviewed", "EarlyWarning",
            w.Id.ToString(), projectId,
            detail: new { title = w.Title }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return w;
    }

    /// <summary>
    /// Close transition: UnderReview → Closed. Optional note.
    /// Closed is terminal.
    /// </summary>
    public async Task<EarlyWarning> CloseAsync(
        Guid projectId, Guid earlyWarningId, CloseEarlyWarningRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var w = await LoadAsync(projectId, earlyWarningId, ct);
        if (w.State != EarlyWarningState.UnderReview)
            throw new ConflictException(
                $"EarlyWarning is in state {w.State}; only UnderReview early warnings can be closed");

        w.State       = EarlyWarningState.Closed;
        w.ClosedById  = actorId;
        w.ClosedAt    = DateTime.UtcNow;
        w.ClosureNote = req.ClosureNote;

        await audit.WriteAsync(actorId, "early_warning.closed", "EarlyWarning",
            w.Id.ToString(), projectId,
            detail: new { title = w.Title }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return w;
    }

    public async Task<List<EarlyWarning>> ListAsync(
        Guid projectId, Guid contractId, EarlyWarningState? state, CancellationToken ct = default)
    {
        _ = await db.Contracts
            .FirstOrDefaultAsync(c => c.Id == contractId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Contract");
        var q = db.EarlyWarnings.Where(w => w.ContractId == contractId);
        if (state.HasValue) q = q.Where(w => w.State == state.Value);
        return await q.OrderByDescending(w => w.RaisedAt).ToListAsync(ct);
    }

    public Task<EarlyWarning> GetAsync(
        Guid projectId, Guid earlyWarningId, CancellationToken ct = default)
        => LoadAsync(projectId, earlyWarningId, ct);

    private async Task<EarlyWarning> LoadAsync(
        Guid projectId, Guid earlyWarningId, CancellationToken ct)
        => await db.EarlyWarnings
            .FirstOrDefaultAsync(w => w.Id == earlyWarningId && w.ProjectId == projectId, ct)
            ?? throw new NotFoundException("EarlyWarning");
}

/// <summary>
/// CompensationEventsService — NEC4 clause-60.1 5-state workflow
/// per Contract (T-S6-08, PAFM-SD F.7 fifth bullet). State machine
/// in <see cref="CimsApp.Core.CompensationEventWorkflow"/>; this
/// service wraps with persistence + audit-twin emission. v1.0
/// limitations explicitly deferred to v1.1 inline (B-048 PM 4-week
/// notification deadline; B-049 contractor 3-week quotation
/// deadline; B-050 risk-allowance pricing rules).
/// </summary>
public class CompensationEventsService(CimsDbContext db, AuditService audit)
{
    /// <summary>
    /// Notify a new compensation event against an Active Contract.
    /// Title required. Auto-generates `CE-NNNN` per project.
    /// Initial state Notified. Audit:
    /// compensation_event.notified.
    /// </summary>
    public async Task<CompensationEvent> NotifyAsync(
        Guid projectId, Guid contractId, NotifyCompensationEventRequest req, Guid actorId,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ValidationException(["Title is required"]);

        var contract = await db.Contracts
            .FirstOrDefaultAsync(c => c.Id == contractId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Contract");
        if (contract.State != ContractState.Active)
            throw new ConflictException(
                $"Contract is in state {contract.State}; CEs can only be notified against Active contracts");

        // Concurrency note: same shape as Variation / ChangeRequest /
        // TenderPackage — count + 1 racing produces duplicates that
        // the unique (ProjectId, Number) index catches at SaveChanges.
        var count = await db.CompensationEvents
            .CountAsync(c => c.ProjectId == projectId, ct);

        var ce = new CompensationEvent
        {
            ProjectId    = projectId,
            ContractId   = contractId,
            Number       = $"CE-{(count + 1):D4}",
            Title        = req.Title.Trim(),
            Description  = req.Description,
            State        = CompensationEventState.Notified,
            NotifiedById = actorId,
            NotifiedAt   = DateTime.UtcNow,
        };
        db.CompensationEvents.Add(ce);

        await audit.WriteAsync(actorId, "compensation_event.notified", "CompensationEvent",
            ce.Id.ToString(), projectId,
            detail: new
            {
                number         = ce.Number,
                contractId,
                contractNumber = contract.Number,
                title          = ce.Title,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ce;
    }

    /// <summary>
    /// Quote transition: Notified → Quoted. Records the contractor's
    /// quotation (cost + time impact + rationale). State machine +
    /// role gate via CompensationEventWorkflow.CanTransition
    /// (TaskTeamMember+ — contractor-side input).
    /// </summary>
    public async Task<CompensationEvent> QuoteAsync(
        Guid projectId, Guid compensationEventId, QuoteCompensationEventRequest req,
        Guid actorId, UserRole role,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.QuotationNote))
            throw new ValidationException(["QuotationNote is required"]);
        if (req.EstimatedCostImpact < 0m)
            throw new ValidationException(["EstimatedCostImpact cannot be negative"]);

        var ce = await LoadAsync(projectId, compensationEventId, ct);
        EnforceTransition(ce.State, CompensationEventState.Quoted, role);

        ce.State                    = CompensationEventState.Quoted;
        ce.EstimatedCostImpact      = req.EstimatedCostImpact;
        ce.EstimatedTimeImpactDays  = req.EstimatedTimeImpactDays;
        ce.QuotationNote            = req.QuotationNote.Trim();
        ce.QuotedById               = actorId;
        ce.QuotedAt                 = DateTime.UtcNow;

        await audit.WriteAsync(actorId, "compensation_event.quoted", "CompensationEvent",
            ce.Id.ToString(), projectId,
            detail: new
            {
                number         = ce.Number,
                costImpact     = req.EstimatedCostImpact,
                timeImpactDays = req.EstimatedTimeImpactDays,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ce;
    }

    /// <summary>
    /// Accept transition: Quoted → Accepted. DecisionNote required.
    /// Role gate ProjectManager+.
    /// </summary>
    public async Task<CompensationEvent> AcceptAsync(
        Guid projectId, Guid compensationEventId, DecideCompensationEventRequest req,
        Guid actorId, UserRole role,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.DecisionNote))
            throw new ValidationException(["DecisionNote is required"]);

        var ce = await LoadAsync(projectId, compensationEventId, ct);
        EnforceTransition(ce.State, CompensationEventState.Accepted, role);

        ce.State        = CompensationEventState.Accepted;
        ce.DecisionById = actorId;
        ce.DecisionAt   = DateTime.UtcNow;
        ce.DecisionNote = req.DecisionNote.Trim();

        await audit.WriteAsync(actorId, "compensation_event.accepted", "CompensationEvent",
            ce.Id.ToString(), projectId,
            detail: new
            {
                number         = ce.Number,
                costImpact     = ce.EstimatedCostImpact,
                timeImpactDays = ce.EstimatedTimeImpactDays,
            }, ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ce;
    }

    /// <summary>
    /// Reject transition: Notified → Rejected (clause 61.4) OR
    /// Quoted → Rejected. DecisionNote required. Role gate
    /// ProjectManager+.
    /// </summary>
    public async Task<CompensationEvent> RejectAsync(
        Guid projectId, Guid compensationEventId, DecideCompensationEventRequest req,
        Guid actorId, UserRole role,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.DecisionNote))
            throw new ValidationException(["DecisionNote is required"]);

        var ce = await LoadAsync(projectId, compensationEventId, ct);
        EnforceTransition(ce.State, CompensationEventState.Rejected, role);

        ce.State        = CompensationEventState.Rejected;
        ce.DecisionById = actorId;
        ce.DecisionAt   = DateTime.UtcNow;
        ce.DecisionNote = req.DecisionNote.Trim();

        await audit.WriteAsync(actorId, "compensation_event.rejected", "CompensationEvent",
            ce.Id.ToString(), projectId,
            detail: new { number = ce.Number, decisionNote = req.DecisionNote },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ce;
    }

    /// <summary>
    /// Implement transition: Accepted → Implemented. Optional note.
    /// Terminal state.
    /// </summary>
    public async Task<CompensationEvent> ImplementAsync(
        Guid projectId, Guid compensationEventId, ImplementCompensationEventRequest req,
        Guid actorId, UserRole role,
        string? ip = null, string? ua = null, CancellationToken ct = default)
    {
        var ce = await LoadAsync(projectId, compensationEventId, ct);
        EnforceTransition(ce.State, CompensationEventState.Implemented, role);

        ce.State           = CompensationEventState.Implemented;
        ce.ImplementedById = actorId;
        ce.ImplementedAt   = DateTime.UtcNow;

        await audit.WriteAsync(actorId, "compensation_event.implemented", "CompensationEvent",
            ce.Id.ToString(), projectId,
            detail: new { number = ce.Number, note = req.Note },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ce;
    }

    public async Task<List<CompensationEvent>> ListAsync(
        Guid projectId, Guid contractId, CompensationEventState? state, CancellationToken ct = default)
    {
        _ = await db.Contracts
            .FirstOrDefaultAsync(c => c.Id == contractId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("Contract");
        var q = db.CompensationEvents.Where(c => c.ContractId == contractId);
        if (state.HasValue) q = q.Where(c => c.State == state.Value);
        return await q.OrderByDescending(c => c.NotifiedAt).ToListAsync(ct);
    }

    public Task<CompensationEvent> GetAsync(
        Guid projectId, Guid compensationEventId, CancellationToken ct = default)
        => LoadAsync(projectId, compensationEventId, ct);

    private async Task<CompensationEvent> LoadAsync(
        Guid projectId, Guid compensationEventId, CancellationToken ct)
        => await db.CompensationEvents
            .FirstOrDefaultAsync(c => c.Id == compensationEventId && c.ProjectId == projectId, ct)
            ?? throw new NotFoundException("CompensationEvent");

    private static void EnforceTransition(
        CompensationEventState from, CompensationEventState to, UserRole role)
    {
        if (!CimsApp.Core.CompensationEventWorkflow.IsValidTransition(from, to))
            throw new ConflictException(
                $"Cannot transition CompensationEvent from {from} to {to}");
        if (!CimsApp.Core.CompensationEventWorkflow.CanTransition(from, to, role))
            throw new ForbiddenException(
                $"Role {role} cannot perform the {from} → {to} transition");
    }
}

/// <summary>
/// DashboardsService — per-role aggregation views (T-S7-02,
/// PAFM-SD F.8 first bullet — "Role-specific dashboards (PM, CM,
/// SM, IM, HSE, Client)"). Read-only; no mutations and no audit
/// twin. Each per-role method returns a uniform
/// <see cref="DashboardDto"/> with a list of
/// <see cref="DashboardCardDto"/> rows. The HSE dashboard is
/// sparse in v1.0 because the HSE module is S12 — explicit
/// B-059 backlog entry covers the deferral.
///
/// Performance note: each dashboard runs ~5-8 small COUNT /
/// AGG queries per request. Top-3 risk #2 in the kickoff: if
/// pilot profiling shows real latency, the v1.1 candidate is a
/// single-batched query per dashboard.
/// </summary>
public class DashboardsService(CimsDbContext db)
{
    public async Task<DashboardDto> GetPmDashboardAsync(
        Guid projectId, CancellationToken ct = default)
    {
        var p = await LoadProjectAsync(projectId, ct);

        var openRfis = await db.Rfis.CountAsync(
            r => r.ProjectId == projectId
              && r.Status != RfiStatus.Closed && r.Status != RfiStatus.Cancelled, ct);
        var openActions = await db.ActionItems.CountAsync(
            a => a.ProjectId == projectId
              && (a.Status == ActionStatus.Open || a.Status == ActionStatus.InProgress), ct);
        var openChangeRequests = await db.ChangeRequests.CountAsync(
            c => c.ProjectId == projectId
              && c.State != ChangeRequestState.Rejected
              && c.State != ChangeRequestState.Closed, ct);
        var openEarlyWarnings = await db.EarlyWarnings.CountAsync(
            w => w.ProjectId == projectId && w.State != EarlyWarningState.Closed, ct);
        var openCompensationEvents = await db.CompensationEvents.CountAsync(
            c => c.ProjectId == projectId
              && c.State != CompensationEventState.Rejected
              && c.State != CompensationEventState.Implemented, ct);
        var openRisks = await db.Risks.CountAsync(
            r => r.Project.Id == projectId && r.Status != RiskStatus.Closed, ct);

        return new DashboardDto("PM", p.Id, p.Name, p.Code, new List<DashboardCardDto>
        {
            new("Open RFIs",                 openRfis.ToString(),                DashboardCardType.Count, null),
            new("Open Actions",              openActions.ToString(),             DashboardCardType.Count, null),
            new("Open Change Requests",      openChangeRequests.ToString(),      DashboardCardType.Count, null),
            new("Open Early Warnings",       openEarlyWarnings.ToString(),       DashboardCardType.Count, null),
            new("Open Compensation Events",  openCompensationEvents.ToString(),  DashboardCardType.Count, null),
            new("Open Risks",                openRisks.ToString(),               DashboardCardType.Count, null),
        });
    }

    public async Task<DashboardDto> GetCmDashboardAsync(
        Guid projectId, CancellationToken ct = default)
    {
        var p = await LoadProjectAsync(projectId, ct);

        var totalBudget = await db.CostBreakdownItems
            .Where(c => c.ProjectId == projectId && c.Budget.HasValue)
            .SumAsync(c => c.Budget!.Value, ct);
        var totalCommitted = await db.Commitments
            .Where(c => c.ProjectId == projectId)
            .SumAsync(c => (decimal?)c.Amount, ct) ?? 0m;
        var totalActuals = await db.ActualCosts
            .Where(a => a.ProjectId == projectId)
            .SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;
        var raisedVariations = await db.Variations.CountAsync(
            v => v.ProjectId == projectId && v.State == VariationState.Raised, ct);
        var approvedVariations = await db.Variations.CountAsync(
            v => v.ProjectId == projectId && v.State == VariationState.Approved, ct);
        var latestPaymentCert = await db.PaymentCertificates
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new { c.CertificateNumber, c.State })
            .FirstOrDefaultAsync(ct);

        var cards = new List<DashboardCardDto>
        {
            new("Total CBS Budget",     FormatCurrency(totalBudget,    p.Currency), DashboardCardType.Currency, null),
            new("Total Committed",      FormatCurrency(totalCommitted, p.Currency), DashboardCardType.Currency, null),
            new("Total Actuals",        FormatCurrency(totalActuals,   p.Currency), DashboardCardType.Currency, null),
            new("Raised Variations",    raisedVariations.ToString(),                DashboardCardType.Count, null),
            new("Approved Variations",  approvedVariations.ToString(),              DashboardCardType.Count, null),
            new("Latest Payment Cert",
                latestPaymentCert?.CertificateNumber ?? "—",
                DashboardCardType.Text,
                latestPaymentCert is null ? "No certificate yet" : $"State: {latestPaymentCert.State}"),
        };
        return new DashboardDto("CM", p.Id, p.Name, p.Code, cards);
    }

    public async Task<DashboardDto> GetSmDashboardAsync(
        Guid projectId, CancellationToken ct = default)
    {
        var p = await LoadProjectAsync(projectId, ct);

        var activeLookaheads = await db.LookaheadEntries.CountAsync(
            le => le.ProjectId == projectId && le.IsActive, ct);
        var latestWwp = await db.WeeklyWorkPlans
            .Where(w => w.ProjectId == projectId)
            .OrderByDescending(w => w.WeekStarting)
            .Select(w => new { w.Id, w.WeekStarting })
            .FirstOrDefaultAsync(ct);

        // PPC computed-on-read for the latest WWP, mirroring
        // LpsService.GetWeeklyWorkPlanAsync's compute path.
        decimal? latestPpc = null;
        if (latestWwp is not null)
        {
            var commits = await db.WeeklyTaskCommitments
                .Where(c => c.WeeklyWorkPlanId == latestWwp.Id)
                .Select(c => new { c.Committed, c.Completed })
                .ToListAsync(ct);
            var committedCount = commits.Count(c => c.Committed);
            var completedCount = commits.Count(c => c.Completed);
            if (committedCount > 0)
                latestPpc = Math.Round(100m * completedCount / committedCount, 2);
        }

        var openActions = await db.ActionItems.CountAsync(
            a => a.ProjectId == projectId
              && (a.Status == ActionStatus.Open || a.Status == ActionStatus.InProgress), ct);
        var openEarlyWarnings = await db.EarlyWarnings.CountAsync(
            w => w.ProjectId == projectId && w.State != EarlyWarningState.Closed, ct);

        return new DashboardDto("SM", p.Id, p.Name, p.Code, new List<DashboardCardDto>
        {
            new("Active Lookaheads",    activeLookaheads.ToString(), DashboardCardType.Count, null),
            new("Latest WWP",
                latestWwp?.WeekStarting.ToString("yyyy-MM-dd") ?? "—",
                DashboardCardType.Date,
                latestWwp is null ? "No weekly plan yet" : null),
            new("Latest WWP PPC",
                latestPpc?.ToString("0.##") ?? "—",
                DashboardCardType.Percentage,
                latestPpc is null ? "No commitments recorded" : null),
            new("Open Actions",          openActions.ToString(),       DashboardCardType.Count, null),
            new("Open Early Warnings",   openEarlyWarnings.ToString(), DashboardCardType.Count, null),
        });
    }

    public async Task<DashboardDto> GetImDashboardAsync(
        Guid projectId, CancellationToken ct = default)
    {
        var p = await LoadProjectAsync(projectId, ct);

        var docCounts = await db.Documents
            .Where(d => d.ProjectId == projectId)
            .GroupBy(d => d.CurrentState)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var byState = docCounts.ToDictionary(g => g.State, g => g.Count);

        var openRfis = await db.Rfis.CountAsync(
            r => r.ProjectId == projectId
              && r.Status != RfiStatus.Closed && r.Status != RfiStatus.Cancelled, ct);
        var crsAwaitingAssessment = await db.ChangeRequests.CountAsync(
            c => c.ProjectId == projectId && c.State == ChangeRequestState.Raised, ct);

        return new DashboardDto("IM", p.Id, p.Name, p.Code, new List<DashboardCardDto>
        {
            new("Docs - Work in Progress", (byState.GetValueOrDefault(CdeState.WorkInProgress)).ToString(), DashboardCardType.Count, null),
            new("Docs - Shared",           (byState.GetValueOrDefault(CdeState.Shared)).ToString(),         DashboardCardType.Count, null),
            new("Docs - Published",        (byState.GetValueOrDefault(CdeState.Published)).ToString(),      DashboardCardType.Count, null),
            new("Docs - Archived",         (byState.GetValueOrDefault(CdeState.Archived)).ToString(),       DashboardCardType.Count, null),
            new("Open RFIs",               openRfis.ToString(),                                              DashboardCardType.Count, null),
            new("CRs Awaiting Assessment", crsAwaitingAssessment.ToString(),                                 DashboardCardType.Count, null),
        });
    }

    public async Task<DashboardDto> GetHseDashboardAsync(
        Guid projectId, CancellationToken ct = default)
    {
        var p = await LoadProjectAsync(projectId, ct);

        // HSE module integration is S12 (Genera Systems QA, HS&E
        // Integration). v1.0 dashboard is sparse — surfaces the
        // deferral explicitly so the UI can render a placeholder
        // with a "Coming in S12 (B-059)" message.
        return new DashboardDto("HSE", p.Id, p.Name, p.Code, new List<DashboardCardDto>
        {
            new("HSE Module",
                "Coming in S12",
                DashboardCardType.Text,
                "Per PAFM-SD roadmap; see backlog entry B-059 for HSE-specific dashboard cards"),
        });
    }

    public async Task<DashboardDto> GetClientDashboardAsync(
        Guid projectId, CancellationToken ct = default)
    {
        var p = await LoadProjectAsync(projectId, ct);

        var raisedVariations = await db.Variations.CountAsync(
            v => v.ProjectId == projectId && v.State == VariationState.Raised, ct);
        var approvedVariations = await db.Variations.CountAsync(
            v => v.ProjectId == projectId && v.State == VariationState.Approved, ct);

        // Project finish date best-estimate: max EarlyFinish across
        // active activities (post-CPM-recompute). Falls back to
        // Project.EndDate if no schedule exists, else null.
        var efs = await db.Activities
            .Where(a => a.ProjectId == projectId && a.IsActive && a.EarlyFinish.HasValue)
            .Select(a => a.EarlyFinish!.Value)
            .ToListAsync(ct);
        DateTime? estimatedFinish = efs.Count == 0 ? p.EndDate : efs.Max();

        var latestBaseline = await db.ScheduleBaselines
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.CapturedAt)
            .Select(b => new { b.Label, b.CapturedAt })
            .FirstOrDefaultAsync(ct);

        return new DashboardDto("Client", p.Id, p.Name, p.Code, new List<DashboardCardDto>
        {
            new("Project Status",         p.Status.ToString(),                            DashboardCardType.Text, null),
            new("Estimated Finish",
                estimatedFinish?.ToString("yyyy-MM-dd") ?? "—",
                DashboardCardType.Date,
                estimatedFinish is null ? "No schedule yet" : null),
            new("Raised Variations",      raisedVariations.ToString(),                    DashboardCardType.Count, null),
            new("Approved Variations",    approvedVariations.ToString(),                  DashboardCardType.Count, null),
            new("Latest Baseline",
                latestBaseline?.Label ?? "—",
                DashboardCardType.Text,
                latestBaseline is null ? "No baseline captured" : $"Captured {latestBaseline.CapturedAt:yyyy-MM-dd}"),
        });
    }

    private async Task<Project> LoadProjectAsync(Guid projectId, CancellationToken ct)
        => await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

    private static string FormatCurrency(decimal value, string currency)
        => $"{currency} {value:N2}";
}

// T-S7-03 Monthly Project Report (MPR) data aggregator.
// PAFM-SD F.8 second bullet. v1.0 produces a JSON-only DTO;
// PDF rendering deferred to v1.1 / B-055. Each section is a
// best-effort inference of the canonical PAFM Ch 30 layout
// (paste-on-request per reference_pafm_spec.md) — reconcile
// at B-055 time. Read-only — no audit / mutation.
public class ReportingService(CimsDbContext db)
{
    public async Task<MprDto> GenerateMonthlyProjectReportAsync(
        Guid projectId,
        DateTime? periodStart,
        DateTime? periodEnd,
        CancellationToken ct = default)
    {
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        // Default period = last full calendar month [00:00 UTC of
        // first-of-last-month, 00:00 UTC of first-of-this-month).
        // Caller can override via query string for ad-hoc windows.
        var now = DateTime.UtcNow;
        var defaultEnd = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = periodEnd ?? defaultEnd;
        var start = periodStart ?? end.AddMonths(-1);

        // ── Programme aggregates ────────────────────────────────
        var activities = await db.Activities
            .Where(a => a.ProjectId == projectId && a.IsActive)
            .Select(a => new { a.PercentComplete, a.EarlyStart, a.EarlyFinish })
            .ToListAsync(ct);
        var totalActivities = activities.Count;
        var completedActivities = activities.Count(a => a.PercentComplete >= 1m);
        decimal? percentComplete = totalActivities == 0
            ? null
            : Math.Round(100m * activities.Sum(a => a.PercentComplete) / totalActivities, 2);
        var earliestEarlyStart = activities
            .Where(a => a.EarlyStart.HasValue)
            .Select(a => (DateTime?)a.EarlyStart!.Value)
            .DefaultIfEmpty(null).Min();
        var latestEarlyFinish = activities
            .Where(a => a.EarlyFinish.HasValue)
            .Select(a => (DateTime?)a.EarlyFinish!.Value)
            .DefaultIfEmpty(null).Max();
        var latestBaseline = await db.ScheduleBaselines
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.CapturedAt)
            .Select(b => new { b.Label, b.CapturedAt })
            .FirstOrDefaultAsync(ct);

        // ── Cost aggregates ────────────────────────────────────
        var totalBudget = await db.CostBreakdownItems
            .Where(c => c.ProjectId == projectId && c.Budget.HasValue)
            .SumAsync(c => c.Budget!.Value, ct);
        var totalCommitted = await db.Commitments
            .Where(c => c.ProjectId == projectId)
            .SumAsync(c => (decimal?)c.Amount, ct) ?? 0m;
        var totalActuals = await db.ActualCosts
            .Where(a => a.ProjectId == projectId)
            .SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;
        decimal? percentSpent = totalBudget == 0m
            ? null
            : Math.Round(100m * totalActuals / totalBudget, 2);

        // ── Risk severity buckets ──────────────────────────────
        // v1.0 uses the standard PMI 5x5 split: high ≥ 15,
        // medium 7..12, low ≤ 6. Per-tenant thresholds are S14
        // Admin Console territory (kickoff Top-3 risk #3 of S2).
        var openRiskScores = await db.Risks
            .Where(r => r.ProjectId == projectId && r.Status != RiskStatus.Closed)
            .Select(r => r.Score)
            .ToListAsync(ct);
        var openHigh   = openRiskScores.Count(s => s >= 15);
        var openMed    = openRiskScores.Count(s => s >= 7 && s <= 12);
        var openLow    = openRiskScores.Count(s => s <= 6);
        var openRisks  = openRiskScores.Count;

        // ── Variations & changes (period-windowed) ─────────────
        var varsRaised = await db.Variations.CountAsync(
            v => v.ProjectId == projectId
              && v.CreatedAt >= start && v.CreatedAt < end, ct);
        var varsApprovedInPeriod = await db.Variations
            .Where(v => v.ProjectId == projectId
                     && v.State == VariationState.Approved
                     && v.DecidedAt.HasValue
                     && v.DecidedAt >= start && v.DecidedAt < end)
            .Select(v => v.EstimatedCostImpact)
            .ToListAsync(ct);
        var varsApprovedCount = varsApprovedInPeriod.Count;
        var varsApprovedValue = varsApprovedInPeriod.Sum(x => x ?? 0m);
        var crsRaised = await db.ChangeRequests.CountAsync(
            c => c.ProjectId == projectId
              && c.CreatedAt >= start && c.CreatedAt < end, ct);
        var crsApproved = await db.ChangeRequests.CountAsync(
            c => c.ProjectId == projectId
              && c.State == ChangeRequestState.Approved
              && c.UpdatedAt >= start && c.UpdatedAt < end, ct);

        // ── Open issues ────────────────────────────────────────
        var openRfis = await db.Rfis.CountAsync(
            r => r.ProjectId == projectId
              && r.Status != RfiStatus.Closed && r.Status != RfiStatus.Cancelled, ct);
        var openActions = await db.ActionItems.CountAsync(
            a => a.ProjectId == projectId
              && (a.Status == ActionStatus.Open || a.Status == ActionStatus.InProgress), ct);
        var openEarlyWarnings = await db.EarlyWarnings.CountAsync(
            w => w.ProjectId == projectId && w.State != EarlyWarningState.Closed, ct);
        var openCompensationEvents = await db.CompensationEvents.CountAsync(
            c => c.ProjectId == projectId
              && c.State != CompensationEventState.Rejected
              && c.State != CompensationEventState.Implemented, ct);
        var openIssuesTotal = openRfis + openActions
                            + openEarlyWarnings + openCompensationEvents;

        // ── Stakeholders ───────────────────────────────────────
        var stakeholdersTotal = await db.Stakeholders.CountAsync(
            s => s.ProjectId == projectId && s.IsActive, ct);
        var engagementsInPeriod = await db.EngagementLogs.CountAsync(
            e => e.ProjectId == projectId
              && e.OccurredAt >= start && e.OccurredAt < end, ct);
        var communicationsTotal = await db.CommunicationItems.CountAsync(
            c => c.ProjectId == projectId && c.IsActive, ct);

        // Estimated finish: max EarlyFinish across active activities,
        // mirroring the Client dashboard's logic. Falls back to
        // Project.EndDate if no schedule exists.
        var estimatedFinish = latestEarlyFinish ?? p.EndDate;

        return new MprDto(
            ProjectId:        p.Id,
            ProjectName:      p.Name,
            ProjectCode:      p.Code,
            PeriodStart:      start,
            PeriodEnd:        end,
            GeneratedAtUtc:   now,
            ExecutiveSummary: new MprExecutiveSummary(
                ProjectStatus:    p.Status.ToString(),
                PlannedEndDate:   p.EndDate,
                EstimatedEndDate: estimatedFinish,
                OpenRisksCount:   openRisks,
                OpenIssuesCount:  openIssuesTotal),
            Programme: new MprProgrammeStatus(
                TotalActivities:            totalActivities,
                CompletedActivities:        completedActivities,
                PercentComplete:            percentComplete,
                EarliestEarlyStart:         earliestEarlyStart,
                LatestEarlyFinish:          latestEarlyFinish,
                LatestBaselineLabel:        latestBaseline?.Label,
                LatestBaselineCapturedAt:   latestBaseline?.CapturedAt),
            Cost: new MprCostStatus(
                Currency:        p.Currency,
                TotalBudget:     totalBudget,
                TotalCommitted:  totalCommitted,
                TotalActuals:    totalActuals,
                PercentSpent:    percentSpent),
            Risk: new MprRiskStatus(
                OpenTotal:           openRisks,
                OpenHighSeverity:    openHigh,
                OpenMediumSeverity:  openMed,
                OpenLowSeverity:     openLow),
            Changes: new MprVariationsAndChanges(
                VariationsRaisedInPeriod:        varsRaised,
                VariationsApprovedInPeriod:      varsApprovedCount,
                VariationsApprovedValueInPeriod: varsApprovedValue,
                ChangeRequestsRaisedInPeriod:    crsRaised,
                ChangeRequestsApprovedInPeriod:  crsApproved),
            Issues: new MprIssues(
                OpenRfis:                openRfis,
                OpenActions:             openActions,
                OpenEarlyWarnings:       openEarlyWarnings,
                OpenCompensationEvents:  openCompensationEvents),
            Stakeholders: new MprStakeholderUpdates(
                StakeholdersTotal:       stakeholdersTotal,
                EngagementLogsInPeriod:  engagementsInPeriod,
                CommunicationsTotal:     communicationsTotal));
    }

    // T-S7-04 KPI cards. Project-level success-criteria dashboard
    // mapped to PAFM-SD Ch 2.6 v1.0 success criteria. Honest v1.0
    // proxies where genuine EVM (CPI / SPI) needs the v1.1 per-line
    // progress signal — subtitles call out which cards are proxies.
    public async Task<KpiCardsDto> GetProjectKpiCardsAsync(
        Guid projectId, CancellationToken ct = default)
    {
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var now    = DateTime.UtcNow;
        var thirty = now.AddDays(-30);

        // Module activity (last 30 days) — sum of new rows across
        // the user-facing modules. Direct row counts (not audit log)
        // because some modules emit audit records in atomic batches
        // that are awkward to per-entity bucket.
        var rfi30      = await db.Rfis.CountAsync(            r => r.ProjectId == projectId && r.CreatedAt >= thirty, ct);
        var actions30  = await db.ActionItems.CountAsync(     a => a.ProjectId == projectId && a.CreatedAt >= thirty, ct);
        var crs30      = await db.ChangeRequests.CountAsync(  c => c.ProjectId == projectId && c.CreatedAt >= thirty, ct);
        var vars30     = await db.Variations.CountAsync(      v => v.ProjectId == projectId && v.CreatedAt >= thirty, ct);
        var engs30     = await db.EngagementLogs.CountAsync(  e => e.ProjectId == projectId && e.CreatedAt >= thirty, ct);
        var moduleActivity = rfi30 + actions30 + crs30 + vars30 + engs30;

        // MPR data freshness — latest CostPeriod EndDate.
        var latestPeriodEnd = await db.CostPeriods
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.EndDate)
            .Select(c => (DateTime?)c.EndDate)
            .FirstOrDefaultAsync(ct);

        var criticalActivities = await db.Activities.CountAsync(
            a => a.ProjectId == projectId && a.IsActive && a.IsCritical, ct);

        var totalBudget = await db.CostBreakdownItems
            .Where(c => c.ProjectId == projectId && c.Budget.HasValue)
            .SumAsync(c => c.Budget!.Value, ct);
        var totalActuals = await db.ActualCosts
            .Where(a => a.ProjectId == projectId)
            .SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;
        decimal? percentSpent = totalBudget == 0m
            ? null
            : Math.Round(100m * totalActuals / totalBudget, 2);

        var actsList = await db.Activities
            .Where(a => a.ProjectId == projectId && a.IsActive)
            .Select(a => a.PercentComplete)
            .ToListAsync(ct);
        var totalActs     = actsList.Count;
        var completedActs = actsList.Count(pc => pc >= 1m);
        decimal? completionPct = totalActs == 0
            ? null
            : Math.Round(100m * completedActs / totalActs, 2);

        var closedRfis30 = await db.Rfis
            .Where(r => r.ProjectId == projectId
                     && r.Status == RfiStatus.Closed
                     && r.ClosedAt.HasValue
                     && r.ClosedAt >= thirty)
            .Select(r => new { r.CreatedAt, r.ClosedAt })
            .ToListAsync(ct);
        decimal? avgRfiDays = closedRfis30.Count == 0
            ? null
            : Math.Round(
                (decimal)closedRfis30.Average(
                    r => (r.ClosedAt!.Value - r.CreatedAt).TotalDays), 1);

        var overdueActions = await db.ActionItems.CountAsync(
            a => a.ProjectId == projectId
              && (a.Status == ActionStatus.Open || a.Status == ActionStatus.InProgress)
              && a.DueDate.HasValue && a.DueDate < now, ct);

        return new KpiCardsDto(p.Id, p.Name, p.Code, new List<DashboardCardDto>
        {
            new("Module Activity (Last 30d)",
                moduleActivity.ToString(), DashboardCardType.Count,
                "RFIs + Actions + CRs + Variations + Engagements"),
            new("MPR Period Coverage",
                latestPeriodEnd?.ToString("yyyy-MM-dd") ?? "—",
                DashboardCardType.Date,
                latestPeriodEnd is null
                    ? "No CostPeriod yet"
                    : "Latest CostPeriod end-date"),
            new("Critical Path Activities",
                criticalActivities.ToString(),
                DashboardCardType.Count,
                "CPM IsCritical = true"),
            new("Cost Spent vs Budget",
                percentSpent?.ToString("0.##") ?? "—",
                DashboardCardType.Percentage,
                "Proxy for CPI; genuine EVM-CPI requires v1.1 per-line progress signal"),
            new("Schedule Completion",
                completionPct?.ToString("0.##") ?? "—",
                DashboardCardType.Percentage,
                "Proxy for SPI; activities at 100% / total active"),
            new("RFI Avg Response (Last 30d, days)",
                avgRfiDays?.ToString("0.#") ?? "—",
                DashboardCardType.Text,
                avgRfiDays is null ? "No RFIs closed in window" : null),
            new("Overdue Actions",
                overdueActions.ToString(),
                DashboardCardType.Count,
                "Open / InProgress with DueDate in past"),
        });
    }
}

// T-S9-05 Master Information Delivery Plan (MIDP) service.
// PAFM-SD F.9 second bullet. Per-project list of planned
// information deliveries. v1.0 simple shape; full ISO 19650-2
// §5 information-requirements model is v1.1. Audit-twin per
// the ADR-0014 pattern.
public class MidpService(CimsDbContext db, AuditService audit)
{
    public async Task<List<MidpEntryDto>> ListAsync(Guid projectId, CancellationToken ct = default)
    {
        var rows = await db.MidpEntries
            .Where(x => x.ProjectId == projectId && x.IsActive)
            .OrderBy(x => x.DueDate)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<MidpEntryDto> GetAsync(Guid projectId, Guid id, CancellationToken ct = default)
    {
        var x = await db.MidpEntries
            .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId && e.IsActive, ct)
            ?? throw new NotFoundException("MidpEntry");
        return ToDto(x);
    }

    public async Task<MidpEntryDto> CreateAsync(
        Guid projectId, CreateMidpEntryRequest req,
        Guid actorId, string? ip, string? ua,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ValidationException(new List<string> { "Title is required" });
        if (req.DocTypeFilter is { } t && !CimsApp.Core.Iso19650Codes.TypeCodeSet.Contains(t))
            throw new ValidationException(new List<string>
            { $"DocTypeFilter '{t}' is not in the ISO 19650-2 Annex A type whitelist" });
        if (!await db.Users.AnyAsync(u => u.Id == req.OwnerId, ct))
            throw new NotFoundException("Owner");

        var entry = new MidpEntry
        {
            ProjectId = projectId,
            Title = req.Title.Trim(),
            Description = req.Description,
            DocTypeFilter = req.DocTypeFilter?.ToUpperInvariant(),
            DueDate = req.DueDate,
            OwnerId = req.OwnerId,
        };
        db.MidpEntries.Add(entry);
        await audit.WriteAsync(actorId, "midp_entry.created",
            "MidpEntry", entry.Id.ToString(),
            projectId: projectId,
            detail: new { entry.Title, entry.DueDate, entry.OwnerId, entry.DocTypeFilter },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ToDto(entry);
    }

    public async Task<MidpEntryDto> UpdateAsync(
        Guid projectId, Guid id, UpdateMidpEntryRequest req,
        Guid actorId, string? ip, string? ua,
        CancellationToken ct = default)
    {
        var x = await db.MidpEntries
            .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId && e.IsActive, ct)
            ?? throw new NotFoundException("MidpEntry");
        var changed = new List<string>();
        if (req.Title is not null && req.Title != x.Title)
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                throw new ValidationException(new List<string> { "Title cannot be empty" });
            x.Title = req.Title.Trim(); changed.Add(nameof(x.Title));
        }
        if (req.Description is not null && req.Description != x.Description)
        { x.Description = req.Description; changed.Add(nameof(x.Description)); }
        if (req.DocTypeFilter is not null && req.DocTypeFilter != x.DocTypeFilter)
        {
            var t = req.DocTypeFilter.ToUpperInvariant();
            if (!CimsApp.Core.Iso19650Codes.TypeCodeSet.Contains(t))
                throw new ValidationException(new List<string>
                { $"DocTypeFilter '{t}' is not in the ISO 19650-2 Annex A type whitelist" });
            x.DocTypeFilter = t; changed.Add(nameof(x.DocTypeFilter));
        }
        if (req.DueDate is { } d && d != x.DueDate)
        { x.DueDate = d; changed.Add(nameof(x.DueDate)); }
        if (req.OwnerId is { } o && o != x.OwnerId)
        {
            if (!await db.Users.AnyAsync(u => u.Id == o, ct))
                throw new NotFoundException("Owner");
            x.OwnerId = o; changed.Add(nameof(x.OwnerId));
        }
        if (changed.Count == 0) throw new ConflictException("No changes specified.");

        await audit.WriteAsync(actorId, "midp_entry.updated",
            "MidpEntry", x.Id.ToString(),
            projectId: projectId, detail: new { changedFields = changed },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ToDto(x);
    }

    public async Task<MidpEntryDto> CompleteAsync(
        Guid projectId, Guid id, CompleteMidpEntryRequest req,
        Guid actorId, string? ip, string? ua,
        CancellationToken ct = default)
    {
        var x = await db.MidpEntries
            .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId && e.IsActive, ct)
            ?? throw new NotFoundException("MidpEntry");
        if (x.IsCompleted) throw new ConflictException("MidpEntry is already completed.");

        if (req.DocumentId is { } docId)
        {
            var doc = await db.Documents
                .FirstOrDefaultAsync(d => d.Id == docId && d.ProjectId == projectId, ct)
                ?? throw new NotFoundException("Document");
            if (x.DocTypeFilter is { } expected && doc.DocType != expected)
                throw new ValidationException(new List<string>
                { $"Document.DocType '{doc.DocType}' does not match MidpEntry.DocTypeFilter '{expected}'" });
            x.DocumentId = docId;
        }
        x.IsCompleted = true;
        x.CompletedAt = DateTime.UtcNow;
        await audit.WriteAsync(actorId, "midp_entry.completed",
            "MidpEntry", x.Id.ToString(),
            projectId: projectId, documentId: x.DocumentId,
            detail: new { x.DocumentId },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ToDto(x);
    }

    public async Task DeleteAsync(
        Guid projectId, Guid id,
        Guid actorId, string? ip, string? ua,
        CancellationToken ct = default)
    {
        var x = await db.MidpEntries
            .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId && e.IsActive, ct)
            ?? throw new NotFoundException("MidpEntry");
        x.IsActive = false;
        await audit.WriteAsync(actorId, "midp_entry.deleted",
            "MidpEntry", x.Id.ToString(),
            projectId: projectId, detail: new { x.Title },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
    }

    private static MidpEntryDto ToDto(MidpEntry x) =>
        new(x.Id, x.ProjectId, x.Title, x.Description, x.DocTypeFilter,
            x.DueDate, x.OwnerId, x.DocumentId,
            x.IsCompleted, x.CompletedAt,
            x.CreatedAt, x.UpdatedAt);
}

// T-S9-06 Task Information Delivery Plan (TIDP) service.
// PAFM-SD F.9 third bullet. Per-team slice of a parent
// MidpEntry; v1.0 ships with TeamName as free text (B-069 for
// structured Team entity). Sign-off transitions are one-way:
// not-signed-off → signed-off (no un-sign-off in v1.0; that's
// a workflow concern v1.1 / B-NNN if pilot need surfaces).
public class TidpService(CimsDbContext db, AuditService audit)
{
    public async Task<List<TidpEntryDto>> ListAsync(Guid projectId, Guid? midpEntryId = null, CancellationToken ct = default)
    {
        var q = db.TidpEntries.Where(x => x.ProjectId == projectId && x.IsActive);
        if (midpEntryId.HasValue) q = q.Where(x => x.MidpEntryId == midpEntryId);
        var rows = await q.OrderBy(x => x.DueDate).ThenBy(x => x.TeamName).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<TidpEntryDto> GetAsync(Guid projectId, Guid id, CancellationToken ct = default)
    {
        var x = await db.TidpEntries
            .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId && e.IsActive, ct)
            ?? throw new NotFoundException("TidpEntry");
        return ToDto(x);
    }

    public async Task<TidpEntryDto> CreateAsync(
        Guid projectId, CreateTidpEntryRequest req,
        Guid actorId, string? ip, string? ua,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.TeamName))
            throw new ValidationException(new List<string> { "TeamName is required" });
        var midp = await db.MidpEntries
            .FirstOrDefaultAsync(m => m.Id == req.MidpEntryId && m.ProjectId == projectId && m.IsActive, ct)
            ?? throw new NotFoundException("MidpEntry");

        var entry = new TidpEntry
        {
            ProjectId = projectId,
            MidpEntryId = req.MidpEntryId,
            TeamName = req.TeamName.Trim(),
            DueDate = req.DueDate,
        };
        db.TidpEntries.Add(entry);
        await audit.WriteAsync(actorId, "tidp_entry.created",
            "TidpEntry", entry.Id.ToString(),
            projectId: projectId,
            detail: new { entry.TeamName, entry.DueDate, entry.MidpEntryId },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ToDto(entry);
    }

    public async Task<TidpEntryDto> UpdateAsync(
        Guid projectId, Guid id, UpdateTidpEntryRequest req,
        Guid actorId, string? ip, string? ua,
        CancellationToken ct = default)
    {
        var x = await db.TidpEntries
            .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId && e.IsActive, ct)
            ?? throw new NotFoundException("TidpEntry");
        if (x.IsSignedOff)
            throw new ConflictException("Cannot edit a signed-off TidpEntry.");
        var changed = new List<string>();
        if (req.TeamName is not null && req.TeamName != x.TeamName)
        {
            if (string.IsNullOrWhiteSpace(req.TeamName))
                throw new ValidationException(new List<string> { "TeamName cannot be empty" });
            x.TeamName = req.TeamName.Trim(); changed.Add(nameof(x.TeamName));
        }
        if (req.DueDate is { } d && d != x.DueDate)
        { x.DueDate = d; changed.Add(nameof(x.DueDate)); }
        if (changed.Count == 0) throw new ConflictException("No changes specified.");

        await audit.WriteAsync(actorId, "tidp_entry.updated",
            "TidpEntry", x.Id.ToString(),
            projectId: projectId, detail: new { changedFields = changed },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ToDto(x);
    }

    public async Task<TidpEntryDto> SignOffAsync(
        Guid projectId, Guid id, SignOffTidpEntryRequest req,
        Guid actorId, string? ip, string? ua,
        CancellationToken ct = default)
    {
        var x = await db.TidpEntries
            .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId && e.IsActive, ct)
            ?? throw new NotFoundException("TidpEntry");
        if (x.IsSignedOff)
            throw new ConflictException("TidpEntry is already signed off.");

        x.IsSignedOff   = true;
        x.SignedOffById = actorId;
        x.SignedOffAt   = DateTime.UtcNow;
        x.SignOffNote   = req.Note;
        await audit.WriteAsync(actorId, "tidp_entry.signed_off",
            "TidpEntry", x.Id.ToString(),
            projectId: projectId, detail: new { x.SignOffNote },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ToDto(x);
    }

    public async Task DeleteAsync(
        Guid projectId, Guid id,
        Guid actorId, string? ip, string? ua,
        CancellationToken ct = default)
    {
        var x = await db.TidpEntries
            .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId && e.IsActive, ct)
            ?? throw new NotFoundException("TidpEntry");
        x.IsActive = false;
        await audit.WriteAsync(actorId, "tidp_entry.deleted",
            "TidpEntry", x.Id.ToString(),
            projectId: projectId, detail: new { x.TeamName },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
    }

    private static TidpEntryDto ToDto(TidpEntry x) =>
        new(x.Id, x.ProjectId, x.MidpEntryId, x.TeamName, x.DueDate,
            x.IsSignedOff, x.SignedOffById, x.SignedOffAt, x.SignOffNote,
            x.CreatedAt, x.UpdatedAt);
}

// T-S7-05 Custom Report Definitions service. Per-project saved
// queries; pure-equality JSON filter against per-entity field
// allow-list (CustomReportRunner). Audit-twin per write path
// per the ADR-0014 pattern. Run path is read-only (no audit).
public class CustomReportDefinitionsService(CimsDbContext db, AuditService audit)
{
    public async Task<List<CustomReportDefinitionDto>> ListAsync(
        Guid projectId, CancellationToken ct = default)
    {
        var rows = await db.CustomReportDefinitions
            .Where(d => d.ProjectId == projectId && d.IsActive)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<CustomReportDefinitionDto> GetAsync(
        Guid projectId, Guid definitionId, CancellationToken ct = default)
    {
        var d = await db.CustomReportDefinitions
            .FirstOrDefaultAsync(x => x.Id == definitionId
                                   && x.ProjectId == projectId
                                   && x.IsActive, ct)
            ?? throw new NotFoundException("CustomReportDefinition");
        return ToDto(d);
    }

    public async Task<CustomReportDefinitionDto> CreateAsync(
        Guid projectId, CreateCustomReportDefinitionRequest req,
        Guid actorId, string? ip, string? ua,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ValidationException(new List<string> { "Name is required" });

        var filter  = req.FilterJson  ?? "{}";
        var columns = req.ColumnsJson ?? "[]";
        // Validate at write time so bad JSON / unknown fields can't
        // be persisted.
        CustomReportRunner.ValidateFilterJson(filter, req.EntityType);
        CustomReportRunner.ValidateColumnsJson(columns, req.EntityType);

        if (await db.CustomReportDefinitions.AnyAsync(
                x => x.ProjectId == projectId
                  && x.IsActive
                  && x.Name == req.Name, ct))
            throw new ConflictException(
                $"A custom report named '{req.Name}' already exists.");

        var def = new CustomReportDefinition
        {
            ProjectId   = projectId,
            Name        = req.Name.Trim(),
            EntityType  = req.EntityType,
            FilterJson  = filter,
            ColumnsJson = columns,
            CreatedById = actorId,
        };
        db.CustomReportDefinitions.Add(def);
        await audit.WriteAsync(actorId, "custom_report.created",
            "CustomReportDefinition", def.Id.ToString(),
            projectId: projectId,
            detail: new { def.Name, def.EntityType },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ToDto(def);
    }

    public async Task<CustomReportDefinitionDto> UpdateAsync(
        Guid projectId, Guid definitionId,
        UpdateCustomReportDefinitionRequest req,
        Guid actorId, string? ip, string? ua,
        CancellationToken ct = default)
    {
        var d = await db.CustomReportDefinitions
            .FirstOrDefaultAsync(x => x.Id == definitionId
                                   && x.ProjectId == projectId
                                   && x.IsActive, ct)
            ?? throw new NotFoundException("CustomReportDefinition");

        var changed = new List<string>();
        if (req.Name is not null && req.Name != d.Name)
        {
            var newName = req.Name.Trim();
            if (newName.Length == 0)
                throw new ValidationException(new List<string> { "Name cannot be empty" });
            if (await db.CustomReportDefinitions.AnyAsync(
                    x => x.ProjectId == projectId
                      && x.IsActive
                      && x.Id != definitionId
                      && x.Name == newName, ct))
                throw new ConflictException(
                    $"A custom report named '{newName}' already exists.");
            d.Name = newName;
            changed.Add(nameof(d.Name));
        }
        if (req.FilterJson is not null && req.FilterJson != d.FilterJson)
        {
            CustomReportRunner.ValidateFilterJson(req.FilterJson, d.EntityType);
            d.FilterJson = req.FilterJson;
            changed.Add(nameof(d.FilterJson));
        }
        if (req.ColumnsJson is not null && req.ColumnsJson != d.ColumnsJson)
        {
            CustomReportRunner.ValidateColumnsJson(req.ColumnsJson, d.EntityType);
            d.ColumnsJson = req.ColumnsJson;
            changed.Add(nameof(d.ColumnsJson));
        }
        if (changed.Count == 0)
            throw new ConflictException("No changes specified.");

        await audit.WriteAsync(actorId, "custom_report.updated",
            "CustomReportDefinition", d.Id.ToString(),
            projectId: projectId,
            detail: new { changedFields = changed },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
        return ToDto(d);
    }

    public async Task DeleteAsync(
        Guid projectId, Guid definitionId,
        Guid actorId, string? ip, string? ua,
        CancellationToken ct = default)
    {
        var d = await db.CustomReportDefinitions
            .FirstOrDefaultAsync(x => x.Id == definitionId
                                   && x.ProjectId == projectId
                                   && x.IsActive, ct)
            ?? throw new NotFoundException("CustomReportDefinition");
        d.IsActive = false;
        await audit.WriteAsync(actorId, "custom_report.deleted",
            "CustomReportDefinition", d.Id.ToString(),
            projectId: projectId,
            detail: new { d.Name },
            ip: ip, ua: ua);
        await db.SaveChangesAsync(ct);
    }

    public async Task<CustomReportRunResultDto> RunAsync(
        Guid projectId, Guid definitionId, CancellationToken ct = default)
    {
        var d = await db.CustomReportDefinitions
            .FirstOrDefaultAsync(x => x.Id == definitionId
                                   && x.ProjectId == projectId
                                   && x.IsActive, ct)
            ?? throw new NotFoundException("CustomReportDefinition");

        var filter  = CustomReportRunner.ValidateFilterJson(d.FilterJson, d.EntityType);
        var columns = CustomReportRunner.ValidateColumnsJson(d.ColumnsJson, d.EntityType);

        var rows = await LoadRowsAsync(projectId, d.EntityType, ct);
        var matched = rows.Where(r => CustomReportRunner.MatchesFilter(r, filter))
                          .Select(r => CustomReportRunner.ProjectColumns(r, columns))
                          .ToList();

        return new CustomReportRunResultDto(
            DefinitionId: d.Id,
            Name:         d.Name,
            EntityType:   d.EntityType,
            RowCount:     matched.Count,
            Columns:      columns,
            Rows:         matched);
    }

    private async Task<List<object>> LoadRowsAsync(
        Guid projectId, CustomReportEntityType type, CancellationToken ct)
    {
        // v1.0 in-memory filter pass: load the project's rows for
        // the chosen entity, filter / project in-memory. Single-
        // project queries are bounded by domain volume; pushdown to
        // SQL is a v1.1 perf concern if pilot data grows past the
        // simple-query envelope.
        return type switch
        {
            CustomReportEntityType.Risk =>
                (await db.Risks.Where(x => x.ProjectId == projectId).ToListAsync(ct))
                    .Cast<object>().ToList(),
            CustomReportEntityType.ActionItem =>
                (await db.ActionItems.Where(x => x.ProjectId == projectId).ToListAsync(ct))
                    .Cast<object>().ToList(),
            CustomReportEntityType.Rfi =>
                (await db.Rfis.Where(x => x.ProjectId == projectId).ToListAsync(ct))
                    .Cast<object>().ToList(),
            CustomReportEntityType.Variation =>
                (await db.Variations.Where(x => x.ProjectId == projectId).ToListAsync(ct))
                    .Cast<object>().ToList(),
            CustomReportEntityType.ChangeRequest =>
                (await db.ChangeRequests.Where(x => x.ProjectId == projectId).ToListAsync(ct))
                    .Cast<object>().ToList(),
            _ => throw new ValidationException(new List<string>
                 { $"EntityType {type} is not supported" }),
        };
    }

    private static CustomReportDefinitionDto ToDto(CustomReportDefinition d) =>
        new(d.Id, d.ProjectId, d.Name, d.EntityType,
            d.FilterJson, d.ColumnsJson,
            d.CreatedById, d.CreatedAt, d.UpdatedAt);
}

public class CdeService(CimsDbContext db, AuditService audit)
{
    public async Task<List<CdeContainer>> ListContainersAsync(Guid projectId) =>
        await db.CdeContainers.Where(c => c.ProjectId == projectId && c.IsActive).ToListAsync();

    public async Task<CdeContainer> CreateContainerAsync(Guid projectId, CreateContainerRequest req, Guid userId, string? ip, string? ua)
    {
        var c = new CdeContainer { ProjectId = projectId, Name = req.Name, Originator = req.Originator.ToUpperInvariant(), Volume = req.Volume?.ToUpperInvariant(), Level = req.Level?.ToUpperInvariant(), Type = req.Type.ToUpperInvariant(), Discipline = req.Discipline?.ToUpperInvariant(), Description = req.Description };
        db.CdeContainers.Add(c);
        await audit.WriteAsync(userId, "cde.container_created", "CdeContainer", c.Id.ToString(), projectId, ip: ip, ua: ua);
        await db.SaveChangesAsync();
        return c;
    }
}

// ── Documents ─────────────────────────────────────────────────────────────────
public class DocumentsService(
    CimsDbContext db,
    AuditService audit,
    CimsApp.Services.Iso19650.Iso19650FilenameValidator iso19650)
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

        // T-S9-03: ISO 19650 filename validation. Construct the 9-field
        // canonical name with S0 + P01 defaults for Suitability and
        // Revision (those become real values per-DocumentRevision; at
        // Document create time the document hasn't been issued so S0 /
        // P01 are the natural starting state). Run the validator and
        // fail-fast on checks 1-3 (Structure / FieldValidity /
        // Numbering). Checks 4 (Suitability) and 6 (Revision) are
        // skipped at create time because the placeholder defaults
        // would never reflect a real filename. Checks 7-10 (Uniclass
        // / IFC / cross-reference) are deferred to v1.1 / B-068.
        var canonical = DocumentNaming.Build(req.ProjectCode, req.Originator, req.Volume, req.Level, req.DocType, req.Role, req.Number)
                      + "-S0-P01";
        var validation = iso19650.Validate(canonical);
        var blocking = validation.Checks
            .Where(c => !c.Passed
                     && (c.Id == CimsApp.Services.Iso19650.Iso19650CheckId.Structure
                      || c.Id == CimsApp.Services.Iso19650.Iso19650CheckId.FieldValidity
                      || c.Id == CimsApp.Services.Iso19650.Iso19650CheckId.Numbering))
            .Select(c => $"ISO 19650 {c.Label}: {c.Message}")
            .ToList();
        if (blocking.Count > 0) throw new ValidationException(blocking);

        var docNum = DocumentNaming.Build(req.ProjectCode, req.Originator, req.Volume, req.Level, req.DocType, req.Role, req.Number);
        // DocumentNumber uniqueness is per-project (matches the
        // (ProjectId, DocumentNumber) unique index). Without the
        // explicit projectId filter here the tenant query filter
        // already narrows reads to the caller's tenant, but tying
        // the check to the same shape as the constraint keeps the
        // two layers in sync.
        if (await db.Documents.AnyAsync(d => d.ProjectId == projectId && d.DocumentNumber == docNum))
            throw new ConflictException($"Document {docNum} already exists");
        var doc = new Document { ProjectId = projectId, ContainerId = req.ContainerId, ProjectCode = req.ProjectCode.ToUpperInvariant(), Originator = req.Originator.ToUpperInvariant(), Volume = req.Volume?.ToUpperInvariant(), Level = req.Level?.ToUpperInvariant(), DocType = req.DocType.ToUpperInvariant(), Role = req.Role?.ToUpperInvariant(), Number = req.Number.ToString("D4"), DocumentNumber = docNum, Title = req.Title, Description = req.Description, Type = req.Type ?? DocumentType.Other, Tags = req.Tags ?? [], CreatorId = userId, CurrentState = CdeState.WorkInProgress };
        db.Documents.Add(doc);
        await audit.WriteAsync(userId, "document.created", "Document", doc.Id.ToString(), projectId, doc.Id, ip: ip, ua: ua);
        await db.SaveChangesAsync();
        return doc;
    }

    public async Task<Document> TransitionAsync(Guid docId, Guid projectId, CdeState toState, SuitabilityCode? suitability, Guid userId, UserRole userRole, string? ip, string? ua)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == docId && d.ProjectId == projectId && d.IsActive) ?? throw new NotFoundException("Document");
        if (!CdeStateMachine.IsValidTransition(doc.CurrentState, toState)) throw new CdeTransitionException(doc.CurrentState, toState);
        if (!CdeStateMachine.CanTransition(doc.CurrentState, toState, userRole)) throw new ForbiddenException($"Role {userRole} cannot perform this transition");
        // Wrap the revision-publish ExecuteUpdateAsync (separate SQL
        // UPDATE that bypasses the change tracker) and the doc state
        // change + audit (commits via SaveChangesAsync) in one
        // transaction. Without this wrap, a process crash between the
        // two writes when transitioning to Published would leave the
        // latest revisions marked Published (PublishedAt + ApprovedById
        // + Suitability set) but the Document.CurrentState still in
        // the previous state — revisions and document disagree until
        // the next transition retries. Same shape as the RegisterAsync
        // wrap (PR #29). EF in-memory provider treats this as a no-op.
        await using var tx = await db.Database.BeginTransactionAsync();
        if (toState == CdeState.Published)
            await db.DocumentRevisions.Where(r => r.DocumentId == docId && r.IsLatest)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.PublishedAt, DateTime.UtcNow).SetProperty(r => r.ApprovedById, userId).SetProperty(r => r.ApprovedAt, DateTime.UtcNow).SetProperty(r => r.Suitability, suitability ?? SuitabilityCode.S2));
        var from = doc.CurrentState;
        doc.CurrentState = toState;
        await audit.WriteAsync(userId, "document.state_transition", "Document", docId.ToString(), projectId, docId, new { from = from.ToString(), to = toState.ToString() }, ip, ua);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
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
        await audit.WriteAsync(userId, "rfi.created", "Rfi", rfi.Id.ToString(), projectId, ip: ip, ua: ua);
        await db.SaveChangesAsync();
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
        await audit.WriteAsync(userId, "rfi.responded", "Rfi", rfiId.ToString(), projectId, ip: ip, ua: ua);
        await db.SaveChangesAsync();
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
        await audit.WriteAsync(userId, "action.created", "ActionItem", a.Id.ToString(), projectId, ip: ip, ua: ua);
        await db.SaveChangesAsync();
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
        await audit.WriteAsync(userId, "action.updated", "ActionItem", actionId.ToString(), projectId, ip: ip, ua: ua);
        await db.SaveChangesAsync();
        return a;
    }
}
