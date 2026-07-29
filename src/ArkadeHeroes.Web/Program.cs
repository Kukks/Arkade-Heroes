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
var apiBase = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5210";
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });
builder.Services.AddScoped(sp => new ArkadeHeroesClient(sp.GetRequiredService<HttpClient>()));

// ── Non-custodial wallet, entirely in the browser (keys never leave the tab) ──
// Local regtest arkd over REST — the browser talks to arkd directly, no relay.
var networkConfig = ArkNetworkConfig.Regtest;

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
builder.Services.AddSingleton<IBitcoinBlockchain>(_ =>
    new EsploraBlockchain(new Uri("http://localhost:3000/api/")));
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
