using System.Text;
using Solnet.Wallet;

namespace PolymarketApp.Services;

public record MarketInfo(
    string Address, string Creator, string Resolver, string Mint, string Vault,
    ulong MarketId, string Question, long EndTime,
    ulong YesReserve, ulong NoReserve, bool Resolved, int? Winner)
{
    public double YesPrice => (double)NoReserve / (YesReserve + NoReserve);
    public double NoPrice => 1 - YesPrice;

    public static MarketInfo Decode(string address, byte[] d)
    {
        int o = 8; // skip Anchor's 8-byte account discriminator
        string Key() { var k = new PublicKey(d.AsSpan(o, 32).ToArray()).Key; o += 32; return k; }

        var creator = Key(); var resolver = Key(); var mint = Key(); var vault = Key();
        var id = BitConverter.ToUInt64(d, o); o += 8;
        var len = (int)BitConverter.ToUInt32(d, o); o += 4;
        var question = Encoding.UTF8.GetString(d, o, len); o += len;
        var end = BitConverter.ToInt64(d, o); o += 8;
        var yes = BitConverter.ToUInt64(d, o); o += 8;
        var no = BitConverter.ToUInt64(d, o); o += 8;
        var resolved = d[o++] == 1;
        int? winner = d[o++] == 1 ? (int?)d[o++] : null;

        return new MarketInfo(address, creator, resolver, mint, vault,
            id, question, end, yes, no, resolved, winner);
    }
}