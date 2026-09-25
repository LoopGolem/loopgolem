# Mission protocol

A mission is a durable user goal. A mission owns an ordered list of persisted tasks whose readiness is determined by explicit dependencies.

Mission states: Created, Planning, Running, WaitingForQuota, WaitingForApproval, Paused, NeedsHumanAttention, Failed, Completed.

Task states: Planned, Ready, Running, Verifying, Retrying, RecoveryPending, Escalated, Blocked, Failed, Completed.

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
13. Mutable tasks persist their execution context before external work begins. An interrupted Luna Low task resumes as `Retrying` against its original Git workspace baseline.
14. Deterministic `write_file` and `create_directory` operations may be replayed. `rename_path` uses persisted pre-execution state to recognize an already-applied rename.
15. An interrupted arbitrary `run_command` is not replayed automatically because LoopGolem cannot prove whether side effects already occurred; the mission becomes `NeedsHumanAttention`.
16. A deterministic host process that finishes with a non-zero exit code or a controlled timeout produces a persisted failed task attempt with its full process evidence and may enter `RecoveryPending`. Infrastructure/configuration failures are not treated as code-repair candidates.
17. Recovery is scoped to the original failed deterministic check. The persistent GPT-6 Luna High Supervisor diagnoses the failure and returns a bounded batch of Luna Low repair tasks. Repair tasks cannot replace, weaken, or redefine the failed check.
18. After every repair task in the cycle completes, LoopGolem reruns the original deterministic task definition. For .NET verification, the original build target is persisted before the first attempt so recovery cannot silently switch to a different solution/project.
19. Recovery cycle state is persisted as `Pending -> Planning -> Repairing -> Retrying -> Succeeded`. A failed recheck starts another cycle until `MissionPolicy.MaxRecoveryCycles` is reached; exhaustion escalates the original task and moves the mission to `NeedsHumanAttention`.
20. Task attempts are persisted independently from task state. On restart, a known persisted deterministic success/failure is reconciled before any replay; attempt numbers are allocated from persisted history so restarts and migrated databases cannot reuse a logical attempt number.
21. An interrupted arbitrary `run_command` still is not replayed automatically when no completed process outcome was persisted. Recovery never converts an unknown side effect into a blind retry.
22. Quota exhaustion is a wait state rather than a mission failure; automatic paid API fallback is forbidden.

## Self-hosting

The currently running Worker cannot hot-reload changes to its own Core/Orchestrator/Worker assemblies. A self-hosted mission may build or launch a fresh child process to test new code, but subsequent orchestrator tasks must not assume that newly implemented runtime behavior has replaced the Worker process already executing the mission.
