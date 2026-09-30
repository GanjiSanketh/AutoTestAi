using System.Text.Json;
using AutoTestAi.Api.Configuration;
using AutoTestAi.Api.Endpoints;
using AutoTestAi.Api.Health;
using AutoTestAi.Api.Hubs;
using AutoTestAi.Api.Identity;
using AutoTestAi.Api.Middleware;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application;
using AutoTestAi.Infrastructure;
using AutoTestAi.Workflows;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ---------- structured logging ----------
if (builder.Environment.IsProduction())
    builder.Logging.AddJsonConsole(options => options.JsonWriterOptions = new JsonWriterOptions { Indented = false });

// ---------- options ----------
builder.Services.Configure<AuthenticationOptions>(
    builder.Configuration.GetSection(AuthenticationOptions.SectionName));
builder.Services.Configure<CorsOptions>(
    builder.Configuration.GetSection(CorsOptions.SectionName));
builder.Services.Configure<AutoTestAi.Application.AI.AiOptions>(
    builder.Configuration.GetSection(AutoTestAi.Application.AI.AiOptions.SectionName));
builder.Services.Configure<AutoTestAi.Application.TestExecution.ExecutionOptions>(
    builder.Configuration.GetSection(AutoTestAi.Application.TestExecution.ExecutionOptions.SectionName));
builder.Services.Configure<AutoTestAi.Application.TestExecution.WorkerOptions>(
    builder.Configuration.GetSection(AutoTestAi.Application.TestExecution.WorkerOptions.SectionName));
builder.Services.Configure<AutoTestAi.Application.ExecutionGrid.GridOptions>(
    builder.Configuration.GetSection(AutoTestAi.Application.ExecutionGrid.GridOptions.SectionName));
builder.Services.Configure<AutoTestAi.Application.AI.FailureAnalysisOptions>(
    builder.Configuration.GetSection(AutoTestAi.Application.AI.FailureAnalysisOptions.SectionName));
builder.Services.Configure<AutoTestAi.Application.Tickets.TicketOptions>(
    builder.Configuration.GetSection(AutoTestAi.Application.Tickets.TicketOptions.SectionName));
builder.Services.Configure<AutoTestAi.Application.Tickets.AutoTicketOptions>(
    builder.Configuration.GetSection(AutoTestAi.Application.Tickets.AutoTicketOptions.SectionName));
builder.Services.Configure<AutoTestAi.Infrastructure.Jira.JiraOptions>(
    builder.Configuration.GetSection(AutoTestAi.Infrastructure.Jira.JiraOptions.SectionName));
builder.Services.Configure<ObservabilityOptions>(
    builder.Configuration.GetSection(ObservabilityOptions.SectionName));

// ---------- layers (ADR-002 modular monolith) ----------
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWorkflows(builder.Configuration);

// ---------- API surface ----------
builder.Services.AddProblemDetails();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "AutoTest AI API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Keycloak JWT access token.",
    });
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer",
                },
            },
            Array.Empty<string>()
        },
    });
});

// ---------- CORS ----------
var allowedOrigins = builder.Configuration
    .GetSection(CorsOptions.SectionName)
    .Get<CorsOptions>()?.AllowedOrigins ?? ["http://localhost:5173"];
builder.Services.AddCors(options => options.AddPolicy("web", policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// ---------- auth (Keycloak/OIDC JWT validation, docs/06 §3) ----------
var authOptions = builder.Configuration
    .GetSection(AuthenticationOptions.SectionName)
    .Get<AuthenticationOptions>() ?? new AuthenticationOptions();
if (authOptions.Configured)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(jwt =>
        {
            jwt.Authority = authOptions.Authority;
            jwt.Audience = authOptions.Audience;
            jwt.RequireHttpsMetadata = authOptions.RequireHttpsMetadata;
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = !string.IsNullOrWhiteSpace(authOptions.Audience),
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
                NameClaimType = "name",
                RoleClaimType = "role",
            };
            // SignalR bearer token via query string.
            jwt.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                        context.Token = accessToken;
                    return Task.CompletedTask;
                }
            };
        });
}
else
{
    builder.Services.AddAuthentication();
    builder.Logging.AddFilter("AutoTestAi", LogLevel.Debug);
}
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, HttpCurrentUserService>();
builder.Services.AddScoped<AutoTestAi.Application.TestExecution.IExecutionEventPublisher, AutoTestAi.Api.Hubs.SignalRExecutionEventPublisher>();
builder.Services.AddHostedService<AutoTestAi.Api.Services.AutoTicketBackgroundService>();

// ---------- health (STEP 21) ----------
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<ValkeyHealthCheck>("valkey", tags: ["ready"])
    .AddCheck<MinioHealthCheck>("minio", tags: ["ready"])
    .AddCheck<TemporalHealthCheck>("temporal", tags: ["ready"]);

// ---------- observability foundation ----------
var observability = builder.Configuration
    .GetSection(ObservabilityOptions.SectionName)
    .Get<ObservabilityOptions>() ?? new ObservabilityOptions();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(observability.ServiceName))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(observability.OtlpEndpoint))
            tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(observability.OtlpEndpoint));
    });

var app = builder.Build();

// ---------- pipeline ----------
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(swagger => swagger.SwaggerEndpoint("/swagger/v1/swagger.json", "AutoTest AI API v1"));
}

app.UseCors("web");
app.UseAuthentication();
app.UseMiddleware<UserProvisioningMiddleware>();
app.UseAuthorization();

static Task WriteHealthAsJson(HttpContext context, Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
{
    context.Response.ContentType = "application/json";
    return context.Response.WriteAsJsonAsync(new
    {
        status = report.Status.ToString().ToLowerInvariant(),
        checks = report.Entries.Select(e => new
        {
            name = e.Key,
            status = e.Value.Status.ToString().ToLowerInvariant(),
            description = e.Value.Description,
        }),
    });
}

// Liveness: never fails because an external dependency is down.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = (_, _) => Task.CompletedTask,
}).AllowAnonymous();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).AllowAnonymous();

// Readiness: Unhealthy only when a CONFIGURED Postgres is unreachable;
// optional deps report Degraded (HTTP 200) so their absence is visible, not fatal.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
    ResponseWriter = WriteHealthAsJson,
}).AllowAnonymous();

app.MapV1Endpoints();
app.MapAuthEndpoints();
app.MapProjectEndpoints();
app.MapTestCaseEndpoints();
app.MapTestGenerationEndpoints();
app.MapExecutionEndpoints();
app.MapFailureAnalysisEndpoints();
app.MapDefectEndpoints();
app.MapTicketEndpoints();
app.MapAutoTicketEndpoints();
app.MapDashboardEndpoints();
app.MapExecutionGridEndpoints();
app.MapHub<ExecutionHub>("/hubs/execution");

if (!authOptions.Configured)
    app.Logger.LogWarning("Authentication is not configured (Authentication:Authority is empty). Running open for local Phase-0 development only.");

app.Run();

/// <summary>Exposes Program to integration tests (WebApplicationFactory).</summary>
public partial class Program;
