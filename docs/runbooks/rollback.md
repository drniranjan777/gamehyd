# Runbook: release rollback

**Use when:** a release raises crashes (crash-free below 99.5%), breaks matchmaking, purchases or the portal, or fails the post-deploy smoke test.

## Rules that make rollback safe

- **Database migrations are forward-only and additive** during a release: new tables and columns, no drops or renames of anything the previous API version uses. That lets the previous API version run on the new schema. Destructive changes ship one release later, after the rollback window. The migration runner refuses edited migrations (checksums).
- **Published content is immutable:** map versions (hash-pinned), weapon balance versions and store prices. A bad map or balance release is fixed by switching back to the previous version id, not by editing it.
- **Protocol:** the match server and client share a protocol version; the API's `MIN_CLIENT_VERSION` controls which game builds may queue.

## By component

| Component              | Roll back by                                                                                                                                                             | Notes                                                                                                                   |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------- |
| Platform API           | Redeploy the previous image **(cloud)**                                                                                                                                  | Safe with additive migrations. Run `fullstack-smoke.sh` against staging first when time allows                          |
| Match servers          | Redeploy the previous build **(cloud)**; send each old server SIGTERM (or Ctrl+C) once: it drains (no new matches, unhosted ones requeued), finishes its match and exits | Same protocol version required as the clients in the stores                                                             |
| Game client            | Halt the staged rollout in Play Console / App Store Connect                                                                                                              | Builds already installed stay; if the API must reject them, raise `MIN_CLIENT_VERSION` only after a fixed build is live |
| Admin portal / website | Redeploy the previous build                                                                                                                                              | Stateless                                                                                                               |
| Config / kill switches | **Live ops** → publish the previous version's values                                                                                                                     | Versions are kept with reasons; publishing the old values is itself audited                                             |
| Database               | Don't. Write a compensating migration                                                                                                                                    | Restore from backup only for data loss ([data incident](data-incident.md))                                              |

## Confirm

The smoke test passes, Beta metrics recover within an hour, and the rollback is noted in the release log with the reason.
