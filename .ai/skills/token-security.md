# Token Security Skill

Authentication and authorization tokens for jellyfin-plugin-tvheadend-api.

## Overview

The plugin uses two distinct token systems: persistent auth tokens for TVHeadend communication, and short-lived HMAC relay tokens for stream access. Auth code lives in `Service/Auth/`, relay token code in `Service/Relay/`.

## TVHeadend auth tokens

### TokenService

`TokenService` (`Service/Auth/TokenService.cs`) — implements `ITokenService`:

- Generates persistent authentication tokens for TVHeadend API access.
- Tokens are stored in plugin configuration.
- Used by `DigestAuthHandler` for HTTP Digest authentication.

### TokenValidator

`TokenValidator` (`Service/Auth/TokenValidator.cs`):

- Validates token format constraints.
- Tokens must be **alphanumeric only** — this is a hard requirement for FFmpeg compatibility (FFmpeg passes tokens in URLs and special characters cause parsing failures).
- Rejects tokens with non-alphanumeric characters.

### DigestAuthHandler

`DigestAuthHandler` (`Service/Auth/DigestAuthHandler.cs`):

- `DelegatingHandler` implementing HTTP Digest authentication (RFC 2617).
- Handles nonce extraction, response hash computation, automatic retry on 401.
- Inserted into the `HttpClient` pipeline via `ServiceRegistrator.cs`.

## Relay tokens (HMAC-SHA256)

### Token issuance

`RelayTokenService` (`Service/Relay/RelayTokenService.cs`) — implements `IRelayTokenService`:

- Issues short-lived tokens scoped to a specific resource, user, and device.
- Token properties:
  - **TTL**: configurable expiration time.
  - **Max uses**: optional usage limit per token.
  - **Scope**: bound to relay type (stream), channel ID, user ID, device ID.
- Tokens are HMAC-SHA256 signed using a per-instance secret key.

### Token validation

`RelayTokenValidatorService` (`Service/Relay/RelayTokenValidatorService.cs`) — implements `IRelayTokenValidator`:

- Validates on every relay request:
  1. Token exists in the database.
  2. HMAC signature is valid.
  3. Token has not expired (TTL check).
  4. Usage count has not exceeded max uses.
  5. Resource scope matches the requested resource.

### Token hashing

`RelayTokenHasher` (`Service/Relay/RelayTokenHasher.cs`):

- HMAC-SHA256 computation for token signing and verification.
- Key is generated once and held in memory for the plugin lifetime.
- Implements `IDisposable` — disposes the HMAC instance.

### Token configuration

`RelayTokenOptions` (`Service/Relay/RelayTokenOptions.cs`):

- Reads from `PluginConfiguration`:
  - `StreamTokenTtlMinutes` — token time-to-live.
  - `StreamTokenMaxUses` — maximum usage count (0 = unlimited).
  - `StreamTokenEnabled` — master enable/disable flag.

### Token persistence

`RelayTokenRepository` (`Service/Relay/RelayTokenRepository.cs`) — implements `IRelayTokenRepository`:

- CRUD operations for `RelayTokenRecord` entities.
- Uses `RelayTokenDbContext` (EF Core + SQLite).
- All writes go through `DatabaseWriteCoordinator`.

### Token cleanup

`RelayTokenCleanupService` (`Service/Relay/RelayTokenCleanupService.cs`):

- Registered as `IHostedService`.
- Periodically removes expired tokens from the database.
- Prevents unbounded token table growth.

## Rules

- Auth tokens: alphanumeric only (FFmpeg compatibility).
- Relay tokens: always short-lived, always scoped to a specific resource.
- Never log token values — use `LogSanitizer` if token context must be logged.
- Never store relay token plaintext — only the HMAC hash is persisted.
- Token validation must happen before any stream data is proxied.
- `RelayTokenOptions` must have safe defaults (tokens enabled, reasonable TTL).
- All token database operations use `DatabaseWriteCoordinator`.
