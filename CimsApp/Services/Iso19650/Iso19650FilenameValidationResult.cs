namespace CimsApp.Services.Iso19650;

/// <summary>
/// Result of validating an ISO 19650 filename.
/// v1.0 scope: 5 of 12 checks from PAFM Appendix F.9.
/// Remaining 7 checks (Uniclass, IFC, cross-ref, metadata completeness,
/// audit trail, state transition, deprecation) are Sprint 8 proper.
/// </summary>
public sealed record Iso19650FilenameValidationResult(
    string Filename,
    IReadOnlyList<Iso19650CheckOutcome> Checks)
{
    public bool IsValid => Checks.All(c => c.Passed);
}

public sealed record Iso19650CheckOutcome(
    Iso19650CheckId Id,
    string Label,
    bool Passed,
    string Message);

public enum Iso19650CheckId
{
    Structure = 1,
    FieldValidity = 2,
    Numbering = 3,
    Suitability = 4,
    Revision = 5
}