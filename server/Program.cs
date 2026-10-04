using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Solnet.Wallet;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<Db>(o => o.UseSqlite("Data Source=kluck.db"));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<Db>().Database.EnsureCreated();

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

app.Run("http://localhost:5080");

record AuthRequest(string Address, long Timestamp, string Signature);
record ProfileBody(string? Name, string? Emoji);
record CommentBody(string? Text);

class Db(DbContextOptions<Db> options) : DbContext(options)
{
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Session> Sessions => Set<Session>();
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