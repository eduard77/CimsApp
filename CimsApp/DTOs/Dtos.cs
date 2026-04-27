using CimsApp.Models;

namespace CimsApp.DTOs;

public record RegisterRequest(string Email, string Password, string FirstName, string LastName, string? JobTitle, string InvitationToken);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record AuthResponse(string AccessToken, string RefreshToken, UserSummaryDto User);
public record UserSummaryDto(Guid Id, string Email, string FirstName, string LastName, string? JobTitle, OrgSummaryDto Organisation);
public record OrgSummaryDto(Guid Id, string Name, string Code);
public record CreateOrgRequest(string Name, string Code, string? Country);
public record CreateInvitationRequest(string? Email = null, int? ExpiresInDays = null);
public record InvitationDto(Guid Id, string Token, DateTime ExpiresAt, bool IsBootstrap, string? Email);
public record CreateProjectRequest(string Name, string Code, string? Description, Guid AppointingPartyId, DateTime? StartDate, DateTime? EndDate, string? Location, string? Country, string Currency, decimal? BudgetValue, string? Sector, string? Sponsor, string? EirRef);
public record UpdateProjectRequest(string? Name, string? Description, ProjectStatus? Status, DateTime? StartDate, DateTime? EndDate, string? Location, decimal? BudgetValue, string? Sponsor, string? EirRef);
public record AddMemberRequest(Guid UserId, UserRole Role);
public record CreateContainerRequest(string Name, string Originator, string? Volume, string? Level, string Type, string? Discipline, string? Description);
public record CreateDocumentRequest(string ProjectCode, string Originator, string? Volume, string? Level, string DocType, string? Role, int Number, string Title, string? Description, DocumentType? Type, Guid? ContainerId, string[]? Tags);
public record UploadRevisionRequest(string Revision, SuitabilityCode? Suitability, string? Description, string FileName, long FileSize, string MimeType, string StorageKey, string Checksum);
public record TransitionRequest(CdeState ToState, SuitabilityCode? Suitability);
public record CreateRfiRequest(string Subject, string Description, string? Discipline, Priority Priority, Guid? AssignedToId, DateTime? DueDate);
public record RespondRfiRequest(string Response, RfiStatus Status);
public record CreateActionRequest(string Title, string? Description, string? Source, Priority Priority, Guid? AssigneeId, DateTime? DueDate);
public record UpdateActionRequest(string? Title, string? Description, Priority? Priority, ActionStatus? Status, Guid? AssigneeId, DateTime? DueDate);
public record SetLineBudgetRequest(decimal? Budget);
public record CreateCommitmentRequest(Guid CostBreakdownItemId, CommitmentType Type, string Reference, string Counterparty, decimal Amount, string? Description);
// Variance is Budget - Committed (T-S1-05 semantic, preserved through
// T-S1-06). Actual is the sum of all ActualCost rows on the line; the
// consumer derives Budget - Actual or Committed - Actual as needed.
public record CbsLineRollupDto(Guid ItemId, string Code, string Name, Guid? ParentId, decimal? Budget, decimal Committed, decimal Actual, decimal? Variance);
// PlannedCashflow optional at create time — the baseline is often
// set later as the QS works through the cashflow plan. T-S1-11.
public record CreatePeriodRequest(string Label, DateTime StartDate, DateTime EndDate, decimal? PlannedCashflow = null);
public record RecordActualRequest(Guid CostBreakdownItemId, Guid PeriodId, decimal Amount, string? Reference, string? Description);
public record SetPeriodBaselineRequest(decimal? PlannedCashflow);
// Cashflow S-curve (T-S1-11). Project-level only in v1.0; per-CBS-line
// breakdown deferred. Forecast formula: actuals up to and including the
// latest period with any actuals, then baseline-projected from there.
public record CashflowPeriodPoint(
    Guid PeriodId, string Label, DateTime StartDate, DateTime EndDate, bool IsClosed,
    decimal? BaselinePlanned, decimal CumulativeBaseline,
    decimal Actual, decimal CumulativeActual,
    decimal? Forecast);
public record CashflowDto(string ProjectCurrency, List<CashflowPeriodPoint> Points);
public record RaiseVariationRequest(string Title, string? Description, string? Reason, decimal? EstimatedCostImpact, int? EstimatedTimeImpactDays, Guid? CostBreakdownItemId);
public record VariationDecisionRequest(string? DecisionNote);
// T-S1-09. CumulativeValuation / CumulativeMaterialsOnSite are PWDD-style:
// the assessor states the running total each period, not the increment.
// RetentionPercent is 0..100 (3.00 = 3%). NEC4 default per ADR-0013.
public record CreatePaymentCertificateDraftRequest(Guid PeriodId, decimal CumulativeValuation, decimal CumulativeMaterialsOnSite, decimal RetentionPercent);
public record UpdatePaymentCertificateDraftRequest(decimal CumulativeValuation, decimal CumulativeMaterialsOnSite, decimal RetentionPercent);
// Computed view of a payment certificate. Stored fields are the inputs;
// derived fields (gross, retention amount, net, amount due, previously
// certified) are computed in PaymentCertificatesService.GetAsync per
// the ADR-0013 NEC4 formula.
public record PaymentCertificateDto(
    Guid Id, Guid ProjectId, Guid PeriodId,
    string CertificateNumber, PaymentCertificateState State,
    decimal CumulativeValuation, decimal CumulativeMaterialsOnSite,
    decimal RetentionPercent, decimal IncludedVariationsAmount,
    decimal CumulativeGross, decimal RetentionAmount,
    decimal CumulativeNet, decimal PreviouslyCertified, decimal AmountDue,
    DateTime? IssuedAt);
