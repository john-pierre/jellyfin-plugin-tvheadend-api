---
name: pr-review
description: "PR review for jellyfin-plugin-tvheadend-api. Evaluates 7 mandatory categories: Documentation, Dependencies, Security, Bugs, Outdated code, Standards, Optimization."
---

# PR Review Skill

Every review **must** evaluate 7 mandatory categories and produce findings structured by those categories.

---

## Mandatory Pre-Steps

1. **Read `.github/copilot-instructions.md`** and the [.NET Skill](/.github/skills/dotnet/SKILL.md).
2. **Identify domains affected** by examining the PR diff.
3. **Apply the .NET review checklist** (§5 of dotnet/SKILL.md).
4. **Check domain language compliance** against `domain-language.md`.

---

## 7 Mandatory Review Categories

### 1. Documentation
- [ ] Implementation documented sufficiently for its complexity
- [ ] XML docs on public members of public types
- [ ] Important non-obvious logic has comments

### 2. Dependencies
- [ ] New dependencies genuinely necessary and justified
- [ ] No outdated, duplicated, or risky dependencies
- [ ] Jellyfin SDK version compatible with targetAbi in manifest.json

### 3. Security
- [ ] No hardcoded secrets, passwords, API keys, tokens
- [ ] Sensitive data masked in logs (use `UrlBuilder.MaskSensitiveData`)
- [ ] Input validation present
- [ ] Auth tokens handled securely

### 4. Bugs
- [ ] No broken logic, incorrect conditions, unreachable code
- [ ] Edge cases handled (null, empty, boundary)
- [ ] CancellationToken forwarded to all async calls
- [ ] No resource leaks or race conditions

### 5. Outdated Code / Obsolete Patterns
- [ ] No deprecated APIs or legacy patterns
- [ ] Uses current .NET 8 idioms (file-scoped namespaces, records where appropriate)
- [ ] Patterns match codebase conventions

### 6. Standards and Conventions
- [ ] All code/comments in **English**
- [ ] PR title follows Conventional Commits
- [ ] Naming follows domain language glossary and naming-conventions.md
- [ ] No unrelated changes mixed in
- [ ] Nullable handled explicitly (no unwarranted `!` suppressions)

### 7. Optimization Potential
- [ ] No unnecessary complexity
- [ ] No significant duplication
- [ ] `ConfigureAwait(false)` on awaits in library code
- [ ] No sync-over-async or unnecessary allocations

---

## Verdict Format

### Overall Verdict
- ✅ **Ready to merge** — All checks pass
- ⚠️ **Needs fixes** — Non-blocking issues
- ❌ **Blocking issues** — Must fix before merging

### Finding Format

- **Severity:** ❌ Blocking / ⚠️ Should Fix / 💡 Suggestion
- **File:** `<file-path>:<line>`
- **Description:** What the issue is and why
- **Suggestion:** How to fix it

### Checklist Summary

| # | Category | Status | Notes |
|---|----------|--------|-------|
| 1 | Documentation | ✅/⚠️/❌ | |
| 2 | Dependencies | ✅/⚠️/❌ | |
| 3 | Security | ✅/⚠️/❌ | |
| 4 | Bugs | ✅/⚠️/❌ | |
| 5 | Outdated Code | ✅/⚠️/❌ | |
| 6 | Standards | ✅/⚠️/❌ | |
| 7 | Optimization | ✅/⚠️/❌ | |

---

## Database (additional checks)

- No service-locator pattern — dependencies are constructor-injected, never resolved from a
  container at call time.
- SQLite identifiers use `lowercase_with_underscore`.
- No raw SQL without parameterization. Raw SQL is acceptable where an EF Core API is not stable
  across the supported Jellyfin versions — see `EfCoreApiCompatibilityTests`.
- Every write goes through `DatabaseWriteCoordinator`.

## Additional review checks

- Constructors with more than 5 parameters — a sign the type owns too much.
- Methods longer than ~30 lines.
- No test-only code paths in production code.

Report each finding with a severity of **error** (must fix), **warning** (should fix) or
**note** (consider).
