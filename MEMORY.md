# Decisions and gaps

- Refresh tokens cross HTTP boundaries only as torvello_rt cookies; response JSON
  contains only accessToken, accessTokenExpiresAt and user.
- Identity authenticator tokens and recovery codes avoid schema changes and packages.
  Normalization accepts spaces/dashes; recovery codes restore Identity's separator.
- Setup preserves an unenrolled user's current security stamp so enable can follow
  within the authenticated enrollment session. Enable/disable/code replacement
  invalidate access and refresh sessions; clients must sign in again.
- Manager/owner 2FA policy defaults on. Legacy TestApp cases explicitly disable it;
  SecurityTests retain an enabled-policy enrollment and manager gate scenario.
- Persistent Data Protection keys need deployment-owned durable storage, backup,
  permissions and certificate/DPAPI recovery procedures.
- No builds, tests, formatter, package audit or network commands were run during
  this change, as requested. The lead must run them before merging.
- Deployment must configure trusted proxy handling if TLS terminates upstream.
  Strict cookies require same-site website/API deployment. Existing refresh rotation
  concurrency semantics remain unchanged; simultaneous refreshes can trigger reuse
  detection. TOTP challenge replay within its five-minute lifetime remains bounded
  by stamp validity and Identity lockout; the contract does not require single-use challenges.

## Deployment handoff

- Work remains uncommitted on `feature/deploy` in the backend worktree.
- Full task brief: `E:\torvello\.worktrees\codex-deploy.md`.
- Production runbook: [deploy/README.md](deploy/README.md).
- Deployment files use PostgreSQL 17, .NET 10 runtime and Caddy 2 on Ubuntu 24.04
  x64. Builds happen on the PC; the server never compiles application source.
- Four DNS-only hosts: apex cafe, www redirect, app SPA and API. The third static
  build (`landing`) is copied but intentionally has no hostname in the brief.
- Fixed trusted Compose network: `172.30.80.0/24`; API/PostgreSQL have no host ports.
- Forwarding is opt-in with startup validation and a one-hop limit. DeploymentTests
  covers trusted/untrusted peers, HTTPS and rate-limit partitioning, bad config,
  anonymous health and database failure. `/health` bypasses account setup gates.
- Data Protection keys persist in `api_keys` but are unencrypted at rest on Linux;
  certificate protection is a documented follow-up. Back up keys with the database.
- Continuation review found DeploymentTests already present; extended it rather
  than replacing it. Added runbook/security notes, build ignore and LF rules.
- No dotnet, Docker or SSH commands were run or searched for; no packages added.
  Lead must run formatter, build with warnings as errors, all tests and vulnerability
  audit, then Compose/Caddy validation and a first deployment smoke test.
- Check forwarding test compilation/startup first, then non-root keys volume write
  access, migration permissions, SMTP, browser CSP, public health and restore.
- Only PowerShell syntax parsing and static diff/line-ending review are possible
  in this environment. Deployment, live certificates and backup restore remain untested.
