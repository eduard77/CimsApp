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
public class AuthService(CimsDbContext db, IConfiguration cfg, InvitationService invitations)
{
    private string AccessSecret  => cfg["Jwt:AccessSecret"]!;
    private string RefreshSecret => cfg["Jwt:RefreshSecret"]!;
    private string Issuer        => cfg["Jwt:Issuer"]!;
    private string Audience      => cfg["Jwt:Audience"]!;
    private int    AccessMinutes => int.Parse(cfg["Jwt:AccessExpiresMinutes"] ?? "60");
    private int    RefreshDays   => int.Parse(cfg["Jwt:RefreshExpiresDays"]   ?? "7");

    public async Task<UserSummaryDto> RegisterAsync(RegisterRequest req)
    {
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
        return new AuthResponse(access, refresh.Token, Map(user, user.Organisation));
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

    public async Task<Rfi> RespondAsync(Guid rfiId, Guid projectId, RespondRfiRequest req, Guid userId, string? ip, string? ua)
    {
        var rfi = await db.Rfis.FirstOrDefaultAsync(r => r.Id == rfiId && r.ProjectId == projectId) ?? throw new NotFoundException("RFI");
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

    public async Task<ActionItem> UpdateAsync(Guid actionId, Guid projectId, UpdateActionRequest req, Guid userId, string? ip, string? ua)
    {
        var a = await db.ActionItems.FirstOrDefaultAsync(x => x.Id == actionId && x.ProjectId == projectId) ?? throw new NotFoundException("Action item");
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
