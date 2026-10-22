import { ActionForm } from '../../../components/ActionForm';
import { publishSeason, saveSeason } from '../../../lib/actions';
import { api, can, type Schemas } from '../../../lib/api';
import { when } from '../../../lib/format';

const EXAMPLE = `1 FREE COIN 50
1 PREMIUM COSMETIC skin_rifle_neon_01
2 FREE COSMETIC banner_irani_chai
3 PREMIUM GEM 20`;

const rewardText = (r: Schemas['SeasonReward']) =>
  r.type === 'COSMETIC' ? r.cosmeticKey : `${r.amount} ${r.currency}`;

export default async function SeasonsPage() {
  const [list, mayWrite] = await Promise.all([
    api<{ items: Schemas['AdminSeason'][] }>('/admin/seasons'),
    can('liveops.write'),
  ]);
  return (
    <>
      <h1>Seasons</h1>
      <p className="muted">
        Players earn season XP from matches; each tier unlocks a free reward and, with the season
        pass, a premium one. Sell the pass as a store product with a SEASON_PASS grant (Catalogue).
        Published seasons are frozen and may not overlap.
      </p>
      {list.items.map((s) => (
        <div key={s.key} className="panel">
          <h2 style={{ marginTop: 0 }}>
            {s.title}{' '}
            <span className={`badge ${s.status === 'PUBLISHED' ? 'PUBLISHED' : 'PENDING'}`}>
              {s.status}
            </span>
          </h2>
          <p className="muted">
            <span className="mono">{s.key}</span> · {when(s.startsAt)} → {when(s.endsAt)} ·{' '}
            {s.xpPerTier} XP per tier · by {s.createdBy}
          </p>
          <table>
            <thead>
              <tr>
                <th>Tier</th>
                <th>Free</th>
                <th>Premium</th>
              </tr>
            </thead>
            <tbody>
              {[...new Set(s.rewards.map((r) => r.tier))].map((tier) => (
                <tr key={tier}>
                  <td>{tier}</td>
                  {(['FREE', 'PREMIUM'] as const).map((track) => {
                    const r = s.rewards.find((x) => x.tier === tier && x.track === track);
                    return <td key={track}>{r ? rewardText(r.reward) : '—'}</td>;
                  })}
                </tr>
              ))}
            </tbody>
          </table>
          {mayWrite && s.status === 'DRAFT' && (
            <div style={{ marginTop: 10 }}>
              <ActionForm
                action={publishSeason.bind(null, s.key)}
                submit="Publish"
                confirm="Publish this season? Its dates and rewards become permanent."
              />
            </div>
          )}
        </div>
      ))}
      {mayWrite && (
        <div className="panel">
          <h2 style={{ marginTop: 0 }}>Create or replace a draft season</h2>
          <ActionForm action={saveSeason} submit="Save draft">
            <div className="row">
              <label>
                Key
                <input name="key" pattern="[a-z0-9_-]{3,40}" required placeholder="monsoon-1" />
              </label>
              <label>
                Title
                <input name="title" maxLength={80} required />
              </label>
              <label>
                XP per tier
                <input name="xpPerTier" type="number" min={100} defaultValue={1000} required />
              </label>
            </div>
            <div className="row">
              <label>
                Starts (UTC)
                <input name="startsAt" type="datetime-local" required />
              </label>
              <label>
                Ends (UTC)
                <input name="endsAt" type="datetime-local" required />
              </label>
            </div>
            <label>
              Rewards, one per line: tier, FREE or PREMIUM, then COSMETIC key or COIN/GEM amount
              <textarea name="rewards" rows={8} defaultValue={EXAMPLE} required />
            </label>
          </ActionForm>
        </div>
      )}
    </>
  );
}
