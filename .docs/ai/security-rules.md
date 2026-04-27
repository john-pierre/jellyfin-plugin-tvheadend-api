# Security Rules

## Credential Handling

- TVHeadend credentials (username/password) are stored only in `PluginConfiguration` on the server
- **Credentials must never appear in client-facing URLs** — the relay system exists precisely to avoid this
- `ApiClient` passes credentials to `DigestAuthHandler` which handles HTTP Digest authentication server-side

## Relay Token Security

Relay tokens protect proxied stream URLs that are exposed to clients without Jellyfin authentication:

- **HMAC-SHA256**: tokens are hashed with a stable server-side secret before storage
- **Raw tokens are never persisted** — only HMAC hashes are stored in SQLite via `RelayTokenRepository`
- **Scoped**: each token is bound to a specific channel/resource
- **Short-lived**: configurable TTL (expiration time)
- **Max-use limited**: configurable maximum number of uses per token
- **Cleanup**: `RelayTokenCleanupService` (IHostedService) periodically purges expired tokens
- Token generation: `RelayTokenHasher` uses `HMACSHA256` with `System.Security.Cryptography`

## Digest Authentication

`DigestAuthHandler` (`Service/Auth/DigestAuthHandler.cs`) implements RFC 2617/7616 HTTP Digest authentication:

- Intercepts 401 challenges from TVHeadend
- Computes Digest response with proper nonce handling
- Handler chain: `ResilienceHandler` -> `DigestAuthHandler` -> `HttpClientHandler`

## Log Sanitization

`LogSanitizer` (`Service/Logging/LogSanitizer.cs`) redacts sensitive data from log output:

- Passwords
- Tokens
- Authentication headers
- Applied automatically by `PluginLogService` when serving log entries

**Rule: No secrets in log output at any level** — not in Debug, not in Verbose.

## Endpoint Security

| Endpoint Type | Auth Requirement |
|---------------|------------------|
| `PluginController` | Jellyfin admin elevation policy |
| `DashboardController` | Jellyfin admin elevation policy |
| `LogsController`, `DashboardLogsController` | Jellyfin admin elevation policy |
| `StreamingProfileController` | Jellyfin admin elevation policy |
| `RelayController` | Anonymous access, but **token-secured** (HMAC-validated, scoped, time-limited) |

## Input Validation

- All constructor parameters validated with `ArgumentNullException.ThrowIfNull`
- Relay token validation checks: existence, expiration, use count, scope match
- `RelayAuthorizationHelper` centralizes relay endpoint authorization checks

## Secrets Checklist for Code Changes

When modifying code that handles credentials, tokens, or authentication:

1. Verify credentials never appear in URLs returned to clients
2. Verify no secrets are logged (check all log statements in the change)
3. Verify relay tokens are properly scoped and time-limited
4. Verify `LogSanitizer` covers any new sensitive patterns
5. Verify `DigestAuthHandler` is used (not Basic auth or URL-embedded credentials)
