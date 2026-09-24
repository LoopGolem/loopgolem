# Manual test: first vertical slice

The first vertical slice deliberately uses no AI provider and consumes no Codex quota.

## Windows

1. Build the solution in Debug or Release.
2. Start `loopgolem-worker.exe` from the Worker output folder.
3. Start `loopgolem-desktop.exe` from the Desktop output folder, or set both projects to start in Visual Studio.
4. Confirm the Desktop shows the Worker as connected.
5. Select a workspace directory.
6. Enter any mission goal.
7. Start the mission.
8. The mission should transition to Running and then Completed.
9. The result should report the number of files/directories and approximate size of the selected workspace.

The goal text is persisted even though this first deterministic executor only inspects the workspace.

## Persistence check

The database is stored at:

- Windows: `%LOCALAPPDATA%\LoopGolem\loopgolem.db`
- Linux: `$XDG_STATE_HOME/loopgolem/loopgolem.db` or `~/.local/state/loopgolem/loopgolem.db`

To exercise recovery, terminate the Worker while a future long-running task is active and restart it. Recoverable missions are loaded from SQLite rather than process memory.

## Automated smoke test

```bash
dotnet run --project src/LoopGolem.Worker/LoopGolem.Worker.csproj -- --self-test
```

This creates a temporary SQLite database and workspace, runs a mission through the orchestrator and verifies persisted completion.
