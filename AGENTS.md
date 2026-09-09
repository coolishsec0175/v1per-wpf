# AGENTS.md

Guidance for OpenCode agents working with code in this repo.

## General Principles
- Generate concise, short solutions for new modules or code.
- Watch for over-engineering and oversized files that need refactoring.
- Watch for weird syntax or style that mismatches the rest of the codebase.
- Watch for obvious bugs and the blast radius of errors.
- No emojis or special characters in comments.
- Write `activity-log.md` in `/docs` to refer back to if confused.
- Run major changes by the user first — do not execute blindly.
- Review existing files before any refactor or change.
- Markdown files use kebab-case naming (e.g. `some-description-changes.md`).
- Comments: one-liner, one sentence.

## Code Quality
- Choose the right data structures and algorithms for the problem.
- Don't expose data needlessly (least privilege).
- No external libraries unless absolutely necessary.
- Use the project dependency file for correct versions.
- Avoid redundancy unless it improves usability.

## Version Control
- Commit after significant changes with clear messages.
- Keep commits focused and atomic.
- No auto-push to any branch.
- Don't auto-commit activity logs and docs.
- Access only these repositories: `<REPO_ALLOWLIST>`

## AI Restrictions
- No customer personal data — names, contacts, account numbers, transactions (unless approved exemption).
- No credentials — passwords, API keys, tokens, connection strings.
- Always check that npm/yarn install/download is safe; verify via `<PACKAGE_REGISTRY_HOST>`.

## OpenCode Agent Instructions

### Session & Mode Behavior
- Prefer **Plan** mode first for non-trivial work: design the approach, list files to touch, and surface risks before editing.
- Switch to **Build** mode only after the plan is confirmed (or the task is clearly small).
- Use subagents (`explore`, `general`, or custom ones) for research, broad codebase exploration, or parallel work instead of bloating the main session.
- Keep sessions focused. Start a new session for unrelated work.

### Tool & Permission Discipline
- Ask before any destructive or irreversible action (force push, deleting files outside the change set, dropping data, etc.).
- Prefer read / grep / glob / LSP diagnostics over guessing file locations or APIs.
- Do not read or edit files outside the workspace unless the user explicitly asks and permission is granted.
- Treat environment files, secrets, and credential stores as restricted — always ask.
- When running shell commands, prefer the project's existing scripts (`package.json`, Makefile, etc.) over inventing new ones.

### Workflow
1. Understand the request and the relevant existing code.
2. If the change is non-trivial, produce a short plan (files, steps, risks).
3. Implement the smallest correct change.
4. Verify with the project's tests / typecheck / lint when available.
5. Summarize what changed and any follow-ups.
6. Update `docs/activity-log.md` when the work is significant or non-obvious.

### Project Conventions (fill in / keep current)
- Primary language / runtime: 
- Package manager & install command: 
- Test command: 
- Lint / typecheck / format commands: 
- Preferred branch naming: 
- Commit message style: 

### Custom Agents & Skills
- Project agents live in `.opencode/agents/`.
- Skills live in `.opencode/skills/<name>/SKILL.md`.
- Prefer loading a relevant skill with the `skill` tool when one matches the task instead of re-deriving the workflow.
- Do not invent new top-level agents or skills unless the user asks.

### Safety Defaults
- Never commit secrets, `.env` files, or private keys.
- Never push to main/master unless the user explicitly requests it.
- Never run `rm -rf`, database drops, or mass deletions without explicit confirmation.
- Prefer atomic, reversible edits.