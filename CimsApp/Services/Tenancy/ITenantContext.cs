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
}
