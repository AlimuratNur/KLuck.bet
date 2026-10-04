using Microsoft.JSInterop;

namespace PolymarketApp.Services;

public class ProfileService(IJSRuntime js)
{
    public string? Name { get; private set; }
    public string? Emoji { get; private set; }

    static string K(string address, string field) => $"kluck:{address}:{field}";

    public async Task LoadAsync(string address)
    {
        Name = await js.InvokeAsync<string?>("localStorage.getItem", K(address, "name"));
        Emoji = await js.InvokeAsync<string?>("localStorage.getItem", K(address, "emoji"));
    }

    public async Task SaveAsync(string address, string? name, string? emoji)
    {
        await js.InvokeVoidAsync("localStorage.setItem", K(address, "name"), name ?? "");
        await js.InvokeVoidAsync("localStorage.setItem", K(address, "emoji"), emoji ?? "");
        Name = name;
        Emoji = emoji;
    }
}