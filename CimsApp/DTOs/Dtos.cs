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
// T-S4-07 LPS lookahead entry. WeekStarting is normalised by the
// service to the Monday of the supplied date. ConstraintsRemoved
// defaults false at create time; the PM toggles it during the
// weekly LPS meeting.
public record CreateLookaheadEntryRequest(
    Guid ActivityId, DateTime WeekStarting,
    bool ConstraintsRemoved, string? Notes);
public record UpdateLookaheadEntryRequest(
    bool? ConstraintsRemoved, string? Notes);
// T-S4-07 weekly work plan. WeekStarting normalised to Monday.
// Unique within project.
public record CreateWeeklyWorkPlanRequest(DateTime WeekStarting, string? Notes);
// T-S4-07 commit-to-task. ActivityId must belong to the same project
// as the WeeklyWorkPlan; service enforces same-project + active.
public record AddCommitmentRequest(Guid ActivityId, string? Notes);
// At week-end the IM/PM marks each commitment Completed = true OR
// supplies a Reason. Completed = false WITHOUT a Reason is rejected
// (the whole point of LPS is to surface reasons-for-non-completion).
public record UpdateCommitmentRequest(
    bool Completed, LpsReasonForNonCompletion? Reason, string? Notes);
// Weekly Work Plan view with computed PPC. PPC = 100 ×
// completed_count / committed_count when committed > 0; null
// otherwise (no commitments yet recorded).
public record WeeklyWorkPlanDto(
    Guid Id, Guid ProjectId, DateTime WeekStarting, string? Notes,
    DateTime CreatedAt, Guid CreatedById,
    int CommittedCount, int CompletedCount, decimal? PercentPlanComplete,
    List<WeeklyTaskCommitmentDto> Commitments);
public record WeeklyTaskCommitmentDto(
    Guid Id, Guid WeeklyWorkPlanId, Guid ActivityId,
    string ActivityCode, string ActivityName,
    bool Committed, bool Completed,
    LpsReasonForNonCompletion? Reason, string? Notes,
    DateTime UpdatedAt);
// T-S4-09 MSP XML import summary. Counts what was inserted; the
// caller can call /schedule/recompute to populate CPM fields.
public record MsProjectImportResultDto(
    string? ProjectName, DateTime? ProjectStart,
    int ActivitiesImported, int DependenciesImported,
    List<string> Warnings);
// T-S4-11 Gantt data endpoint. UI-friendly read view: per-activity
// bars + per-dependency arrows. Start / Finish prefer CPM-computed
// EarlyStart / EarlyFinish; fall back to ScheduledStart / Finish if
// the CPM solver hasn't been run yet (or both null if neither set).
// Network-view (PERT diagram) endpoint deferred to B-033 v1.1 per
// CR-005.
public record GanttActivityDto(
    Guid Id, string Code, string Name,
    DateTime? Start, DateTime? Finish,
    decimal Duration, decimal PercentComplete,
    bool IsCritical, Guid? AssigneeId, string? Discipline);
public record GanttDependencyDto(
    Guid Id, Guid PredecessorId, Guid SuccessorId,
    DependencyType Type, decimal Lag);
public record GanttDto(
    DateTime? ProjectStart, DateTime? ProjectFinish,
    List<GanttActivityDto> Activities,
    List<GanttDependencyDto> Dependencies);
// T-S5 ChangeRequest workflow DTOs (PAFM-SD F.6).
// Raise: TaskTeamMember+. Title required; Category required;
// BsaCategory defaults to NotApplicable. Impact summaries are
// optional at raise time and typically populated at assess.
public record RaiseChangeRequestRequest(
    string Title,
    string? Description,
    ChangeRequestCategory Category,
    BsaHrbCategory BsaCategory,
    string? ProgrammeImpactSummary,
    string? CostImpactSummary,
    decimal? EstimatedCostImpact,
    int? EstimatedTimeImpactDays);
// Assess: InformationManager+. AssessmentNote required (the
// whole point of the assess step is the impact analysis text).
// Optional impact-summary / estimate updates if the IM refines
// what the raiser captured.
public record AssessChangeRequestRequest(
    string AssessmentNote,
    string? ProgrammeImpactSummary,
    string? CostImpactSummary,
    decimal? EstimatedCostImpact,
    int? EstimatedTimeImpactDays,
    BsaHrbCategory? BsaCategory);
// Approve: ProjectManager+. DecisionNote required. CreateVariation
// flag (T-S5-06) atomically spawns an S1 Variation as a side-effect.
public record ApproveChangeRequestRequest(
    string DecisionNote,
    bool CreateVariation);
// Reject: ProjectManager+. DecisionNote required so the audit
// trail carries a "why rejected" rationale.
public record RejectChangeRequestRequest(string DecisionNote);
// Implement / Close take optional notes only (the body itself is
// just a marker that the transition occurred).
public record ImplementChangeRequestRequest(string? Note);
public record CloseChangeRequestRequest(string? Note);
// T-S6-02 ProcurementStrategy upsert. One row per project; the
// service does the read-then-update dance. Approve transition is
// a separate endpoint (POST .../approve).
public record UpsertProcurementStrategyRequest(
    ProcurementApproach Approach,
    ContractForm ContractForm,
    decimal? EstimatedTotalValue,
    string? KeyDates,
    string? PackageBreakdownNotes);
// T-S6-03 TenderPackage create. Name + EstimatedValue typically
// required at creation; dates can be filled in before Issue.
public record CreateTenderPackageRequest(
    string Name,
    string? Description,
    decimal? EstimatedValue,
    DateTime? IssueDate,
    DateTime? ReturnDate);
// All fields nullable for partial update. Update only allowed in
// Draft state (service enforces).
public record UpdateTenderPackageRequest(
    string? Name,
    string? Description,
    decimal? EstimatedValue,
    DateTime? IssueDate,
    DateTime? ReturnDate);
// T-S6-04 Tender submission. The act of "submitting" is just
// recording — bidders submit externally; the project admin / PM
// captures the bid receipt. SubmittedAt is set to UtcNow at
// create.
public record SubmitTenderRequest(
    string BidderName,
    string? BidderOrganisation,
    string? ContactEmail,
    decimal BidAmount);
// Bidder withdraws — only allowed before evaluation / award
// (Submitted state only). Note required so the audit trail
// captures the rationale.
public record WithdrawTenderRequest(string Note);
// T-S6-05 EvaluationCriterion + Score DTOs.
public record AddEvaluationCriterionRequest(
    string Name,
    EvaluationCriterionType Type,
    decimal Weight);
public record UpdateEvaluationCriterionRequest(
    string? Name,
    EvaluationCriterionType? Type,
    decimal? Weight);
// Score in [0, 100]; Notes optional rationale.
public record SetEvaluationScoreRequest(
    decimal Score,
    string? Notes);
// Per-(criterion) cell in the evaluation matrix view.
public record EvaluationMatrixCellDto(
    Guid CriterionId, string CriterionName, EvaluationCriterionType Type,
    decimal Weight, decimal? Score, string? Notes);
// Per-tender row in the evaluation matrix view.
public record EvaluationMatrixRowDto(
    Guid TenderId, string BidderName, decimal BidAmount, TenderState State,
    decimal? OverallScore,
    List<EvaluationMatrixCellDto> Cells);
// The whole matrix. TotalWeight should be 1.0 (epsilon 0.0001);
// IsValid = false flags weight-sum drift to the UI.
public record EvaluationMatrixDto(
    Guid TenderPackageId,
    decimal TotalWeight,
    bool IsValid,
    List<EvaluationMatrixRowDto> Tenders);
// T-S6-06 Award request. Atomically transitions winning Tender →
// Awarded, all other active Tenders in the package → Rejected,
// closes the package, and spawns a Contract row. ContractForm
// optional (defaults to ProcurementStrategy.ContractForm if a
// strategy exists, else Other). Start / End dates optional at
// award time — typically filled in once the contract is signed.
public record AwardTenderPackageRequest(
    Guid AwardedTenderId,
    string AwardNote,
    ContractForm? ContractForm,
    DateTime? ContractStartDate,
    DateTime? ContractEndDate);
// T-S6-07 EarlyWarning DTOs.
public record RaiseEarlyWarningRequest(
    string Title,
    string? Description);
// Review: ResponseNote required (the analysis IS the review).
public record ReviewEarlyWarningRequest(string ResponseNote);
// Close: optional ClosureNote.
public record CloseEarlyWarningRequest(string? ClosureNote);
// T-S6-08 CompensationEvent DTOs (NEC4 clause 60.1).
public record NotifyCompensationEventRequest(
    string Title,
    string? Description);
// Quote: contractor submits the cost / time impact quotation.
// Both impact fields required so the matrix has data to evaluate.
public record QuoteCompensationEventRequest(
    decimal EstimatedCostImpact,
    int EstimatedTimeImpactDays,
    string QuotationNote);
// Decide (accept / reject): DecisionNote required so the trail
// captures the rationale.
public record DecideCompensationEventRequest(string DecisionNote);
// Implement: optional note.
public record ImplementCompensationEventRequest(string? Note);
// T-S7-02 Dashboards. Six per-role aggregation views (PM / CM /
// SM / IM / HSE / Client). All return the same DTO shape — a
// list of DashboardCardDto rows, each tagged with a semantic type
// so the UI can format Counts, Percentages, Currency, Dates, and
// free-text values appropriately.
public enum DashboardCardType { Count, Percentage, Currency, Date, Text }
public record DashboardCardDto(
    string Name,
    string Value,
    DashboardCardType Type,
    string? Subtitle);
public record DashboardDto(
    string Role,
    Guid ProjectId,
    string ProjectName,
    string ProjectCode,
    List<DashboardCardDto> Cards);
// T-S7-03 Monthly Project Report (MPR). PAFM-SD F.8 second
// bullet. v1.0 returns JSON-only — PDF rendering deferred to
// v1.1 / B-055. Section layout is a best-effort inference from
// PMBOK 7 / NEC4 standard reporting templates; reconcile against
// canonical PAFM Ch 30 paste when B-055 lands.
// PAFM Ch 30 reference: best-effort inference v1.0
public record MprDto(
    Guid ProjectId,
    string ProjectName,
    string ProjectCode,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime GeneratedAtUtc,
    MprExecutiveSummary ExecutiveSummary,
    MprProgrammeStatus Programme,
    MprCostStatus Cost,
    MprRiskStatus Risk,
    MprVariationsAndChanges Changes,
    MprIssues Issues,
    MprStakeholderUpdates Stakeholders);
public record MprExecutiveSummary(
    string ProjectStatus,
    DateTime? PlannedEndDate,
    DateTime? EstimatedEndDate,
    int OpenRisksCount,
    int OpenIssuesCount);
public record MprProgrammeStatus(
    int TotalActivities,
    int CompletedActivities,
    decimal? PercentComplete,
    DateTime? EarliestEarlyStart,
    DateTime? LatestEarlyFinish,
    string? LatestBaselineLabel,
    DateTime? LatestBaselineCapturedAt);
public record MprCostStatus(
    string Currency,
    decimal TotalBudget,
    decimal TotalCommitted,
    decimal TotalActuals,
    decimal? PercentSpent);
public record MprRiskStatus(
    int OpenTotal,
    int OpenHighSeverity,
    int OpenMediumSeverity,
    int OpenLowSeverity);
public record MprVariationsAndChanges(
    int VariationsRaisedInPeriod,
    int VariationsApprovedInPeriod,
    decimal VariationsApprovedValueInPeriod,
    int ChangeRequestsRaisedInPeriod,
    int ChangeRequestsApprovedInPeriod);
public record MprIssues(
    int OpenRfis,
    int OpenActions,
    int OpenEarlyWarnings,
    int OpenCompensationEvents);
public record MprStakeholderUpdates(
    int StakeholdersTotal,
    int EngagementLogsInPeriod,
    int CommunicationsTotal);
// T-S7-04 KPI cards. PAFM-SD F.8 third bullet — "KPI cards
// tied to success criteria" mapped to PAFM-SD Ch 2.6 v1.0
// success criteria. Single project-level endpoint returning
// the card list. Re-uses DashboardCardDto for shape parity
// with the per-role dashboards.
public record KpiCardsDto(
    Guid ProjectId,
    string ProjectName,
    string ProjectCode,
    List<DashboardCardDto> Cards);
// T-S7-05 Custom report definitions. v1.0 ships pure-equality
// filtering against a per-entity field allow-list. Richer
// operators → v1.1 / B-060. Cross-entity joins → B-056.
// Scheduled runs + email → B-057. Export formats → B-058.
public record CustomReportDefinitionDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    CustomReportEntityType EntityType,
    string FilterJson,
    string ColumnsJson,
    Guid CreatedById,
    DateTime CreatedAt,
    DateTime UpdatedAt);
public record CreateCustomReportDefinitionRequest(
    string Name,
    CustomReportEntityType EntityType,
    string? FilterJson,
    string? ColumnsJson);
public record UpdateCustomReportDefinitionRequest(
    string? Name,
    string? FilterJson,
    string? ColumnsJson);
public record CustomReportRunResultDto(
    Guid DefinitionId,
    string Name,
    CustomReportEntityType EntityType,
    int RowCount,
    List<string> Columns,
    List<Dictionary<string, object?>> Rows);
// T-S9-05 MIDP entry DTOs.
public record MidpEntryDto(
    Guid Id, Guid ProjectId,
    string Title, string? Description,
    string? DocTypeFilter,
    DateTime DueDate,
    Guid OwnerId, Guid? DocumentId,
    bool IsCompleted, DateTime? CompletedAt,
    DateTime CreatedAt, DateTime UpdatedAt);
public record CreateMidpEntryRequest(
    string Title, string? Description,
    string? DocTypeFilter,
    DateTime DueDate,
    Guid OwnerId);
public record UpdateMidpEntryRequest(
    string? Title, string? Description,
    string? DocTypeFilter,
    DateTime? DueDate,
    Guid? OwnerId);
public record CompleteMidpEntryRequest(Guid? DocumentId);
// T-S9-06 TIDP entry DTOs.
public record TidpEntryDto(
    Guid Id, Guid ProjectId, Guid MidpEntryId,
    string TeamName, DateTime DueDate,
    bool IsSignedOff, Guid? SignedOffById, DateTime? SignedOffAt, string? SignOffNote,
    DateTime CreatedAt, DateTime UpdatedAt);
public record CreateTidpEntryRequest(
    Guid MidpEntryId,
    string TeamName,
    DateTime DueDate);
public record UpdateTidpEntryRequest(
    string? TeamName,
    DateTime? DueDate);
public record SignOffTidpEntryRequest(string? Note);
// T-S10-02 BSA 2022 HRB project metadata.
// // BSA 2022 ref: Part 4 (Higher-Risk Buildings); Schedule 1
// (HRB categorisation).
public record SetProjectHrbRequest(bool IsHrb, BsaHrbCategory HrbCategory);
// T-S10-03 GatewayPackage DTOs.
// // BSA 2022 ref: Part 3 (HRB construction).
public record GatewayPackageDto(
    Guid Id, Guid ProjectId,
    string Number, GatewayType Type,
    string Title, string? Description,
    GatewayPackageState State,
    DateTime? SubmittedAt, Guid? SubmittedById,
    DateTime? DecidedAt, Guid? DecidedById,
    GatewayDecision? Decision, string? DecisionNote,
    DateTime CreatedAt, DateTime UpdatedAt);
public record CreateGatewayPackageRequest(
    GatewayType Type,
    string Title,
    string? Description);
public record DecideGatewayPackageRequest(
    GatewayDecision Decision,
    string DecisionNote);
// T-S10-04 MOR DTOs.
// // BSA 2022 ref: s.87 (mandatory occurrence reporting).
public record MandatoryOccurrenceReportDto(
    Guid Id, Guid ProjectId,
    string Number, string Title, string Description,
    MorSeverity Severity, DateTime OccurredAt,
    Guid ReporterId,
    bool ReportedToBsr, DateTime? ReportedToBsrAt, string? BsrReference,
    DateTime CreatedAt, DateTime UpdatedAt);
public record CreateMorRequest(
    string Title, string Description,
    MorSeverity Severity, DateTime OccurredAt);
public record MarkMorReportedToBsrRequest(string? BsrReference);
// T-S10-05 Safety Case Summary DTO. v1.0 best-effort
// inference; canonical Schedule 5 reconciliation → v1.1 / B-071.
// // BSA 2022 ref: Schedule 5 (Safety Case).
public record SafetyCaseSummaryDto(
    Guid ProjectId, string ProjectName, string ProjectCode,
    bool IsHrb, BsaHrbCategory HrbCategory,
    DateTime GeneratedAtUtc,
    int OpenRisksCount, int OpenIssuesCount,
    int OpenMorsCount, int OpenGatewayPackagesCount,
    int GoldenThreadDocumentsCount,
    GatewayPackageState? Gateway1State,
    GatewayPackageState? Gateway2State,
    GatewayPackageState? Gateway3State);
// T-S10-06 Golden Thread DTOs.
public record AddDocumentToGoldenThreadRequest(string? Note);
public record GoldenThreadDocumentDto(
    Guid Id, string DocumentNumber, string Title,
    CdeState CurrentState,
    DateTime AddedToGoldenThreadAt, Guid AddedToGoldenThreadById);
// T-S11-02 ROPA DTOs. // GDPR ref: Art. 30.
public record RopaEntryDto(
    Guid Id, Guid OrganisationId,
    string Purpose, LawfulBasis LawfulBasis,
    string DataCategoriesCsv, string Recipients,
    string RetentionPeriod, string SecurityMeasures,
    DateTime CreatedAt, DateTime UpdatedAt);
public record CreateRopaEntryRequest(
    string Purpose, LawfulBasis LawfulBasis,
    string? DataCategoriesCsv, string? Recipients,
    string? RetentionPeriod, string? SecurityMeasures);
public record UpdateRopaEntryRequest(
    string? Purpose, LawfulBasis? LawfulBasis,
    string? DataCategoriesCsv, string? Recipients,
    string? RetentionPeriod, string? SecurityMeasures);
// T-S11-03 DPIA DTOs. // GDPR ref: Art. 35.
public record DpiaDto(
    Guid Id, Guid ProjectId,
    string Title, string Description,
    string? HighRiskProcessingDescription, string? MitigationsDescription,
    DpiaState State,
    Guid CreatedById, Guid? ReviewedById, DateTime? ReviewedAt, string? DecisionNote,
    DateTime CreatedAt, DateTime UpdatedAt);
public record CreateDpiaRequest(
    string Title, string Description,
    string? HighRiskProcessingDescription,
    string? MitigationsDescription);
public record UpdateDpiaRequest(
    string? Title, string? Description,
    string? HighRiskProcessingDescription,
    string? MitigationsDescription);
public record DpiaDecisionRequest(string DecisionNote);
// T-S11-04 SAR DTOs. // GDPR ref: Art. 12, 15.
public record SarDto(
    Guid Id, Guid OrganisationId,
    string Number,
    string DataSubjectName, string? DataSubjectEmail, string RequestDescription,
    SarState State,
    DateTime RequestedAt, DateTime DueAt,
    Guid? FulfilledById, DateTime? FulfilledAt, string? FulfilmentNote,
    Guid? RefusedById, DateTime? RefusedAt, string? RefusalReason,
    DateTime CreatedAt, DateTime UpdatedAt);
public record CreateSarRequest(
    string DataSubjectName, string? DataSubjectEmail, string RequestDescription,
    DateTime? RequestedAt);
public record StartSarFulfilmentRequest(string? Note);
public record FulfilSarRequest(string FulfilmentNote);
public record RefuseSarRequest(string RefusalReason);
// T-S11-05 Data Breach Log DTOs. // GDPR ref: Art. 33-34.
public record DataBreachLogDto(
    Guid Id, Guid OrganisationId,
    string Number, string Title, string Description,
    BreachSeverity Severity,
    DateTime OccurredAt, DateTime DiscoveredAt,
    string DataCategoriesCsv, int? AffectedSubjectsCount,
    bool ReportedToIco, DateTime? ReportedToIcoAt, string? IcoReference,
    bool NotifiedDataSubjects, DateTime? NotifiedDataSubjectsAt,
    Guid CreatedById,
    DateTime CreatedAt, DateTime UpdatedAt);
public record CreateBreachRequest(
    string Title, string Description,
    BreachSeverity Severity,
    DateTime OccurredAt, DateTime DiscoveredAt,
    string? DataCategoriesCsv, int? AffectedSubjectsCount);
public record MarkBreachReportedToIcoRequest(string? IcoReference);
public record MarkBreachNotifiedDataSubjectsRequest();
// T-S11-06 Retention Schedule DTOs. // GDPR ref: Art. 5(1)(e).
public record RetentionScheduleDto(
    Guid Id, Guid OrganisationId,
    string DataCategory, int RetentionPeriodMonths,
    string LawfulBasisForRetention, string? Notes,
    DateTime CreatedAt, DateTime UpdatedAt);
public record CreateRetentionScheduleRequest(
    string DataCategory, int RetentionPeriodMonths,
    string LawfulBasisForRetention, string? Notes);
public record UpdateRetentionScheduleRequest(
    int? RetentionPeriodMonths,
    string? LawfulBasisForRetention,
    string? Notes);
// T-S12-02 Improvement Register DTOs.
public record ImprovementEntryDto(
    Guid Id, Guid ProjectId, string Number,
    string Title, string Description,
    PdcaState State, int CycleNumber,
    string? PlanNotes, string? DoNotes, string? CheckNotes, string? ActNotes,
    Guid OwnerId, Guid CreatedById,
    DateTime CreatedAt, DateTime UpdatedAt);
public record CreateImprovementRequest(
    string Title, string Description, Guid OwnerId);
public record TransitionImprovementRequest(string? StageNotes);
// T-S12-03 Lesson Learned DTOs.
public record LessonLearnedDto(
    Guid Id, Guid OrganisationId,
    string Title, string Description,
    string? Category, Guid? SourceProjectId, string TagsCsv,
    Guid RecordedById,
    DateTime CreatedAt, DateTime UpdatedAt);
public record CreateLessonLearnedRequest(
    string Title, string Description,
    string? Category, Guid? SourceProjectId, string? TagsCsv);
public record UpdateLessonLearnedRequest(
    string? Title, string? Description,
    string? Category, string? TagsCsv);
// T-S12-04 OpportunityToImprove DTOs.
public record OpportunityToImproveDto(
    Guid Id, Guid ProjectId, string Number,
    string Title, string Description,
    string? SourceEntityType, Guid? SourceEntityId,
    Guid RaisedById,
    bool IsActioned, DateTime? ActionedAt, Guid? ActionedById, string? ActionNote,
    DateTime CreatedAt, DateTime UpdatedAt);
public record CreateOpportunityToImproveRequest(
    string Title, string Description,
    string? SourceEntityType, Guid? SourceEntityId);
public record ActionOpportunityToImproveRequest(string? Note);
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
