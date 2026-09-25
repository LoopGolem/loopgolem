# Codex integration

LoopGolem uses the official Codex CLI for bounded planning, implementation and validation tasks.

Agent work is allowed only when `codex login status` reports ChatGPT authentication. LoopGolem never asks for API keys, cookies or access tokens and does not silently fall back to paid API usage.

## Windows: Codex runs inside WSL2

LoopGolem runs Codex inside a user Linux distribution in WSL2 while keeping the Desktop, Worker, Git verification and .NET build on Windows.

The workspace remains on the Windows filesystem. A path such as:

```text
C:\Projects\repo
```

is exposed to Codex as:

```text
/mnt/c/Projects/repo
```

LoopGolem ignores infrastructure-only distributions such as `docker-desktop`. Set `LOOPGOLEM_WSL_DISTRO` to explicitly choose a user distribution when more than one is installed.

Windows onboarding requirements:

1. Install a WSL2 Linux distribution, for example Ubuntu.
2. Install the official Codex CLI inside that distribution.
3. Run `codex` inside the distribution and choose **Sign in with ChatGPT**.
4. Verify inside WSL with `codex login status`.

The Windows Codex CLI installation is not used for autonomous LoopGolem Codex execution.

LoopGolem invokes Codex through `bash -lc` inside the selected distribution so the login-shell PATH is available while arguments remain safely quoted.

## Linux

On Linux, LoopGolem invokes the native Codex CLI directly.

## Model policy

LoopGolem does not inherit the user's Codex default model.

The current autonomous policy is:

```text
planner:   gpt-6-luna / high
worker:    gpt-6-luna / low
validator: gpt-6-luna / high
```

LoopGolem never automatically escalates above GPT-6 Luna High. Repeated validation failure becomes `NeedsHumanAttention`.

## Planner

The planner runs read-only with repository-wide visibility. Its hidden contract requires a structured plan of small dependency-aware tasks containing precise read files, write files and acceptance checks.

The planner chooses between:

- deterministic operations for exact mechanical work;
- GPT-6 Luna Low for small tasks requiring implementation judgment.

A self-hosting rule warns the planner that the currently running LoopGolem Worker does not hot-reload changes made to its own runtime projects.

## Luna Low workers

Each Luna Low call is a fresh bounded task. It receives the microtask goal, read files, write allowlist and acceptance checks rather than the entire original mission context.

Codex is instructed not to commit, push, create branches or rewrite Git history. LoopGolem verifies the actual changed files after every worker call and rejects edits outside the task's write allowlist.

## Final validation

After deterministic Git/build verification, LoopGolem creates an unreachable snapshot commit using a temporary Git index. The user's branch and real index are unchanged.

GPT-6 Luna High receives:

- the original user goal;
- the original planner output;
- the original base commit;
- the immutable snapshot commit.

The validator inspects `git diff baseCommit..snapshotCommit` in read-only mode. It returns either:

- `ok`: the mission satisfies the original goal;
- `not_ok`: a bounded correction task batch.

Correction batches use the same deterministic/Luna Low task format. LoopGolem runs at most three validation cycles before stopping for human attention.

## Permission model

Luna Low runs with `workspace-write` sandboxing, network disabled and approval policy `never`. Planning and validation run read-only. Apps, plugins and multi-agent mode are disabled.

## Worktree safety

Codex missions require a clean Git working tree before planning begins. This avoids mixing autonomous edits with unrelated local work.

A zero Codex exit code is never accepted as proof by itself. LoopGolem additionally checks structured responses, changed-file allowlists, Git diff validity, local builds when applicable, and final Luna High validation.

Before each Luna Low call, LoopGolem persists a Git workspace baseline in mission state. If the Worker is interrupted, the next attempt reuses that same baseline rather than recapturing the partially modified workspace. This keeps write-allowlist verification meaningful across process restarts.

## Token usage telemetry

LoopGolem invokes autonomous `codex exec` calls with JSON event output enabled and reads token usage from the completed-turn event. It persists input, cached-input, output, optional reasoning-output, and total-token counts per mission task. Cached input is a subset of input and is not added a second time when computing totals. If the CLI does not provide `total_tokens`, LoopGolem computes the task-call total as input plus output.

Token usage is accumulated across completed retries for the same task and summed across tasks for the mission total shown in the Desktop. If the Worker or Codex process is terminated before a usage event is returned, LoopGolem does not invent an estimate for that interrupted call.

## Future direction

The integration can later move to Codex app-server or the official SDK for richer streaming, lifecycle control, quota telemetry and resumable sessions without changing mission semantics.
