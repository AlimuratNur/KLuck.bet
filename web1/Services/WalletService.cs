using Microsoft.JSInterop;

namespace PolymarketApp.Services;

public class WalletService(IJSRuntime js)
{
    public string? Address { get; private set; }
    public event Action? Changed;

    public async Task ConnectAsync()
    {
        Address = await js.InvokeAsync<string>("wallet.connect");
        Changed?.Invoke();
    }

    public async Task DisconnectAsync()
    {
        await js.InvokeVoidAsync("wallet.disconnect");
        Address = null;
        Changed?.Invoke();
    }
}