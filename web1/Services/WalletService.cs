using Microsoft.JSInterop;

namespace PolymarketApp.Services;

public class WalletService(IJSRuntime js)
{
    bool registered;
    public string? Address { get; private set; }
    public event Action? Changed;

    public async Task ConnectAsync()
    {
        Address = await js.InvokeAsync<string>("wallet.connect");
        if (!registered)
        {
            await js.InvokeVoidAsync("wallet.register", DotNetObjectReference.Create(this));
            registered = true;
        }
        Changed?.Invoke();
    }

    public async Task DisconnectAsync()
    {
        await js.InvokeVoidAsync("wallet.disconnect");
        Address = null;
        Changed?.Invoke();
    }

    [JSInvokable]
    public void OnAccountChanged(string? address)
    {
        Address = address;
        Changed?.Invoke();
    }
}