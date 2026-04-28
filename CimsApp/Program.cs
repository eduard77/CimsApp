using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using CimsApp.Core;
using CimsApp.Data;
using CimsApp.Middleware;
using CimsApp.Services;
using CimsApp.UI;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddScoped<CimsApp.Services.Tenancy.ITenantContext, CimsApp.Services.Tenancy.HttpTenantContext>();
builder.Services.AddScoped<CimsApp.Services.Audit.AuditInterceptor>();
builder.Services.AddDbContext<CimsDbContext>((sp, o) =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"))
     .AddInterceptors(sp.GetRequiredService<CimsApp.Services.Audit.AuditInterceptor>()));
builder.Services.AddScoped<IProjectProvisioningService, ProjectProvisioningService>();
builder.Services.AddScoped<CimsApp.Services.Iso19650.Iso19650FilenameValidator>();

// ── JWT Auth ──────────────────────────────────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:AccessSecret"]!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer           = true,  ValidIssuer   = builder.Configuration["Jwt:Issuer"],
            ValidateAudience         = true,  ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime         = true,  ClockSkew     = TimeSpan.Zero,
            RoleClaimType            = CimsApp.Services.Tenancy.HttpTenantContext.GlobalRoleClaimType,
        };
        // B-001: per-user revocation hook. After JWT signature /
        // issuer / audience / lifetime pass, look up the User and
        // run the TokenRevocation rules (inactive user, or token
        // issued before TokenInvalidationCutoff). The pure rule
        // logic lives in `CimsApp.Services.Auth.TokenRevocation`
        // for unit testing.
        o.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var sub = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(sub, out var userId))
                {
                    ctx.Fail("missing user id claim");
                    return;
                }
                var db = ctx.HttpContext.RequestServices.GetRequiredService<CimsDbContext>();
                var user = await db.Users.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(u => u.Id == userId);
                var iat = (ctx.SecurityToken as System.IdentityModel.Tokens.Jwt.JwtSecurityToken)
                    ?.IssuedAt
                    ?? DateTime.MinValue;
                if (CimsApp.Services.Auth.TokenRevocation.IsRevoked(user, iat))
                {
                    ctx.Fail("token revoked");
                }
            }
        };
    });
builder.Services.AddAuthorization();

// ── Rate limiting (B-002) ─────────────────────────────────────────────────────
// Per-IP fixed-window limits on the four anonymous endpoints. Two
// policies — "anon-login" is tighter because it's the credential-testing
// target; "anon-default" covers register / refresh / organisation
// creation. Authenticated routes are NOT rate-limited at this layer
// (cross-cutting hardening for those is B-001 / a future per-user
// throttle). 429 is returned with Retry-After.
//
// CAPTCHA / email-verification on organisation creation are NOT in this
// task — separate v1.1 items if pre-customer onboarding warrants them.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("anon-login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit       = 5,
                Window            = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit        = 0,
            }));

    options.AddPolicy("anon-default", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit       = 10,
                Window            = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit        = 0,
            }));
});

// ── API controllers ───────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

// ── Blazor Server ─────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddHttpClient("Self", client =>
{
    client.BaseAddress = new Uri("https://localhost:55069");
})
// ── HttpClient pointing at self (Blazor calls its own API) ────────────────────
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
});
builder.Services.AddHttpContextAccessor();

// ── App Services ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<AuditService>();
// B-002 progressive back-off: tracker holds the per-IP failed-attempt
// counter in IMemoryCache. Singleton because the cache state must
// persist across requests.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<CimsApp.Services.Auth.ILoginAttemptTracker,
    CimsApp.Services.Auth.LoginAttemptTracker>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<ProjectsService>();
builder.Services.AddScoped<CostService>();
builder.Services.AddScoped<VariationsService>();
builder.Services.AddScoped<PaymentCertificatesService>();
builder.Services.AddScoped<CdeService>();
builder.Services.AddScoped<DocumentsService>();
builder.Services.AddScoped<RfiService>();
builder.Services.AddScoped<ActionsService>();

// ── Blazor UI Services ────────────────────────────────────────────────────────
builder.Services.AddScoped<UiStateService>();
builder.Services.AddScoped<BlazorApiClient>();

// ── Swagger ───────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "CIMS API", Version = "v1", Description = "ISO 19650 + PMBOK 7" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Name = "Authorization", Type = SecuritySchemeType.ApiKey, Scheme = "Bearer", BearerFormat = "JWT", In = ParameterLocation.Header });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [] });
});

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseMiddleware<ErrorHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "CIMS API v1"); c.RoutePrefix = "swagger"; });
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();    // B-002 — must follow Authentication so policies
                         // can partition on IP after the connection is set.
app.MapControllers();
app.MapRazorComponents<CimsApp.Components.App>()
   .AddInteractiveServerRenderMode();

// ── Health check ──────────────────────────────────────────────────────────────
app.MapGet("/health", async (CimsDbContext db) =>
{
    try { await db.Database.ExecuteSqlRawAsync("SELECT 1"); return Results.Ok(new { status = "ok" }); }
    catch { return Results.StatusCode(503); }
});

// ── Auto-migrate on startup ───────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CimsDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
/* admin@adi.com
 * Password123!*/