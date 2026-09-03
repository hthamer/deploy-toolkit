using DeployToolkit.Api.Auth;
using DeployToolkit.Api.Deploy;
using DeployToolkit.Api.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

// ---------------------------------------------------------------------------
// DeployToolkit.RegistryApi — Phase 1 (plan §2.2 "central API").
//
// Minimal API host that secures the toolkit's central registry surface:
//   * POST /api/auth/authenticate — validates a username/password pair
//     against the ApiUsers credentials saved in the SAME registry database
//     the Packager app uses (EF Core / SQL Server; connection string from
//     appsettings.json). Deliberately TOKEN-FREE: no JWT, no bearer flow —
//     each request simply presents credentials that must match the stored
//     ones.
//   * PasswordRotationService — a hosted background service that replaces
//     the API users' password with a crypto-random one every 45 minutes.
//     Everything it needs lives IN THE DATABASE (user requirement): the
//     schedule and related settings are read from the ApiSettings table on
//     every cycle, and after each rotation the new credential is registered
//     in the ApiCredentialLogs table (latest row per username = the current
//     working password) — no appsettings.json secrets, no state file.
//
// The route/payload contract of /api/auth/authenticate is pinned by the
// WinForms clients (RegistryApiClient.AuthenticatePath in
// DeployToolkit.AppKit) — see AuthEndpoints.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// ---- registry database (same DB as the Packager app) ----------------------
builder.Services.AddRegistryDatabase(builder.Configuration);

// ---- auth services ---------------------------------------------------------
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddHostedService<PasswordRotationService>();

// ---- brute-force backstop on the login endpoint ----------------------------
// Fixed window per remote IP (default: 10 requests / minute). The Deployer's
// RegistryApiConnectionDialog issues a handful of logins per minute at most;
// anything above that from one address is either a stuck retry loop or an
// attack — both deserve a 429 instead of free PBKDF2 work.
var authPermitLimit = builder.Configuration.GetValue("Auth:RateLimit:PermitLimit", 10);
var authWindowSeconds = builder.Configuration.GetValue("Auth:RateLimit:WindowSeconds", 60);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authPermitLimit,
                Window = TimeSpan.FromSeconds(authWindowSeconds),
                QueueLimit = 0,
            }));
});

// ---- Swagger (Development convenience; not exposed in production) ----------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "DeployToolkit Registry API",
        Version = "v1",
        Description = "Central registry API for the DeployToolkit (Packager/Deployer). " +
                      "Phase 1: username/password authentication against the registry database " +
                      "(token-free by design) with scheduled password rotation.",
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "DeployToolkit Registry API v1"));
}

app.UseRateLimiter();

// Schema + settings + initial credential BEFORE the first request is
// accepted (the rotation service starts afterwards and reads its settings
// from the freshly seeded ApiSettings rows).
await app.InitializeRegistryDatabaseAsync();

// Plain health/identity probe for load balancers and smoke tests.
app.MapGet("/", () => Results.Ok(new
{
    service = "DeployToolkit.RegistryApi",
    phase = 1,
    status = "running",
    timeUtc = DateTimeOffset.UtcNow,
    endpoints = new[]
    {
        "POST /api/auth/authenticate",
        "POST /api/deploy (HTTP Basic credentials + deploy report)",
        "GET  /swagger (Development only)",
        "background: password rotation (settings: ApiSettings table, credentials: ApiCredentialLogs table)",
    },
}))
.WithTags("Health");

app.MapRegistryAuthEndpoints();
app.MapDeployEndpoints();

app.Run();
