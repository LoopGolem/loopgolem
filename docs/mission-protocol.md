# Mission protocol

A mission is a durable user goal. A mission owns an ordered list of persisted tasks whose readiness is determined by explicit dependencies.

Mission states: Created, Planning, Running, WaitingForQuota, WaitingForApproval, Paused, NeedsHumanAttention, Failed, Completed.

Task states: Planned, Ready, Running, Verifying, Retrying, Escalated, Blocked, Failed, Completed.

## Current execution rules

1. New missions persist their initial deterministic inspection tasks before execution.
2. GPT-6 Luna High planning returns a validated dependency graph of deterministic and Luna Low microtasks.
3. A Planned task becomes Ready only when all of its declared dependencies are Completed.
4. The current executor runs tasks sequentially even when the dependency graph could permit parallel work; parallel scheduling is future work.
5. Luna Low tasks are bounded by explicit read/write files and LoopGolem verifies their actual changed-file set.
6. After each implementation batch, deterministic Git inspection and available local .NET build verification run before final validation.
7. LoopGolem creates an unreachable Git snapshot commit without moving the user's branch or real index.
8. GPT-6 Luna High validates the original goal against the immutable base-to-snapshot diff.
9. A `not_ok` validation may add a new correction batch. The correction batch is persisted before execution and then verified and validated again.
10. After three validation cycles without `ok`, the mission becomes `NeedsHumanAttention` rather than escalating above Luna High.
11. A mission reaches Completed only after all scheduled tasks, including the final validator, are Completed.
12. Recovery-relevant transitions and dynamically added tasks are persisted before the next external action.
13. An interrupted deterministic Running task may be safely re-run on Worker startup.
14. Quota exhaustion is a wait state rather than a mission failure; automatic paid API fallback is forbidden.

## Self-hosting

The currently running Worker cannot hot-reload changes to its own Core/Orchestrator/Worker assemblies. A self-hosted mission may build or launch a fresh child process to test new code, but subsequent orchestrator tasks must not assume that newly implemented runtime behavior has replaced the Worker process already executing the mission.
