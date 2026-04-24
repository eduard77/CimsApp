using System.Security.Claims;
using CimsApp.Services.Tenancy;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CimsApp.Tests.Services.Tenancy;

public class HttpTenantContextTests
{
    [Fact]
    public void Returns_null_ids_when_no_http_context()
    {
        var sut = new HttpTenantContext(new HttpContextAccessor());

        Assert.Null(sut.OrganisationId);
        Assert.Null(sut.UserId);
    }

    [Fact]
    public void Returns_null_ids_for_unauthenticated_principal()
    {
        var accessor = FakeAccessor(new ClaimsPrincipal(new ClaimsIdentity()));
        var sut = new HttpTenantContext(accessor);

        Assert.Null(sut.OrganisationId);
        Assert.Null(sut.UserId);
    }

    [Fact]
    public void Reads_user_id_from_NameIdentifier_claim()
    {
        var userId = Guid.NewGuid();
        var accessor = FakeAccessor(PrincipalWith(
            (ClaimTypes.NameIdentifier, userId.ToString())));
        var sut = new HttpTenantContext(accessor);

        Assert.Equal(userId, sut.UserId);
    }

    [Fact]
    public void Reads_organisation_id_from_cims_org_claim()
    {
        var orgId = Guid.NewGuid();
        var accessor = FakeAccessor(PrincipalWith(
            (HttpTenantContext.OrganisationClaimType, orgId.ToString())));
        var sut = new HttpTenantContext(accessor);

        Assert.Equal(orgId, sut.OrganisationId);
    }

    [Fact]
    public void Returns_null_for_malformed_guid_claim()
    {
        var accessor = FakeAccessor(PrincipalWith(
            (ClaimTypes.NameIdentifier, "not-a-guid"),
            (HttpTenantContext.OrganisationClaimType, "also-not-a-guid")));
        var sut = new HttpTenantContext(accessor);

        Assert.Null(sut.UserId);
        Assert.Null(sut.OrganisationId);
    }

    private static IHttpContextAccessor FakeAccessor(ClaimsPrincipal user)
    {
        var ctx = new DefaultHttpContext { User = user };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static ClaimsPrincipal PrincipalWith(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)),
            authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }
}
