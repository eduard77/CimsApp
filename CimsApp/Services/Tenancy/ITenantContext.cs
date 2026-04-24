using CimsApp.Models;

namespace CimsApp.Services.Tenancy;

/// <summary>
/// Request-scoped view of the calling tenant. Consumed by the EF global
/// query filter on tenant-scoped entities and by services that need the
/// acting user. Returns nulls for anonymous / system contexts.
/// </summary>
public interface ITenantContext
{
    Guid? OrganisationId { get; }
    Guid? UserId { get; }

    /// <summary>
    /// Caller's cross-project role, if any. Null for regular users who
    /// have only project-scoped roles (read from ProjectMember).
    /// </summary>
    UserRole? GlobalRole { get; }

    /// <summary>
    /// True when the caller carries the SuperAdmin global role. Call
    /// sites that wish to show cross-tenant data must explicitly opt in
    /// with IgnoreQueryFilters() on the query (see ADR-0003); the audit
    /// interceptor records any resulting mutations as usual.
    /// </summary>
    bool IsSuperAdmin { get; }
}
