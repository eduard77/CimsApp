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

namespace CimsApp.Tests.Services.Bsa2022;

/// <summary>
/// Behavioural tests for T-S10-04 MOR + T-S10-05 Safety Case
/// Summary + T-S10-06 Golden Thread immutability.
/// </summary>
public class MorAndSafetyCaseTests
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
            IsHrb = true, HrbCategory = BsaHrbCategory.A,
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    [Fact]
    public async Task MorService_CreateAsync_assigns_sequential_MOR_numbers()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = new MorService(new CimsDbContext(options, tenant),
            new AuditService(new CimsDbContext(options, tenant)));

        var m1 = await svc.CreateAsync(projectId,
            new CreateMorRequest("Crane near-miss", "Lift overload alarm",
                MorSeverity.High, DateTime.UtcNow.AddDays(-1)),
            userId, null, null);
        var m2 = await svc.CreateAsync(projectId,
            new CreateMorRequest("Scaffold collapse", "Partial collapse south face",
                MorSeverity.Critical, DateTime.UtcNow.AddDays(-2)),
            userId, null, null);

        Assert.Equal("MOR-0001", m1.Number);
        Assert.Equal("MOR-0002", m2.Number);
    }

    [Fact]
    public async Task MorService_MarkReportedToBsrAsync_records_timestamp_and_reference()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new MorService(db, new AuditService(db));

        var mor = await svc.CreateAsync(projectId,
            new CreateMorRequest("Fall from height", "Worker fell 2m",
                MorSeverity.Critical, DateTime.UtcNow.AddHours(-3)),
            userId, null, null);
        var marked = await svc.MarkReportedToBsrAsync(projectId, mor.Id,
            new MarkMorReportedToBsrRequest("BSR-CASE-2026-0042"),
            userId, null, null);

        Assert.True(marked.ReportedToBsr);
        Assert.NotNull(marked.ReportedToBsrAt);
        Assert.Equal("BSR-CASE-2026-0042", marked.BsrReference);
    }

    [Fact]
    public async Task MorService_MarkReportedToBsrAsync_rejects_already_reported()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new MorService(db, new AuditService(db));
        var mor = await svc.CreateAsync(projectId,
            new CreateMorRequest("X", "y", MorSeverity.Low, DateTime.UtcNow),
            userId, null, null);
        await svc.MarkReportedToBsrAsync(projectId, mor.Id,
            new MarkMorReportedToBsrRequest(null), userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.MarkReportedToBsrAsync(projectId, mor.Id,
                new MarkMorReportedToBsrRequest("retry"),
                userId, null, null));
    }

    [Fact]
    public async Task SafetyCaseService_GenerateAsync_aggregates_BSA_2022_state()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        // Seed: 2 open Risks, 1 Rfi (open), 0 Actions, 1 unreported MOR,
        // 1 Drafting GW1 + 1 Decided GW2, 0 GT documents.
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Risks.AddRange(
                new CimsApp.Models.Risk { ProjectId = projectId, Title = "R1", Probability = 3, Impact = 3, Score = 9, Status = RiskStatus.Active },
                new CimsApp.Models.Risk { ProjectId = projectId, Title = "R2", Probability = 2, Impact = 2, Score = 4, Status = RiskStatus.Identified });
            db.Rfis.Add(new Rfi
            {
                ProjectId = projectId, RfiNumber = "RFI-001",
                Subject = "x", Description = "y",
                Status = RfiStatus.Open, Priority = Priority.Medium,
                RaisedById = userId,
            });
            db.MandatoryOccurrenceReports.Add(new MandatoryOccurrenceReport
            {
                ProjectId = projectId, Number = "MOR-0001", Title = "x",
                Description = "y", Severity = MorSeverity.Medium,
                OccurredAt = DateTime.UtcNow, ReporterId = userId,
            });
            db.GatewayPackages.AddRange(
                new GatewayPackage
                {
                    ProjectId = projectId, Number = "GW1-0001",
                    Type = GatewayType.Gateway1, Title = "Planning",
                    State = GatewayPackageState.Drafting, CreatedById = userId,
                },
                new GatewayPackage
                {
                    ProjectId = projectId, Number = "GW2-0001",
                    Type = GatewayType.Gateway2, Title = "Pre-con",
                    State = GatewayPackageState.Decided,
                    Decision = GatewayDecision.Approved, DecisionNote = "ok",
                    CreatedById = userId,
                });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new SafetyCaseService(db2);
        var dto = await svc.GenerateAsync(projectId);

        Assert.True(dto.IsHrb);
        Assert.Equal(BsaHrbCategory.A,                dto.HrbCategory);
        Assert.Equal(2,                                dto.OpenRisksCount);
        Assert.Equal(1,                                dto.OpenIssuesCount);
        Assert.Equal(1,                                dto.OpenMorsCount);
        Assert.Equal(1,                                dto.OpenGatewayPackagesCount);
        Assert.Equal(0,                                dto.GoldenThreadDocumentsCount);
        Assert.Equal(GatewayPackageState.Drafting,     dto.Gateway1State);
        Assert.Equal(GatewayPackageState.Decided,      dto.Gateway2State);
        Assert.Null(dto.Gateway3State);
    }

    [Fact]
    public async Task DocumentsService_AddToGoldenThreadAsync_blocks_subsequent_state_transition()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid docId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var doc = new Document
            {
                ProjectId = projectId,
                ProjectCode = "TP1", Originator = "ABC",
                DocType = "RP", Number = "0001",
                DocumentNumber = "TP1-ABC-ZZ-ZZ-RP-XX-0001",
                Title = "Safety case appendix", CreatorId = userId,
                CurrentState = CdeState.WorkInProgress,
            };
            db.Documents.Add(doc);
            db.SaveChanges();
            docId = doc.Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new DocumentsService(db2, new AuditService(db2),
            new Iso19650FilenameValidator());
        await svc.AddToGoldenThreadAsync(docId, projectId, userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.TransitionAsync(docId, projectId, CdeState.Shared,
                suitability: null, userId, UserRole.InformationManager,
                ip: null, ua: null));
    }

    [Fact]
    public async Task DocumentsService_AddToGoldenThreadAsync_rejects_already_marked_document()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid docId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var doc = new Document
            {
                ProjectId = projectId,
                ProjectCode = "TP1", Originator = "ABC",
                DocType = "RP", Number = "0001",
                DocumentNumber = "TP1-ABC-ZZ-ZZ-RP-XX-0001",
                Title = "X", CreatorId = userId,
            };
            db.Documents.Add(doc);
            db.SaveChanges();
            docId = doc.Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc = new DocumentsService(db2, new AuditService(db2),
            new Iso19650FilenameValidator());
        await svc.AddToGoldenThreadAsync(docId, projectId, userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.AddToGoldenThreadAsync(docId, projectId, userId, null, null));
    }

    [Fact]
    public async Task DocumentsService_ListGoldenThreadAsync_returns_only_GT_marked_active_docs()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid gtId;
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Documents.Add(new Document
            {
                ProjectId = projectId,
                ProjectCode = "TP1", Originator = "ABC",
                DocType = "RP", Number = "0001",
                DocumentNumber = "TP1-ABC-ZZ-ZZ-RP-XX-0001",
                Title = "Not in GT", CreatorId = userId,
            });
            var gt = new Document
            {
                ProjectId = projectId,
                ProjectCode = "TP1", Originator = "ABC",
                DocType = "RP", Number = "0002",
                DocumentNumber = "TP1-ABC-ZZ-ZZ-RP-XX-0002",
                Title = "In GT", CreatorId = userId,
            };
            db.Documents.Add(gt);
            db.SaveChanges();
            gtId = gt.Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc = new DocumentsService(db2, new AuditService(db2),
            new Iso19650FilenameValidator());
        await svc.AddToGoldenThreadAsync(gtId, projectId, userId, null, null);

        var rows = await svc.ListGoldenThreadAsync(projectId);

        Assert.Single(rows);
        Assert.Equal("In GT", rows[0].Title);
    }
}
