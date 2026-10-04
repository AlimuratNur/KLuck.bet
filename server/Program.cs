using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Solnet.Wallet;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solnet.Programs;
using Solnet.Rpc;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;

var builder = WebApplication.CreateBuilder(args);
var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "kluck.db";
builder.Services.AddDbContext<Db>(o => o.UseSqlite($"Data Source={dbPath}"));

var origins = (Environment.GetEnvironmentVariable("ALLOWED_ORIGINS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (origins.Length == 0) p.AllowAnyOrigin(); else p.WithOrigins(origins);
    p.AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();
app.UseCors();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Db>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw(
        "CREATE TABLE IF NOT EXISTS FaucetClaims (Address TEXT NOT NULL PRIMARY KEY, LastClaimAt INTEGER NOT NULL)");
}

static string Clip(string? s, int n) { s = (s ?? "").Trim(); return s.Length > n ? s[..n] : s; }
static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

async Task<string?> Me(HttpRequest req, Db db)
{
    var h = req.Headers.Authorization.ToString();
    if (!h.StartsWith("Bearer ")) return null;
    var token = h[7..];
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var s = await db.Sessions.FirstOrDefaultAsync(x => x.Token == token && x.ExpiresAt > now);
    return s?.Address;
}

// Sign in: the wallet signs a message, we verify it and hand out a session token.
app.MapPost("/api/auth", async (AuthRequest r, Db db) =>
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (Math.Abs(now - r.Timestamp) > 300) return Results.BadRequest("Signature expired, try again.");
    try
    {
        var msg = Encoding.UTF8.GetBytes($"Sign in to KLuck.bet\nAddress: {r.Address}\nTime: {r.Timestamp}");
        if (!new PublicKey(r.Address).Verify(msg, Convert.FromBase64String(r.Signature)))
            return Results.Unauthorized();
    }
    catch { return Results.BadRequest("Bad address or signature."); }

    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    db.Sessions.Add(new Session { Token = token, Address = r.Address, ExpiresAt = now + 7 * 86400 });
    await db.SaveChangesAsync();
    return Results.Ok(new { token });
});

// Profiles
app.MapGet("/api/profiles", async (string addresses, Db db) =>
{
    var list = addresses.Split(',', StringSplitOptions.RemoveEmptyEntries).Take(100).ToList();
    return await db.Profiles.Where(p => list.Contains(p.Address)).ToListAsync();
});

app.MapPut("/api/profile", async (ProfileBody d, HttpRequest req, Db db) =>
{
    var me = await Me(req, db);
    if (me is null) return Results.Unauthorized();
    var p = await db.Profiles.FindAsync(me);
    if (p is null) { p = new Profile { Address = me }; db.Profiles.Add(p); }
    p.Name = Clip(d.Name, 24);
    p.Emoji = Clip(d.Emoji, 8);
    await db.SaveChangesAsync();
    return Results.Ok(p);
});

// Comments
app.MapGet("/api/bets/{bet}/comments", async (string bet, Db db) =>
    await db.Comments.Where(c => c.Bet == bet).OrderBy(c => c.CreatedAt).Take(300).ToListAsync());

app.MapGet("/api/comment-counts", async (Db db) =>
    await db.Comments.GroupBy(c => c.Bet).Select(g => new { bet = g.Key, count = g.Count() }).ToListAsync());

app.MapPost("/api/bets/{bet}/comments", async (string bet, CommentBody d, HttpRequest req, Db db) =>
{
    var me = await Me(req, db);
    if (me is null) return Results.Unauthorized();
    if (bet.Length > 60) return Results.BadRequest("Bad bet address.");
    var text = Clip(d.Text, 500);
    if (text.Length == 0) return Results.BadRequest("Comment is empty.");

    var now = NowMs();
    var last = await db.Comments.Where(c => c.Author == me).MaxAsync(c => (long?)c.CreatedAt);
    if (last is not null && now - last < 2000) return Results.StatusCode(429);

    var c = new Comment { Bet = bet, Author = me, Text = text, CreatedAt = now };
    db.Comments.Add(c);
    await db.SaveChangesAsync();
    return Results.Ok(c);
});

app.MapDelete("/api/comments/{id:int}", async (int id, HttpRequest req, Db db) =>
{
    var me = await Me(req, db);
    if (me is null) return Results.Unauthorized();
    var c = await db.Comments.FindAsync(id);
    if (c is null) return Results.NotFound();
    if (c.Author != me) return Results.Forbid();
    db.Comments.Remove(c);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// ---------- Solana settings ----------
var solanaRpcUrl = Environment.GetEnvironmentVariable("SOLANA_RPC") ?? "https://api.devnet.solana.com";
var mintAddress = Environment.GetEnvironmentVariable("COLLATERAL_MINT") ?? "4aGqcQpUS9FSHwzz7nxAtFCHWaabwaz7TWed2hf2jzXa";
var solRpc = ClientFactory.GetClient(solanaRpcUrl);

// ---------- RPC proxy with short cache ----------
var rpcCache = new ConcurrentDictionary<string, (DateTime Exp, string Body)>();
var rpcTtl = new Dictionary<string, int>
{
    ["getProgramAccounts"] = 5,
    ["getAccountInfo"] = 3,
    ["getTokenAccountBalance"] = 3,
    ["getBalance"] = 3,
    ["getLatestBlockhash"] = 2
};
var rpcHttp = new HttpClient();

static string WithId(string body, JsonNode? id)
{
    var node = JsonNode.Parse(body)!;
    node["id"] = id;
    return node.ToJsonString();
}

app.MapPost("/rpc", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    if (body.Length > 20_000) return Results.BadRequest("Too large");

    JsonNode? root;
    try { root = JsonNode.Parse(body); } catch { return Results.BadRequest("Bad JSON"); }

    var method = root?["method"]?.GetValue<string>();
    if (method is null || !rpcTtl.TryGetValue(method, out var ttl))
        return Results.BadRequest("Method not allowed");

    var id = root!["id"]?.DeepClone();
    var key = method + "|" + root["params"]?.ToJsonString();

    if (rpcCache.TryGetValue(key, out var hit) && hit.Exp > DateTime.UtcNow)
        return Results.Content(WithId(hit.Body, id), "application/json");

    var resp = await rpcHttp.PostAsync(solanaRpcUrl, new StringContent(body, Encoding.UTF8, "application/json"));
    var text = await resp.Content.ReadAsStringAsync();
    if (resp.IsSuccessStatusCode && !text.Contains("\"error\""))
    {
        if (rpcCache.Count > 2000) rpcCache.Clear();
        rpcCache[key] = (DateTime.UtcNow.AddSeconds(ttl), text);
    }
    return Results.Content(text, "application/json");
});

// ---------- Faucet ----------
const long FaucetCooldownSeconds = 24 * 3600;
const ulong FaucetTokens = 100;           // test tokens per claim
const ulong FaucetSolLamports = 10_000_000; // 0.01 SOL, only if the wallet has almost none
var faucetLock = new SemaphoreSlim(1, 1);

static string Base58(byte[] data)
{
    const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    var n = new BigInteger(data, isUnsigned: true, isBigEndian: true);
    var sb = new StringBuilder();
    while (n > 0) { n = BigInteger.DivRem(n, 58, out var r); sb.Insert(0, alphabet[(int)r]); }
    foreach (var b in data) { if (b == 0) sb.Insert(0, '1'); else break; }
    return sb.ToString();
}

Solnet.Wallet.Account? faucet = null;
{
    var path = Environment.GetEnvironmentVariable("FAUCET_KEYPAIR_PATH");
    var json = Environment.GetEnvironmentVariable("FAUCET_KEYPAIR_JSON")
               ?? (path is not null && File.Exists(path) ? File.ReadAllText(path) : null);
    if (json is not null)
    {
        var bytes = JsonSerializer.Deserialize<int[]>(json)!.Select(i => (byte)i).ToArray();
        faucet = Solnet.Wallet.Account.FromSecretKey(Base58(bytes));
        Console.WriteLine($"Faucet wallet: {faucet.PublicKey.Key}");
    }
    else Console.WriteLine("Faucet is NOT configured (set FAUCET_KEYPAIR_PATH).");
}

app.MapPost("/api/faucet", async (HttpRequest req, Db db) =>
{
    var me = await Me(req, db);
    if (me is null) return Results.Unauthorized();
    if (faucet is null) return Results.BadRequest("The faucet is not configured on this server.");

    await faucetLock.WaitAsync();
    try
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claim = await db.FaucetClaims.FindAsync(me);
        if (claim is not null && now - claim.LastClaimAt < FaucetCooldownSeconds)
        {
            var mins = (FaucetCooldownSeconds - (now - claim.LastClaimAt)) / 60;
            return Results.BadRequest($"You already claimed. Try again in {mins / 60}h {mins % 60}m.");
        }

        var owner = new Solnet.Wallet.PublicKey(me);
        var mint = new Solnet.Wallet.PublicKey(mintAddress);
        var ata = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint);

        var bh = await solRpc.GetLatestBlockHashAsync();
        if (!bh.WasSuccessful) return Results.BadRequest("Solana is busy, try again.");

        var tb = new TransactionBuilder()
            .SetRecentBlockHash(bh.Result.Value.Blockhash)
            .SetFeePayer(faucet);

        var bal = await solRpc.GetBalanceAsync(me);
        var dripSol = bal.WasSuccessful && bal.Result.Value < 5_000_000;
        if (dripSol)
            tb.AddInstruction(SystemProgram.Transfer(faucet.PublicKey, owner, FaucetSolLamports));

        // Create the user's token account if it doesn't exist yet (idempotent), then mint.
        tb.AddInstruction(new TransactionInstruction
        {
            ProgramId = AssociatedTokenAccountProgram.ProgramIdKey.KeyBytes,
            Keys = new List<AccountMeta>
            {
                AccountMeta.Writable(faucet.PublicKey, true),
                AccountMeta.Writable(ata, false),
                AccountMeta.ReadOnly(owner, false),
                AccountMeta.ReadOnly(mint, false),
                AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false),
                AccountMeta.ReadOnly(TokenProgram.ProgramIdKey, false),
            },
            Data = new byte[] { 1 }
        });
        tb.AddInstruction(TokenProgram.MintTo(mint, ata, FaucetTokens * 1_000_000UL, faucet.PublicKey));

        var sent = await solRpc.SendTransactionAsync(tb.Build(faucet));
        if (!sent.WasSuccessful) return Results.BadRequest("Faucet transaction failed: " + sent.Reason);

        if (claim is null) db.FaucetClaims.Add(new FaucetClaim { Address = me, LastClaimAt = now });
        else claim.LastClaimAt = now;
        await db.SaveChangesAsync();

        return Results.Ok(new { signature = sent.Result, tokens = FaucetTokens, sol = dripSol });
    }
    finally { faucetLock.Release(); }
});


var port = Environment.GetEnvironmentVariable("PORT") ?? "5080";
app.Run($"http://0.0.0.0:{port}");

record AuthRequest(string Address, long Timestamp, string Signature);
record ProfileBody(string? Name, string? Emoji);
record CommentBody(string? Text);

class Db(DbContextOptions<Db> options) : DbContext(options)
{
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Session> Sessions => Set<Session>();
    
    public DbSet<FaucetClaim> FaucetClaims => Set<FaucetClaim>();
}

class Profile
{
    [Key] public string Address { get; set; } = "";
    public string Name { get; set; } = "";
    public string Emoji { get; set; } = "";
}

class Comment
{
    public int Id { get; set; }
    public string Bet { get; set; } = "";
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public long CreatedAt { get; set; }
}

class Session
{
    [Key] public string Token { get; set; } = "";
    public string Address { get; set; } = "";
    public long ExpiresAt { get; set; }
}

class FaucetClaim
{
    [Key] public string Address { get; set; } = "";
    public long LastClaimAt { get; set; }
}