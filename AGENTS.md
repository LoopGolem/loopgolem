# LoopGolem agent instructions

This file is authoritative for automated coding agents working in this repository.

## Language
- Code, identifiers, comments, commit messages, technical documentation and agent-facing material are English.
- User-facing strings belong in localization resources.
- English is the canonical user-facing source language.

## Product invariants
- Missions are persistent and resumable.
- Quota exhaustion is a wait state, not mission failure.
- Never purchase credits automatically.
- Never silently fall back to paid API usage.
- Prefer deterministic local verification before spending model quota.
- Never place provider credentials, cookies or raw auth tokens in prompts or logs.
- Prefer official provider authentication flows.
- Windows and Linux are first-class platforms.
- Arabic, Hebrew and Persian require RTL support.

## Architecture
- Domain logic belongs in LoopGolem.Core.
- Orchestration and state transitions stay out of UI projects.
- The worker must be restartable independently from the desktop UI.
- Provider integrations sit behind adapters.
- Mission persistence must not depend on one AI provider.

## Cost discipline
- Use the least expensive capable model for well-scoped work.
- Escalation is controlled by orchestrator policy, never by an agent promoting itself.
- Keep repeated context compact and stable.

## Changes
- Keep commits focused.
- Update deterministic tests when behavior changes.
- Update architecture docs when changing persistence, security or state semantics.
- Never commit secrets, auth material, local databases or build output.
