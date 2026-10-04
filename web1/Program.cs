using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Polymarket.Client;
using Polymarket.Client.Services;
using PolymarketApp.Services;
using Solnet.Rpc;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
Polymarket.Client.Services.Chain.ApiUrl = builder.Configuration["ApiUrl"] ?? Polymarket.Client.Services.Chain.ApiUrl;
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
var rpcUrl = builder.Configuration["RpcUrl"] ?? (Chain.ApiUrl + "/rpc");
builder.Services.AddSingleton(ClientFactory.GetClient(rpcUrl));
builder.Services.AddScoped<WalletService>();
builder.Services.AddScoped<SolanaService>();
builder.Services.AddScoped<ApiService>();

await builder.Build().RunAsync();
