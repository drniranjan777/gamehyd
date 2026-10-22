import { ActionForm } from '../../components/ActionForm';
import { login } from '../../lib/actions';

export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<{ expired?: string }>;
}) {
  const { expired } = await searchParams;
  return (
    <main className="login">
      <div className="panel">
        <h1>RWBR operations</h1>
        {expired && <p className="muted">Your session ended. Sign in again.</p>}
        <ActionForm action={login} submit="Sign in">
          <label>
            Email
            <input name="email" type="email" autoComplete="username" required />
          </label>
          <label>
            Password
            <input name="password" type="password" autoComplete="current-password" required />
          </label>
          <label>
            Authenticator code
            <input
              name="code"
              inputMode="numeric"
              pattern="\d{6}"
              maxLength={6}
              autoComplete="one-time-code"
              required
            />
          </label>
        </ActionForm>
        <p className="muted" style={{ fontSize: 12 }}>
          Five failed attempts lock the account for 15 minutes. Every sign-in and change is audited.
        </p>
      </div>
    </main>
  );
}
