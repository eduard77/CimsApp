using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Services.Iso19650;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CimsApp.Tests.Services.Documents;

/// <summary>
/// Behavioural tests for T-S9-03: ISO 19650 filename validator wired
/// into <see cref="DocumentsService.CreateAsync"/>. Strict-on-new-only
/// per the kickoff policy decision; checks 1-3 (Structure /
/// FieldValidity / Numbering) are blocking; checks 4 (Suitability) and
/// 6 (Revision) are skipped at create time because Suitability /
/// Revision attach to DocumentRevision rather than Document; checks
/// 7-10 (Uniclass / IFC / cross-reference) are deferred to v1.1 / B-068.
/// </summary>
public class DocumentIso19650ValidationTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId) BuildFixture()
    {
        var orgId     = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = userId, GlobalRole = UserRole.OrgAdmin,
        };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditInterceptor(tenant, httpAccessor: null))
            .Options;
        using var seed = new CimsDbContext(options, tenant);
        seed.Organisations.Add(new Organisation { Id = orgId, Name = "Org", Code = "OG" });
        seed.Users.Add(new User
        {
            Id = userId, Email = $"u-{Guid.NewGuid():N}@example.com",
            PasswordHash = "x", FirstName = "T", LastName = "U",
            OrganisationId = orgId,
        });
        seed.Projects.Add(new Project
        {
            Id = projectId, Name = "P", Code = "TP-1",
            AppointingPartyId = orgId, Currency = "GBP",
            Status = ProjectStatus.Execution,
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    private static DocumentsService NewSvc(DbContextOptions<CimsDbContext> options, StubTenantContext tenant)
    {
        var db = new CimsDbContext(options, tenant);
        return new DocumentsService(db, new AuditService(db), new Iso19650FilenameValidator());
    }

    private static CreateDocumentRequest GoodRequest(int number = 1) =>
        new(ProjectCode: "PRJ", Originator: "ABC",
            Volume: "ZZ", Level: "ZZ", DocType: "RP",
            Role: "XX", Number: number,
            Title: "Test", Description: null,
            Type: DocumentType.Report, ContainerId: null, Tags: null);

    [Fact]
    public async Task CreateAsync_with_canonical_metadata_succeeds()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);

        var doc = await svc.CreateAsync(projectId, GoodRequest(), userId, ip: null, ua: null);

        Assert.NotEqual(Guid.Empty, doc.Id);
        Assert.Equal("RP", doc.DocType);
        Assert.Equal("PRJ-ABC-ZZ-ZZ-RP-XX-0001", doc.DocumentNumber);
    }

    [Fact]
    public async Task CreateAsync_rejects_unknown_DocType_with_validator_message()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId,
                GoodRequest() with { DocType = "ZZ" },  // ZZ is not in TypeCodeSet
                userId, ip: null, ua: null));

        Assert.Contains(ex.Errors,
            e => e.StartsWith("ISO 19650 Field validity")
              && e.Contains("'ZZ' not recognised"));
    }

    [Fact]
    public async Task CreateAsync_rejects_unknown_Role_with_validator_message()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId,
                GoodRequest() with { Role = "QQ" },  // QQ is not in RoleCodeSet
                userId, ip: null, ua: null));

        Assert.Contains(ex.Errors,
            e => e.StartsWith("ISO 19650 Field validity")
              && e.Contains("'QQ' not recognised"));
    }

    [Fact]
    public async Task CreateAsync_rejects_reserved_number_0126()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId,
                GoodRequest(126),  // 0126 is the reserved/deprecated template
                userId, ip: null, ua: null));

        Assert.Contains(ex.Errors,
            e => e.StartsWith("ISO 19650 Numbering")
              && e.Contains("0126"));
    }

    [Fact]
    public async Task CreateAsync_accepts_valid_Annex_A_volume_codes()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);

        // "A" is a valid Block-A volume code per ISO 19650-2 Annex A.
        // Pre-S9 the validator's `^\d{2}$` regex would have rejected
        // it; post-S9 the whitelist accepts it.
        var doc = await svc.CreateAsync(projectId,
            GoodRequest() with { Volume = "A" },
            userId, ip: null, ua: null);

        Assert.Equal("PRJ-ABC-A-ZZ-RP-XX-0001", doc.DocumentNumber);
    }

    [Fact]
    public async Task CreateAsync_rejects_unknown_Volume_code()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId,
                GoodRequest() with { Volume = "QQ" },  // not in VolumeCodeSet
                userId, ip: null, ua: null));

        Assert.Contains(ex.Errors,
            e => e.StartsWith("ISO 19650 Field validity")
              && e.Contains("Volume not in"));
    }

    [Fact]
    public async Task CreateAsync_does_not_block_on_skipped_Suitability_check()
    {
        // The validator runs against a canonical filename built with
        // S0 + P01 placeholders; Suitability / Revision check outcomes
        // for placeholder values would be misleading at create time.
        // Verify the create succeeds when only those skipped checks
        // would have failed — i.e. the v1.0 strict-on-new-only policy
        // really only blocks on checks 1-3 (Structure / FieldValidity /
        // Numbering) and not on 4-6.
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);

        // GoodRequest() produces canonical metadata that passes 1-3.
        // Test passes if no exception is thrown.
        await svc.CreateAsync(projectId, GoodRequest(), userId, ip: null, ua: null);
    }
}
