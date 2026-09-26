#!/usr/bin/env python3
"""Compare old-style and pinned Codex thread/fork model identity without a model turn.

The probe reuses the persistent HIGH Supervisor produced by an existing
LoopGolem benchmark plan-only mission. That Supervisor already has a persisted
Codex rollout, which thread/fork requires.

The probe itself performs only app-server initialize + thread/fork requests.
It never calls thread/start or turn/start, so it does not intentionally request
new model inference.

Usage from WSL:

    python3 docs/benchmarks/probe-fork-model-no-turn.py \
        --artifacts-root /mnt/c/Projetos/Github/loopgolem-benchmark-v3-YYYYMMDD-HHMMSS

The benchmark artifact root must contain:
- benchmark-v3-summary.json
- state/loopgolem.db
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sqlite3
import subprocess
import sys
from pathlib import Path
from typing import Any

MODEL = "gpt-6-luna"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--artifacts-root",
        required=True,
        help="Benchmark v3 artifact root containing benchmark-v3-summary.json and state/loopgolem.db.",
    )
    return parser.parse_args()


def send(stream, payload: dict[str, Any]) -> None:
    stream.write(json.dumps(payload, separators=(",", ":")) + "\n")
    stream.flush()


def request(
    proc: subprocess.Popen[str],
    request_id: int,
    method: str,
    params: dict[str, Any],
) -> dict[str, Any]:
    assert proc.stdin is not None
    assert proc.stdout is not None

    send(
        proc.stdin,
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": params,
        },
    )

    while True:
        line = proc.stdout.readline()
        if line == "":
            stderr = ""
            if proc.stderr is not None:
                stderr = proc.stderr.read()
            raise RuntimeError(
                f"app-server closed before response to {method}. stderr={stderr!r}"
            )

        message = json.loads(line)

        # Notifications and responses for unrelated ids can be ignored. This
        # probe sends requests serially, so only the requested id is relevant.
        if message.get("id") != request_id:
            continue

        if "error" in message:
            raise RuntimeError(
                f"{method} failed: {json.dumps(message['error'], ensure_ascii=False)}"
            )

        result = message.get("result")
        if not isinstance(result, dict):
            raise RuntimeError(f"{method} returned a non-object result: {result!r}")

        return result


def notify(
    proc: subprocess.Popen[str],
    method: str,
    params: dict[str, Any] | None = None,
) -> None:
    assert proc.stdin is not None
    payload: dict[str, Any] = {
        "jsonrpc": "2.0",
        "method": method,
    }
    if params is not None:
        payload["params"] = params
    send(proc.stdin, payload)


def describe_fork(result: dict[str, Any]) -> dict[str, Any]:
    thread = result.get("thread")
    thread_id = thread.get("id") if isinstance(thread, dict) else None
    return {
        "threadId": thread_id,
        "model": result.get("model"),
        "modelProvider": result.get("modelProvider"),
        "reasoningEffort": result.get("reasoningEffort"),
        "cwd": result.get("cwd"),
    }


def to_wsl_path(path: str) -> str:
    # Mission.WorkspacePath is persisted by the Windows Worker, so benchmark
    # databases normally contain a Windows path. Convert it when this probe is
    # executed under WSL.
    if re.match(r"^[A-Za-z]:[\\/]", path):
        completed = subprocess.run(
            ["wslpath", "-a", "-u", path],
            check=True,
            capture_output=True,
            text=True,
        )
        return completed.stdout.strip()

    return os.path.abspath(path)


def load_parent(artifacts_root: Path) -> dict[str, str]:
    summary_path = artifacts_root / "benchmark-v3-summary.json"
    database_path = artifacts_root / "state" / "loopgolem.db"

    if not summary_path.is_file():
        raise RuntimeError(f"Missing benchmark summary: {summary_path}")
    if not database_path.is_file():
        raise RuntimeError(f"Missing benchmark database: {database_path}")

    summary = json.loads(summary_path.read_text(encoding="utf-8-sig"))
    mission_id = summary.get("sourcePlanOnlyMissionId")
    if not isinstance(mission_id, str) or not mission_id.strip():
        raise RuntimeError(
            "benchmark-v3-summary.json has no sourcePlanOnlyMissionId."
        )

    connection = sqlite3.connect(f"file:{database_path}?mode=ro", uri=True)
    try:
        row = connection.execute(
            """
            SELECT
                s.provider_thread_id,
                s.model,
                s.reasoning_effort,
                m.workspace_path
            FROM agent_sessions AS s
            INNER JOIN missions AS m
                ON m.id = s.mission_id
            WHERE
                s.mission_id = ?
                AND s.role = 'Supervisor'
                AND s.provider_thread_id IS NOT NULL
                AND length(trim(s.provider_thread_id)) > 0
            ORDER BY
                s.last_used_utc DESC,
                s.updated_utc DESC
            LIMIT 1
            """,
            (mission_id,),
        ).fetchone()
    finally:
        connection.close()

    if row is None:
        raise RuntimeError(
            f"No persisted Supervisor provider thread found for source mission {mission_id}."
        )

    provider_thread_id, model, reasoning_effort, workspace_path = row

    if model != MODEL:
        raise RuntimeError(
            f"Persisted parent model is {model!r}, expected {MODEL!r}; refusing the probe."
        )

    if str(reasoning_effort).lower() != "high":
        raise RuntimeError(
            f"Persisted parent effort is {reasoning_effort!r}, expected 'high'; refusing the probe."
        )

    return {
        "missionId": mission_id,
        "threadId": str(provider_thread_id),
        "model": str(model),
        "reasoningEffort": str(reasoning_effort),
        "workspace": str(workspace_path),
    }


def main() -> int:
    args = parse_args()
    artifacts_root = Path(args.artifacts_root).expanduser().resolve()
    parent = load_parent(artifacts_root)
    app_workspace = to_wsl_path(parent["workspace"])

    if not os.path.isdir(app_workspace):
        raise RuntimeError(
            f"Persisted benchmark workspace does not exist in WSL: {app_workspace}"
        )

    command = [
        "codex",
        "--enable",
        "reasoning_effort_override",
        "--disable",
        "apps",
        "--disable",
        "plugins",
        "--disable",
        "multi_agent",
        "--disable",
        "memories",
        "app-server",
        "--listen",
        "stdio://",
    ]

    proc = subprocess.Popen(
        command,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,
    )

    try:
        request(
            proc,
            1,
            "initialize",
            {
                "clientInfo": {
                    "name": "loopgolem-fork-model-probe",
                    "title": "LoopGolem fork model probe",
                    "version": "0.1",
                },
                "capabilities": {
                    "experimentalApi": False,
                    "requestAttestation": False,
                },
            },
        )
        notify(proc, "initialized")

        common_fork = {
            "threadId": parent["threadId"],
            "cwd": app_workspace,
            "approvalPolicy": "never",
            "sandbox": "workspace-write",
            "ephemeral": True,
            "excludeTurns": True,
        }

        # Reproduce the old LoopGolem shape: no model override on thread/fork.
        old_style = request(
            proc,
            2,
            "thread/fork",
            dict(common_fork),
        )

        # Change one field only: explicitly pin the child model to Luna.
        pinned = request(
            proc,
            3,
            "thread/fork",
            {
                **common_fork,
                "model": MODEL,
            },
        )

        old_description = describe_fork(old_style)
        pinned_description = describe_fork(pinned)

        summary = {
            "modelTurnStarted": False,
            "parentSource": "persisted benchmark Supervisor rollout",
            "parent": {
                "missionId": parent["missionId"],
                "threadId": parent["threadId"],
                "model": parent["model"],
                "reasoningEffort": parent["reasoningEffort"],
                "cwd": app_workspace,
            },
            "oldStyleForkWithoutModel": old_description,
            "pinnedForkWithModel": pinned_description,
            "oldStyleChangedModel": old_description["model"] != parent["model"],
            "pinnedMatchesRequestedModel": pinned_description["model"] == MODEL,
        }

        print(json.dumps(summary, indent=2, ensure_ascii=False))

        if pinned_description["model"] != MODEL:
            print(
                f"ERROR: pinned fork is {pinned_description['model']!r}, expected {MODEL!r}.",
                file=sys.stderr,
            )
            return 3

        return 0
    finally:
        if proc.stdin is not None:
            try:
                proc.stdin.close()
            except Exception:
                pass

        try:
            proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            proc.terminate()
            try:
                proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()


if __name__ == "__main__":
    raise SystemExit(main())
