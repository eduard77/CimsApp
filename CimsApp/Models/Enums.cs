namespace CimsApp.Models;

public enum UserRole { SuperAdmin, OrgAdmin, ProjectManager, InformationManager, TaskTeamMember, ClientRep, Viewer }
public enum CdeState { WorkInProgress, Shared, Published, Archived, Voided }
public enum SuitabilityCode { S0, S1, S2, S3, S4, S5, S6, D1, D2, D3, D4, D5 }
public enum ProjectStatus { Initiation, Planning, Execution, Monitoring, Closeout, Completed, Suspended, Cancelled }
public enum DocumentType { Model, Drawing, Specification, Schedule, Report, Correspondence, MeetingMinutes, Rfi, Transmittal, Contract, VariationOrder, PaymentCertificate, InspectionReport, Ncr, RiskRegister, OmManual, Other }
public enum RfiStatus { Draft, Open, UnderReview, Responded, Closed, Cancelled }
public enum Priority { Low, Medium, High, Urgent }
public enum ActionStatus { Open, InProgress, Closed, Cancelled }
public enum CommitmentType { PO, Subcontract }
public enum VariationState { Raised, Approved, Rejected }
public enum PaymentCertificateState { Draft, Issued }

// S2 Risk module (PAFM-SD F.3).
public enum RiskStatus { Identified, Assessed, Active, Mitigated, Closed }
// PMBOK 5 / 7 negative-risk response strategies. (Opportunity / positive-risk
// strategies — exploit / share / enhance / accept — land in B-029 v1.1.)
public enum ResponseStrategy { Avoid, Transfer, Mitigate, Accept }
// 3-point estimate distribution shape (T-S2-07). Triangular is the simplest
// (linear interpolation between Best, Most-Likely, Worst). PERT is the
// classical PMBOK weighted-average shape (mean = (B + 4M + W) / 6,
// stddev = (W - B) / 6). Beta is a generalisation of PERT used in
// quantitative-risk literature; v1.0 ships parameter-shape selection
// only, the actual distribution sampling lands in T-S2-08 Monte Carlo.
public enum DistributionShape { Triangular, Pert, Beta }

// S3 Stakeholder & Communications module (PAFM-SD F.4).
// Mendelow's Power/Interest grid quadrants. Service auto-computes the
// approach from Power and Interest at write time (3-as-midpoint);
// v1.1 candidate: per-tenant threshold override in S14 Admin Console.
public enum EngagementApproach { ManageClosely, KeepSatisfied, KeepInformed, Monitor }
// Type of recorded interaction with a stakeholder (T-S3-06).
public enum EngagementType { Meeting, Call, Email, Letter, Workshop, Other }
// Frequency / channel for a row in the project-level communications
// matrix (T-S3-07).
public enum CommunicationFrequency { Daily, Weekly, Fortnightly, Monthly, Quarterly, AdHoc }
public enum CommunicationChannel { Email, Meeting, Portal, Letter, Phone, Other }

// S4 Schedule & Programme module (PAFM-SD F.5).
// Activity duration unit. v1.0 ships Day only; Hour is reserved for
// v1.1 when calendar / non-working-day rules can be modelled.
public enum DurationUnit { Day, Hour }
// MS-Project-style activity scheduling constraint. ASAP (As Soon As
// Possible) is the default — no hard date constraint, just the
// dependency-driven start. The seven other types pin start / finish
// to a date in the named direction. SNET = Start No Earlier Than;
// SNLT = Start No Later Than; FNET = Finish No Earlier Than;
// FNLT = Finish No Later Than; MSO = Must Start On; MFO = Must
// Finish On. ALAP (As Late As Possible) is the backward-pass twin
// of ASAP. CPM solver respects the constraint when computing
// Early/Late dates.
public enum ConstraintType { ASAP, ALAP, SNET, SNLT, FNET, FNLT, MSO, MFO }
// Inter-activity dependency type (T-S4-03). Standard PMBOK / MS
// Project four shapes:
// - FS (Finish-to-Start): predecessor finishes, then successor starts.
//   Default in 90% of construction schedules.
// - SS (Start-to-Start): successor can start once predecessor starts.
// - FF (Finish-to-Finish): successor can finish once predecessor finishes.
// - SF (Start-to-Finish): successor can finish once predecessor starts.
//   Rare; valid CPM topology nonetheless.
// Lag (in days, decimal) is added to the trigger event; negative lag
// is "lead" and pulls the successor in.
public enum DependencyType { FS, SS, FF, SF }
// LPS reason-for-non-completion (T-S4-07). Standard Last Planner
// System root-cause categories for activities that were Committed
// but not Completed in a Weekly Work Plan. v1.1 candidate: per-tenant
// custom reason categories — UK construction projects often want a
// finer breakdown (e.g. "Welfare facilities unavailable" or
// "Permit-to-work delay").
public enum LpsReasonForNonCompletion
{
    ResourceUnavailability,
    MaterialDelay,
    WeatherImpact,
    DesignChange,
    PrerequisiteIncomplete,
    ScopeChange,
    AccessIssue,
    Other,
}

// S5 Change Control module (PAFM-SD F.6).
// Construction-site change category — F.6 second bullet.
public enum ChangeRequestCategory { Scope, Time, Cost, Quality }
// Building Safety Act 2022 HRB categorisation — F.6 third bullet.
// NotApplicable for non-HRB projects (the majority); A/B/C tagged
// per Building Safety Regulator guidance for HRB projects. v1.0
// ships the categorisation as a tag (no auto-inference); per-tenant
// configurable inference rules → v1.1 / S14 Admin Console.
public enum BsaHrbCategory { NotApplicable, A, B, C }
// 5-state ChangeRequest workflow — F.6 first bullet. Raised → Assessed
// → Approved | Rejected → Implemented → Closed. Reject is allowed from
// Raised or Assessed only; once Approved the only forward path is
// Implemented → Closed. State-machine enforcement in
// Core/ChangeWorkflow.cs.
public enum ChangeRequestState { Raised, Assessed, Approved, Rejected, Implemented, Closed }

// S6 Procurement module (PAFM-SD F.7).
// Procurement approach — F.7 first bullet (strategy capture). UK
// construction default options. v1.1 candidate: per-tenant
// configurable approach catalogue.
public enum ProcurementApproach
{
    Traditional,
    DesignAndBuild,
    ConstructionManagement,
    ManagementContracting,
    PartneringFramework,
    Other,
}
// Contract form — F.7 first bullet. NEC4 Options A..F + JCT
// Standard Building / Design and Build are the dominant UK forms.
// "Other" covers JCT minor works, FIDIC, bespoke contracts, etc.
public enum ContractForm
{
    Nec4OptionA,   // Priced contract with activity schedule
    Nec4OptionB,   // Priced contract with bill of quantities
    Nec4OptionC,   // Target contract with activity schedule
    Nec4OptionD,   // Target contract with bill of quantities
    Nec4OptionE,   // Cost reimbursable contract
    Nec4OptionF,   // Management contract
    JctStandardBuilding,
    JctDesignAndBuild,
    Other,
}
