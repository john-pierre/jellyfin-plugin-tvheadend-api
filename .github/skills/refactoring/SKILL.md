---
name: refactoring
description: "Code analysis for bugs, inconsistencies, dead code, duplication, and optimization opportunities across all domains. Ensures no breaking changes."
---

# Refactoring Skill

---

## Core Principle: No Breaking Changes

Every suggestion **must be safe for all environments**.

**Before suggesting any change, verify:**
1. Is this a public API consumed by Jellyfin or other modules?
2. Will this change affect the `ILiveTvService` contract?
3. Does this require configuration migration (PluginConfiguration defaults)?
4. Are existing tests still valid?

---

## What Counts as a Breaking Change

| Action | Breaking? | Reason |
|--------|-----------|--------|
| Remove/rename a public API on PluginController | ✅ Yes | Admin UI depends on it |
| Change ILiveTvService method behavior | ✅ Yes | Jellyfin contract |
| Change PluginConfiguration defaults | ⚠️ Caution | Existing installations |
| Remove/rename internal services | ✅ Safe | Internal, DI-managed |
| Add new endpoints/services | ✅ Safe | No existing consumer impact |
| Refactor Helper layer internals | ✅ Safe | No external contracts |

---

## Analysis Checklist

### 1. Bugs
- [ ] Null handling, CancellationToken forwarding, error handling, resource leaks, race conditions

### 2. Dead Code
- [ ] Unused imports, variables, methods, commented-out code, unreachable code

### 3. Code Quality
- [ ] High complexity, duplication, naming violations, magic numbers, excessive nesting, methods >30 lines

### 4. Security
- [ ] Hardcoded secrets, sensitive data in logs, missing input validation

### 5. Cross-Module Consistency
- [ ] Consistent patterns across services, naming consistency with glossary, configuration alignment

---

## Output Format

```
## Refactoring Analysis: <target>

**Scope:** <module / component / full repo>
**Risk Level:** <Low / Medium / High>
**Findings:** <X> bugs, <Y> improvements, <Z> suggestions
```

Per finding:
- **Severity:** ❌ Bug / ⚠️ Improvement / 💡 Suggestion
- **File:** `<file-path>:<line>`
- **Impact:** What happens if not fixed
- **Current:** code block
- **Suggested:** code block
- **Breaking Change:** Yes / No — explanation

