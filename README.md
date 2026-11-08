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
| `POST /api/auth/login` | anyone | email + password → AuthResponse + HttpOnly refresh cookie, or a 2FA challenge |
| `POST /api/auth/login/2fa` | anyone with a challenge | authenticator or recovery code → AuthResponse + cookie |
| `POST /api/auth/refresh` | anyone | rotates the refresh token; replaying an old one ends the whole session family |
| `POST /api/auth/logout` | anyone | revokes the cookie token family and clears the cookie |
| `POST /api/auth/forgot-password` | anyone | always 202; emails a reset link if the account exists |
| `POST /api/auth/reset-password` | anyone | token from email; signs out other devices; AuthResponse + cookie |
| `POST /api/auth/accept-invite` | anyone | confirms email and sets password; AuthResponse + cookie |
| `POST /api/auth/change-password` | signed in | returns fresh tokens; other devices are signed out |
| `GET /api/auth/me` | signed in | |
| `GET/POST /api/staff`, `PUT /api/staff/{id}`, `POST /api/staff/{id}/resend-invite` | Owner | invite, change role, disable |

Security rules:
- Passwords follow the same policy as the till (8+ chars, letters and numbers, not common, not containing the email name).
- 5 wrong passwords lock the account for 5 minutes; auth endpoints are rate-limited per IP.
- Unknown email and wrong password return the same error.
- Disabling a user, changing their role or password rotates their security stamp: existing access tokens stop working immediately.
- The bootstrap owner must change the temporary password; until then only account setup and session endpoints work. Owners and managers must also enroll in 2FA by default.
- Refresh tokens and reset/invite tokens are never stored in plain text. Sign-ins, failures, invites and changes are written to `AuditEvents`.

## Website sign-in

Use `credentials: 'include'` on token-issuing requests. AuthResponse contains
`accessToken`, `accessTokenExpiresAt` and `user`; keep access tokens in memory.
Never store refresh tokens in localStorage or send them in JSON.
Refresh and logout are bodyless POSTs with `X-Torvello-Client: web`.
The browser sends and rotates the HttpOnly `torvello_rt` cookie automatically.

When login returns `twoFactorRequired` and `twoFactorToken`, submit that token and
an authenticator `code` (or `recoveryCode`) to `POST /api/auth/login/2fa`.
Enroll with `GET /api/auth/2fa` and `POST /api/auth/2fa/setup` then `/2fa/enable`.
Recovery codes are shown once; keep them offline. Enabling/disabling 2FA or
replacing codes signs out sessions, so sign in again afterward.
Owners/managers with `user.twoFactorSetupRequired` must enroll before normal API use.

See [the security guide and API contract](docs/SECURITY.md) for the complete flow,
deployment configuration and production checklist.

## Till sync
| Endpoint | Who | Notes |
|---|---|---|
| `GET/POST /api/devices`, `DELETE /api/devices/{id}` | Owner | register a till (the key `{deviceId}.{secret}` is shown once, stored hashed), list, revoke |
| `GET /api/sync/ping` | till (`X-Device-Key`) | checks the key, returns the till name |
| `POST /api/sync/events` | till (`X-Device-Key`) | up to 200 outbox events; idempotent by event id; each event accepted or rejected with a reason |
| `GET /api/reports/daily?date=YYYY-MM-DD` | Owner, Manager | orders, sales, discounts, payment methods, top items for a Dhaka business day |

| `GET /api/inventory` | Owner, Manager | stock per ingredient (thousandths of g / ml / pcs), low-stock flag |
| `GET /api/inventory/{id}/movements` | Owner, Manager | latest 100 stock changes for one ingredient |

| `GET /api/hr/policies`, `GET /api/hr/policies/{id}` | everyone | published policies (Owner/Manager also see drafts), with your confirmation |
| `POST/PUT /api/hr/policies`, `GET .../{id}/acknowledgements` | Owner, Manager | write policies; who confirmed which version |
| `DELETE /api/hr/policies/{id}` | Owner | soft delete |
| `POST /api/hr/policies/{id}/acknowledge` | everyone | confirm the current version |
| `GET /api/hr/attendance?from=&to=` | Owner, Manager | clock-ins and hours per person (max 62 days) |

| `GET /api/customers`, `GET /api/customers/{id}` | Owner, Manager | customers with visits and spend; managers see masked phones |
| `PUT /api/customers/{id}` | Owner | edit details; queued for tills |
| `GET /api/employees`, `GET /api/employees/{id}` | Owner, Manager | staff from the till; personal details and national ID for the owner only |
| `GET /api/employees/me` | everyone | your own staff record, by email |
| `PUT /api/employees/{id}` | Owner | edit HR details; queued for tills |
| `GET /api/orders?date=`, `GET /api/orders/{id}` | Owner, Manager | bills with lines, payments and customer |
| `GET /api/sync/changes?after=` | till | owner edits for tills to apply (cursor based) |

Customers and staff details are entered at the till (`customer_saved`, `employee_saved` events)
and may also be edited by the owner on the website (stored in `ServerChanges`, encrypted, and
downloaded by tills). Every record keeps `UpdatedAt` / `UpdatedByName`; the newest edit wins.
National IDs are encrypted with ASP.NET Data Protection.

Changing a policy's title or text raises its version, so staff are asked to confirm again.
Attendance comes from `attendance_clock_in` / `attendance_clock_out` till events.

Inventory events from the till: `ingredient_saved` (upserts `InventoryIngredients`),
`stock_movement` (deliveries, wastage, counts) and the `stockUsage` list inside
`order_completed` (one negative `sale` movement per ingredient, unique per event).

Every uploaded event is stored raw in `SyncEvents`; `order_completed` is also projected into
`SyncedOrders` after re-checking that lines, discount, total and payments add up. Other event
types (shifts, cash movements, later stock and attendance) are kept for future reports.

Before production: set `Email:Provider=Smtp` with SMTP settings, set `AppLinks:WebBaseUrl`
to the web app URL, set `Cors:Origins` and `DataProtection:KeysPath`, and back up Data Protection keys
(invite/reset links otherwise stop working after a restart).

## Branches
- `main` – released code
- `dev` – integration branch; merges to `main` for releases
- `feature/*` – one branch per feature, merged into `dev`

## Public menu

`menu_published` device events contain a complete menu snapshot. The API keeps one
`PublishedMenus` row for the current single cafe; strictly newer `publishedAt`
(milliseconds) replaces it atomically. Equal/older snapshots are accepted without
changing the projection, and event retries remain idempotent. Empty categories
must be omitted; every item needs a size. Names are limited to 80 characters,
prices to 0�10,000,000 paisa, and menus to 50 categories and 500 items.

`GET /api/public/menu` requires no authentication and returns
`{ cafeName, updatedAt, categories }`, or 404 with title `Menu not published yet`.
It sends `Cache-Control: public, max-age=60` and an ETag; `If-None-Match` can return
304. It has a separate limit of 60 requests/minute per IP. Set
`PublicSite:Origins` to an array of explicit HTTPS cafe website origins
(Development defaults to `http://localhost:5175`). Its named `PublicMenu` CORS
policy allows GET without credentials and exposes ETag. Set `PublicSite:CafeName`
to override the default cafe name `Torvello`. The public DTO includes only menu
categories, items, sizes and add-ons, never recipes, costs, stock, staff or sales.
