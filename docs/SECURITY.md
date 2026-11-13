# Security

## Threat model and protections

The API protects staff and customer records from stolen credentials, token theft,
cross-site requests, brute force and accidental exposure. A compromised API host,
database administrator or browser script remains a serious trust boundary.

- Passwords use ASP.NET Core Identity salted password hashing and the shared password
  policy. Unknown accounts and wrong passwords share an error. Five failures lock
  an account for five minutes, including second-factor failures.
- Short-lived signed JWTs carry a security stamp checked against Identity on every
  request. Password, role, account-status and 2FA security changes invalidate sessions.
- Refresh tokens are random, stored as SHA-256 hashes, rotated on refresh, and sent
  only in an HttpOnly Strict cookie. Reuse revokes the token family. Logout revokes
  the family. Refresh and logout require the custom CSRF header and explicit
  credentialed CORS origins. Keep access tokens in memory and send credentials with
  browser auth calls. HttpOnly limits token extraction; it cannot stop an XSS script
  acting as the user.
- Authenticator TOTP and single-use recovery codes use Identity token storage.
  Owners and managers must enroll by default. Challenges are Data Protection
  payloads bound to user ID and security stamp with a five-minute lifetime.
  Enable, disable and recovery-code replacement rotate the stamp and revoke refresh
  sessions, then issue a fresh session and cookie to the caller. Setup preserves the current session
  until enable, and cannot reset a key while 2FA is enabled.
- Data Protection uses application name Torvello and persisted keys. Development
  defaults to App_Data/keys; Production requires DataProtection:KeysPath.
  Windows uses DPAPI unless DataProtection:CertificateThumbprint selects a
  certificate. On other hosts configure a certificate for encryption at rest.
  Identity invite/reset links and national-ID encryption depend on retaining keys.
- National IDs and server change payloads use Data Protection. Device API keys
  are shown once and stored hashed; revoke lost tills promptly.
- Auth endpoints, including second-factor endpoints, use per-IP rate limits.
  Audit events record sign-ins, failures, token reuse and security changes without
  recording passwords, authenticator secrets or recovery codes.
- All responses carry nosniff, frame denial, no-referrer, restrictive CSP,
  Permissions-Policy and same-origin opener headers. Kestrel omits Server.
  Outside Development the API enables HSTS and HTTPS redirection.

## Production checklist

- Serve API and website over HTTPS on the same site so SameSite=Strict cookies work.
  Configure trusted proxy forwarding before HTTPS redirection where required.
- Set Cors:Origins to explicit HTTPS website origins; no wildcard origins.
- Put a random JWT signing key (at least 32 UTF-8 bytes), SMTP credentials, DB
  credentials and certificate private keys in a secrets vault.
- Use a least-privilege database user, never postgres. Non-Development startup
  rejects postgres, short JWT keys and non-HTTPS CORS origins.
- Configure a durable DataProtection:KeysPath, restrict filesystem access to the
  service account and configure certificate protection on non-Windows hosts.
- Back up Data Protection keys and the decryption certificate/private key securely;
  DPAPI-protected backups require the appropriate Windows account/machine context.
  Test restoration alongside encrypted database backups. Losing keys loses access
  to protected national IDs and invalidates outstanding protected links.
- Use encrypted backups, retention/access controls, patch monitoring, audit review,
  accurate server clocks for TOTP and tested incident/session-revocation procedures.
- Keep Security:RequireTwoFactorForManagers enabled. Store recovery codes offline.
- Configure SMTP and AppLinks:WebBaseUrl; remove bootstrap secrets after provisioning.
- Run build, tests, vulnerability audit and dotnet format before release.

## Production deployment on the droplet

See [the deployment runbook](../deploy/README.md). Caddy terminates HTTPS with
DNS-only Cloudflare records; only Caddy publishes host ports. PostgreSQL uses
`torvello_app`, a non-superuser database owner with no CREATEDB or CREATEROLE.
Ownership permits startup migrations. The administrator password is for operations.

Keep `/srv/torvello/app/.env` mode 600 and out of Git, containing random JWT and DB
secrets and SMTP credentials. Remove the bootstrap password after the first password
change. Release snapshots may contain older secrets; protect and retire them too.

Forwarded headers run before redirects, rate limits and authentication. They are
disabled without configured `ForwardedHeaders:KnownProxies` IPs or `KnownNetworks`
CIDRs. Defaults are cleared, invalid entries fail startup and only one hop is read.
Compose trusts `172.30.80.0/24`; every container on that network is in the trust
boundary. Do not attach unrelated containers or publish API directly. Changing to
Cloudflare proxying or adding another proxy requires a fresh trust review.

The `api_keys` named volume persists Data Protection keys. On this Linux deployment
they are **unencrypted at rest**, an accepted temporary exception to the certificate
recommendation above. Restrict host and Docker access, and encrypt off-host backups
of both keys and database. Follow-up: configure certificate-backed key encryption,
protect its private key separately, and test key rotation and full disaster recovery.
`/health` is anonymous and not rate limited; it performs only a cheap database check
and exposes no version, configuration or failure details.

## API contract

# Security contract between the API and the owner website (both tasks follow this exactly)

## Refresh token moves to an HttpOnly cookie
- Cookie name `torvello_rt`, `HttpOnly`, `SameSite=Strict`, `Path=/api/auth`, `Secure` (always, except when the
  request is plain http in Development), expires with the refresh token. The refresh token is **never** in a JSON
  body or in localStorage any more.
- `AuthResponse` JSON: `{ accessToken, accessTokenExpiresAt, user }` (no `refreshToken`, no `refreshTokenExpiresAt`).
  `user` gains `twoFactorEnabled: boolean` and `twoFactorSetupRequired: boolean`.
- `POST /api/auth/refresh` — no body; reads the cookie; rotates it (same reuse detection as today); returns
  AuthResponse. `POST /api/auth/logout` — no body; revokes the cookie's token family and deletes the cookie; 204.
- CSRF: both cookie endpoints (`refresh`, `logout`) require the header `X-Torvello-Client: web`; without it → 400.
  (A custom header forces a CORS preflight, so other sites cannot call them.) CORS: explicit origins only
  (`Cors:Origins` config), `AllowCredentials()`. The website calls these with `credentials: 'include'`.
- Every endpoint that issues tokens (login, login/2fa, accept-invite, reset-password, change-password,
  2fa/enable, 2fa/recovery-codes, 2fa/disable) sets the
  cookie the same way.

## Two-factor sign-in (TOTP, authenticator app)
- `POST /api/auth/login` `{ email, password }`:
  - no 2FA on the account → 200 AuthResponse + cookie (as today).
  - 2FA enabled → 200 `{ twoFactorRequired: true, twoFactorToken }` (no cookie). `twoFactorToken` is a
    Data-Protection time-limited payload (purpose `Torvello.Login2fa.v1`, 5 minutes, user id + security stamp).
- `POST /api/auth/login/2fa` `{ twoFactorToken, code?: string, recoveryCode?: string }` → AuthResponse + cookie.
  Wrong code counts as a failed access (Identity lockout: 5 tries → 5 min). Rate-limited like login.
- Enrolment (authenticated):
  - `GET /api/auth/2fa` → `{ enabled, recoveryCodesLeft }`
  - `POST /api/auth/2fa/setup` → `{ sharedKey, otpauthUri }` (resets the authenticator key; key formatted in groups
    of 4; URI `otpauth://totp/Torvello:{email}?secret=…&issuer=Torvello&digits=6`)
  - `POST /api/auth/2fa/enable` `{ code }` → `{ recoveryCodes: string[10], session: AuthResponse }`
  - `POST /api/auth/2fa/recovery-codes` `{ code }` → `{ recoveryCodes: string[10], session: AuthResponse }` (replaces old ones)
  - `POST /api/auth/2fa/disable` `{ password, code }` → 200 `{ session: AuthResponse }` + cookie. Owners/managers may not disable it while
    `Security:RequireTwoFactorForManagers` is true (400 with a clear message).
- Policy `Security:RequireTwoFactorForManagers` (default **true**): owners and managers without 2FA get
  `user.twoFactorSetupRequired = true`, and a gate (like the existing PasswordChangeGate) allows only
  `GET/POST /api/auth/2fa*`, `/api/auth/logout`, `/api/auth/refresh`, `/api/auth/me` and change-password until 2FA
  is enabled; other requests → 403 `{ title: "Set up two-step sign-in first" }`.
- Audit events: `2fa_enabled`, `2fa_disabled`, `2fa_recovery_codes_reset`, `2fa_recovery_code_used`,
  `login_2fa_failed`. Enabling/disabling 2FA or resetting codes updates the security stamp and revokes all previous refresh tokens (signs out other sessions).
  All three return a fresh `session: AuthResponse` and set a new `torvello_rt` cookie using the login cookie rules.
  The website replaces its in-memory access token with `session.accessToken`; the new cookie supports refresh.
  Access tokens and cookies issued before the change are refused, including the caller's previous tokens.

## Errors
ProblemDetails `{ title }` as today; the website shows `title`.

## Public menu endpoint

`GET /api/public/menu` is intentionally anonymous, including during account setup.
Only this endpoint uses the named `PublicMenu` CORS policy: GET from the explicit
`PublicSite:Origins` array, no credentials, with ETag exposed. Production origins
must use HTTPS; Development permits HTTP (`http://localhost:5175` by default).
The separate public-menu rate limit is 60 requests per minute per remote IP.
Responses are publicly cacheable for 60 seconds and support ETag/304. No published
snapshot returns 404 with title `Menu not published yet`.

Only device-key-authenticated `menu_published` events can publish. Snapshots are
validated before storing their projection; atomic timestamp comparison prevents
older or equal snapshots replacing newer content. Explicit DTO whitelists at
projection and response boundaries exclude recipes, ingredients, costs, stock,
staff and sales, even if extra fields arrive in a raw sync event. Raw events remain
private. Public menu names are at most 80 characters, prices are integer paisa in
0�10,000,000, each item has at least one size, and a snapshot has at most 50
categories and 500 items. Configure `PublicSite:CafeName` for the public cafe name.
