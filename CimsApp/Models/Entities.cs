using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
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
/// Budget at line level lands in T-S1-04 (separate property/entity);
/// this row carries identity and structure only.
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

    public bool IsActive { get; set; } = true;
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

