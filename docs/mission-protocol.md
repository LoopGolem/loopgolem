# Mission protocol

A mission is a durable user goal. A mission owns an ordered list of persisted tasks whose readiness is determined by explicit dependencies.

Mission states: Created, Planning, Running, WaitingForQuota, WaitingForApproval, Paused, NeedsHumanAttention, Failed, Completed.

Task states: Planned, Ready, Running, Verifying, Retrying, Escalated, Blocked, Failed, Completed.

## Current execution rules

1. New missions persist their initial deterministic inspection tasks before execution.
2. Codex missions persist a host/agent capability snapshot before planning. Tool availability in the Codex/WSL environment is independent from tool availability in the host deterministic executor.
3. GPT-6 Luna High planning receives that snapshot and returns a validated dependency graph of deterministic and Luna Low microtasks. Luna Low prompts receive the same snapshot and must not attempt tools known to be unavailable in the agent environment.
4. A Planned task becomes Ready only when all of its declared dependencies are Completed.
5. The current executor runs tasks sequentially even when the dependency graph could permit parallel work; parallel scheduling is future work.
6. Luna Low tasks are bounded by explicit read/write files and LoopGolem verifies their actual changed-file set.
7. After each implementation batch, deterministic Git inspection and available local .NET build verification run before final validation.
8. LoopGolem creates an unreachable Git snapshot commit without moving the user's branch or real index.
9. GPT-6 Luna High validates the original goal against the immutable base-to-snapshot diff.
10. A `not_ok` validation may add a new correction batch. The correction batch is persisted before execution and then verified and validated again.
11. After three validation cycles without `ok`, the mission becomes `NeedsHumanAttention` rather than escalating above Luna High.
12. A mission reaches Completed only after all scheduled tasks, including the final validator, are Completed.
13. Recovery-relevant transitions and dynamically added tasks are persisted before the next external action.
14. Mutable tasks persist their execution context before external work begins. An interrupted Luna Low task resumes as `Retrying` against its original Git workspace baseline.
15. Deterministic `write_file` and `create_directory` operations may be replayed. `rename_path` uses persisted pre-execution state to recognize an already-applied rename.
16. An interrupted arbitrary `run_command` is not replayed automatically because LoopGolem cannot prove whether side effects already occurred; the mission becomes `NeedsHumanAttention`.
17. Quota exhaustion is a wait state rather than a mission failure; automatic paid API fallback is forbidden.

## Self-hosting

The currently running Worker cannot hot-reload changes to its own Core/Orchestrator/Worker assemblies. A self-hosted mission may build or launch a fresh child process to test new code, but subsequent orchestrator tasks must not assume that newly implemented runtime behavior has replaced the Worker process already executing the mission.
