using System.Text.Json;
using CimsApp.Models;
using CimsApp.Services.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CimsApp.Services.Audit;

/// <summary>
/// Captures Insert / Update / Delete on tenant-scoped entities as
/// AuditLog rows within the same SaveChanges transaction. Implements
/// PAFM F.1 "audit trail captures actor, action, entity, before/after"
/// per the discipline manual's prescribed pattern (Appendix D.1: audit
/// via EF SaveChanges interceptor).
/// </summary>
public sealed class AuditInterceptor(
    ITenantContext tenant,
    IHttpContextAccessor? httpAccessor = null) : SaveChangesInterceptor
{
    // Never audit: AuditLog itself (recursion), RefreshToken (auth-layer
    // noise), Notification (UI-layer noise).
    internal static readonly HashSet<Type> SkippedEntityTypes =
    [
        typeof(AuditLog),
        typeof(RefreshToken),
        typeof(Notification),
    ];

    // Field names that must NEVER appear in the BeforeValue /
    // AfterValue JSON, regardless of which entity is being audited.
    // Defense-in-depth: even though `PasswordHash` is a bcrypt'd
    // value (not plaintext) and `TokenHash` is a SHA-256 of an
    // already-shown-once invitation token (not recoverable to the
    // plaintext), the audit log is a wider blast radius than the
    // User / Invitation tables and these values should not leak
    // outward via audit-log exports or read-only audit dashboards.
    internal static readonly HashSet<string> SkippedFieldNames =
        new(StringComparer.Ordinal)
        {
            nameof(User.PasswordHash),
            nameof(Invitation.TokenHash),
        };

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        CaptureAuditLogs(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CaptureAuditLogs(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void CaptureAuditLogs(DbContext? context)
    {
        if (context is null) return;
        var userId = tenant.UserId;
        // System / anonymous contexts (registration, seed, migrations)
        // are not audited — there is no actor to record.
        if (userId is null || userId == Guid.Empty) return;

        var (ip, ua) = ExtractRequestMeta();

        var entries = context.ChangeTracker.Entries()
            .Where(e => ShouldAudit(e))
            .ToList();

        foreach (var entry in entries)
        {
            var log = BuildAuditLog(entry, userId.Value, ip, ua);
            if (log is not null) context.Set<AuditLog>().Add(log);
        }
    }

    internal static bool ShouldAudit(EntityEntry entry)
    {
        if (SkippedEntityTypes.Contains(entry.Entity.GetType())) return false;
        return entry.State is EntityState.Added
                           or EntityState.Modified
                           or EntityState.Deleted;
    }

    internal static string? ActionFor(EntityState state) => state switch
    {
        EntityState.Added    => "Insert",
        EntityState.Modified => "Update",
        EntityState.Deleted  => "Delete",
        _ => null,
    };

    internal static AuditLog? BuildAuditLog(
        EntityEntry entry, Guid userId, string? ip, string? ua)
    {
        var action = ActionFor(entry.State);
        if (action is null) return null;

        var primaryKey = entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey());
        var entityId = primaryKey?.CurrentValue?.ToString() ?? "";

        var before = entry.State == EntityState.Added ? null : SerialiseState(entry, current: false);
        var after  = entry.State == EntityState.Deleted ? null : SerialiseState(entry, current: true);
        var (projectId, documentId) = ExtractRelatedIds(entry);

        return new AuditLog
        {
            UserId = userId,
            ProjectId = projectId,
            DocumentId = documentId,
            Action = action,
            Entity = entry.Entity.GetType().Name,
            EntityId = entityId,
            BeforeValue = before,
            AfterValue = after,
            IpAddress = ip,
            UserAgent = ua,
        };
    }

    private static string SerialiseState(EntityEntry entry, bool current)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var p in entry.Properties)
        {
            if (p.Metadata.IsShadowProperty()) continue;
            if (SkippedFieldNames.Contains(p.Metadata.Name)) continue;
            dict[p.Metadata.Name] = current ? p.CurrentValue : p.OriginalValue;
        }
        return JsonSerializer.Serialize(dict);
    }

    internal static (Guid? projectId, Guid? documentId) ExtractRelatedIds(EntityEntry entry)
    {
        Guid? projectId = null;
        Guid? documentId = null;

        if (entry.Entity is Project project)
        {
            projectId = project.Id;
        }
        else
        {
            var projProp = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "ProjectId");
            if (projProp?.CurrentValue is Guid pid && pid != Guid.Empty) projectId = pid;
        }

        if (entry.Entity is Document doc)
        {
            documentId = doc.Id;
            // A Document-type audit row also scopes to its Project.
            if (projectId is null) projectId = doc.ProjectId;
        }
        else
        {
            var docProp = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "DocumentId");
            if (docProp?.CurrentValue is Guid did && did != Guid.Empty) documentId = did;
        }

        return (projectId, documentId);
    }

    private (string? ip, string? ua) ExtractRequestMeta()
    {
        var ctx = httpAccessor?.HttpContext;
        if (ctx is null) return (null, null);
        return (
            ctx.Connection.RemoteIpAddress?.ToString(),
            ctx.Request.Headers.UserAgent.ToString() is { Length: > 0 } s ? s : null
        );
    }
}
