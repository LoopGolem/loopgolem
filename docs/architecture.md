# Architecture

LoopGolem is a persistent orchestrator rather than a long-lived chat process.

## Components

- **LoopGolem.Core**: provider-independent mission, task, quota, protocol and policy concepts.
- **LoopGolem.Orchestrator**: state transitions, routing, retry/escalation, verification and checkpoint rules.
- **LoopGolem.Worker**: background mission execution, persistence and local IPC; restartable independently of the UI.
- **LoopGolem.Desktop**: Avalonia Windows/Linux client; product logic does not belong here.
- **LoopGolem.Cli**: automation and power-user surface over the same worker contracts.

## Current vertical slice

The first executable slice deliberately uses no AI provider. A mission contains one deterministic workspace-inspection task.

1. A mission is created with a goal and workspace.
2. The worker persists mission/task state in SQLite.
3. The orchestrator transitions the mission through Running.
4. A deterministic executor inspects the workspace.
5. Result and terminal state are persisted.
6. On worker startup, Created/Planning/Running missions are recovered and safely re-run.

This is intentionally the same lifecycle that later Codex-backed tasks will use.

## Persistence

The worker owns the SQLite database.

- Windows: `%LOCALAPPDATA%\LoopGolem\loopgolem.db`
- Linux: `$XDG_STATE_HOME/loopgolem/loopgolem.db` or `~/.local/state/loopgolem/loopgolem.db`

SQLite uses WAL mode. UI clients do not open the database directly.

## IPC

Desktop and CLI clients communicate with the worker through the local named pipe `loopgolem-worker-v1`. The current protocol is newline-delimited JSON with request/response framing. Named pipes keep the first slice dependency-light and cross-platform.

## Planned infrastructure

Provider adapters beginning with official Codex integration, richer deterministic verifiers, quota scheduling, multi-task planning, and GitHub Releases update discovery.

Provider credentials must not be injected into prompts. Planning/execution/escalation are separate concerns, and orchestrator policy controls expensive-model escalation.
