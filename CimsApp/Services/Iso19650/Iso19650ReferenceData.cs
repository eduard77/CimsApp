namespace CimsApp.Services.Iso19650;

/// <summary>
/// Hard-coded reference data for v1. Sprint 8 replaces this with
/// an NBS-backed feed and a Uniclass classification service.
/// Do not add an interface now - YAGNI; Sprint 8 introduces the
/// abstraction when the real source is known.
/// </summary>
internal static class Iso19650ReferenceData
{
    // ISO 19650-2 Annex A, illustrative subset. Extend in Sprint 8.
    public static readonly HashSet<string> TypeCodes = new(StringComparer.Ordinal)
    {
        "DR", // Drawing
        "MD", // Model
        "SP", // Specification
        "SH", // Schedule
        "RP", // Report
        "CA", // Calculation
        "CO"  // Correspondence
    };

    // Role codes - illustrative subset.
    public static readonly HashSet<string> RoleCodes = new(StringComparer.Ordinal)
    {
        "A",  // Architect
        "B",  // Building surveyor
        "C",  // Civil engineer
        "E",  // Electrical engineer
        "M",  // Mechanical engineer
        "S",  // Structural engineer
        "Q",  // Quantity surveyor
        "X",  // Sub-consultant
        "Y",  // Specialist
        "Z"   // General
    };

    // Suitability codes - v1 WIP/Shared set only. A- and B- (Published,
    // Authorised) are Sprint 8+ because they're bound to the CDE state
    // machine and gateway approval workflow.
    public static readonly HashSet<string> SuitabilityCodes = new(StringComparer.Ordinal)
    {
        "S1", // WIP
        "S2", // Shared (non-contractual)
        "S3", // Shared (for review/comment)
        "S4", // Shared (for construction)
        "S5", // Shared (for manufacture)
        "S6", // Shared (as-constructed / PIM handover)
        "S7"  // Shared (as-constructed / AIM)
    };
}