use anchor_lang::prelude::*;
use anchor_spl::token_interface::{self, Mint, TokenAccount, TokenInterface, TransferChecked};

declare_id!("B3WpaPrLXRRnctaivRgwUBXdmGfcayZfKu4HqPHmH73i");

pub const MARKET_SEED: &[u8] = b"market";
pub const VAULT_SEED: &[u8] = b"vault";
pub const POSITION_SEED: &[u8] = b"position";
pub const MAX_QUESTION_LEN: usize = 200;

#[program]
pub mod polymarket_solana {
    use super::*;

    /// Creates a YES/NO market and funds the pool. Pool starts at price 0.50.
    pub fn create_market(
        ctx: Context<CreateMarket>,
        market_id: u64,
        question: String,
        end_time: i64,
        initial_liquidity: u64,
    ) -> Result<()> {
        require!(
            question.len() <= MAX_QUESTION_LEN,
            MarketError::QuestionTooLong
        );
        require!(initial_liquidity > 0, MarketError::ZeroAmount);
        require!(
            end_time > Clock::get()?.unix_timestamp,
            MarketError::EndTimeInPast
        );

        let market = &mut ctx.accounts.market;
        market.creator = ctx.accounts.creator.key();
        market.resolver = ctx.accounts.creator.key();
        market.collateral_mint = ctx.accounts.collateral_mint.key();
        market.vault = ctx.accounts.vault.key();
        market.market_id = market_id;
        market.question = question;
        market.end_time = end_time;
        market.yes_reserve = initial_liquidity;
        market.no_reserve = initial_liquidity;
        market.status = MarketStatus::Open;
        market.winning_outcome = None;
        market.bump = ctx.bumps.market;

        let cpi_accounts = TransferChecked {
            from: ctx.accounts.creator_collateral.to_account_info(),
            mint: ctx.accounts.collateral_mint.to_account_info(),
            to: ctx.accounts.vault.to_account_info(),
            authority: ctx.accounts.creator.to_account_info(),
        };
        let cpi_ctx = CpiContext::new(ctx.accounts.token_program.key(), cpi_accounts);
        token_interface::transfer_checked(
            cpi_ctx,
            initial_liquidity,
            ctx.accounts.collateral_mint.decimals,
        )?;
        Ok(())
    }

    /// Spend `amount_in` collateral to buy shares of `outcome`.
    pub fn buy(
        ctx: Context<Trade>,
        outcome: Outcome,
        amount_in: u64,
        min_shares_out: u64,
    ) -> Result<()> {
        require!(amount_in > 0, MarketError::ZeroAmount);

        let user_key = ctx.accounts.user.key();
        let market_key = ctx.accounts.market.key();
        let position_bump = ctx.bumps.position;

        {
            let market = &mut ctx.accounts.market;
            require!(
                market.status == MarketStatus::Open,
                MarketError::MarketNotOpen
            );
            require!(
                Clock::get()?.unix_timestamp < market.end_time,
                MarketError::TradingEnded
            );

            // y = reserve of the outcome being bought, n = the other reserve
            let (y, n) = match outcome {
                Outcome::Yes => (market.yes_reserve as u128, market.no_reserve as u128),
                Outcome::No => (market.no_reserve as u128, market.yes_reserve as u128),
            };
            let a = amount_in as u128;
            let k = y * n;
            let n_new = n + a;
            // Round up so the pool never loses value to rounding.
            let y_new = k.div_ceil(n_new);
            let shares_out = (y + a).checked_sub(y_new).ok_or(MarketError::MathError)?;
            let shares_out = u64::try_from(shares_out).map_err(|_| MarketError::MathError)?;
            require!(shares_out > 0, MarketError::ZeroAmount);
            require!(shares_out >= min_shares_out, MarketError::SlippageExceeded);

            let y_new = u64::try_from(y_new).map_err(|_| MarketError::MathError)?;
            let n_new = u64::try_from(n_new).map_err(|_| MarketError::MathError)?;
            match outcome {
                Outcome::Yes => {
                    market.yes_reserve = y_new;
                    market.no_reserve = n_new;
                }
                Outcome::No => {
                    market.no_reserve = y_new;
                    market.yes_reserve = n_new;
                }
            }

            let position = &mut ctx.accounts.position;
            if position.owner == Pubkey::default() {
                position.market = market_key;
                position.owner = user_key;
                position.bump = position_bump;
            }
            match outcome {
                Outcome::Yes => {
                    position.yes_shares = position
                        .yes_shares
                        .checked_add(shares_out)
                        .ok_or(MarketError::MathError)?
                }
                Outcome::No => {
                    position.no_shares = position
                        .no_shares
                        .checked_add(shares_out)
                        .ok_or(MarketError::MathError)?
                }
            }
        }

        // Collateral goes from the user into the vault.
        let cpi_accounts = TransferChecked {
            from: ctx.accounts.user_collateral.to_account_info(),
            mint: ctx.accounts.collateral_mint.to_account_info(),
            to: ctx.accounts.vault.to_account_info(),
            authority: ctx.accounts.user.to_account_info(),
        };
        let cpi_ctx = CpiContext::new(ctx.accounts.token_program.key(), cpi_accounts);
        token_interface::transfer_checked(
            cpi_ctx,
            amount_in,
            ctx.accounts.collateral_mint.decimals,
        )?;
        Ok(())
    }

    /// Receive exactly `collateral_out` by returning shares of `outcome`.
    /// The shares needed are computed on-chain and must be <= `max_shares_in`.
    pub fn sell(
        ctx: Context<Trade>,
        outcome: Outcome,
        collateral_out: u64,
        max_shares_in: u64,
    ) -> Result<()> {
        require!(collateral_out > 0, MarketError::ZeroAmount);

        {
            let market = &mut ctx.accounts.market;
            require!(
                market.status == MarketStatus::Open,
                MarketError::MarketNotOpen
            );
            require!(
                Clock::get()?.unix_timestamp < market.end_time,
                MarketError::TradingEnded
            );

            let (y, n) = match outcome {
                Outcome::Yes => (market.yes_reserve as u128, market.no_reserve as u128),
                Outcome::No => (market.no_reserve as u128, market.yes_reserve as u128),
            };
            let r = collateral_out as u128;
            require!(r < n, MarketError::PoolTooSmall);

            let k = y * n;
            // Round up so the user pays slightly more, never the pool.
            let y_new = k.div_ceil(n - r);
            let shares_in = (y_new + r).checked_sub(y).ok_or(MarketError::MathError)?;
            let shares_in = u64::try_from(shares_in).map_err(|_| MarketError::MathError)?;
            require!(shares_in <= max_shares_in, MarketError::SlippageExceeded);

            let position = &mut ctx.accounts.position;
            match outcome {
                Outcome::Yes => {
                    require!(
                        position.yes_shares >= shares_in,
                        MarketError::InsufficientShares
                    );
                    position.yes_shares -= shares_in;
                }
                Outcome::No => {
                    require!(
                        position.no_shares >= shares_in,
                        MarketError::InsufficientShares
                    );
                    position.no_shares -= shares_in;
                }
            }

            let y_new = u64::try_from(y_new).map_err(|_| MarketError::MathError)?;
            let n_new = (n - r) as u64;
            match outcome {
                Outcome::Yes => {
                    market.yes_reserve = y_new;
                    market.no_reserve = n_new;
                }
                Outcome::No => {
                    market.no_reserve = y_new;
                    market.yes_reserve = n_new;
                }
            }
        }

        pay_out(
            &ctx.accounts.market,
            &ctx.accounts.vault,
            &ctx.accounts.collateral_mint,
            &ctx.accounts.user_collateral,
            &ctx.accounts.token_program,
            collateral_out,
        )
    }

    /// Resolver declares the winner after the end time.
    pub fn resolve_market(ctx: Context<ResolveMarket>, winning_outcome: Outcome) -> Result<()> {
        let market = &mut ctx.accounts.market;
        require!(
            market.status == MarketStatus::Open,
            MarketError::MarketNotOpen
        );
        require!(
            Clock::get()?.unix_timestamp >= market.end_time,
            MarketError::TooEarlyToResolve
        );
        market.status = MarketStatus::Resolved;
        market.winning_outcome = Some(winning_outcome);
        Ok(())
    }

    /// Winners swap their winning shares for collateral, 1 share = 1 unit.
    pub fn redeem(ctx: Context<Redeem>) -> Result<()> {
        let amount;
        {
            let market = &ctx.accounts.market;
            require!(
                market.status == MarketStatus::Resolved,
                MarketError::NotResolved
            );
            let winner = market.winning_outcome.ok_or(MarketError::NotResolved)?;

            let position = &mut ctx.accounts.position;
            amount = match winner {
                Outcome::Yes => position.yes_shares,
                Outcome::No => position.no_shares,
            };
            require!(amount > 0, MarketError::NothingToRedeem);
            position.yes_shares = 0;
            position.no_shares = 0;
        }

        pay_out(
            &ctx.accounts.market,
            &ctx.accounts.vault,
            &ctx.accounts.collateral_mint,
            &ctx.accounts.user_collateral,
            &ctx.accounts.token_program,
            amount,
        )
    }

    /// After resolution, the creator collects the winning shares left in the pool.
    pub fn claim_pool(ctx: Context<ClaimPool>) -> Result<()> {
        let amount;
        {
            let market = &mut ctx.accounts.market;
            require!(
                market.status == MarketStatus::Resolved,
                MarketError::NotResolved
            );
            let winner = market.winning_outcome.ok_or(MarketError::NotResolved)?;
            amount = match winner {
                Outcome::Yes => market.yes_reserve,
                Outcome::No => market.no_reserve,
            };
            require!(amount > 0, MarketError::NothingToRedeem);
            market.yes_reserve = 0;
            market.no_reserve = 0;
        }

        pay_out(
            &ctx.accounts.market,
            &ctx.accounts.vault,
            &ctx.accounts.collateral_mint,
            &ctx.accounts.creator_collateral,
            &ctx.accounts.token_program,
            amount,
        )
    }
}

/// Sends collateral from the vault, signed by the market's PDA.
fn pay_out<'info>(
    market: &Account<'info, Market>,
    vault: &InterfaceAccount<'info, TokenAccount>,
    mint: &InterfaceAccount<'info, Mint>,
    to: &InterfaceAccount<'info, TokenAccount>,
    token_program: &Interface<'info, TokenInterface>,
    amount: u64,
) -> Result<()> {
    let id = market.market_id.to_le_bytes();
    let bump = [market.bump];
    let signer_seeds: &[&[&[u8]]] = &[&[MARKET_SEED, market.creator.as_ref(), &id, &bump]];
    let cpi_accounts = TransferChecked {
        from: vault.to_account_info(),
        mint: mint.to_account_info(),
        to: to.to_account_info(),
        authority: market.to_account_info(),
    };
    let cpi_ctx = CpiContext::new_with_signer(token_program.key(), cpi_accounts, signer_seeds);
    token_interface::transfer_checked(cpi_ctx, amount, mint.decimals)
}

#[derive(Accounts)]
#[instruction(market_id: u64)]
pub struct CreateMarket<'info> {
    #[account(mut)]
    pub creator: Signer<'info>,

    pub collateral_mint: InterfaceAccount<'info, Mint>,

    #[account(
        init,
        payer = creator,
        space = 8 + Market::INIT_SPACE,
        seeds = [MARKET_SEED, creator.key().as_ref(), &market_id.to_le_bytes()],
        bump
    )]
    pub market: Account<'info, Market>,

    #[account(
        init,
        payer = creator,
        seeds = [VAULT_SEED, market.key().as_ref()],
        bump,
        token::mint = collateral_mint,
        token::authority = market,
        token::token_program = token_program
    )]
    pub vault: InterfaceAccount<'info, TokenAccount>,

    #[account(
        mut,
        token::mint = collateral_mint,
        token::authority = creator,
        token::token_program = token_program
    )]
    pub creator_collateral: InterfaceAccount<'info, TokenAccount>,

    pub token_program: Interface<'info, TokenInterface>,
    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
pub struct Trade<'info> {
    #[account(mut)]
    pub user: Signer<'info>,

    #[account(mut, has_one = collateral_mint, has_one = vault)]
    pub market: Account<'info, Market>,

    pub collateral_mint: InterfaceAccount<'info, Mint>,

    #[account(mut)]
    pub vault: InterfaceAccount<'info, TokenAccount>,

    #[account(
        mut,
        token::mint = collateral_mint,
        token::authority = user,
        token::token_program = token_program
    )]
    pub user_collateral: InterfaceAccount<'info, TokenAccount>,

    #[account(
        init_if_needed,
        payer = user,
        space = 8 + Position::INIT_SPACE,
        seeds = [POSITION_SEED, market.key().as_ref(), user.key().as_ref()],
        bump
    )]
    pub position: Account<'info, Position>,

    pub token_program: Interface<'info, TokenInterface>,
    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
pub struct ResolveMarket<'info> {
    pub resolver: Signer<'info>,

    #[account(mut, has_one = resolver)]
    pub market: Account<'info, Market>,
}

#[derive(Accounts)]
pub struct Redeem<'info> {
    pub user: Signer<'info>,

    #[account(has_one = collateral_mint, has_one = vault)]
    pub market: Account<'info, Market>,

    pub collateral_mint: InterfaceAccount<'info, Mint>,

    #[account(mut)]
    pub vault: InterfaceAccount<'info, TokenAccount>,

    #[account(
        mut,
        token::mint = collateral_mint,
        token::authority = user,
        token::token_program = token_program
    )]
    pub user_collateral: InterfaceAccount<'info, TokenAccount>,

    #[account(
        mut,
        seeds = [POSITION_SEED, market.key().as_ref(), user.key().as_ref()],
        bump = position.bump
    )]
    pub position: Account<'info, Position>,

    pub token_program: Interface<'info, TokenInterface>,
}

#[derive(Accounts)]
pub struct ClaimPool<'info> {
    pub creator: Signer<'info>,

    #[account(mut, has_one = creator, has_one = collateral_mint, has_one = vault)]
    pub market: Account<'info, Market>,

    pub collateral_mint: InterfaceAccount<'info, Mint>,

    #[account(mut)]
    pub vault: InterfaceAccount<'info, TokenAccount>,

    #[account(
        mut,
        token::mint = collateral_mint,
        token::authority = creator,
        token::token_program = token_program
    )]
    pub creator_collateral: InterfaceAccount<'info, TokenAccount>,

    pub token_program: Interface<'info, TokenInterface>,
}

#[account]
#[derive(InitSpace)]
pub struct Market {
    pub creator: Pubkey,
    pub resolver: Pubkey,
    pub collateral_mint: Pubkey,
    pub vault: Pubkey,
    pub market_id: u64,
    #[max_len(200)]
    pub question: String,
    pub end_time: i64,
    pub yes_reserve: u64,
    pub no_reserve: u64,
    pub status: MarketStatus,
    pub winning_outcome: Option<Outcome>,
    pub bump: u8,
}

#[account]
#[derive(InitSpace)]
pub struct Position {
    pub market: Pubkey,
    pub owner: Pubkey,
    pub yes_shares: u64,
    pub no_shares: u64,
    pub bump: u8,
}

#[derive(AnchorSerialize, AnchorDeserialize, Clone, Copy, PartialEq, Eq, InitSpace)]
pub enum MarketStatus {
    Open,
    Resolved,
}

#[derive(AnchorSerialize, AnchorDeserialize, Clone, Copy, PartialEq, Eq, InitSpace)]
pub enum Outcome {
    Yes,
    No,
}

#[error_code]
pub enum MarketError {
    #[msg("Question is too long (max 200 bytes)")]
    QuestionTooLong,
    #[msg("Amount must be greater than zero")]
    ZeroAmount,
    #[msg("End time must be in the future")]
    EndTimeInPast,
    #[msg("Market is not open")]
    MarketNotOpen,
    #[msg("Trading has ended for this market")]
    TradingEnded,
    #[msg("Too early to resolve this market")]
    TooEarlyToResolve,
    #[msg("Market is not resolved yet")]
    NotResolved,
    #[msg("Price moved too much (slippage limit hit)")]
    SlippageExceeded,
    #[msg("Not enough shares")]
    InsufficientShares,
    #[msg("Requested amount is larger than the pool can pay")]
    PoolTooSmall,
    #[msg("Nothing to redeem")]
    NothingToRedeem,
    #[msg("Math error")]
    MathError,
}
