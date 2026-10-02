# KLuck.bet

A prediction market on **Solana**, inspired by Polymarket. Users create YES/NO markets, buy and sell outcome shares, and after a market ends the winners redeem their payout.

- **Smart contract:** Rust + Anchor 1.2.0
- **Frontend:** C# Blazor WebAssembly + Solnet
- **Wallet:** Phantom (transactions are signed in the browser, private keys never leave the wallet)
- **Network:** **devnet** only (test network)

> ⚠️ This project is experimental. It has not been audited and must not be used with real money.

## How it works

Each market uses a constant-product AMM (the same model as Gnosis/Omen):

- The pool holds reserves of YES and NO shares (`y` and `n`), and trading keeps `y * n = k` constant.
- YES price = `n / (y + n)`. A new market starts at a price of 0.50.
- Collateral is an SPL token (on devnet, a test token acting as "test USDC").
- After resolution, each winning share redeems for 1 collateral token; losing shares are worth 0.
- Rounding always favors the pool, and all math uses `u128`.

### Contract instructions

| Instruction | Caller | Description |
|---|---|---|
| `create_market` | anyone | Creates a market and its vault, and deposits the initial liquidity |
| `buy` | anyone | Buys outcome shares with collateral, with slippage protection |
| `sell` | anyone | Sells shares and receives an exact amount of collateral |
| `resolve_market` | resolver (market creator) | Declares the winning outcome after the end time |
| `redeem` | anyone | Winners swap shares for collateral 1:1 |
| `claim_pool` | market creator | Collects the winning shares left in the pool after resolution |

### Accounts

- **Market:** question, end time, YES/NO reserves, status, winner, vault address.
- **Position:** a user's YES/NO shares in one market (PDA derived from `market + user`).
- **Vault:** the market's token account holding the collateral.

## Project structure

```
polymarket_solana/          # smart contract (Anchor)
├── Anchor.toml
├── programs/polymarket_solana/src/lib.rs
└── target/idl/polymarket_solana.json   # IDL (after anchor build)

PolymarketApp/              # Blazor WebAssembly app
├── wwwroot/wallet.js       # Phantom bridge (JS interop)
├── Services/
│   ├── Chain.cs            # Program ID and mint address
│   ├── WalletService.cs    # wallet connection
│   ├── SolanaService.cs    # reading markets, sending transactions
│   ├── Borsh.cs            # argument serialization
│   ├── MarketInfo.cs       # Market account decoding
│   └── Amm.cs              # quote math (same formula as the contract)
└── Pages/
    ├── Home.razor          # wallet connection and balance
    ├── Markets.razor       # market list and creation
    └── Market.razor        # buy, sell, resolve, redeem
```

The internal folder, module and namespace names are legacy and can be renamed to `kluckbet`. If you rename the program, run `anchor keys sync` and update `Chain.cs`.

## Requirements

- Linux / macOS / Windows (WSL)
- [Rust](https://rustup.rs/)
- [Solana CLI (Agave) and Anchor](https://solana.com/docs/intro/installation) (built with Anchor 1.2.0, Solana CLI 3.1.x, platform-tools v1.52)
- Node.js and Yarn
- [.NET SDK](https://dotnet.microsoft.com/download) 8 or newer
- The [Phantom](https://phantom.app/) browser extension with Testnet Mode (Devnet) enabled

Verify your installation:

```bash
solana --version
anchor --version
cargo build-sbf --version
dotnet --version
```

## Getting started

### 1. Smart contract

```bash
cd polymarket_solana

# switch to devnet and get test SOL
solana config set --url devnet
solana address        # paste the address at https://faucet.solana.com

# build and deploy
anchor keys sync
anchor build
anchor deploy --provider.cluster devnet
```

Save the **Program ID** printed by `anchor deploy`. Deploying needs roughly 3 to 5 devnet SOL.

If `Anchor.toml` has no `[programs.devnet]` section, copy the line from `[programs.localnet]` into it.

> If LiteSVM tests fail with `InvalidAccountData`, build the program with the older SBPF target:
> ```bash
> cd programs/polymarket_solana
> cargo build-sbf --arch v0
> cd ../..
> cargo test
> ```

### 2. Test collateral token

```bash
cargo install spl-token-cli
spl-token create-token --decimals 6
spl-token create-account <MINT_ADDRESS>
spl-token mint <MINT_ADDRESS> 10000
```

Save the **mint address**. To trade from Phantom, send some tokens to your wallet:

```bash
spl-token transfer <MINT_ADDRESS> 1000 <PHANTOM_ADDRESS> --fund-recipient
```

The Phantom wallet also needs devnet SOL (from [faucet.solana.com](https://faucet.solana.com)).

### 3. Frontend (Blazor)

Set your own values in `PolymarketApp/Services/Chain.cs`:

```csharp
public const string ProgramId = "YOUR_PROGRAM_ID";
public const string CollateralMint = "YOUR_MINT_ADDRESS";
```

Run the app:

```bash
cd PolymarketApp
dotnet restore
dotnet run
```

Open the URL printed in the console in a browser with Phantom installed (network set to Devnet).

> The Market account size (`MarketAccountSize = 376` in `SolanaService.cs`) is 8 + `Market::INIT_SPACE`. If you change the fields of the `Market` struct, update this number.

## Usage

1. Open the home page and click **Connect Phantom**.
2. Go to `/markets`, enter a question, the time until the market ends (in minutes) and the initial liquidity, click **Create market**, and approve the transaction in Phantom.
3. After a few seconds, click **Refresh**. The market appears in the list at YES 50% / NO 50%.
4. Open the market, choose an outcome, and buy or sell shares. Quotes are calculated in the client, and the contract makes the final calculation.
5. After the end time, the resolver (the wallet that created the market) picks the winner with **YES won** or **NO won**.
6. Winners click **Redeem winnings** to get their tokens back.

To test the full cycle quickly, create a market that ends in 5 to 10 minutes.

## Known limitations

- Devnet only, and the contract has not been audited.
- No trading fee.
- A single trusted resolver (the market creator), with no oracle or dispute process.
- `claim_pool` (returning liquidity to the creator) exists in the contract but is not in the UI yet.
- No automated contract tests; a manual run through the UI serves as the test.
- No backend or indexer: the app reads data directly from Solana RPC, so there is no price history.

## Roadmap

- [ ] `claim_pool` button in the UI
- [ ] Trading fee
- [ ] Safer resolution (oracle / multisig / disputes)
- [ ] Automated contract tests
- [ ] ASP.NET Core backend with an indexer and PostgreSQL (price history, portfolio)
- [ ] Price charts and a portfolio page
- [ ] Security audit before any use with real money

## Useful links

- [Solana docs](https://solana.com/docs)
- [Anchor](https://www.anchor-lang.com/)
- [Solnet](https://github.com/bmresearch/Solnet)
- [Solana Explorer (devnet)](https://explorer.solana.com/?cluster=devnet)

## License

MIT (or choose your own)
