using CimsApp.Core;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// Pure-function tests for `DocumentNaming.Build` + `Validate`.
/// `Build` produces ISO 19650 document numbers like
/// `PROJ-ORIG-VOL-LVL-TYPE-ROLE-NNNN` from the form fields,
/// substituting placeholder codes ("ZZ" for missing volume / level,
/// "XX" for missing role) and stripping non-alphanumeric chars.
/// `Validate` returns the list of required-field errors.
/// </summary>
public class DocumentNamingTests
{
    // ── Build ────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_with_all_parts_concatenates_with_dashes_and_uppercases()
    {
        var result = DocumentNaming.Build(
            projectCode: "ProjA", originator: "Acme",
            volume: "v1", level: "L2",
            docType: "DR", role: "S",
            number: 42);
        // Build cleans (alphanumeric only) + uppercases each part.
        Assert.Equal("PROJA-ACME-V1-L2-DR-S-0042", result);
    }

    [Fact]
    public void Build_replaces_missing_volume_and_level_with_ZZ()
    {
        Assert.Equal("PROJ-ORIG-ZZ-ZZ-DR-XX-0001",
            DocumentNaming.Build("Proj", "Orig",
                volume: null, level: null,
                docType: "DR", role: null,
                number: 1));
        // Whitespace-only is also treated as missing.
        Assert.Equal("PROJ-ORIG-ZZ-ZZ-DR-XX-0001",
            DocumentNaming.Build("Proj", "Orig",
                volume: "   ", level: "",
                docType: "DR", role: "  ",
                number: 1));
    }

    [Fact]
    public void Build_strips_non_alphanumeric_characters()
    {
        // Punctuation, whitespace, and accents are stripped from
        // each cleaned part. Number is formatted separately and
        // doesn't go through Clean.
        Assert.Equal("PROJ123-ACMECO-ZZ-ZZ-DR-XX-0042",
            DocumentNaming.Build(
                projectCode: "Proj-123",
                originator: "Acme & Co.",
                volume: null, level: null,
                docType: "DR", role: null,
                number: 42));
    }

    [Fact]
    public void Build_pads_number_to_four_digits()
    {
        Assert.Contains("-0001", DocumentNaming.Build("P", "O", null, null, "T", null, 1));
        Assert.Contains("-0099", DocumentNaming.Build("P", "O", null, null, "T", null, 99));
        Assert.Contains("-9999", DocumentNaming.Build("P", "O", null, null, "T", null, 9999));
        // Above 9999 the format spills past 4 digits — same as
        // string.Format("D4"). No test assertion of the spill
        // because the spill is allowed by `D4` semantics; just
        // checking that 5-digit input survives without exception.
        var big = DocumentNaming.Build("P", "O", null, null, "T", null, 12345);
        Assert.Contains("-12345", big);
    }

    // ── Validate ────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_all_present_returns_empty_list()
    {
        Assert.Empty(DocumentNaming.Validate(
            projectCode: "PROJ", originator: "ORIG", docType: "DR", number: 1));
    }

    [Theory]
    [InlineData(null, "ORIG", "DR", 1, "ProjectCode is required")]
    [InlineData("",   "ORIG", "DR", 1, "ProjectCode is required")]
    [InlineData("  ", "ORIG", "DR", 1, "ProjectCode is required")]
    [InlineData("PROJ", null, "DR", 1, "Originator is required")]
    [InlineData("PROJ", "",   "DR", 1, "Originator is required")]
    [InlineData("PROJ", "ORIG", null, 1, "DocType is required")]
    [InlineData("PROJ", "ORIG", "",   1, "DocType is required")]
    public void Validate_missing_required_field_returns_specific_error(
        string? projectCode, string? originator, string? docType, int? number,
        string expected)
    {
        var errors = DocumentNaming.Validate(projectCode, originator, docType, number);
        Assert.Contains(expected, errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_invalid_number_returns_positive_integer_error(int? number)
    {
        var errors = DocumentNaming.Validate("PROJ", "ORIG", "DR", number);
        Assert.Contains("Number must be a positive integer", errors);
    }

    [Fact]
    public void Validate_collects_multiple_errors_in_one_pass()
    {
        // A request missing ProjectCode AND with number=0 should
        // produce two error strings — Validate is collect-not-fail-fast.
        var errors = DocumentNaming.Validate(
            projectCode: null, originator: "ORIG", docType: "DR", number: 0);
        Assert.Equal(2, errors.Count);
        Assert.Contains("ProjectCode is required", errors);
        Assert.Contains("Number must be a positive integer", errors);
    }
}
