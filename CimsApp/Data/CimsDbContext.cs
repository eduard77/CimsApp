using Microsoft.EntityFrameworkCore;
using CimsApp.Models;
using CimsApp.Services.Tenancy;

namespace CimsApp.Data;

public class CimsDbContext(
    DbContextOptions<CimsDbContext> options,
    ITenantContext? tenant = null) : DbContext(options)
{
    // Null-object default keeps EF design-time (migrations, scaffolding)
    // working without a DI-provided tenant. In production a scoped
    // ITenantContext is always injected.
    private readonly ITenantContext _tenant = tenant ?? NullTenantContext.Instance;

    private sealed class NullTenantContext : ITenantContext
    {
        public static readonly NullTenantContext Instance = new();
        public Guid? OrganisationId => null;
        public Guid? UserId => null;
        public UserRole? GlobalRole => null;
        public bool IsSuperAdmin => false;
    }

    public DbSet<Organisation>       Organisations       => Set<Organisation>();
    public DbSet<User>               Users               => Set<User>();
    public DbSet<RefreshToken>       RefreshTokens       => Set<RefreshToken>();
    public DbSet<Project>            Projects            => Set<Project>();
    public DbSet<ProjectMember>      ProjectMembers      => Set<ProjectMember>();
    public DbSet<ProjectAppointment> ProjectAppointments => Set<ProjectAppointment>();
    public DbSet<CdeContainer>       CdeContainers       => Set<CdeContainer>();
    public DbSet<Document>           Documents           => Set<Document>();
    public DbSet<DocumentRevision>   DocumentRevisions   => Set<DocumentRevision>();
    public DbSet<Rfi>                Rfis                => Set<Rfi>();
    public DbSet<RfiDocument>        RfiDocuments        => Set<RfiDocument>();
    public DbSet<ActionItem>         ActionItems         => Set<ActionItem>();
    public DbSet<AuditLog>           AuditLogs           => Set<AuditLog>();
    public DbSet<Notification>       Notifications       => Set<Notification>();
    public DbSet<ProjectTemplate>    ProjectTemplates    => Set<ProjectTemplate>();
    public DbSet<Invitation>         Invitations         => Set<Invitation>();
    public DbSet<CostBreakdownItem>  CostBreakdownItems  => Set<CostBreakdownItem>();
    public DbSet<Commitment>         Commitments         => Set<Commitment>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        base.OnModelCreating(m);

        m.Entity<Organisation>(e => e.HasIndex(o => o.Code).IsUnique());

        m.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.HasOne(u => u.Organisation).WithMany(o => o.Users)
             .HasForeignKey(u => u.OrganisationId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<RefreshToken>(e =>
        {
            e.HasIndex(r => r.Token).IsUnique();
            e.HasOne(r => r.User).WithMany(u => u.RefreshTokens)
             .HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<Project>(e =>
        {
            e.HasIndex(p => new { p.AppointingPartyId, p.Code }).IsUnique();
            e.HasOne(p => p.AppointingParty).WithMany(o => o.AppointedProjects)
             .HasForeignKey(p => p.AppointingPartyId).OnDelete(DeleteBehavior.Restrict);
            e.Property(p => p.BudgetValue).HasPrecision(15, 2);
        });

        m.Entity<ProjectMember>(e =>
        {
            e.HasIndex(m2 => new { m2.ProjectId, m2.UserId }).IsUnique();
            e.HasOne(m2 => m2.Project).WithMany(p => p.Members)
             .HasForeignKey(m2 => m2.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(m2 => m2.User).WithMany(u => u.ProjectMemberships)
             .HasForeignKey(m2 => m2.UserId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<ProjectAppointment>(e =>
        {
            e.HasOne(a => a.Project).WithMany(p => p.Appointments)
             .HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(a => a.Organisation).WithMany(o => o.Appointments)
             .HasForeignKey(a => a.OrganisationId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<CdeContainer>(e =>
            e.HasOne(c => c.Project).WithMany(p => p.Containers)
             .HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.NoAction));

        m.Entity<Document>(e =>
        {
            e.HasIndex(d => d.DocumentNumber).IsUnique();
            e.HasIndex(d => new { d.ProjectId, d.CurrentState });
            e.Ignore(d => d.Tags); // handled via TagsCsv
            e.HasOne(d => d.Project).WithMany(p => p.Documents)
             .HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(d => d.Creator).WithMany(u => u.CreatedDocuments)
             .HasForeignKey(d => d.CreatorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.Container).WithMany(c => c.Documents)
             .HasForeignKey(d => d.ContainerId).OnDelete(DeleteBehavior.SetNull);
        });

        m.Entity<DocumentRevision>(e =>
        {
            e.HasIndex(r => new { r.DocumentId, r.Revision }).IsUnique();
            e.HasOne(r => r.Document).WithMany(d => d.Revisions)
             .HasForeignKey(r => r.DocumentId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(r => r.UploadedBy).WithMany(u => u.UploadedRevisions)
             .HasForeignKey(r => r.UploadedById).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<Rfi>(e =>
        {
            e.HasIndex(r => new { r.ProjectId, r.RfiNumber }).IsUnique();
            e.HasOne(r => r.Project).WithMany(p => p.Rfis)
             .HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<RfiDocument>(e =>
        {
            e.HasKey(r => new { r.RfiId, r.DocumentId });
            e.HasOne(r => r.Rfi).WithMany(rfi => rfi.Documents)
             .HasForeignKey(r => r.RfiId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(r => r.Document).WithMany(d => d.RfiLinks)
             .HasForeignKey(r => r.DocumentId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<ActionItem>(e =>
        {
            e.HasOne(a => a.Project).WithMany(p => p.ActionItems)
             .HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(a => a.CreatedBy).WithMany(u => u.CreatedActions)
             .HasForeignKey(a => a.CreatedById).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Assignee).WithMany(u => u.AssignedActions)
             .HasForeignKey(a => a.AssigneeId).OnDelete(DeleteBehavior.SetNull);
        });

        m.Entity<AuditLog>(e =>
        {
            e.HasIndex(a => new { a.ProjectId, a.CreatedAt });
            e.HasOne(a => a.User).WithMany(u => u.AuditLogs)
             .HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Project).WithMany(p => p.AuditLogs)
             .HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(a => a.Document).WithMany(d => d.AuditLogs)
             .HasForeignKey(a => a.DocumentId).OnDelete(DeleteBehavior.SetNull);
        });
        m.Entity<ProjectTemplate>(e =>
        {
            e.HasIndex(t => new { t.ProjectId, t.StorageKey }).IsUnique();
            e.HasOne(t => t.Project).WithMany()
             .HasForeignKey(t => t.ProjectId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<Invitation>(e =>
        {
            e.HasIndex(i => i.TokenHash).IsUnique();
            e.HasIndex(i => new { i.OrganisationId, i.ConsumedAt });
            // All three FKs are NoAction to satisfy SQL Server's
            // multi-cascade-path check (cascade on Organisation +
            // SetNull on Users via two columns produced
            // FK_Invitations_Users_CreatedById errors). Aligns with
            // the dominant NoAction pattern used elsewhere in this
            // DbContext (ProjectMember, RfiDocument, etc.). Deleting
            // an Organisation now requires explicit invitation
            // cleanup first — appropriate for an audit-style entity.
            e.HasOne(i => i.Organisation).WithMany()
             .HasForeignKey(i => i.OrganisationId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(i => i.ConsumedByUser).WithMany()
             .HasForeignKey(i => i.ConsumedByUserId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(i => i.CreatedBy).WithMany()
             .HasForeignKey(i => i.CreatedById).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<CostBreakdownItem>(e =>
        {
            // Code unique within a project (any depth in the tree).
            e.HasIndex(c => new { c.ProjectId, c.Code }).IsUnique();
            // Fast lookup of children for tree traversal.
            e.HasIndex(c => new { c.ProjectId, c.ParentId });
            e.HasOne(c => c.Project).WithMany()
             .HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.NoAction);
            // Self-referencing tree: NoAction on parent so deleting a
            // parent does not silently orphan children — service layer
            // (T-S1-03 onwards) will handle cascade decisions
            // explicitly.
            e.HasOne(c => c.Parent).WithMany(c => c.Children)
             .HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.NoAction);
            // T-S1-04. decimal(18,2) per kickoff spec; Project.BudgetValue
            // uses (15,2) but per-line CBS budgets aggregate independently
            // and 18 digits of precision keeps headroom for very large
            // works.
            e.Property(c => c.Budget).HasPrecision(18, 2);
        });

        m.Entity<Commitment>(e =>
        {
            // Rollup queries group by CostBreakdownItemId — index that.
            // Project-level filtering is covered by the per-project
            // (ProjectId, CostBreakdownItemId) shape of the index.
            e.HasIndex(c => new { c.ProjectId, c.CostBreakdownItemId });
            e.Property(c => c.Amount).HasPrecision(18, 2);
            e.HasOne(c => c.Project).WithMany()
             .HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(c => c.CostBreakdownItem).WithMany()
             .HasForeignKey(c => c.CostBreakdownItemId).OnDelete(DeleteBehavior.NoAction);
        });

        // ── Tenant isolation (PAFM F.1, ADR-0003) ────────────────────────
        // Global query filter on OrganisationId. Anonymous contexts
        // (null tenant) see nothing by design; pre-auth paths in
        // AuthService use IgnoreQueryFilters() to bypass. Only
        // Organisation (the tenant anchor) is unfiltered — SuperAdmin
        // cross-tenant reads opt in via IgnoreQueryFilters() at the
        // call site (T-S0-07).
        m.Entity<User>().HasQueryFilter(u => u.OrganisationId == _tenant.OrganisationId);
        m.Entity<RefreshToken>().HasQueryFilter(x => x.User.OrganisationId == _tenant.OrganisationId);
        m.Entity<Project>().HasQueryFilter(p => p.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<ProjectMember>().HasQueryFilter(x => x.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<ProjectAppointment>().HasQueryFilter(x => x.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<CdeContainer>().HasQueryFilter(x => x.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<Document>().HasQueryFilter(x => x.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<DocumentRevision>().HasQueryFilter(x => x.Document.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<Rfi>().HasQueryFilter(x => x.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<RfiDocument>().HasQueryFilter(x => x.Rfi.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<ActionItem>().HasQueryFilter(x => x.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<ProjectTemplate>().HasQueryFilter(x => x.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<Notification>().HasQueryFilter(x => x.User.OrganisationId == _tenant.OrganisationId);
        m.Entity<AuditLog>().HasQueryFilter(x => x.User.OrganisationId == _tenant.OrganisationId);
        // Invitations are tenant-scoped. Pre-auth consumption in
        // InvitationService.ConsumeAsync uses IgnoreQueryFilters(), the
        // same pattern AuthService uses for User/RefreshToken lookups.
        m.Entity<Invitation>().HasQueryFilter(i => i.OrganisationId == _tenant.OrganisationId);
        m.Entity<CostBreakdownItem>().HasQueryFilter(c => c.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<Commitment>().HasQueryFilter(c => c.Project.AppointingPartyId == _tenant.OrganisationId);
    }

    public override int SaveChanges()
    { SetTimestamps(); return base.SaveChanges(); }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    { SetTimestamps(); return base.SaveChangesAsync(ct); }

    private void SetTimestamps()
    {
        foreach (var e in ChangeTracker.Entries().Where(e => e.State == EntityState.Modified))
        {
            if (e.Entity is Organisation o)  o.UpdatedAt  = DateTime.UtcNow;
            else if (e.Entity is User u)     u.UpdatedAt  = DateTime.UtcNow;
            else if (e.Entity is Project p)  p.UpdatedAt  = DateTime.UtcNow;
            else if (e.Entity is Document d) d.UpdatedAt  = DateTime.UtcNow;
            else if (e.Entity is CdeContainer c) c.UpdatedAt = DateTime.UtcNow;
            else if (e.Entity is Rfi r)      r.UpdatedAt  = DateTime.UtcNow;
            else if (e.Entity is ActionItem a) a.UpdatedAt = DateTime.UtcNow;
            else if (e.Entity is CostBreakdownItem cbi) cbi.UpdatedAt = DateTime.UtcNow;
        }
    }
}
