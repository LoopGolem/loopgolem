# Codex integration

LoopGolem currently uses the official Codex CLI for bounded agent tasks.

Agent work is allowed only when codex login status reports ChatGPT authentication. LoopGolem never asks for API keys, cookies or access tokens and does not silently fall back to paid API usage.

Current Codex execution uses a fresh ephemeral session, workspace-write sandbox, no approvals, network disabled, and no explicit model override. Codex is instructed not to commit or push. After agent work, LoopGolem performs deterministic Git inspection and the available local build verification.

The longer-term integration target can move to Codex app-server or the official SDK for richer streaming, lifecycle control, quota telemetry and resumable sessions without changing mission semantics.
