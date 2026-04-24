using System.Text.RegularExpressions;

namespace CimsApp.Services.Iso19650;

/// <summary>
/// ISO 19650-2 filename validator. Covers 5 of 12 checks (PAFM Appendix F.9):
/// Structure, FieldValidity, Numbering, Suitability, Revision.
/// Remaining 7 are Sprint 8 scope.
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
        var checks = new List<Iso19650CheckOutcome>(5);

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
            checks.Add(Skipped(Iso19650CheckId.FieldValidity, "Field validity"));
            checks.Add(Skipped(Iso19650CheckId.Numbering,     "Numbering"));
            checks.Add(Skipped(Iso19650CheckId.Suitability,   "Suitability"));
            checks.Add(Skipped(Iso19650CheckId.Revision,      "Revision"));
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
        var typeOk  = Iso19650ReferenceData.TypeCodes.Contains(type);
        var roleOk  = Iso19650ReferenceData.RoleCodes.Contains(role);
        var volOk   = Regex.IsMatch(volume, "^\\d{2}$");
        var levelOk = Regex.IsMatch(level,  "^\\d{2}$");
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
        var suitOk = Iso19650ReferenceData.SuitabilityCodes.Contains(suitability);
        checks.Add(new Iso19650CheckOutcome(
            Iso19650CheckId.Suitability,
            "Suitability",
            suitOk,
            suitOk
                ? $"{suitability} accepted."
                : $"Suitability '{suitability}' not in v1 whitelist (S1-S7). A-/B-codes deferred to Sprint 8."));

        // Check 5: Revision - P## or C##.
        var revOk = RevisionPattern.IsMatch(revision);
        checks.Add(new Iso19650CheckOutcome(
            Iso19650CheckId.Revision,
            "Revision",
            revOk,
            revOk
                ? $"{revision} well-formed."
                : "Revision must match P## or C## (e.g. P03, C01)."));

        return new Iso19650FilenameValidationResult(input, checks);
    }

    private static Iso19650CheckOutcome Skipped(Iso19650CheckId id, string label) =>
        new Iso19650CheckOutcome(id, label, false, "Skipped - structure invalid.");

    private static string BuildFieldFailure(
        bool typeOk, bool roleOk, bool volOk, bool levelOk,
        string type, string role)
    {
        var problems = new List<string>();
        if (!volOk)   problems.Add("Volume must be 2 digits");
        if (!levelOk) problems.Add("Level must be 2 digits");
        if (!typeOk)  problems.Add($"Type '{type}' not recognised");
        if (!roleOk)  problems.Add($"Role '{role}' not recognised");
        return string.Join("; ", problems) + ".";
    }
}