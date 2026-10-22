'use client';

import { useActionState, type ReactNode } from 'react';
import type { ActionState } from '../lib/actions';

/**
 * A form bound to a server action that reports the outcome inline. `confirm` asks before submitting, for
 * actions that affect players or money.
 */
export function ActionForm({
  action,
  submit,
  children,
  confirm,
  danger,
  inline,
}: {
  action: (state: ActionState, data: FormData) => Promise<ActionState>;
  submit: string;
  children?: ReactNode;
  confirm?: string;
  danger?: boolean;
  inline?: boolean;
}) {
  const [state, formAction, pending] = useActionState(action, null);
  return (
    <form
      action={formAction}
      className={inline ? 'form inline' : 'form'}
      onSubmit={(e) => {
        if (confirm && !window.confirm(confirm)) e.preventDefault();
      }}
    >
      {children}
      <button type="submit" disabled={pending} className={danger ? 'danger' : undefined}>
        {pending ? 'Working…' : submit}
      </button>
      {state?.error && (
        <p className="msg error" role="alert">
          {state.error}
        </p>
      )}
      {state?.ok && <p className="msg ok">{state.ok}</p>}
      {state?.secret && (
        <div className="secret" role="status">
          <p>
            <strong>Shown once.</strong> Give this to the new admin to add to an authenticator app,
            then close this page.
          </p>
          <code>{state.secret.totpSecret}</code>
          <code className="small">{state.secret.otpauthUrl}</code>
        </div>
      )}
    </form>
  );
}
