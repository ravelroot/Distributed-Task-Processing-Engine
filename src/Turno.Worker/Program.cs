using Turno.Core.Handlers;
using Turno.Infrastructure;
using Turno.Samples;
using Turno.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTurnoInfrastructure(builder.Configuration);

if (builder.Configuration.GetValue("Turno:Samples:Enabled", true))
{
    builder.Services.AddJobHandlersFromAssembly(typeof(SampleHandlers).Assembly);
}

builder.Services.AddTurnoWorker(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
