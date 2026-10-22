import { ActionForm } from '../../../components/ActionForm';
import { createRelease, updateRelease } from '../../../lib/actions';
import { api, can, type Schemas } from '../../../lib/api';
import { when } from '../../../lib/format';

export default async function ReleasesPage() {
  const [list, mayWrite] = await Promise.all([
    api<{ items: Schemas['AppRelease'][] }>('/admin/releases'),
    can('releases.write'),
  ]);
  return (
    <>
      <h1>Releases</h1>
      <p className="muted">
        Staged rollouts happen in Play Console and App Store Connect; record each one here with its
        rollback threshold. Reconciliation halts a release automatically when its crash-free
        sessions drop below the threshold. Then halt the store rollout (runbook: release rollback).
      </p>
      <table>
        <thead>
          <tr>
            <th>Version</th>
            <th>Status</th>
            <th>Rollout</th>
            <th>Crash-free (sessions)</th>
            <th>Threshold</th>
            <th>Updated</th>
            {mayWrite && <th />}
          </tr>
        </thead>
        <tbody>
          {list.items.length === 0 && (
            <tr>
              <td colSpan={7} className="muted">
                No releases recorded.
              </td>
            </tr>
          )}
          {list.items.map((r) => (
            <tr key={r.id}>
              <td>
                <strong>{r.appVersion}</strong> {r.platform}
                <div className="muted">
                  {r.regions.length ? r.regions.join(', ') : 'all regions'}
                </div>
              </td>
              <td>
                <span
                  className={`badge ${r.status === 'ROLLING_OUT' ? 'PENDING' : r.status === 'HALTED' ? 'BANNED' : 'ACTIVE'}`}
                >
                  {r.status}
                </span>
                {r.haltReason && <div className="muted">{r.haltReason}</div>}
              </td>
              <td>{r.rolloutPercent}%</td>
              <td>
                {r.health.crashFreeRate === null
                  ? '—'
                  : `${(r.health.crashFreeRate * 100).toFixed(2)}%`}{' '}
                ({r.health.sessions})
              </td>
              <td>
                ≥ {(r.minCrashFree * 100).toFixed(1)}% after {r.minSessions}
              </td>
              <td>{when(r.updatedAt)}</td>
              {mayWrite && (
                <td>
                  {(r.status === 'ROLLING_OUT' || r.status === 'HALTED') && (
                    <ActionForm action={updateRelease.bind(null, r.id)} submit="Update" inline>
                      <select name="status" defaultValue="">
                        <option value="">keep status</option>
                        <option value="ROLLING_OUT">resume rollout</option>
                        <option value="COMPLETE">complete (100%)</option>
                        <option value="ROLLED_BACK">rolled back</option>
                      </select>
                      <input
                        name="rolloutPercent"
                        type="number"
                        min={1}
                        max={100}
                        placeholder="%"
                        style={{ width: 70 }}
                      />
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
          <h2 style={{ marginTop: 0 }}>Record a rollout</h2>
          <ActionForm action={createRelease} submit="Record">
            <div className="row">
              <label>
                Platform
                <select name="platform">
                  <option>ANDROID</option>
                  <option>IOS</option>
                </select>
              </label>
              <label>
                App version
                <input name="appVersion" pattern="\d+\.\d+\.\d+" required placeholder="0.7.0" />
              </label>
              <label>
                Rollout %
                <input
                  name="rolloutPercent"
                  type="number"
                  min={1}
                  max={100}
                  defaultValue={5}
                  required
                />
              </label>
            </div>
            <div className="row">
              <label>
                Regions (comma separated, empty = all)
                <input name="regions" placeholder="IN" />
              </label>
              <label>
                Minimum crash-free (%)
                <input
                  name="minCrashFree"
                  type="number"
                  step="0.1"
                  min={90}
                  max={100}
                  defaultValue={99.5}
                />
              </label>
              <label>
                Judge after sessions
                <input name="minSessions" type="number" min={10} defaultValue={500} />
              </label>
            </div>
          </ActionForm>
        </div>
      )}
    </>
  );
}
