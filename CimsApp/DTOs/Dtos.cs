using CimsApp.Models;

namespace CimsApp.DTOs;

public record RegisterRequest(string Email, string Password, string FirstName, string LastName, string? JobTitle, Guid OrganisationId);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record AuthResponse(string AccessToken, string RefreshToken, UserSummaryDto User);
public record UserSummaryDto(Guid Id, string Email, string FirstName, string LastName, string? JobTitle, OrgSummaryDto Organisation);
public record OrgSummaryDto(Guid Id, string Name, string Code);
public record CreateOrgRequest(string Name, string Code, string? Country);
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
