---
mode: ask
description: Analyze and report the current context window composition — files, tools, skills, system prompts, conversation history — with estimated sizes. Use to understand token budget consumption before starting a task.
---

Analyze your current context window and provide a detailed breakdown of all components and their estimated sizes.

## Report structure

1. **System instructions & tool definitions** — list all tool categories with estimated character counts
2. **Project-specific instructions** — copilot-instructions, mode instructions, agent definitions, skills registry
3. **Workspace metadata** — directory tree, environment info
4. **Loaded repository files** — list each file with exact or estimated size
5. **Conversation history** — summarize turns with estimated sizes
6. **Summary table** — category, estimated characters, percentage of total

## Rules

- Distinguish between **loaded content** and **referenced but not loaded**
- Flag duplicate content
- Estimate total context usage in characters and approximate tokens (1 token ≈ 4 chars)
- Highlight the top 3 context consumers
- Give advice how to reduce context size

