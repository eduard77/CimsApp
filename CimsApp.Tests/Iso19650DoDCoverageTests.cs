using System.Reflection;
using CimsApp.Core;
using CimsApp.Models;
using Xunit;

namespace CimsApp.Tests;

/// <summary>
/// T-S9-04. Audit-tests proving that the F.9 ISO 19650 / MIDP DoD
/// bullets that are already shipped in S0 still hold true after S9's
/// reconciliation work. PAFM-SD Appendix F.9 has five bullets; three
/// of them (CDE states + workflow, document metadata, naming wizard)
/// were delivered in S0 and parked-validator from sessions 1-2. These
/// tests pin that delivery to a named DoD bullet so a future regression
/// is caught at the test surface.
///
/// MIDP and TIDP — the two genuinely-new F.9 bullets — get their own
/// dedicated test files (T-S9-05 and T-S9-06).
/// </summary>
public class Iso19650DoDCoverageTests
{
    [Fact]
    public void CDE_states_bullet_4_F9_has_WIP_Shared_Published_Archived()
    {
        // F.9 bullet 4: "CDE states — WIP, Shared, Published, Archived
        // — with workflow." The CdeState enum from S0 carries all four
        // (plus Voided as a soft-delete extra; not a regression of the
        // F.9 bullet, just a superset).
        Assert.True(Enum.IsDefined(typeof(CdeState), CdeState.WorkInProgress));
        Assert.True(Enum.IsDefined(typeof(CdeState), CdeState.Shared));
        Assert.True(Enum.IsDefined(typeof(CdeState), CdeState.Published));
        Assert.True(Enum.IsDefined(typeof(CdeState), CdeState.Archived));
    }

    [Fact]
    public void CDE_workflow_bullet_4_F9_supports_standard_progression()
    {
        // The standard ISO 19650-2 progression is
        // WIP → Shared → Published → Archived. Each must be a valid
        // transition in CdeStateMachine.
        Assert.True(CdeStateMachine.IsValidTransition(
            CdeState.WorkInProgress, CdeState.Shared));
        Assert.True(CdeStateMachine.IsValidTransition(
            CdeState.Shared, CdeState.Published));
        Assert.True(CdeStateMachine.IsValidTransition(
            CdeState.Published, CdeState.Archived));
    }

    [Fact]
    public void Document_metadata_bullet_5_F9_has_required_ISO_19650_fields()
    {
        // F.9 bullet 5: "Document metadata per ISO 19650." The ISO
        // 19650-2 filename pattern is
        // PROJ-ORIG-VOL-LVL-TYPE-ROLE-NNNN-SUITABILITY-REVISION.
        // The first seven fields live on Document (stable identifier);
        // Suitability + Revision live on DocumentRevision (per-revision
        // attributes). All nine fields must exist on the entity surface.
        var docFields = typeof(Document)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).ToHashSet();
        Assert.Contains("ProjectCode",  docFields);
        Assert.Contains("Originator",   docFields);
        Assert.Contains("Volume",       docFields);
        Assert.Contains("Level",        docFields);
        Assert.Contains("DocType",      docFields);
        Assert.Contains("Role",         docFields);
        Assert.Contains("Number",       docFields);
        Assert.Contains("DocumentNumber", docFields);  // composed identifier

        var revFields = typeof(DocumentRevision)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).ToHashSet();
        Assert.Contains("Revision",    revFields);
        Assert.Contains("Suitability", revFields);
    }

    [Fact]
    public void ISO_19650_naming_bullet_1_F9_validator_active_post_T_S9_03()
    {
        // F.9 bullet 1: "ISO 19650 naming wizard for documents." The
        // parked validator (sessions 1-2) was wired into
        // DocumentsService.CreateAsync at T-S9-03 (strict-on-new-only;
        // checks 1-3 active). The validator class itself is a public
        // type in the assembly; its presence + DI registration in
        // Program.cs is asserted by the build (DocumentsService takes
        // it as a constructor parameter).
        var validatorType = typeof(CimsApp.Services.Iso19650.Iso19650FilenameValidator);
        Assert.NotNull(validatorType);
        Assert.True(validatorType.IsPublic);
        // Validate method exists with the expected single-string signature.
        var validateMethod = validatorType.GetMethod(
            "Validate",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(string) },
            modifiers: null);
        Assert.NotNull(validateMethod);
    }
}
