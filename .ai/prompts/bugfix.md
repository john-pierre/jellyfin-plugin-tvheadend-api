# Bugfix

Diagnose and fix a bug in jellyfin-plugin-tvheadend-api.

## Steps

### 1. Reproduce

- Clarify the exact symptom: error message, unexpected behavior, stack trace.
- Identify the service domain(s) involved (see `Service/` subfolders).
- If the bug involves TVHeadend communication, check `Service/Backend/ApiClient.cs` and `Service/Auth/DigestAuthHandler.cs`.
- If the bug involves streaming, check `Service/Relay/RelayService.cs` and `Service/Stream/`.
- Write down the expected vs. actual behavior.

### 2. Identify root cause

- Trace the call chain from the entry point (API controller or Jellyfin callback) through service layers.
- Check for common causes:
  - Null reference with nullable types enabled.
  - Missing `CancellationToken` forwarding in async chains.
  - `OperationTimeouts` mismatch in `Service/Resilience/`.
  - Race conditions in `DatabaseWriteCoordinator` or `RelayActivityTracker`.
  - Configuration state from `PluginConfiguration` not refreshed (check `ConfigurationProvider`).
  - Circuit breaker state in `Service/Health/HealthService.cs` blocking calls.

### 3. Fix

- Make the minimal change that addresses the root cause.
- Do not change unrelated code in the same commit scope.
- Ensure the fix handles edge cases (null inputs, empty collections, cancellation).
- Use `ILogger<T>` with static message templates for any new log statements.
- Never log secrets — passwords, tokens, auth headers must go through `LogSanitizer`.

### 4. Add regression test

- Add a test in the matching folder under `Jellyfin.Plugin.TvHeadendApi.Tests/Service/<Domain>/`.
- Test naming: `{MethodName}_{Scenario}_{ExpectedResult}`.
- Use xUnit `[Fact]` or `[Theory]` attributes.
- Use `Moq` for dependencies, `NullLogger<T>.Instance` for loggers.
- Assert with xUnit built-in assertions or FluentAssertions.
- The test must fail without the fix and pass with it.

### 5. Verify

- Build with zero warnings:
  ```
  dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
  ```
- Run all unit tests:
  ```
  dotnet test Jellyfin.Plugin.TvHeadendApi.Tests -c Release --no-build --filter "Category!=LiveIntegration"
  ```
- Confirm the new regression test passes.
