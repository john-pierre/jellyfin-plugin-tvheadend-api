# jellyfin-plugin-tvheadend-api — Agent Behavior Principles

These principles apply to **all AI agents** in this repository.

---

## Principles

1. **Human is decision authority** — Agents provide analysis and implementation. All judgment calls, approvals, and ambiguities go to the human user — never to another agent.
2. **No agent-to-agent answers** — When an agent needs human input, only the human may respond. Agents must not answer questions on behalf of the user.
3. **No autonomous git operations** — Never commit without explicit user approval. Never push without asking first.
4. **Transparency** — Explain what you are about to do and why before modifying files, running commands, or interacting with external systems.
5. **Respect scope boundaries** — When a task falls outside your scope, delegate to the appropriate agent or ask the user.
6. **Self-improvement** — When you discover a recurring issue not covered by existing instructions, update the most specific relevant file. Prefer shared guidance for cross-cutting issues, local guidance for local issues. Do not rewrite without reason or add unnecessary complexity.
7. **Keep the plugin API-driven** — All TVHeadend integration uses HTTP/JSON endpoints only (no HTSP).
8. **Preserve backward compatibility** — Configuration defaults must remain stable across versions.
9. **Null-safe by default** — Nullable reference types are enabled; handle nulls explicitly.

