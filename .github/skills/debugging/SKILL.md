---
name: debugging
description: "Root-cause triage and regression-test workflow for jellyfin-plugin-tvheadend-api bugs. Covers reproduction, common causes per domain, the minimal fix, and the verification gate."
---

# Debugging Skill

Diagnose and fix a bug without guessing. Every step below produces evidence; a fix without a
failing-then-passing regression test is not finished.

---

## 1 — Reproduce

- Clarify the exact symptom: error message, unexpected behaviour, stack trace.
- Identify the service domain(s) involved — see the subfolders under
  `Jellyfin.Plugin.TvHeadendApi/Service/`.
- TVHeadend communication → `Service/Backend/ApiClient.cs`, `Service/Auth/DigestAuthHandler.cs`.
- Streaming → `Service/Relay/RelayService.cs`, `Service/Stream/`.
- Write down expected versus actual behaviour before changing anything.

---

## 2 — Identify the root cause

Trace the call chain from the entry point (API controller or Jellyfin callback) down through the
service layers. Common causes in this codebase:

- Null reference despite nullable reference types being enabled.
- Missing `CancellationToken` forwarding in an async chain.
- `OperationTimeouts` mismatch in `Service/Resilience/`.
- Race conditions around `DatabaseWriteCoordinator` or `RelayActivityTracker`.
- Configuration read directly instead of through `ConfigurationProvider`, so it never refreshes.
- Circuit breaker state in `Service/Health/HealthService.cs` blocking calls.
- An EF Core or Jellyfin API whose signature differs between supported server versions — the plugin
  resolves both from the **server**, not from its own package references. See
  `Jellyfin.Plugin.TvHeadendApi.Tests/EfCoreApiCompatibilityTests.cs`.

> A bug that reproduces only on one Jellyfin version is an ABI problem until proven otherwise.
> Run the E2E stack against the other versions: `JELLYFIN_IMAGE=jellyfin/jellyfin:10.11.11 docker/run-e2e-tests.sh`.

---

## 3 — Fix

- Make the minimal change that addresses the root cause.
- Do not change unrelated code in the same commit scope.
- Handle the edge cases the root cause exposed: null inputs, empty collections, cancellation.
- Use `ILogger<T>` with **static** message templates for any new log statement.
- Never log secrets — mask with `UrlBuilder.MaskSensitiveData` at the call site. See
  `docs/guides/security.md`.

---

## 4 — Add a regression test

- Place it in the matching folder under `Jellyfin.Plugin.TvHeadendApi.Tests/Service/<Domain>/`.
- Namespace: `Jellyfin.Plugin.TvHeadendApi.Tests.Service.<Domain>`.
- Naming: `{MethodName}_{Scenario}_{ExpectedResult}`.
- xUnit `[Fact]` / `[Theory]`; `Moq` for dependencies; `NullLogger<T>.Instance` for loggers.
- **The test must fail without the fix and pass with it.** Verify both directions — revert the fix
  temporarily and confirm the test goes red. A test that was never seen failing proves nothing.
- No wall-clock sleeps or filesystem-permission tricks: they behave differently under root and on
  other platforms. Inject the failure deterministically instead.

---

## 5 — Verify

```bash
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj \
  -c Release --no-build --filter "Category!=LiveIntegration"
```

Build must be warning-free (`TreatWarningsAsErrors` is on). Confirm the new regression test passes,
and state the result — do not claim a fix without the output.
