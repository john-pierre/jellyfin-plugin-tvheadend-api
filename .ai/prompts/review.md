# Code Review

Review code changes in jellyfin-plugin-tvheadend-api.

## Checklist

### Naming and domain language
- [ ] Identifiers match the domain glossary (`docs/guides/naming-conventions.md`).
- [ ] No abbreviations except established ones (TVH, DI, API, DVR, EPG).
- [ ] Test names follow `{MethodName}_{Scenario}_{ExpectedResult}`.

### Architecture boundaries
- [ ] No cross-domain direct references that bypass interfaces.
- [ ] Service dependencies flow through constructor injection.
- [ ] Business logic stays in `Service/` — not in controllers (`Api/`) or models (`Model/`).
- [ ] New services registered as singletons in `ServiceRegistrator.cs`.
- [ ] TVHeadend communication uses HTTP/JSON only (no HTSP).

### Dependency injection
- [ ] All dependencies injected via constructor.
- [ ] No service locator patterns (no `IServiceProvider.GetService` in service classes).
- [ ] `IHostedService` implementations registered correctly.
- [ ] `HttpClient` obtained from `IHttpClientFactory`, not instantiated directly.

### Error handling
- [ ] Specific exception types caught (no bare `catch (Exception)`).
- [ ] `CancellationToken` forwarded to every async call in the chain.
- [ ] Nullable reference types handled explicitly — no unguarded `!` suppressions.
- [ ] `OperationCanceledException` not swallowed silently.

### Logging
- [ ] Uses `ILogger<T>` with static message templates.
- [ ] No string interpolation in logger calls (`$"..."` inside `Log*` methods).
- [ ] No secrets in log output — passwords, tokens, auth headers must go through `LogSanitizer`.
- [ ] Appropriate log levels: Debug for flow, Information for state changes, Warning for recoverable issues, Error for failures.

### Async patterns
- [ ] All I/O methods are async with `Async` suffix.
- [ ] No `Task.Result` or `.Wait()` (sync-over-async).
- [ ] `ConfigureAwait(false)` used in library-style code.
- [ ] No `async void` except event handlers.

### Test coverage
- [ ] New/modified public methods have corresponding tests.
- [ ] Tests use Moq for dependencies, not real implementations (except integration tests).
- [ ] Edge cases covered: nulls, empty inputs, cancellation, exceptions.
- [ ] No test logic in production code (no `#if DEBUG` test hooks).

### Security
- [ ] No hardcoded secrets or credentials.
- [ ] Relay tokens validated via `RelayTokenValidatorService` before granting access.
- [ ] Auth tokens use `TokenValidator` format constraints (alphanumeric for FFmpeg compatibility).
- [ ] HMAC-SHA256 relay tokens are short-lived and scoped.

### Database
- [ ] All writes go through `DatabaseWriteCoordinator`.
- [ ] Schema uses `lowercase_with_underscore` column naming.
- [ ] No raw SQL without parameterization.

### Performance
- [ ] No unnecessary allocations in hot paths (relay streaming, log processing).
- [ ] No blocking calls in async methods.
- [ ] Collections sized appropriately (no unbounded growth).
