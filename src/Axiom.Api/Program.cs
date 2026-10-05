using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Axiom.Api;
using Axiom.Api.Endpoints;
using Axiom.Api.Security;
using Axiom.Application;
using Axiom.Application.Policy;
using Axiom.Infrastructure;
using Axiom.Mcp;
using Axiom.Infrastructure.Catalog;
using Axiom.Infrastructure.Governance;
using Axiom.Infrastructure.Scm;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Callers are untrusted (design.md §14): bound what they can send.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
    kestrel.AddServerHeader = false;
});

builder.Services.AddAxiomApplication();
builder.Services.AddAxiomInfrastructure(builder.Configuration, "axiom-api");
builder.Services.AddGovernanceInfrastructure(builder.Configuration);
builder.Services.AddCatalogInfrastructure(builder.Configuration);
builder.Services.AddScmInfrastructure(builder.Configuration);
builder.Services.AddPolicyEngine();
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddAspNetCoreInstrumentation());
builder.Services.AddAxiomAuthentication(builder.Configuration, builder.Environment);

builder.Services.AddExceptionHandler<AxiomExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddOpenApi();

// MCP is the agent contract (ADR-0003). It is hosted here, behind the same authentication, rate limits
// and application use cases as REST. Stateless: every call is authorized on its own token.
builder.Services.AddMcpServer(server =>
{
    server.ServerInfo = new() { Name = "axiom", Title = "Axiom engineering governance", Version = "1.0.0" };
    server.ServerInstructions =
        "Call governance.preflight_change before designing or editing. A significant change needs governance.validate_design before implementation, "
        + "and governance.validate_diff on the actual commits afterwards. Treat REQUIRE_REVIEW and BLOCK as stop. A tool error is never ALLOW.";
}).WithHttpTransport(http => http.Stateless = true).AddAxiomTools();

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // Partition by tenant and caller so one agent session cannot starve another organization.
        var key = $"{context.User.FindFirst("org")?.Value}|{context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString()}";
        return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = builder.Configuration.GetValue("Axiom:RateLimit:Burst", 120),
            TokensPerPeriod = builder.Configuration.GetValue("Axiom:RateLimit:PerSecond", 20),
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

var app = builder.Build();

app.UseExceptionHandler();

// The portal is a static SPA built from src/Axiom.Portal. It is served same-origin only when a build
// directory is configured, so the API stays usable headless and no CORS surface is opened.
var portalPath = app.Configuration["Axiom:Portal:Path"];
if (!string.IsNullOrWhiteSpace(portalPath) && Directory.Exists(portalPath))
{
    var portalFiles = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(portalPath));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = portalFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = portalFiles });
}

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// Liveness says the process runs; readiness says its dependencies are reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
app.MapOpenApi().AllowAnonymous();

app.MapEvaluationEndpoints();
app.MapGovernanceEndpoints();
app.MapExceptionEndpoints();
app.MapMcp("/mcp");
app.MapAuthMetadata();

await app.RunAsync();

public partial class Program;
