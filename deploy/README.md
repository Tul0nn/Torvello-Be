# Production deployment

Target: Ubuntu 24.04 x64, 1 vCPU / 2 GB RAM. Run server commands as root.
The server runs published artifacts; all application builds happen on the lead's PC.
Do not upload source credentials or commit `.env` or `build/`.

## First deployment

1. On the PC, run the repository build, tests, vulnerability audit and formatter
   from `AGENT.md`. Review the deployment tests before release.
2. Provision the droplet with SSH key access. Point DNS-only Cloudflare A records
   for `@`, `www`, `app`, and `api` at its IPv4 address. Remove stale AAAA records
   unless IPv6 is configured. Allow TCP 22/80/443 and UDP 443 in the cloud firewall.
   Caddy needs public DNS and inbound 80/443 for certificates. HSTS covers subdomains.
3. Copy `deploy/` (without build output) to `/srv/torvello/app` using SCP.
   Run `bash /srv/torvello/app/scripts/server-setup.sh` once. Keep the session open
   and verify a second key-authenticated SSH session after hardening.
4. Run `bash /srv/torvello/app/scripts/make-env.sh`. Save the printed bootstrap
   password securely. Edit `/srv/torvello/app/.env` to set real SMTP credentials,
   sender details and owner name. Keep mode 600. The generated connection string
   and application password must match. Never use the postgres administrator for API access.
5. From the backend root in Windows PowerShell 5.1, with .NET 10 SDK, Node/npm,
   Windows OpenSSH and tar installed, run:

   ```powershell
   .\deploy\scripts\deploy.ps1 -Server root@YOUR_IP -SitesRepo E:\torvello
   ```

   `SitesRepo` must contain `owner-web`, `cafe-site`, and `landing` with lockfiles.
   All three are built and copied. Only cafe and owner have public hosts in this
   deployment; landing is staged at `/srv/torvello/sites/landing` for future use.
   API publish is framework-dependent `linux-x64`. The script stops on command
   failures and waits for public API health; it does not automatically roll back.
6. Verify `https://api.torvellobd.com/health` returns only `{"status":"ok"}`.
   Verify apex cafe, www redirect, an owner SPA deep link, menu loading, browser
   CSP console, HTTPS cookies, sign-in, password change and mandatory 2FA setup.
   Confirm API/postgres have no published ports using `docker compose ps`.
7. Remove `Bootstrap__OwnerPassword` from `.env` after provisioning and recreate
   the API with `docker compose up -d api`. Install nightly backups:

   ```sh
   install -o root -g root -m 644 /srv/torvello/app/scripts/backup.cron /etc/cron.d/torvello-backup
   bash /srv/torvello/app/scripts/backup.sh
   ```

The fixed Compose subnet is `172.30.80.0/24`; check for host network conflicts
before first deployment. If changed, update both Compose and the env template.
Only Caddy should send forwarded headers. Do not attach unrelated containers.
Postgres initialization runs only with an empty data volume; editing env passwords
later does not change database roles. Rotate passwords in PostgreSQL and env together.

## Updates and logs

Run the same PC deployment command after review and checks. Before a release that
changes schema, run `bash scripts/backup.sh` from `/srv/torvello/app` and save keys.
Startup applies EF migrations automatically; check their compatibility before release.
Deployment briefly replaces static files in place. Schedule a quiet window.

```sh
cd /srv/torvello/app
docker compose ps
docker compose logs --tail=100 api postgres caddy
docker compose logs -f --tail=100 api
```

Logs rotate at 10 MB with three files per service. API and PostgreSQL are limited
to 512 MB each, Caddy to 128 MB; monitor OOM/restarts and disk use on the 2 GB host.
The runtime image has no HTTP probe utility, so its Docker healthcheck is disabled.
Public `/health` probes the database with `SELECT 1`; failures return an empty 503.
Arrange external uptime monitoring. Never use `docker compose down -v` on production.

## Backups and restore

The cron job runs at 02:15 server time and retains the newest 14 successful custom
format database dumps, mode 600. It excludes partial failures and concurrent runs.
Check `/var/log/torvello-backup.log`; rotate this log with the host's logrotate.
Copy dumps off the droplet into encrypted storage; the local files are not encrypted.
Also back up `.env` and Data Protection keys securely, for example:

```sh
cd /srv/torvello/app
umask 077
docker compose cp api:/data/keys /srv/torvello/backups/keys-YYYYMMDD
chmod -R go-rwx /srv/torvello/backups/keys-YYYYMMDD
```

Keep the copied key directory private (mode 700, files 600). The database dump
alone cannot recover encrypted national IDs. Retention for key/env copies and
release snapshots is manual; remove old copies securely after off-host backup.

Test restore on an isolated host first. For an in-place restore during maintenance:

```sh
cd /srv/torvello/app
docker compose stop api
docker compose exec -T postgres psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c 'DROP DATABASE torvello WITH (FORCE);'
docker compose exec -T postgres psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c 'CREATE DATABASE torvello OWNER torvello_app; REVOKE ALL ON DATABASE torvello FROM PUBLIC;'
docker compose exec -T postgres pg_restore -U postgres -d torvello --exit-on-error < /srv/torvello/backups/CHOSEN.dump
docker compose up -d api
```

This deletes the current database: take a fresh backup and select the dump before
running it. Preserve/restore the matching keys volume too. On a replacement host,
initialize PostgreSQL with the same application role first, restore the dump, then
copy saved keys into the API volume before starting API; retain ownership for the
image's non-root `app` user. Verify health, login and protected data after restore.

## Rollback

Each update saves the previous app (including `.env`) and sites under
`/srv/torvello/releases/previous-TIMESTAMP`. Choose and inspect the snapshot first.
From `/srv/torvello/app`, stop API, then restore that snapshot's `app/build`,
Dockerfile, Compose, Caddyfile, init and scripts into `app`, and its site contents
into `/srv/torvello/sites`. Replace old build/site contents rather than merging
hashed assets. Keep the current `.env` unless deliberately restoring matching
secrets. Run `docker compose up -d --build --force-recreate` and verify public health
and all hosts. The first deployment has no previous snapshot.

Code rollback does not undo migrations. If an older API cannot use the migrated
schema, restore the pre-release database with matching keys during maintenance;
this loses writes made since the dump. Never attempt an unreviewed schema downgrade.
