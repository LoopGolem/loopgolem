# Codex integration

LoopGolem currently uses the official Codex CLI for bounded agent tasks.

Agent work is allowed only when `codex login status` reports ChatGPT authentication. LoopGolem never asks for API keys, cookies or access tokens and does not silently fall back to paid API usage.

## Permission model

LoopGolem selects Codex's built-in `:workspace` permission profile through `default_permissions`. This is the current permission-profile path and replaces the legacy `--sandbox workspace-write` invocation.

The built-in workspace profile permits writes inside the selected workspace while keeping network access restricted. LoopGolem also sets approval policy to `never` for unattended tasks and starts each task in an ephemeral Codex session.

Codex is instructed not to commit, push, create branches or rewrite Git history.

## Worktree safety

Codex missions currently require a clean Git working tree before agent execution. This avoids mixing autonomous edits with unrelated local work and gives LoopGolem a deterministic postcondition.

Codex returns a structured outcome:

- `changed`: the goal was implemented and Git must show working-tree changes;
- `already_satisfied`: no edit was needed and Git must remain clean;
- `blocked`: the task could not be completed and the mission fails.

LoopGolem rejects contradictory outcomes. A zero exit code by itself is not considered proof of success.

After agent work, LoopGolem runs deterministic Git inspection and the available local build verification.

## Future direction

The longer-term integration target can move to Codex app-server or the official SDK for richer streaming, lifecycle control, quota telemetry, permission handling and resumable sessions without changing mission semantics.
