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
    public DbSet<CostPeriod>         CostPeriods         => Set<CostPeriod>();
    public DbSet<ActualCost>         ActualCosts         => Set<ActualCost>();
    public DbSet<Variation>          Variations          => Set<Variation>();
    public DbSet<PaymentCertificate> PaymentCertificates => Set<PaymentCertificate>();
    public DbSet<RiskCategory>       RiskCategories      => Set<RiskCategory>();
    public DbSet<Risk>               Risks               => Set<Risk>();
    public DbSet<RiskDrawdown>       RiskDrawdowns       => Set<RiskDrawdown>();
    public DbSet<Stakeholder>        Stakeholders        => Set<Stakeholder>();
    public DbSet<EngagementLog>      EngagementLogs      => Set<EngagementLog>();
    public DbSet<CommunicationItem>  CommunicationItems  => Set<CommunicationItem>();
    public DbSet<Activity>           Activities          => Set<Activity>();
    public DbSet<Dependency>         Dependencies        => Set<Dependency>();
    public DbSet<ScheduleBaseline>   ScheduleBaselines   => Set<ScheduleBaseline>();
    public DbSet<ScheduleBaselineActivity> ScheduleBaselineActivities => Set<ScheduleBaselineActivity>();
    public DbSet<LookaheadEntry>      LookaheadEntries     => Set<LookaheadEntry>();
    public DbSet<WeeklyWorkPlan>      WeeklyWorkPlans      => Set<WeeklyWorkPlan>();
    public DbSet<WeeklyTaskCommitment> WeeklyTaskCommitments => Set<WeeklyTaskCommitment>();
    public DbSet<ChangeRequest>       ChangeRequests       => Set<ChangeRequest>();
    public DbSet<ProcurementStrategy> ProcurementStrategies => Set<ProcurementStrategy>();
    public DbSet<TenderPackage>       TenderPackages       => Set<TenderPackage>();
    public DbSet<Tender>              Tenders              => Set<Tender>();

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
            // DocumentNumber is unique WITHIN A PROJECT. Two tenants
            // can each have a Project with the same Code (project Code
            // is per-tenant unique via (AppointingPartyId, Code)), so
            // both can legitimately derive identical DocumentNumbers
            // from `PROJ-ORIG-VOL-LVL-TYPE-ROLE-NNNN`. The original
            // global unique index allowed only the first such number
            // to land — the second tenant's SaveChanges threw a
            // unique-index violation and the user saw HTTP 500.
            // Scoping the uniqueness to (ProjectId, DocumentNumber)
            // matches the project-level convention and lets both
            // tenants coexist; same-project duplicates are still
            // prevented and surface as the existing
            // ConflictException at the service layer.
            e.HasIndex(d => new { d.ProjectId, d.DocumentNumber }).IsUnique();
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
            // B-017. PercentComplete is a fraction in [0, 1]; (5, 4)
            // gives 0.0001 granularity which is finer than any sensible
            // assessor would report. ScheduledStart / ScheduledEnd use
            // the default datetime2 mapping (no special precision needed).
            e.Property(c => c.PercentComplete).HasPrecision(5, 4);
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

        m.Entity<CostPeriod>(e =>
        {
            e.HasIndex(p => new { p.ProjectId, p.StartDate });
            e.Property(p => p.PlannedCashflow).HasPrecision(18, 2);
            e.HasOne(p => p.Project).WithMany()
             .HasForeignKey(p => p.ProjectId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<ActualCost>(e =>
        {
            // Two indices for the two natural rollup directions: per
            // CBS line (the standard committed/actual rollup) and per
            // period (cashflow / reporting at T-S1-11).
            e.HasIndex(a => new { a.ProjectId, a.CostBreakdownItemId });
            e.HasIndex(a => new { a.ProjectId, a.PeriodId });
            e.Property(a => a.Amount).HasPrecision(18, 2);
            e.HasOne(a => a.Project).WithMany()
             .HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(a => a.CostBreakdownItem).WithMany()
             .HasForeignKey(a => a.CostBreakdownItemId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(a => a.Period).WithMany(p => p.Actuals)
             .HasForeignKey(a => a.PeriodId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<PaymentCertificate>(e =>
        {
            // One certificate per period (any state). Two parallel
            // drafts on the same period would race the cumulative
            // chain — disallow at the storage layer.
            e.HasIndex(c => new { c.ProjectId, c.PeriodId }).IsUnique();
            e.HasIndex(c => new { c.ProjectId, c.CertificateNumber }).IsUnique();
            e.Property(c => c.CumulativeValuation).HasPrecision(18, 2);
            e.Property(c => c.CumulativeMaterialsOnSite).HasPrecision(18, 2);
            e.Property(c => c.IncludedVariationsAmount).HasPrecision(18, 2);
            e.Property(c => c.RetentionPercent).HasPrecision(5, 2);
            // NoAction across all FKs — same multi-cascade-path
            // reasoning that drives Variation / Invitation.
            e.HasOne(c => c.Project).WithMany()
             .HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(c => c.Period).WithMany()
             .HasForeignKey(c => c.PeriodId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(c => c.IssuedBy).WithMany()
             .HasForeignKey(c => c.IssuedById).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<Variation>(e =>
        {
            // Project-scoped sequential number must be unique per project.
            e.HasIndex(v => new { v.ProjectId, v.VariationNumber }).IsUnique();
            e.Property(v => v.EstimatedCostImpact).HasPrecision(18, 2);
            // All four FKs NoAction for the same multi-cascade-path
            // reasoning that drives the Invitation entity's config:
            // Project, RaisedBy, DecidedBy, and CostBreakdownItem each
            // ultimately resolve to Organisation via Project, and SQL
            // Server forbids multiple cascade paths to the same root.
            e.HasOne(v => v.Project).WithMany()
             .HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(v => v.CostBreakdownItem).WithMany()
             .HasForeignKey(v => v.CostBreakdownItemId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(v => v.RaisedBy).WithMany()
             .HasForeignKey(v => v.RaisedById).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(v => v.DecidedBy).WithMany()
             .HasForeignKey(v => v.DecidedById).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<RiskCategory>(e =>
        {
            // RBS Code unique within a project at any depth in the tree —
            // mirrors the CostBreakdownItem index pattern.
            e.HasIndex(r => new { r.ProjectId, r.Code }).IsUnique();
            // Fast lookup of children for tree traversal.
            e.HasIndex(r => new { r.ProjectId, r.ParentId });
            e.HasOne(r => r.Project).WithMany()
             .HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.NoAction);
            // Self-referencing tree: NoAction on parent so deleting a
            // category does not silently orphan its children — service
            // layer in T-S2-04 onwards handles cascade decisions
            // explicitly. Same shape as CostBreakdownItem.
            e.HasOne(r => r.Parent).WithMany(r => r.Children)
             .HasForeignKey(r => r.ParentId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<Risk>(e =>
        {
            // Heat-map / register queries hit (ProjectId, Status) and
            // (ProjectId, Score) frequently; index both. Score
            // descending matches the natural "highest risks first"
            // listing order.
            e.HasIndex(r => new { r.ProjectId, r.Status });
            e.HasIndex(r => new { r.ProjectId, r.Score });
            e.Property(r => r.ContingencyAmount).HasPrecision(18, 2);
            // T-S2-07 3-point estimates — same precision as
            // ContingencyAmount and the cost-domain decimals.
            e.Property(r => r.BestCase).HasPrecision(18, 2);
            e.Property(r => r.MostLikely).HasPrecision(18, 2);
            e.Property(r => r.WorstCase).HasPrecision(18, 2);
            // All FKs NoAction for the same multi-cascade-path reasoning
            // that drives the Variation / Invitation configs. Project,
            // Category, and Owner each ultimately resolve to
            // Organisation.
            e.HasOne(r => r.Project).WithMany()
             .HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(r => r.Category).WithMany()
             .HasForeignKey(r => r.CategoryId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(r => r.Owner).WithMany()
             .HasForeignKey(r => r.OwnerId).OnDelete(DeleteBehavior.NoAction);
            // T-S2-06 qualitative assessment FK — also NoAction.
            e.HasOne(r => r.AssessedBy).WithMany()
             .HasForeignKey(r => r.AssessedById).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<RiskDrawdown>(e =>
        {
            // Drawdown listings hit (RiskId, OccurredAt) and
            // (ProjectId, OccurredAt) for register and project rollup
            // views; index both.
            e.HasIndex(d => new { d.RiskId, d.OccurredAt });
            e.HasIndex(d => new { d.ProjectId, d.OccurredAt });
            e.Property(d => d.Amount).HasPrecision(18, 2);
            // All FKs NoAction for the same multi-cascade-path reasoning
            // that drives the rest of the cost-domain configs.
            e.HasOne(d => d.Project).WithMany()
             .HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(d => d.Risk).WithMany(r => r.Drawdowns)
             .HasForeignKey(d => d.RiskId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(d => d.RecordedBy).WithMany()
             .HasForeignKey(d => d.RecordedById).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<Stakeholder>(e =>
        {
            // Register listings hit (ProjectId, Score) for "highest
            // priority first" and (ProjectId, IsActive) for filtering
            // out deactivated rows.
            e.HasIndex(s => new { s.ProjectId, s.Score });
            e.HasIndex(s => new { s.ProjectId, s.IsActive });
            e.HasOne(s => s.Project).WithMany()
             .HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<EngagementLog>(e =>
        {
            // Listings hit (StakeholderId, OccurredAt desc) for the
            // per-stakeholder log and (ProjectId, OccurredAt desc)
            // for project-wide engagement reporting.
            e.HasIndex(g => new { g.StakeholderId, g.OccurredAt });
            e.HasIndex(g => new { g.ProjectId, g.OccurredAt });
            e.HasOne(g => g.Project).WithMany()
             .HasForeignKey(g => g.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(g => g.Stakeholder).WithMany(s => s.Engagements)
             .HasForeignKey(g => g.StakeholderId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(g => g.RecordedBy).WithMany()
             .HasForeignKey(g => g.RecordedById).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<CommunicationItem>(e =>
        {
            // Listings hit (ProjectId, IsActive) for the matrix view
            // and (ProjectId, ItemType) for the by-type filter.
            e.HasIndex(c => new { c.ProjectId, c.IsActive });
            e.HasIndex(c => new { c.ProjectId, c.ItemType });
            e.HasOne(c => c.Project).WithMany()
             .HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(c => c.Owner).WithMany()
             .HasForeignKey(c => c.OwnerId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<Activity>(e =>
        {
            // (ProjectId, Code) is the natural unique-within-project
            // identifier; the CPM solver and the Gantt renderer hit
            // (ProjectId, IsActive) for the live activity list.
            e.HasIndex(a => new { a.ProjectId, a.Code }).IsUnique();
            e.HasIndex(a => new { a.ProjectId, a.IsActive });
            e.HasOne(a => a.Project).WithMany()
             .HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(a => a.Assignee).WithMany()
             .HasForeignKey(a => a.AssigneeId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<Dependency>(e =>
        {
            // CPM forward pass walks (ProjectId, SuccessorId);
            // backward pass walks (ProjectId, PredecessorId).
            e.HasIndex(d => new { d.ProjectId, d.SuccessorId });
            e.HasIndex(d => new { d.ProjectId, d.PredecessorId });
            // Disallow duplicate (Predecessor, Successor) pairs.
            // The Type / Lag are properties of *the* link; multi-link
            // pairs are vanishingly rare in real schedules and would
            // muddle the CPM solver semantics.
            e.HasIndex(d => new { d.PredecessorId, d.SuccessorId }).IsUnique();
            e.HasOne(d => d.Project).WithMany()
             .HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(d => d.Predecessor).WithMany(a => a.SuccessorLinks)
             .HasForeignKey(d => d.PredecessorId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(d => d.Successor).WithMany(a => a.PredecessorLinks)
             .HasForeignKey(d => d.SuccessorId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<ScheduleBaseline>(e =>
        {
            e.HasIndex(b => new { b.ProjectId, b.CapturedAt });
            e.HasOne(b => b.Project).WithMany()
             .HasForeignKey(b => b.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(b => b.CapturedBy).WithMany()
             .HasForeignKey(b => b.CapturedById).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<ScheduleBaselineActivity>(e =>
        {
            // Comparison endpoint joins per-baseline rows back to
            // current Activity rows; index on (ScheduleBaselineId,
            // ActivityId) makes that lookup O(log n).
            e.HasIndex(b => new { b.ScheduleBaselineId, b.ActivityId });
            e.HasOne(b => b.ScheduleBaseline).WithMany(s => s.Activities)
             .HasForeignKey(b => b.ScheduleBaselineId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(b => b.Activity).WithMany()
             .HasForeignKey(b => b.ActivityId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<LookaheadEntry>(e =>
        {
            // Lookahead board renders by (ProjectId, WeekStarting);
            // per-activity drill-down hits (ActivityId, WeekStarting).
            e.HasIndex(le => new { le.ProjectId, le.WeekStarting, le.IsActive });
            e.HasIndex(le => new { le.ActivityId, le.WeekStarting });
            e.HasOne(le => le.Project).WithMany()
             .HasForeignKey(le => le.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(le => le.Activity).WithMany()
             .HasForeignKey(le => le.ActivityId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(le => le.CreatedBy).WithMany()
             .HasForeignKey(le => le.CreatedById).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<WeeklyWorkPlan>(e =>
        {
            // One WWP per (project, week) — unique constraint.
            e.HasIndex(w => new { w.ProjectId, w.WeekStarting }).IsUnique();
            e.HasOne(w => w.Project).WithMany()
             .HasForeignKey(w => w.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(w => w.CreatedBy).WithMany()
             .HasForeignKey(w => w.CreatedById).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<WeeklyTaskCommitment>(e =>
        {
            // Disallow duplicate (WWP, Activity) commitments — each
            // WWP can commit to a given activity at most once.
            e.HasIndex(c => new { c.WeeklyWorkPlanId, c.ActivityId }).IsUnique();
            e.HasIndex(c => new { c.ProjectId, c.WeeklyWorkPlanId });
            e.HasOne(c => c.WeeklyWorkPlan).WithMany(w => w.Commitments)
             .HasForeignKey(c => c.WeeklyWorkPlanId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(c => c.Activity).WithMany()
             .HasForeignKey(c => c.ActivityId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<ChangeRequest>(e =>
        {
            // Project-scoped sequential number — unique within project.
            e.HasIndex(c => new { c.ProjectId, c.Number }).IsUnique();
            // Register listing renders by (ProjectId, State) and
            // (ProjectId, RaisedAt desc) for "newest first" views.
            e.HasIndex(c => new { c.ProjectId, c.State });
            e.HasIndex(c => new { c.ProjectId, c.RaisedAt });
            e.HasOne(c => c.Project).WithMany()
             .HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(c => c.RaisedBy).WithMany()
             .HasForeignKey(c => c.RaisedById).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(c => c.AssessedBy).WithMany()
             .HasForeignKey(c => c.AssessedById).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(c => c.DecisionBy).WithMany()
             .HasForeignKey(c => c.DecisionById).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(c => c.GeneratedVariation).WithMany()
             .HasForeignKey(c => c.GeneratedVariationId).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<ProcurementStrategy>(e =>
        {
            // One row per project — unique constraint enforces
            // upsert semantics at the DB layer; the service-layer
            // CreateOrUpdateAsync method does the read-then-update
            // dance.
            e.HasIndex(s => s.ProjectId).IsUnique();
            e.HasOne(s => s.Project).WithMany()
             .HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(s => s.ApprovedBy).WithMany()
             .HasForeignKey(s => s.ApprovedById).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<TenderPackage>(e =>
        {
            // Project-scoped sequential number — unique within project.
            e.HasIndex(t => new { t.ProjectId, t.Number }).IsUnique();
            // Listing renders by (ProjectId, State, IsActive).
            e.HasIndex(t => new { t.ProjectId, t.State });
            e.HasIndex(t => new { t.ProjectId, t.IsActive });
            e.HasOne(t => t.Project).WithMany()
             .HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(t => t.CreatedBy).WithMany()
             .HasForeignKey(t => t.CreatedById).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(t => t.IssuedBy).WithMany()
             .HasForeignKey(t => t.IssuedById).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(t => t.ClosedBy).WithMany()
             .HasForeignKey(t => t.ClosedById).OnDelete(DeleteBehavior.NoAction);
        });

        m.Entity<Tender>(e =>
        {
            // Listings hit (TenderPackageId, State) for the per-package
            // bid-evaluation view.
            e.HasIndex(t => new { t.TenderPackageId, t.State });
            e.HasIndex(t => new { t.ProjectId, t.SubmittedAt });
            e.HasOne(t => t.Project).WithMany()
             .HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(t => t.TenderPackage).WithMany(p => p.Tenders)
             .HasForeignKey(t => t.TenderPackageId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(t => t.CreatedBy).WithMany()
             .HasForeignKey(t => t.CreatedById).OnDelete(DeleteBehavior.NoAction);
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
        // AuditLog.UserId is nullable for anonymous flows (bootstrap
        // invitation creation has no actor). Anonymous-actor audit
        // rows have no tenant binding — they're system events visible
        // only via IgnoreQueryFilters (SuperAdmin / audit-export).
        m.Entity<AuditLog>().HasQueryFilter(x => x.User != null && x.User.OrganisationId == _tenant.OrganisationId);
        // Invitations are tenant-scoped. Pre-auth consumption in
        // InvitationService.ConsumeAsync uses IgnoreQueryFilters(), the
        // same pattern AuthService uses for User/RefreshToken lookups.
        m.Entity<Invitation>().HasQueryFilter(i => i.OrganisationId == _tenant.OrganisationId);
        m.Entity<CostBreakdownItem>().HasQueryFilter(c => c.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<Commitment>().HasQueryFilter(c => c.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<CostPeriod>().HasQueryFilter(p => p.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<ActualCost>().HasQueryFilter(a => a.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<Variation>().HasQueryFilter(v => v.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<PaymentCertificate>().HasQueryFilter(c => c.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<RiskCategory>().HasQueryFilter(r => r.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<Risk>().HasQueryFilter(r => r.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<RiskDrawdown>().HasQueryFilter(d => d.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<Stakeholder>().HasQueryFilter(s => s.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<EngagementLog>().HasQueryFilter(g => g.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<CommunicationItem>().HasQueryFilter(c => c.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<Activity>().HasQueryFilter(a => a.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<Dependency>().HasQueryFilter(d => d.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<ScheduleBaseline>().HasQueryFilter(b => b.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<ScheduleBaselineActivity>().HasQueryFilter(b => b.ScheduleBaseline.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<LookaheadEntry>().HasQueryFilter(le => le.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<WeeklyWorkPlan>().HasQueryFilter(w => w.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<WeeklyTaskCommitment>().HasQueryFilter(c => c.WeeklyWorkPlan.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<ChangeRequest>().HasQueryFilter(c => c.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<ProcurementStrategy>().HasQueryFilter(s => s.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<TenderPackage>().HasQueryFilter(t => t.Project.AppointingPartyId == _tenant.OrganisationId);
        m.Entity<Tender>().HasQueryFilter(t => t.Project.AppointingPartyId == _tenant.OrganisationId);
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
            else if (e.Entity is Commitment cm) cm.UpdatedAt = DateTime.UtcNow;
            else if (e.Entity is RiskCategory rc) rc.UpdatedAt = DateTime.UtcNow;
            else if (e.Entity is Risk risk) risk.UpdatedAt = DateTime.UtcNow;
            else if (e.Entity is Stakeholder s) s.UpdatedAt = DateTime.UtcNow;
            else if (e.Entity is CommunicationItem ci) ci.UpdatedAt = DateTime.UtcNow;
            else if (e.Entity is Activity act) act.UpdatedAt = DateTime.UtcNow;
            else if (e.Entity is WeeklyTaskCommitment wtc) wtc.UpdatedAt = DateTime.UtcNow;
            else if (e.Entity is ChangeRequest cr) cr.UpdatedAt = DateTime.UtcNow;
            else if (e.Entity is ProcurementStrategy ps) ps.UpdatedAt = DateTime.UtcNow;
            else if (e.Entity is TenderPackage tp) tp.UpdatedAt = DateTime.UtcNow;
            else if (e.Entity is Tender ten) ten.UpdatedAt = DateTime.UtcNow;
        }
    }
}
