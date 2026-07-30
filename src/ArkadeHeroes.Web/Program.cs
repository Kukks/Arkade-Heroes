using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ArkadeHeroes.Web;
using ArkadeHeroes.Web.Wallet;
using ArkadeHeroes.Client.Sdk;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Core.Services;
using NArk.Core.Wallet;
using NArk.Hosting;
using NArk.Storage.EfCore.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Logging.AddFilter("NArk", LogLevel.Information);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

// ── Game server API — the typed SDK, the same transport the console client uses ──
// EMPTY means same-origin, and that is the normal case now: the server hosts this bundle,
// so whoever served the app also serves /api and BaseAddress is simply where we loaded
// from. Set ApiBaseUrl only for the split deployment — a `dotnet run` frontend against a
// separately-run API — where the two really are different origins. Falling back to
// HostEnvironment.BaseAddress rather than a hardcoded localhost port means the bundle is
// correct wherever it is served from, with no configuration at all.
var apiBase = builder.Configuration["ApiBaseUrl"];
if (string.IsNullOrWhiteSpace(apiBase)) apiBase = builder.HostEnvironment.BaseAddress;
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });
builder.Services.AddScoped(sp => new ArkadeHeroesClient(sp.GetRequiredService<HttpClient>()));

// ── Non-custodial wallet, entirely in the browser (keys never leave the tab) ──
// The browser talks to arkd DIRECTLY, no relay, so this picks the endpoints the player's
// machine dials. Supplied at RUNTIME through wwwroot/appsettings.json (the container
// entrypoint rewrites it from $ARK_NETWORK) for the same reason ApiBaseUrl is: one
// published bundle has to serve regtest, mutinynet and mainnet, and the network is a
// deployment fact, not a compile-time one. Hardcoding it meant a deployed bundle pointed
// every visitor's wallet at http://localhost:7070.
//
// An unrecognised value THROWS rather than falling back. On a wallet, a silent default is
// the worst outcome available: a typo would quietly run mainnet keys against regtest — or
// the reverse — and the player only finds out once funds are somewhere they cannot reach.
var networkName = builder.Configuration["ArkNetwork"];
var networkConfig = (networkName ?? "regtest").Trim().ToLowerInvariant() switch
{
    "regtest" or "" => ArkNetworkConfig.Regtest,
    "mutinynet" => ArkNetworkConfig.Mutinynet,
    "mainnet" => ArkNetworkConfig.Mainnet,
    _ => throw new InvalidOperationException(
        $"Unknown ArkNetwork '{networkName}'. Use regtest, mutinynet or mainnet."),
};

// EF Core + SQLite persisted to browser storage (Cache API / IndexedDB) via Bit.Besql.
builder.Services.AddBesqlDbContextFactory<WalletDbContext>(options =>
    options.UseSqlite("Data Source=ArkadeHeroesWallet.db"));
builder.Services.AddArkEfCoreStorage<WalletDbContext>();

// NArk core services + the REST transport to arkd.
builder.Services.AddArkCoreServices();
builder.Services.AddArkRestTransport(networkConfig);

builder.Services.AddSingleton<IIntentScheduler, SimpleIntentScheduler>();
// The scheduler REQUIRES a renewal threshold — without it, it throws every intent-generation
// cycle and the wallet's VTXO auto-renewal never runs (VTXOs would eventually expire unspendable).
// Mirror the NArk sample wallet: re-board VTXOs approaching expiry.
builder.Services.Configure<NArk.Core.Models.Options.SimpleIntentSchedulerOptions>(opts =>
    opts.Threshold = TimeSpan.FromDays(1));
builder.Services.AddSingleton<ISafetyService, WasmSafetyService>();
// Esplora comes from the SAME network config as arkd above — it was a second hardcoded
// localhost, and leaving it behind would point the wallet's chain reads at the player's own
// machine while its Ark calls went to the real network. EsploraUri is optional on the record,
// so an entry without one is a configuration error rather than a silent no-chain wallet.
var esploraUri = networkConfig.EsploraUri
    ?? throw new InvalidOperationException(
        $"ArkNetwork '{networkName ?? "regtest"}' defines no EsploraUri, so the wallet has no chain source.");
builder.Services.AddSingleton<IBitcoinBlockchain>(_ =>
    new EsploraBlockchain(new Uri(esploraUri.TrimEnd('/') + "/")));
builder.Services.AddSingleton<IWalletProvider, DefaultWalletProvider>();
builder.Services.AddSingleton<IAssetManager, AssetManager>();

// The game's wallet facade + shared session state (the in-tab, non-custodial player wallet).
builder.Services.AddSingleton<ArkadeHeroes.Web.Wallet.GameWallet>();
builder.Services.AddSingleton<ArkadeHeroes.Web.Wallet.WalletState>();
// The Terms-of-Use gate's state. Singleton so the pending prompt is shared by whoever opened it (the Play
// button) and whoever renders it (the layout's TermsGate).
builder.Services.AddSingleton<ArkadeHeroes.Web.Wallet.TermsState>();
// Sign-in-with-wallet bridge to the game server — Scoped to use the SDK client (its bearer
// token, set on register/login, then persists app-wide in WASM's single scope).
builder.Services.AddScoped<ArkadeHeroes.Web.Wallet.GameSession>();

var host = builder.Build();

// Create the wallet DB on first launch, then start the SDK lifecycle manually (WASM has no IHostedService).
var dbFactory = host.Services.GetRequiredService<IDbContextFactory<WalletDbContext>>();
await using (var db = await dbFactory.CreateDbContextAsync())
    await db.Database.EnsureCreatedAsync();
await host.Services.StartArkServicesAsync();

await host.RunAsync();
