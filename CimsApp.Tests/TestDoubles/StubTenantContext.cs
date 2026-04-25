using CimsApp.Models;
using CimsApp.Services.Tenancy;

namespace CimsApp.Tests.TestDoubles;

/// <summary>
/// In-memory ITenantContext for tests. Mutate properties on the
/// instance to simulate switching identities mid-test (e.g. login as
/// tenant A, then tenant B).
/// </summary>
public sealed class StubTenantContext : ITenantContext
{
    public Guid? OrganisationId { get; set; }
    public Guid? UserId { get; set; }
    public UserRole? GlobalRole { get; set; }
    public bool IsSuperAdmin => GlobalRole == UserRole.SuperAdmin;
}
