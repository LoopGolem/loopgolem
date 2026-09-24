# Codex integration

LoopGolem uses the official Codex CLI for bounded agent tasks.

Agent work is allowed only when `codex login status` reports ChatGPT authentication. LoopGolem never asks for API keys, cookies or access tokens and does not silently fall back to paid API usage.

## Windows: Codex runs inside WSL2

Native Windows Codex sandboxing currently has edge cases around workspace-write. LoopGolem therefore runs Codex inside a user Linux distribution in WSL2 while keeping the Desktop, Worker, Git verification and .NET build on Windows.

The workspace remains on the Windows filesystem. For example:

```text
C:\Projects\repo
```

is translated through `wslpath` and passed to Codex as a WSL path such as:

```text
/mnt/c/Projects/repo
```

LoopGolem ignores infrastructure-only distributions such as `docker-desktop`. Set `LOOPGOLEM_WSL_DISTRO` to explicitly choose a user distribution when more than one is installed.

Windows onboarding requirements:

1. Install a WSL2 Linux distribution, for example Ubuntu.
2. Install the official Codex CLI inside that distribution. The official Linux installer currently places the binary in `~/.local/bin/codex` by default.
3. Run `codex` inside the distribution and choose **Sign in with ChatGPT**.
4. Verify inside WSL with `codex login status`.

The Windows Codex CLI installation is not used for autonomous LoopGolem agent execution.

LoopGolem invokes Codex through `bash -lc` inside the selected WSL distribution so the same login-shell PATH used by an interactive Ubuntu terminal is available. Arguments are not concatenated into the shell command: LoopGolem passes the executable as `$0` and all remaining values as positional parameters, then executes `exec "$0" "$@"`. This preserves argument boundaries while still loading the user's login environment.

## Linux

On Linux, LoopGolem invokes the native Codex CLI directly.

## Model policy

The initial autonomous worker policy is pinned explicitly to:

```text
model: gpt-6-luna
reasoning effort: low
```

LoopGolem does not inherit the user's current Codex default model. This prevents an account-side default change from silently routing routine autonomous work to a more expensive model such as Astra.

Future model routing will promote tasks to stronger reasoning/model tiers only through orchestrator policy.

## Permission model

Codex runs with `workspace-write` sandboxing, network disabled, and approval policy `never` for unattended work. Apps, plugins and multi-agent mode are disabled for this bounded worker execution.

Codex is instructed not to commit, push, create branches or rewrite Git history.

## Worktree safety

Codex missions require a clean Git working tree before agent execution. This avoids mixing autonomous edits with unrelated local work and gives LoopGolem a deterministic postcondition.

Codex returns a structured outcome:

- `changed`: the goal was implemented and Git must show working-tree changes;
- `already_satisfied`: no edit was needed and Git must remain clean;
- `blocked`: the task could not be completed and the mission fails.

LoopGolem rejects contradictory outcomes. A zero exit code by itself is not considered proof of success.

After agent work, LoopGolem runs deterministic Git inspection and the available local build verification.

## Future direction

The longer-term integration target can move to Codex app-server or the official SDK for richer streaming, lifecycle control, quota telemetry, permission handling and resumable sessions without changing mission semantics.


## Integrated smoke test

The Desktop exposes a **Test Codex** action even when the automatic status probe is not ready. The Worker reproduces the WSL login-shell probe, then creates a temporary Windows workspace and asks Codex (Luna, low reasoning, workspace-write, network disabled) to create one exact marker file. LoopGolem verifies the file from Windows and deletes the temporary workspace afterwards.

This test intentionally consumes a small Codex execution. Its probe and execution stdout/stderr are shown in the Desktop result area so WSL/runtime failures can be diagnosed without external shell experiments.
