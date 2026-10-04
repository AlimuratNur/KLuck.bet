using System.Net;
using System.Net.Http.Json;
using Microsoft.JSInterop;
using Polymarket.Client.Services;

namespace PolymarketApp.Services;

public record ProfileDto(string Address, string? Name, string? Emoji);
public record CommentDto(int Id, string Bet, string Author, string Text, long CreatedAt);
record TokenDto(string Token);
record CountDto(string Bet, int Count);

public class ApiService(IJSRuntime js, WalletService wallet)
{
    public record FaucetResult(string Signature, int Tokens, bool Sol);
    static readonly HttpClient http = new() { BaseAddress = new Uri(Chain.ApiUrl) };
    readonly Dictionary<string, ProfileDto> profiles = new();
    string? token, tokenOwner;

    public static string Short(string a) => a[..4] + "…" + a[^4..];

    // ---------- profiles ----------

    public ProfileDto Get(string address) =>
        profiles.TryGetValue(address, out var p) ? p : new(address, null, null);

    public string NameOf(string address)
    {
        var n = Get(address).Name;
        if (!string.IsNullOrWhiteSpace(n)) return n!;
        return address == wallet.Address ? "You" : Short(address);
    }

    public async Task LoadProfilesAsync(IEnumerable<string> addresses)
    {
        var list = addresses.Where(a => !string.IsNullOrEmpty(a)).Distinct().Take(100).ToList();
        if (list.Count == 0) return;
        try
        {
            var res = await http.GetFromJsonAsync<List<ProfileDto>>("api/profiles?addresses=" + string.Join(',', list));
            foreach (var p in res ?? new()) profiles[p.Address] = p;
        }
        catch { }
    }

    public async Task SaveProfileAsync(string name, string emoji)
    {
        var res = await SendAuth(HttpMethod.Put, "api/profile", new { name, emoji });
        res.EnsureSuccessStatusCode();
        profiles[wallet.Address!] = new(wallet.Address!, name, emoji);
    }

    // ---------- comments ----------

    // Returns null when the backend can't be reached.
    public async Task<List<CommentDto>?> GetCommentsAsync(string bet)
    {
        try { return await http.GetFromJsonAsync<List<CommentDto>>($"api/bets/{bet}/comments"); }
        catch { return null; }
    }

    public async Task<Dictionary<string, int>> GetCountsAsync()
    {
        try
        {
            var l = await http.GetFromJsonAsync<List<CountDto>>("api/comment-counts");
            return (l ?? new()).ToDictionary(x => x.Bet, x => x.Count);
        }
        catch { return new(); }
    }

    public async Task PostCommentAsync(string bet, string text)
    {
        var res = await SendAuth(HttpMethod.Post, $"api/bets/{bet}/comments", new { text });
        if (res.StatusCode == (HttpStatusCode)429) throw new Exception("Slow down a little.");
        res.EnsureSuccessStatusCode();
    }

    public async Task DeleteCommentAsync(int id)
    {
        var res = await SendAuth(HttpMethod.Delete, $"api/comments/{id}");
        res.EnsureSuccessStatusCode();
    }

    // ---------- sign-in ----------

    async Task<bool> EnsureSignedInAsync()
    {
        var addr = wallet.Address;
        if (addr is null) return false;
        if (token is not null && tokenOwner == addr) return true;

        token = await js.InvokeAsync<string?>("localStorage.getItem", $"kluck:token:{addr}");
        if (token is not null) { tokenOwner = addr; return true; }
        return await SignInAsync(addr);
    }

    async Task<bool> SignInAsync(string addr)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = await js.InvokeAsync<string>("wallet.signMessage",
            $"Sign in to KLuck.bet\nAddress: {addr}\nTime: {ts}");
        var res = await http.PostAsJsonAsync("api/auth", new { address = addr, timestamp = ts, signature = sig });
        if (!res.IsSuccessStatusCode) return false;
        var body = await res.Content.ReadFromJsonAsync<TokenDto>();
        token = body!.Token;
        tokenOwner = addr;
        await js.InvokeVoidAsync("localStorage.setItem", $"kluck:token:{addr}", token);
        return true;
    }

    async Task<HttpResponseMessage> SendAuth(HttpMethod method, string url, object? body = null)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!await EnsureSignedInAsync()) throw new Exception("Sign-in was cancelled or failed.");
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new("Bearer", token);
            if (body is not null) req.Content = JsonContent.Create(body);

            var res = await http.SendAsync(req);
            if (res.StatusCode != HttpStatusCode.Unauthorized) return res;

            // Session expired: forget it and sign in again.
            await js.InvokeVoidAsync("localStorage.removeItem", $"kluck:token:{tokenOwner}");
            token = null; tokenOwner = null;
        }
        throw new Exception("Could not sign in.");
    }
    public async Task<FaucetResult> ClaimFaucetAsync()
    {
        var res = await SendAuth(HttpMethod.Post, "api/faucet");
        if (!res.IsSuccessStatusCode)
            throw new Exception((await res.Content.ReadAsStringAsync()).Trim('"'));
        return (await res.Content.ReadFromJsonAsync<FaucetResult>())!;
    }
}