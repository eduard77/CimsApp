using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;

namespace CimsApp.Services;

// ── Auth ──────────────────────────────────────────────────────────────────────
public class AuthService(CimsDbContext db, IConfiguration cfg)
{
    private string AccessSecret  => cfg["Jwt:AccessSecret"]!;
    private string RefreshSecret => cfg["Jwt:RefreshSecret"]!;
    private string Issuer        => cfg["Jwt:Issuer"]!;
    private string Audience      => cfg["Jwt:Audience"]!;
    private int    AccessMinutes => int.Parse(cfg["Jwt:AccessExpiresMinutes"] ?? "60");
    private int    RefreshDays   => int.Parse(cfg["Jwt:RefreshExpiresDays"]   ?? "7");

    public async Task<UserSummaryDto> RegisterAsync(RegisterRequest req)
    {
        if (await db.Users.AnyAsync(u => u.Email == req.Email))
            throw new ConflictException("Email already registered");
        var org = await db.Organisations.FindAsync(req.OrganisationId) ?? throw new NotFoundException("Organisation");
        var user = new User { Email = req.Email.ToLowerInvariant(), PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password), FirstName = req.FirstName, LastName = req.LastName, JobTitle = req.JobTitle, OrganisationId = req.OrganisationId };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return Map(user, org);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest req, string? ua, string? ip)
    {
        var user = await db.Users.Include(u => u.Organisation).FirstOrDefaultAsync(u => u.Email == req.Email.ToLowerInvariant() && u.IsActive)
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
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(r => r.Token == token);
        if (stored == null || !stored.IsActive) throw new AppException("Token revoked", 401, "TOKEN_REVOKED");
        stored.RevokedAt = DateTime.UtcNow;
        var user   = await db.Users.Include(u => u.Organisation).FirstAsync(u => u.Id == userId);
        var access = GenerateAccess(user);
        var newRef = await CreateRefreshAsync(userId, null, null);
        await db.SaveChangesAsync();
        return (access, newRef.Token);
    }

    public async Task LogoutAsync(string token)
    {
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(r => r.Token == token);
        if (stored != null) { stored.RevokedAt = DateTime.UtcNow; await db.SaveChangesAsync(); }
    }

    public async Task<User> GetUserAsync(Guid id) =>
        await db.Users.Include(u => u.Organisation).FirstOrDefaultAsync(u => u.Id == id && u.IsActive)
        ?? throw new NotFoundException("User");

    private string GenerateAccess(User user)
    {
        var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AccessSecret));
        var token = new JwtSecurityToken(Issuer, Audience,
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Email, user.Email)],
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

// ── Projects ──────────────────────────────────────────────────────────────────
public class ProjectsService(CimsDbContext db, AuditService audit)
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
        var p = new Project { Name = req.Name, Code = req.Code.ToUpperInvariant(), Description = req.Description, AppointingPartyId = req.AppointingPartyId, StartDate = req.StartDate, EndDate = req.EndDate, Location = req.Location, Country = req.Country, Currency = req.Currency ?? "GBP", BudgetValue = req.BudgetValue, Sector = req.Sector, Sponsor = req.Sponsor, EirRef = req.EirRef, Members = [new ProjectMember { UserId = userId, Role = UserRole.ProjectManager }] };
        db.Projects.Add(p);
        await db.SaveChangesAsync();
        await audit.WriteAsync(userId, "project.created", "Project", p.Id.ToString(), p.Id, ip: ip, ua: ua);
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
