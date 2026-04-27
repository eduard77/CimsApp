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
