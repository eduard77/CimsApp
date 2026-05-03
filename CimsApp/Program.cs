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
            // SR-S0-03 / ADR-0010: ASP.NET reads the role claim from
            // "cims:role" (CIMS GlobalRole) instead of the default
            // ClaimTypes.Role. Required for `[Authorize(Roles = ...)]`
            // gates to honour our enum-string roles. Side effect:
            // anything else that calls `User.IsInRole(...)` /
            // `FindFirst(ClaimTypes.Role)` will see only the cims:role
            // claim, not any standard-claim role. If federated SSO
            // lands (B-009), the IdP may emit `ClaimTypes.Role` and
            // those roles would silently be ignored — revisit then,
            // possibly via a claims-transformer that maps external
            // roles onto cims:role.
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
            // T-S14-02: SignalR over WebSocket / SSE cannot send a
            // bearer header — clients pass the access token as the
            // ?access_token=... query param when negotiating /hubs/*.
            // Move it onto ctx.Token before the rest of the JWT
            // pipeline runs. Standard HTTP requests are unaffected.
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken)
                    && path.StartsWithSegments("/hubs"))
                {
                    ctx.Token = accessToken;
                }
                return Task.CompletedTask;
            },
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

// ── SignalR (T-S14-02) ────────────────────────────────────────────────────────
// In-app notifications hub. Auth: JWT bearer; query-param token
// fallback wired via JwtBearerEvents.OnMessageReceived above.
builder.Services.AddSignalR();

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
builder.Services.AddScoped<RisksService>();
builder.Services.AddScoped<StakeholdersService>();
builder.Services.AddScoped<CommunicationsService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<LpsService>();
builder.Services.AddScoped<ChangeRequestService>();
builder.Services.AddScoped<ProcurementStrategyService>();
builder.Services.AddScoped<TenderPackagesService>();
builder.Services.AddScoped<TendersService>();
builder.Services.AddScoped<EvaluationService>();
builder.Services.AddScoped<EarlyWarningsService>();
builder.Services.AddScoped<CompensationEventsService>();
builder.Services.AddScoped<DashboardsService>();
builder.Services.AddScoped<ReportingService>();
builder.Services.AddScoped<CustomReportDefinitionsService>();
builder.Services.AddScoped<MidpService>();
builder.Services.AddScoped<TidpService>();
builder.Services.AddScoped<GatewayPackageService>();
builder.Services.AddScoped<MorService>();
builder.Services.AddScoped<SafetyCaseService>();
builder.Services.AddScoped<RopaService>();
builder.Services.AddScoped<DpiaService>();
builder.Services.AddScoped<SarService>();
builder.Services.AddScoped<DataBreachService>();
builder.Services.AddScoped<RetentionScheduleService>();
builder.Services.AddScoped<ImprovementRegisterService>();
builder.Services.AddScoped<LessonsLearnedService>();
builder.Services.AddScoped<OpportunityToImproveService>();
builder.Services.AddScoped<InspectionActivityService>();
builder.Services.AddScoped<CdeService>();
builder.Services.AddScoped<DocumentsService>();
builder.Services.AddScoped<RfiService>();
builder.Services.AddScoped<ActionsService>();
// T-S14-02 in-app notifications.
builder.Services.AddScoped<CimsApp.Services.Notifications.INotificationPusher,
    CimsApp.Services.Notifications.NotificationPusher>();

// T-S14-03 email pipeline. Singleton queue + scoped sender +
// hosted dispatcher. Email:Enabled = false (default) routes
// every send through NoopEmailSender — production deployments
// flip the flag and populate Email:Smtp.
builder.Services.Configure<CimsApp.Services.Email.EmailOptions>(
    builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<CimsApp.Services.Email.EmailQueue>();
var emailEnabled = builder.Configuration.GetValue<bool>("Email:Enabled");
if (emailEnabled)
{
    builder.Services.AddScoped<CimsApp.Services.Email.IEmailSender,
        CimsApp.Services.Email.SmtpEmailSender>();
}
else
{
    builder.Services.AddScoped<CimsApp.Services.Email.IEmailSender,
        CimsApp.Services.Email.NoopEmailSender>();
}
builder.Services.AddHostedService<CimsApp.Services.Email.EmailDispatcherHostedService>();

// T-S14-04 AlertRule + threshold evaluator.
builder.Services.AddScoped<AlertRuleService>();
builder.Services.AddHostedService<CimsApp.Services.Alerts.ThresholdEvaluatorHostedService>();

// T-S15-02 Search & Discovery. One ISearchProvider per
// searchable entity type; SearchAggregatorService fans out
// to all of them.
builder.Services.AddScoped<CimsApp.Services.Search.ISearchProvider, CimsApp.Services.Search.DocumentSearchProvider>();
builder.Services.AddScoped<CimsApp.Services.Search.ISearchProvider, CimsApp.Services.Search.RfiSearchProvider>();
builder.Services.AddScoped<CimsApp.Services.Search.ISearchProvider, CimsApp.Services.Search.ActionSearchProvider>();
builder.Services.AddScoped<CimsApp.Services.Search.ISearchProvider, CimsApp.Services.Search.RiskSearchProvider>();
builder.Services.AddScoped<CimsApp.Services.Search.ISearchProvider, CimsApp.Services.Search.ChangeRequestSearchProvider>();
builder.Services.AddScoped<CimsApp.Services.Search.ISearchProvider, CimsApp.Services.Search.EarlyWarningSearchProvider>();
builder.Services.AddScoped<CimsApp.Services.Search.ISearchProvider, CimsApp.Services.Search.CompensationEventSearchProvider>();
builder.Services.AddScoped<CimsApp.Services.Search.SearchAggregatorService>();

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
// T-S14-02 SignalR hub for in-app notifications. Auth-required;
// route prefix /hubs is what JwtBearerEvents.OnMessageReceived
// looks at to allow query-param tokens.
app.MapHub<CimsApp.Services.Notifications.NotificationsHub>("/hubs/notifications");
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