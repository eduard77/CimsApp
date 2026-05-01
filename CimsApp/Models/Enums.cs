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
