using System.Numerics;

namespace PolymarketApp.Services;

public static class Amm
{
    // outcome: 0 = YES, 1 = NO
    static (BigInteger y, BigInteger n) Reserves(MarketInfo m, int outcome) =>
        outcome == 0 ? (m.YesReserve, m.NoReserve) : (m.NoReserve, m.YesReserve);

    static BigInteger CeilDiv(BigInteger a, BigInteger b) => (a + b - 1) / b;

    public static ulong SharesForBuy(MarketInfo m, int outcome, ulong amountIn)
    {
        var (y, n) = Reserves(m, outcome);
        var a = new BigInteger(amountIn);
        var yNew = CeilDiv(y * n, n + a);
        return (ulong)(y + a - yNew);
    }

    public static ulong? SharesForSell(MarketInfo m, int outcome, ulong collateralOut)
    {
        var (y, n) = Reserves(m, outcome);
        var r = new BigInteger(collateralOut);
        if (r >= n) return null;
        var yNew = CeilDiv(y * n, n - r);
        return (ulong)(yNew + r - y);
    }
}