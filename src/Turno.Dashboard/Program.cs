using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Turno.Dashboard;
using Turno.Dashboard.Services;

CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("pt-BR");
CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("pt-BR");

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ApiKeyState>();
builder.Services.AddScoped<TurnoApi>();
builder.Services.AddScoped<LiveConnection>();
builder.Services.AddScoped<Toasts>();
builder.Services.AddScoped<UiState>();

await builder.Build().RunAsync();
