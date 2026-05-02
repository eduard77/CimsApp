using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace CimsApp.Models;

public class Organisation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [Required, MaxLength(10)]  public string Code { get; set; } = "";
    public string? Address { get; set; }
    public string? Country { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<User> Users { get; set; } = [];
    public ICollection<Project> AppointedProjects { get; set; } = [];
    public ICollection<ProjectAppointment> Appointments { get; set; } = [];
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(200)] public string Email { get; set; } = "";
    // [JsonIgnore]: never appear in API responses. The bcrypt hash is
    // used for BCrypt.Verify only — not output. Without this, any
    // endpoint that returns a User (directly or via a navigation
    // chain like Project.Members[0].User) leaks the hash. Found
    // when a real-DB smoke test of POST /api/v1/projects returned
    // "passwordHash":"$2a$11$..." in the response body (2026-04-29).
    [JsonIgnore]
    [Required] public string PasswordHash { get; set; } = "";
    [Required, MaxLength(100)] public string FirstName { get; set; } = "";
    [Required, MaxLength(100)] public string LastName { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid OrganisationId { get; set; }
    public Organisation Organisation { get; set; } = null!;
    // Cross-project role: SuperAdmin or OrgAdmin. Null for regular users
    // whose only roles live on ProjectMember. SuperAdmin is permitted
    // cross-tenant visibility (see ADR-0003, PAFM F.1).
    public UserRole? GlobalRole { get; set; }

    /// <summary>B-001: any access token issued before this cutoff is
    /// rejected by `JwtBearerEvents.OnTokenValidated`. Bumped on
    /// explicit revoke (e.g. role demotion, deactivation, password
    /// reset). Null = no cutoff applied. The matching `IsActive == false`
    /// case is also rejected at the same hook regardless of cutoff.</summary>
    public DateTime? TokenInvalidationCutoff { get; set; }
    [NotMapped] public string FullName => $"{FirstName} {LastName}";
    public ICollection<ProjectMember> ProjectMemberships { get; set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<AuditLog> AuditLogs { get; set; } = [];
    public ICollection<Document> CreatedDocuments { get; set; } = [];
    public ICollection<DocumentRevision> UploadedRevisions { get; set; } = [];
    public ICollection<ActionItem> AssignedActions { get; set; } = [];
    public ICollection<ActionItem> CreatedActions { get; set; } = [];
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    // [JsonIgnore]: the literal refresh token grants a fresh access
    // session; it must never appear in any response other than the
    // auth endpoints' explicit AuthResponse DTO. Defense-in-depth
    // alongside User.PasswordHash and Invitation.TokenHash.
    [JsonIgnore]
    [Required] public string Token { get; set; } = "";
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }
    [NotMapped] public bool IsActive => RevokedAt == null && ExpiresAt > DateTime.UtcNow;
}

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [Required, MaxLength(20)]  public string Code { get; set; } = "";
    public string? Description { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Initiation;
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? Location { get; set; }
    public string? Country { get; set; }
    [MaxLength(3)] public string Currency { get; set; } = "GBP";
    [Column(TypeName = "decimal(15,2)")] public decimal? BudgetValue { get; set; }
    public string? Sector { get; set; }
    public string? Sponsor { get; set; }
    public string? EirRef { get; set; }
    public string? MidpRef { get; set; }

    /// <summary>T-S10-02. Higher-Risk Building flag per Building
    /// Safety Act 2022. False for the majority of projects. When
    /// true, <see cref="HrbCategory"/> classifies the building
    /// per Building Safety Regulator guidance. v1.0 ships manual
    /// PM toggling; per-tenant inference rules → v1.1 / B-072.
    /// // BSA 2022 ref: Part 4 (Higher-Risk Buildings).</summary>
    public bool IsHrb { get; set; }

    /// <summary>BSA 2022 HRB category (A / B / C). NotApplicable
    /// for non-HRB projects (must agree with IsHrb=false). Mirrors
    /// the per-ChangeRequest `BsaHrbCategory` from S5.
    /// // BSA 2022 ref: Schedule 1 (HRB categorisation).</summary>
    public BsaHrbCategory HrbCategory { get; set; } = BsaHrbCategory.NotApplicable;

    public Guid AppointingPartyId { get; set; }
    public Organisation AppointingParty { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<ProjectMember> Members { get; set; } = [];
    public ICollection<ProjectAppointment> Appointments { get; set; } = [];
    public ICollection<CdeContainer> Containers { get; set; } = [];
    public ICollection<Document> Documents { get; set; } = [];
    public ICollection<Rfi> Rfis { get; set; } = [];
    public ICollection<ActionItem> ActionItems { get; set; } = [];
    public ICollection<AuditLog> AuditLogs { get; set; } = [];
}

public class ProjectMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public UserRole Role { get; set; } = UserRole.TaskTeamMember;
    public bool IsActive { get; set; } = true;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

public class ProjectAppointment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid OrganisationId { get; set; }
    public Organisation Organisation { get; set; } = null!;
    [Required] public string Role { get; set; } = "";
    public string? Scope { get; set; }
    public string? BepRef { get; set; }
    public string? TidpRef { get; set; }
    public bool IsLead { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CdeContainer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    [Required, MaxLength(6)]   public string Originator { get; set; } = "";
    [MaxLength(6)] public string? Volume { get; set; }
    [MaxLength(6)] public string? Level { get; set; }
    [Required, MaxLength(4)] public string Type { get; set; } = "";
    [MaxLength(6)] public string? Discipline { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Document> Documents { get; set; } = [];
}

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid? ContainerId { get; set; }
    public CdeContainer? Container { get; set; }
    [Required, MaxLength(20)] public string ProjectCode { get; set; } = "";
    [Required, MaxLength(6)]  public string Originator { get; set; } = "";
    [MaxLength(6)] public string? Volume { get; set; }
    [MaxLength(6)] public string? Level { get; set; }
    [Required, MaxLength(4)] public string DocType { get; set; } = "";
    [MaxLength(6)] public string? Role { get; set; }
    [Required, MaxLength(8)] public string Number { get; set; } = "";
    [Required, MaxLength(100)] public string DocumentNumber { get; set; } = "";
    [Required, MaxLength(500)] public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DocumentType Type { get; set; } = DocumentType.Other;
    public CdeState CurrentState { get; set; } = CdeState.WorkInProgress;
    public string TagsCsv { get; set; } = "";
    [NotMapped] public string[] Tags
    {
        get => string.IsNullOrEmpty(TagsCsv) ? [] : TagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        set => TagsCsv = string.Join(',', value);
    }
    public Guid CreatorId { get; set; }
    public User Creator { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<DocumentRevision> Revisions { get; set; } = [];
    public ICollection<RfiDocument> RfiLinks { get; set; } = [];
    public ICollection<AuditLog> AuditLogs { get; set; } = [];
}

public class DocumentRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    [Required, MaxLength(6)] public string Revision { get; set; } = "";
    public CdeState Status { get; set; } = CdeState.WorkInProgress;
    public SuitabilityCode Suitability { get; set; } = SuitabilityCode.S0;
    public string? Description { get; set; }
    [Required] public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string MimeType { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string Checksum { get; set; } = "";
    public Guid UploadedById { get; set; }
    public User UploadedBy { get; set; } = null!;
    public Guid? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public bool IsLatest { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Rfi
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    [Required, MaxLength(20)] public string RfiNumber { get; set; } = "";
    [Required, MaxLength(500)] public string Subject { get; set; } = "";
    [Required] public string Description { get; set; } = "";
    public string? Discipline { get; set; }
    public Priority Priority { get; set; } = Priority.Medium;
    public RfiStatus Status { get; set; } = RfiStatus.Open;
    public string? Response { get; set; }
    public Guid RaisedById { get; set; }
    public Guid? AssignedToId { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<RfiDocument> Documents { get; set; } = [];
}

public class RfiDocument
{
    public Guid RfiId { get; set; }
    public Rfi Rfi { get; set; } = null!;
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
}

public class ActionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    [Required, MaxLength(500)] public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Source { get; set; }
    public Priority Priority { get; set; } = Priority.Medium;
    public ActionStatus Status { get; set; } = ActionStatus.Open;
    public DateTime? DueDate { get; set; }
    public DateTime? ClosedAt { get; set; }
    public Guid CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;
    public Guid? AssigneeId { get; set; }
    public User? Assignee { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    // Nullable so anonymous flows (bootstrap-invitation creation
    // from the anonymous org-create endpoint) can record "no actor"
    // honestly. Pre-fix the column was non-nullable and callers
    // wrote Guid.Empty, which violated the FK to Users.Id on SQL
    // Server (in-memory provider ignores FKs, which is why the
    // bug was latent until smoke-tested 2026-04-29).
    public Guid? UserId { get; set; }
    public User? User { get; set; }
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public Guid? DocumentId { get; set; }
    public Document? Document { get; set; }
    [Required] public string Action { get; set; } = "";
    [Required] public string Entity { get; set; } = "";
    [Required] public string EntityId { get; set; } = "";
    public string? Detail { get; set; }
    public string? BeforeValue { get; set; }
    public string? AfterValue { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    [Required] public string Type { get; set; } = "";
    [Required] public string Title { get; set; } = "";
    [Required] public string Body { get; set; } = "";
    public string? Link { get; set; }
    public bool Read { get; set; } = false;
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
/// <summary>
/// A PMBOK-aligned document/template that belongs to a project.
/// Created automatically when a project is provisioned.
/// Stored on disk under {StorageRoot}/{PmbokFolder}/{FileName}
/// </summary>
public class ProjectTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>PMBOK knowledge area folder, e.g. "08_Risk_Management/02_Risk_Register"</summary>
    [Required, MaxLength(200)]
    public string PmbokFolder { get; set; } = "";

    /// <summary>File name on disk, e.g. "Risk_Register_Template.csv"</summary>
    [Required, MaxLength(200)]
    public string FileName { get; set; } = "";

    /// <summary>Human-readable title for the UI</summary>
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";

    /// <summary>Relative storage path from project root</summary>
    [Required, MaxLength(500)]
    public string StorageKey { get; set; } = "";

    /// <summary>File extension (md, csv, docx, pdf)</summary>
    [MaxLength(10)]
    public string FileExtension { get; set; } = "";

    public CdeState CdeState { get; set; } = CdeState.WorkInProgress;
    public SuitabilityCode Suitability { get; set; } = SuitabilityCode.S0;

    [MaxLength(10)]
    public string RevisionCode { get; set; } = "P01";

    public bool IsEdited { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Cost Breakdown Structure (CBS) line for a Project. Hierarchical
/// via self-referencing ParentId — a project's CBS is a tree of these
/// rows. Tenant-scoped indirectly through Project.AppointingPartyId
/// (query filter in CimsDbContext). Per PAFM-SD Appendix F.2 (S1 DoD).
/// </summary>
public class CostBreakdownItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Null for top-level CBS lines.</summary>
    public Guid? ParentId { get; set; }
    public CostBreakdownItem? Parent { get; set; }
    public ICollection<CostBreakdownItem> Children { get; set; } = [];

    /// <summary>WBS-style code, project-unique. Examples: "1", "1.1", "1.2.3".</summary>
    [Required, MaxLength(50)] public string Code { get; set; } = "";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Sort order among siblings of the same Parent.</summary>
    public int SortOrder { get; set; }

    /// <summary>Planned budget at this CBS line (T-S1-04). Currency follows
    /// Project.Currency; precision configured as decimal(18,2) in
    /// CimsDbContext. Null = not yet budgeted.</summary>
    public decimal? Budget { get; set; }

    /// <summary>Scheduled start of work on this line (B-017). Null until
    /// the schedule baseline is set. Together with <see cref="ScheduledEnd"/>
    /// gives the date range used to distribute the line's Budget across
    /// CostPeriods for cashflow / EVM PV.</summary>
    public DateTime? ScheduledStart { get; set; }

    /// <summary>Scheduled end of work on this line (B-017). Null until set.
    /// Service layer enforces ScheduledStart &lt; ScheduledEnd.</summary>
    public DateTime? ScheduledEnd { get; set; }

    /// <summary>Percent complete on this line, stored as a decimal in
    /// [0, 1] (B-017). Null = not yet reported. Drives EV (Earned
    /// Value) at T-S1-07 and the auto-derived valuation at T-S1-09
    /// when wired up. Service layer enforces 0 ≤ value ≤ 1; precision
    /// decimal(5, 4) is enough for 0.0001 granularity.</summary>
    public decimal? PercentComplete { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A payment certificate for a project at a specific CostPeriod
/// (T-S1-09). NEC4 cumulative semantics per ADR-0013 (v1.0 default
/// contract convention). One certificate per period — `Draft` while
/// the assessor composes it, `Issued` when locked as a legal record.
///
/// Stored fields are the *inputs* to the calculation; derived
/// values (gross, retention amount, net, amount due, previously
/// certified) are computed in the response DTO so the math stays
/// in the service layer rather than the database.
///
/// `IncludedVariationsAmount` is null while Draft (a live preview is
/// computed on read) and snapshotted at issue time as the sum of
/// `EstimatedCostImpact` over Approved Variations on the project at
/// that moment. Variations approved after a certificate is issued
/// land in the *next* certificate's snapshot.
/// </summary>
public class PaymentCertificate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid PeriodId { get; set; }
    public CostPeriod Period { get; set; } = null!;

    /// <summary>Project-scoped sequential number, e.g. "PC-0001".</summary>
    [Required, MaxLength(20)] public string CertificateNumber { get; set; } = "";

    public PaymentCertificateState State { get; set; } = PaymentCertificateState.Draft;

    /// <summary>Cumulative Price for Work Done to Date (NEC4 PWDD),
    /// assessor-stated. v1.0: manual entry (no progress signal yet).</summary>
    public decimal CumulativeValuation { get; set; }

    /// <summary>Cumulative on-site materials. NEC4 typically excludes
    /// these from the retention base — see ADR-0013.</summary>
    public decimal CumulativeMaterialsOnSite { get; set; }

    /// <summary>Retention rate as percent (0..100). e.g. 3.00 for 3%.
    /// Lives on the certificate in v1.0; promotion to Project is a
    /// v1.1 candidate (the contract sets retention, not each cert).</summary>
    public decimal RetentionPercent { get; set; }

    /// <summary>Snapshot of approved-variations sum at issue time.
    /// Null while Draft — computed live on read.</summary>
    public decimal? IncludedVariationsAmount { get; set; }

    public Guid? IssuedById { get; set; }
    public User? IssuedBy { get; set; }
    public DateTime? IssuedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A change-order / variation against the project's contracted scope
/// (T-S1-08, F.2 sixth bullet). v1.0 implements the **core 3-state
/// machine** — Raised → Approved or Raised → Rejected — per CR-003.
/// The full PMBOK / NEC4 6-state workflow (assess / instruct / value
/// / agree) is deferred to v1.1 backlog item B-016.
///
/// Approval / rejection records the decision; it does **not**
/// automatically integrate the cost / schedule impact into the
/// project baseline. Manual data entry on the affected CBS lines /
/// commitments is expected. Auto-integration is intentionally out of
/// T-S1-08 scope.
/// </summary>
public class Variation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Project-scoped sequential number, e.g. "VAR-0001".</summary>
    [Required, MaxLength(20)] public string VariationNumber { get; set; } = "";

    [Required, MaxLength(500)] public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Reason { get; set; }

    public VariationState State { get; set; } = VariationState.Raised;

    /// <summary>Positive = adds cost; negative = omission saving.
    /// Currency follows Project.Currency. Optional — not all
    /// variations have a measurable cost impact at raise time.</summary>
    public decimal? EstimatedCostImpact { get; set; }

    /// <summary>Positive = extension of time; negative = acceleration.
    /// Optional.</summary>
    public int? EstimatedTimeImpactDays { get; set; }

    /// <summary>Optional link to a specific CBS line if the variation
    /// targets one. Project-wide variations leave this null.</summary>
    public Guid? CostBreakdownItemId { get; set; }
    public CostBreakdownItem? CostBreakdownItem { get; set; }

    public Guid RaisedById { get; set; }
    public User RaisedBy { get; set; } = null!;

    public Guid? DecidedById { get; set; }
    public User? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A reporting / accounting period for a project (T-S1-06). Typically
/// monthly but the entity does not enforce a calendar — pick whatever
/// `[StartDate, EndDate]` window the project's commercial cycle uses.
/// Once closed, no more ActualCost rows can be recorded against it
/// (the close is the integrity boundary; corrective actuals go to a
/// later open period). Re-open is intentionally not supported in v1.0.
/// </summary>
public class CostPeriod
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    [Required, MaxLength(50)] public string Label { get; set; } = "";
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public bool IsClosed { get; set; }
    public DateTime? ClosedAt { get; set; }
    public Guid? ClosedById { get; set; }

    /// <summary>Planned cashflow for this period — manual baseline entry
    /// (T-S1-11). Decimal(18,2). Null = no baseline set yet.</summary>
    public decimal? PlannedCashflow { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ActualCost> Actuals { get; set; } = [];
}

/// <summary>
/// An actual cost recorded against a CBS line in a specific
/// CostPeriod (T-S1-06). Currency follows Project.Currency. Once a
/// CostPeriod is closed, no more ActualCost rows targeting it can be
/// inserted (service-layer guard, not a DB constraint). Reference is
/// optional and typically holds an invoice number or cost-system
/// reference for traceability.
/// </summary>
public class ActualCost
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid CostBreakdownItemId { get; set; }
    public CostBreakdownItem CostBreakdownItem { get; set; } = null!;

    public Guid PeriodId { get; set; }
    public CostPeriod Period { get; set; } = null!;

    public decimal Amount { get; set; }

    [MaxLength(100)] public string? Reference { get; set; }
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A monetary commitment (PO or Subcontract) issued against a CBS line
/// (T-S1-05). Tenant-scoped indirectly through Project.AppointingPartyId
/// like every other Cost-domain entity. Amount uses decimal(18,2);
/// currency is implied by Project.Currency. v1.0 has no
/// status/cancellation lifecycle — that is a v1.1 hardening item if
/// real-world usage demands it.
/// </summary>
public class Commitment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid CostBreakdownItemId { get; set; }
    public CostBreakdownItem CostBreakdownItem { get; set; } = null!;

    public CommitmentType Type { get; set; }

    /// <summary>Reference number — PO number, subcontract number etc.</summary>
    [Required, MaxLength(100)] public string Reference { get; set; } = "";

    /// <summary>Supplier (for PO) or subcontractor (for Subcontract).</summary>
    [Required, MaxLength(200)] public string Counterparty { get; set; } = "";

    public decimal Amount { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Single-use token binding a registering user to a specific organisation.
/// Closes SR-S0-01: registration can no longer accept an attacker-supplied
/// OrganisationId. Minted by an OrgAdmin via POST /organisations/{id}/invitations
/// or auto-issued as a bootstrap token by POST /organisations.
/// See ADR-0011.
/// </summary>
public class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The invited organisation. On registration, the created User inherits this.</summary>
    public Guid OrganisationId { get; set; }
    public Organisation Organisation { get; set; } = null!;

    /// <summary>SHA-256 hash of the plaintext token. The plaintext is shown
    /// once at creation time and never persisted.</summary>
    // [JsonIgnore]: the hash is not the plaintext, but exposing it
    // narrows the brute-force search space against the limited-entropy
    // plaintext token alphabet. Same defense-in-depth posture as
    // User.PasswordHash and the matching AuditInterceptor SkippedFieldNames.
    [JsonIgnore]
    [Required, MaxLength(128)]
    public string TokenHash { get; set; } = "";

    /// <summary>Optional email bind — if set, Register must use this email.</summary>
    [MaxLength(200)]
    public string? Email { get; set; }

    /// <summary>If true, the consumer of this invitation is auto-assigned
    /// GlobalRole = OrgAdmin. Used for the first-user bootstrap on a freshly
    /// created organisation. OrgAdmin-minted invitations are not bootstraps.</summary>
    public bool IsBootstrap { get; set; } = false;

    public DateTime ExpiresAt { get; set; }

    public DateTime? ConsumedAt { get; set; }
    public Guid? ConsumedByUserId { get; set; }
    public User? ConsumedByUser { get; set; }

    /// <summary>Null for bootstrap invitations (no user exists yet).</summary>
    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Risk Breakdown Structure (RBS) category for a Project. Hierarchical
/// via self-referencing ParentId — a project's RBS is a tree of these
/// rows. Tenant-scoped indirectly through Project.AppointingPartyId
/// (query filter in CimsDbContext). Per PAFM-SD Appendix F.3 (S2 DoD)
/// — the "Risk register with RBS taxonomy" bullet.
///
/// v1.0: per-project ownership (each project has its own RBS tree),
/// matching the CostBreakdownItem pattern from S1. v1.1 candidate:
/// org-level RBS templates that seed new projects (similar shape to
/// the project-template feature already present for documents).
/// </summary>
public class RiskCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Null for top-level RBS categories.</summary>
    public Guid? ParentId { get; set; }
    public RiskCategory? Parent { get; set; }
    public ICollection<RiskCategory> Children { get; set; } = [];

    /// <summary>WBS-style code, project-unique. Examples: "1", "1.1", "1.2.3".
    /// Common RBS top-level codes: 1=Technical, 2=External, 3=Organisational,
    /// 4=Project Management — but the convention is per-project, not enforced.</summary>
    [Required, MaxLength(50)] public string Code { get; set; } = "";
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Sort order among siblings of the same Parent.</summary>
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A project risk per PAFM-SD Appendix F.3 / PMBOK 5 Risk knowledge area
/// (T-S2-03). Tenant-scoped indirectly through Project.AppointingPartyId.
///
/// v1.0 ships the identity, classification, qualitative scoring (P×I on
/// the 5×5 matrix per F.3), response strategy, and contingency amount.
/// 3-point estimates (BestCase / MostLikely / WorstCase + Distribution
/// choice) for quantitative assessment land in T-S2-07 via a separate
/// migration. Qualitative-assessment metadata (notes, assessor, date)
/// lands in T-S2-06. The two are deferred to keep T-S2-03 focused on
/// the identity and base scoring shape — same incremental pattern S1
/// used for CostBreakdownItem (T-S1-02) → Budget (T-S1-04) → Schedule
/// (B-017).
/// </summary>
public class Risk
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Optional RBS classification. Null = unclassified
    /// (acceptable while a risk is initially registered).</summary>
    public Guid? CategoryId { get; set; }
    public RiskCategory? Category { get; set; }

    [Required, MaxLength(200)] public string Title { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>1-5 on the standard 5×5 matrix. Service layer enforces
    /// the range at write time.</summary>
    public int Probability { get; set; }

    /// <summary>1-5 on the standard 5×5 matrix. Service layer enforces
    /// the range at write time.</summary>
    public int Impact { get; set; }

    /// <summary>Persisted P×I — denormalised for fast queries / heat-map
    /// rendering. Recomputed by the service on every Probability or
    /// Impact change. Range 1..25. Numeric only — threshold mapping
    /// (low / medium / high) belongs to S14 Admin Console as a
    /// per-tenant setting per the S2 kickoff Top-3-risks mitigation.</summary>
    public int Score { get; set; }

    public RiskStatus Status { get; set; } = RiskStatus.Identified;

    /// <summary>The responsible owner. Null at registration; set when
    /// the risk is assessed and assigned (T-S2-04 service path).</summary>
    public Guid? OwnerId { get; set; }
    public User? Owner { get; set; }

    /// <summary>F.3 negative-risk response. Null while the risk is in
    /// the Identified / Assessed phase pre-decision.</summary>
    public ResponseStrategy? ResponseStrategy { get; set; }

    /// <summary>Free-text plan describing how the chosen strategy is
    /// being executed. Set when ResponseStrategy is set.</summary>
    public string? ResponsePlan { get; set; }

    /// <summary>Allocated contingency for this specific risk (if the
    /// response is Accept or Mitigate with reserved budget). v1.0:
    /// drawdown amounts manually tracked per-risk via T-S2-09's
    /// RiskDrawdown entity. Cross-module link to specific Commitments /
    /// Actuals deferred to v1.1 — see B-030.</summary>
    public decimal? ContingencyAmount { get; set; }

    /// <summary>T-S2-06 qualitative assessment — free-text rationale
    /// for the Probability/Impact scores. Null until first assessment.
    /// Subsequent re-assessments overwrite (history is captured
    /// passively via the AuditInterceptor's per-row before/after
    /// JSON; an explicit assessment-history entity is a v1.1
    /// candidate if real workflows need point-in-time queries).</summary>
    public string? QualitativeNotes { get; set; }

    /// <summary>UTC timestamp of the most recent qualitative
    /// assessment. Bumped by RecordQualitativeAssessmentAsync.</summary>
    public DateTime? AssessedAt { get; set; }

    /// <summary>The assessor — typically the Owner or a delegated
    /// risk analyst. Null until first assessment.</summary>
    public Guid? AssessedById { get; set; }
    public User? AssessedBy { get; set; }

    /// <summary>T-S2-07 quantitative assessment — 3-point estimate.
    /// Best-case (lowest realistic cost) of the risk's monetary impact.
    /// Currency follows Project.Currency. Either all three (Best /
    /// MostLikely / Worst) are set together with a Distribution, or
    /// all four are null. RisksService validates this invariant plus
    /// Best ≤ MostLikely ≤ Worst at write time.</summary>
    public decimal? BestCase { get; set; }

    /// <summary>3-point estimate — expected cost.</summary>
    public decimal? MostLikely { get; set; }

    /// <summary>3-point estimate — catastrophic cost (worst realistic).</summary>
    public decimal? WorstCase { get; set; }

    /// <summary>Distribution shape used for Monte Carlo sampling
    /// (T-S2-08). Triangular is the v1.0 default. Per
    /// <see cref="DistributionShape"/> notes, the sampler itself
    /// lands in T-S2-08; this column captures the analyst's choice
    /// at quantitative-assessment time.</summary>
    public DistributionShape? Distribution { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RiskDrawdown> Drawdowns { get; set; } = [];
}

/// <summary>
/// One drawdown event against a Risk's allocated contingency
/// (T-S2-09, PAFM-SD F.3 fifth bullet — "Contingency drawdown
/// tracking"). v1.0 records amount + date + free-text reference;
/// cross-module link to specific Commitments / ActualCosts is
/// deferred to v1.1 (B-030 per CR-004) so the v1.0 audit trail
/// stays self-contained even before the Cost-domain integration
/// lands. Tenant-scoped indirectly through Risk → Project.
/// </summary>
public class RiskDrawdown
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Denormalised for the tenant query filter — matches
    /// the rest of the cost-domain entities. Always equals
    /// Risk.ProjectId; service enforces.</summary>
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid RiskId { get; set; }
    public Risk Risk { get; set; } = null!;

    /// <summary>Amount drawn down, in Project.Currency. Must be > 0.
    /// Cumulative drawdowns may exceed Risk.ContingencyAmount —
    /// over-runs are tracked honestly rather than blocked, matching
    /// real construction practice where contingency overruns happen
    /// and need to be visible.</summary>
    public decimal Amount { get; set; }

    /// <summary>UTC date the drawdown was incurred / recorded —
    /// distinct from CreatedAt which is row-write time.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>Free-text reference: PO number, invoice number, or
    /// description of the event triggering the drawdown. The richer
    /// "link to specific Commitment / ActualCost row" is the v1.1
    /// B-030 deferral.</summary>
    [MaxLength(200)] public string? Reference { get; set; }

    public string? Note { get; set; }

    /// <summary>Who recorded the drawdown (typically the Risk Owner
    /// or a delegated cost engineer).</summary>
    public Guid RecordedById { get; set; }
    public User RecordedBy { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A project stakeholder (T-S3-02, PAFM-SD F.4 first bullet —
/// "Stakeholder register with power/interest scoring"). Tenant-scoped
/// indirectly through Project.AppointingPartyId.
///
/// Identity fields (Name, Organisation as free text, Role, Email,
/// Phone) describe the person; Power and Impact 1..5 drive the
/// Mendelow Power/Interest grid. EngagementApproach is auto-computed
/// by the service from Power/Interest at 3-as-midpoint unless the
/// caller overrides — same denormalisation pattern as Risk.Score.
/// EngagementNotes is the free-text plan for engagement; together
/// with EngagementApproach this satisfies F.4's second bullet
/// ("Engagement plan per stakeholder") without a separate entity.
/// </summary>
public class Stakeholder
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    [Required, MaxLength(200)] public string Name { get; set; } = "";

    /// <summary>Free-text organisation name (NOT a FK to the Organisation
    /// entity — stakeholders typically belong to external organisations
    /// outside the tenant's own row set, e.g. the local authority,
    /// a neighbour, the principal contractor's client). v1.1 candidate:
    /// optional FK to Organisation when the stakeholder is from a
    /// known tenant, with this field as a fallback.</summary>
    [MaxLength(200)] public string? Organisation { get; set; }

    [MaxLength(100)] public string? Role { get; set; }

    [MaxLength(200)] public string? Email { get; set; }
    [MaxLength(50)]  public string? Phone { get; set; }

    /// <summary>1-5 Mendelow power axis. Service-enforced range.</summary>
    public int Power { get; set; }

    /// <summary>1-5 Mendelow interest axis. Service-enforced range.</summary>
    public int Interest { get; set; }

    /// <summary>Persisted P×I — denormalised for fast queries / heat-map
    /// rendering. Recomputed by the service on every Power or Interest
    /// change. Same pattern as Risk.Score.</summary>
    public int Score { get; set; }

    /// <summary>Mendelow quadrant — auto-computed from Power/Interest at
    /// 3-as-midpoint by the service unless the caller overrides.
    /// Per-tenant threshold override is S14 Admin Console territory.</summary>
    public EngagementApproach EngagementApproach { get; set; } = EngagementApproach.Monitor;

    /// <summary>Free-text engagement plan — what cadence, what messages,
    /// who owns the relationship.</summary>
    public string? EngagementNotes { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<EngagementLog> Engagements { get; set; } = [];
}

/// <summary>
/// One recorded interaction with a stakeholder — meeting, call, email,
/// letter etc. (T-S3-06, PAFM-SD F.4 third bullet — "Engagement log").
/// Tenant-scoped indirectly through Project.AppointingPartyId. Listing
/// is bounded to the most-recent 200 entries per stakeholder per the
/// S2 kickoff Top-3-risks throughput mitigation; full pagination is a
/// v1.1 candidate.
/// </summary>
public class EngagementLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Denormalised for the tenant query filter — same
    /// pattern as RiskDrawdown.ProjectId. Always equals
    /// Stakeholder.ProjectId; service enforces.</summary>
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid StakeholderId { get; set; }
    public Stakeholder Stakeholder { get; set; } = null!;

    public EngagementType Type { get; set; }

    /// <summary>UTC date / time of the interaction. Distinct from
    /// CreatedAt (row-write time) — meetings can be logged days
    /// after they happened.</summary>
    public DateTime OccurredAt { get; set; }

    [Required] public string Summary { get; set; } = "";

    /// <summary>Optional follow-up actions agreed in the interaction.
    /// Free-text in v1.0; a v1.1 candidate is to link Actions to the
    /// existing ActionItem entity (S0).</summary>
    public string? ActionsAgreed { get; set; }

    public Guid RecordedById { get; set; }
    public User RecordedBy { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One row in the project-level communications matrix (T-S3-07,
/// PAFM-SD F.4 fourth bullet — "Communications matrix (what, who,
/// when, how)"). The four DoD axes map to ItemType / Audience /
/// Frequency / Channel respectively. Tenant-scoped through
/// Project.AppointingPartyId; soft-deleted via IsActive to preserve
/// audit history when a planned communication is retired.
/// </summary>
public class CommunicationItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>The "what" — short label e.g. "Monthly project report",
    /// "Daily diary", "Variation notice". Free text in v1.0; an
    /// org-level template seed is a v1.1 candidate per the S3 kickoff
    /// Top-3 risks list.</summary>
    [Required, MaxLength(200)] public string ItemType { get; set; } = "";

    /// <summary>The "who" — comma-separated roles or stakeholder names
    /// in v1.0. v1.1 candidate: link to Stakeholder rows directly per
    /// the S3 kickoff decisions section.</summary>
    [Required, MaxLength(500)] public string Audience { get; set; } = "";

    /// <summary>The "when" — fixed cadence enum.</summary>
    public CommunicationFrequency Frequency { get; set; }

    /// <summary>The "how" — channel / medium.</summary>
    public CommunicationChannel Channel { get; set; }

    /// <summary>Caller-nominated owner of the communication. Service
    /// validates the user belongs to the project (membership check).</summary>
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One scheduled activity in a project's CPM schedule (T-S4-02,
/// PAFM-SD F.5 first bullet). Tenant-scoped through
/// Project.AppointingPartyId. CPM-computed fields (Early/Late
/// Start/Finish, Total/Free Float, IsCritical) are nullable until
/// the first ScheduleService.RecomputeAsync run; the service
/// populates them by calling Core/Cpm.cs and persisting the result.
/// Soft-deleted via IsActive to preserve dependency-graph history.
/// </summary>
public class Activity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Caller-supplied unique-within-project code, e.g.
    /// "A1010", "MIL-100". Service enforces uniqueness on
    /// (ProjectId, Code).</summary>
    [Required, MaxLength(50)] public string Code { get; set; } = "";

    [Required, MaxLength(300)] public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Planned duration in DurationUnit. Decimal so half-day
    /// activities are expressible without an Hour unit. v1.0 only
    /// supports Day; Hour reserved for v1.1.</summary>
    [Column(TypeName = "decimal(9,2)")]
    public decimal Duration { get; set; }
    public DurationUnit DurationUnit { get; set; } = DurationUnit.Day;

    // ── CPM-computed fields (populated by Core/Cpm.cs run) ─────────
    public DateTime? EarlyStart  { get; set; }
    public DateTime? EarlyFinish { get; set; }
    public DateTime? LateStart   { get; set; }
    public DateTime? LateFinish  { get; set; }
    [Column(TypeName = "decimal(9,2)")]
    public decimal? TotalFloat   { get; set; }
    [Column(TypeName = "decimal(9,2)")]
    public decimal? FreeFloat    { get; set; }
    public bool IsCritical { get; set; }

    // ── Scheduled (committed plan) and Actual (recorded) dates ─────
    public DateTime? ScheduledStart  { get; set; }
    public DateTime? ScheduledFinish { get; set; }
    public DateTime? ActualStart     { get; set; }
    public DateTime? ActualFinish    { get; set; }

    // ── Constraint propagation (MS-Project-style) ───────────────────
    public ConstraintType ConstraintType { get; set; } = ConstraintType.ASAP;
    public DateTime? ConstraintDate { get; set; }

    /// <summary>Progress fraction in [0, 1]. Drives EV (post-S1)
    /// and reporting. Service validates range.</summary>
    [Column(TypeName = "decimal(5,4)")]
    public decimal PercentComplete { get; set; }

    /// <summary>Optional caller-supplied owner / responsible.
    /// Service validates project membership.</summary>
    public Guid? AssigneeId { get; set; }
    public User? Assignee { get; set; }

    [MaxLength(100)] public string? Discipline { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Dependency> PredecessorLinks { get; set; } = [];
    public ICollection<Dependency> SuccessorLinks   { get; set; } = [];
}

/// <summary>
/// Inter-activity scheduling dependency (T-S4-03). Predecessor and
/// Successor are both Activity rows in the *same* project; service
/// enforces same-project at write time. Lag is in days (decimal,
/// can be negative for lead). Tenant-scoped via Project.
/// </summary>
public class Dependency
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Denormalised for the tenant query filter — same
    /// pattern as RiskDrawdown.ProjectId. Always equals
    /// Predecessor.ProjectId == Successor.ProjectId; service
    /// enforces.</summary>
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid PredecessorId { get; set; }
    public Activity Predecessor { get; set; } = null!;

    public Guid SuccessorId { get; set; }
    public Activity Successor { get; set; } = null!;

    public DependencyType Type { get; set; } = DependencyType.FS;

    /// <summary>Lag in days. Negative = lead. Service validates a
    /// reasonable range (-365..365) to catch typos.</summary>
    [Column(TypeName = "decimal(9,2)")]
    public decimal Lag { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A frozen snapshot of a project's schedule at a point in time
/// (T-S4-06, PAFM-SD F.5 second bullet — "Baselines captured and
/// compared against actual"). Multiple baselines per project are
/// allowed (Original, Revision 1, etc.). The header carries summary
/// metadata; per-activity rows live in
/// <see cref="ScheduleBaselineActivity"/>. Tenant-scoped through
/// Project.AppointingPartyId. Baselines are immutable after capture
/// — there's no Update / Delete; superseded baselines stay visible
/// for audit / reporting.
/// </summary>
public class ScheduleBaseline
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    [Required, MaxLength(200)] public string Label { get; set; } = "";

    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    public Guid CapturedById { get; set; }
    public User CapturedBy { get; set; } = null!;

    /// <summary>Denormalised summary at capture time so list endpoints
    /// don't need to aggregate across the per-activity rows.</summary>
    public int ActivitiesCount { get; set; }

    /// <summary>Project finish (max EarlyFinish across activities) at
    /// the moment the baseline was captured. Null if any activity
    /// hadn't been CPM-recomputed (EarlyFinish missing).</summary>
    public DateTime? ProjectFinishAtBaseline { get; set; }

    public ICollection<ScheduleBaselineActivity> Activities { get; set; } = [];
}

/// <summary>
/// One frozen activity row inside a <see cref="ScheduleBaseline"/>.
/// Code / Name are denormalised so the baseline preserves the
/// activity's identity even if the Activity row's Code is later
/// renamed. Comparison endpoint subtracts current Activity.Early*
/// from these fields to compute start / finish / duration variances.
/// Tenant-scoped via the parent ScheduleBaseline + denormalised
/// ProjectId for query filter consistency.
/// </summary>
public class ScheduleBaselineActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ScheduleBaselineId { get; set; }
    public ScheduleBaseline ScheduleBaseline { get; set; } = null!;

    /// <summary>Denormalised — equals ScheduleBaseline.ProjectId.
    /// Drives the tenant query filter without requiring a join.</summary>
    public Guid ProjectId { get; set; }

    public Guid ActivityId { get; set; }
    public Activity Activity { get; set; } = null!;

    [Required, MaxLength(50)]  public string Code { get; set; } = "";
    [Required, MaxLength(300)] public string Name { get; set; } = "";

    [Column(TypeName = "decimal(9,2)")]
    public decimal Duration { get; set; }

    public DateTime? EarlyStart  { get; set; }
    public DateTime? EarlyFinish { get; set; }
    public bool IsCritical { get; set; }
}

/// <summary>
/// One Lookahead-window entry (T-S4-07, PAFM-SD F.5 third bullet —
/// "Last Planner System boards"). The 6-week-out lookahead is the
/// LPS pull-planning view: each entry pairs an Activity with the
/// week it should become "ready to start" (constraints removed).
/// Tenant-scoped through Project.AppointingPartyId. Soft-deleted via
/// IsActive — lookaheads are transient planning artefacts and the
/// PM/IM workflow drops + re-adds entries as the constraints picture
/// evolves week to week.
/// </summary>
public class LookaheadEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid ActivityId { get; set; }
    public Activity Activity { get; set; } = null!;

    /// <summary>The Monday of the lookahead-week this entry tracks.
    /// Service truncates incoming values to date-only at the
    /// week-start (the controller can be lenient on the input).</summary>
    public DateTime WeekStarting { get; set; }

    /// <summary>"Ready to start" flag — when every prerequisite,
    /// resource, drawing, and approval is in place. Toggled by the
    /// PM during the weekly LPS meeting.</summary>
    public bool ConstraintsRemoved { get; set; }

    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;
}

/// <summary>
/// One Weekly Work Plan (T-S4-07, PAFM-SD F.5 third bullet). Header
/// per project per week. Owns a collection of
/// <see cref="WeeklyTaskCommitment"/> rows — one per Activity the PM
/// commits to that week. PPC (Percent Plan Complete) is computed on
/// read as completed_count / committed_count × 100 — never persisted,
/// so PPC always reflects current commitment state.
/// </summary>
public class WeeklyWorkPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Monday of the week. Unique within project.</summary>
    public DateTime WeekStarting { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;

    public ICollection<WeeklyTaskCommitment> Commitments { get; set; } = [];
}

/// <summary>
/// One Activity-level commitment inside a <see cref="WeeklyWorkPlan"/>
/// (T-S4-07). The PM commits to completing the Activity in the week;
/// at week-end the IM/PM marks Completed = true or supplies a
/// <see cref="LpsReasonForNonCompletion"/> for the failure. Tenant-
/// scoped via the parent WeeklyWorkPlan + denormalised ProjectId.
/// </summary>
public class WeeklyTaskCommitment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WeeklyWorkPlanId { get; set; }
    public WeeklyWorkPlan WeeklyWorkPlan { get; set; } = null!;

    /// <summary>Denormalised — equals WeeklyWorkPlan.ProjectId.
    /// Drives the tenant query filter without a join.</summary>
    public Guid ProjectId { get; set; }

    public Guid ActivityId { get; set; }
    public Activity Activity { get; set; } = null!;

    public bool Committed { get; set; } = true;
    public bool Completed { get; set; }

    /// <summary>Required when Committed = true and Completed = false
    /// at any read time. Service enforces on the mark-completed
    /// transition: caller cannot leave Completed = false without a
    /// Reason once the WeeklyWorkPlan's week has ended.</summary>
    public LpsReasonForNonCompletion? Reason { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One project-level Change Request (T-S5-02, PAFM-SD F.6).
/// 5-state workflow: Raised → Assessed → Approved | Rejected →
/// Implemented → Closed. State-machine enforcement in
/// <see cref="CimsApp.Core.ChangeWorkflow"/>. Multiple change
/// requests per project; project-scoped sequential
/// <see cref="Number"/> ("CR-NNNN") for human-readable reference.
/// Tenant-scoped through Project.AppointingPartyId.
/// </summary>
public class ChangeRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Project-scoped sequential number, e.g. "CR-0001".
    /// Auto-generated by the service on Raise; unique within
    /// project. Distinct from PAFM-SD Ch 17 development-discipline
    /// change records (CR-001..CR-005 in `docs/change-register.md`)
    /// — those are sprint-scope decisions; this is per-project
    /// construction-site change-control. Same naming convention
    /// kept because UK construction usage of "CR-NNNN" is
    /// industry-standard.</summary>
    [Required, MaxLength(20)] public string Number { get; set; } = "";

    [Required, MaxLength(300)] public string Title { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>F.6 second bullet — Scope / Time / Cost / Quality.</summary>
    public ChangeRequestCategory Category { get; set; }

    /// <summary>F.6 third bullet — Building Safety Act 2022 HRB
    /// categorisation. NotApplicable for the majority of projects;
    /// A/B/C tagged manually by the PM for HRB projects per
    /// Building Safety Regulator guidance. Pure tag in v1.0.</summary>
    public BsaHrbCategory BsaCategory { get; set; } = BsaHrbCategory.NotApplicable;

    public ChangeRequestState State { get; set; } = ChangeRequestState.Raised;

    // ── Audit-trail fields per state transition ────────────────────
    public Guid RaisedById { get; set; }
    public User RaisedBy { get; set; } = null!;
    public DateTime RaisedAt { get; set; } = DateTime.UtcNow;

    public Guid? AssessedById { get; set; }
    public User? AssessedBy { get; set; }
    public DateTime? AssessedAt { get; set; }
    public string? AssessmentNote { get; set; }

    public Guid? DecisionById { get; set; }
    public User? DecisionBy { get; set; }
    public DateTime? DecisionAt { get; set; }
    /// <summary>Approval / rejection rationale; required at the
    /// approve / reject transition.</summary>
    public string? DecisionNote { get; set; }

    public DateTime? ImplementedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    // ── Impact summaries (F.6 fifth bullet) ────────────────────────
    [Column(TypeName = "decimal(18,2)")]
    public decimal? EstimatedCostImpact { get; set; }
    public int? EstimatedTimeImpactDays { get; set; }
    /// <summary>Free-text in v1.0; richer per-Activity structural
    /// links → v1.1 / B-035.</summary>
    public string? ProgrammeImpactSummary { get; set; }
    /// <summary>Free-text in v1.0; richer per-CBS-line structural
    /// links → v1.1 / B-035.</summary>
    public string? CostImpactSummary { get; set; }

    /// <summary>Optional FK to the Variation spawned at Approve
    /// time when the caller passes `CreateVariation = true`.</summary>
    public Guid? GeneratedVariationId { get; set; }
    public Variation? GeneratedVariation { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Project-level procurement strategy (T-S6-02, PAFM-SD F.7 first
/// bullet — "Procurement strategy capture"). Single row per project
/// (unique index on ProjectId — service enforces upsert semantics).
/// Captures the procurement Approach + ContractForm + headline
/// estimated value + key dates + package-breakdown notes. Approval
/// (F.7 fourth bullet downstream) is recorded via ApprovedById /
/// ApprovedAt.
/// </summary>
public class ProcurementStrategy
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public ProcurementApproach Approach { get; set; }
    public ContractForm ContractForm { get; set; }

    /// <summary>Headline estimated total project value, in
    /// Project.Currency. Optional at strategy-capture time —
    /// finalised after Tender Packages are built up.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? EstimatedTotalValue { get; set; }

    /// <summary>Free-text key dates summary (e.g. "OJEU notice
    /// 2026-06-01; Tender issue 2026-07-01; Award 2026-09-15").
    /// v1.1 candidate: structured key-dates table (B-NNN — defer
    /// inline at first real customer demand).</summary>
    public string? KeyDates { get; set; }

    /// <summary>Free-text breakdown of how the project will be
    /// packaged for tender (e.g. "Concrete frame; MEP; Fit-out;
    /// Lifts"). Each named package becomes a TenderPackage row.
    /// Free text in v1.0; structural link is the TenderPackage
    /// rows themselves.</summary>
    public string? PackageBreakdownNotes { get; set; }

    public Guid? ApprovedById { get; set; }
    public User? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One tender package on a project (T-S6-03, PAFM-SD F.7 second
/// bullet — "Tender package creation"). Project-scoped sequential
/// `TP-NNNN`. Multiple packages per project (Concrete frame; MEP;
/// Fit-out, etc). 3-state workflow Draft → Issued → Closed in
/// <see cref="CimsApp.Core.TenderPackageWorkflow"/>. Soft-delete
/// via IsActive — Draft packages can be deactivated; Issued
/// packages cannot (real bidders are working on them).
/// </summary>
public class TenderPackage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Project-scoped sequential number, e.g. "TP-0001".
    /// Unique within project; service auto-generates on Create.</summary>
    [Required, MaxLength(20)] public string Number { get; set; } = "";

    [Required, MaxLength(300)] public string Name { get; set; } = "";
    public string? Description { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? EstimatedValue { get; set; }

    public DateTime? IssueDate { get; set; }
    public DateTime? ReturnDate { get; set; }

    public TenderPackageState State { get; set; } = TenderPackageState.Draft;

    public Guid? IssuedById { get; set; }
    public User? IssuedBy { get; set; }
    public DateTime? IssuedAt { get; set; }

    public Guid? ClosedById { get; set; }
    public User? ClosedBy { get; set; }
    public DateTime? ClosedAt { get; set; }

    /// <summary>Optional FK to the winning Tender once Awarded.
    /// Set by the Award workflow (T-S6-06).</summary>
    public Guid? AwardedTenderId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Tender> Tenders { get; set; } = [];
}

/// <summary>
/// One bid submitted against a <see cref="TenderPackage"/> (T-S6-04,
/// PAFM-SD F.7 second bullet — bids received). Recorded by the
/// project admin / PM at receipt; SubmittedAt is set to UtcNow at
/// create. Bidders are identified by free-text BidderName +
/// optional BidderOrganisation; v1.1 candidate is the
/// pre-qualification register at B-054 which would replace the
/// free-text identification with a structural FK to a Bidder
/// entity.
/// State machine: Submitted → Evaluated (T-S6-05 once criteria
/// scored) → Awarded | Rejected (T-S6-06 Award workflow). Submitted
/// → Withdrawn is the bidder-pulls-out branch, allowed only before
/// evaluation / award (Submitted state only). Awarded / Rejected /
/// Withdrawn are terminal.
/// </summary>
public class Tender
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Denormalised for the tenant query filter — same
    /// pattern as Dependency.ProjectId. Always equals
    /// TenderPackage.ProjectId; service enforces.</summary>
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid TenderPackageId { get; set; }
    public TenderPackage TenderPackage { get; set; } = null!;

    [Required, MaxLength(200)] public string BidderName { get; set; } = "";
    [MaxLength(200)] public string? BidderOrganisation { get; set; }
    [MaxLength(200)] public string? ContactEmail { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal BidAmount { get; set; }

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    public TenderState State { get; set; } = TenderState.Submitted;

    /// <summary>Reason / rationale recorded at the terminal-state
    /// transition (Withdrawn / Rejected / Awarded). Single field
    /// covers all three since at most one transition is reached.</summary>
    public string? StateNote { get; set; }

    /// <summary>Set by the T-S6-06 Award workflow once a winner is
    /// chosen and a Contract spawned.</summary>
    public Guid? GeneratedContractId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<EvaluationScore> Scores { get; set; } = [];
}

/// <summary>
/// One evaluation criterion attached to a <see cref="TenderPackage"/>
/// (T-S6-05, PAFM-SD F.7 third bullet — "price and quality
/// weighted"). Per-package weights must sum to 1.0 (epsilon
/// 0.0001) for the matrix to be valid; the service-layer
/// validation runs at GetMatrixAsync time so individual weight
/// edits during Draft don't reject prematurely. Add / Remove
/// criteria allowed only when the parent TenderPackage is in
/// Draft state — once Issued the criteria are frozen.
/// </summary>
public class EvaluationCriterion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Denormalised — equals TenderPackage.ProjectId.
    /// Drives the tenant query filter without a join.</summary>
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid TenderPackageId { get; set; }
    public TenderPackage TenderPackage { get; set; } = null!;

    [Required, MaxLength(200)] public string Name { get; set; } = "";

    public EvaluationCriterionType Type { get; set; }

    /// <summary>Weight in [0, 1]. The matrix is valid iff Σ
    /// weights ≈ 1.0 across the package. Stored as decimal(5, 4)
    /// so weight changes round-trip without floating-point drift.</summary>
    [Column(TypeName = "decimal(5,4)")]
    public decimal Weight { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One score recorded for a specific (Tender, EvaluationCriterion)
/// pair (T-S6-05). Score in [0, 100]. Optional Notes carries the
/// evaluator's rationale. Tenant-scoped via the parent Tender.
/// Service enforces uniqueness on (TenderId, CriterionId) — one
/// score per pair; re-scoring updates the row in-place.
/// </summary>
public class EvaluationScore
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Denormalised — equals Tender.ProjectId.</summary>
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid TenderId { get; set; }
    public Tender Tender { get; set; } = null!;

    public Guid CriterionId { get; set; }
    public EvaluationCriterion Criterion { get; set; } = null!;

    [Column(TypeName = "decimal(6,2)")]
    public decimal Score { get; set; }

    public string? Notes { get; set; }

    public Guid ScoredById { get; set; }
    public User ScoredBy { get; set; } = null!;
    public DateTime ScoredAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One contract awarded against a winning <see cref="Tender"/>
/// (T-S6-06, PAFM-SD F.7 fourth bullet — "Award and contract
/// record"). Spawned atomically by
/// <see cref="CimsApp.Services.TenderPackagesService"/>'s
/// AwardAsync method (mirrors the S5 Approve→Variation pattern).
/// Project-scoped sequential `CON-NNNN`. ContractValue +
/// ContractorName + ContractorOrganisation are copied from the
/// winning Tender at award time so the contract record is stable
/// even if the Tender row is later modified.
/// </summary>
public class Contract
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Project-scoped sequential number, e.g. "CON-0001".
    /// Unique within project; service auto-generates at Award.</summary>
    [Required, MaxLength(20)] public string Number { get; set; } = "";

    public Guid TenderPackageId { get; set; }
    public TenderPackage TenderPackage { get; set; } = null!;

    public Guid AwardedTenderId { get; set; }
    public Tender AwardedTender { get; set; } = null!;

    /// <summary>Copied from <see cref="Tender.BidderName"/> at
    /// award time; persisted distinctly so the contract record
    /// remains stable.</summary>
    [Required, MaxLength(200)] public string ContractorName { get; set; } = "";
    [MaxLength(200)] public string? ContractorOrganisation { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal ContractValue { get; set; }

    /// <summary>Form of contract under which the award lands. v1.0
    /// defaults to <see cref="CimsApp.Models.ContractForm.Other"/>
    /// if not supplied at award time and no
    /// <see cref="ProcurementStrategy"/> exists; takes the
    /// strategy's <see cref="ProcurementStrategy.ContractForm"/>
    /// otherwise. Caller can override at award time.</summary>
    public ContractForm ContractForm { get; set; } = ContractForm.Other;

    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    public ContractState State { get; set; } = ContractState.Active;

    /// <summary>Award rationale captured at AwardAsync time.</summary>
    public string? AwardNote { get; set; }

    public DateTime AwardedAt { get; set; } = DateTime.UtcNow;
    public Guid AwardedById { get; set; }
    public User AwardedBy { get; set; } = null!;

    public DateTime? ClosedAt { get; set; }
    public Guid? ClosedById { get; set; }
    public User? ClosedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<EarlyWarning> EarlyWarnings { get; set; } = [];
}

/// <summary>
/// Per-contract early-warning notice (T-S6-07, PAFM-SD F.7 fifth
/// bullet — NEC4 clause 15 "Early Warning Notice"). The contractor
/// or PM raises an EW when an event becomes likely that could
/// increase cost / delay completion / impair performance. Linear
/// 3-state workflow Raised → UnderReview → Closed. Tenant-scoped
/// indirectly through Contract.Project.
/// </summary>
public class EarlyWarning
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Denormalised — equals Contract.ProjectId. Drives
    /// the tenant query filter without a join.</summary>
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid ContractId { get; set; }
    public Contract Contract { get; set; } = null!;

    [Required, MaxLength(300)] public string Title { get; set; } = "";
    public string? Description { get; set; }

    public EarlyWarningState State { get; set; } = EarlyWarningState.Raised;

    public Guid RaisedById { get; set; }
    public User RaisedBy { get; set; } = null!;
    public DateTime RaisedAt { get; set; } = DateTime.UtcNow;

    public Guid? ReviewedById { get; set; }
    public User? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    /// <summary>Required when transitioning to UnderReview — the
    /// reviewer's analysis is the whole point of the review step.</summary>
    public string? ResponseNote { get; set; }

    public Guid? ClosedById { get; set; }
    public User? ClosedBy { get; set; }
    public DateTime? ClosedAt { get; set; }
    /// <summary>Optional rationale at close-out.</summary>
    public string? ClosureNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Per-contract compensation event (T-S6-08, PAFM-SD F.7 fifth
/// bullet — NEC4 clause 60.1 "Compensation Events"). Formal
/// notice with cost / time impact. 5-state workflow Notified →
/// Quoted → Accepted | Rejected → Implemented (with the
/// Notified → Rejected branch for clause 61.4 "PM notifies it is
/// not a CE"). State-machine enforcement in
/// <see cref="CimsApp.Core.CompensationEventWorkflow"/>.
/// Project-scoped sequential `CE-NNNN`. Tenant-scoped indirectly
/// through Contract.Project.
/// </summary>
public class CompensationEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Denormalised — equals Contract.ProjectId.</summary>
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid ContractId { get; set; }
    public Contract Contract { get; set; } = null!;

    /// <summary>Project-scoped sequential number, e.g. "CE-0001".
    /// Unique within project; service auto-generates on Notify.</summary>
    [Required, MaxLength(20)] public string Number { get; set; } = "";

    [Required, MaxLength(300)] public string Title { get; set; } = "";
    public string? Description { get; set; }

    public CompensationEventState State { get; set; } = CompensationEventState.Notified;

    public Guid NotifiedById { get; set; }
    public User NotifiedBy { get; set; } = null!;
    public DateTime NotifiedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Quotation cost impact (decimal(18,2) nullable).
    /// Recorded at Quote transition. Always null at Notified state.
    /// Risk-allowance / disallowance rules deferred to v1.1 / B-050.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? EstimatedCostImpact { get; set; }
    public int? EstimatedTimeImpactDays { get; set; }

    public Guid? QuotedById { get; set; }
    public User? QuotedBy { get; set; }
    public DateTime? QuotedAt { get; set; }
    /// <summary>Quotation rationale captured at Quote.</summary>
    public string? QuotationNote { get; set; }

    public Guid? DecisionById { get; set; }
    public User? DecisionBy { get; set; }
    public DateTime? DecisionAt { get; set; }
    /// <summary>Approval / rejection rationale; required at the
    /// accept / reject transition.</summary>
    public string? DecisionNote { get; set; }

    public DateTime? ImplementedAt { get; set; }
    public Guid? ImplementedById { get; set; }
    public User? ImplementedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Saved custom-report definition (T-S7-05, PAFM-SD F.8 fourth
/// bullet — "Basic custom report builder"). Per-project saved
/// queries: pick an EntityType from the allow-list, define a
/// JSON filter, choose JSON columns to project. v1.0 ships
/// pure-equality filtering against a strict per-entity field
/// allow-list — richer operators (gt / lt / ne / contains) are
/// v1.1 / B-060. Cross-entity joins → v1.1 / B-056. Scheduled
/// runs + email → v1.1 / B-057. Export formats → v1.1 / B-058.
/// Soft-deleted via IsActive to preserve history.
/// </summary>
public class CustomReportDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    [Required, MaxLength(200)] public string Name { get; set; } = "";

    public CustomReportEntityType EntityType { get; set; }

    /// <summary>JSON object of field-equality clauses, e.g.
    /// `{"Status":"Open","Priority":"High"}`. Service validates
    /// every key against the per-entity allow-list at write time.
    /// Empty object `{}` = no filter (returns all rows for project).</summary>
    [Required] public string FilterJson { get; set; } = "{}";

    /// <summary>JSON array of field names to project, e.g.
    /// `["RfiNumber","Subject","Status"]`. Strict allow-list per
    /// EntityType. Empty array `[]` = whole entity (allow-list).</summary>
    [Required] public string ColumnsJson { get; set; } = "[]";

    public Guid CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One line in a project's Master Information Delivery Plan (T-S9-05,
/// PAFM-SD F.9 second bullet — "MIDP creation"; ISO 19650-2 §5.4).
/// One MidpEntry per planned information delivery (drawing, model,
/// specification, report). v1.0 ships the simple delivery-list shape;
/// the full ISO 19650-2 information-requirements model (AIR / OIR /
/// EIR / PIR with bidirectional gate dependencies) is a v1.1 concern
/// per the S9 kickoff scope-cut. DocTypeFilter is optional and
/// references an `Iso19650Codes.DocumentTypes` code; DocumentId is set
/// when the planned delivery has been satisfied by a real Document
/// row. Tenant-scoped through Project.AppointingPartyId.
/// </summary>
public class MidpEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    [Required, MaxLength(300)] public string Title { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Optional ISO 19650-2 Annex A type-code filter (e.g. "DR",
    /// "M3"). Service validates against `Iso19650Codes.TypeCodeSet` at
    /// write time. Null = any type acceptable for the delivery.</summary>
    [MaxLength(4)] public string? DocTypeFilter { get; set; }

    public DateTime DueDate { get; set; }

    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    /// <summary>Optional FK to the Document that satisfied this planned
    /// delivery. Null = not yet delivered. Service-layer guard ensures
    /// the document is in the same project.</summary>
    public Guid? DocumentId { get; set; }
    public Document? Document { get; set; }

    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One line in a team's Task Information Delivery Plan (T-S9-06,
/// PAFM-SD F.9 third bullet — "TIDP per team"; ISO 19650-2 §5.4.1).
/// FK to MidpEntry — TIDP is the team's slice of the master plan,
/// not a parallel delivery list. v1.0 stores the team as a free-text
/// `TeamName` string; a structured Team entity (members + lead +
/// deliverable counts) is v1.1 / B-069. Sign-off captures the per-team
/// acceptance: which user, when, and the rationale. Tenant-scoped
/// through Project.AppointingPartyId.
/// </summary>
public class TidpEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid MidpEntryId { get; set; }
    public MidpEntry MidpEntry { get; set; } = null!;

    /// <summary>Free-text team identifier in v1.0 (e.g. "Architecture",
    /// "MEP Coordination"). Structured Team rows → B-069.</summary>
    [Required, MaxLength(100)] public string TeamName { get; set; } = "";

    /// <summary>Team-level due date — may be earlier than the parent
    /// MidpEntry's due date when the team needs lead time before
    /// the master plan's deadline.</summary>
    public DateTime DueDate { get; set; }

    public bool IsSignedOff { get; set; }
    public Guid? SignedOffById { get; set; }
    public User? SignedOffBy { get; set; }
    public DateTime? SignedOffAt { get; set; }
    public string? SignOffNote { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

