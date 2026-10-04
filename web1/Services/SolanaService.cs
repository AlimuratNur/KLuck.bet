using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.JSInterop;
using Polymarket.Client.Services;
using Solnet.Programs;
using Solnet.Rpc;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace PolymarketApp.Services;

public record PositionInfo(ulong Yes, ulong No, bool Claimed);

public class SolanaService(IRpcClient rpc, WalletService wallet, IJSRuntime js)
{
    static readonly PublicKey Program = new(Chain.ProgramId);
    static readonly PublicKey Mint = new(Chain.CollateralMint);
    const int BetAccountSize = 316; // 8 + Bet::INIT_SPACE

    public static ulong ToBase(decimal v) => (ulong)(v * 1_000_000m);
    public static string Fmt(ulong v) => (v / 1_000_000m).ToString("0.##", CultureInfo.InvariantCulture);

    static byte[] Disc(string name) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"global:{name}"))[..8];

    static PublicKey Pda(params byte[][] seeds)
    {
        PublicKey.TryFindProgramAddress(seeds.ToList(), Program, out var pda, out _);
        return pda;
    }
    static PublicKey BetPda(PublicKey creator, ulong id) =>
        Pda(Encoding.UTF8.GetBytes("bet"), creator.KeyBytes, BitConverter.GetBytes(id));
    static PublicKey VaultPda(PublicKey bet) => Pda(Encoding.UTF8.GetBytes("vault"), bet.KeyBytes);
    static PublicKey PositionPda(PublicKey bet, PublicKey user) =>
        Pda(Encoding.UTF8.GetBytes("position"), bet.KeyBytes, user.KeyBytes);
    static PublicKey Ata(PublicKey owner, PublicKey mint) =>
        AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint);

    // ---------- reading ----------

    public async Task<List<BetInfo>> GetBetsAsync()
    {
        var res = await rpc.GetProgramAccountsAsync(Chain.ProgramId, dataSize: BetAccountSize);
        if (!res.WasSuccessful) throw new Exception(res.Reason);
        return res.Result
            .Select(a => BetInfo.Decode(a.PublicKey, Convert.FromBase64String(a.Account.Data[0])))
            .OrderByDescending(b => b.CloseTime)
            .ToList();
    }

    public async Task<BetInfo?> GetBetAsync(string address)
    {
        var res = await rpc.GetAccountInfoAsync(address);
        if (!res.WasSuccessful || res.Result.Value is null) return null;
        return BetInfo.Decode(address, Convert.FromBase64String(res.Result.Value.Data[0]));
    }

    public async Task<PositionInfo> GetPositionAsync(string bet, string owner)
    {
        var pda = PositionPda(new PublicKey(bet), new PublicKey(owner));
        var res = await rpc.GetAccountInfoAsync(pda.Key);
        if (!res.WasSuccessful || res.Result.Value is null) return new(0, 0, false);
        var d = Convert.FromBase64String(res.Result.Value.Data[0]);
        return new(BitConverter.ToUInt64(d, 72), BitConverter.ToUInt64(d, 80), d[88] == 1);
    }

    // ---------- writing ----------

    public Task<string> CreateBetAsync(string title, long closeUnix, string resolver, int side, ulong stake)
    {
        var creator = new PublicKey(wallet.Address!);
        var betId = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bet = BetPda(creator, betId);

        var data = new BorshWriter().Raw(Disc("create_bet"))
            .U64(betId).Str(title).I64(closeUnix).Key(resolver).U8((byte)side).U64(stake)
            .ToArray();

        var ix = new TransactionInstruction
        {
            ProgramId = Program.KeyBytes,
            Keys = new List<AccountMeta>
            {
                AccountMeta.Writable(creator, true),
                AccountMeta.ReadOnly(Mint, false),
                AccountMeta.Writable(bet, false),
                AccountMeta.Writable(VaultPda(bet), false),
                AccountMeta.Writable(PositionPda(bet, creator), false),
                AccountMeta.Writable(Ata(creator, Mint), false),
                AccountMeta.ReadOnly(TokenProgram.ProgramIdKey, false),
                AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false),
            },
            Data = data
        };
        return SendAsync(creator, ix);
    }

    public Task<string> JoinAsync(BetInfo b, int side, ulong amount)
    {
        var user = new PublicKey(wallet.Address!);
        var bet = new PublicKey(b.Address);
        var mint = new PublicKey(b.Mint);
        var data = new BorshWriter().Raw(Disc("join_bet")).U8((byte)side).U64(amount).ToArray();

        var ix = new TransactionInstruction
        {
            ProgramId = Program.KeyBytes,
            Keys = new List<AccountMeta>
            {
                AccountMeta.Writable(user, true),
                AccountMeta.Writable(bet, false),
                AccountMeta.ReadOnly(mint, false),
                AccountMeta.Writable(new PublicKey(b.Vault), false),
                AccountMeta.Writable(Ata(user, mint), false),
                AccountMeta.Writable(PositionPda(bet, user), false),
                AccountMeta.ReadOnly(TokenProgram.ProgramIdKey, false),
                AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false),
            },
            Data = data
        };
        return SendAsync(user, ix);
    }

    public Task<string> ResolveAsync(BetInfo b, int winner) =>
        JudgeAsync(b, new BorshWriter().Raw(Disc("resolve_bet")).U8((byte)winner).ToArray());

    public Task<string> CancelAsync(BetInfo b) =>
        JudgeAsync(b, new BorshWriter().Raw(Disc("cancel_bet")).ToArray());

    Task<string> JudgeAsync(BetInfo b, byte[] data)
    {
        var user = new PublicKey(wallet.Address!);
        var ix = new TransactionInstruction
        {
            ProgramId = Program.KeyBytes,
            Keys = new List<AccountMeta>
            {
                AccountMeta.ReadOnly(user, true),
                AccountMeta.Writable(new PublicKey(b.Address), false),
            },
            Data = data
        };
        return SendAsync(user, ix);
    }

    public Task<string> ClaimAsync(BetInfo b)
    {
        var user = new PublicKey(wallet.Address!);
        var bet = new PublicKey(b.Address);
        var mint = new PublicKey(b.Mint);
        var ix = new TransactionInstruction
        {
            ProgramId = Program.KeyBytes,
            Keys = new List<AccountMeta>
            {
                AccountMeta.ReadOnly(user, true),
                AccountMeta.ReadOnly(bet, false),
                AccountMeta.ReadOnly(mint, false),
                AccountMeta.Writable(new PublicKey(b.Vault), false),
                AccountMeta.Writable(Ata(user, mint), false),
                AccountMeta.Writable(PositionPda(bet, user), false),
                AccountMeta.ReadOnly(TokenProgram.ProgramIdKey, false),
            },
            Data = new BorshWriter().Raw(Disc("claim")).ToArray()
        };
        return SendAsync(user, ix);
    }

    async Task<string> SendAsync(PublicKey payer, params TransactionInstruction[] ixs)
    {
        var bh = await rpc.GetLatestBlockHashAsync();
        var builder = new TransactionBuilder()
            .SetRecentBlockHash(bh.Result.Value.Blockhash)
            .SetFeePayer(payer);
        foreach (var ix in ixs) builder.AddInstruction(ix);
        var msg = builder.CompileMessage();

        var tx = new byte[1 + 64 + msg.Length];
        tx[0] = 1;
        Array.Copy(msg, 0, tx, 65, msg.Length);
        return await js.InvokeAsync<string>("wallet.signAndSend", Convert.ToBase64String(tx), payer.Key);
    }
    
    public async Task<ulong> GetTokenBalanceAsync(string owner)
    {
        var ata = Ata(new PublicKey(owner), Mint);
        var res = await rpc.GetTokenAccountBalanceAsync(ata.Key);
        if (!res.WasSuccessful || res.Result?.Value is null) return 0;
        return ulong.TryParse(res.Result.Value.Amount, out var v) ? v : 0;
    }
    
    public async Task<List<(string Bet, PositionInfo Pos)>> GetMyPositionsAsync(string owner)
    {
        // Position accounts are 90 bytes; the owner address starts at byte 40.
        var res = await rpc.GetProgramAccountsAsync(Chain.ProgramId, dataSize: 90,
            memCmpList: new List<MemCmp> { new MemCmp { Offset = 40, Bytes = owner } });
        if (!res.WasSuccessful) throw new Exception(res.Reason);

        return res.Result.Select(a =>
        {
            var d = Convert.FromBase64String(a.Account.Data[0]);
            var bet = new PublicKey(d.AsSpan(8, 32).ToArray()).Key;
            return (bet, new PositionInfo(BitConverter.ToUInt64(d, 72), BitConverter.ToUInt64(d, 80), d[88] == 1));
        }).ToList();
    }
}
