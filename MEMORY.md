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
