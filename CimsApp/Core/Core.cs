using System.Text.Json;
using CimsApp.Data;
using CimsApp.Models;

namespace CimsApp.Core;

// ── Exceptions ────────────────────────────────────────────────────────────────
public class AppException(string message, int statusCode = 500, string code = "INTERNAL_ERROR") : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code    { get; } = code;
}
public class NotFoundException(string resource = "Resource")   : AppException($"{resource} not found", 404, "NOT_FOUND");
public class ConflictException(string message)                 : AppException(message, 409, "CONFLICT");
public class ForbiddenException(string message = "Insufficient permissions") : AppException(message, 403, "FORBIDDEN");
public class ValidationException(List<string> errors)         : AppException("Validation failed", 400, "VALIDATION_ERROR")
{ public List<string> Errors { get; } = errors; }
public class CdeTransitionException(CdeState from, CdeState to)
    : AppException($"Invalid CDE transition: {from} → {to}", 422, "INVALID_CDE_TRANSITION");

// ── CDE State Machine ─────────────────────────────────────────────────────────
public static class CdeStateMachine
{
    private static readonly Dictionary<CdeState, CdeState[]> Transitions = new()
    {
        [CdeState.WorkInProgress] = [CdeState.Shared,           CdeState.Voided],
        [CdeState.Shared]         = [CdeState.WorkInProgress,   CdeState.Published, CdeState.Voided],
        [CdeState.Published]      = [CdeState.Archived,         CdeState.Voided],
        [CdeState.Archived]       = [],
        [CdeState.Voided]         = [],
    };

    private static readonly Dictionary<(CdeState, CdeState), UserRole[]> TransitionRoles = new()
    {
        [(CdeState.WorkInProgress, CdeState.Shared)]       = [UserRole.TaskTeamMember, UserRole.InformationManager, UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(CdeState.Shared, CdeState.WorkInProgress)]       = [UserRole.TaskTeamMember, UserRole.InformationManager, UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(CdeState.Shared, CdeState.Published)]            = [UserRole.InformationManager, UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(CdeState.Published, CdeState.Archived)]          = [UserRole.InformationManager, UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(CdeState.WorkInProgress, CdeState.Voided)]       = [UserRole.ProjectManager, UserRole.InformationManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(CdeState.Shared, CdeState.Voided)]               = [UserRole.ProjectManager, UserRole.InformationManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(CdeState.Published, CdeState.Voided)]            = [UserRole.OrgAdmin, UserRole.SuperAdmin],
    };

    private static readonly UserRole[] RoleHierarchy =
    [UserRole.Viewer, UserRole.ClientRep, UserRole.TaskTeamMember, UserRole.InformationManager, UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin];

    public static bool IsValidTransition(CdeState from, CdeState to)
        => Transitions.TryGetValue(from, out var a) && a.Contains(to);

    public static bool CanTransition(CdeState from, CdeState to, UserRole role)
        => TransitionRoles.TryGetValue((from, to), out var p) && p.Contains(role);

    public static bool HasMinimumRole(UserRole role, UserRole minimum)
        => Array.IndexOf(RoleHierarchy, role) >= Array.IndexOf(RoleHierarchy, minimum);

    public static CdeState[] GetValidTransitions(CdeState from)
        => Transitions.TryGetValue(from, out var a) ? a : [];
}

// ── Document Naming ───────────────────────────────────────────────────────────
public static class DocumentNaming
{
    public static string Build(string projectCode, string originator, string? volume, string? level, string docType, string? role, int number)
    {
        var parts = new[]
        {
            Clean(projectCode), Clean(originator),
            string.IsNullOrWhiteSpace(volume) ? "ZZ" : Clean(volume),
            string.IsNullOrWhiteSpace(level)  ? "ZZ" : Clean(level),
            Clean(docType),
            string.IsNullOrWhiteSpace(role)   ? "XX" : Clean(role),
            number.ToString("D4")
        };
        return string.Join("-", parts).ToUpperInvariant();
    }

    public static List<string> Validate(string? projectCode, string? originator, string? docType, int? number)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(projectCode)) errors.Add("ProjectCode is required");
        if (string.IsNullOrWhiteSpace(originator))  errors.Add("Originator is required");
        if (string.IsNullOrWhiteSpace(docType))      errors.Add("DocType is required");
        if (number == null || number < 1)            errors.Add("Number must be a positive integer");
        return errors;
    }

    private static string Clean(string s) => new(s.Trim().Where(char.IsLetterOrDigit).ToArray());
}

// ── Audit ─────────────────────────────────────────────────────────────────────
public class AuditService(CimsDbContext db)
{
    /// <summary>
    /// Add a structured audit-twin event to the change tracker. The
    /// caller is responsible for committing via SaveChangesAsync — the
    /// audit row lands in the same transaction as the business
    /// mutation it describes, so a SaveChanges failure rolls back BOTH
    /// the entity write and the structured audit event (or commits
    /// both atomically). This was a separate SaveChanges call until
    /// 2026-04-29 (PR refactor/audit-twin-atomicity); the audit-twin
    /// contract requires both halves to succeed-or-fail together.
    /// </summary>
    public Task WriteAsync(Guid userId, string action, string entity, string entityId,
        Guid? projectId = null, Guid? documentId = null, object? detail = null,
        string? ip = null, string? ua = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = userId, ProjectId = projectId, DocumentId = documentId,
            Action = action, Entity = entity, EntityId = entityId,
            Detail = detail != null ? JsonSerializer.Serialize(detail) : null,
            IpAddress = ip, UserAgent = ua,
        });
        return Task.CompletedTask;
    }
}
