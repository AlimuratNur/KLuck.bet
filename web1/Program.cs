using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Polymarket.Client;
using PolymarketApp.Services;
using Solnet.Rpc;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton(ClientFactory.GetClient(Cluster.DevNet));
builder.Services.AddScoped<WalletService>();
builder.Services.AddScoped<SolanaService>();
builder.Services.AddScoped<ProfileService>();

await builder.Build().RunAsync();
