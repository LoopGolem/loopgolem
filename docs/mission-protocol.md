# Mission protocol

A mission is a durable user goal. A mission owns an ordered list of persisted tasks.

Mission states: Created, Planning, Running, WaitingForQuota, WaitingForApproval, Paused, Failed, Completed.

Task states: Planned, Ready, Running, Verifying, Retrying, Escalated, Blocked, Failed, Completed.

## Current execution rules

1. New missions are persisted with all currently planned tasks before execution begins.
2. Exactly one task is Running within the current sequential executor.
3. Completing a task promotes the next Planned task to Ready.
4. A mission reaches Completed only after every task is Completed.
5. Quota exhaustion never marks a mission failed.
6. Retries remain attached to the same task identity.
7. Escalation is orchestrator policy, not an agent decision.
8. Recovery-relevant transitions are persisted before the next external action.
9. An interrupted deterministic Running task may be safely re-run on worker startup.

## Current deterministic plan

The pre-Codex vertical slice creates two tasks:

1. Inspect workspace.
2. Discover .NET solution/project files.

The concrete planner will replace this fixed plan later. The persistence and orchestration semantics are intentionally the same ones that AI-backed tasks will use.
