---
name: implementation-plan
description: "Creating, executing, and tracking implementation plans. Covers plan structure, progress tracking, PR creation, and PR description updates."
---

# Implementation Plan

An implementation plan is a detailed, structured document that outlines the steps required to implement a specific feature or fix a bug.

---

## Part 1 — Plan Structure

Every implementation plan must follow this hierarchical structure:

### Top-Level Sections

1. **Summary** — 1–2 sentences describing the feature or bug fix and the overall approach.

2. **Plan Overview and Progress**
    - Overview list of all development iterations contained in the plan
        - each development iteration has an icon at the end of its title indicating its progress:
            - not started: ⬜
            - completed: ✅
        - each development iteration lists its implementation steps as sub-steps

3. **Business Perspective**
   - *Business root cause analysis* — 3–4 sentences explaining the business problem or gap.
   - *Business fixing strategy* — 3–4 sentences describing the intended business-level solution.

4. **Technical Perspective**
   - *Technical root cause analysis* — 3–4 sentences describing the technical root cause.
   - *Technical fixing strategy* — 3–4 sentences describing the technical approach.

5. **Development Iterations**

### Development Iterations

Each development iteration:
- Is **focused on a specific aspect** of the implementation with clear goals and deliverables.
- Is **independently executable and testable**, allowing incremental progress and validation.
- Is **committed separately in git**, with a clear commit message reflecting the changes.
- Requires **all unit tests of changed modules to be implemented and passing** before committing.

Each iteration is composed of **implementation steps**:
- Each step only implements changes of one technology and language.
- Each step includes specific file paths, code patterns, and functions or classes to modify or create.
- Each step provides reasoning for why the changes are necessary.

**Technology-specific skill delegation:** When executing an implementation step, the executing agent **must** consult the relevant technology skill for conventions, build commands, and review checklists.

---

## Part 2 — Creating & Exporting a Plan

### Language

Implementation plans are always written in English, regardless of the user's language.

### File Location & Naming

Implementation plans are exported as Markdown files with the naming convention `<YYYY-MM-DD>-<short-kebab-case-title>.md` and stored in the `agent-results/implementation-plans/` folder.

---

## Part 3 — Working Through a Plan

### Step 1 — Identify which plan to work on

- Is there a plan added to the context by the user?
- Is there a plan in the description of a pull request linked by the user?
- Is there a plan in `agent-results/implementation-plans/` matching a description provided by the user?

### Step 2 — Updating the Plan After Each Step

Whenever an **implementation step** is finished:
1. Open the implementation plan file from `agent-results/implementation-plans/`.
2. Find the corresponding implementation step entry.
3. Change its progress icon from ⬜ to ✅.
4. Save the file.

When **all implementation steps of a development iteration** are completed:
1. Also change the progress icon of the **development iteration itself** from ⬜ to ✅.

### Step 3 — After Finishing Any Development Iteration

#### 3a — Ensure the Plan is Up to Date

#### 3b — 🛑 Mandatory Human Review — DO NOT CONTINUE BEFORE APPROVAL

Present a clear, structured review summary to the user listing **every file that was created or modified** during this iteration:

```
✅ Iteration <N> — "<Iteration Title>" complete.

Please review the following changed files before I continue:

- <file-path-1> — <one-sentence description of what changed>
- ...

⏸️ Waiting for your approval.
```

**Do NOT proceed** until the human user has explicitly confirmed.

#### 3c — Commit the Iteration

Commit message: `feat/fix(<scope>): one sentence reflecting the specific changes`

#### 3d — Push the Iteration

Ask the user: *"Shall I push the committed changes to the remote branch?"*

#### 3e — Create or Update the Pull Request

**If first iteration (no PR yet):**
```bash
gh pr create -d --title "feat(<scope>): <description>" --body "$(cat agent-results/implementation-plans/<plan-file>.md)" --base main
```

**If PR exists:**
```bash
gh pr edit --body "$(cat agent-results/implementation-plans/<plan-file>.md)"
```

### Step 4 — After Finishing All Iterations

- Change the PR from DRAFT to ready for review.
- Add a summary comment to the PR.

---

## Quick Reference

| Event | Actions |
|-------|---------|
| Implementation step finished | Update step icon to ✅ |
| All steps in an iteration finished | Update iteration icon to ✅ |
| **Any iteration complete** | Update plan → 🛑 present review summary → wait for approval → commit → ask to push → on push: update PR description |
| All iterations complete | Update PR from DRAFT to ready for review |

