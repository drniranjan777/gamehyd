export function money(m: { currency: string; amountMinor: number }): string {
  if (m.currency === 'INR')
    return new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR' }).format(
      m.amountMinor / 100,
    );
  return `${m.amountMinor.toLocaleString('en-IN')} ${m.currency === 'COIN' ? 'coins' : 'gems'}`;
}

export function when(iso: string | null | undefined): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleString('en-IN', {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'Asia/Kolkata',
  });
}

export const short = (id: string) => id.slice(0, 8);

type GrantLike =
  | { type: 'COSMETIC'; cosmeticKey: string }
  | { type: 'CURRENCY'; currency: string; amount: number }
  | { type: 'SEASON_PASS'; seasonKey: string };

export const grantText = (g: GrantLike) =>
  g.type === 'COSMETIC'
    ? g.cosmeticKey
    : g.type === 'SEASON_PASS'
      ? `season pass: ${g.seasonKey}`
      : `${g.amount} ${g.currency}`;
