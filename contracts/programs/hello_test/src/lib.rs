use anchor_lang::prelude::*;
use anchor_spl::token_interface::{self, Mint, TokenAccount, TokenInterface, TransferChecked};

declare_id!("B3WpaPrLXRRnctaivRgwUBXdmGfcayZfKu4HqPHmH73i");

pub const BET_SEED: &[u8] = b"bet";
pub const VAULT_SEED: &[u8] = b"vault";
pub const POSITION_SEED: &[u8] = b"position";
pub const MAX_TITLE_LEN: usize = 140;

#[program]
pub mod kluck_bet {
    use super::*;

    /// Creates a bet and puts the creator's stake on one side.
    pub fn create_bet(
        ctx: Context<CreateBet>,
        bet_id: u64,
        title: String,
        close_time: i64,
        resolver: Pubkey,
        side: Outcome,
        stake: u64,
    ) -> Result<()> {
        require!(title.len() <= MAX_TITLE_LEN, BetError::TitleTooLong);
        require!(stake > 0, BetError::ZeroAmount);
        require!(
            close_time > Clock::get()?.unix_timestamp,
            BetError::CloseTimeInPast
        );

        let bet_key = ctx.accounts.bet.key();
        let creator_key = ctx.accounts.creator.key();
        let mint_key = ctx.accounts.collateral_mint.key();
        let vault_key = ctx.accounts.vault.key();
        let bet_bump = ctx.bumps.bet;
        let position_bump = ctx.bumps.position;

        {
            let bet = &mut ctx.accounts.bet;
            bet.creator = creator_key;
            bet.resolver = resolver;
            bet.collateral_mint = mint_key;
            bet.vault = vault_key;
            bet.bet_id = bet_id;
            bet.title = title;
            bet.close_time = close_time;
            bet.yes_pool = 0;
            bet.no_pool = 0;
            bet.status = BetStatus::Open;
            bet.winner = None;
            bet.bump = bet_bump;

            let position = &mut ctx.accounts.position;
            position.bet = bet_key;
            position.owner = creator_key;
            position.yes_stake = 0;
            position.no_stake = 0;
            position.claimed = false;
            position.bump = position_bump;

            add_stake(bet, position, side, stake)?;
        }

        transfer_in(
            &ctx.accounts.creator_collateral,
            &ctx.accounts.collateral_mint,
            &ctx.accounts.vault,
            &ctx.accounts.creator,
            &ctx.accounts.token_program,
            stake,
        )
    }

    /// Anyone can join a side while the bet is open.
    pub fn join_bet(ctx: Context<Join>, side: Outcome, amount: u64) -> Result<()> {
        require!(amount > 0, BetError::ZeroAmount);

        let bet_key = ctx.accounts.bet.key();
        let user_key = ctx.accounts.user.key();
        let position_bump = ctx.bumps.position;

        {
            let bet = &mut ctx.accounts.bet;
            require!(bet.status == BetStatus::Open, BetError::NotOpen);
            require!(
                Clock::get()?.unix_timestamp < bet.close_time,
                BetError::JoiningClosed
            );

            let position = &mut ctx.accounts.position;
            if position.owner == Pubkey::default() {
                position.bet = bet_key;
                position.owner = user_key;
                position.bump = position_bump;
            }
            add_stake(bet, position, side, amount)?;
        }

        transfer_in(
            &ctx.accounts.user_collateral,
            &ctx.accounts.collateral_mint,
            &ctx.accounts.vault,
            &ctx.accounts.user,
            &ctx.accounts.token_program,
            amount,
        )
    }

    /// The judge declares what happened, after the closing time.
    pub fn resolve_bet(ctx: Context<Judge>, winner: Outcome) -> Result<()> {
        let bet = &mut ctx.accounts.bet;
        require!(bet.status == BetStatus::Open, BetError::NotOpen);
        require!(
            Clock::get()?.unix_timestamp >= bet.close_time,
            BetError::TooEarlyToResolve
        );
        bet.status = BetStatus::Resolved;
        bet.winner = Some(winner);
        Ok(())
    }

    /// The judge can cancel an open bet; everyone then gets their stake back.
    pub fn cancel_bet(ctx: Context<Judge>) -> Result<()> {
        let bet = &mut ctx.accounts.bet;
        require!(bet.status == BetStatus::Open, BetError::NotOpen);
        bet.status = BetStatus::Cancelled;
        Ok(())
    }

    /// Winners get a share of the pool; cancelled bets refund stakes.
    pub fn claim(ctx: Context<Claim>) -> Result<()> {
        let amount: u64;
        {
            let bet = &ctx.accounts.bet;
            let pos = &mut ctx.accounts.position;
            require!(!pos.claimed, BetError::AlreadyClaimed);

            let total = bet.yes_pool as u128 + bet.no_pool as u128;
            let own = pos.yes_stake as u128 + pos.no_stake as u128;

            let value: u128 = match bet.status {
                BetStatus::Open => return err!(BetError::NotResolved),
                BetStatus::Cancelled => own,
                BetStatus::Resolved => {
                    let (win_pool, win_stake) = match bet.winner.ok_or(BetError::NotResolved)? {
                        Outcome::Yes => (bet.yes_pool, pos.yes_stake),
                        Outcome::No => (bet.no_pool, pos.no_stake),
                    };
                    if win_pool == 0 {
                        own // nobody was on the winning side: refund
                    } else {
                        (win_stake as u128) * total / (win_pool as u128)
                    }
                }
            };

            amount = u64::try_from(value).map_err(|_| BetError::MathError)?;
            require!(amount > 0, BetError::NothingToClaim);
            pos.claimed = true;
        }

        let id = ctx.accounts.bet.bet_id.to_le_bytes();
        let bump = [ctx.accounts.bet.bump];
        let creator = ctx.accounts.bet.creator;
        let signer_seeds: &[&[&[u8]]] = &[&[BET_SEED, creator.as_ref(), &id, &bump]];
        let cpi_accounts = TransferChecked {
            from: ctx.accounts.vault.to_account_info(),
            mint: ctx.accounts.collateral_mint.to_account_info(),
            to: ctx.accounts.user_collateral.to_account_info(),
            authority: ctx.accounts.bet.to_account_info(),
        };
        let cpi_ctx = CpiContext::new_with_signer(
            ctx.accounts.token_program.key(),
            cpi_accounts,
            signer_seeds,
        );
        token_interface::transfer_checked(cpi_ctx, amount, ctx.accounts.collateral_mint.decimals)
    }
}

fn add_stake(bet: &mut Bet, pos: &mut Position, side: Outcome, amount: u64) -> Result<()> {
    match side {
        Outcome::Yes => {
            bet.yes_pool = bet
                .yes_pool
                .checked_add(amount)
                .ok_or(BetError::MathError)?;
            pos.yes_stake = pos
                .yes_stake
                .checked_add(amount)
                .ok_or(BetError::MathError)?;
        }
        Outcome::No => {
            bet.no_pool = bet.no_pool.checked_add(amount).ok_or(BetError::MathError)?;
            pos.no_stake = pos
                .no_stake
                .checked_add(amount)
                .ok_or(BetError::MathError)?;
        }
    }
    Ok(())
}

fn transfer_in<'info>(
    from: &InterfaceAccount<'info, TokenAccount>,
    mint: &InterfaceAccount<'info, Mint>,
    to: &InterfaceAccount<'info, TokenAccount>,
    authority: &Signer<'info>,
    token_program: &Interface<'info, TokenInterface>,
    amount: u64,
) -> Result<()> {
    let cpi_accounts = TransferChecked {
        from: from.to_account_info(),
        mint: mint.to_account_info(),
        to: to.to_account_info(),
        authority: authority.to_account_info(),
    };
    let cpi_ctx = CpiContext::new(token_program.key(), cpi_accounts);
    token_interface::transfer_checked(cpi_ctx, amount, mint.decimals)
}

#[derive(Accounts)]
#[instruction(bet_id: u64)]
pub struct CreateBet<'info> {
    #[account(mut)]
    pub creator: Signer<'info>,

    pub collateral_mint: InterfaceAccount<'info, Mint>,

    #[account(
        init,
        payer = creator,
        space = 8 + Bet::INIT_SPACE,
        seeds = [BET_SEED, creator.key().as_ref(), &bet_id.to_le_bytes()],
        bump
    )]
    pub bet: Account<'info, Bet>,

    #[account(
        init,
        payer = creator,
        seeds = [VAULT_SEED, bet.key().as_ref()],
        bump,
        token::mint = collateral_mint,
        token::authority = bet,
        token::token_program = token_program
    )]
    pub vault: InterfaceAccount<'info, TokenAccount>,

    #[account(
        init,
        payer = creator,
        space = 8 + Position::INIT_SPACE,
        seeds = [POSITION_SEED, bet.key().as_ref(), creator.key().as_ref()],
        bump
    )]
    pub position: Account<'info, Position>,

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
pub struct Join<'info> {
    #[account(mut)]
    pub user: Signer<'info>,

    #[account(mut, has_one = collateral_mint, has_one = vault)]
    pub bet: Account<'info, Bet>,

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
        seeds = [POSITION_SEED, bet.key().as_ref(), user.key().as_ref()],
        bump
    )]
    pub position: Account<'info, Position>,

    pub token_program: Interface<'info, TokenInterface>,
    pub system_program: Program<'info, System>,
}

#[derive(Accounts)]
pub struct Judge<'info> {
    pub resolver: Signer<'info>,

    #[account(mut, has_one = resolver)]
    pub bet: Account<'info, Bet>,
}

#[derive(Accounts)]
pub struct Claim<'info> {
    pub user: Signer<'info>,

    #[account(has_one = collateral_mint, has_one = vault)]
    pub bet: Account<'info, Bet>,

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
        seeds = [POSITION_SEED, bet.key().as_ref(), user.key().as_ref()],
        bump = position.bump
    )]
    pub position: Account<'info, Position>,

    pub token_program: Interface<'info, TokenInterface>,
}

#[account]
#[derive(InitSpace)]
pub struct Bet {
    pub creator: Pubkey,
    pub resolver: Pubkey,
    pub collateral_mint: Pubkey,
    pub vault: Pubkey,
    pub bet_id: u64,
    #[max_len(140)]
    pub title: String,
    pub close_time: i64,
    pub yes_pool: u64,
    pub no_pool: u64,
    pub status: BetStatus,
    pub winner: Option<Outcome>,
    pub bump: u8,
}

#[account]
#[derive(InitSpace)]
pub struct Position {
    pub bet: Pubkey,
    pub owner: Pubkey,
    pub yes_stake: u64,
    pub no_stake: u64,
    pub claimed: bool,
    pub bump: u8,
}

#[derive(AnchorSerialize, AnchorDeserialize, Clone, Copy, PartialEq, Eq, InitSpace)]
pub enum BetStatus {
    Open,
    Resolved,
    Cancelled,
}

#[derive(AnchorSerialize, AnchorDeserialize, Clone, Copy, PartialEq, Eq, InitSpace)]
pub enum Outcome {
    Yes,
    No,
}

#[error_code]
pub enum BetError {
    #[msg("Title is too long (max 140 bytes)")]
    TitleTooLong,
    #[msg("Amount must be greater than zero")]
    ZeroAmount,
    #[msg("Closing time must be in the future")]
    CloseTimeInPast,
    #[msg("Bet is not open")]
    NotOpen,
    #[msg("Joining is closed for this bet")]
    JoiningClosed,
    #[msg("Too early to resolve")]
    TooEarlyToResolve,
    #[msg("Bet is not resolved yet")]
    NotResolved,
    #[msg("Already claimed")]
    AlreadyClaimed,
    #[msg("Nothing to claim")]
    NothingToClaim,
    #[msg("Math error")]
    MathError,
}
