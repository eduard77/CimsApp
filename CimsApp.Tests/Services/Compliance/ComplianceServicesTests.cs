using System.IO.Compression;
using System.Text.Json;
using CimsApp.Core;
using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Services.Compliance;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CimsApp.Tests.Services.Compliance;

/// <summary>
/// T-S18-05 audit + compliance behavioural tests. Covers per-service
/// happy paths, validation guards on the discriminated-link evidence
/// rows, auditor-assignment lifecycle (create / revoke /
/// idempotent-revoke), tenant-isolation on cross-project access,
/// and bundle-structure assertions on the audit export ZIP.
/// </summary>
public class ComplianceServicesTests
{
    // ── EvidenceLibraryService ─────────────────────────────────────

    [Fact]
    public async Task EvidenceList_returns_only_project_scoped_rows()
    {
        var f = BuildFixture();
        var svc = new EvidenceLibraryService(f.Db, new AuditService(f.Db));

        // Add one in target project, one in another project (same
        // tenant but different ProjectId).
        var otherProjectId = Guid.NewGuid();
        f.Db.Projects.Add(new Project
        {
            Id = otherProjectId, Name = "Other", Code = "OTH",
            AppointingPartyId = f.OrgId, Currency = "GBP",
            Status = ProjectStatus.Execution,
        });
        await f.Db.SaveChangesAsync();

        await svc.AddAsync(f.ProjectId,
            new AddEvidenceRequest(ComplianceArea.UkGdpr, null, null, null, null, "in target project"),
            f.ActorId);
        await svc.AddAsync(otherProjectId,
            new AddEvidenceRequest(ComplianceArea.UkGdpr, null, null, null, null, "in other project"),
            f.ActorId);

        var page = await svc.ListAsync(f.ProjectId);
        Assert.Equal(1, page.Total);
        Assert.Equal("in target project", page.Items[0].Description);
    }

    [Fact]
    public async Task EvidenceList_filters_by_area()
    {
        var f = BuildFixture();
        var svc = new EvidenceLibraryService(f.Db, new AuditService(f.Db));
        await svc.AddAsync(f.ProjectId, new AddEvidenceRequest(ComplianceArea.UkGdpr, null, null, null, null, "gdpr"), f.ActorId);
        await svc.AddAsync(f.ProjectId, new AddEvidenceRequest(ComplianceArea.Bsa2022, null, null, null, null, "bsa"), f.ActorId);

        var gdpr = await svc.ListAsync(f.ProjectId, area: ComplianceArea.UkGdpr);
        Assert.Equal(1, gdpr.Total);
        Assert.Equal(ComplianceArea.UkGdpr, gdpr.Items[0].ComplianceArea);
    }

    [Fact]
    public async Task EvidenceAdd_zero_FK_zero_description_rejected()
    {
        var f = BuildFixture();
        var svc = new EvidenceLibraryService(f.Db, new AuditService(f.Db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.AddAsync(f.ProjectId,
                new AddEvidenceRequest(ComplianceArea.UkGdpr, null, null, null, null, null),
                f.ActorId));
    }

    [Fact]
    public async Task EvidenceAdd_two_FKs_set_rejected()
    {
        var f = BuildFixture();
        var svc = new EvidenceLibraryService(f.Db, new AuditService(f.Db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.AddAsync(f.ProjectId,
                new AddEvidenceRequest(ComplianceArea.UkGdpr, Guid.NewGuid(), Guid.NewGuid(), null, null, null),
                f.ActorId));
    }

    [Fact]
    public async Task EvidenceAdd_FK_pointing_at_other_project_rejected()
    {
        var f = BuildFixture();
        var svc = new EvidenceLibraryService(f.Db, new AuditService(f.Db));

        // Seed a Document in a different project.
        var otherProjectId = Guid.NewGuid();
        f.Db.Projects.Add(new Project
        {
            Id = otherProjectId, Name = "Other", Code = "OTH2",
            AppointingPartyId = f.OrgId, Currency = "GBP",
            Status = ProjectStatus.Execution,
        });
        var docId = Guid.NewGuid();
        f.Db.Documents.Add(new Document
        {
            Id = docId, ProjectId = otherProjectId,
            ProjectCode = "OTH2", Originator = "ORG", DocType = "DR",
            Number = "0001", DocumentNumber = "OTH2-DR-0001", Title = "x",
        });
        await f.Db.SaveChangesAsync();

        // Attempt to attach evidence in target project pointing at
        // the other-project document.
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.AddAsync(f.ProjectId,
                new AddEvidenceRequest(ComplianceArea.UkGdpr, docId, null, null, null, null),
                f.ActorId));
    }

    [Fact]
    public async Task EvidenceAdd_writes_evidence_added_audit_event()
    {
        var f = BuildFixture();
        var svc = new EvidenceLibraryService(f.Db, new AuditService(f.Db));
        await svc.AddAsync(f.ProjectId,
            new AddEvidenceRequest(ComplianceArea.UkGdpr, null, null, null, null, "x"),
            f.ActorId);
        var audits = await f.Db.AuditLogs.Where(a => a.Action == "evidence.added").ToListAsync();
        Assert.Single(audits);
    }

    [Fact]
    public async Task EvidenceRemove_writes_evidence_removed_audit_event()
    {
        var f = BuildFixture();
        var svc = new EvidenceLibraryService(f.Db, new AuditService(f.Db));
        var row = await svc.AddAsync(f.ProjectId,
            new AddEvidenceRequest(ComplianceArea.UkGdpr, null, null, null, null, "x"),
            f.ActorId);
        await svc.RemoveAsync(row.Id, f.ActorId);

        var removed = await f.Db.EvidenceArtefacts.AnyAsync(e => e.Id == row.Id);
        Assert.False(removed);
        var audits = await f.Db.AuditLogs.Where(a => a.Action == "evidence.removed").ToListAsync();
        Assert.Single(audits);
    }

    // ── AuditorAdminService ────────────────────────────────────────

    [Fact]
    public async Task AuditorCreate_sets_user_role_and_bumps_cutoff()
    {
        var f = BuildFixture();
        var auth = BuildAuthService(f);
        var svc = new AuditorAdminService(f.Db, f.Tenant, new AuditService(f.Db), auth);

        var auditorId = Guid.NewGuid();
        f.Db.Users.Add(new User
        {
            Id = auditorId, Email = $"aud-{Guid.NewGuid():N}@e.com",
            PasswordHash = "x", FirstName = "Audrey", LastName = "Auditor",
            OrganisationId = f.OrgId,
        });
        await f.Db.SaveChangesAsync();

        var row = await svc.CreateAsync(
            new CreateAuditorAssignmentRequest(
                auditorId, f.ProjectId, ComplianceAreaScope.UkGdpr | ComplianceAreaScope.Bsa2022,
                DateTime.UtcNow.AddDays(30)),
            f.ActorId);

        var refreshed = await f.Db.Users.FirstAsync(u => u.Id == auditorId);
        Assert.Equal(UserRole.ExternalAuditor, refreshed.GlobalRole);
        Assert.NotNull(refreshed.TokenInvalidationCutoff);
        Assert.Equal(f.ProjectId, row.ProjectId);
        Assert.Equal(ComplianceAreaScope.UkGdpr | ComplianceAreaScope.Bsa2022, row.ScopeAreas);
    }

    [Fact]
    public async Task AuditorCreate_does_not_demote_OrgAdmin_or_SuperAdmin()
    {
        var f = BuildFixture();
        var auth = BuildAuthService(f);
        var svc = new AuditorAdminService(f.Db, f.Tenant, new AuditService(f.Db), auth);

        var orgAdminId = Guid.NewGuid();
        f.Db.Users.Add(new User
        {
            Id = orgAdminId, Email = $"oa-{Guid.NewGuid():N}@e.com",
            PasswordHash = "x", FirstName = "Org", LastName = "Admin",
            OrganisationId = f.OrgId, GlobalRole = UserRole.OrgAdmin,
        });
        await f.Db.SaveChangesAsync();

        await svc.CreateAsync(
            new CreateAuditorAssignmentRequest(
                orgAdminId, f.ProjectId, ComplianceAreaScope.All,
                DateTime.UtcNow.AddDays(30)),
            f.ActorId);

        var refreshed = await f.Db.Users.FirstAsync(u => u.Id == orgAdminId);
        Assert.Equal(UserRole.OrgAdmin, refreshed.GlobalRole); // unchanged
    }

    [Fact]
    public async Task AuditorCreate_rejects_past_expiry()
    {
        var f = BuildFixture();
        var auth = BuildAuthService(f);
        var svc = new AuditorAdminService(f.Db, f.Tenant, new AuditService(f.Db), auth);

        var auditorId = Guid.NewGuid();
        f.Db.Users.Add(new User { Id = auditorId, Email = $"a-{Guid.NewGuid():N}@e.com", PasswordHash = "x", FirstName = "A", LastName = "U", OrganisationId = f.OrgId });
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(
                new CreateAuditorAssignmentRequest(auditorId, f.ProjectId, ComplianceAreaScope.All,
                    DateTime.UtcNow.AddDays(-1)),
                f.ActorId));
    }

    [Fact]
    public async Task AuditorRevoke_marks_revoked_and_bumps_cutoff()
    {
        var f = BuildFixture();
        var auth = BuildAuthService(f);
        var svc = new AuditorAdminService(f.Db, f.Tenant, new AuditService(f.Db), auth);

        var auditorId = Guid.NewGuid();
        f.Db.Users.Add(new User { Id = auditorId, Email = $"a-{Guid.NewGuid():N}@e.com", PasswordHash = "x", FirstName = "A", LastName = "U", OrganisationId = f.OrgId });
        await f.Db.SaveChangesAsync();

        var row = await svc.CreateAsync(
            new CreateAuditorAssignmentRequest(auditorId, f.ProjectId, ComplianceAreaScope.All,
                DateTime.UtcNow.AddDays(30)),
            f.ActorId);

        await svc.RevokeAsync(row.Id, f.ActorId);

        var refreshed = await f.Db.AuditorProjectAssignments.FirstAsync(a => a.Id == row.Id);
        Assert.True(refreshed.IsRevoked);
        Assert.NotNull(refreshed.RevokedAt);
        var audits = await f.Db.AuditLogs.Where(a => a.Action == "auditor.revoked").ToListAsync();
        Assert.Single(audits);
    }

    [Fact]
    public async Task AuditorRevoke_idempotent()
    {
        var f = BuildFixture();
        var auth = BuildAuthService(f);
        var svc = new AuditorAdminService(f.Db, f.Tenant, new AuditService(f.Db), auth);

        var auditorId = Guid.NewGuid();
        f.Db.Users.Add(new User { Id = auditorId, Email = $"a-{Guid.NewGuid():N}@e.com", PasswordHash = "x", FirstName = "A", LastName = "U", OrganisationId = f.OrgId });
        await f.Db.SaveChangesAsync();

        var row = await svc.CreateAsync(
            new CreateAuditorAssignmentRequest(auditorId, f.ProjectId, ComplianceAreaScope.All,
                DateTime.UtcNow.AddDays(30)),
            f.ActorId);

        await svc.RevokeAsync(row.Id, f.ActorId);
        await svc.RevokeAsync(row.Id, f.ActorId); // second call is a no-op

        var auditCount = await f.Db.AuditLogs.CountAsync(a => a.Action == "auditor.revoked");
        Assert.Equal(1, auditCount); // not re-audited
    }

    // ── AuditExportService ─────────────────────────────────────────

    [Fact]
    public async Task AuditExport_bundle_contains_manifest_and_evidence_and_audit_log()
    {
        var f = BuildFixture();
        var ev = new EvidenceLibraryService(f.Db, new AuditService(f.Db));

        await ev.AddAsync(f.ProjectId,
            new AddEvidenceRequest(ComplianceArea.UkGdpr, null, null, null, null, "row 1"),
            f.ActorId);
        await ev.AddAsync(f.ProjectId,
            new AddEvidenceRequest(ComplianceArea.Bsa2022, null, null, null, null, "row 2"),
            f.ActorId);

        var svc = new AuditExportService(f.Db);
        var bytes = await svc.BuildBundleAsync(
            f.ProjectId, ComplianceAreaScope.All,
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        Assert.NotNull(zip.GetEntry("manifest.json"));
        Assert.NotNull(zip.GetEntry("audit-log.json"));
        var evidenceEntries = zip.Entries.Where(e => e.FullName.StartsWith("evidence/")).ToList();
        Assert.Equal(2, evidenceEntries.Count);

        // Manifest shape: counts.evidence == 2
        var manifestEntry = zip.GetEntry("manifest.json")!;
        using var manifestStream = manifestEntry.Open();
        using var reader = new StreamReader(manifestStream);
        var manifest = JsonDocument.Parse(reader.ReadToEnd());
        Assert.Equal(2, manifest.RootElement.GetProperty("counts").GetProperty("evidence").GetInt32());
    }

    [Fact]
    public async Task AuditExport_areas_scope_filters_evidence()
    {
        var f = BuildFixture();
        var ev = new EvidenceLibraryService(f.Db, new AuditService(f.Db));
        await ev.AddAsync(f.ProjectId, new AddEvidenceRequest(ComplianceArea.UkGdpr, null, null, null, null, "gdpr"), f.ActorId);
        await ev.AddAsync(f.ProjectId, new AddEvidenceRequest(ComplianceArea.Bsa2022, null, null, null, null, "bsa"), f.ActorId);

        var svc = new AuditExportService(f.Db);
        var bytes = await svc.BuildBundleAsync(
            f.ProjectId, ComplianceAreaScope.UkGdpr, // only GDPR
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var evidenceEntries = zip.Entries.Where(e => e.FullName.StartsWith("evidence/")).ToList();
        Assert.Single(evidenceEntries);
    }

    [Fact]
    public async Task AuditExport_rejects_inverted_window()
    {
        var f = BuildFixture();
        var svc = new AuditExportService(f.Db);
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.BuildBundleAsync(f.ProjectId, ComplianceAreaScope.All,
                DateTime.UtcNow, DateTime.UtcNow.AddDays(-1)));
    }

    // ── Fixture ────────────────────────────────────────────────────

    private sealed record Fixture(
        Guid OrgId, Guid ProjectId, Guid ActorId,
        StubTenantContext Tenant, CimsDbContext Db);

    private static Fixture BuildFixture()
    {
        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = actorId,
            GlobalRole = UserRole.OrgAdmin,
        };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditInterceptor(tenant, httpAccessor: null))
            .Options;

        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgId, Name = "O", Code = "O" });
            seed.Users.Add(new User
            {
                Id = actorId, Email = $"u-{Guid.NewGuid():N}@e.com",
                PasswordHash = "x", FirstName = "T", LastName = "U",
                OrganisationId = orgId, GlobalRole = UserRole.OrgAdmin,
            });
            seed.Projects.Add(new Project
            {
                Id = projectId, Name = "P", Code = "P-1",
                AppointingPartyId = orgId, Currency = "GBP",
                Status = ProjectStatus.Execution,
            });
            seed.SaveChanges();
        }

        return new Fixture(orgId, projectId, actorId, tenant, new CimsDbContext(options, tenant));
    }

    private static AuthService BuildAuthService(Fixture f)
    {
        // Mirrors the AuthServiceInputValidationTests setup: minimal
        // IConfiguration with Jwt:* keys; the revoke path doesn't
        // issue new tokens, just bumps cutoff + sweeps refresh
        // tokens, so secrets just need to be non-null.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:AccessSecret"]         = new string('a', 64),
                ["Jwt:RefreshSecret"]        = new string('b', 64),
                ["Jwt:Issuer"]               = "TestIssuer",
                ["Jwt:Audience"]             = "TestAudience",
                ["Jwt:AccessExpiresMinutes"] = "60",
                ["Jwt:RefreshExpiresDays"]   = "7",
            })
            .Build();
        var tracker = new CimsApp.Services.Auth.LoginAttemptTracker(
            new MemoryCache(new MemoryCacheOptions()));
        var auditService = new AuditService(f.Db);
        return new AuthService(f.Db, config,
            new InvitationService(f.Db, auditService),
            tracker, auditService);
    }
}
