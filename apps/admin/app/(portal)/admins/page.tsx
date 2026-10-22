import { ActionForm } from '../../../components/ActionForm';
import { createAdmin, disableAdmin, setRoles } from '../../../lib/actions';
import { api, me, type Schemas } from '../../../lib/api';
import { when } from '../../../lib/format';

const ROLES = ['SUPPORT', 'MODERATOR', 'ECONOMY_OPERATOR', 'ANALYST', 'SUPER_ADMIN'] as const;

export default async function AdminsPage() {
  const [list, self] = await Promise.all([api<Schemas['AdminList']>('/admin/admins'), me()]);
  return (
    <>
      <h1>Admin accounts</h1>
      <table>
        <thead>
          <tr>
            <th>Admin</th>
            <th>Status</th>
            <th>Last sign-in</th>
            <th>Roles</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {list.items.map((a) => (
            <tr key={a.id}>
              <td>
                {a.displayName}
                <div className="muted">{a.email}</div>
              </td>
              <td>
                <span className={`badge ${a.status}`}>{a.status}</span>
              </td>
              <td>{when(a.lastLoginAt)}</td>
              <td>
                {a.status === 'ACTIVE' ? (
                  <ActionForm action={setRoles.bind(null, a.id)} submit="Save roles" inline>
                    {ROLES.map((r) => (
                      <label key={r} className="check" style={{ fontSize: 12 }}>
                        <input
                          type="checkbox"
                          name="roles"
                          value={r}
                          defaultChecked={a.roles.includes(r)}
                        />{' '}
                        {r}
                      </label>
                    ))}
                    <input
                      name="reason"
                      placeholder="Reason"
                      minLength={3}
                      maxLength={500}
                      required
                    />
                  </ActionForm>
                ) : (
                  a.roles.join(', ')
                )}
              </td>
              <td>
                {a.status === 'ACTIVE' && a.id !== self.id && (
                  <ActionForm
                    action={disableAdmin.bind(null, a.id)}
                    submit="Disable"
                    danger
                    inline
                    confirm={`Disable ${a.email}? Their sessions end immediately.`}
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
            </tr>
          ))}
        </tbody>
      </table>

      <div className="panel" style={{ marginTop: 16 }}>
        <h2 style={{ marginTop: 0 }}>Add an admin</h2>
        <ActionForm action={createAdmin} submit="Create admin">
          <label>
            Email
            <input name="email" type="email" required />
          </label>
          <label>
            Name
            <input name="displayName" maxLength={60} required />
          </label>
          <label>
            Initial password (12+ characters; share it separately from the authenticator secret)
            <input
              name="password"
              type="password"
              minLength={12}
              autoComplete="new-password"
              required
            />
          </label>
          <div className="row">
            {ROLES.map((r) => (
              <label key={r} className="check">
                <input type="checkbox" name="roles" value={r} /> {r}
              </label>
            ))}
          </div>
        </ActionForm>
      </div>
    </>
  );
}
