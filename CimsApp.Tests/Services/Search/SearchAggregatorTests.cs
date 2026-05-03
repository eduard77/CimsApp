using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services.Audit;
using CimsApp.Services.Search;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CimsApp.Tests.Services.Search;

/// <summary>
/// T-S15-04 search behavioural tests. Covers per-provider hits,
/// cross-entity aggregation, types-filter, tenant isolation, and
/// wildcard escaping.
/// </summary>
public class SearchAggregatorTests
{
    [Fact]
    public async Task Empty_query_returns_no_hits()
    {
        var (db, projectId, _) = BuildFixture();
        var svc = BuildAggregator(db);
        var hits = await svc.SearchAsync(projectId, "");
        Assert.Empty(hits);

        hits = await svc.SearchAsync(projectId, "a"); // below min length
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Cross_entity_aggregation_orders_by_score_desc()
    {
        var (db, projectId, userId) = BuildFixture();
        SeedDoc(db, projectId, "DOC-001", "Foundation Plan", "structural foundation drawing");
        SeedRfi(db, projectId, userId, "RFI-001", "Foundation depth query", "Need clarification on foundation");
        SeedAction(db, projectId, userId, "Foundation snag", "outstanding work near foundation");
        await db.SaveChangesAsync();

        var svc = BuildAggregator(db);
        var hits = await svc.SearchAsync(projectId, "foundation");
        Assert.Equal(3, hits.Count);
        // All three score 4 (title hit 3 + body hit 1) — tied,
        // so the aggregator's secondary sort (alpha on Title) wins.
        // Titles: "DOC-001 — Foundation Plan" (D), "Foundation
        // snag" (F), "RFI-001 — Foundation depth query" (R).
        Assert.All(hits, h => Assert.Equal(4, h.Score));
        Assert.Equal("document", hits[0].EntityType);
        Assert.Equal("action",   hits[1].EntityType);
        Assert.Equal("rfi",      hits[2].EntityType);
    }

    [Fact]
    public async Task Types_filter_excludes_other_providers()
    {
        var (db, projectId, userId) = BuildFixture();
        SeedDoc(db, projectId, "DOC-001", "Foundation Plan", "x");
        SeedRfi(db, projectId, userId, "RFI-001", "Foundation depth", "x");
        await db.SaveChangesAsync();

        var svc = BuildAggregator(db);
        var hits = await svc.SearchAsync(projectId, "foundation",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rfi" });
        Assert.Single(hits);
        Assert.Equal("rfi", hits[0].EntityType);
    }

    [Fact]
    public async Task Tenant_isolation_excludes_other_orgs_data()
    {
        // Two orgs each own one project; seed a Risk titled
        // "Foundation risk" in BOTH. Tenant A's search must NOT
        // see tenant B's row.
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var projA = Guid.NewGuid();
        var projB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgA, UserId = userA, GlobalRole = UserRole.OrgAdmin,
        };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditInterceptor(tenant, httpAccessor: null))
            .Options;
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgA, Name = "A", Code = "A" });
            seed.Organisations.Add(new Organisation { Id = orgB, Name = "B", Code = "B" });
            seed.Users.Add(new User { Id = userA, Email = $"a-{Guid.NewGuid():N}@e.com", PasswordHash = "x", FirstName = "A", LastName = "U", OrganisationId = orgA });
            seed.Users.Add(new User { Id = userB, Email = $"b-{Guid.NewGuid():N}@e.com", PasswordHash = "x", FirstName = "B", LastName = "U", OrganisationId = orgB });
            seed.Projects.Add(new Project { Id = projA, Name = "PA", Code = "PA-1", AppointingPartyId = orgA, Currency = "GBP", Status = ProjectStatus.Execution });
            seed.Projects.Add(new Project { Id = projB, Name = "PB", Code = "PB-1", AppointingPartyId = orgB, Currency = "GBP", Status = ProjectStatus.Execution });
            seed.Risks.Add(new CimsApp.Models.Risk { ProjectId = projA, Title = "Foundation risk A", Description = "x", Probability = 3, Impact = 3 });
            seed.Risks.Add(new CimsApp.Models.Risk { ProjectId = projB, Title = "Foundation risk B", Description = "x", Probability = 3, Impact = 3 });
            seed.SaveChanges();
        }

        var db = new CimsDbContext(options, tenant); // tenant A
        var svc = BuildAggregator(db);
        // Tenant A searches; we expect ONLY org-A's risk row.
        var hits = await svc.SearchAsync(projA, "foundation");
        Assert.Single(hits);
        Assert.Contains("A", hits[0].Title);
        // Cross-tenant search by Tenant A against project B
        // returns nothing — neither the project filter (B not in
        // org A) nor the LIKE-against-project filter pass.
        var crossHits = await svc.SearchAsync(projB, "foundation");
        Assert.Empty(crossHits);
    }

    [Fact]
    public void EscapeLike_neutralises_sql_like_wildcards()
    {
        Assert.Equal(@"10\%",  SearchQueryEscape.EscapeLike("10%"));
        Assert.Equal(@"a\_b",  SearchQueryEscape.EscapeLike("a_b"));
        Assert.Equal(@"\[abc]", SearchQueryEscape.EscapeLike("[abc]"));
        Assert.Equal(@"\\path", SearchQueryEscape.EscapeLike(@"\path"));
        Assert.Equal("plain",  SearchQueryEscape.EscapeLike("plain"));
    }

    [Fact]
    public void ContainsPattern_wraps_with_percent()
    {
        Assert.Equal("%foo%", SearchQueryEscape.ContainsPattern("foo"));
        Assert.Equal(@"%10\%%", SearchQueryEscape.ContainsPattern("10%"));
    }

    [Fact]
    public async Task Provider_returns_hits_above_zero_score()
    {
        // Sanity check — every provider returns ≥ 1 score for an
        // unambiguous title hit.
        var (db, projectId, userId) = BuildFixture();
        SeedDoc(db, projectId, "DOC-001", "alpha", "x");
        SeedRfi(db, projectId, userId, "RFI-001", "alpha", "x");
        SeedAction(db, projectId, userId, "alpha", "x");
        SeedRisk(db, projectId, "alpha", "x");
        SeedChangeRequest(db, projectId, userId, "CR-001", "alpha", "x");
        await db.SaveChangesAsync();

        var svc = BuildAggregator(db);
        var hits = await svc.SearchAsync(projectId, "alpha");
        Assert.Equal(5, hits.Count);
        Assert.All(hits, h => Assert.True(h.Score > 0));
    }

    // ── Fixture helpers ──────────────────────────────────────────

    private static (CimsDbContext db, Guid projectId, Guid userId) BuildFixture()
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
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
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgId, Name = "O", Code = "O" });
            seed.Users.Add(new User
            {
                Id = userId, Email = $"u-{Guid.NewGuid():N}@e.com",
                PasswordHash = "x", FirstName = "T", LastName = "U",
                OrganisationId = orgId,
            });
            seed.Projects.Add(new Project
            {
                Id = projectId, Name = "P", Code = "P-1",
                AppointingPartyId = orgId, Currency = "GBP",
                Status = ProjectStatus.Execution,
            });
            seed.SaveChanges();
        }
        return (new CimsDbContext(options, tenant), projectId, userId);
    }

    private static SearchAggregatorService BuildAggregator(CimsDbContext db)
        => new(new ISearchProvider[]
        {
            new DocumentSearchProvider(db),
            new RfiSearchProvider(db),
            new ActionSearchProvider(db),
            new RiskSearchProvider(db),
            new ChangeRequestSearchProvider(db),
            new EarlyWarningSearchProvider(db),
            new CompensationEventSearchProvider(db),
        });

    private static void SeedDoc(CimsDbContext db, Guid projectId, string number, string title, string desc)
    {
        db.Documents.Add(new Document
        {
            ProjectId = projectId,
            ProjectCode = "P-1", Originator = "ORG", DocType = "DR",
            Number = "0001", DocumentNumber = number, Title = title,
            Description = desc,
        });
    }

    private static void SeedRfi(CimsDbContext db, Guid projectId, Guid userId, string number, string subject, string desc)
    {
        db.Rfis.Add(new Rfi
        {
            ProjectId = projectId,
            RfiNumber = number, Subject = subject, Description = desc,
            RaisedById = userId,
        });
    }

    private static void SeedAction(CimsDbContext db, Guid projectId, Guid userId, string title, string desc)
    {
        db.ActionItems.Add(new ActionItem
        {
            ProjectId = projectId,
            Title = title, Description = desc,
            CreatedById = userId,
        });
    }

    private static void SeedRisk(CimsDbContext db, Guid projectId, string title, string desc)
    {
        db.Risks.Add(new CimsApp.Models.Risk
        {
            ProjectId = projectId,
            Title = title, Description = desc,
            Probability = 3, Impact = 3,
        });
    }

    private static void SeedChangeRequest(CimsDbContext db, Guid projectId, Guid userId, string number, string title, string desc)
    {
        db.ChangeRequests.Add(new ChangeRequest
        {
            ProjectId = projectId,
            Number = number, Title = title, Description = desc,
            Category = ChangeRequestCategory.Scope,
            BsaCategory = BsaHrbCategory.NotApplicable,
            RaisedById = userId,
        });
    }
}
