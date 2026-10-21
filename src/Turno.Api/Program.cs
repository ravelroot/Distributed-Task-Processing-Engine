using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Turno.Api.Endpoints;
using Turno.Api.Realtime;
using Turno.Api.Security;
using Turno.Contracts;
using Turno.Infrastructure;
using Turno.Infrastructure.Jobs;
using Turno.Infrastructure.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTurnoInfrastructure(builder.Configuration);
builder.Services.AddOptions<ApiOptions>().Bind(builder.Configuration.GetSection(ApiOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(options => TurnoJson.Configure(options.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSignalR().AddJsonProtocol(options => TurnoJson.Configure(options.PayloadSerializerOptions));
builder.Services.AddSingleton<LiveBroadcaster>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<LiveBroadcaster>());
builder.Services.AddHostedService<RecurringJobSeeder>();

var allowedOrigins = builder.Configuration.GetSection($"{ApiOptions.SectionName}:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));
}

builder.Services.AddTurnoMessaging(builder.Configuration, (bus, instance) =>
{
    bus.AddConsumer<JobStatusChangedConsumer>().Endpoint(endpoint => instance.Temporary(endpoint, "painel-tarefas"));
    bus.AddConsumer<WorkerStatusChangedConsumer>().Endpoint(endpoint => instance.Temporary(endpoint, "painel-workers"));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (allowedOrigins.Length > 0)
{
    app.UseCors();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseMiddleware<ApiKeyMiddleware>();

app.MapOpenApi("/openapi/{documentName}.json");
if (app.Configuration.GetValue($"{ApiOptions.SectionName}:EnableApiReference", true))
{
    app.MapScalarApiReference("/docs", options => options.WithTitle("Turno API"));
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(DependencyInjection.ReadyTag) || check.Tags.Contains("masstransit"),
    ResponseWriter = WriteHealthAsync
});

app.MapHub<JobsHub>("/hubs/jobs");
app.MapTurnoApi();
app.MapFallbackToFile("index.html");

await app.RunAsync();

static Task WriteHealthAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    var payload = new
    {
        status = report.Status.ToString(),
        checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description })
    };
    return context.Response.WriteAsync(JsonSerializer.Serialize(payload, TurnoJson.Options));
}

public partial class Program;
