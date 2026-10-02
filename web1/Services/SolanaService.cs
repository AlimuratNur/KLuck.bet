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

public class SolanaService(IRpcClient rpc, WalletService wallet, IJSRuntime js)
{
    static readonly PublicKey Program = new(Chain.ProgramId);
    static readonly PublicKey Mint = new(Chain.CollateralMint);
    public record PositionInfo(ulong Yes, ulong No);

    public static ulong ToBase(decimal v) => (ulong)(v * 1_000_000m);
    public static decimal FromBase(ulong v) => v / 1_000_000m;
    const int MarketAccountSize = 376; // 8 + Market::INIT_SPACE
    const int Decimals = 6;
    
    static PublicKey PositionPda(PublicKey market, PublicKey user)
    {
        PublicKey.TryFindProgramAddress(
            new List<byte[]> { Encoding.UTF8.GetBytes("position"), market.KeyBytes, user.KeyBytes },
            Program, out var pda, out _);
        return pda;
    }

    static byte[] Disc(string ns, string name) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{ns}:{name}"))[..8];

    public async Task<List<MarketInfo>> GetMarketsAsync()
    {
        var res = await rpc.GetProgramAccountsAsync(Chain.ProgramId, dataSize: MarketAccountSize);
        if (!res.WasSuccessful) throw new Exception(res.Reason);
        return res.Result
            .Select(a => MarketInfo.Decode(a.PublicKey, Convert.FromBase64String(a.Account.Data[0])))
            .OrderByDescending(m => m.EndTime)
            .ToList();
    }

    public async Task<string> CreateMarketAsync(string question, long endUnix, decimal liquidity)
    {
        var creator = new PublicKey(wallet.Address!);
        var marketId = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        PublicKey.TryFindProgramAddress(
            new List<byte[]> { Encoding.UTF8.GetBytes("market"), creator.KeyBytes, BitConverter.GetBytes(marketId) },
            Program, out var market, out _);
        PublicKey.TryFindProgramAddress(
            new List<byte[]> { Encoding.UTF8.GetBytes("vault"), market.KeyBytes },
            Program, out var vault, out _);
        var creatorAta = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(creator, Mint);

        var amount = (ulong)(liquidity * (decimal)Math.Pow(10, Decimals));
        var data = new BorshWriter()
            .Raw(Disc("global", "create_market"))
            .U64(marketId).Str(question).I64(endUnix).U64(amount)
            .ToArray();

        var createIx = new TransactionInstruction
        {
            ProgramId = Program.KeyBytes,
            Keys = new List<AccountMeta>
            {
                AccountMeta.Writable(creator, true),
                AccountMeta.ReadOnly(Mint, false),
                AccountMeta.Writable(market, false),
                AccountMeta.Writable(vault, false),
                AccountMeta.Writable(creatorAta, false),
                AccountMeta.ReadOnly(TokenProgram.ProgramIdKey, false),
                AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false),
            },
            Data = data
        };

        return await SendAsync(creator, createIx);
    }

    async Task<string> SendAsync(PublicKey payer, params TransactionInstruction[] ixs)
    {
        var bh = await rpc.GetLatestBlockHashAsync();
        var builder = new TransactionBuilder()
            .SetRecentBlockHash(bh.Result.Value.Blockhash)
            .SetFeePayer(payer);
        foreach (var ix in ixs) builder.AddInstruction(ix);
        var msg = builder.CompileMessage();

        // Unsigned transaction: 1 signature slot of 64 zero bytes + message. Phantom fills it in.
        var tx = new byte[1 + 64 + msg.Length];
        tx[0] = 1;
        Array.Copy(msg, 0, tx, 65, msg.Length);

        return await js.InvokeAsync<string>("wallet.signAndSend", Convert.ToBase64String(tx));
    }
    
     public async Task<MarketInfo?> GetMarketAsync(string address)
    {
        var res = await rpc.GetAccountInfoAsync(address);
        if (!res.WasSuccessful || res.Result.Value is null) return null;
        return MarketInfo.Decode(address, Convert.FromBase64String(res.Result.Value.Data[0]));
    }

    public async Task<PositionInfo> GetPositionAsync(string market, string owner)
    {
        var pda = PositionPda(new PublicKey(market), new PublicKey(owner));
        var res = await rpc.GetAccountInfoAsync(pda.Key);
        if (!res.WasSuccessful || res.Result.Value is null) return new(0, 0);
        var d = Convert.FromBase64String(res.Result.Value.Data[0]);
        return new(BitConverter.ToUInt64(d, 72), BitConverter.ToUInt64(d, 80));
    }

    TransactionInstruction TradeIx(PublicKey user, MarketInfo m, byte[] data)
    {
        var market = new PublicKey(m.Address);
        var mint = new PublicKey(m.Mint);
        return new TransactionInstruction
        {
            ProgramId = Program.KeyBytes,
            Keys = new List<AccountMeta>
            {
                AccountMeta.Writable(user, true),
                AccountMeta.Writable(market, false),
                AccountMeta.ReadOnly(mint, false),
                AccountMeta.Writable(new PublicKey(m.Vault), false),
                AccountMeta.Writable(AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(user, mint), false),
                AccountMeta.Writable(PositionPda(market, user), false),
                AccountMeta.ReadOnly(TokenProgram.ProgramIdKey, false),
                AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false),
            },
            Data = data
        };
    }

    public Task<string> BuyAsync(MarketInfo m, int outcome, ulong amountIn, ulong minSharesOut)
    {
        var user = new PublicKey(wallet.Address!);
        var data = new BorshWriter().Raw(Disc("global", "buy"))
            .U8((byte)outcome).U64(amountIn).U64(minSharesOut).ToArray();
        return SendAsync(user, TradeIx(user, m, data));
    }

    public Task<string> SellAsync(MarketInfo m, int outcome, ulong collateralOut, ulong maxSharesIn)
    {
        var user = new PublicKey(wallet.Address!);
        var data = new BorshWriter().Raw(Disc("global", "sell"))
            .U8((byte)outcome).U64(collateralOut).U64(maxSharesIn).ToArray();
        return SendAsync(user, TradeIx(user, m, data));
    }

    public Task<string> ResolveAsync(MarketInfo m, int winningOutcome)
    {
        var user = new PublicKey(wallet.Address!);
        var ix = new TransactionInstruction
        {
            ProgramId = Program.KeyBytes,
            Keys = new List<AccountMeta>
            {
                AccountMeta.ReadOnly(user, true),
                AccountMeta.Writable(new PublicKey(m.Address), false),
            },
            Data = new BorshWriter().Raw(Disc("global", "resolve_market")).U8((byte)winningOutcome).ToArray()
        };
        return SendAsync(user, ix);
    }

    public Task<string> RedeemAsync(MarketInfo m)
    {
        var user = new PublicKey(wallet.Address!);
        var market = new PublicKey(m.Address);
        var mint = new PublicKey(m.Mint);
        var ix = new TransactionInstruction
        {
            ProgramId = Program.KeyBytes,
            Keys = new List<AccountMeta>
            {
                AccountMeta.ReadOnly(user, true),
                AccountMeta.ReadOnly(market, false),
                AccountMeta.ReadOnly(mint, false),
                AccountMeta.Writable(new PublicKey(m.Vault), false),
                AccountMeta.Writable(AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(user, mint), false),
                AccountMeta.Writable(PositionPda(market, user), false),
                AccountMeta.ReadOnly(TokenProgram.ProgramIdKey, false),
            },
            Data = new BorshWriter().Raw(Disc("global", "redeem")).ToArray()
        };
        return SendAsync(user, ix);
    }
}