# Runbook: data incident

**Covers:** suspected exposure of personal data, a wrong bulk change, lost data, a compromised admin or server secret.

## First hour

1. **Open a Sev 1 incident** and keep a written timeline. Don't delete evidence: logs, the audit log, database state.
2. **Stop the leak or damage:**
   - **Compromised admin:** another super-admin disables the account (**Admins → Disable**), which ends its sessions immediately. Review its actions under **Audit log → actor `admin:<id>`**.
   - **Compromised secrets:** rotate them, then restart the API and servers **(cloud: secrets manager)**.
     - `JWT_SECRET` ends every player session; players sign in again silently with their device credential.
     - `SERVER_SHARED_SECRET` and `TICKET_SECRET_HEX` must change on the API and all match servers together.
     - `ADMIN_TOTP_KEY_HEX` needs every admin to re-enrol, because the TOTP secrets are encrypted with it.
   - **Wrong bulk change:** pause the job or the admin flow that caused it.
3. **Assess scope:** which tables, how many players, which fields.
   - Passwords, TOTP secrets, guest secrets, refresh tokens and purchase tokens are stored only as hashes or encrypted.
   - Card and UPI data never reach us.

## Notification (India, DPDP Act 2023; confirm with counsel)

Personal data breaches must be reported to the Data Protection Board and to affected players. Counsel and the grievance officer own the wording and timing; engineering supplies scope and timeline. Platform policies (Google Play, App Store) may also require notice.

## Restore from backup

1. Practise first: `infrastructure/scripts/backup-restore-test.sh` restores a pg_dump into a new database and checks row counts, migrations, ledger drift and append-only triggers. It takes about 3 s locally.
2. In production, restore point-in-time into a **new** database **(cloud: managed PostgreSQL PITR)**, compare, then switch the API's `DATABASE_URL`. Never restore over the live database.
3. Append-only tables (ledger, audit log, purchase events, moderation) mean wrong changes are compensated, not edited. Prefer compensating rows over restores when the damage is small.

## Confirm

Ledger drift 0, purchase integrity 0 / 0 / 0, affected players' data verified by sampling, and the post-incident review is scheduled.
