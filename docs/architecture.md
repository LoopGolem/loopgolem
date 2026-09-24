# Architecture

LoopGolem is a persistent orchestrator rather than a long-lived chat process.

## Components

- **LoopGolem.Core**: provider-independent mission, task, quota, protocol and policy concepts.
- **LoopGolem.Orchestrator**: state transitions, routing, retry/escalation, verification and checkpoint rules.
- **LoopGolem.Worker**: background mission execution, persistence and local IPC; restartable independently of the UI.
- **LoopGolem.Desktop**: Avalonia Windows/Linux client; product logic does not belong here.
- **LoopGolem.Cli**: automation and power-user surface over the same worker contracts.

## Planner-driven mission flow

The planner-driven pipeline is the current execution architecture. The worker registers deterministic workspace inspection and project discovery, Codex planning, deterministic operations, bounded Luna Low microtasks, and deterministic Git/build verification executors. The orchestrator owns task sequencing and state transitions; provider calls remain behind worker-side adapters.

1. A mission is created with a goal and workspace, and its initial deterministic tasks inspect the workspace and discover project structure.
2. The worker persists mission/task state, then the orchestrator advances the mission to Planning and invokes Codex Planning High in read-only mode.
3. The planner returns a structured multi-task plan. The worker validates it before the orchestrator adds its tasks to the mission.
4. Planned tasks run in dependency order. Exact mechanical work uses deterministic operations; tasks requiring implementation judgment use bounded Luna Low microtasks with explicit read/write paths and acceptance checks.
5. Deterministic Git change checks and build verification run as planned tasks. Their results and mission state are persisted.
6. On worker startup, pending Created/Planning/Running missions are recovered and resumed from persisted state.

## Persistence

The worker owns the SQLite database.

- Windows: `%LOCALAPPDATA%\LoopGolem\loopgolem.db`
- Linux: `$XDG_STATE_HOME/loopgolem/loopgolem.db` or `~/.local/state/loopgolem/loopgolem.db`

SQLite uses WAL mode. UI clients do not open the database directly.

## IPC

Desktop and CLI clients communicate with the worker through the local named pipe `loopgolem-worker-v1`. The current protocol is newline-delimited JSON with request/response framing. Named pipes keep the first slice dependency-light and cross-platform.

## Provider and policy boundaries

Codex planning and Luna Low execution are provider-backed worker services. Mission, task, quota, protocol, and policy concepts remain provider-independent in Core, and persisted mission semantics do not depend on a particular provider. Provider credentials must not be injected into prompts. Planning, execution, and escalation are separate concerns; orchestrator policy controls expensive-model escalation. Quota exhaustion is a wait state, and the system does not automatically purchase credits or silently fall back to paid API usage.
