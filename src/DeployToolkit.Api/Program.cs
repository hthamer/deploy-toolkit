using System.Text;
using System.Threading.RateLimiting;
using DeployToolkit.Api.Auth;
using DeployToolkit.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

// ---------------------------------------------------------------------------
// DeployToolkit.RegistryApi — Phase 1 (plan §2.2 "central API").
//
// Minimal API host that secures the toolkit's central registry surface:
//   * POST /api/auth/authenticate — validates a username/password pair
//     against the ApiUsers credentials saved in the SAME registry database
//     the Packager app uses (EF Core / SQL Server; connection string from
//     appsettings.json), and returns a signed JWT access token on success.
//   * GET  /api/auth/me          — Bearer-token-protected sample endpoint
//     proving the credential model; the shape later protected endpoints
//     (e.g. POST /api/deploy, phase 2) will reuse.
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
builder.Services.AddSingleton<JwtTokenService>();

// Bearer authentication for everything below the login step. The
// JwtTokenService constructor validates Auth:Jwt (signing key length,
// issuer/audience) and round-trips a probe token — a misconfigured host
// fails at startup, not at the first login attempt.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetSection("Auth:Jwt");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt["Issuer"] ?? "DeployToolkit.RegistryApi",
            ValidateAudience = true,
            ValidAudience = jwt["Audience"] ?? "DeployToolkit.Clients",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwt["SigningKey"] ?? string.Empty)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });
builder.Services.AddAuthorization();

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
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "DeployToolkit Registry API",
        Version = "v1",
        Description = "Central registry API for the DeployToolkit (Packager/Deployer). " +
                      "Phase 1: username/password authentication against the registry database.",
    });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the accessToken returned by POST /api/auth/authenticate.",
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer",
                },
            },
            Array.Empty<string>()
        },
    });
});

var app = builder.Build();

// Fail fast if Auth:Jwt is misconfigured (missing/too-short signing key …).
_ = app.Services.GetRequiredService<JwtTokenService>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "DeployToolkit Registry API v1"));
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Schema + initial user BEFORE the first request is accepted.
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
        "GET  /api/auth/me (Bearer token required)",
        "GET  /swagger (Development only)",
    },
}))
.WithTags("Health");

app.MapRegistryAuthEndpoints();

app.Run();
