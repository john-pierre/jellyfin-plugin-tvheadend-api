# jellyfin-plugin-tvheadend-api — Business Documentation

This directory is the central hub for cross-cutting business documentation.

---

## About This Documentation

### Business Workflow Documents (this directory)
The files in this directory describe **how domain processes work end-to-end**.

### Module Domain Descriptions (`business-description.md`)
Every module with domain logic has a dedicated `business-description.md` in its source directory.

**Maintenance rule:** Whenever a module is changed, its `business-description.md` **must** be updated by the documentation agent.

### Domain Language Glossary
📖 **[domain-language.md](domain-language.md)**

---

## Index

| Document | Description |
|----------|-------------|
| [domain-language.md](domain-language.md) | Canonical domain terminology glossary |
| [docs/architecture/overview.md](/docs/architecture/overview.md) | Layer diagram, dependency direction, module map |
| [docs/architecture/decisions.md](/docs/architecture/decisions.md) | ADR records for design decisions |
| [docs/architecture/module-responsibilities.md](/docs/architecture/module-responsibilities.md) | Per-module ownership and boundaries |
| [docs/guides/observability.md](/docs/guides/observability.md) | Logging categories, metrics instruments |
| [docs/guides/client-compatibility.md](/docs/guides/client-compatibility.md) | Client test matrix and known issues |
| [docs/guides/developer-onboarding.md](/docs/guides/developer-onboarding.md) | Architecture walkthrough and feature addition guide |

