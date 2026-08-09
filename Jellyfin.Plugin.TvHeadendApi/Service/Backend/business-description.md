# Backend Service

Core HTTP client layer for all TVHeadend API communication.

## Detailed Description

The Backend module centralizes HTTP client creation, URL construction, grid pagination, idnode value extraction, and request cloning for retry support.

`ApiClient` creates authentication-aware `HttpClient` instances — anonymous requests use `IHttpClientFactory`-managed clients with connection pooling, while authenticated requests build an explicit `HttpClientHandler` with `CredentialCache` + `DigestAuthHandler` wrapped in a `ResilienceHandler` for retry/circuit-breaker parity. This avoids casting factory-internal handlers, which is unsafe under .NET 9's `SocketsHttpHandler` default.

`UrlBuilder` constructs all TVHeadend URLs (base URL, API endpoints, auth-token resource URLs) and masks sensitive data (auth tokens, passwords, usernames) in strings before logging.

`GridFetcher` implements TVHeadend's two-phase grid pagination: a probe request discovers the total entry count, then a single follow-up fetches all entries. It sanitizes invalid UTF-8 bytes from DVB sources before JSON deserialization to prevent `DecoderFallbackException`.

`IdNodeValueHelper` reads values from TVHeadend's idnode JSON responses, handling the dual format where values appear either as direct fields or inside a `params` array. Supports string, int, bool, and string-array extraction with lenient type coercion.

`HttpRequestCloner` deep-clones `HttpRequestMessage` instances (including content bytes, headers, and options) so that `DigestAuthHandler` and `ResilienceHandler` can retry requests whose original content stream has been consumed.

## Domain Context

- **Use Case:** Centralized, authentication-aware HTTP communication with TVHeadend
- **Module Type:** Infrastructure Service
- **Key Domain Entities:** ApiClient, UrlBuilder, GridFetcher, IdNodeValueHelper, HttpRequestCloner

## Internal Dependencies

- **`Configuration`** — `ConfigurationProvider` for reading plugin configuration without `Plugin.Instance` coupling
- **`Resilience`** — `ResilienceHandler` wraps authenticated clients for retry + circuit breaker
- **`Auth`** — `DigestAuthHandler` handles TVHeadend's Digest authentication challenges
- **`Common`** — `JsonDefaults` for consistent JSON deserialization settings
