# Security

The consolidated security reference for the plugin.

## Credential Handling

- TVHeadend credentials (username/password) are stored only in `PluginConfiguration` on the server.
- **Credentials must never appear in client-facing URLs** — the relay exists precisely to avoid this.
- `ApiClient` passes credentials to `DigestAuthHandler`, which performs HTTP Digest server-side.
- TVHeadend auth tokens are appended to stream and image URLs as `?auth=<token>` and **must be
  alphanumeric only** (`A-Z a-z 0-9`) so the URL stays FFmpeg-safe. `TokenValidator` rejects
  anything else, and the token endpoint retries create/refresh until it gets a conforming token.
  See `docs/architecture/decisions.md` (ADR-004).

## Relay Token Security

Relay tokens protect proxied stream URLs that are exposed to clients without Jellyfin
authentication.

- **HMAC-SHA256**: tokens are hashed with a stable server-side secret before storage.
- **Raw tokens are never persisted** — only hashes are stored in SQLite via `RelayTokenRepository`.
- **Scoped**: each token is bound to a specific channel/resource.
- **Short-lived**: configurable TTL.
- **Max-use limited**: configurable maximum number of uses per token.
- **Cleanup**: `RelayTokenCleanupService` (`IHostedService`) periodically purges expired tokens.
- `RelayTokenHasher` uses `HMACSHA256` from `System.Security.Cryptography`.

Validation pipeline (`RelayTokenValidatorService`), in order:

1. presence
2. well-formedness
3. hash lookup
4. expiry, with clock-skew tolerance
5. revocation
6. max-use enforcement
7. relay-type matching
8. optional strict scope validation

On success the use count is incremented **atomically** — the check and the increment are a single
conditional `UPDATE` so concurrent validations of the same token cannot exceed the limit.

> `RelayTokenHasher`'s server secret must be stable across restarts. An in-memory-only key
> invalidates every persisted token hash on restart, which silently breaks every issued token.

`RelayTokenOptions` enforces minimum bounds on TTL, max-uses, cleanup interval and clock skew.

Verified configuration keys and defaults:

| Key | Default |
|---|---|
| `EnableRelayTokenSecurity` | `true` |
| `StreamTokenTtlSeconds` | `120` |
| `StreamTokenMaxUses` | `25` |

## Digest Authentication

`DigestAuthHandler` (`Jellyfin.Plugin.TvHeadendApi/Service/Auth/DigestAuthHandler.cs`) implements
RFC 2617/7616 HTTP Digest authentication:

- Intercepts 401 challenges from TVHeadend.
- Computes the Digest response with proper nonce handling.
- Handler chain on the **API** path: `ResilienceHandler` → `DigestAuthHandler` → `HttpClientHandler`.

The relay hot path does not use that chain. It uses a separate `SocketsHttpHandler` with two-tier
fingerprinting and distinct image (10 s) and stream (24 h) clients, so connection pooling is not
disturbed by auth handlers.

## Log Sanitization

**Rule: No secrets in log output at any level — not in Debug, not in Verbose.**

Two mechanisms, and they are not interchangeable:

- `UrlBuilder.MaskSensitiveData(input, config)` (`Service/Backend/UrlBuilder.cs`) masks auth tokens,
  usernames and passwords **before** a value reaches a log call. Use this at the call site.
- `LogSanitizer` (`Service/Logging/LogSanitizer.cs`) is the persistence-time safety net applied by
  `PluginLogService`, replacing matches with `***REDACTED***`.

Do not rely on `LogSanitizer` alone — it only protects what is persisted for the dashboard, not
what the host logger already wrote.

## Endpoint Security

| Endpoint type | Auth requirement |
|---|---|
| `PluginController` | Jellyfin admin elevation policy |
| `DashboardController` | Jellyfin admin elevation policy |
| `LogsController`, `DashboardLogsController` | Jellyfin admin elevation policy |
| `StreamingProfileController` | Jellyfin admin elevation policy |
| `MetricsController` | Jellyfin admin elevation policy |
| `RelayController` | Class-level `[Authorize(Policies.LiveTvAccess)]`. Four actions (`status`, `stream/{channelId}`, `relay/stream/{channelId}`, `relay/images/{**path}`) are `[AllowAnonymous]` and **token-secured** instead (hashed, scoped, time-limited, use-counted) |

`RelayController` is deliberately the exception: media players and Jellyfin's image fetcher cannot
send Jellyfin auth headers, so those four actions authenticate with the relay token instead.

## Input Validation

- All constructor parameters validated with `ArgumentNullException.ThrowIfNull`.
- Relay token validation follows the 8-step pipeline above.
- `RelayAuthorizationHelper` (`Jellyfin.Plugin.TvHeadendApi/Api/RelayAuthorizationHelper.cs`)
  centralizes relay endpoint authorization checks.

## Secrets Checklist for Code Changes

When modifying code that handles credentials, tokens, or authentication:

1. Verify credentials never appear in URLs returned to clients.
2. Verify no secrets are logged — check every log statement in the change.
3. Verify relay tokens are properly scoped and time-limited.
4. Verify `LogSanitizer` covers any new sensitive pattern.
5. Verify `DigestAuthHandler` is used — not Basic auth, not URL-embedded credentials.

## Security Review — Attack Scenarios

Use this list when reviewing changes to authentication, token handling or logging.

1. Non-constant-time token comparison enabling timing attacks.
2. HMAC key entropy below 256 bits.
3. HMAC key reused across instances, or persisted across restarts without a rotation story.
4. Token validation happening **after** response headers are sent.
5. Path traversal through channel or resource identifiers.
6. Sanitizer bypass via structured-logging **property values** rather than the message template.
7. Log entries written before the sanitizer is initialised during startup.
8. Expired token records remaining queryable because cleanup does not actually `DELETE`.
9. Credentials leaking through exception messages.
10. `PluginConfiguration` serialized into a response without redaction.

Report findings as: **Severity** (critical / high / medium / low) → **Location** → **Issue** →
**Attack scenario** → **Recommendation**.

## Related

- `docs/architecture/decisions.md` — ADR-004 (auth token format), relay design decisions
- `docs/architecture/overview.md` — controller/authorization map
- `docs/guides/performance.md` — relay hot-path rules
