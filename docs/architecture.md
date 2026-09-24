# Architecture

LoopGolem is a persistent orchestrator rather than a long-lived chat process.

## Components

- **LoopGolem.Core**: provider-independent mission, task, quota and policy concepts.
- **LoopGolem.Orchestrator**: state transitions, routing, retry/escalation, verification and checkpoint rules.
- **LoopGolem.Worker**: background mission execution, restartable independently of the UI.
- **LoopGolem.Desktop**: Avalonia Windows/Linux client; product logic does not belong here.
- **LoopGolem.Cli**: automation and power-user surface over the same orchestration contracts.

## Planned infrastructure

SQLite durable state, local IPC, provider adapters beginning with official Codex integration, deterministic verifiers, and GitHub Releases update discovery.

Provider credentials must not be injected into prompts. Planning/execution/escalation are separate concerns, and orchestrator policy controls expensive-model escalation.
