# Mission protocol

Mission states: Created, Planning, Running, WaitingForQuota, WaitingForApproval, Paused, Failed, Completed.

Task states: Planned, Ready, Running, Verifying, Retrying, Escalated, Blocked, Failed, Completed.

Rules:
1. Quota exhaustion never marks a mission failed.
2. Tasks advance only after their verification policy succeeds.
3. Retries remain attached to the same task identity.
4. Escalation is orchestrator policy, not an agent decision.
5. Recovery-relevant transitions are persisted before the next external action.
6. Restart reconciliation prefers safe re-execution over assuming a partially observed action succeeded.
