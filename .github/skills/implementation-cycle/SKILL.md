---
name: implementation-cycle
description: "Defines the mandatory development iteration cycle that all coding agents follow when implementing code changes. Each iteration executes four steps — implement, test, review, document — in strict sequential order, and ends at an explicit user gate before the next iteration begins."
---

# Implementation Cycle

## Development Iteration Cycle

⚠️ **MANDATORY PROCESS — You MUST follow this cycle exactly. No exceptions.**

Implementing code changes is always done in a structured development cycle. You execute **one iteration at a time**, and within each iteration you execute **one step at a time** in strict sequential order.

Development iterations are defined in the implementation plan when there is one, otherwise you define them based on logical code changes.

> ⚠️ **This skill defines the generic process only.** Tech-specific additions (test tooling, verify commands, review checklist, documentation scope) are defined in the **technology skill's §4** (e.g., `dotnet/SKILL.md §4`). Always read both together.

### Strict rules

1. **Execute steps 1–4 in strict sequential order within a single turn.** Do NOT skip or reorder steps. Do NOT start the next iteration without user confirmation.
2. **NEVER skip steps.** Every iteration MUST include all four steps. Do NOT skip the review step. Do NOT skip the documentation step.
3. **Always announce the current step.** Before starting each step, state which iteration and step you are on, e.g.: *"🔄 Iteration 1 — Step 1: Implement production code"*.
4. **STOP only after Step 4.** After completing Step 4, you MUST end your turn and wait for the user to respond before starting the next iteration.

### Step sequence (strictly in this order)

| Step | Name | Gate |
|------|------|------|
| **1** | Implement production code | ▶️ Continue to Step 2 |
| **2** | Implement tests and verify | ▶️ Continue to Step 3 |
| **3** | Review code | ▶️ Continue to Step 4 |
| **4** | Update documentation | ▶️ Continue to iteration-end gate |
| **⛔** | **Iteration-end gate** | ⛔ STOP — ask user to review & commit, then end turn |

### Step 1 — Implement production code

- Implement one implementation step of an implementation plan.
- When there is no implementation plan, implement one logical change at a time.
- Apply the **Step 1 additions from the technology skill's §4**.
- ▶️ **Continue immediately** to Step 2.

### Step 2 — Implement tests and verify

- Write all tests for the modified module(s) using the test tooling defined in the **technology skill's §4 Step 2 additions**.
- Verify that all production code and test code compile and all tests pass.
- ▶️ **Continue immediately** to Step 3.

### Step 3 — Review code

- Review all added and modified code using the review checklist defined in the **technology skill's §4 Step 3 additions**.
- If there are any issues or improvements, fix them and re-verify tests until the code is clean.
- ▶️ **Continue immediately** to Step 4.

### Step 4 — Update documentation

- Identify new domain language terms and propose a definition. Ask the user if this is the correct definition and if you should add it to the domain language glossary.
- Instruct the documentation agent to re-analyze every module modified in this iteration, following the scope and invocation defined in the **technology skill's §4 Step 4 additions**.
- ▶️ **Continue immediately** to the iteration-end gate.

### ⛔ Iteration-end gate

After completing all four steps, present a **summary of all changes** in this iteration (production code, tests, review findings, documentation). Then ask:

> *"Iteration X complete. Have you reviewed all changes to production code, test code, and documentation? Shall I create a commit?"*

Then **end your turn** and wait for the user to respond. Do NOT start the next iteration or create a commit without explicit user confirmation.

