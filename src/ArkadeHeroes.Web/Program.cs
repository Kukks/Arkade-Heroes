using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ArkadeHeroes.Web;
using ArkadeHeroes.Client.Sdk;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The game server API. Override in wwwroot/appsettings.json ("ApiBaseUrl") for other hosts.
var apiBase = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5210";
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });

// The typed game SDK — the same transport the console client uses, running in the browser.
builder.Services.AddScoped(sp => new ArkadeHeroesClient(sp.GetRequiredService<HttpClient>()));

await builder.Build().RunAsync();
