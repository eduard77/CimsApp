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
public record SetLineScheduleRequest(DateTime? ScheduledStart, DateTime? ScheduledEnd);
public record SetLineProgressRequest(decimal? PercentComplete);
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
// Per-CBS-line cashflow breakdown (T-S1-11 wire-up via B-017). For
// each (line, period) the BaselinePlanned is the line's Budget
// distributed across the period by linear overlap of the line's
// schedule range with the period's date range. Actual is summed from
// ActualCost where line+period match.
public record CashflowLinePeriodPoint(
    Guid PeriodId, string Label, DateTime StartDate, DateTime EndDate,
    decimal BaselinePlanned, decimal Actual);
public record CashflowLineSeries(
    Guid ItemId, string Code, string Name,
    decimal? Budget, DateTime? ScheduledStart, DateTime? ScheduledEnd,
    List<CashflowLinePeriodPoint> Points);
public record CashflowByLineDto(
    string ProjectCurrency, List<CashflowLineSeries> Lines);
public record RaiseVariationRequest(string Title, string? Description, string? Reason, decimal? EstimatedCostImpact, int? EstimatedTimeImpactDays, Guid? CostBreakdownItemId);
public record VariationDecisionRequest(string? DecisionNote);
// T-S2-04. Risk register CRUD. Probability and Impact are 1..5; Score is
// computed P×I server-side. CategoryId, OwnerId, ResponseStrategy,
// ResponsePlan, ContingencyAmount are all optional at create time.
public record CreateRiskRequest(
    string Title,
    string? Description,
    Guid? CategoryId,
    int Probability,
    int Impact,
    Guid? OwnerId,
    ResponseStrategy? ResponseStrategy,
    string? ResponsePlan,
    decimal? ContingencyAmount);
// All fields nullable for partial update. Setting Status to Closed via
// this DTO is rejected by RisksService.UpdateAsync — callers must use
// the dedicated close endpoint so the audit trail carries a distinct
// risk.closed event.
public record UpdateRiskRequest(
    string? Title,
    string? Description,
    Guid? CategoryId,
    int? Probability,
    int? Impact,
    RiskStatus? Status,
    Guid? OwnerId,
    ResponseStrategy? ResponseStrategy,
    string? ResponsePlan,
    decimal? ContingencyAmount);
// T-S2-06 qualitative assessment. Notes are required (the whole point
// of recording an assessment is the rationale); AssessedAt is set to
// UtcNow server-side; the assessor is the calling user.
public record RecordQualitativeAssessmentRequest(string Notes);
// T-S2-07 quantitative assessment. All four required. Service validates
// BestCase <= MostLikely <= WorstCase. To clear an existing 3-point
// estimate, a future endpoint can use a separate "clear" action — v1.0
// only supports setting / replacing.
public record RecordQuantitativeAssessmentRequest(
    decimal BestCase,
    decimal MostLikely,
    decimal WorstCase,
    DistributionShape Distribution);
// T-S2-09 contingency drawdown. Amount > 0; OccurredAt is a UTC date
// when the cost was incurred (distinct from row-write time).
public record RecordRiskDrawdownRequest(
    decimal Amount,
    DateTime OccurredAt,
    string? Reference,
    string? Note);
// T-S3-03 stakeholder register. Name required + Power/Interest in 1..5;
// EngagementApproach auto-computed from P/I at 3-as-midpoint by the
// service unless the caller overrides.
public record CreateStakeholderRequest(
    string Name,
    string? Organisation,
    string? Role,
    string? Email,
    string? Phone,
    int Power,
    int Interest,
    EngagementApproach? EngagementApproach,
    string? EngagementNotes);
// All fields nullable for partial update. Power/Interest changes
// recompute Score and (unless EngagementApproach is explicitly set in
// the same request) the Mendelow quadrant.
public record UpdateStakeholderRequest(
    string? Name,
    string? Organisation,
    string? Role,
    string? Email,
    string? Phone,
    int? Power,
    int? Interest,
    EngagementApproach? EngagementApproach,
    string? EngagementNotes);
// T-S3-06 engagement log entry. Summary is required (the whole point
// of recording an interaction is the summary); ActionsAgreed optional.
public record RecordEngagementRequest(
    EngagementType Type,
    DateTime OccurredAt,
    string Summary,
    string? ActionsAgreed);
// T-S3-07 communications matrix row. The four DoD axes — what / who /
// when / how — map to ItemType / Audience / Frequency / Channel.
// OwnerId must be a project member; service enforces.
public record CreateCommunicationItemRequest(
    string ItemType,
    string Audience,
    CommunicationFrequency Frequency,
    CommunicationChannel Channel,
    Guid OwnerId,
    string? Notes);
// All fields nullable for partial update. Caller can replace OwnerId
// (membership re-validated) or change cadence/channel as the comms
// plan evolves.
public record UpdateCommunicationItemRequest(
    string? ItemType,
    string? Audience,
    CommunicationFrequency? Frequency,
    CommunicationChannel? Channel,
    Guid? OwnerId,
    string? Notes);
// T-S4-03 dependency add. Predecessor + Successor must both belong to
// the same project (service-enforced); cycle rejected with
// ConflictException. Lag in days, decimal, can be negative for lead.
public record AddDependencyRequest(
    Guid PredecessorId,
    Guid SuccessorId,
    DependencyType Type,
    decimal Lag);
// T-S4-05 activity create. Code unique within project. Duration >= 0
// (zero is a milestone). PercentComplete in [0, 1]. ConstraintDate
// required when ConstraintType is one of the date-pinned variants
// (SNET / SNLT / FNET / FNLT / MSO / MFO). AssigneeId, Discipline
// optional.
public record CreateActivityRequest(
    string Code,
    string Name,
    string? Description,
    decimal Duration,
    DurationUnit DurationUnit,
    DateTime? ScheduledStart,
    DateTime? ScheduledFinish,
    ConstraintType ConstraintType,
    DateTime? ConstraintDate,
    decimal PercentComplete,
    Guid? AssigneeId,
    string? Discipline);
// All fields nullable for partial update. Code change re-validates
// uniqueness; AssigneeId change re-validates membership.
public record UpdateActivityRequest(
    string? Code,
    string? Name,
    string? Description,
    decimal? Duration,
    DurationUnit? DurationUnit,
    DateTime? ScheduledStart,
    DateTime? ScheduledFinish,
    ConstraintType? ConstraintType,
    DateTime? ConstraintDate,
    decimal? PercentComplete,
    Guid? AssigneeId,
    string? Discipline);
// T-S4-05 recompute. Optional dataDate override; service falls back
// to Project.StartDate if not supplied. Service rejects if neither
// is set.
public record RecomputeScheduleRequest(DateTime? DataDate);
// T-S4-05 schedule snapshot returned by GET /schedule and computed
// view endpoints. The Activities list mirrors Cpm.CpmActivityResult
// per row, joined to the Activity Code/Name/AssigneeId for UI use.
public record ScheduleActivityDto(
    Guid Id, string Code, string Name, string? Description,
    decimal Duration, DurationUnit DurationUnit,
    DateTime? EarlyStart, DateTime? EarlyFinish,
    DateTime? LateStart, DateTime? LateFinish,
    decimal? TotalFloat, decimal? FreeFloat, bool IsCritical,
    DateTime? ScheduledStart, DateTime? ScheduledFinish,
    DateTime? ActualStart, DateTime? ActualFinish,
    ConstraintType ConstraintType, DateTime? ConstraintDate,
    decimal PercentComplete, Guid? AssigneeId, string? Discipline,
    bool IsActive);
public record ScheduleRecomputeResultDto(
    DateTime ProjectStart, DateTime ProjectFinish, int ActivitiesCount, int CriticalActivitiesCount);
// T-S4-06 baseline capture. Label required ("Original baseline",
// "Revision 2", etc.). Snapshot pulls every active activity.
public record CreateBaselineRequest(string Label);
// Header listing of baselines on a project (T-S4-06).
public record BaselineSummaryDto(
    Guid Id, string Label, DateTime CapturedAt, Guid CapturedById,
    int ActivitiesCount, DateTime? ProjectFinishAtBaseline);
// One activity-level row in the comparison output. Variances are
// in days, current minus baseline (positive = slipped later /
// expanded). Either side can be null:
// - IsNewSinceBaseline = true → activity exists currently but was
//   not in the baseline; baseline* fields null.
// - IsRemovedSinceBaseline = true → activity was in the baseline
//   but is now deactivated; current* fields null.
public record BaselineActivityComparisonDto(
    Guid ActivityId, string Code, string Name,
    DateTime? BaselineEarlyStart, DateTime? BaselineEarlyFinish,
    decimal? BaselineDuration, bool BaselineWasCritical,
    DateTime? CurrentEarlyStart, DateTime? CurrentEarlyFinish,
    decimal? CurrentDuration, bool CurrentIsCritical,
    decimal? StartVarianceDays, decimal? FinishVarianceDays,
    decimal? DurationVarianceDays,
    bool IsNewSinceBaseline, bool IsRemovedSinceBaseline);
public record BaselineComparisonDto(
    Guid BaselineId, string Label, DateTime CapturedAt,
    DateTime? ProjectFinishAtBaseline, DateTime? CurrentProjectFinish,
    decimal? ProjectFinishVarianceDays,
    int AddedActivitiesCount, int RemovedActivitiesCount,
    List<BaselineActivityComparisonDto> Activities);
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
    DateTime? IssuedAt,
    // T-S1-09 / B-017 valuation auto-derive (NEC4 PWDD per ADR-0013).
    // Σ (CBS line Budget × PercentComplete) across the project, computed
    // every read. Stored CumulativeValuation remains the source of truth
    // (assessor-stated); this field surfaces the progress-derived value
    // alongside it as a guide / preview.
    decimal DerivedValuationFromProgress);
