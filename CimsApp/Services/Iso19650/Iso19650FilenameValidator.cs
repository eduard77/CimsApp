using System.Text.RegularExpressions;

namespace CimsApp.Services.Iso19650;

/// <summary>
/// ISO 19650-2 filename validator. Implements PAFM Appendix F.9 checks.
/// v1.0 scope: 1-4 and 6 are real; 5, 9, 11, 12 are stubs deferred to
/// Session 3 / Sprint 8; 7, 8, 10 run against hard-coded reference data.
///
/// Expected pattern (9 fields, hyphen-separated):
///   Project-Originator-Volume-Level-Type-Role-Number-Suitability-Revision
/// Example: RVP-SAG-02-04-DR-A-0127-S2-P03
/// </summary>
public sealed class Iso19650FilenameValidator
{
    private const int ExpectedFieldCount = 9;

    // Revision: "P" (preliminary) or "C" (contractual) followed by 2 digits.
    private static readonly Regex RevisionPattern =
        new("^(P|C)\\d{2}$", RegexOptions.Compiled);

    public Iso19650FilenameValidationResult Validate(string filename)
    {
        var input = filename?.Trim() ?? string.Empty;
        var checks = new List<Iso19650CheckOutcome>(12);

        // Check 1: Structure - 9 hyphen-separated fields, none empty.
        var fields = input.Split('-');
        var structureOk = fields.Length == ExpectedFieldCount
            && fields.All(f => !string.IsNullOrWhiteSpace(f));
        checks.Add(new Iso19650CheckOutcome(
            Iso19650CheckId.Structure,
            "Structure",
            structureOk,
            structureOk
                ? "9 fields present."
                : $"Expected 9 hyphen-separated fields; got {fields.Length}."));

        // If structure failed, remaining checks cannot run meaningfully.
        if (!structureOk)
        {
            checks.Add(Skipped(Iso19650CheckId.FieldValidity,   "Field validity"));
            checks.Add(Skipped(Iso19650CheckId.Numbering,       "Numbering"));
            checks.Add(Skipped(Iso19650CheckId.Suitability,     "Suitability"));
            checks.Add(Skipped(Iso19650CheckId.StateTransition,        "State transition"));
            checks.Add(Skipped(Iso19650CheckId.Revision,               "Revision"));
            checks.Add(Skipped(Iso19650CheckId.UniclassClassification, "Uniclass classification"));
            checks.Add(Skipped(Iso19650CheckId.UniclassHierarchy,      "Uniclass hierarchy validity"));
            checks.Add(Skipped(Iso19650CheckId.IfcSchema,               "IFC schema (for models)"));
            checks.Add(Skipped(Iso19650CheckId.CrossReferenceIntegrity, "Cross-reference integrity"));
            checks.Add(Skipped(Iso19650CheckId.MetadataCompleteness,    "Metadata completeness"));
            checks.Add(Skipped(Iso19650CheckId.AuditTrail,              "Audit trail"));
            return new Iso19650FilenameValidationResult(input, checks);
        }

        var volume      = fields[2];
        var level       = fields[3];
        var type        = fields[4];
        var role        = fields[5];
        var number      = fields[6];
        var suitability = fields[7];
        var revision    = fields[8];

        // Check 2: Field validity - Type and Role from reference sets,
        // Volume and Level are 2-digit numerics.
        var typeOk  = CimsApp.Core.Iso19650Codes.TypeCodeSet.Contains(type);
        var roleOk  = CimsApp.Core.Iso19650Codes.RoleCodeSet.Contains(role);
        // Volume / Level use the ISO 19650-2 Annex A whitelists; the
        // pre-S9 "must be 2 digits" rule was wrong against the
        // standard which allows ZZ (not applicable), XX (all zones),
        // single-letter blocks (A-E for Volume), B1/B2/GF/RF/M for
        // Level, etc.
        var volOk   = CimsApp.Core.Iso19650Codes.VolumeCodeSet.Contains(volume);
        var levelOk = CimsApp.Core.Iso19650Codes.LevelCodeSet.Contains(level);
        var fieldOk = typeOk && roleOk && volOk && levelOk;
        checks.Add(new Iso19650CheckOutcome(
            Iso19650CheckId.FieldValidity,
            "Field validity",
            fieldOk,
            fieldOk
                ? "Type, Role, Volume, Level recognised."
                : BuildFieldFailure(typeOk, roleOk, volOk, levelOk, type, role)));

        // Check 3: Numbering - 4-digit zero-padded, and for v1 we reject
        // reserved ranges. PAFM demo dataset flags 0126 as deprecated.
        var numberOk = Regex.IsMatch(number, "^\\d{4}$") && number != "0126";
        checks.Add(new Iso19650CheckOutcome(
            Iso19650CheckId.Numbering,
            "Numbering",
            numberOk,
            numberOk
                ? "Number well-formed."
                : number == "0126"
                    ? "Number 0126 is a reserved/deprecated template."
                    : "Number must be a 4-digit zero-padded integer."));

        // Check 4: Suitability - must be in v1 S-code whitelist.
        var suitOk = CimsApp.Core.Iso19650Codes.SuitabilityCodeSet.Contains(suitability);
        checks.Add(new Iso19650CheckOutcome(
            Iso19650CheckId.Suitability,
            "Suitability",
            suitOk,
            suitOk
                ? $"{suitability} accepted."
                : $"Suitability '{suitability}' not in the ISO 19650-2 Annex A whitelist."));

        // Check 5: State transition. Stub - needs previous Suitability to compare.
        checks.Add(CheckStateTransition());

        // Check 6: Revision - P## or C##.
        var revOk = RevisionPattern.IsMatch(revision);
        checks.Add(new Iso19650CheckOutcome(
            Iso19650CheckId.Revision,
            "Revision",
            revOk,
            revOk
                ? $"{revision} well-formed."
                : "Revision must match P## or C## (e.g. P03, C01)."));

        // Check 7: Uniclass classification - does the Type code map to a
        // mandatory Uniclass code in the v1 hard-coded set?
        checks.Add(CheckUniclassClassification(type));

        // Check 8: Uniclass hierarchy validity - is the derived code still
        // live (not deprecated) in the pinned v1 reference?
        checks.Add(CheckUniclassHierarchy(type));

        // Check 9: IFC schema. Stub - schema validation needs the IFC file,
        // not just the filename.
        checks.Add(CheckIfcSchema());

        // Check 10: Cross-reference integrity - do Type, Uniclass class and
        // IFC entity agree? (PAFM F.9 #10: a wall-system deliverable should
        // not be classified as a ceiling.)
        checks.Add(CheckCrossReferenceIntegrity(type));

        // Check 11: Metadata completeness. Stub - metadata model lands in
        // Session 3.
        checks.Add(CheckMetadataCompleteness());

        // Check 12: Audit trail. Stub - audit subsystem lands in Session 3.
        checks.Add(CheckAuditTrail());

        return new Iso19650FilenameValidationResult(input, checks);
    }

    private static Iso19650CheckOutcome CheckStateTransition() =>
        new(Iso19650CheckId.StateTransition,
            "State transition",
            true,
            "Deferred: no previous Suitability to compare against. CDE state machine is Sprint 8 scope.");

    private static Iso19650CheckOutcome CheckUniclassClassification(string type)
    {
        if (CimsApp.Core.Iso19650Codes.TypeToUniclass.TryGetValue(type, out var uniclass))
        {
            return new Iso19650CheckOutcome(
                Iso19650CheckId.UniclassClassification,
                "Uniclass classification",
                true,
                $"Type '{type}' maps to Uniclass '{uniclass}' (hard-coded; BEP-driven mandatory-code check is Sprint 8).");
        }

        return new Iso19650CheckOutcome(
            Iso19650CheckId.UniclassClassification,
            "Uniclass classification",
            false,
            $"Type '{type}' has no mandatory Uniclass code in the v1 hard-coded set.");
    }

    private static Iso19650CheckOutcome CheckUniclassHierarchy(string type)
    {
        if (!CimsApp.Core.Iso19650Codes.TypeToUniclass.TryGetValue(type, out var uniclass))
        {
            return new Iso19650CheckOutcome(
                Iso19650CheckId.UniclassHierarchy,
                "Uniclass hierarchy validity",
                false,
                $"Cannot check hierarchy: Type '{type}' has no Uniclass code.");
        }

        if (CimsApp.Core.Iso19650Codes.DeprecatedUniclassCodes.Contains(uniclass))
        {
            return new Iso19650CheckOutcome(
                Iso19650CheckId.UniclassHierarchy,
                "Uniclass hierarchy validity",
                false,
                $"Uniclass code '{uniclass}' is deprecated in the pinned v1 reference.");
        }

        return new Iso19650CheckOutcome(
            Iso19650CheckId.UniclassHierarchy,
            "Uniclass hierarchy validity",
            true,
            $"Uniclass code '{uniclass}' is live in the pinned v1 reference (NBS feed is Sprint 8).");
    }

    private static Iso19650CheckOutcome CheckIfcSchema() =>
        new(Iso19650CheckId.IfcSchema,
            "IFC schema (for models)",
            true,
            "Deferred: schema validation needs the IFC file, not just the filename. Sprint 8 scope.");

    private static Iso19650CheckOutcome CheckCrossReferenceIntegrity(string type)
    {
        var hasUniclass = CimsApp.Core.Iso19650Codes.TypeToUniclass.TryGetValue(type, out var uniclass);
        var hasIfc = CimsApp.Core.Iso19650Codes.TypeToIfc.TryGetValue(type, out var ifc);
        if (!hasUniclass || !hasIfc)
        {
            return new Iso19650CheckOutcome(
                Iso19650CheckId.CrossReferenceIntegrity,
                "Cross-reference integrity",
                false,
                $"Type '{type}' is missing a Uniclass or IFC mapping in the v1 hard-coded set.");
        }

        // First 2 chars of a Uniclass code are the table prefix (EF, Ss, Ac...).
        var table = uniclass!.Length >= 2 ? uniclass[..2] : uniclass;
        if (!CimsApp.Core.Iso19650Codes.UniclassTableToIfcPrefixes.TryGetValue(
                table, out var allowedPrefixes))
        {
            return new Iso19650CheckOutcome(
                Iso19650CheckId.CrossReferenceIntegrity,
                "Cross-reference integrity",
                false,
                $"No Uniclass->IFC rule for table '{table}' in the v1 hard-coded set.");
        }

        var aligned = allowedPrefixes.Any(p => ifc!.StartsWith(p, StringComparison.Ordinal));
        return new Iso19650CheckOutcome(
            Iso19650CheckId.CrossReferenceIntegrity,
            "Cross-reference integrity",
            aligned,
            aligned
                ? $"Type '{type}' -> Uniclass '{uniclass}' ({table}) -> IFC '{ifc}' is consistent."
                : $"Type '{type}' -> Uniclass '{uniclass}' ({table}) -> IFC '{ifc}' is inconsistent: table '{table}' expects IFC entities starting with {{{string.Join(", ", allowedPrefixes)}}}.");
    }

    private static Iso19650CheckOutcome CheckMetadataCompleteness() =>
        new(Iso19650CheckId.MetadataCompleteness,
            "Metadata completeness",
            true,
            "Deferred: metadata model (author, status, date, related objects) lands in Session 3.");

    private static Iso19650CheckOutcome CheckAuditTrail() =>
        new(Iso19650CheckId.AuditTrail,
            "Audit trail",
            true,
            "Deferred: immutable-timestamp audit subsystem lands in Session 3.");

    private static Iso19650CheckOutcome Skipped(Iso19650CheckId id, string label) =>
        new Iso19650CheckOutcome(id, label, false, "Skipped - structure invalid.");

    private static string BuildFieldFailure(
        bool typeOk, bool roleOk, bool volOk, bool levelOk,
        string type, string role)
    {
        var problems = new List<string>();
        if (!volOk)   problems.Add("Volume not in ISO 19650-2 Annex A whitelist");
        if (!levelOk) problems.Add("Level not in ISO 19650-2 Annex A whitelist");
        if (!typeOk)  problems.Add($"Type '{type}' not recognised");
        if (!roleOk)  problems.Add($"Role '{role}' not recognised");
        return string.Join("; ", problems) + ".";
    }
}