namespace CimsApp.Core;

/// <summary>
/// ISO 19650-2 Annex A - Standard metadata code tables for document naming.
/// These codes are the UK BIM Framework recommended values.
/// </summary>
public static class Iso19650Codes
{
    // ── Volume / System codes ─────────────────────────────────────────────────
    // Identifies the volume, system or main building that the container relates to
    public static readonly List<CodeItem> Volumes =
    [
        new("ZZ", "Not applicable / multiple"),
        new("XX", "All zones / whole site"),
        new("01", "Volume 1 / Building 1"),
        new("02", "Volume 2 / Building 2"),
        new("03", "Volume 3 / Building 3"),
        new("04", "Volume 4 / Building 4"),
        new("05", "Volume 5 / Building 5"),
        new("A",  "Block A"),
        new("B",  "Block B"),
        new("C",  "Block C"),
        new("D",  "Block D"),
        new("E",  "Block E"),
    ];

    // ── Level / Location codes ────────────────────────────────────────────────
    public static readonly List<CodeItem> Levels =
    [
        new("ZZ", "Not applicable / multiple levels"),
        new("XX", "All levels"),
        new("B2", "Basement 2"),
        new("B1", "Basement 1"),
        new("GF", "Ground floor"),
        new("00", "Level 00 (ground)"),
        new("01", "Level 01"),
        new("02", "Level 02"),
        new("03", "Level 03"),
        new("04", "Level 04"),
        new("05", "Level 05"),
        new("06", "Level 06"),
        new("07", "Level 07"),
        new("08", "Level 08"),
        new("09", "Level 09"),
        new("10", "Level 10"),
        new("RF", "Roof"),
        new("M",  "Mezzanine"),
    ];

    // ── Document Type codes (ISO 19650-2 Table A.1) ───────────────────────────
    public static readonly List<CodeItem> DocumentTypes =
    [
        new("DR", "Drawing"),
        new("M3", "3D model"),
        new("M2", "2D model"),
        new("SP", "Specification"),
        new("SH", "Schedule"),
        new("CA", "Calculations"),
        new("CM", "Communication / memo"),
        new("CO", "Correspondence"),
        new("CP", "Cost plan"),
        new("EA", "Employer's agreement"),
        new("FN", "File note"),
        new("HS", "Health & safety"),
        new("IE", "Information exchange"),
        new("MI", "Minutes of meeting"),
        new("MS", "Method statement"),
        new("PP", "Project programme"),
        new("PR", "Programme / schedule"),
        new("RD", "Room data sheet"),
        new("RI", "Request for information (RFI)"),
        new("RP", "Report"),
        new("SA", "Schedule of accommodation"),
        new("SN", "Snagging list"),
        new("SU", "Survey"),
        new("VS", "Visualization"),
        new("AC", "Agenda"),
        new("BQ", "Bill of quantities"),
        new("CT", "Contract"),
        new("DB", "Database"),
        new("MM", "Movie / animation"),
        new("PH", "Photograph"),
        new("PL", "Plan"),
    ];

    // ── Role codes (ISO 19650-2 Table A.2) — Discipline / functional role ────
    public static readonly List<CodeItem> Roles =
    [
        new("XX", "Not applicable / multiple"),
        new("A",  "Architect"),
        new("B",  "Building surveyor"),
        new("C",  "Civil engineer"),
        new("D",  "Drainage / highways engineer"),
        new("E",  "Electrical engineer"),
        new("F",  "Facilities manager"),
        new("G",  "Geotechnical / geographical"),
        new("H",  "Heating / ventilation engineer"),
        new("I",  "Interior designer"),
        new("K",  "Client"),
        new("L",  "Landscape architect"),
        new("M",  "Mechanical engineer"),
        new("P",  "Public health engineer"),
        new("Q",  "Quantity surveyor"),
        new("S",  "Structural engineer"),
        new("T",  "Town & country planner"),
        new("W",  "Contractor"),
        new("X",  "Subcontractor"),
        new("Y",  "Specialist designer"),
        new("Z",  "General / multiple"),
    ];

    // ── Suitability Status codes (ISO 19650-2 Table A.3) ──────────────────────
    public static readonly List<CodeItem> SuitabilityCodes =
    [
        // Work in Progress
        new("S0", "S0 - Initial status / work in progress"),
        // Shared (non-contractual)
        new("S1", "S1 - Suitable for coordination"),
        new("S2", "S2 - Suitable for information"),
        new("S3", "S3 - Suitable for internal review and comment"),
        new("S4", "S4 - Suitable for construction approval"),
        new("S6", "S6 - Suitable for PIM authorization"),
        new("S7", "S7 - Suitable for AIM authorization"),
        // Published (contractual)
        new("A1", "A1 - Authorised & accepted"),
        new("A2", "A2 - Authorised & accepted with comments"),
        new("A3", "A3 - Authorised for construction"),
        new("A4", "A4 - Authorised for manufacture / fabrication"),
        new("A5", "A5 - Authorised for tender"),
        // Published (contractual - as-built)
        new("AB", "AB - As-built record information"),
        // CDC / Client codes
        new("B1", "B1 - Partial sign off with minor comments"),
        new("B2", "B2 - Full sign off"),
        new("B3", "B3 - Rejected - do not use"),
        // Deprecated but still widely used
        new("CR", "CR - As constructed record"),
    ];

    // ── Revision codes (common formats) ───────────────────────────────────────
    public static readonly List<CodeItem> RevisionCodes =
    [
        new("P01", "P01 - Preliminary 01"),
        new("P02", "P02 - Preliminary 02"),
        new("P03", "P03 - Preliminary 03"),
        new("P04", "P04 - Preliminary 04"),
        new("P05", "P05 - Preliminary 05"),
        new("C01", "C01 - Contract 01"),
        new("C02", "C02 - Contract 02"),
        new("C03", "C03 - Contract 03"),
        new("A01", "A01 - As-built 01"),
        new("A02", "A02 - As-built 02"),
    ];

    // Helper to get the description for a given code
    public static string DescribeCode(List<CodeItem> list, string code)
        => list.FirstOrDefault(c => c.Code == code)?.Label ?? code;

    // ─────────────────────────────────────────────────────────────────────────
    // T-S9-02 reconciliation: HashSet accessors for fast Contains-checks +
    // Uniclass / IFC mappings + Uniclass-table-to-IFC-prefix rules.
    // Merged from the parked Services/Iso19650/Iso19650ReferenceData.cs which
    // itself is now deleted; Core is the single source of truth for ISO 19650
    // reference data. v1.1 / B-068 replaces the hard-coded Uniclass/IFC tables
    // with an NBS-backed feed; v1.1 / B-065 adds per-tenant overrides.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Volume codes as a HashSet for O(1) Contains-check.</summary>
    public static readonly HashSet<string> VolumeCodeSet =
        Volumes.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);

    /// <summary>Level codes as a HashSet for O(1) Contains-check.</summary>
    public static readonly HashSet<string> LevelCodeSet =
        Levels.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);

    /// <summary>Type codes as a HashSet for O(1) Contains-check.</summary>
    public static readonly HashSet<string> TypeCodeSet =
        DocumentTypes.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);

    /// <summary>Role codes as a HashSet for O(1) Contains-check.</summary>
    public static readonly HashSet<string> RoleCodeSet =
        Roles.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);

    /// <summary>Suitability codes as a HashSet for O(1) Contains-check.</summary>
    public static readonly HashSet<string> SuitabilityCodeSet =
        SuitabilityCodes.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);

    /// <summary>Type → Uniclass code. Hard-coded illustrative mapping per
    /// ISO 19650-2 Annex A. Full NBS-backed feed is v1.1 / B-068. Types
    /// absent from this map fail validator check 7 (mandatory Uniclass for
    /// object class). M3/M2 both map to the same model Uniclass since at
    /// the Annex A level "model" is a single category.</summary>
    public static readonly IReadOnlyDictionary<string, string> TypeToUniclass =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DR"] = "EF_25_30_20",  // Drawing
            ["M3"] = "Ss_25_30_20",  // 3D model
            ["M2"] = "Ss_25_30_20",  // 2D model — same Annex A category
            ["SP"] = "Ac_10_50_10",  // Specification
            ["CA"] = "Ac_10_50_40",  // Calculation
        };

    /// <summary>Deprecated Uniclass codes per the pinned reference. Empty in
    /// v1.0; v1.1 / B-068 populates from the NBS release.</summary>
    public static readonly HashSet<string> DeprecatedUniclassCodes =
        new(StringComparer.Ordinal);

    /// <summary>Type → IFC entity. Hard-coded illustrative mapping. Consumed
    /// by validator check 10 (cross-reference integrity); not a live IFC
    /// schema validator.</summary>
    public static readonly IReadOnlyDictionary<string, string> TypeToIfc =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DR"] = "IfcAnnotation",
            ["M3"] = "IfcBuildingElementProxy",
            ["M2"] = "IfcBuildingElementProxy",
            ["SP"] = "IfcDocumentReference",
            ["CA"] = "IfcDocumentReference",
        };

    /// <summary>Uniclass table prefix → allowed IFC entity prefixes. Drives
    /// the Type/Uniclass/IFC agreement rule in validator check 10.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> UniclassTableToIfcPrefixes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["EF"] = new[] { "IfcAnnotation" },   // Elements / Functions
            ["Ss"] = new[] { "IfcBuilding" },     // Systems
            ["Ac"] = new[] { "IfcDocument" },     // Activities
        };
}

public record CodeItem(string Code, string Label);
