# Development diagnostics

LoopGolem intentionally does not keep verbose AI transcripts as a permanent product log.

Mission state is persisted in SQLite because the orchestrator needs it for recovery and verification. In particular:

- the normalized Planner JSON is stored in the `result_details` column of the `PlanMission` task;
- each expanded microtask definition is stored in `mission_tasks.definition_json`;
- normal task summaries/results remain in the mission database.

The exact raw structured response from Codex is temporary during normal execution and is deleted with the runtime scratch directory.

During development, Debug builds print the exact structured Planner and Luna Low final messages to the Worker console between `[LoopGolem diagnostics]` markers. Release builds keep this disabled unless `LOOPGOLEM_DEV_DIAGNOSTICS=1` is explicitly set.

This gives developers visibility into prompts/results without creating a permanent transcript-retention feature by default.
