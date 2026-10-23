import { ActionForm } from '../../../components/ActionForm';
import { cancelEvent, createEvent, saveConfig } from '../../../lib/actions';
import { api, can, type Schemas } from '../../../lib/api';
import { when } from '../../../lib/format';

export default async function LiveOpsPage() {
  const [config, events, mayWrite] = await Promise.all([
    api<Schemas['LiveConfig']>('/admin/liveops/config'),
    api<Schemas['AdminEventList']>('/admin/liveops/events'),
    can('liveops.write'),
  ]);
  const c = config.current.config;
  const lt = c.lootTable ?? {};
  return (
    <>
      <h1>Live operations</h1>
      <div className="row">
        <div className="panel">
          <h2 style={{ marginTop: 0 }}>Kill switches (version {config.current.version})</h2>
          {mayWrite ? (
            <ActionForm
              action={saveConfig.bind(null, config.current.version)}
              submit="Publish new version"
              confirm="Publish these settings to all players now?"
            >
              <label className="check">
                <input type="checkbox" name="storeEnabled" defaultChecked={c.storeEnabled} /> Store
                open
              </label>
              <label className="check">
                <input
                  type="checkbox"
                  name="matchmakingEnabled"
                  defaultChecked={c.matchmakingEnabled}
                />{' '}
                Matchmaking open
              </label>
              <label>
                Products off sale (keys, comma separated)
                <input name="disabledProducts" defaultValue={c.disabledProducts.join(', ')} />
              </label>
              <label>
                Weapons disabled (keys, comma separated)
                <input name="disabledWeapons" defaultValue={c.disabledWeapons.join(', ')} />
              </label>
              <label>
                XP multiplier (1 = normal, 2 = double-XP event)
                <input
                  name="xpMultiplier"
                  type="number"
                  step="0.5"
                  min={1}
                  max={5}
                  defaultValue={c.xpMultiplier ?? 1}
                />
              </label>
              <details>
                <summary>Loot table (version {lt.version ?? 1})</summary>
                <p className="muted" style={{ fontSize: 12 }}>
                  What map loot spots hold in matches that start after publishing. Shares are % of
                  the spots of that kind; the rest goes to the last option. Raise the version on
                  every change.
                </p>
                <div className="grid2">
                  <label>
                    Version
                    <input
                      name="loot.version"
                      type="number"
                      min={1}
                      defaultValue={lt.version ?? 1}
                    />
                  </label>
                  <label>
                    Frag grenades, % of ammo spots
                    <input name="loot.fragPercent" type="number" defaultValue={lt.fragPercent} />
                  </label>
                  <label>
                    Smoke grenades, % of ammo spots
                    <input name="loot.smokePercent" type="number" defaultValue={lt.smokePercent} />
                  </label>
                  <label>
                    Attachments, % of ammo spots
                    <input
                      name="loot.attachmentPercent"
                      type="number"
                      defaultValue={lt.attachmentPercent}
                    />
                  </label>
                  <label>
                    Light ammo, % of the remaining ammo spots
                    <input name="loot.lightPercent" type="number" defaultValue={lt.lightPercent} />
                  </label>
                  <label>
                    Medium ammo, %
                    <input
                      name="loot.mediumPercent"
                      type="number"
                      defaultValue={lt.mediumPercent}
                    />
                  </label>
                  <label>
                    Shells, % (the rest is heavy ammo)
                    <input
                      name="loot.shellsPercent"
                      type="number"
                      defaultValue={lt.shellsPercent}
                    />
                  </label>
                  <label>
                    Light rounds per spot
                    <input name="loot.lightRounds" type="number" defaultValue={lt.lightRounds} />
                  </label>
                  <label>
                    Medium rounds per spot
                    <input name="loot.mediumRounds" type="number" defaultValue={lt.mediumRounds} />
                  </label>
                  <label>
                    Shells per spot
                    <input name="loot.shellRounds" type="number" defaultValue={lt.shellRounds} />
                  </label>
                  <label>
                    Heavy rounds per spot
                    <input name="loot.heavyRounds" type="number" defaultValue={lt.heavyRounds} />
                  </label>
                  <label>
                    Bandages, % of heal spots
                    <input
                      name="loot.bandagePercent"
                      type="number"
                      defaultValue={lt.bandagePercent}
                    />
                  </label>
                  <label>
                    Medkits, % (the rest are energy drinks)
                    <input
                      name="loot.medkitPercent"
                      type="number"
                      defaultValue={lt.medkitPercent}
                    />
                  </label>
                  <label>
                    Bandages per spot
                    <input
                      name="loot.bandagesPerSpot"
                      type="number"
                      defaultValue={lt.bandagesPerSpot}
                    />
                  </label>
                  <label>
                    Vests, % of armour spots
                    <input name="loot.vestPercent" type="number" defaultValue={lt.vestPercent} />
                  </label>
                  <label>
                    Helmets, % (the rest are backpacks)
                    <input
                      name="loot.helmetPercent"
                      type="number"
                      defaultValue={lt.helmetPercent}
                    />
                  </label>
                  <label>
                    Chance of one level higher, %
                    <input
                      name="loot.upgradePercent"
                      type="number"
                      defaultValue={lt.upgradePercent}
                    />
                  </label>
                </div>
              </details>
              <label>
                Reason (audited)
                <input name="reason" minLength={3} maxLength={300} required />
              </label>
            </ActionForm>
          ) : (
            <pre className="mono">{JSON.stringify(c, null, 2)}</pre>
          )}
          <p className="muted" style={{ fontSize: 12 }}>
            API servers pick up a new version within 5 seconds. Disabled weapons apply to matches
            that start after the match servers read the config (Phase 6).
          </p>
        </div>
        <div className="panel">
          <h2 style={{ marginTop: 0 }}>History</h2>
          <table>
            <thead>
              <tr>
                <th>v</th>
                <th>When</th>
                <th>By</th>
                <th>Reason</th>
              </tr>
            </thead>
            <tbody>
              {config.history.map((h) => (
                <tr key={h.version}>
                  <td>{h.version}</td>
                  <td>{when(h.createdAt)}</td>
                  <td className="mono">{h.createdBy}</td>
                  <td>{h.reason}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <h2>Events and news</h2>
      <table>
        <thead>
          <tr>
            <th>Event</th>
            <th>Runs</th>
            <th>Status</th>
            {mayWrite && <th />}
          </tr>
        </thead>
        <tbody>
          {events.items.map((e) => (
            <tr key={e.key}>
              <td>
                <strong>{e.title}</strong>
                <div className="mono muted">{e.key}</div>
                {e.body && <div>{e.body}</div>}
              </td>
              <td>
                {when(e.startsAt)} → {when(e.endsAt)}
              </td>
              <td>
                <span className={`badge ${e.status}`}>{e.status}</span>
              </td>
              {mayWrite && (
                <td>
                  {e.status === 'SCHEDULED' && new Date(e.endsAt) > new Date() && (
                    <ActionForm
                      action={cancelEvent.bind(null, e.key)}
                      submit="Cancel"
                      danger
                      inline
                    >
                      <input
                        name="reason"
                        placeholder="Reason"
                        minLength={3}
                        maxLength={500}
                        required
                      />
                    </ActionForm>
                  )}
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
      {mayWrite && (
        <div className="panel" style={{ marginTop: 16 }}>
          <h2 style={{ marginTop: 0 }}>Schedule an event</h2>
          <ActionForm action={createEvent} submit="Schedule">
            <label>
              Key
              <input name="key" pattern="[a-z0-9_-]{3,60}" required />
            </label>
            <label>
              Title
              <input name="title" maxLength={80} required />
            </label>
            <label>
              Text shown on the website and in game
              <textarea name="body" maxLength={2000} />
            </label>
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
          </ActionForm>
        </div>
      )}
    </>
  );
}
