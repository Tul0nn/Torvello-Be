# Torvello Backend

Cloud API for Torvello: staff accounts with email sign-in now; owner dashboard,
till sync and SMS later. The desktop till lives in the separate `torvello` repo.

## Stack
- ASP.NET Core on .NET 10, controllers + FluentValidation (same style as autom)
- PostgreSQL with EF Core (Npgsql)
- ASP.NET Core Identity for accounts, JWT access tokens + rotating refresh tokens
- MailKit SMTP for email (or a pickup folder in development)

## Layout
```
src/Torvello.Domain          Roles, password rules (no dependencies)
src/Torvello.Application     Request/response contracts, validators, service interfaces
src/Torvello.Infrastructure  EF Core, Identity, tokens, email, migrations
src/Torvello.Api             Controllers, auth pipeline, Program.cs
tests/Torvello.Tests         Unit + in-memory API tests (SQLite)
```

## Run locally
1. Start PostgreSQL, either installed locally or with Docker:
   ```
   docker compose up -d
   ```
   The development connection string (`appsettings.Development.json`) expects
   user `postgres`, password `postgres`, database `torvello` on port 5432.
   Change it with user-secrets if your password differs.
2. Set secrets (once). Never commit these:
   ```
   cd src/Torvello.Api
   dotnet user-secrets set "Jwt:SigningKey" "<random string, at least 32 characters>"
   dotnet user-secrets set "Bootstrap:OwnerEmail" "you@example.com"
   dotnet user-secrets set "Bootstrap:OwnerPassword" "<temporary password>"
   ```
3. Run the API (it applies migrations on start and creates the first owner):
   ```
   dotnet run --project src/Torvello.Api
   ```
   Swagger UI: http://localhost:5249/swagger
4. Development emails (invites, password resets) are written to
   `src/Torvello.Api/mail-pickup/` instead of being sent.

## Test
```
dotnet test
```

## Auth design
| Endpoint | Who | Notes |
|---|---|---|
| `POST /api/auth/login` | anyone | email + password → access token (15 min) + refresh token (14 days) |
| `POST /api/auth/refresh` | anyone | rotates the refresh token; replaying an old one ends the whole session family |
| `POST /api/auth/logout` | anyone | revokes the refresh token |
| `POST /api/auth/forgot-password` | anyone | always 202; emails a reset link if the account exists |
| `POST /api/auth/reset-password` | anyone | token from email; signs out every device |
| `POST /api/auth/accept-invite` | anyone | token from invite email; confirms email and sets the password |
| `POST /api/auth/change-password` | signed in | returns fresh tokens; other devices are signed out |
| `GET /api/auth/me` | signed in | |
| `GET/POST /api/staff`, `PUT /api/staff/{id}`, `POST /api/staff/{id}/resend-invite` | Owner | invite, change role, disable |

Security rules:
- Passwords follow the same policy as the till (8+ chars, letters and numbers, not common, not containing the email name).
- 5 wrong passwords lock the account for 5 minutes; auth endpoints are rate-limited per IP.
- Unknown email and wrong password return the same error.
- Disabling a user, changing their role or password rotates their security stamp: existing access tokens stop working immediately.
- The bootstrap owner must change the temporary password; until then only `/me` and `/change-password` work.
- Refresh tokens and reset/invite tokens are never stored in plain text. Sign-ins, failures, invites and changes are written to `AuditEvents`.

## Till sync
| Endpoint | Who | Notes |
|---|---|---|
| `GET/POST /api/devices`, `DELETE /api/devices/{id}` | Owner | register a till (the key `{deviceId}.{secret}` is shown once, stored hashed), list, revoke |
| `GET /api/sync/ping` | till (`X-Device-Key`) | checks the key, returns the till name |
| `POST /api/sync/events` | till (`X-Device-Key`) | up to 200 outbox events; idempotent by event id; each event accepted or rejected with a reason |
| `GET /api/reports/daily?date=YYYY-MM-DD` | Owner, Manager | orders, sales, discounts, payment methods, top items for a Dhaka business day |

Every uploaded event is stored raw in `SyncEvents`; `order_completed` is also projected into
`SyncedOrders` after re-checking that lines, discount, total and payments add up. Other event
types (shifts, cash movements, later stock and attendance) are kept for future reports.

Before production: set `Email:Provider=Smtp` with SMTP settings, set `AppLinks:WebBaseUrl`
to the web app URL, set `Cors:AllowedOrigins`, and persist ASP.NET Data Protection keys
(invite/reset links otherwise stop working after a restart).

## Branches
- `main` – released code
- `dev` – integration branch; merges to `main` for releases
- `feature/*` – one branch per feature, merged into `dev`
