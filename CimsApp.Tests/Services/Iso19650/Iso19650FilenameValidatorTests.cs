using CimsApp.Services.Iso19650;
using Xunit;

namespace CimsApp.Tests.Services.Iso19650;

public class Iso19650FilenameValidatorTests
{
    private readonly Iso19650FilenameValidator _sut = new();

    private const string ValidFilename = "RVP-SAG-02-04-DR-A-0127-S2-P03";
    private const string FailingFilename = "RVP-SAG-02-04-DR-A-0126-A3-P03";

    [Fact]
    public void Valid_filename_passes_all_implemented_checks()
    {
        var result = _sut.Validate(ValidFilename);

        Assert.True(result.IsValid,
            "All implemented checks should pass for the canonical example.");
        Assert.Equal(12, result.Checks.Count);
        Assert.All(result.Checks, c => Assert.True(c.Passed, c.Label));
    }

    [Fact]
    public void StateTransition_check_passes_with_deferred_message_in_v1()
    {
        var result = _sut.Validate(ValidFilename);

        var outcome = result.Checks.Single(c => c.Id == Iso19650CheckId.StateTransition);
        Assert.True(outcome.Passed);
        Assert.Contains("Deferred", outcome.Message);
    }

    [Fact]
    public void UniclassClassification_check_passes_for_type_with_mapping()
    {
        var result = _sut.Validate(ValidFilename);

        var outcome = result.Checks.Single(c => c.Id == Iso19650CheckId.UniclassClassification);
        Assert.True(outcome.Passed);
        Assert.Contains("EF_25_30_20", outcome.Message);
    }

    [Fact]
    public void UniclassHierarchy_check_passes_when_code_not_deprecated()
    {
        var result = _sut.Validate(ValidFilename);

        var outcome = result.Checks.Single(c => c.Id == Iso19650CheckId.UniclassHierarchy);
        Assert.True(outcome.Passed);
        Assert.Contains("live", outcome.Message);
    }

    [Fact]
    public void IfcSchema_check_returns_deferred_stub()
    {
        var result = _sut.Validate(ValidFilename);

        var outcome = result.Checks.Single(c => c.Id == Iso19650CheckId.IfcSchema);
        Assert.True(outcome.Passed);
        Assert.Contains("Deferred", outcome.Message);
    }

    [Fact]
    public void CrossReferenceIntegrity_check_passes_for_consistent_triple()
    {
        var result = _sut.Validate(ValidFilename);

        var outcome = result.Checks.Single(c => c.Id == Iso19650CheckId.CrossReferenceIntegrity);
        Assert.True(outcome.Passed);
        Assert.Contains("IfcAnnotation", outcome.Message);
        Assert.Contains("consistent", outcome.Message);
    }

    [Fact]
    public void MetadataCompleteness_check_returns_deferred_stub()
    {
        var result = _sut.Validate(ValidFilename);

        var outcome = result.Checks.Single(c => c.Id == Iso19650CheckId.MetadataCompleteness);
        Assert.True(outcome.Passed);
        Assert.Contains("Deferred", outcome.Message);
    }

    [Fact]
    public void AuditTrail_check_returns_deferred_stub()
    {
        var result = _sut.Validate(ValidFilename);

        var outcome = result.Checks.Single(c => c.Id == Iso19650CheckId.AuditTrail);
        Assert.True(outcome.Passed);
        Assert.Contains("Deferred", outcome.Message);
    }

    [Fact]
    public void Structure_check_fails_when_field_count_wrong()
    {
        var result = _sut.Validate("RVP-SAG-02-04-DR-A-0127-S2");  // 8 fields

        var structure = result.Checks.Single(c => c.Id == Iso19650CheckId.Structure);
        Assert.False(structure.Passed);
        Assert.Contains("8", structure.Message);
    }

    [Fact]
    public void FieldValidity_check_fails_on_unknown_type_code()
    {
        var result = _sut.Validate("RVP-SAG-02-04-ZZ-A-0127-S2-P03");

        var fieldValidity =
            result.Checks.Single(c => c.Id == Iso19650CheckId.FieldValidity);
        Assert.False(fieldValidity.Passed);
        Assert.Contains("ZZ", fieldValidity.Message);
    }

    [Fact]
    public void Numbering_check_fails_on_reserved_0126()
    {
        var result = _sut.Validate(FailingFilename);

        var numbering = result.Checks.Single(c => c.Id == Iso19650CheckId.Numbering);
        Assert.False(numbering.Passed);
        Assert.Contains("0126", numbering.Message);
    }

    [Fact]
    public void Suitability_check_accepts_A_code_after_T_S9_02_reconciliation()
    {
        // T-S9-02: post-reconciliation, A-codes (A1..A5/AB) are valid per
        // ISO 19650-2 Annex A. Pre-S9 the parked reference data only listed
        // S1..S7; S9 swapped to Core/Iso19650Codes which carries the full
        // standard set. A3 in particular is "Authorised for construction"
        // and a real-world workflow value. This test asserts the new
        // accepting behaviour.
        var result = _sut.Validate(FailingFilename);

        var suitability =
            result.Checks.Single(c => c.Id == Iso19650CheckId.Suitability);
        Assert.True(suitability.Passed,
            "A-codes should be accepted after T-S9-02 reconciliation.");
        Assert.Contains("A3", suitability.Message);
    }

    [Fact]
    public void Suitability_check_fails_on_truly_invalid_code()
    {
        // ZZ is not in the ISO 19650-2 Annex A suitability whitelist.
        var result = _sut.Validate("RVP-SAG-02-04-DR-A-0127-ZZ-P03");

        var suitability =
            result.Checks.Single(c => c.Id == Iso19650CheckId.Suitability);
        Assert.False(suitability.Passed);
        Assert.Contains("ZZ", suitability.Message);
    }

    [Fact]
    public void Revision_check_fails_on_malformed_revision()
    {
        var result = _sut.Validate("RVP-SAG-02-04-DR-A-0127-S2-XX3");

        var revision = result.Checks.Single(c => c.Id == Iso19650CheckId.Revision);
        Assert.False(revision.Passed);
    }
}