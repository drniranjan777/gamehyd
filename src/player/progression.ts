/** Progression and match rewards. Pure functions so balance changes are easy to test and review. */

export const XP_PER_LEVEL = 1000;

export function levelFor(xp: number): number {
  return Math.floor(xp / XP_PER_LEVEL) + 1;
}

export function xpForNextLevel(xp: number): number {
  return XP_PER_LEVEL - (xp % XP_PER_LEVEL);
}

export interface Reward {
  xp: number;
  coins: number;
}

/**
 * XP: 100 for playing, 50 per elimination, 10 per player outlasted, 300 for the win.
 * Coins: 10 for playing, 5 per elimination, 50 for the win. Kills are capped at the number of opponents.
 */
export function matchReward(
  placement: number,
  players: number,
  kills: number,
  won: boolean,
): Reward {
  const k = Math.max(0, Math.min(kills, players - 1));
  const outlasted = Math.max(0, players - placement);
  return {
    xp: 100 + 50 * k + 10 * outlasted + (won ? 300 : 0),
    coins: 10 + 5 * k + (won ? 50 : 0),
  };
}
