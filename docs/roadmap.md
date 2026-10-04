# KLuck.bet Roadmap

> KLuck.bet is an experimental prediction market on Solana (devnet). It is not audited and must not be used with real money. This roadmap shows where we are and where we want to go. Order matters more than dates.

---

## Phase 0 — Test Version ✅ (done)

A working end-to-end market cycle on devnet.

- [x] Anchor smart contract with a constant-product AMM (`y * n = k`)
- [x] `create_market`: open a YES/NO market with initial liquidity
- [x] `buy` with slippage protection
- [x] `sell` for an exact amount of collateral
- [x] `resolve_market`: the resolver declares the winner after the end time
- [x] `redeem`: winners swap shares for collateral 1:1
- [x] `claim_pool` instruction in the contract
- [x] Blazor WebAssembly app with Phantom wallet connection
- [x] Client-side quote math that matches the contract formula
- [x] Test collateral token ("test USDC") on devnet

---

## Phase 1 — Public Beta (next)

Make the app complete enough for real people to try.

- [ ] `claim_pool` button in the UI
- [ ] Trading fee
- [ ] Nicer market pages
- [ ] Price charts
- [ ] Portfolio page (positions and winnings)
- [ ] Automated contract tests (replace the manual UI run-through)

---

## Phase 2 — Trusted Results

Remove the single point of trust in how markets are resolved.

- [ ] Safer resolution: oracle, multisig or another reliable way to confirm outcomes
- [ ] Dispute process for contested results
- [ ] ASP.NET Core backend with an indexer and PostgreSQL (price history, portfolio data)

---

## Phase 3 — Before Any Real Money

Nothing here is optional.

- [ ] Independent security audit of the contract
- [ ] Legal and safety review
- [ ] Mainnet readiness checklist

---

## Phase 4 — Everywhere

Growth ideas once the foundation is solid.

- [ ] Mobile app
- [ ] Social sharing
- [ ] Leaderboards
- [ ] Community-created markets

---

## Known Limitations Today

- Devnet only, and the contract has not been audited
- No trading fee
- A single trusted resolver (the market creator), with no oracle or dispute process
- `claim_pool` exists in the contract but is not in the UI yet
- No automated contract tests
- No backend or indexer, so there is no price history

---

*Last updated: October 2026*
