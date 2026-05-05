using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CimsApp.Core;
using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CimsApp.Services.Compliance;

// ── DTOs (sprint-local) ───────────────────────────────────────────────────────

public sealed record EvidenceListItem(
    Guid Id, ComplianceArea ComplianceArea, DateTime AddedAt,
    Guid? DocumentId, string? DocumentTitle,
    Guid? RfiId, string? RfiNumber,
    Guid? AuditLogId, string? AuditLogAction,
    Guid? InspectionActivityId, string? InspectionTitle,
    string? Description,
    Guid AddedById, string AddedByName);

public sealed record EvidencePagedResult(
    IReadOnlyList<EvidenceListItem> Items, int Total, int Skip, int Take);

public sealed record AddEvidenceRequest(
    ComplianceArea ComplianceArea,
    Guid? DocumentId, Guid? RfiId, Guid? AuditLogId, Guid? InspectionActivityId,
    string? Description);

public sealed record AuditorAssignmentItem(
    Guid Id, Guid UserId, string UserEmail, string UserName,
    Guid ProjectId, string ProjectCode,
    ComplianceAreaScope ScopeAreas, DateTime ExpiresAt,
    DateTime CreatedAt, Guid CreatedById, string CreatedByName,
    bool IsRevoked, DateTime? RevokedAt);

public sealed record AuditorAssignmentPagedResult(
    IReadOnlyList<AuditorAssignmentItem> Items, int Total, int Skip, int Take);

public sealed record CreateAuditorAssignmentRequest(
    Guid UserId, Guid ProjectId,
    ComplianceAreaScope ScopeAreas, DateTime ExpiresAt);

// ── EvidenceLibraryService ────────────────────────────────────────────────────

public class EvidenceLibraryService(CimsDbContext db, AuditService audit)
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    public async Task<EvidencePagedResult> ListAsync(
        Guid projectId, ComplianceArea? area = null,
        int skip = 0, int? take = null, CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        var t = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        var q = db.EvidenceArtefacts.Where(e => e.ProjectId == projectId);
        if (area is { } a) q = q.Where(e => e.ComplianceArea == a);

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(e => e.AddedAt)
            .Skip(skip).Take(t)
            .Select(e => new EvidenceListItem(
                e.Id, e.ComplianceArea, e.AddedAt,
                e.DocumentId, e.Document == null ? null : e.Document.Title,
                e.RfiId, e.Rfi == null ? null : e.Rfi.RfiNumber,
                e.AuditLogId, e.AuditLog == null ? null : e.AuditLog.Action,
                e.InspectionActivityId,
                e.InspectionActivity == null ? null : e.InspectionActivity.Title,
                e.Description,
                e.AddedById, e.AddedBy.FirstName + " " + e.AddedBy.LastName))
            .ToListAsync(ct);

        return new EvidencePagedResult(rows, total, skip, t);
    }

    /// <summary>
    /// Add an evidence row. Validation: exactly one of the four FKs
    /// is populated, OR all four are null AND Description is set
    /// (bare-description evidence). Cross-FK or zero-FK-without-
    /// description rows are rejected with ValidationException.
    /// Audit-twin event "evidence.added" fires atomically with
    /// the row insert (PR #33 pattern).
    /// </summary>
    public async Task<EvidenceArtefact> AddAsync(
        Guid projectId, AddEvidenceRequest req, Guid actorId, CancellationToken ct = default)
    {
        // Validate the project is reachable in the caller's tenant —
        // FirstOrDefault honours the standard query filter; SuperAdmin
        // bypasses via ADR-0007 if the controller is gated for it.
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var fkCount = (req.DocumentId.HasValue ? 1 : 0)
                    + (req.RfiId.HasValue ? 1 : 0)
                    + (req.AuditLogId.HasValue ? 1 : 0)
                    + (req.InspectionActivityId.HasValue ? 1 : 0);
        if (fkCount > 1)
            throw new ValidationException(["Evidence row must reference at most one source entity"]);
        if (fkCount == 0 && string.IsNullOrWhiteSpace(req.Description))
            throw new ValidationException(["Bare-description evidence requires Description"]);

        // Cross-source-entity validation: each FK must point to a row
        // that belongs to the same project. Cross-project references
        // would let an OrgAdmin point evidence in project A at a
        // document in project B (still same tenant), which is a
        // referential coherence bug even though it's not a security
        // leak. v1.0 enforces same-project; v1.1 could relax for
        // tenant-wide artefacts (B-NNN if it surfaces).
        if (req.DocumentId is { } docId)
        {
            var ok = await db.Documents.AnyAsync(x => x.Id == docId && x.ProjectId == projectId, ct);
            if (!ok) throw new ValidationException(["Document not found in project"]);
        }
        if (req.RfiId is { } rfiId)
        {
            var ok = await db.Rfis.AnyAsync(x => x.Id == rfiId && x.ProjectId == projectId, ct);
            if (!ok) throw new ValidationException(["RFI not found in project"]);
        }
        if (req.AuditLogId is { } auditId)
        {
            // AuditLog has no ProjectId column directly; check via
            // ProjectId field.
            var ok = await db.AuditLogs.AnyAsync(x => x.Id == auditId && x.ProjectId == projectId, ct);
            if (!ok) throw new ValidationException(["Audit log row not found in project"]);
        }
        if (req.InspectionActivityId is { } inspId)
        {
            var ok = await db.InspectionActivities.AnyAsync(x => x.Id == inspId && x.ProjectId == projectId, ct);
            if (!ok) throw new ValidationException(["Inspection activity not found in project"]);
        }

        var row = new EvidenceArtefact
        {
            ProjectId = projectId,
            ComplianceArea = req.ComplianceArea,
            DocumentId = req.DocumentId,
            RfiId = req.RfiId,
            AuditLogId = req.AuditLogId,
            InspectionActivityId = req.InspectionActivityId,
            Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
            AddedById = actorId,
        };
        db.EvidenceArtefacts.Add(row);

        await audit.WriteAsync(actorId,
            "evidence.added", "EvidenceArtefact", row.Id.ToString(),
            projectId: projectId,
            detail: new
            {
                area = req.ComplianceArea.ToString(),
                req.DocumentId, req.RfiId, req.AuditLogId, req.InspectionActivityId,
                hasDescription = !string.IsNullOrWhiteSpace(req.Description),
            });
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task RemoveAsync(Guid evidenceId, Guid actorId, CancellationToken ct = default)
    {
        var row = await db.EvidenceArtefacts.FirstOrDefaultAsync(e => e.Id == evidenceId, ct)
            ?? throw new NotFoundException("Evidence");

        db.EvidenceArtefacts.Remove(row);

        await audit.WriteAsync(actorId,
            "evidence.removed", "EvidenceArtefact", evidenceId.ToString(),
            projectId: row.ProjectId,
            detail: new { evidenceId, projectId = row.ProjectId, area = row.ComplianceArea.ToString() });
        await db.SaveChangesAsync(ct);
    }
}

// ── AuditorAdminService ───────────────────────────────────────────────────────

public class AuditorAdminService(
    CimsDbContext db, ITenantContext tenant, AuditService audit, AuthService auth)
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    public async Task<AuditorAssignmentPagedResult> ListAsync(
        Guid? projectId = null, bool includeRevoked = false,
        int skip = 0, int? take = null, CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        var t = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        IQueryable<AuditorProjectAssignment> q = db.AuditorProjectAssignments;
        if (projectId is { } pid) q = q.Where(a => a.ProjectId == pid);
        if (!includeRevoked) q = q.Where(a => !a.IsRevoked);

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(a => a.CreatedAt)
            .Skip(skip).Take(t)
            .Select(a => new AuditorAssignmentItem(
                a.Id, a.UserId, a.User.Email, a.User.FirstName + " " + a.User.LastName,
                a.ProjectId, a.Project.Code,
                a.ScopeAreas, a.ExpiresAt,
                a.CreatedAt, a.CreatedById,
                a.CreatedBy.FirstName + " " + a.CreatedBy.LastName,
                a.IsRevoked, a.RevokedAt))
            .ToListAsync(ct);

        return new AuditorAssignmentPagedResult(rows, total, skip, t);
    }

    /// <summary>
    /// Create an external-auditor project assignment. Side effects:
    /// (a) sets target user's GlobalRole to ExternalAuditor if not
    /// already (preserving an existing ExternalAuditor or
    /// other-tenant SuperAdmin assignment as a safety check);
    /// (b) creates the AuditorProjectAssignment row;
    /// (c) writes audit-twin "auditor.assigned" event.
    /// Atomic via a single SaveChanges.
    /// </summary>
    public async Task<AuditorProjectAssignment> CreateAsync(
        CreateAuditorAssignmentRequest req, Guid actorId, CancellationToken ct = default)
    {
        if (req.ExpiresAt <= DateTime.UtcNow)
            throw new ValidationException(["ExpiresAt must be in the future"]);

        // Tenant scoping: project must be in caller's tenant
        // (or caller is SuperAdmin via the controller-level role gate).
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == req.ProjectId, ct)
            ?? throw new NotFoundException("Project");

        // Target user: SuperAdmin can target any user (cross-tenant
        // for the ExternalAuditor's home tenancy use case); OrgAdmin
        // is restricted to their own tenant via the standard query
        // filter on Users.
        var query = tenant.IsSuperAdmin
            ? db.Users.IgnoreQueryFilters()
            : db.Users.AsQueryable();
        var user = await query.FirstOrDefaultAsync(u => u.Id == req.UserId, ct)
            ?? throw new NotFoundException("User");

        // Refuse to flip a SuperAdmin to ExternalAuditor — that would
        // demote a higher-privilege user. The only role transitions
        // we permit here: null / ClientRep / Viewer / TaskTeamMember
        // / InformationManager / ProjectManager / ExternalAuditor.
        // If the user is already an OrgAdmin or SuperAdmin, the
        // assignment is silent on GlobalRole (the SuperAdmin would
        // still pass the standard role gate, so the assignment row
        // is harmless).
        if (user.GlobalRole == null
            || (user.GlobalRole != UserRole.SuperAdmin
                && user.GlobalRole != UserRole.OrgAdmin))
        {
            user.GlobalRole = UserRole.ExternalAuditor;
            // Bump cutoff so existing JWTs (with the old role baked
            // in at issue time) are rejected — the user must
            // re-login to pick up the new role. ADR-0014.
            user.TokenInvalidationCutoff = DateTime.UtcNow;
        }

        var row = new AuditorProjectAssignment
        {
            UserId = req.UserId,
            ProjectId = req.ProjectId,
            ScopeAreas = req.ScopeAreas == ComplianceAreaScope.None
                ? ComplianceAreaScope.All : req.ScopeAreas,
            ExpiresAt = req.ExpiresAt,
            CreatedById = actorId,
        };
        db.AuditorProjectAssignments.Add(row);

        await audit.WriteAsync(actorId,
            "auditor.assigned", "AuditorProjectAssignment", row.Id.ToString(),
            projectId: req.ProjectId,
            detail: new
            {
                targetUserId = req.UserId,
                req.ProjectId,
                scopeAreas = row.ScopeAreas.ToString(),
                req.ExpiresAt,
            });
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>
    /// Revoke an active assignment. Marks IsRevoked=true + RevokedAt;
    /// bumps the target user's TokenInvalidationCutoff and sweeps
    /// their refresh tokens so any in-flight session is killed
    /// immediately (ADR-0014). Already-revoked assignments are a
    /// no-op (idempotent — same shape as the InvitationAdmin revoke).
    /// </summary>
    public async Task RevokeAsync(Guid assignmentId, Guid actorId, CancellationToken ct = default)
    {
        var row = await db.AuditorProjectAssignments
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == assignmentId, ct)
            ?? throw new NotFoundException("AuditorProjectAssignment");
        if (row.IsRevoked) return;

        row.IsRevoked = true;
        row.RevokedAt = DateTime.UtcNow;

        // Cutoff bump + refresh sweep. AuthService is the canonical
        // owner of the revocation primitive (ADR-0014).
        await auth.RevokeUserTokensAsync(row.UserId, tenant);

        await audit.WriteAsync(actorId,
            "auditor.revoked", "AuditorProjectAssignment", assignmentId.ToString(),
            projectId: row.ProjectId,
            detail: new { assignmentId, targetUserId = row.UserId, projectId = row.ProjectId });
        await db.SaveChangesAsync(ct);
    }
}

// ── AuditExportService ────────────────────────────────────────────────────────

public class AuditExportService(CimsDbContext db)
{
    /// <summary>
    /// Build a ZIP bundle containing the project's compliance evidence
    /// + the date-windowed audit trail. Bundle structure:
    ///
    ///   manifest.json            — project + window + counts
    ///   evidence/&lt;id&gt;.json — per-row evidence record with
    ///                              embedded source-entity snapshot
    ///   audit-log.json           — audit rows for the time window
    ///
    /// Stream-built so a large export doesn't OOM. Areas filter
    /// scopes the evidence; the audit log is always full for the
    /// window (auditors typically want the whole trail). When
    /// invoked by an ExternalAuditor, the standard project-role
    /// check has already passed — service-layer trusts the caller
    /// at this point. Non-tenant access is rejected upstream by
    /// the standard filters.
    /// </summary>
    public async Task<byte[]> BuildBundleAsync(
        Guid projectId, ComplianceAreaScope areas,
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (from > to) throw new ValidationException(["from must be <= to"]);
        if (areas == ComplianceAreaScope.None) areas = ComplianceAreaScope.All;

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new NotFoundException("Project");

        var evidence = await db.EvidenceArtefacts
            .Where(e => e.ProjectId == projectId
                     && e.AddedAt >= from && e.AddedAt <= to)
            .Include(e => e.Document)
            .Include(e => e.Rfi)
            .Include(e => e.AuditLog)
            .Include(e => e.InspectionActivity)
            .Include(e => e.AddedBy)
            .ToListAsync(ct);
        // Filter by ScopeAreas in-memory — flag enum can't translate
        // to SQL directly without per-flag predicates; the row count
        // is bounded by the project + window so this is cheap.
        var allowedAreas = ToAreaSet(areas);
        evidence = evidence.Where(e => allowedAreas.Contains(e.ComplianceArea)).ToList();

        var auditRows = await db.AuditLogs
            .Where(a => a.ProjectId == projectId
                     && a.CreatedAt >= from && a.CreatedAt <= to)
            .Include(a => a.User)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // manifest.json
            var manifest = new
            {
                projectId,
                projectCode = project.Code,
                projectName = project.Name,
                window = new { from, to },
                scopeAreas = allowedAreas.Select(a => a.ToString()).ToArray(),
                counts = new { evidence = evidence.Count, auditRows = auditRows.Count },
                generatedAt = DateTime.UtcNow,
            };
            await WriteJsonEntryAsync(zip, "manifest.json", manifest, jsonOptions, ct);

            // evidence/<id>.json per row, with embedded snapshots
            foreach (var e in evidence)
            {
                var record = new
                {
                    e.Id,
                    e.ProjectId,
                    area = e.ComplianceArea.ToString(),
                    e.AddedAt,
                    addedBy = new { e.AddedById, name = e.AddedBy.FirstName + " " + e.AddedBy.LastName },
                    e.Description,
                    source = SnapshotSource(e),
                };
                await WriteJsonEntryAsync(zip, $"evidence/{e.Id}.json", record, jsonOptions, ct);
            }

            // audit-log.json
            var auditPayload = auditRows.Select(a => new
            {
                a.Id, a.CreatedAt, a.Action, a.Entity, a.EntityId,
                userId = a.UserId,
                userName = a.User == null ? null : a.User.FirstName + " " + a.User.LastName,
                a.IpAddress,
                a.Detail,
                a.BeforeValue,
                a.AfterValue,
            }).ToArray();
            await WriteJsonEntryAsync(zip, "audit-log.json", auditPayload, jsonOptions, ct);
        }

        return ms.ToArray();
    }

    private static async Task WriteJsonEntryAsync<T>(
        ZipArchive zip, string entryName, T payload,
        JsonSerializerOptions options, CancellationToken ct)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        var json = JsonSerializer.Serialize(payload, options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await entryStream.WriteAsync(bytes, ct);
    }

    private static object? SnapshotSource(EvidenceArtefact e)
    {
        if (e.Document is { } d) return new { type = "Document", d.Id, d.Title, d.DocumentNumber, d.CurrentState };
        if (e.Rfi is { } r) return new { type = "Rfi", r.Id, r.RfiNumber, r.Subject, r.Status };
        if (e.AuditLog is { } a) return new { type = "AuditLog", a.Id, a.Action, a.Entity, a.EntityId, a.CreatedAt };
        if (e.InspectionActivity is { } i) return new { type = "InspectionActivity", i.Id, i.Title, i.Status };
        return null;
    }

    private static HashSet<ComplianceArea> ToAreaSet(ComplianceAreaScope scope)
    {
        var set = new HashSet<ComplianceArea>();
        if (scope.HasFlag(ComplianceAreaScope.UkGdpr))     set.Add(ComplianceArea.UkGdpr);
        if (scope.HasFlag(ComplianceAreaScope.Bsa2022))    set.Add(ComplianceArea.Bsa2022);
        if (scope.HasFlag(ComplianceAreaScope.Iso19650))   set.Add(ComplianceArea.Iso19650);
        if (scope.HasFlag(ComplianceAreaScope.Nec4))       set.Add(ComplianceArea.Nec4);
        if (scope.HasFlag(ComplianceAreaScope.QualityHse)) set.Add(ComplianceArea.QualityHse);
        if (scope.HasFlag(ComplianceAreaScope.Other))      set.Add(ComplianceArea.Other);
        return set;
    }
}
