using System.Globalization;
using System.Numerics;
using System.Text;
using Solnet.Wallet;

namespace PolymarketApp.Services;

// Status: 0 = open, 1 = resolved, 2 = cancelled. Winner: 0 = YES, 1 = NO.
public record BetInfo(
    string Address, string Creator, string Resolver, string Mint, string Vault,
    ulong BetId, string Title, long CloseTime,
    ulong YesPool, ulong NoPool, int Status, int? Winner)
{
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public ulong Total => YesPool + NoPool;
    public double YesPct => Total == 0 ? 50 : 100.0 * YesPool / Total;
    public string YesPctText => YesPct.ToString("F0", CultureInfo.InvariantCulture);
    public string NoPctText => (100 - Math.Round(YesPct)).ToString("F0", CultureInfo.InvariantCulture);
    public string YesWidth => YesPct.ToString("F1", CultureInfo.InvariantCulture);

    public bool CanJoin => Status == 0 && Now < CloseTime;
    public bool WaitingForResult => Status == 0 && Now >= CloseTime;
    public DateTime CloseLocal => DateTimeOffset.FromUnixTimeSeconds(CloseTime).LocalDateTime;

    public DateTime CreatedLocal => DateTimeOffset.FromUnixTimeMilliseconds((long)BetId).LocalDateTime;

    public string PostedAgo
    {
        get
        {
            var d = DateTime.Now - CreatedLocal;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
            return $"{(int)d.TotalDays} d ago";
        }
    }
    
    public string StatusClass => Status != 0 ? "done" : (CanJoin ? "open" : "wait");
    public string StatusText => Status switch
    {
        1 => $"Resolved: {(Winner == 0 ? "YES" : "NO")}",
        2 => "Cancelled",
        _ => CanJoin ? "Open" : "Waiting for result"
    };

    // Same rules as the contract's `claim`.
    public ulong ClaimAmount(ulong yes, ulong no)
    {
        if (Status == 2) return yes + no;
        if (Status != 1 || Winner is null) return 0;
        var (winPool, winStake) = Winner == 0 ? (YesPool, yes) : (NoPool, no);
        if (winPool == 0) return yes + no;
        return (ulong)((BigInteger)winStake * Total / winPool);
    }

    // What you would get if your side wins after adding `add` to it.
    public ulong PotentialPayout(int side, ulong myStakeOnSide, ulong add)
    {
        var pool = side == 0 ? YesPool : NoPool;
        var denom = (BigInteger)pool + add;
        if (denom == 0) return 0;
        return (ulong)(((BigInteger)myStakeOnSide + add) * ((BigInteger)Total + add) / denom);
    }

    public static BetInfo Decode(string address, byte[] d)
    {
        int o = 8;
        string Key() { var k = new PublicKey(d.AsSpan(o, 32).ToArray()).Key; o += 32; return k; }

        var creator = Key(); var resolver = Key(); var mint = Key(); var vault = Key();
        var id = BitConverter.ToUInt64(d, o); o += 8;
        var len = (int)BitConverter.ToUInt32(d, o); o += 4;
        var title = Encoding.UTF8.GetString(d, o, len); o += len;
        var close = BitConverter.ToInt64(d, o); o += 8;
        var yes = BitConverter.ToUInt64(d, o); o += 8;
        var no = BitConverter.ToUInt64(d, o); o += 8;
        int status = d[o++];
        int? winner = d[o++] == 1 ? (int?)d[o++] : null;

        return new BetInfo(address, creator, resolver, mint, vault, id, title, close, yes, no, status, winner);
    }
}
