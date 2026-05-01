using CimsApp.Data;
using CimsApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Model-level verification that every tenant-scoped entity has a
/// global query filter registered. Does not connect to a database —
/// only inspects the EF Core model built from OnModelCreating.
/// A full runtime multi-tenant isolation test (T-S0-04) is deferred
/// pending ADR approval for an in-memory test provider.
/// </summary>
public class CimsDbContextTenantFilterTests
{
    private static readonly Type[] TenantScoped =
    [
        typeof(User),
        typeof(RefreshToken),
        typeof(Project),
        typeof(ProjectMember),
        typeof(ProjectAppointment),
        typeof(CdeContainer),
        typeof(Document),
        typeof(DocumentRevision),
        typeof(Rfi),
        typeof(RfiDocument),
        typeof(ActionItem),
        typeof(ProjectTemplate),
        typeof(Notification),
        typeof(AuditLog),
        typeof(Invitation),
        typeof(CostBreakdownItem),
        // S1 cost-domain additions (T-S1-13 sweep).
        typeof(Commitment),
        typeof(CostPeriod),
        typeof(ActualCost),
        typeof(Variation),
        typeof(PaymentCertificate),
        // S2 risk-domain additions (T-S2-02 onwards).
        typeof(RiskCategory),
        typeof(Risk),
        typeof(RiskDrawdown),
        // S3 stakeholder-domain additions (T-S3-02 onwards).
        typeof(Stakeholder),
        typeof(EngagementLog),
        typeof(CommunicationItem),
        // S4 schedule-domain additions (T-S4-02 onwards).
        typeof(Activity),
        typeof(Dependency),
    ];

    private static readonly Type[] IntentionallyUnfiltered =
    [
        typeof(Organisation),
    ];

    private static CimsDbContext BuildContext() =>
        new(new DbContextOptionsBuilder<CimsDbContext>()
            .UseSqlServer("Server=model-only;Database=model-only;")
            .Options);

    [Fact]
    public void Every_tenant_scoped_entity_has_a_global_query_filter()
    {
        using var ctx = BuildContext();

        foreach (var clrType in TenantScoped)
        {
            var entityType = ctx.Model.FindEntityType(clrType);
            Assert.NotNull(entityType);
            Assert.NotNull(entityType.GetQueryFilter());
        }
    }

    [Fact]
    public void Intentionally_unfiltered_entities_have_no_filter()
    {
        using var ctx = BuildContext();

        foreach (var clrType in IntentionallyUnfiltered)
        {
            var entityType = ctx.Model.FindEntityType(clrType);
            Assert.NotNull(entityType);
            Assert.Null(entityType.GetQueryFilter());
        }
    }
}
