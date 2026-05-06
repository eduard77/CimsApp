using CimsApp.Core;
using CimsApp.Data;
using CimsApp.Models;
using Microsoft.EntityFrameworkCore;

namespace CimsApp.Services.SiteUserWorkflows;

// ── DTOs (sprint-local) ───────────────────────────────────────────────────────

public sealed record DailyDiaryItem(
    Guid Id, Guid ProjectId, DateOnly DiaryDate,
    Guid AuthorId, string AuthorName,
    string? Weather, string? Manpower, string? Plant,
    string? Deliveries, string? Incidents, string? ProgressNotes,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record DailyDiaryPagedResult(
    IReadOnlyList<DailyDiaryItem> Items, int Total, int Skip, int Take);

public sealed record CreateDailyDiaryRequest(
    DateOnly DiaryDate,
    string? Weather, string? Manpower, string? Plant,
    string? Deliveries, string? Incidents, string? ProgressNotes);

public sealed record UpdateDailyDiaryRequest(
    string? Weather, string? Manpower, string? Plant,
    string? Deliveries, string? Incidents, string? ProgressNotes);

public sealed record NcrItem(
    Guid Id, Guid ProjectId, string Number, string Title,
    NcrSeverity Severity, NcrState Status,
    string? Location,
    Guid RaisedById, string RaisedByName,
    Guid? AssignedToId, string? AssignedToName,
    DateTime CreatedAt, DateTime? AssignedAt,
    DateTime? ResolvedAt, DateTime? ClosedAt);

public sealed record NcrDetail(
    Guid Id, Guid ProjectId, string Number, string Title,
    string Description, NcrSeverity Severity, NcrState Status,
    string? Location, string? PhotoStorageKey,
    string? ResolutionNotes,
    Guid RaisedById, string RaisedByName,
    Guid? AssignedToId, string? AssignedToName,
    DateTime CreatedAt, DateTime? AssignedAt,
    DateTime? ResolvedAt, DateTime? ClosedAt);

public sealed record NcrPagedResult(
    IReadOnlyList<NcrItem> Items, int Total, int Skip, int Take);

public sealed record CreateNcrRequest(
    string Title, string Description,
    NcrSeverity Severity, string? Location,
    string? PhotoStorageKey);

public sealed record AssignNcrRequest(Guid AssignedToId);

public sealed record TransitionNcrRequest(NcrState ToState, string? ResolutionNotes);

// ── DailyDiaryService ─────────────────────────────────────────────────────────

public class DailyDiaryService(CimsDbContext db, AuditService audit)
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    public async Task<DailyDiaryPagedResult> ListAsync(
        Guid projectId, DateOnly? from = null, DateOnly? to = null,
        int skip = 0, int? take = null, CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        var t = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        var q = db.DailyDiaryEntries.Where(e => e.ProjectId == projectId);
        if (from is { } f) q = q.Where(e => e.DiaryDate >= f);
        if (to   is { } u) q = q.Where(e => e.DiaryDate <= u);

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(e => e.DiaryDate).ThenByDescending(e => e.CreatedAt)
            .Skip(skip).Take(t)
            .Select(e => new DailyDiaryItem(
                e.Id, e.ProjectId, e.DiaryDate,
                e.AuthorId, e.Author.FirstName + " " + e.Author.LastName,
                e.Weather, e.Manpower, e.Plant,
                e.Deliveries, e.Incidents, e.ProgressNotes,
                e.CreatedAt, e.UpdatedAt))
            .ToListAsync(ct);

        return new DailyDiaryPagedResult(rows, total, skip, t);
    }

    public async Task<DailyDiaryEntry> CreateAsync(
        Guid projectId, CreateDailyDiaryRequest req, Guid actorId,
        CancellationToken ct = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        // (ProjectId, DiaryDate, AuthorId) uniqueness — service-layer
        // check before relying on the unique index for a clear error.
        var dup = await db.DailyDiaryEntries.AnyAsync(e =>
            e.ProjectId == projectId && e.DiaryDate == req.DiaryDate
            && e.AuthorId == actorId, ct);
        if (dup)
            throw new ConflictException("A diary entry already exists for this user on this date");

        var row = new DailyDiaryEntry
        {
            ProjectId = projectId,
            DiaryDate = req.DiaryDate,
            AuthorId = actorId,
            Weather = Trim(req.Weather),
            Manpower = Trim(req.Manpower),
            Plant = Trim(req.Plant),
            Deliveries = Trim(req.Deliveries),
            Incidents = Trim(req.Incidents),
            ProgressNotes = Trim(req.ProgressNotes),
        };
        db.DailyDiaryEntries.Add(row);

        await audit.WriteAsync(actorId,
            "dailydiary.created", "DailyDiaryEntry", row.Id.ToString(),
            projectId: projectId,
            detail: new { date = req.DiaryDate.ToString("yyyy-MM-dd") });
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<DailyDiaryEntry> UpdateAsync(
        Guid entryId, UpdateDailyDiaryRequest req, Guid actorId,
        CancellationToken ct = default)
    {
        var row = await db.DailyDiaryEntries.FirstOrDefaultAsync(e => e.Id == entryId, ct)
            ?? throw new NotFoundException("DailyDiaryEntry");

        // Author-or-PM+ enforcement is at the controller via
        // GetProjectRoleAsync; service trusts the caller here.
        row.Weather = Trim(req.Weather);
        row.Manpower = Trim(req.Manpower);
        row.Plant = Trim(req.Plant);
        row.Deliveries = Trim(req.Deliveries);
        row.Incidents = Trim(req.Incidents);
        row.ProgressNotes = Trim(req.ProgressNotes);

        await audit.WriteAsync(actorId,
            "dailydiary.updated", "DailyDiaryEntry", row.Id.ToString(),
            projectId: row.ProjectId,
            detail: new { entryId, originalAuthorId = row.AuthorId });
        await db.SaveChangesAsync(ct);
        return row;
    }

    private static string? Trim(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

// ── NcrService ────────────────────────────────────────────────────────────────

public class NcrService(CimsDbContext db, AuditService audit)
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    public async Task<NcrPagedResult> ListAsync(
        Guid projectId, NcrState? status = null,
        int skip = 0, int? take = null, CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        var t = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        var q = db.Ncrs.Where(n => n.ProjectId == projectId);
        if (status is { } s) q = q.Where(n => n.Status == s);

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(n => n.CreatedAt)
            .Skip(skip).Take(t)
            .Select(n => new NcrItem(
                n.Id, n.ProjectId, n.Number, n.Title,
                n.Severity, n.Status, n.Location,
                n.RaisedById, n.RaisedBy.FirstName + " " + n.RaisedBy.LastName,
                n.AssignedToId,
                n.AssignedTo == null ? null
                    : n.AssignedTo.FirstName + " " + n.AssignedTo.LastName,
                n.CreatedAt, n.AssignedAt, n.ResolvedAt, n.ClosedAt))
            .ToListAsync(ct);

        return new NcrPagedResult(rows, total, skip, t);
    }

    public async Task<NcrDetail> GetAsync(Guid ncrId, CancellationToken ct = default)
    {
        var n = await db.Ncrs
            .Include(x => x.RaisedBy)
            .Include(x => x.AssignedTo)
            .FirstOrDefaultAsync(x => x.Id == ncrId, ct)
            ?? throw new NotFoundException("Ncr");
        return new NcrDetail(
            n.Id, n.ProjectId, n.Number, n.Title, n.Description,
            n.Severity, n.Status, n.Location, n.PhotoStorageKey,
            n.ResolutionNotes,
            n.RaisedById, n.RaisedBy.FirstName + " " + n.RaisedBy.LastName,
            n.AssignedToId,
            n.AssignedTo == null ? null
                : n.AssignedTo.FirstName + " " + n.AssignedTo.LastName,
            n.CreatedAt, n.AssignedAt, n.ResolvedAt, n.ClosedAt);
    }

    public async Task<Ncr> CreateAsync(
        Guid projectId, CreateNcrRequest req, Guid actorId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Description))
            throw new ValidationException(["Title and Description are required"]);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        // Per-project sequential number — same shape as RFI / change
        // request / inspection / etc. Compute from the highest existing
        // number; race-condition is tolerable at v1.0 pilot scale (a
        // collision would fail the unique index and surface as 409;
        // serialised numbering → v1.1 if needed).
        var lastNumber = await db.Ncrs
            .Where(n => n.ProjectId == projectId)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => n.Number)
            .FirstOrDefaultAsync(ct);
        var next = NextNcrNumber(lastNumber);

        var row = new Ncr
        {
            ProjectId = projectId,
            Number = next,
            Title = req.Title.Trim(),
            Description = req.Description.Trim(),
            Severity = req.Severity,
            Location = string.IsNullOrWhiteSpace(req.Location) ? null : req.Location.Trim(),
            PhotoStorageKey = string.IsNullOrWhiteSpace(req.PhotoStorageKey) ? null : req.PhotoStorageKey.Trim(),
            Status = NcrState.Raised,
            RaisedById = actorId,
        };
        db.Ncrs.Add(row);

        await audit.WriteAsync(actorId,
            "ncr.created", "Ncr", row.Id.ToString(),
            projectId: projectId,
            detail: new { number = next, severity = req.Severity.ToString() });
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<Ncr> AssignAsync(
        Guid ncrId, AssignNcrRequest req, UserRole callerRole, Guid actorId,
        CancellationToken ct = default)
    {
        var n = await db.Ncrs.FirstOrDefaultAsync(x => x.Id == ncrId, ct)
            ?? throw new NotFoundException("Ncr");
        if (n.Status != NcrState.Raised)
            throw new ValidationException(["NCR must be in Raised state to assign"]);
        if (!NcrWorkflow.CanTransition(NcrState.Raised, NcrState.Assigned, callerRole))
            throw new ForbiddenException("Insufficient role to assign NCR");

        // Target assignee must be a member of the project.
        var memberOk = await db.ProjectMembers.AnyAsync(m =>
            m.ProjectId == n.ProjectId && m.UserId == req.AssignedToId && m.IsActive, ct);
        if (!memberOk)
            throw new ValidationException(["Assignee is not an active member of this project"]);

        n.AssignedToId = req.AssignedToId;
        n.AssignedAt = DateTime.UtcNow;
        n.Status = NcrState.Assigned;

        await audit.WriteAsync(actorId,
            "ncr.assigned", "Ncr", n.Id.ToString(),
            projectId: n.ProjectId,
            detail: new { ncrNumber = n.Number, assignedToId = req.AssignedToId });
        await db.SaveChangesAsync(ct);
        return n;
    }

    public async Task<Ncr> TransitionAsync(
        Guid ncrId, TransitionNcrRequest req, UserRole callerRole, Guid actorId,
        CancellationToken ct = default)
    {
        var n = await db.Ncrs.FirstOrDefaultAsync(x => x.Id == ncrId, ct)
            ?? throw new NotFoundException("Ncr");
        if (!NcrWorkflow.IsValidTransition(n.Status, req.ToState))
            throw new ValidationException([$"Invalid transition: {n.Status} → {req.ToState}"]);
        if (!NcrWorkflow.CanTransition(n.Status, req.ToState, callerRole))
            throw new ForbiddenException("Insufficient role for this transition");

        var from = n.Status;
        n.Status = req.ToState;

        if (req.ToState == NcrState.Resolved)
        {
            n.ResolvedAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(req.ResolutionNotes))
                n.ResolutionNotes = req.ResolutionNotes.Trim();
        }
        if (req.ToState == NcrState.Closed) n.ClosedAt = DateTime.UtcNow;

        await audit.WriteAsync(actorId,
            "ncr.transitioned", "Ncr", n.Id.ToString(),
            projectId: n.ProjectId,
            detail: new { ncrNumber = n.Number, from = from.ToString(), to = req.ToState.ToString() });
        await db.SaveChangesAsync(ct);
        return n;
    }

    private static string NextNcrNumber(string? lastNumber)
    {
        if (string.IsNullOrWhiteSpace(lastNumber)) return "NCR-0001";
        var parts = lastNumber.Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var n)) return "NCR-0001";
        return $"NCR-{(n + 1):D4}";
    }
}
